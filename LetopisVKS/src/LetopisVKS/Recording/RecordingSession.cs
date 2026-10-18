using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using LetopisVks.Audio;
using LetopisVks.Core;
using LetopisVks.Video;

namespace LetopisVks.Recording
{
    internal enum NoticeLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    internal sealed class Notice
    {
        public readonly DateTime Time = DateTime.Now;
        public readonly string Text;
        public readonly NoticeLevel Level;

        public Notice(string text, NoticeLevel level)
        {
            Text = text;
            Level = level;
        }
    }

    internal enum SessionState
    {
        Starting,
        Recording,
        Paused,
        Stopping,
        Stopped
    }

    /// <summary>
    /// Одна запись от нажатия «Начать» до «Остановить». Следит за ffmpeg, местом на
    /// диске и экраном, перезапускает модуль записи при сбое и никогда не бросает
    /// запись молча.
    /// </summary>
    internal sealed class RecordingSession
    {
        private const double StallSeconds = 25;

        private readonly AppSettings _settings;
        private readonly AudioEngine _engine;
        private readonly string _ffmpegPath;
        private readonly List<string> _parts = new List<string>();
        private readonly ConcurrentQueue<Notice> _notices = new ConcurrentQueue<Notice>();
        private readonly Action<AudioBlock> _audioHandler;

        private VideoPump _pump;
        private volatile RecordingSegment _segment;
        private PendingJournal _journal;
        private Thread _watchdog;
        private volatile bool _stopRequested;
        private volatile bool _paused;
        private volatile SessionState _state = SessionState.Starting;
        private int _segmentIndex;
        private double _completedActiveSeconds;
        private long _completedBytes;
        private long _currentBytes;
        private long _lastResumeTicks;
        private long _nextRestartTicks;
        private int _quickFailures;
        private readonly List<long> _restartTimes = new List<long>();

        private double _bytesPerSecond;
        private long _lastRateTicks;
        private long _lastRateBytes;
        private long _freeBytes = -1;
        private long _lastDiskCheck;
        private int _diskWarningLevel;
        private long _slowSinceTicks;
        private bool _slowWarned;
        private long _lastFlushTicks;
        private string _pendingPumpProblem;

        public readonly string Folder;
        public readonly string BaseName;
        public readonly string Mp4Path;
        public readonly DateTime StartedAt = DateTime.Now;

        public RecordingSession(AppSettings settings, AudioEngine engine, string ffmpegPath)
        {
            _settings = settings.Clone();
            _engine = engine;
            _ffmpegPath = ffmpegPath;
            _audioHandler = OnAudioBlock;

            Folder = Path.Combine(_settings.OutputFolder, StartedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            string baseName = StartedAt.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture) + "_ВКС";
            BaseName = baseName;
            int n = 2;
            while (NameTaken(BaseName))
            {
                BaseName = baseName + "_" + n;
                n++;
            }
            Mp4Path = Path.Combine(Folder, BaseName + ".mp4");
        }

        private bool NameTaken(string name)
        {
            try
            {
                return Directory.Exists(Folder) && Directory.GetFiles(Folder, name + ".*").Length > 0
                       || Directory.Exists(Folder) && Directory.GetFiles(Folder, name + "_часть*").Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ===================== Состояние для интерфейса =====================

        public SessionState State
        {
            get { return _state; }
        }

        public TimeSpan Elapsed
        {
            get
            {
                RecordingSegment seg = _segment;
                double current = seg != null ? seg.Gate.ActiveSeconds(Clock.Now) : 0;
                return TimeSpan.FromSeconds(Volatile.Read(ref _completedActiveSeconds) + current);
            }
        }

        public long BytesWritten
        {
            get { return Interlocked.Read(ref _completedBytes) + Interlocked.Read(ref _currentBytes); }
        }

        public int PartCount
        {
            get { lock (_parts) return _parts.Count; }
        }

        public long FreeBytes
        {
            get { return Interlocked.Read(ref _freeBytes); }
        }

        /// <summary>На сколько ещё хватит места при текущем потоке данных.</summary>
        public TimeSpan? RemainingDiskTime
        {
            get
            {
                double rate = Math.Max(Volatile.Read(ref _bytesPerSecond), EstimatedBytesPerSecond * 0.5);
                long free = FreeBytes;
                if (free < 0 || rate <= 0) return null;
                return TimeSpan.FromSeconds(Math.Min(free / rate, 360000));
            }
        }

        public double EstimatedBytesPerSecond
        {
            get
            {
                VideoPump pump = _pump;
                if (pump == null) return 0;
                return RecordingArgs.EstimateBytesPerSecond(pump.Width, pump.Height, _settings.FrameRate, _settings.Quality);
            }
        }

        /// <summary>Запись действительно идёт: модуль записи работает и не падает раз за разом.</summary>
        public bool IsHealthy
        {
            get { return _segment != null && Volatile.Read(ref _quickFailures) == 0; }
        }

        public int Width
        {
            get { return _pump != null ? _pump.Width : 0; }
        }

        public int Height
        {
            get { return _pump != null ? _pump.Height : 0; }
        }

        public bool TryTakeNotice(out Notice notice)
        {
            return _notices.TryDequeue(out notice);
        }

        public List<string> Parts
        {
            get { lock (_parts) return _parts.ToList(); }
        }

        // ===================== Управление =====================

        public void Start()
        {
            Directory.CreateDirectory(Folder);
            Log.Info("=== Начало записи: " + Mp4Path);

            _pump = new VideoPump(_settings.MonitorDevice, _settings.FrameRate, _settings.ShowCursor);
            _pump.Start();

            _journal = new PendingJournal
            {
                Mp4Path = Mp4Path,
                KeepMkv = _settings.KeepMkvAfterConvert,
                Started = StartedAt
            };

            try
            {
                StartSegment();
            }
            catch (Exception)
            {
                _pump.Dispose();
                throw;
            }

            _engine.Subscribe(_audioHandler);
            _lastResumeTicks = Clock.Now;
            _state = SessionState.Recording;

            _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "Сторож записи" };
            _watchdog.Start();
        }

        public void Pause()
        {
            if (_state != SessionState.Recording) return;
            _paused = true;
            RecordingSegment seg = _segment;
            if (seg != null) seg.Gate.Close(Clock.Now);
            _state = SessionState.Paused;
            Log.Info("Пауза");
        }

        public void Resume()
        {
            if (_state != SessionState.Paused) return;
            _paused = false;
            _lastResumeTicks = Clock.Now;
            RecordingSegment seg = _segment;
            if (seg != null) seg.Gate.Open(Clock.Now);
            _state = SessionState.Recording;
            Log.Info("Продолжение после паузы");
        }

        /// <summary>Останавливает запись и дожидается, пока файл будет дописан. Вызывать из фонового потока.</summary>
        public List<string> Stop(bool fast)
        {
            if (_state == SessionState.Stopped) return Parts;
            _state = SessionState.Stopping;
            _stopRequested = true;
            if (_watchdog != null && !_watchdog.Join(fast ? 3000 : 20000)) Log.Warn("Сторож записи не остановился вовремя");

            RecordingSegment seg = _segment;
            long now = Clock.Now;
            if (seg != null)
            {
                AddActive(seg.Gate.ActiveSeconds(now));
                seg.Finish(now, fast);
                Interlocked.Add(ref _completedBytes, seg.FileSize);
                Interlocked.Exchange(ref _currentBytes, 0);
            }
            _segment = null;
            if (_pump != null)
            {
                _pump.SetTarget(null);
                _pump.Dispose();
            }
            _engine.Unsubscribe(_audioHandler);
            _state = SessionState.Stopped;
            Log.Info("=== Запись остановлена. Длительность " + Fmt.Timer(Elapsed) + ", частей: " + PartCount);
            return Parts;
        }

        // ===================== Внутреннее =====================

        private string PartPath(int index)
        {
            return Path.Combine(Folder, index == 1 ? BaseName + ".mkv" : BaseName + "_часть" + index + ".mkv");
        }

        private void StartSegment()
        {
            int index = ++_segmentIndex;
            var seg = new RecordingSegment(index, PartPath(index), _pump.Width * _pump.Height * 4);
            if (!_paused) seg.Gate.Open(Clock.Now);
            try
            {
                seg.Start(_ffmpegPath, _pump.Width, _pump.Height, _settings.FrameRate, _settings.Quality, "ВКС " + StartedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                seg.Abandon();
                throw;
            }
            lock (_parts)
            {
                _parts.Add(seg.FilePath);
                _journal.Parts = _parts.ToList();
            }
            _journal.Save();
            _segment = seg;
            _pump.SetTarget(seg);
        }

        private void OnAudioBlock(AudioBlock block)
        {
            RecordingSegment seg = _segment;
            if (seg != null) seg.OnAudioBlock(block, _settings.MixMic1, _settings.MixMic2, _settings.MixSystem);
        }

        private void AddActive(double seconds)
        {
            double current = Volatile.Read(ref _completedActiveSeconds);
            Volatile.Write(ref _completedActiveSeconds, current + seconds);
        }

        private void AddNotice(string text, NoticeLevel level)
        {
            _notices.Enqueue(new Notice(text, level));
            if (level >= NoticeLevel.Warning) Log.Warn(text);
            else Log.Info(text);
        }

        private void WatchdogLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    WatchdogTick();
                }
                catch (Exception ex)
                {
                    Log.Warn("Сбой сторожа записи", ex);
                }
                for (int i = 0; i < 5 && !_stopRequested; i++) Thread.Sleep(100);
            }
        }

        private void WatchdogTick()
        {
            long now = Clock.Now;
            RecordingSegment seg = _segment;

            if (seg == null)
            {
                if (now >= _nextRestartTicks) TryRestart(null);
                return;
            }
            if (_quickFailures > 0 && Clock.ToSeconds(now - seg.CreatedTicks) > 15)
            {
                Volatile.Write(ref _quickFailures, 0);
                AddNotice("Модуль записи снова работает, запись идёт.", NoticeLevel.Success);
            }

            Interlocked.Exchange(ref _currentBytes, seg.FileSize);

            string reason = null;
            if (seg.Failure != null) reason = seg.Failure;
            else if (seg.ProcessExited) reason = "модуль записи неожиданно завершился";
            else if (!_paused && seg.Gate.IsOpen
                     && Clock.ToSeconds(now - seg.LastProgressTicks) > StallSeconds
                     && Clock.ToSeconds(now - _lastResumeTicks) > StallSeconds
                     && Clock.ToSeconds(now - seg.CreatedTicks) > StallSeconds)
                reason = "модуль записи завис";

            if (reason != null)
            {
                string tail = seg.StderrTail;
                if (!string.IsNullOrEmpty(tail)) Log.Warn("Последние сообщения ffmpeg:" + Environment.NewLine + tail);
                bool quick = Clock.ToSeconds(now - seg.CreatedTicks) < 10;
                AddActive(seg.Gate.ActiveSeconds(now));
                seg.Gate.Close(now);
                _segment = null;
                _pump.SetTarget(null);
                seg.Abandon();
                Interlocked.Add(ref _completedBytes, seg.FileSize);
                Interlocked.Exchange(ref _currentBytes, 0);

                if (quick)
                {
                    // Падает сразу после запуска: не перезапускаем в цикле, а ждём всё дольше.
                    int failures = Interlocked.Increment(ref _quickFailures);
                    int delay = Math.Min(30, 3 * failures);
                    _nextRestartTicks = Clock.Now + Clock.Frequency * delay;
                    if (failures == 1 || failures % 5 == 0)
                    {
                        string last = LastLine(tail);
                        AddNotice("Модуль записи не запускается (" + reason + (last != null ? ": " + last : "") +
                                  "). Пробую снова через " + delay + " с. Если это повторяется, проверьте, не блокирует ли ffmpeg.exe антивирус.", NoticeLevel.Error);
                    }
                    return;
                }
                TryRestart(reason);
                return;
            }

            if (now - _lastFlushTicks > Clock.Frequency * 2)
            {
                _lastFlushTicks = now;
                seg.FlushToDisk();
            }

            CheckSpeed(seg, now);
            CheckDisk(now);

            string problem = _pump.TakeProblem();
            if (problem != null && problem != _pendingPumpProblem)
            {
                _pendingPumpProblem = problem;
                AddNotice(problem, problem.StartsWith("Экран снова", StringComparison.Ordinal) ? NoticeLevel.Info : NoticeLevel.Warning);
            }
        }

        private void TryRestart(string reason)
        {
            long now = Clock.Now;
            _restartTimes.Add(now);
            _restartTimes.RemoveAll(t => now - t > Clock.Frequency * 60);

            try
            {
                StartSegment();
                if (reason != null)
                    AddNotice("Сбой записи: " + reason + ". Запись продолжается в новый файл (часть " + _segmentIndex + "), после остановки части склеятся в один файл.", NoticeLevel.Warning);
            }
            catch (Exception ex)
            {
                int delay = _restartTimes.Count > 3 ? 10 : 3;
                _nextRestartTicks = now + Clock.Frequency * delay;
                AddNotice("Не получается возобновить запись (" + ex.Message + "). Пробую снова через " + delay + " с.", NoticeLevel.Error);
                Log.Warn("Ошибка перезапуска фрагмента", ex);
            }
        }

        private static string LastLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? null : lines[lines.Length - 1].Trim();
        }

        private void CheckSpeed(RecordingSegment seg, long now)
        {
            if (_paused || Clock.ToSeconds(now - seg.CreatedTicks) < 20) return;
            if (seg.Speed < 0.9)
            {
                if (_slowSinceTicks == 0) _slowSinceTicks = now;
                if (!_slowWarned && Clock.ToSeconds(now - _slowSinceTicks) > 30)
                {
                    _slowWarned = true;
                    AddNotice("Компьютер не успевает сжимать видео в реальном времени. Запись продолжается без потерь звука, но может отставать. Если это повторяется, уменьшите частоту кадров или качество в настройках.", NoticeLevel.Warning);
                }
            }
            else
            {
                _slowSinceTicks = 0;
            }
        }

        private void CheckDisk(long now)
        {
            if (now - _lastDiskCheck < Clock.Frequency * 5) return;
            _lastDiskCheck = now;

            long bytes = BytesWritten;
            if (_lastRateTicks == 0)
            {
                _lastRateTicks = now;
                _lastRateBytes = bytes;
            }
            else if (now - _lastRateTicks > Clock.Frequency * 30)
            {
                double rate = (bytes - _lastRateBytes) / Clock.ToSeconds(now - _lastRateTicks);
                double old = Volatile.Read(ref _bytesPerSecond);
                if (rate > 0) Volatile.Write(ref _bytesPerSecond, old <= 0 ? rate : old * 0.7 + rate * 0.3);
                _lastRateTicks = now;
                _lastRateBytes = bytes;
            }

            long free = DiskSpace.FreeBytes(Folder);
            Interlocked.Exchange(ref _freeBytes, free);
            if (free < 0) return;

            TimeSpan? left = RemainingDiskTime;
            int level = 0;
            if (free < 2L * 1024 * 1024 * 1024) level = 2;
            else if (left.HasValue && left.Value.TotalHours < 2) level = 1;

            if (level > _diskWarningLevel)
            {
                if (level == 2)
                    AddNotice("Место на диске почти закончилось: осталось " + Fmt.Size(free) + ". Освободите место, иначе запись остановится.", NoticeLevel.Error);
                else
                    AddNotice("Места на диске осталось примерно на " + Fmt.Duration(left.Value) + " записи (" + Fmt.Size(free) + ").", NoticeLevel.Warning);
            }
            _diskWarningLevel = level;
        }
    }

    internal static class DiskSpace
    {
        /// <summary>Свободно байт на диске с папкой; -1 если узнать не удалось.</summary>
        public static long FreeBytes(string folder)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root)) return -1;
                if (root.StartsWith(@"\\", StringComparison.Ordinal)) return -1;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
