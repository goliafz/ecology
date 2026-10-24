using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MasterVks.Core;
using MasterVks.Recording;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MasterVks.Audio
{
    internal enum FeedState
    {
        /// <summary>Выключено в настройках.</summary>
        Off,
        /// <summary>Не установлен драйвер VB-Cable.</summary>
        NoDriver,
        Starting,
        Active,
        Error
    }

    /// <summary>Распознавание устройств виртуального кабеля VB-Audio по названию.</summary>
    internal static class VirtualCable
    {
        public const string DownloadPage = "https://vb-audio.com/Cable/";

        /// <summary>Любое устройство виртуального кабеля (в списки микрофонов и динамиков его не показываем).</summary>
        public static bool IsCable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   name.IndexOf("Cable", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>«CABLE Input (VB-Audio Virtual Cable)»: сюда программа подаёт звук.</summary>
        public static bool IsInput(string name)
        {
            return IsCable(name) && name.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>«CABLE Output (VB-Audio Virtual Cable)»: этот микрофон выбирают в Телемосте.</summary>
        public static bool IsOutput(string name)
        {
            return IsCable(name) && name.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// Буфер между звуковым ядром (кладёт по 10 мс) и виртуальным кабелем (забирает,
    /// когда просит Windows). Держит около 30 мс запаса; если часы ядра и кабеля
    /// немного расходятся, незаметно выбрасывает или повторяет по одному сэмплу.
    /// Отдельно от Windows, поэтому проверяется автотестами.
    /// </summary>
    internal sealed class FeedBuffer
    {
        public const int Rate = AudioEngine.Rate;
        public const int TargetFrames = Rate * 30 / 1000;
        private const int Tolerance = Rate * 10 / 1000;
        private const int MaxFrames = Rate * 150 / 1000;

        private readonly FloatRing _ring = new FloatRing(Rate, 1);
        private readonly object _lock = new object();
        private bool _primed;
        private float _last;
        /// <summary>Сглаженный запас после чтения: по нему решаем, подправлять ли ход.</summary>
        private double _average = TargetFrames;

        public long Underruns { get; private set; }
        public long Corrections { get; private set; }

        public int Fill
        {
            get
            {
                lock (_lock) return _ring.FramesAvailable;
            }
        }

        public void Write(float[] mono, int frames)
        {
            lock (_lock)
            {
                _ring.Write(mono, frames);
                // Кабель долго не забирал звук (например, его перезапускали): старое не нужно.
                if (_ring.FramesAvailable > MaxFrames) _ring.Skip(_ring.FramesAvailable - TargetFrames);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _ring.Skip(_ring.FramesAvailable);
                _primed = false;
            }
        }

        /// <summary>Отдаёт ровно <paramref name="frames"/> кадров; чего нет, заполняет тишиной.</summary>
        public void Read(float[] mono, int frames)
        {
            lock (_lock)
            {
                int have = _ring.FramesAvailable;
                if (!_primed)
                {
                    // Сначала набираем запас, иначе первые же доли секунды будут с провалами.
                    if (have < TargetFrames + frames)
                    {
                        Array.Clear(mono, 0, frames);
                        return;
                    }
                    _primed = true;
                    _average = have - frames;
                }

                _average = 0.95 * _average + 0.05 * (have - frames);
                int want = frames;
                if (_average > TargetFrames + Tolerance && frames > 1 && have > frames)
                {
                    // Запас растёт: часы кабеля медленнее. Пропускаем один сэмпл.
                    _ring.Skip(1);
                    Corrections++;
                }
                else if (_average < TargetFrames - Tolerance && have >= frames && frames > 1)
                {
                    // Запас тает: часы кабеля быстрее. Повторяем один сэмпл.
                    want = frames - 1;
                    Corrections++;
                }

                int got = _ring.Read(mono, 0, want);
                if (got > 0) _last = mono[got - 1];
                if (want < frames && got == want)
                {
                    mono[frames - 1] = mono[frames - 2];
                    got = frames;
                }
                if (got < frames)
                {
                    // Звук кончился: дальше тишина, затухающая от последнего сэмпла (без щелчка).
                    float v = _last;
                    for (int i = got; i < frames; i++)
                    {
                        v *= 0.95f;
                        mono[i] = v;
                    }
                    _last = 0;
                    Underruns++;
                    _primed = false;
                }
            }
        }
    }

    /// <summary>
    /// Передача микрофонов в Телемост. Телемост берёт только один микрофон, поэтому
    /// программа сводит все выбранные микрофоны (громче тот, рядом с которым говорят)
    /// и подаёт смесь в виртуальный кабель VB-Cable. В Телемосте выбирается микрофон
    /// «CABLE Output». Эхо не вычитается: это Телемост делает сам.
    /// Работает всё время, пока открыта программа, и сам переподключается.
    /// </summary>
    internal sealed class MicFeed : IDisposable
    {
        private const float ProbeAmplitude = 0.005f;

        private readonly AudioEngine _engine;
        private readonly AutoMixer _mixer = new AutoMixer();
        private readonly FeedBuffer _buffer = new FeedBuffer();
        private readonly ConcurrentQueue<Notice> _notices = new ConcurrentQueue<Notice>();
        private readonly Action<AudioBlock> _handler;
        private readonly object _probeLock = new object();
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _enabled;
        private volatile FeedState _state = FeedState.Off;
        private volatile string _error;
        private volatile string _deviceName;
        private volatile bool _outputFailed;
        private double _probeFrequency, _probePhase;
        private int _probeLeft;
        private bool _announced;
        private FeedState _lastReported = FeedState.Off;

        public MicFeed(AudioEngine engine)
        {
            _engine = engine;
            _handler = OnBlock;
        }

        public FeedState State
        {
            get { return _state; }
        }

        /// <summary>Почему не работает (для строки проверки), если State = Error.</summary>
        public string Error
        {
            get { return _error; }
        }

        /// <summary>Название устройства, в которое идёт звук («CABLE Input …»).</summary>
        public string DeviceName
        {
            get { return _deviceName; }
        }

        public bool Enabled
        {
            get { return _enabled; }
        }

        public long Underruns
        {
            get { return _buffer.Underruns; }
        }

        /// <summary>Какие микрофоны сейчас передаются (номера 1–3).</summary>
        public List<int> MicsInFeed()
        {
            var list = new List<int>();
            AudioSource[] mics = { _engine.Mic1, _engine.Mic2, _engine.Mic3 };
            for (int k = 0; k < 3; k++)
                if (Usable(mics[k])) list.Add(k + 1);
            return list;
        }

        public bool TryTakeNotice(out Notice notice)
        {
            return _notices.TryDequeue(out notice);
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) _buffer.Clear();
        }

        public void Start()
        {
            _engine.Subscribe(_handler);
            _thread = new Thread(Loop) { IsBackground = true, Name = "Микрофоны в Телемост", Priority = ThreadPriority.AboveNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        /// <summary>Подмешивает неслышный тон (для проверки, что звук доходит до «CABLE Output»).</summary>
        public void Probe(double frequency, int milliseconds)
        {
            lock (_probeLock)
            {
                _probeFrequency = frequency;
                _probePhase = 0;
                _probeLeft = AudioEngine.Rate * milliseconds / 1000;
            }
        }

        private static bool Usable(AudioSource source)
        {
            // Если в программе выбрали сам виртуальный кабель, звук пошёл бы по кругу.
            return source.IsConfigured && !VirtualCable.IsCable(source.ActiveDeviceName);
        }

        private void OnBlock(AudioBlock block)
        {
            if (!_enabled) return;
            int n = block.Frames;
            var mics = new[]
            {
                Usable(_engine.Mic1) ? block.Mic1 : null,
                Usable(_engine.Mic2) ? block.Mic2 : null,
                Usable(_engine.Mic3) ? block.Mic3 : null
            };
            var mono = new float[n];
            _mixer.Mix(mics, n, mono);
            lock (_probeLock)
            {
                for (int i = 0; i < n && _probeLeft > 0; i++, _probeLeft--)
                {
                    mono[i] += (float)Math.Sin(_probePhase) * ProbeAmplitude;
                    _probePhase += 2 * Math.PI * _probeFrequency / AudioEngine.Rate;
                }
            }
            for (int i = 0; i < n; i++) mono[i] = RoomMixer.SoftLimit(mono[i]);
            _buffer.Write(mono, n);
        }

        private void Loop()
        {
            WasapiOut output = null;
            var retry = Stopwatch.StartNew();
            long retryAfterMs = 0;
            while (!_stop)
            {
                try
                {
                    if (!_enabled)
                    {
                        Close(ref output);
                        SetState(FeedState.Off, null);
                    }
                    else if (output != null && (_outputFailed || output.PlaybackState != PlaybackState.Playing))
                    {
                        Log.Warn("Микрофоны в Телемост: вывод в «" + _deviceName + "» остановился" + (_error != null ? ": " + _error : ""));
                        Close(ref output);
                        SetState(FeedState.Error, _error ?? "виртуальный кабель перестал принимать звук");
                        retryAfterMs = retry.ElapsedMilliseconds + 2000;
                    }
                    else if (output == null && retry.ElapsedMilliseconds >= retryAfterMs)
                    {
                        output = Open();
                        if (output == null) retryAfterMs = retry.ElapsedMilliseconds + 3000;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Микрофоны в Телемост: сбой", ex);
                    Close(ref output);
                    SetState(FeedState.Error, AudioSource.Humanize(ex));
                    retryAfterMs = retry.ElapsedMilliseconds + 3000;
                }
                Thread.Sleep(250);
            }
            Close(ref output);
        }

        private WasapiOut Open()
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                MMDevice device = null;
                bool presentButOff = false;
                foreach (MMDevice d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
                {
                    string name;
                    try
                    {
                        name = d.FriendlyName;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (!VirtualCable.IsInput(name)) continue;
                    if (d.State == DeviceState.Active)
                    {
                        device = d;
                        break;
                    }
                    presentButOff = true;
                }
                if (device == null)
                {
                    if (presentButOff)
                        SetState(FeedState.Error, "устройство «CABLE Input» выключено в Windows: включите его в «Параметры → Звук → Все звуковые устройства»");
                    else
                        SetState(FeedState.NoDriver, null);
                    return null;
                }

                SetState(FeedState.Starting, null);
                _deviceName = device.FriendlyName;
                _outputFailed = false;
                _error = null;
                _buffer.Clear();
                var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 30);
                output.PlaybackStopped += (s, e) =>
                {
                    if (e.Exception != null) _error = AudioSource.Humanize(e.Exception);
                    _outputFailed = true;
                };
                output.Init(new BufferProvider(_buffer));
                output.Play();
                Log.Info("Микрофоны в Телемост: звук идёт в «" + _deviceName + "»");
                SetState(FeedState.Active, null);
                return output;
            }
        }

        private static void Close(ref WasapiOut output)
        {
            if (output == null) return;
            try
            {
                output.Stop();
                output.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Микрофоны в Телемост: ошибка при остановке вывода", ex);
            }
            output = null;
        }

        private void SetState(FeedState state, string error)
        {
            if (error != null) _error = error;
            _state = state;
            if (state == _lastReported || state == FeedState.Starting) return;

            // Сообщения в ленту: о включении один раз, о сбоях и восстановлении при каждом переходе.
            if (state == FeedState.Active)
            {
                if (!_announced)
                    _notices.Enqueue(new Notice("Микрофоны передаются в Телемост. В Телемосте выберите микрофон «CABLE Output».", NoticeLevel.Info));
                else if (_lastReported == FeedState.Error || _lastReported == FeedState.NoDriver)
                    _notices.Enqueue(new Notice("Микрофоны снова передаются в Телемост.", NoticeLevel.Success));
                _announced = true;
            }
            else if (state == FeedState.Error && _lastReported == FeedState.Active)
            {
                _notices.Enqueue(new Notice("Передача микрофонов в Телемост прервалась: " + (_error ?? "неизвестная ошибка") + ". Пробую подключиться снова.", NoticeLevel.Warning));
            }
            else if (state == FeedState.NoDriver && _lastReported == FeedState.Active)
            {
                _notices.Enqueue(new Notice("Виртуальный кабель пропал из Windows, микрофоны в Телемост не передаются.", NoticeLevel.Warning));
            }
            _lastReported = state;
        }

        public void Dispose()
        {
            _stop = true;
            _engine.Unsubscribe(_handler);
            if (_thread != null) _thread.Join(3000);
        }

        /// <summary>Отдаёт звук из буфера в формате 48 кГц, float, стерео.</summary>
        private sealed class BufferProvider : IWaveProvider
        {
            private readonly FeedBuffer _buffer;
            private float[] _mono = new float[0];
            private float[] _stereo = new float[0];

            public BufferProvider(FeedBuffer buffer)
            {
                _buffer = buffer;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(AudioEngine.Rate, 2);
            }

            public WaveFormat WaveFormat { get; private set; }

            public int Read(byte[] buffer, int offset, int count)
            {
                int frames = count / 8;
                if (_mono.Length < frames)
                {
                    _mono = new float[frames];
                    _stereo = new float[frames * 2];
                }
                _buffer.Read(_mono, frames);
                for (int i = 0; i < frames; i++)
                {
                    _stereo[i * 2] = _mono[i];
                    _stereo[i * 2 + 1] = _mono[i];
                }
                Buffer.BlockCopy(_stereo, 0, buffer, offset, frames * 8);
                return frames * 8;
            }
        }
    }

    /// <summary>
    /// Какие программы сейчас держат включённым микрофон (по записям Windows, как
    /// значок микрофона в области уведомлений). Нужно, чтобы не закрыть программу
    /// посреди ВКС: без неё у Телемоста пропадёт микрофон.
    /// </summary>
    internal static class MicrophoneUsage
    {
        private const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        public static List<string> ActiveApps()
        {
            var entries = new List<KeyValuePair<string, bool>>();
            try
            {
                using (RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                {
                    Collect(hkcu, StoreKey, false, entries);
                    Collect(hkcu, StoreKey + @"\NonPackaged", true, entries);
                }
            }
            catch (Exception)
            {
                return new List<string>();
            }
            return InUse(entries, IsRunning);
        }

        private static void Collect(RegistryKey root, string path, bool nonPackaged, List<KeyValuePair<string, bool>> into)
        {
            using (RegistryKey key = root.OpenSubKey(path))
            {
                if (key == null) return;
                foreach (string name in key.GetSubKeyNames())
                {
                    if (!nonPackaged && string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                    using (RegistryKey app = key.OpenSubKey(name))
                    {
                        if (app == null) continue;
                        long start = ToLong(app.GetValue("LastUsedTimeStart"));
                        long stop = ToLong(app.GetValue("LastUsedTimeStop"));
                        if (start > 0 && stop == 0) into.Add(new KeyValuePair<string, bool>(name, !nonPackaged));
                    }
                }
            }
        }

        private static long ToLong(object v)
        {
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            return 0;
        }

        /// <param name="entries">Ключ записи и признак программы из Microsoft Store.</param>
        public static List<string> InUse(IEnumerable<KeyValuePair<string, bool>> entries, Func<string, bool> isRunning)
        {
            var result = new List<string>();
            foreach (KeyValuePair<string, bool> e in entries)
            {
                string title;
                if (e.Value)
                {
                    title = e.Key;
                    int us = title.IndexOf('_');
                    if (us > 0) title = title.Substring(0, us);
                    int dot = title.LastIndexOf('.');
                    if (dot >= 0 && dot < title.Length - 1) title = title.Substring(dot + 1);
                }
                else
                {
                    string exe = ExeName(e.Key);
                    // Сама программа тоже слушает микрофоны, её не считаем.
                    if (string.Equals(exe, "MasterVKS", StringComparison.OrdinalIgnoreCase)) continue;
                    if (isRunning != null && !isRunning(exe)) continue;
                    title = AppTitle(exe, e.Key);
                }
                if (!result.Contains(title)) result.Add(title);
            }
            return result;
        }

        private static string ExeName(string key)
        {
            string file = key;
            int sep = Math.Max(file.LastIndexOf('#'), file.LastIndexOf('\\'));
            if (sep >= 0) file = file.Substring(sep + 1);
            return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
        }

        private static string AppTitle(string exe, string key)
        {
            string l = exe.ToLowerInvariant();
            string path = key.ToLowerInvariant();
            if (l.Contains("telemost") || l.Contains("телемост")) return "Телемост";
            if (l == "browser" && path.Contains("yandex")) return "Яндекс Браузер";
            if (l == "chrome") return "Google Chrome";
            if (l == "msedge") return "Microsoft Edge";
            if (l == "firefox") return "Firefox";
            if (l == "zoom") return "Zoom";
            return exe;
        }

        private static bool IsRunning(string exeName)
        {
            try
            {
                Process[] list = Process.GetProcessesByName(exeName);
                bool any = list.Length > 0;
                foreach (Process p in list) p.Dispose();
                return any;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
