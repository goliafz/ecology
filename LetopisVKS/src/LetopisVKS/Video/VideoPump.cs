using System;
using System.Threading;
using LetopisVks.Core;

namespace LetopisVks.Video
{
    /// <summary>Буфер кадра со счётчиком ссылок: один и тот же кадр можно отправить несколько раз без копирования.</summary>
    internal sealed class FrameBuffer
    {
        public readonly byte[] Data;
        private int _refs;

        public FrameBuffer(int size)
        {
            Data = new byte[size];
        }

        public bool IsFree
        {
            get { return Volatile.Read(ref _refs) == 0; }
        }

        public void AddRef()
        {
            Interlocked.Increment(ref _refs);
        }

        public void Release()
        {
            Interlocked.Decrement(ref _refs);
        }
    }

    /// <summary>Куда видеонасос складывает кадры (реализует фрагмент записи).</summary>
    internal interface IVideoTarget
    {
        ActivityGate Gate { get; }
        bool AcceptsVideo { get; }
        long VideoFramesQueued { get; }
        bool EnqueueVideo(FrameBuffer frame);
    }

    /// <summary>
    /// Отдельный поток, который снимает экран с постоянной частотой кадров.
    /// Номер кадра строго привязан ко времени записи: если компьютер не успел
    /// снять новый кадр, повторяется предыдущий. Поэтому видео никогда не
    /// «уезжает» относительно звука.
    /// </summary>
    internal sealed class VideoPump : IDisposable
    {
        private const int PoolSize = 4;

        private readonly string _device;
        private readonly bool _cursor;
        private readonly int _fps;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private Thread _thread;
        private volatile bool _stop;
        private volatile IVideoTarget _target;
        private Exception _initError;
        private FrameBuffer[] _pool;

        private long _captured;
        private long _failed;
        private double _avgCaptureMs;
        private string _problem;
        private long _lastErrorLog;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public long CapturedFrames
        {
            get { return Interlocked.Read(ref _captured); }
        }

        public long FailedCaptures
        {
            get { return Interlocked.Read(ref _failed); }
        }

        public double AverageCaptureMs
        {
            get { return Volatile.Read(ref _avgCaptureMs); }
        }

        /// <summary>Последнее событие с экраном (смена разрешения, недоступность). Забирается вызывающим.</summary>
        public string TakeProblem()
        {
            return Interlocked.Exchange(ref _problem, null);
        }

        public VideoPump(string device, int fps, bool cursor)
        {
            _device = device;
            _fps = fps;
            _cursor = cursor;
        }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Захват экрана", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            if (!_ready.WaitOne(15000)) throw new TimeoutException("Захват экрана не запустился за 15 секунд.");
            if (_initError != null) throw new InvalidOperationException("Не удалось начать захват экрана: " + _initError.Message, _initError);
        }

        public void SetTarget(IVideoTarget target)
        {
            _target = target;
        }

        private void Loop()
        {
            try
            {
                Native.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch (Exception)
            {
            }

            ScreenCapturer capturer;
            try
            {
                capturer = new ScreenCapturer(_device, _cursor);
                Width = capturer.Width;
                Height = capturer.Height;
                _pool = new FrameBuffer[PoolSize];
                for (int i = 0; i < PoolSize; i++) _pool[i] = new FrameBuffer(capturer.FrameBytes);
            }
            catch (Exception ex)
            {
                _initError = ex;
                _ready.Set();
                return;
            }
            Log.Info("Захват экрана: " + capturer.SourceDevice + ", " + Width + "×" + Height + ", " + _fps + " кадров/с");
            _ready.Set();

            FrameBuffer last = null;
            bool lastCaptureFailed = false;
            try
            {
                while (!_stop)
                {
                    try
                    {
                        IVideoTarget target = _target;
                        if (target == null || !target.AcceptsVideo)
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        long now = Clock.Now;
                        long due = FramesDue(target.Gate, now);
                        long queued = target.VideoFramesQueued;

                        if (queued < due)
                        {
                            FrameBuffer free = null;
                            for (int i = 0; i < _pool.Length; i++)
                            {
                                if (_pool[i].IsFree)
                                {
                                    free = _pool[i];
                                    break;
                                }
                            }

                            if (free != null)
                            {
                                long t0 = Clock.Now;
                                bool ok;
                                try
                                {
                                    ok = capturer.Capture(free.Data);
                                }
                                catch (Exception ex)
                                {
                                    ok = false;
                                    LogThrottled("Ошибка захвата кадра", ex);
                                }
                                double ms = Clock.ToSeconds(Clock.Now - t0) * 1000;
                                Volatile.Write(ref _avgCaptureMs, _avgCaptureMs <= 0 ? ms : _avgCaptureMs * 0.95 + ms * 0.05);

                                if (ok)
                                {
                                    free.AddRef();
                                    if (last != null) last.Release();
                                    last = free;
                                    Interlocked.Increment(ref _captured);
                                    if (lastCaptureFailed)
                                    {
                                        lastCaptureFailed = false;
                                        Interlocked.Exchange(ref _problem, "Экран снова доступен, запись картинки продолжается.");
                                    }
                                }
                                else
                                {
                                    Interlocked.Increment(ref _failed);
                                    if (!lastCaptureFailed)
                                    {
                                        lastCaptureFailed = true;
                                        Interlocked.Exchange(ref _problem, "Экран временно недоступен (например, открыто окно контроля учётных записей). Звук пишется, картинка застыла на последнем кадре.");
                                    }
                                }

                                string notice = capturer.PendingNotice;
                                if (notice != null)
                                {
                                    capturer.PendingNotice = null;
                                    Log.Info(notice);
                                    Interlocked.Exchange(ref _problem, notice);
                                }
                            }

                            if (last == null)
                            {
                                // Самый первый кадр не получился — начинаем с чёрного, чтобы не терять синхронизацию.
                                last = _pool[0];
                                last.AddRef();
                            }

                            while (queued < due)
                            {
                                last.AddRef();
                                if (!target.EnqueueVideo(last))
                                {
                                    last.Release();
                                    break;
                                }
                                queued++;
                            }
                        }

                        int sleepMs = 10;
                        if (target.Gate.IsOpen)
                        {
                            double active = target.Gate.ActiveSeconds(Clock.Now);
                            double nextAt = target.VideoFramesQueued / (double)_fps;
                            sleepMs = (int)Math.Ceiling((nextAt - active) * 1000.0);
                        }
                        Thread.Sleep(Math.Max(1, Math.Min(sleepMs, 20)));
                    }
                    catch (Exception ex)
                    {
                        LogThrottled("Сбой в потоке захвата экрана", ex);
                        Thread.Sleep(50);
                    }
                }
            }
            finally
            {
                if (last != null) last.Release();
                capturer.Dispose();
            }
        }

        /// <summary>Сколько кадров должно быть отправлено к этому моменту: кадр k соответствует времени k/fps.</summary>
        private long FramesDue(ActivityGate gate, long now)
        {
            if (!gate.EverOpened) return 0;
            double active = gate.ActiveSeconds(now);
            return (long)Math.Floor(active * _fps) + 1;
        }

        private void LogThrottled(string message, Exception ex)
        {
            long now = Clock.Now;
            if (now - _lastErrorLog < Clock.Frequency * 10) return;
            _lastErrorLog = now;
            Log.Warn(message, ex);
        }

        public void Dispose()
        {
            _stop = true;
            _target = null;
            if (_thread != null && !_thread.Join(5000)) Log.Warn("Поток захвата экрана не остановился вовремя");
        }
    }
}
