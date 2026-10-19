using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using MasterVks.Core;
using MasterVks.Recording;
using MasterVks.Video;

namespace MasterVks.Checks
{
    /// <summary>Что удалось узнать о камере за проверку.</summary>
    internal sealed class CameraProbe
    {
        public string DeviceName;
        /// <summary>Камеру выбирали раньше, а сейчас её нет среди подключённых.</summary>
        public bool Missing;
        public List<string> Available = new List<string>();
        public string PrivacyBlock;
        public List<string> InUseBy = new List<string>();
        public CameraFailure Failure;
        public string ErrorLine;
        /// <summary>Камера открылась, но за отведённое время не прислала ни кадра.</summary>
        public bool NoFrames;
        public CameraPicture Picture;
        public Size NativeSize;
        public double NativeFps;
    }

    /// <summary>Правила оценки камеры. Только решения по уже снятым данным, поэтому проверяются автотестами.</summary>
    internal static class CameraRules
    {
        public const string AntivirusHint = "Если камера точно свободна, возможно, доступ к ней блокирует антивирус: в Kaspersky добавьте в доверенные не только MasterVKS.exe, но и ffmpeg.exe из папки программы (контроль доступа к веб-камере).";

        public static HealthVerdict Verdict(CameraProbe p)
        {
            string available = p.Available.Count > 0 ? "Сейчас Windows видит: " + string.Join(", ", p.Available) + "." : "Windows сейчас не видит ни одной камеры.";
            if (p.Missing)
                return new HealthVerdict(CheckStatus.Error, "Камера «" + p.DeviceName + "» не подключена",
                    "Проверьте USB-кабель камеры или выберите другую в списке «Камера» слева. " + available);
            if (p.PrivacyBlock != null)
                return new HealthVerdict(CheckStatus.Error, "Windows не пускает программы к камере",
                    Capitalize(p.PrivacyBlock) + ". Пока так, камеру не увидит и ВКС.");
            if (p.InUseBy.Count > 0)
                return new HealthVerdict(CheckStatus.Ok, "Камера включена в " + string.Join(", ", p.InUseBy),
                    "Камеру сейчас использует " + string.Join(", ", p.InUseBy) + ", значит она работает. Картинку с неё видно в окне ВКС. " +
                    "Чтобы не мешать ВКС, программа её не трогает.");

            if (p.Picture == null || p.Picture.Frames == 0)
            {
                string error = string.IsNullOrEmpty(p.ErrorLine) ? "" : " Ответ драйвера: " + p.ErrorLine;
                switch (p.Failure)
                {
                    case CameraFailure.Busy:
                        return new HealthVerdict(CheckStatus.Warning, "Камера занята другой программой",
                            "Закройте программы, которые могут её держать (Камера Windows, вкладка браузера с ВКС), и повторите проверку. Если ВКС уже идёт, просто посмотрите на картинку в её окне." + error);
                    case CameraFailure.NotFound:
                        return new HealthVerdict(CheckStatus.Error, "Камера не открылась: Windows её не нашла",
                            "Похоже, камеру только что отключили. Переподключите её и повторите проверку. " + available);
                }
                if (p.NoFrames && p.Failure == CameraFailure.None)
                    return new HealthVerdict(CheckStatus.Error, "Камера не присылает картинку",
                        "Камера включилась, но не прислала ни одного кадра. Переподключите её, лучше в другой USB-порт прямо в компьютере. " + AntivirusHint);
                return new HealthVerdict(CheckStatus.Error, "Камера не открылась",
                    "Переподключите камеру и повторите проверку. " + AntivirusHint + error);
            }

            CameraPicture pic = p.Picture;
            string facts = p.DeviceName +
                           (p.NativeSize.Width > 0 ? " · " + p.NativeSize.Width + " × " + p.NativeSize.Height : "") +
                           (pic.Fps > 0 ? " · " + Math.Round(pic.Fps).ToString(CultureInfo.InvariantCulture) + " кадров/с" : "");

            if (pic.Brightness < 14 && pic.Contrast < 8)
                return new HealthVerdict(CheckStatus.Warning, "Картинка с камеры чёрная",
                    "Проверьте шторку или крышку на объективе. Если её нет, переподключите камеру. " + facts);
            if (pic.Contrast < 3)
                return new HealthVerdict(CheckStatus.Warning, "Картинка с камеры однотонная",
                    "Похоже, объектив чем-то закрыт или камера показывает заглушку вместо изображения. " + facts);
            if (pic.Frozen)
                return new HealthVerdict(CheckStatus.Warning, "Камера отвечает, но картинка застыла",
                    "Все кадры одинаковые до последней точки: камера зависла или вместо неё показывается заглушка. Отключите камеру и подключите снова. " + facts);
            if (pic.Brightness < 35)
                return new HealthVerdict(CheckStatus.Warning, "Картинка с камеры очень тёмная",
                    "Камера работает, но собеседники будут видеть вас плохо. Включите свет или откройте шторку объектива. " + facts);
            if (pic.Frames >= 3 && pic.Fps > 0 && pic.Fps < 5)
                return new HealthVerdict(CheckStatus.Warning, "Камера присылает мало кадров: " + Math.Round(pic.Fps).ToString(CultureInfo.InvariantCulture) + " в секунду",
                    "Видео в ВКС будет дёргаться. Так бывает при слабом свете или перегруженном USB: включите свет или подключите камеру в другой порт. " + facts);
            return new HealthVerdict(CheckStatus.Ok, "Камера работает, картинка есть",
                facts + ". Снимок с камеры виден внизу, рядом с экраном; нажмите на него, чтобы посмотреть вживую. " +
                "Во время ВКС камерой пользуется программа ВКС, а МастерВКС следит, чтобы камера не отключилась.");
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0], CultureInfo.GetCultureInfo("ru-RU")) + s.Substring(1);
        }
    }

    internal sealed class CameraTestResult
    {
        public HealthVerdict Verdict;
        public CameraDevice Device;
        public CameraFrame LastFrame;
        public List<CameraDevice> Devices = new List<CameraDevice>();
    }

    /// <summary>Проверка камеры: найти, убедиться, что Windows пускает, и снять несколько секунд картинки.</summary>
    internal static class CameraTest
    {
        public const int PreviewWidth = 320;
        public const int PreviewHeight = 180;

        /// <summary>Выбирает камеру по настройкам. Если камеру ещё не выбирали, берёт подходящую сама.</summary>
        public static CameraDevice Resolve(AppSettings settings, IList<CameraDevice> devices)
        {
            CameraDevice found = CameraDevices.Find(devices, settings.CameraName, settings.CameraId);
            if (found != null) return found;
            if (!settings.CameraInitialized) return CameraDevices.PickDefault(devices);
            return null;
        }

        /// <param name="onFrame">Кадры для живой картинки в окне, пока идёт проверка.</param>
        public static CameraTestResult Run(AppSettings settings, Action<CameraFrame> onFrame)
        {
            var result = new CameraTestResult();
            string ffmpeg = Ffmpeg.Locate(settings.FfmpegPath);
            if (ffmpeg == null)
            {
                result.Verdict = new HealthVerdict(CheckStatus.Skipped, "Не проверена: нужен модуль записи FFmpeg", "Камера проверяется через FFmpeg, как и запись.");
                return result;
            }

            result.Devices = CameraDevices.List(ffmpeg);
            var probe = new CameraProbe { Available = result.Devices.Select(d => d.Name).ToList() };
            CameraDevice device = Resolve(settings, result.Devices);
            result.Device = device;
            if (device == null)
            {
                if (settings.CameraInitialized && !string.IsNullOrEmpty(settings.CameraName))
                {
                    probe.DeviceName = settings.CameraName;
                    probe.Missing = true;
                    result.Verdict = CameraRules.Verdict(probe);
                }
                else if (settings.CameraInitialized)
                    result.Verdict = new HealthVerdict(CheckStatus.Skipped, "Не выбрана", "Выберите камеру в списке «Камера» слева, если её нужно проверять.");
                else
                    result.Verdict = new HealthVerdict(CheckStatus.Skipped, "Камера не найдена",
                        "Windows не видит ни одной камеры. Если камера нужна, подключите её и нажмите «Проверить».");
                return result;
            }
            probe.DeviceName = device.Name;

            // Сначала то, что можно узнать, не включая камеру.
            probe.PrivacyBlock = CameraUsage.PrivacyBlock();
            if (probe.PrivacyBlock == null) probe.InUseBy = CameraUsage.ActiveApps(ffmpeg);
            if (probe.PrivacyBlock != null || probe.InUseBy.Count > 0)
            {
                result.Verdict = CameraRules.Verdict(probe);
                return result;
            }

            var frames = new List<CameraFrame>();
            using (var gotFrame = new ManualResetEventSlim(false))
            {
                double firstAt = -1;
                double lastPreview = -1;
                using (var grabber = new CameraGrabber(ffmpeg, CameraDevices.InputArgs(device), PreviewWidth, PreviewHeight, f =>
                {
                    lock (frames)
                    {
                        if (firstAt < 0) firstAt = f.Seconds;
                        frames.Add(f);
                        if (frames.Count > 60) frames.RemoveAt(0);
                    }
                    gotFrame.Set();
                    if (onFrame != null && f.Seconds - lastPreview >= 0.1)
                    {
                        lastPreview = f.Seconds;
                        onFrame(f);
                    }
                }))
                {
                    // Камере нужно время, чтобы включиться; потом снимаем три секунды.
                    var wait = Stopwatch.StartNew();
                    while (!gotFrame.IsSet && !grabber.Exited && wait.ElapsedMilliseconds < 10000) gotFrame.Wait(100);
                    if (gotFrame.IsSet)
                    {
                        var collect = Stopwatch.StartNew();
                        while (collect.ElapsedMilliseconds < 3000 && !grabber.Exited) Thread.Sleep(50);
                    }
                    Thread.Sleep(100);
                    probe.NativeSize = grabber.NativeSize;
                    probe.NativeFps = grabber.NativeFps;
                    string log = grabber.LogText;
                    lock (frames)
                    {
                        // Яркость и застывание считаем по последней секунде с лишним: первые кадры бывают тёмными.
                        List<CameraFrame> tail = frames.Where(f => f.Seconds >= firstAt + 1.0).ToList();
                        if (tail.Count < 4) tail = frames.ToList();
                        probe.Picture = CameraPicture.Analyze(tail);
                        if (frames.Count > 0) result.LastFrame = frames[frames.Count - 1];
                    }
                    if (probe.Picture.Frames == 0)
                    {
                        probe.Failure = CameraErrors.Classify(grabber.Exited ? log : "");
                        probe.NoFrames = !grabber.Exited;
                        probe.ErrorLine = CameraErrors.LastError(log);
                        if (probe.Failure != CameraFailure.None) Log.Warn("Камера не открылась: " + log);
                    }
                }
            }
            // Если камера оказалась занята, узнаём кем, уже после того как отпустили её.
            if (probe.Picture.Frames == 0 && probe.Failure == CameraFailure.Busy) probe.InUseBy = CameraUsage.ActiveApps(ffmpeg);

            result.Verdict = CameraRules.Verdict(probe);
            return result;
        }
    }

    /// <summary>
    /// Решает, о чём сообщить по камере во время работы. Предупреждает один раз,
    /// когда что-то изменилось, без спама. Отдельно от Windows, проверяется автотестами.
    /// </summary>
    internal sealed class CameraWatch
    {
        private const double IdleWarnSeconds = 90;

        private int _missingPolls, _presentPolls;
        private bool _missingWarned;
        private int _usedPolls, _unusedPolls;
        private bool _used;
        private string _lastApps;
        private bool _recording;
        private double _recordingSince;
        private bool _everUsedInSession, _idleWarned;
        private string _cameraName;

        public bool Missing
        {
            get { return _missingWarned; }
        }

        /// <param name="present">Подключена ли камера (null — в этот раз не проверяли).</param>
        /// <param name="inUseBy">Какие программы сейчас держат камеру.</param>
        public List<Notice> Feed(string cameraName, bool? present, IList<string> inUseBy, bool recording, double now)
        {
            var notices = new List<Notice>();
            if (!string.Equals(cameraName, _cameraName, StringComparison.Ordinal))
            {
                // Выбрали другую камеру: прежние «пропала» к ней не относятся.
                _cameraName = cameraName;
                _missingPolls = _presentPolls = 0;
                _missingWarned = false;
            }
            if (string.IsNullOrEmpty(cameraName)) return notices;

            if (recording && !_recording)
            {
                _recordingSince = now;
                _everUsedInSession = _used;
                _idleWarned = false;
            }
            _recording = recording;

            // Камера пропала или вернулась (два опроса подряд, чтобы не реагировать на случайность).
            if (present == false)
            {
                _presentPolls = 0;
                _missingPolls++;
                if (_missingPolls >= 2 && !_missingWarned)
                {
                    _missingWarned = true;
                    notices.Add(new Notice("Камера «" + cameraName + "» отключилась от компьютера. Проверьте её USB-кабель." +
                                           (recording ? " Запись экрана и звука продолжается." : ""), NoticeLevel.Warning));
                }
            }
            else if (present == true)
            {
                _missingPolls = 0;
                _presentPolls++;
                if (_missingWarned)
                {
                    _missingWarned = false;
                    notices.Add(new Notice("Камера «" + cameraName + "» снова подключена. Если в ВКС картинки нет, выключите и снова включите камеру в окне ВКС.", NoticeLevel.Success));
                }
            }

            // Включена ли камера в программе ВКС.
            bool usedNow = inUseBy != null && inUseBy.Count > 0;
            if (usedNow)
            {
                _lastApps = string.Join(", ", inUseBy);
                _unusedPolls = 0;
                _usedPolls++;
                if (!_used && _usedPolls >= 2)
                {
                    _used = true;
                    if (recording && _everUsedInSession)
                        notices.Add(new Notice("Камера снова включена (" + _lastApps + ").", NoticeLevel.Success));
                    _everUsedInSession |= recording;
                }
            }
            else
            {
                _usedPolls = 0;
                _unusedPolls++;
                if (_used && _unusedPolls >= 2)
                {
                    _used = false;
                    if (recording && !_missingWarned)
                        notices.Add(new Notice(_lastApps + " выключил камеру. Если вы не выключали видео в ВКС, проверьте камеру.", NoticeLevel.Info));
                }
            }

            if (recording && !_everUsedInSession && !_idleWarned && !_missingWarned && now - _recordingSince >= IdleWarnSeconds)
            {
                _idleWarned = true;
                notices.Add(new Notice("Камеру сейчас не использует ни одна программа. Если собеседники должны вас видеть, включите камеру в окне ВКС.", NoticeLevel.Info));
            }
            return notices;
        }
    }

    /// <summary>
    /// Фоновое наблюдение за камерой: кто ею пользуется (раз в 3 секунды, по
    /// записям Windows) и не отключилась ли она (во время записи раз в 15 секунд).
    /// Саму камеру не открывает, поэтому ВКС не мешает.
    /// </summary>
    internal sealed class CameraMonitor : IDisposable
    {
        private readonly CameraWatch _watch = new CameraWatch();
        private readonly ConcurrentQueue<Notice> _notices = new ConcurrentQueue<Notice>();
        private readonly object _sync = new object();
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _recording;
        private string _name, _id, _ffmpegPath;
        private volatile string[] _inUseBy = new string[0];
        private volatile bool _missing;

        /// <summary>Программы, которые сейчас держат камеру.</summary>
        public string[] InUseBy
        {
            get { return _inUseBy; }
        }

        public bool Missing
        {
            get { return _missing; }
        }

        public bool TryTakeNotice(out Notice notice)
        {
            return _notices.TryDequeue(out notice);
        }

        public void SetDevice(string name, string id, string ffmpegPath)
        {
            lock (_sync)
            {
                _name = name;
                _id = id;
                _ffmpegPath = ffmpegPath;
            }
        }

        public void SetRecording(bool recording)
        {
            _recording = recording;
        }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Камера: наблюдение", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void Loop()
        {
            var clock = Stopwatch.StartNew();
            double lastPresence = -1000;
            while (!_stop)
            {
                try
                {
                    string name, id, ffmpeg;
                    lock (_sync)
                    {
                        name = _name;
                        id = _id;
                        ffmpeg = _ffmpegPath;
                    }
                    bool recording = _recording;
                    double now = clock.Elapsed.TotalSeconds;

                    List<string> apps = CameraUsage.ActiveApps(ffmpeg);
                    _inUseBy = apps.ToArray();

                    bool? present = null;
                    if (recording && !string.IsNullOrEmpty(name) && ffmpeg != null && now - lastPresence >= 15)
                    {
                        lastPresence = now;
                        List<CameraDevice> list = CameraDevices.TryList(ffmpeg);
                        if (list != null) present = CameraDevices.Find(list, name, id) != null;
                    }
                    else if (!recording)
                    {
                        lastPresence = -1000;
                    }

                    foreach (Notice n in _watch.Feed(name, present, apps, recording, now))
                    {
                        if (n.Level >= NoticeLevel.Warning) Log.Warn("Камера: " + n.Text);
                        else Log.Info("Камера: " + n.Text);
                        _notices.Enqueue(n);
                    }
                    _missing = _watch.Missing;
                }
                catch (Exception ex)
                {
                    Log.Warn("Сбой наблюдения за камерой", ex);
                }
                for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            _stop = true;
            if (_thread != null) _thread.Join(3000);
        }
    }
}
