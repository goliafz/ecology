using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    public sealed class QueueRunSummary
    {
        public int Completed { get; set; }
        public int Skipped { get; set; }
        public int Errors { get; set; }
        public int Stopped { get; set; }
        public int NotStarted { get; set; }
        public TimeSpan Elapsed { get; set; }
        public bool WasStopped { get; set; }
    }

    /// <summary>
    /// Управляемая очередь: ограничение числа одновременных заданий, пауза, продолжение, остановка.
    /// Пауза настоящая: запущенные ffmpeg/whisper-cli замораживаются средствами ОС, новые этапы не начинаются.
    /// </summary>
    public sealed class QueueProcessingService
    {
        private readonly JobProcessor _processor;
        private readonly ProgressEstimationService _estimator;
        private readonly ILogService _log;
        private readonly ExternalProcessRegistry _registry;
        private readonly QueueStateStore _stateStore;
        private readonly ITranscriptExportService _export;
        private readonly object _sync = new object();
        private readonly Dictionary<VideoJob, LiveJobProgress> _live = new Dictionary<VideoJob, LiveJobProgress>();
        private CancellationTokenSource _cts;
        private QueueState _state = QueueState.Idle;
        private volatile bool _keepCheckpointsOnStop;

        public QueueProcessingService(JobProcessor processor, ProgressEstimationService estimator, ILogService log,
            ExternalProcessRegistry registry, QueueStateStore stateStore, ITranscriptExportService export)
        {
            _processor = processor;
            _estimator = estimator;
            _log = log;
            _registry = registry;
            _stateStore = stateStore;
            _export = export;
            Gate = new PauseGate();
            _processor.LiveProgress += (job, p) => { lock (_sync) _live[job] = p; };
            _registry.SuspendFailed += msg => _log.Warning(msg, stage: "Пауза");
        }

        public PauseGate Gate { get; }
        public DateTime? StartedAt { get; private set; }
        public int Parallelism { get; private set; } = 1;

        public QueueState State
        {
            get => _state;
            private set
            {
                if (_state == value) return;
                _state = value;
                StateChanged?.Invoke(value);
            }
        }

        public bool IsBusy => State != QueueState.Idle;

        public event Action<QueueState> StateChanged;
        public event Action<QueueRunSummary> Finished;
        public event Action<VideoJob> JobFinished;

        public IList<LiveJobProgress> GetLiveProgress()
        {
            lock (_sync) return _live.Values.ToList();
        }

        /// <summary>Запускает обработку заданий в статусах «Ожидает» и «Остановлено».</summary>
        public async Task<QueueRunSummary> RunAsync(IList<VideoJob> allJobs, AppSettings settings, ToolPaths tools, string sourceFolder)
        {
            if (IsBusy) throw new InvalidOperationException("Очередь уже выполняется");
            var toRun = allJobs.Where(j => j.Status == JobStatus.Waiting || j.Status == JobStatus.Stopped).ToList();
            var summary = new QueueRunSummary();
            if (toRun.Count == 0) return summary;

            _cts = new CancellationTokenSource();
            _keepCheckpointsOnStop = false;
            var ct = _cts.Token;
            Gate.Resume();
            _estimator.Reset();
            lock (_sync) _live.Clear();
            StartedAt = DateTime.Now;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            State = QueueState.Running;

            Parallelism = Math.Max(1, Math.Min(settings.MaxParallelJobs, toRun.Count));
            int threads = settings.Threads > 0
                ? settings.Threads
                : Math.Max(1, Math.Min(8, Environment.ProcessorCount / Parallelism));

            var ctx = new ProcessingContext
            {
                Settings = settings,
                Tools = tools,
                Gate = Gate,
                ThreadsPerJob = threads,
                KeepCheckpointsOnStop = () => _keepCheckpointsOnStop
            };

            _export.PlanOutputs(allJobs, settings);
            _log.Info($"Запуск очереди: видео к обработке {toRun.Count} из {allJobs.Count}; одновременно {Parallelism}; потоков на задание {threads}; " +
                      $"сохранение: {(settings.OutputMode == OutputMode.NextToVideo ? "рядом с видео" : "в папку " + settings.OutputFolder)}; " +
                      $"временные метки: {(settings.AddTimestamps ? "да" : "нет")}", stage: "Очередь");
            _log.Info($"Компоненты: ffmpeg «{tools.Ffmpeg}», whisper-cli «{tools.WhisperCli}», модель «{tools.Model}»", stage: "Очередь");
            _stateStore.Save(sourceFolder, allJobs, true);

            var pending = new Queue<VideoJob>(toRun);
            foreach (var j in toRun)
            {
                if (j.Status == JobStatus.Stopped) j.Reset();
            }

            async Task Worker()
            {
                while (true)
                {
                    try
                    {
                        await Gate.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    if (ct.IsCancellationRequested) return;
                    VideoJob job;
                    lock (pending)
                    {
                        if (pending.Count == 0) return;
                        job = pending.Dequeue();
                    }

                    JobStatistics stats;
                    try
                    {
                        stats = await _processor.ProcessAsync(job, ctx, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // JobProcessor сам обрабатывает ошибки; это последняя линия защиты.
                        job.Status = JobStatus.Error;
                        job.Error = "Непредвиденная ошибка: " + ex.Message;
                        _log.Error(job.Error, ex, job.FileName, "Задание");
                        stats = null;
                    }
                    lock (_sync) _live.Remove(job);
                    if (job.Status == JobStatus.Completed) _estimator.AddCompleted(stats);
                    _stateStore.Save(sourceFolder, allJobs, true);
                    JobFinished?.Invoke(job);
                }
            }

            var monitor = MonitorPauseAsync(ct);
            await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(_ => Task.Run(Worker))).ConfigureAwait(false);

            wall.Stop();
            bool stopped = ct.IsCancellationRequested;
            summary.WasStopped = stopped;
            summary.Elapsed = wall.Elapsed;
            summary.Completed = toRun.Count(j => j.Status == JobStatus.Completed);
            summary.Skipped = toRun.Count(j => j.Status == JobStatus.Skipped);
            summary.Errors = toRun.Count(j => j.Status == JobStatus.Error);
            summary.Stopped = toRun.Count(j => j.Status == JobStatus.Stopped);
            summary.NotStarted = toRun.Count(j => j.Status == JobStatus.Waiting);

            _stateStore.Save(sourceFolder, allJobs, false);
            _log.Info($"Очередь {(stopped ? "остановлена" : "завершена")} за {TimeFormat.Clock(summary.Elapsed)}: завершено {summary.Completed}, " +
                      $"пропущено {summary.Skipped}, ошибок {summary.Errors}, остановлено {summary.Stopped}, не начато {summary.NotStarted}", stage: "Очередь");

            Gate.Resume();
            _registry.ResumeAll();
            State = QueueState.Idle;
            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            var cts = _cts;
            _cts = null;
            cts.Dispose();
            Finished?.Invoke(summary);
            return summary;
        }

        public void Pause()
        {
            if (State != QueueState.Running) return;
            Gate.Pause();
            int active = _registry.ActiveCount;
            int suspended = _registry.SuspendAll();
            _estimator.ResetDisplay();
            if (suspended >= active)
            {
                State = QueueState.Paused;
                _log.Info(active > 0
                    ? $"Обработка приостановлена (заморожено процессов: {suspended})"
                    : "Обработка приостановлена", stage: "Очередь");
            }
            else
            {
                State = QueueState.PausePending;
                _log.Warning("Не все процессы удалось заморозить: пауза вступит в силу после завершения текущего этапа", stage: "Очередь");
            }
        }

        public void Resume()
        {
            if (State != QueueState.Paused && State != QueueState.PausePending) return;
            _registry.ResumeAll();
            Gate.Resume();
            _estimator.ResetDisplay();
            State = QueueState.Running;
            _log.Info("Обработка продолжена", stage: "Очередь");
        }

        /// <param name="keepCheckpoints">true — при закрытии приложения: сохранить прогресс длинных файлов для продолжения.</param>
        public void Stop(bool keepCheckpoints = false)
        {
            var cts = _cts;
            if (cts == null || State == QueueState.Idle) return;
            _keepCheckpointsOnStop = keepCheckpoints;
            State = QueueState.Stopping;
            _log.Info(keepCheckpoints ? "Остановка очереди (закрытие приложения)" : "Остановка очереди по команде пользователя", stage: "Очередь");
            _registry.ResumeAll();
            Gate.Resume();
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Пока пауза «ожидает», следим, когда активные процессы закончатся, и переводим в «Приостановлено».</summary>
        private async Task MonitorPauseAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && State != QueueState.Idle)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                if (State == QueueState.PausePending && _registry.ActiveCount == 0)
                {
                    State = QueueState.Paused;
                    _log.Info("Обработка приостановлена", stage: "Очередь");
                }
            }
        }
    }
}
