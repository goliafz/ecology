using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MasterVks.Audio;
using MasterVks.Core;
using MasterVks.Recording;
using MasterVks.Video;

namespace MasterVks.Checks
{
    internal enum CheckStatus
    {
        Pending,
        Running,
        Skipped,
        Ok,
        Warning,
        Error
    }

    internal sealed class CheckItem
    {
        public string Key;
        public string Title;
        public CheckStatus Status = CheckStatus.Pending;
        public string Summary = "Ожидает проверки";
        public string Details = "";

        public CheckItem Copy()
        {
            return (CheckItem)MemberwiseClone();
        }
    }

    internal sealed class CheckReport
    {
        public readonly List<CheckItem> Items = new List<CheckItem>();
        public CheckMode Mode;
        public DateTime Time = DateTime.Now;

        public CheckStatus Overall
        {
            get
            {
                CheckStatus worst = CheckStatus.Ok;
                foreach (CheckItem i in Items)
                    if (i.Status == CheckStatus.Error || (i.Status == CheckStatus.Warning && worst != CheckStatus.Error))
                        worst = i.Status;
                return worst;
            }
        }
    }

    /// <summary>
    /// Проверка всего, что нужно для записи. Тихий режим ничего не проигрывает
    /// вслух (звук компьютера проверяется неслышным сигналом), режим «со звуком»
    /// проигрывает короткую мелодию и проверяет, что её слышат оба микрофона.
    /// </summary>
    internal sealed class SystemCheck
    {
        public const string KeyFfmpeg = "ffmpeg";
        public const string KeyScreen = "screen";
        public const string KeyMic1 = "mic1";
        public const string KeyMic2 = "mic2";
        public const string KeySystem = "sys";
        public const string KeyTogether = "together";
        public const string KeyDisk = "disk";

        private static readonly double[] ChimeFrequencies = { 880, 1320, 1760 };
        private static readonly double[] ProbeFrequencies = { 14000 };

        private readonly AppSettings _settings;
        private readonly AudioEngine _engine;
        private readonly CheckMode _mode;
        private readonly Action<CheckItem> _onUpdate;
        private readonly Dictionary<string, CheckItem> _items = new Dictionary<string, CheckItem>();
        private readonly CheckReport _report = new CheckReport();

        public static List<CheckItem> CreateItems()
        {
            return new List<CheckItem>
            {
                new CheckItem { Key = KeyFfmpeg, Title = "Модуль записи" },
                new CheckItem { Key = KeyScreen, Title = "Экран" },
                new CheckItem { Key = KeyMic1, Title = "Микрофон 1" },
                new CheckItem { Key = KeyMic2, Title = "Микрофон 2" },
                new CheckItem { Key = KeySystem, Title = "Звук собеседников" },
                new CheckItem { Key = KeyTogether, Title = "Два микрофона вместе" },
                new CheckItem { Key = KeyDisk, Title = "Папка и место на диске" }
            };
        }

        public SystemCheck(AppSettings settings, AudioEngine engine, CheckMode mode, Action<CheckItem> onUpdate)
        {
            _settings = settings.Clone();
            _engine = engine;
            _mode = mode;
            _onUpdate = onUpdate;
            _report.Mode = mode;
            foreach (CheckItem item in CreateItems())
            {
                _items[item.Key] = item;
                _report.Items.Add(item);
            }
        }

        /// <summary>Выполняет проверку целиком. Вызывать из фонового потока.</summary>
        public CheckReport Run()
        {
            Log.Info("Проверка (" + (_mode == CheckMode.Audible ? "со звуком" : "тихая") + "): начало");
            foreach (CheckItem item in _report.Items) Set(item.Key, CheckStatus.Running, "Проверяю…", "");

            Safe(KeyDisk, CheckDisk);
            Safe(KeyFfmpeg, CheckFfmpeg);
            Safe(KeyScreen, CheckScreen);
            try
            {
                CheckAudio();
            }
            catch (Exception ex)
            {
                Log.Error("Сбой проверки звука", ex);
                foreach (string key in new[] { KeyMic1, KeyMic2, KeySystem, KeyTogether })
                    if (_items[key].Status == CheckStatus.Running) Set(key, CheckStatus.Error, "Проверка не удалась: " + ex.Message, "");
            }

            foreach (CheckItem item in _report.Items)
                Log.Info("Проверка: " + item.Title + ": " + item.Status + ": " + item.Summary + (string.IsNullOrEmpty(item.Details) ? "" : " (" + item.Details + ")"));
            return _report;
        }

        private void Safe(string key, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Сбой проверки «" + _items[key].Title + "»", ex);
                Set(key, CheckStatus.Error, "Проверка не удалась: " + ex.Message, "");
            }
        }

        private void Set(string key, CheckStatus status, string summary, string details)
        {
            CheckItem item = _items[key];
            item.Status = status;
            item.Summary = summary;
            item.Details = details ?? "";
            if (_onUpdate != null)
            {
                try
                {
                    _onUpdate(item.Copy());
                }
                catch (Exception)
                {
                }
            }
        }

        // ===================== Диск =====================

        private void CheckDisk()
        {
            string folder = _settings.OutputFolder;
            try
            {
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".mastervks-proverka-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllBytes(probe, new byte[4096]);
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                Set(KeyDisk, CheckStatus.Error, "В папку нельзя записать файлы: " + ex.Message, folder);
                return;
            }

            long free = DiskSpace.FreeBytes(folder);
            if (free < 0)
            {
                Set(KeyDisk, CheckStatus.Ok, "Папка доступна для записи", "Свободное место узнать не удалось (сетевая папка?). " + folder);
                return;
            }

            MonitorDesc monitor = MonitorList.Find(_settings.MonitorDevice) ?? MonitorList.Primary();
            int w = monitor != null ? monitor.Bounds.Width : 1920;
            int h = monitor != null ? monitor.Bounds.Height : 1080;
            double rate = RecordingArgs.EstimateBytesPerSecond(w, h, _settings.FrameRate, _settings.Quality);
            double needFor2h = rate * 7200;
            TimeSpan enough = TimeSpan.FromSeconds(Math.Min(free / rate, 360000));

            if (free < needFor2h)
                Set(KeyDisk, CheckStatus.Warning, "Места хватит примерно на " + Fmt.Duration(enough) + " записи, это меньше двух часов",
                    "Свободно " + Fmt.Size(free) + ", для двух часов нужно около " + Fmt.Size((long)needFor2h) + ". Удалите или перенесите старые записи.");
            else
                Set(KeyDisk, CheckStatus.Ok, "Свободно " + Fmt.Size(free) + ", хватит примерно на " + Fmt.Duration(enough) + " записи", folder);
        }

        // ===================== FFmpeg =====================

        private void CheckFfmpeg()
        {
            string path = Ffmpeg.Locate(_settings.FfmpegPath);
            if (path == null)
            {
                Set(KeyFfmpeg, CheckStatus.Error, "Не найден FFmpeg, без него запись невозможна",
                    "Скачайте ffmpeg-release-essentials.zip с сайта gyan.dev и положите ffmpeg.exe рядом с программой. Подробности есть в «Настройках».");
                return;
            }
            string version = Ffmpeg.Version(path);
            if (version == null)
            {
                Set(KeyFfmpeg, CheckStatus.Error, "FFmpeg найден, но не запускается", path + ". Возможно, его блокирует антивирус: добавьте папку программы в доверенные.");
                return;
            }
            if (!Ffmpeg.HasEncoder(path, "libx264"))
            {
                Set(KeyFfmpeg, CheckStatus.Error, "В этой сборке FFmpeg нет кодировщика H.264 (libx264)", "Скачайте сборку «essentials» с gyan.dev, в ней всё есть. Сейчас: " + path);
                return;
            }

            MonitorDesc monitor = MonitorList.Find(_settings.MonitorDevice) ?? MonitorList.Primary();
            int w = monitor != null ? monitor.Bounds.Width & ~1 : 1920;
            int h = monitor != null ? monitor.Bounds.Height & ~1 : 1080;
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=" + w + "x" + h + ":rate=" + _settings.FrameRate,
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-t", "2", "-map", "0:v", "-map", "1:a",
                "-c:v", "libx264", "-preset", RecordingArgs.Preset(_settings.Quality), "-crf", RecordingArgs.Crf(_settings.Quality).ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-f", "null", "-"
            };
            long t0 = Clock.Now;
            Ffmpeg.RunResult r = Ffmpeg.Run(path, args, 60000);
            double seconds = Clock.ToSeconds(Clock.Now - t0);
            if (r.ExitCode != 0)
            {
                Log.Warn("Пробное сжатие не удалось: " + r.Error);
                Set(KeyFfmpeg, CheckStatus.Error, "FFmpeg не смог сжать пробное видео", FirstLine(r.Error));
                return;
            }
            string details = "FFmpeg " + version + " · пробные 2 секунды видео " + w + "×" + h + " сжаты за " + seconds.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " с";
            if (seconds > 3.5)
                Set(KeyFfmpeg, CheckStatus.Warning, "Работает, но сжимает видео медленно, компьютер может не успевать", details + ". Попробуйте снизить частоту кадров в настройках.");
            else
                Set(KeyFfmpeg, CheckStatus.Ok, "Готов к записи", details);
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
            return line.Trim();
        }

        // ===================== Экран =====================

        private void CheckScreen()
        {
            MonitorDesc requested = MonitorList.Find(_settings.MonitorDevice);
            string notFound = null;
            if (requested == null && !string.IsNullOrEmpty(_settings.MonitorDevice))
                notFound = "Выбранный экран не найден, будет записан основной. ";

            int frames = 0, failed = 0;
            double totalMs = 0, darkShare = 1;
            int width = 0, height = 0;
            Exception error = null;
            string sourceName = null;

            var thread = new Thread(() =>
            {
                try
                {
                    Native.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                }
                catch (Exception)
                {
                }
                try
                {
                    using (var capturer = new ScreenCapturer(_settings.MonitorDevice, false))
                    {
                        width = capturer.Width;
                        height = capturer.Height;
                        sourceName = capturer.SourceDevice;
                        var buffer = new byte[capturer.FrameBytes];
                        for (int i = 0; i < 8; i++)
                        {
                            long t0 = Clock.Now;
                            if (capturer.Capture(buffer))
                            {
                                frames++;
                                if (i > 0) totalMs += Clock.ToSeconds(Clock.Now - t0) * 1000;
                                darkShare = ScreenCapturer.DarkShare(buffer, width, height);
                            }
                            else
                            {
                                failed++;
                            }
                            Thread.Sleep(30);
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }) { IsBackground = true, Name = "Проверка экрана" };
            thread.Start();
            thread.Join(20000);

            if (error != null)
            {
                Set(KeyScreen, CheckStatus.Error, "Экран захватить не удалось", error.Message);
                return;
            }
            if (frames == 0)
            {
                Set(KeyScreen, CheckStatus.Error, "Экран сейчас недоступен для записи", "Windows не отдала ни одного кадра. Если открыт экран блокировки или окно UAC, закройте его и повторите проверку.");
                return;
            }

            double avg = frames > 1 ? totalMs / (frames - 1) : totalMs;
            double possibleFps = avg > 0 ? 1000.0 / avg : 999;
            string details = (notFound ?? "") + width + " × " + height + " · один кадр снимается за " + avg.ToString("0", CultureInfo.InvariantCulture) + " мс";

            if (darkShare > 0.995)
            {
                Set(KeyScreen, CheckStatus.Warning, "Экран захватывается, но картинка полностью чёрная",
                    details + ". Если на экране сейчас что-то видно, возможно, программа запрещает запись своего окна.");
                return;
            }
            if (possibleFps < _settings.FrameRate * 0.9)
            {
                Set(KeyScreen, CheckStatus.Warning, "Экран доступен, но снимается медленно",
                    details + ". Получится около " + (int)possibleFps + " кадров/с вместо " + _settings.FrameRate + ". Запись будет, просто менее плавной.");
                return;
            }
            Set(KeyScreen, notFound != null ? CheckStatus.Warning : CheckStatus.Ok, "Экран доступен", details);
        }

        // ===================== Звук =====================

        private void CheckAudio()
        {
            AudioSource mic1 = _engine.Mic1, mic2 = _engine.Mic2, sys = _engine.System;

            // Ждём, пока устройства подключатся (сразу после запуска это может занять пару секунд).
            long deadline = Clock.Now + Clock.Frequency * 4;
            while (Clock.Now < deadline)
            {
                bool allReady = _engine.Sources.All(s => !s.IsConfigured || s.State == SourceState.Active);
                if (allReady) break;
                Thread.Sleep(100);
            }

            long under1 = mic1.Underruns, under2 = mic2.Underruns;
            var collector = new AudioCollector();
            Action<AudioBlock> handler = collector.OnBlock;
            _engine.Subscribe(handler);

            int toneStart = -1, toneEnd = -1;
            string playError = null;
            double[] freqs;
            try
            {
                Thread.Sleep(900);
                List<ToneStep> steps;
                if (_mode == CheckMode.Audible)
                {
                    freqs = ChimeFrequencies;
                    steps = new List<ToneStep>
                    {
                        new ToneStep(880, 260, 0.25f), new ToneStep(0, 50, 0),
                        new ToneStep(1320, 260, 0.25f), new ToneStep(0, 50, 0),
                        new ToneStep(1760, 420, 0.25f), new ToneStep(0, 300, 0)
                    };
                }
                else
                {
                    freqs = ProbeFrequencies;
                    steps = new List<ToneStep> { new ToneStep(14000, 1200, 0.001f), new ToneStep(0, 300, 0) };
                }

                string renderId = string.IsNullOrEmpty(_settings.OutputDeviceId) ? null : _settings.OutputDeviceId;
                toneStart = collector.Position;
                long startedTicks;
                playError = TonePlayer.Play(renderId, steps, out startedTicks);
                toneEnd = collector.Position;
                Thread.Sleep(900);
            }
            finally
            {
                _engine.Unsubscribe(handler);
            }

            AudioCollector.Snapshot snap = collector.Take();
            int skip = AudioEngine.Rate / 5;
            bool tonePlayed = playError == null;
            int ts = tonePlayed ? toneStart : -1;

            SourceAnalysis a1 = AudioAnalyzer.Analyze(snap.Mic1, snap.Mic1Real, snap.TotalFrames, skip, ts, toneEnd, freqs);
            SourceAnalysis a2 = AudioAnalyzer.Analyze(snap.Mic2, snap.Mic2Real, snap.TotalFrames, skip, ts, toneEnd, freqs);
            SourceAnalysis aS = AudioAnalyzer.Analyze(snap.Sys, snap.SysReal, snap.TotalFrames, skip, ts, toneEnd, freqs);

            long drop1 = mic1.Underruns - under1, drop2 = mic2.Underruns - under2;
            CheckStatus s1 = MicVerdict(KeyMic1, mic1, a1, drop1, tonePlayed);
            CheckStatus s2 = MicVerdict(KeyMic2, mic2, a2, drop2, tonePlayed);
            SystemVerdict(sys, aS, playError);
            TogetherVerdict(mic1, mic2, a1, a2, s1, s2, drop1, drop2, tonePlayed);
        }

        private CheckStatus MicVerdict(string key, AudioSource src, SourceAnalysis a, long dropouts, bool tonePlayed)
        {
            string device = src.ActiveDeviceName ?? "";
            if (!src.IsConfigured)
            {
                Set(key, CheckStatus.Skipped, "Не выбран", "Выберите устройство в списке «Микрофон» слева.");
                return CheckStatus.Skipped;
            }
            if (src.State != SourceState.Active && a.RealShare < 0.1)
            {
                Set(key, CheckStatus.Error, "Не работает", src.StateText);
                return CheckStatus.Error;
            }
            if (a.RealShare < 0.5)
            {
                Set(key, CheckStatus.Error, "Подключён, но звук от него почти не приходит", device + ". Попробуйте переподключить устройство или выбрать другой USB-порт.");
                return CheckStatus.Error;
            }
            if (a.AllZero)
            {
                Set(key, CheckStatus.Error, "Присылает полную тишину",
                    device + ". Скорее всего, доступ к микрофону запрещён: «Параметры Windows → Конфиденциальность → Микрофон» (разрешите классическим приложениям) или антивирус (в Kaspersky это контроль доступа к микрофону). Также проверьте кнопку отключения на самом микрофоне.");
                return CheckStatus.Error;
            }

            // Каждая проблема: что не так (для короткой строки) и что с этим делать.
            var problems = new List<KeyValuePair<string, string>>();
            if (dropouts > 2)
                problems.Add(Problem("звук прерывается (" + dropouts + " " + Fmt.Plural(dropouts, "разрыв", "разрыва", "разрывов") + " за проверку)",
                    "попробуйте другой USB-порт, лучше прямо в ноутбуке, без разветвителя"));
            if (a.ClipShare > 0.002)
                problems.Add(Problem("слишком громко, звук хрипит", "уменьшите уровень микрофона в настройках звука Windows"));
            if (a.Hum)
                problems.Add(Problem("слышен гул 50 Гц (наводка " + Fmt.Db(a.HumDb) + ")",
                    "частая причина в HDMI-кабеле, зарядке или удлинителе; попробуйте другой порт или работу от батареи"));
            if (a.NoiseFloorDb > -30)
                problems.Add(Problem("сильный фоновый шум (" + Fmt.Db(a.NoiseFloorDb) + ")", "если сейчас в комнате тихо, это помехи"));
            if (a.LoudDb < -90)
                problems.Add(Problem("сигнал очень слабый (" + Fmt.Db(a.LoudDb) + ")", "проверьте, не выключен ли микрофон и не стоит ли уровень на нуле"));

            bool heard = tonePlayed && _mode == CheckMode.Audible && a.ToneSnrDb >= 10;
            if (_mode == CheckMode.Audible && tonePlayed && !heard)
                problems.Add(Problem("не услышал проверочную мелодию", "проверьте громкость динамиков или телевизора и где стоит микрофон"));

            string details = device + " · фон " + Fmt.Db(a.NoiseFloorDb) + " · громкие места " + Fmt.Db(a.LoudDb);
            if (heard) details += " · мелодия громче фона на " + a.ToneSnrDb.ToString("0", CultureInfo.InvariantCulture) + " дБ";

            if (problems.Count > 0)
            {
                string summary = problems.Count == 1 ? "Работает, но " + problems[0].Key : "Работает, но есть замечания";
                string advice = string.Join(" ", problems.Select(p => Capitalize(p.Key) + ": " + p.Value + "."));
                Set(key, CheckStatus.Warning, summary, advice + " " + details);
                return CheckStatus.Warning;
            }
            Set(key, CheckStatus.Ok, heard ? "Работает и слышит проверочную мелодию" : "Работает, звук идёт", details);
            return CheckStatus.Ok;
        }

        private void SystemVerdict(AudioSource src, SourceAnalysis a, string playError)
        {
            string device = src.ActiveDeviceName ?? "";
            if (src.State != SourceState.Active && a.RealShare < 0.05)
            {
                Set(KeySystem, CheckStatus.Error, "Не работает", src.StateText);
                return;
            }
            bool otherSound = a.LoudDb > -60;
            if (playError != null)
            {
                if (otherSound)
                    Set(KeySystem, CheckStatus.Ok, "Работает, звук идёт", device + ". Проверочный сигнал проиграть не удалось: " + playError);
                else
                    Set(KeySystem, CheckStatus.Warning, "Подключено, но проверочный сигнал проиграть не удалось", playError + ". " + device);
                return;
            }

            bool toneOk = a.ToneSnrDb >= 20;
            if (toneOk)
            {
                Set(KeySystem, CheckStatus.Ok,
                    _mode == CheckMode.Audible ? "Работает: проверочная мелодия записалась" : "Работает (проверено неслышным сигналом)",
                    "Пишется всё, что звучит в «" + device + "»: голоса собеседников из Телемоста и любые другие звуки.");
                return;
            }
            if (otherSound)
            {
                Set(KeySystem, CheckStatus.Ok, "Работает, звук идёт", device);
                return;
            }
            Set(KeySystem, CheckStatus.Warning, "Подключено, но проверочный сигнал в запись не попал",
                "Проверьте, что звук в Windows не выключен и в списке «Звук собеседников» выбрано то устройство, через которое слышны собеседники (например, телевизор по HDMI). " + device);
        }

        private void TogetherVerdict(AudioSource mic1, AudioSource mic2, SourceAnalysis a1, SourceAnalysis a2,
                                     CheckStatus s1, CheckStatus s2, long drop1, long drop2, bool tonePlayed)
        {
            if (!mic1.IsConfigured && !mic2.IsConfigured)
            {
                Set(KeyTogether, CheckStatus.Skipped, "Микрофоны не выбраны", "Выберите микрофоны в списках слева.");
                return;
            }
            if (!mic1.IsConfigured || !mic2.IsConfigured)
            {
                Set(KeyTogether, CheckStatus.Skipped, "Выбран только один микрофон", "Чтобы писать оба микрофона, выберите второй в списке слева.");
                return;
            }
            bool sameDevice = (!string.IsNullOrEmpty(_settings.Mic1Id) && _settings.Mic1Id == _settings.Mic2Id)
                              || (!string.IsNullOrEmpty(mic1.ActiveDeviceName) && mic1.ActiveDeviceName == mic2.ActiveDeviceName);
            if (sameDevice)
            {
                Set(KeyTogether, CheckStatus.Warning, "В обоих пунктах выбрано одно и то же устройство", "Выберите для «Микрофона 2» другое устройство (например, микрофон камеры).");
                return;
            }
            if (s1 == CheckStatus.Error || s2 == CheckStatus.Error)
            {
                string which = s1 == CheckStatus.Error && s2 == CheckStatus.Error ? "оба микрофона" : (s1 == CheckStatus.Error ? "микрофон 1" : "микрофон 2");
                Set(KeyTogether, CheckStatus.Error, "Вместе не работают: не работает " + which, "Подробности в строке микрофона выше.");
                return;
            }

            var problems = new List<string>();
            if (drop1 > 2 || drop2 > 2)
                problems.Add("при одновременной работе звук прерывается: подключите микрофон и камеру в разные USB-порты ноутбука, без разветвителя");
            if (a1.Hum || a2.Hum)
                problems.Add("есть наводка (гул 50 Гц), проверьте кабели и питание");

            string details = "Оба микрофона пишутся одновременно, каждый ещё и на свою дорожку. Если в общей что-то не так, нужный микрофон можно включить отдельно.";
            if (tonePlayed && _mode == CheckMode.Audible && a1.ToneOnset >= 0 && a2.ToneOnset >= 0)
            {
                int ms = Math.Abs(a1.ToneOnset - a2.ToneOnset) * 1000 / AudioEngine.Rate;
                if (ms > 100)
                    problems.Add("микрофоны слышат звук с разницей около " + ms + " мс, поэтому в общей дорожке возможно лёгкое эхо (отдельные дорожки чистые)");
                else
                    details = "Разница во времени между микрофонами около " + ms + " мс, эха не будет. " + details;
            }

            if (problems.Count > 0)
            {
                Set(KeyTogether, CheckStatus.Warning, "Работают, но есть замечания", Capitalize(string.Join("; ", problems)) + ".");
                return;
            }
            Set(KeyTogether, CheckStatus.Ok, "Работают одновременно без сбоев и помех", details);
        }

        private static KeyValuePair<string, string> Problem(string what, string advice)
        {
            return new KeyValuePair<string, string>(what, advice);
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0], CultureInfo.GetCultureInfo("ru-RU")) + s.Substring(1);
        }
    }
}
