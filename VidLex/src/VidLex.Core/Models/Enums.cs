namespace VidLex.Core.Models
{
    /// <summary>Статус задания обработки одного видео.</summary>
    public enum JobStatus
    {
        Waiting,
        PreparingAudio,
        Recognizing,
        ProcessingText,
        Saving,
        Completed,
        Skipped,
        Error,
        Stopped
    }

    /// <summary>Куда сохранять TXT.</summary>
    public enum OutputMode
    {
        /// <summary>Рядом с исходным видео (по умолчанию).</summary>
        NextToVideo,
        /// <summary>Все результаты в одной папке, без подпапок.</summary>
        SingleFolder
    }

    /// <summary>Что делать, если TXT для видео уже существует.</summary>
    public enum ExistingResultPolicy
    {
        /// <summary>Пропустить видео (статус «Пропущено»).</summary>
        Skip,
        /// <summary>Распознать заново и перезаписать результат этого же видео.</summary>
        Overwrite,
        /// <summary>Распознать заново и сохранить под новым именем с суффиксом (2), (3)…</summary>
        CreateNew
    }

    /// <summary>Что сохранять, если в видео не обнаружена речь.</summary>
    public enum EmptyResultBehavior
    {
        /// <summary>Создать TXT с пометкой «[Речь не обнаружена]».</summary>
        WriteMarker,
        /// <summary>Создать пустой TXT.</summary>
        WriteEmptyFile,
        /// <summary>Не создавать файл.</summary>
        DoNotCreate
    }

    /// <summary>Режим проверки орфографии.</summary>
    public enum SpellCheckMode
    {
        Off,
        /// <summary>Только записывать неизвестные слова в журнал (уровень «Подробно»).</summary>
        ReportOnly,
        /// <summary>Исправлять только однозначные опечатки; неизвестные слова не трогать.</summary>
        SafeFix
    }

    /// <summary>Уровень подробности журнала.</summary>
    public enum LogVerbosity
    {
        Normal,
        Detailed
    }

    /// <summary>Уровень важности записи журнала.</summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>Приоритет внешних процессов (FFmpeg, whisper.cpp).</summary>
    public enum WorkerPriority
    {
        Idle,
        BelowNormal,
        Normal
    }

    /// <summary>Состояние очереди.</summary>
    public enum QueueState
    {
        Idle,
        Running,
        PausePending,
        Paused,
        Stopping
    }
}
