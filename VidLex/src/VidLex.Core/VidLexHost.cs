using System;
using System.IO;
using System.Linq;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;
using VidLex.Core.Services;

namespace VidLex.Core
{
    /// <summary>
    /// Корень композиции: создаёт сервисы и связывает их через конструкторы (внедрение зависимостей).
    /// Используется и WPF-приложением, и консольной утилитой VidLex.Cli.
    /// </summary>
    public sealed class VidLexHost : IDisposable
    {
        private ToolPaths _tools = new ToolPaths();

        public VidLexHost(AppPaths paths, string dictionaryDirectory)
        {
            Paths = paths;
            Paths.EnsureCreated();

            var logger = new LoggingService(paths.LogsDirectory);
            Log = logger;
            Settings = new SettingsService(paths.SettingsFile, Log);
            var settings = Settings.Load();
            logger.Verbosity = settings.LogVerbosity;
            logger.MaxFileSizeMb = settings.MaxLogFileSizeMb;
            Settings.SettingsChanged += s =>
            {
                logger.Verbosity = s.LogVerbosity;
                logger.MaxFileSizeMb = s.MaxLogFileSizeMb;
            };

            Registry = new ExternalProcessRegistry();
            Runner = new ProcessRunner(Registry);
            Locator = new ToolLocator(paths);
            Func<ToolPaths> tools = () => _tools;
            Func<WorkerPriority> priority = () => Settings.Current.WorkerPriority;

            Discovery = new VideoDiscoveryService(Log);
            Probe = new MediaProbeService(Runner, tools);
            Audio = new AudioExtractionService(Runner, tools, priority);
            Silence = new SilenceDetectionService(Runner, tools, priority);
            Engine = new WhisperCppEngine(Runner, tools, Log);
            Text = new TextCorrectionService(Log, dictionaryDirectory);
            Export = new TranscriptExportService(Log);
            Estimator = new ProgressEstimationService();
            StateStore = new QueueStateStore(paths.QueueStateFile);
            Processor = new JobProcessor(Probe, Audio, Silence, Engine, Text, Export, Log, paths);
            Queue = new QueueProcessingService(Processor, Estimator, Log, Registry, StateStore, Export);
        }

        public AppPaths Paths { get; }
        public ILogService Log { get; }
        public ISettingsService Settings { get; }
        public ExternalProcessRegistry Registry { get; }
        public IProcessRunner Runner { get; }
        public ToolLocator Locator { get; }
        public IVideoDiscoveryService Discovery { get; }
        public IMediaProbeService Probe { get; }
        public IAudioExtractionService Audio { get; }
        public ISilenceDetectionService Silence { get; }
        public ITranscriptionEngine Engine { get; }
        public TextCorrectionService Text { get; }
        public ITranscriptExportService Export { get; }
        public ProgressEstimationService Estimator { get; }
        public QueueStateStore StateStore { get; }
        public JobProcessor Processor { get; }
        public QueueProcessingService Queue { get; }

        /// <summary>Пути к инструментам для текущего запуска.</summary>
        public ToolPaths Tools => _tools;

        /// <summary>Повторно ищет ffmpeg, whisper-cli и модель (по текущим или переданным настройкам).</summary>
        public ToolPaths RefreshTools(AppSettings settings = null)
        {
            _tools = Locator.Locate(settings ?? Settings.Current);
            return _tools;
        }

        /// <summary>
        /// Удаляет временные папки заданий. Папки с контрольными точками сохраняются, если
        /// keepCheckpoints = true (пользователь согласился восстановить прерванную очередь).
        /// </summary>
        public int CleanupTemp(bool keepCheckpoints)
        {
            int removed = 0;
            try
            {
                foreach (var dir in Directory.GetDirectories(Paths.TempDirectory, "job-*"))
                {
                    bool hasCheckpoint = File.Exists(Path.Combine(dir, "checkpoint.json"));
                    bool stale = Directory.GetLastWriteTime(dir) < DateTime.Now.AddDays(-7);
                    if (keepCheckpoints && hasCheckpoint && !stale) continue;
                    JobProcessor.TryDeleteDirectory(dir);
                    if (!Directory.Exists(dir)) removed++;
                }
                // Временные файлы атомарной записи, оставшиеся после сбоя.
                foreach (var f in Directory.GetFiles(Paths.DataRoot, "*.vidlex-*.tmp", SearchOption.AllDirectories))
                    AtomicFile.TryDelete(f);
            }
            catch (Exception ex)
            {
                Log.Warning("Не удалось очистить временные файлы", stage: "Запуск", details: ex.Message);
            }
            if (removed > 0) Log.Info($"Удалено временных папок от прошлых сеансов: {removed}", stage: "Запуск");
            return removed;
        }

        public static string AppVersion =>
            typeof(VidLexHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        public void Dispose()
        {
            (Log as IDisposable)?.Dispose();
        }
    }
}
