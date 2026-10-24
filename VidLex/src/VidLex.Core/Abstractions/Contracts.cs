using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VidLex.Core.Models;

namespace VidLex.Core.Abstractions
{
    /// <summary>Журналирование: файл с ротацией + буфер последних записей для интерфейса.</summary>
    public interface ILogService
    {
        LogVerbosity Verbosity { get; set; }
        string LogsDirectory { get; }
        event Action<LogEntry> EntryAdded;

        void Log(LogLevel level, string message, string fileName = null, string stage = null, string details = null);
        void Debug(string message, string fileName = null, string stage = null, string details = null);
        void Info(string message, string fileName = null, string stage = null, string details = null);
        void Warning(string message, string fileName = null, string stage = null, string details = null);
        void Error(string message, Exception exception = null, string fileName = null, string stage = null, string details = null);

        IReadOnlyList<LogEntry> GetRecent();
        /// <summary>Удаляет файлы журнала старше указанного числа дней (кроме текущего). Возвращает число удалённых.</summary>
        int DeleteOldLogs(int olderThanDays);
        void Flush();
    }

    /// <summary>Загрузка и сохранение настроек.</summary>
    public interface ISettingsService
    {
        AppSettings Current { get; }
        event Action<AppSettings> SettingsChanged;
        AppSettings Load();
        void Save(AppSettings settings);
    }

    public sealed class DiscoveryResult
    {
        public List<string> Files { get; } = new List<string>();
        public int SkippedUnsupported { get; set; }
        public int InaccessibleFolders { get; set; }
        public int FoldersScanned { get; set; }
    }

    /// <summary>Рекурсивный поиск видео.</summary>
    public interface IVideoDiscoveryService
    {
        Task<DiscoveryResult> ScanAsync(string rootFolder, IEnumerable<string> extensions, IProgress<int> foundSoFar, CancellationToken ct);
    }

    /// <summary>Чтение параметров медиафайла (ffprobe).</summary>
    public interface IMediaProbeService
    {
        Task<MediaInfo> ProbeAsync(string path, CancellationToken ct);
    }

    /// <summary>Извлечение и нарезка аудио (ffmpeg).</summary>
    public interface IAudioExtractionService
    {
        /// <summary>Извлекает дорожку в WAV 16 кГц моно PCM16. progress — доля 0…1 по времени.</summary>
        Task ExtractAsync(string inputPath, int audioIndex, string outputWav, TimeSpan? duration, IProgress<double> progress, CancellationToken ct);

        /// <summary>Вырезает фрагмент [start, start+length) из WAV без перекодирования.</summary>
        Task ExtractChunkAsync(string sourceWav, double start, double length, string outputWav, CancellationToken ct);
    }

    /// <summary>Поиск пауз (ffmpeg silencedetect).</summary>
    public interface ISilenceDetectionService
    {
        Task<List<TimeRange>> DetectAsync(string wavPath, double thresholdDb, double minSilenceSeconds, double totalSeconds, CancellationToken ct);
    }

    public sealed class ChunkTranscriptionRequest
    {
        public string WorkingDirectory { get; set; }
        /// <summary>Имя WAV-файла фрагмента внутри WorkingDirectory (только латиница).</summary>
        public string ChunkFileName { get; set; }
        public string ModelPath { get; set; }
        public string Language { get; set; } = "ru";
        public string Prompt { get; set; } = "";
        public int Threads { get; set; }
        public int BeamSize { get; set; } = 5;
        public bool UseGpu { get; set; } = true;
        /// <summary>false — передать -mc 0 (не переносить контекст между окнами).</summary>
        public bool CarryContext { get; set; }
        public WorkerPriority Priority { get; set; }
        /// <summary>Смещение фрагмента в исходном видео — прибавляется ко всем временам.</summary>
        public double OffsetSeconds { get; set; }
        public double ChunkLengthSeconds { get; set; }
        public int ChunkIndex { get; set; }
    }

    public struct ChunkProgress
    {
        /// <summary>Доля фрагмента 0…1 по данным whisper.cpp.</summary>
        public double Fraction;
        /// <summary>Время в аудио (сек от начала фрагмента), до которого уже получен текст.</summary>
        public double ConfirmedSeconds;
    }

    /// <summary>Распознавание одного фрагмента аудио движком.</summary>
    public interface ITranscriptionEngine
    {
        string EngineName { get; }
        Task<List<TranscriptSegment>> TranscribeChunkAsync(ChunkTranscriptionRequest request, IProgress<ChunkProgress> progress, CancellationToken ct);
    }

    public sealed class TextProcessingOptions
    {
        public SpellCheckMode SpellCheckMode { get; set; } = SpellCheckMode.SafeFix;
        public bool EnableSentenceSplitting { get; set; } = true;
        public int SentencePauseMs { get; set; } = 700;
        public bool RemoveNonSpeechAnnotations { get; set; } = true;
        public bool FilterHallucinations { get; set; } = true;
        public ICollection<string> CustomWords { get; set; } = new List<string>();

        public static TextProcessingOptions From(AppSettings s) => new TextProcessingOptions
        {
            SpellCheckMode = s.SpellCheckMode,
            EnableSentenceSplitting = s.EnableSentenceSplitting,
            SentencePauseMs = s.SentencePauseMs,
            RemoveNonSpeechAnnotations = s.RemoveNonSpeechAnnotations,
            FilterHallucinations = s.FilterHallucinations,
            CustomWords = s.CustomWords
        };
    }

    /// <summary>Постобработка распознанного текста.</summary>
    public interface ITextCorrectionService
    {
        ProcessedTranscript Process(IList<TranscriptSegment> segments, IList<TimeRange> silences, TextProcessingOptions options, string fileNameForLog = null);
    }

    /// <summary>Решение о том, куда и нужно ли сохранять TXT.</summary>
    public sealed class OutputDecision
    {
        public string TargetPath { get; set; }
        /// <summary>true — результат уже есть и настройки велят пропустить видео.</summary>
        public bool Skip { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>Формирование и сохранение TXT.</summary>
    public interface ITranscriptExportService
    {
        /// <summary>Заранее рассчитывает имена TXT для всей очереди (защита от коллизий имён).</summary>
        void PlanOutputs(IList<VideoJob> jobs, AppSettings settings);
        OutputDecision Decide(VideoJob job, AppSettings settings);
        string Render(ProcessedTranscript transcript, AppSettings settings);
        /// <summary>Атомарно записывает файл. Возвращает фактический путь.</summary>
        string Save(VideoJob job, string targetPath, string content, AppSettings settings);
        /// <summary>Проверяет, что в папку можно писать. null — можно, иначе текст ошибки.</summary>
        string CheckWritable(string folder);
    }
}
