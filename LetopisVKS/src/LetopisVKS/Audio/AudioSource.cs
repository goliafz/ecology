using System;
using System.Runtime.InteropServices;
using System.Threading;
using LetopisVks.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LetopisVks.Audio
{
    internal enum SourceState
    {
        NotSelected,
        Connecting,
        Active,
        Lost,
        Error
    }

    /// <summary>Захват звука компьютера (то, что играет в динамиках/на телевизоре) с коротким буфером.</summary>
    internal sealed class LoopbackCapture : WasapiCapture
    {
        public LoopbackCapture(MMDevice device, int bufferMs)
            : base(device, false, bufferMs)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        {
            return AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
        }
    }

    /// <summary>
    /// Один источник звука (микрофон или звук компьютера).
    /// Сам переподключается, если устройство выдернули и вставили обратно,
    /// а пока устройства нет — отдаёт тишину, и запись продолжается.
    /// </summary>
    internal sealed class AudioSource : IDisposable
    {
        private const int CaptureBufferMs = 40;
        private const int TargetFrames = CaptureConverter.TargetRate * 60 / 1000;   // 60 мс запас от рывков
        private const int ToleranceFrames = CaptureConverter.TargetRate * 8 / 1000;
        private const int HardMaxFrames = CaptureConverter.TargetRate * 400 / 1000;

        private readonly object _lock = new object();
        private readonly FloatRing _ring;

        // Настройка (меняется из интерфейса)
        private string _deviceId;
        private string _deviceName;
        private bool _followDefault;
        private volatile int _configVersion;

        // Работа с устройством (только поток-надзиратель)
        private MMDeviceEnumerator _enumerator;
        private WasapiCapture _capture;
        private CaptureConverter _converter;
        private int _openedVersion = -1;
        private string _openedDefaultId;
        private long _nextRetry;
        private long _lastDefaultCheck;
        private bool _everActive;
        private string _lastErrorLogged;
        private volatile bool _captureStopped;
        private Exception _stopError;
        private long _lastDataTicks;
        private float[] _convertOut = new float[4096];

        // Сведение
        private bool _buffering = true;
        private double _avgFill;

        // Статистика и индикатор
        private long _underruns;
        private long _realFrames;
        private long _zeroRunFrames;
        private float _meter;

        public readonly string Title;
        public readonly bool IsLoopback;
        public readonly int Channels;

        private volatile SourceState _state = SourceState.NotSelected;
        private volatile string _stateText = "Не выбран";
        private volatile string _activeDeviceName;

        public AudioSource(string title, bool isLoopback)
        {
            Title = title;
            IsLoopback = isLoopback;
            Channels = isLoopback ? 2 : 1;
            _ring = new FloatRing(CaptureConverter.TargetRate * 2, Channels);
        }

        public SourceState State
        {
            get { return _state; }
        }

        public string StateText
        {
            get { return _stateText; }
        }

        public string ActiveDeviceName
        {
            get { return _activeDeviceName; }
        }

        public bool IsConfigured
        {
            get { return _followDefault || !string.IsNullOrEmpty(_deviceId); }
        }

        /// <summary>Число разрывов звука (устройство не успело прислать данные) с момента запуска.</summary>
        public long Underruns
        {
            get { return Interlocked.Read(ref _underruns); }
        }

        public long RealFrames
        {
            get { return Interlocked.Read(ref _realFrames); }
        }

        /// <summary>Сколько подряд кадров устройство присылает абсолютную цифровую тишину.</summary>
        public long ZeroRunFrames
        {
            get { return Interlocked.Read(ref _zeroRunFrames); }
        }

        /// <summary>Пиковый уровень 0..1 с плавным спадом — для индикатора.</summary>
        public float Meter
        {
            get { return Volatile.Read(ref _meter); }
        }

        public void Configure(string deviceId, string deviceName, bool followDefault)
        {
            lock (_lock)
            {
                _deviceId = deviceId;
                _deviceName = deviceName;
                _followDefault = followDefault;
                _configVersion++;
            }
        }

        // ===================== Поток-надзиратель =====================

        public void Maintain()
        {
            long now = Clock.Now;
            int version = _configVersion;
            string id, name;
            bool followDefault;
            lock (_lock)
            {
                id = _deviceId;
                name = _deviceName;
                followDefault = _followDefault;
            }

            if (_capture != null && version != _openedVersion)
            {
                CloseCapture();
                _nextRetry = now;
            }

            if (!followDefault && string.IsNullOrEmpty(id))
            {
                SetState(SourceState.NotSelected, "Не выбран");
                _activeDeviceName = null;
                _everActive = false;
                return;
            }

            if (_capture != null)
            {
                if (_captureStopped)
                {
                    string reason = _stopError != null ? Humanize(_stopError) : "устройство остановилось";
                    Log.Warn(Title + ": захват остановился, причина: " + reason, _stopError);
                    CloseCapture();
                    SetState(SourceState.Lost, "Связь с устройством потеряна (" + reason + "). Переподключаюсь…");
                    _nextRetry = now + Clock.Frequency;
                }
                else if (!IsLoopback && now - Interlocked.Read(ref _lastDataTicks) > Clock.Frequency * 3)
                {
                    Log.Warn(Title + ": звук перестал поступать 3 секунды, переподключаю устройство");
                    CloseCapture();
                    SetState(SourceState.Lost, "Звук перестал поступать. Переподключаюсь…");
                    _nextRetry = now;
                }
                else if (IsLoopback && followDefault && now - _lastDefaultCheck > Clock.Frequency * 2)
                {
                    _lastDefaultCheck = now;
                    string currentDefault = TryGetDefaultRenderId();
                    if (currentDefault != null && currentDefault != _openedDefaultId)
                    {
                        Log.Info(Title + ": Windows сменила устройство вывода по умолчанию, переключаюсь");
                        CloseCapture();
                        _nextRetry = now;
                    }
                }
            }

            if (_capture == null && now >= _nextRetry)
                TryOpen(version, id, name, followDefault);
        }

        private void TryOpen(int version, string id, string name, bool followDefault)
        {
            WasapiCapture capture = null;
            try
            {
                if (_state != SourceState.Lost) SetState(SourceState.Connecting, "Подключаюсь…");
                MMDevice device = Resolve(id, name, followDefault);
                if (device == null)
                {
                    string text = _everActive
                        ? "Устройство отключено. Как только оно появится, программа подхватит его сама."
                        : "Устройство не найдено. Подключите его, и программа подхватит его сама.";
                    SetState(_everActive ? SourceState.Lost : SourceState.Error, text);
                    _nextRetry = Clock.Now + Clock.Frequency * 2;
                    return;
                }

                capture = IsLoopback ? new LoopbackCapture(device, CaptureBufferMs) : new WasapiCapture(device, false, CaptureBufferMs);
                var converter = new CaptureConverter(capture.WaveFormat, Channels);
                _captureStopped = false;
                _stopError = null;
                Interlocked.Exchange(ref _lastDataTicks, Clock.Now);
                lock (_lock)
                {
                    _ring.Clear();
                    _buffering = true;
                }
                _converter = converter;
                _capture = capture;
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();

                _openedVersion = version;
                _everActive = true;
                _activeDeviceName = device.FriendlyName;
                _lastErrorLogged = null;
                SetState(SourceState.Active, "Работает");
                Log.Info(Title + ": подключено «" + device.FriendlyName + "» (" + converter.Description + ")");
            }
            catch (Exception ex)
            {
                if (capture != null)
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.RecordingStopped -= OnRecordingStopped;
                    DisposeWithTimeout(capture);
                }
                _capture = null;
                _converter = null;
                string text = Humanize(ex);
                SetState(_everActive ? SourceState.Lost : SourceState.Error, "Не удалось подключиться: " + text);
                if (_lastErrorLogged != text)
                {
                    _lastErrorLogged = text;
                    Log.Warn(Title + ": ошибка подключения: " + text, ex);
                }
                _nextRetry = Clock.Now + Clock.Frequency * 3;
                ResetEnumerator();
            }
        }

        private MMDevice Resolve(string id, string name, bool followDefault)
        {
            MMDeviceEnumerator enumerator = GetEnumerator();
            if (IsLoopback && followDefault)
            {
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
                MMDevice def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _openedDefaultId = def.ID;
                _lastDefaultCheck = Clock.Now;
                return def;
            }

            DataFlow flow = IsLoopback ? DataFlow.Render : DataFlow.Capture;
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    MMDevice byId = enumerator.GetDevice(id);
                    if (byId != null && byId.State == DeviceState.Active) return byId;
                }
                catch (Exception)
                {
                    // устройство с таким ID сейчас недоступно
                }
            }

            // При переподключении в другой USB-порт у устройства может смениться ID — ищем по имени.
            if (!string.IsNullOrEmpty(name))
            {
                foreach (MMDevice d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    if (string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase)) return d;
                }
            }
            return null;
        }

        private string TryGetDefaultRenderId()
        {
            try
            {
                MMDeviceEnumerator enumerator = GetEnumerator();
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch (Exception)
            {
                ResetEnumerator();
                return null;
            }
        }

        private MMDeviceEnumerator GetEnumerator()
        {
            if (_enumerator == null) _enumerator = new MMDeviceEnumerator();
            return _enumerator;
        }

        private void ResetEnumerator()
        {
            try
            {
                if (_enumerator != null) _enumerator.Dispose();
            }
            catch (Exception)
            {
            }
            _enumerator = null;
        }

        private void CloseCapture()
        {
            WasapiCapture capture = _capture;
            _capture = null;
            _converter = null;
            if (capture == null) return;
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            DisposeWithTimeout(capture);
            lock (_lock)
            {
                _ring.Clear();
                _buffering = true;
            }
        }

        /// <summary>Драйвер может «зависнуть» при выдёргивании устройства — не ждём его дольше 2 секунд.</summary>
        private static void DisposeWithTimeout(WasapiCapture capture)
        {
            var t = new Thread(() =>
            {
                try
                {
                    capture.StopRecording();
                    capture.Dispose();
                }
                catch (Exception)
                {
                }
            }) { IsBackground = true, Name = "Освобождение звукового устройства" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            if (!t.Join(2000)) Log.Warn("Звуковое устройство не освободилось за 2 секунды, продолжаю без ожидания");
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            try
            {
                if (!ReferenceEquals(sender, _capture)) return;
                CaptureConverter converter = _converter;
                if (converter == null) return;
                Interlocked.Exchange(ref _lastDataTicks, Clock.Now);
                int frames = converter.Process(e.Buffer, e.BytesRecorded, ref _convertOut);
                if (frames <= 0) return;
                lock (_lock)
                {
                    _ring.Write(_convertOut, frames);
                }
            }
            catch (Exception ex)
            {
                // Исключение здесь остановило бы захват — глотаем и пишем в журнал.
                Log.Warn(Title + ": ошибка обработки звука", ex);
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (!ReferenceEquals(sender, _capture)) return;
            _stopError = e.Exception;
            _captureStopped = true;
        }

        private void SetState(SourceState state, string text)
        {
            _state = state;
            _stateText = text;
        }

        // ===================== Поток сведения =====================

        /// <summary>
        /// Отдаёт ровно <paramref name="frames"/> кадров. Если устройство не успело — дополняет тишиной.
        /// Расхождение часов устройства и компьютера плавно выравнивается по одному сэмплу.
        /// Возвращает число кадров настоящего звука.
        /// </summary>
        public int ReadBlock(float[] destination, int frames)
        {
            int real;
            lock (_lock)
            {
                if (_state != SourceState.Active)
                {
                    _ring.Clear();
                    _buffering = true;
                    Array.Clear(destination, 0, frames * Channels);
                    real = 0;
                }
                else
                {
                    real = ReadWithJitterControl(destination, frames);
                }
            }
            UpdateMeter(destination, frames, real);
            return real;
        }

        private int ReadWithJitterControl(float[] destination, int frames)
        {
            int available = _ring.FramesAvailable;
            if (_buffering)
            {
                if (available >= TargetFrames)
                {
                    _buffering = false;
                    _avgFill = available;
                }
                else
                {
                    Array.Clear(destination, 0, frames * Channels);
                    return 0;
                }
            }

            if (available > HardMaxFrames)
            {
                _ring.Skip(available - TargetFrames);
                available = TargetFrames;
                _avgFill = available;
            }

            _avgFill = _avgFill * 0.98 + available * 0.02;

            if (available < frames)
            {
                int got = _ring.Read(destination, 0, available);
                Array.Clear(destination, got * Channels, (frames - got) * Channels);
                _buffering = true;
                if (!IsLoopback) Interlocked.Increment(ref _underruns);
                return got;
            }

            if (_avgFill > TargetFrames + ToleranceFrames && available > frames + 1)
            {
                // Устройство чуть спешит — пропускаем один сэмпл (неслышно).
                int got = _ring.Read(destination, 0, frames);
                _ring.Skip(1);
                _avgFill -= 1;
                return got;
            }

            if (_avgFill < TargetFrames - ToleranceFrames && frames > 1)
            {
                // Устройство чуть отстаёт — повторяем один сэмпл (неслышно).
                int got = _ring.Read(destination, 0, frames - 1);
                Array.Copy(destination, (got - 1) * Channels, destination, got * Channels, Channels);
                _avgFill += 1;
                return got + 1;
            }

            return _ring.Read(destination, 0, frames);
        }

        private void UpdateMeter(float[] block, int frames, int real)
        {
            int samples = frames * Channels;
            float peak = 0;
            bool allZero = true;
            for (int i = 0; i < samples; i++)
            {
                float v = block[i];
                if (v != 0f) allZero = false;
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            float decayed = Volatile.Read(ref _meter) * 0.93f;
            Volatile.Write(ref _meter, Math.Max(peak, decayed));

            if (real > 0)
            {
                Interlocked.Add(ref _realFrames, real);
                if (allZero) Interlocked.Add(ref _zeroRunFrames, real);
                else Interlocked.Exchange(ref _zeroRunFrames, 0);
            }
        }

        // ===================== Для автотестов =====================

        /// <summary>Имитирует подключённое устройство (используется только автотестами).</summary>
        internal void TestSetActive()
        {
            SetState(SourceState.Active, "Работает");
        }

        /// <summary>Имитирует приход звука от устройства (используется только автотестами).</summary>
        internal void TestInject(float[] data, int frames)
        {
            lock (_lock)
            {
                _ring.Write(data, frames);
            }
        }

        internal int TestBufferedFrames
        {
            get { lock (_lock) return _ring.FramesAvailable; }
        }

        // ===================== Общее =====================

        public static string Humanize(Exception ex)
        {
            var com = ex as COMException;
            int code = com != null ? com.ErrorCode : (ex != null ? ex.HResult : 0);
            switch ((uint)code)
            {
                case 0x80070005:
                    return "Windows запрещает доступ к микрофону. Откройте «Параметры → Конфиденциальность → Микрофон» и разрешите доступ классическим приложениям. Также проверьте, не блокирует ли микрофон антивирус";
                case 0x8889000A:
                    return "устройство занято другой программой в монопольном режиме";
                case 0x88890004:
                    return "устройство отключено";
                case 0x88890008:
                    return "формат звука устройства не поддерживается";
                case 0x80070490:
                    return "устройство не найдено";
            }
            return ex == null ? "неизвестная ошибка" : ex.Message;
        }

        public void Dispose()
        {
            try
            {
                CloseCapture();
            }
            catch (Exception)
            {
            }
            ResetEnumerator();
        }
    }
}
