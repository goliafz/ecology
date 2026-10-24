using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace VidLex.Core.Models
{
    /// <summary>
    /// Единый объект настроек приложения. Хранится в %LocalAppData%\VidLex\settings.json.
    /// Значения по умолчанию заданы прямо в инициализаторах свойств.
    /// </summary>
    public sealed class AppSettings
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        // ---------- Папки ----------

        /// <summary>Последняя выбранная исходная папка.</summary>
        public string LastSourceFolder { get; set; } = "";

        // ---------- Сохранение результатов ----------

        [JsonConverter(typeof(StringEnumConverter))]
        public OutputMode OutputMode { get; set; } = OutputMode.NextToVideo;

        /// <summary>Папка результатов для режима «Все результаты в отдельной папке».</summary>
        public string OutputFolder { get; set; } = "";

        [JsonConverter(typeof(StringEnumConverter))]
        public ExistingResultPolicy ExistingResultPolicy { get; set; } = ExistingResultPolicy.Skip;

        [JsonConverter(typeof(StringEnumConverter))]
        public EmptyResultBehavior EmptyResultBehavior { get; set; } = EmptyResultBehavior.WriteMarker;

        /// <summary>UTF-8 с BOM — для корректного открытия в старых версиях «Блокнота».</summary>
        public bool WriteUtf8Bom { get; set; } = true;

        // ---------- Оформление расшифровки ----------

        /// <summary>Добавлять временные метки. По умолчанию выключено.</summary>
        public bool AddTimestamps { get; set; } = false;

        /// <summary>
        /// Период отображения временных меток, секунды. 0 — метка перед каждой фразой.
        /// N &gt; 0 — фразы объединяются в абзацы, новая метка ставится перед первой фразой,
        /// которая начинается не раньше чем через N секунд после предыдущей метки.
        /// Не влияет на то, как движок режет аудио (см. <see cref="ChunkLengthSeconds"/>).
        /// </summary>
        public int TimestampIntervalSeconds { get; set; } = 0;

        /// <summary>Разбивать текст без меток на абзацы по длинным паузам.</summary>
        public bool SplitIntoParagraphs { get; set; } = true;

        /// <summary>Пауза между фразами (сек), после которой начинается новый абзац.</summary>
        public double ParagraphPauseSeconds { get; set; } = 3.0;

        // ---------- Распознавание ----------

        /// <summary>Путь к ffmpeg.exe. Пусто — искать в папке tools приложения и в PATH.</summary>
        public string FfmpegPath { get; set; } = "";

        /// <summary>Путь к ffprobe.exe. Пусто — рядом с ffmpeg, в папке tools и в PATH.</summary>
        public string FfprobePath { get; set; } = "";

        /// <summary>Путь к whisper-cli.exe. Пусто — искать в папке tools приложения и в PATH.</summary>
        public string WhisperCliPath { get; set; } = "";

        /// <summary>Путь к модели ggml (*.bin). Пусто — первая модель в папке Models.</summary>
        public string ModelPath { get; set; } = "";

        /// <summary>Язык распознавания. Задаётся явно: «ru».</summary>
        public string Language { get; set; } = "ru";

        /// <summary>Номер аудиодорожки (0 — первая). -1 — автоматически: русская, затем дорожка по умолчанию, затем первая.</summary>
        public int AudioTrackIndex { get; set; } = -1;

        /// <summary>Целевая длительность фрагмента для распознавания длинных записей, секунды.</summary>
        public int ChunkLengthSeconds { get; set; } = 600;

        /// <summary>Использовать GPU, если сборка whisper.cpp его поддерживает.</summary>
        public bool UseGpu { get; set; } = true;

        /// <summary>Число потоков whisper.cpp. 0 — автоматически.</summary>
        public int Threads { get; set; } = 0;

        /// <summary>Ширина лучевого поиска (beam size). 5 — значение whisper.cpp по умолчанию.</summary>
        public int BeamSize { get; set; } = 5;

        /// <summary>
        /// Передавать конец предыдущего фрагмента как подсказку. По умолчанию выключено: в замерах
        /// (docs/TESTING.md) подсказка-«хвост» провоцировала зацикливание модели и пропуск фраз.
        /// </summary>
        public bool UsePreviousTextAsPrompt { get; set; } = false;

        /// <summary>
        /// Переносить текстовый контекст между 30-секундными окнами внутри фрагмента (whisper.cpp -mc).
        /// По умолчанию выключено (-mc 0): меньше повторов и «галлюцинаций», ниже WER в замерах.
        /// </summary>
        public bool CarryContextBetweenWindows { get; set; } = false;

        /// <summary>Передавать пользовательские термины как подсказку движку.</summary>
        public bool UseCustomWordsAsPrompt { get; set; } = true;

        /// <summary>Порог тишины для детектора пауз FFmpeg, дБ.</summary>
        public double SilenceThresholdDb { get; set; } = -35;

        /// <summary>Минимальная длительность паузы, которая считается тишиной, секунды.</summary>
        public double MinSilenceSeconds { get; set; } = 0.5;

        // ---------- Производительность ----------

        /// <summary>Сколько видео обрабатывать одновременно. По умолчанию 1 (последовательно).</summary>
        public int MaxParallelJobs { get; set; } = 1;

        [JsonConverter(typeof(StringEnumConverter))]
        public WorkerPriority WorkerPriority { get; set; } = WorkerPriority.BelowNormal;

        /// <summary>Сохранять контрольные точки длинных файлов, чтобы продолжить после сбоя.</summary>
        public bool KeepCheckpoints { get; set; } = true;

        // ---------- Обработка текста ----------

        [JsonConverter(typeof(StringEnumConverter))]
        public SpellCheckMode SpellCheckMode { get; set; } = SpellCheckMode.SafeFix;

        /// <summary>Разделять слитные фразы на предложения по паузам и словам-связкам.</summary>
        public bool EnableSentenceSplitting { get; set; } = true;

        /// <summary>Пауза (мс) между словами, которая считается границей фразы.</summary>
        public int SentencePauseMs { get; set; } = 700;

        /// <summary>Удалять пометки неречевых звуков вида [музыка], [аплодисменты].</summary>
        public bool RemoveNonSpeechAnnotations { get; set; } = true;

        /// <summary>Отбрасывать типичные «галлюцинации» Whisper на тишине и зацикленные повторы.</summary>
        public bool FilterHallucinations { get; set; } = true;

        /// <summary>Пользовательский словарь: имена, термины, названия — не исправляются.</summary>
        public List<string> CustomWords { get; set; } = new List<string>();

        // ---------- Журнал ----------

        [JsonConverter(typeof(StringEnumConverter))]
        public LogVerbosity LogVerbosity { get; set; } = LogVerbosity.Normal;

        /// <summary>Максимальный размер одного файла журнала, МБ.</summary>
        public int MaxLogFileSizeMb { get; set; } = 10;

        /// <summary>Журналы старше этого числа дней можно удалить кнопкой «Очистить старые».</summary>
        public int LogRetentionDays { get; set; } = 30;

        // ---------- Прочее ----------

        /// <summary>Расширения видео, которые ищутся при сканировании.</summary>
        public List<string> VideoExtensions { get; set; } = DefaultVideoExtensions.ToList();

        public static readonly string[] DefaultVideoExtensions =
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mpeg", ".mpg", ".m4v", ".flv", ".ts", ".3gp"
        };

        public AppSettings Clone()
        {
            var json = JsonConvert.SerializeObject(this, Infrastructure.JsonDefaults.Settings);
            return JsonConvert.DeserializeObject<AppSettings>(json, Infrastructure.JsonDefaults.Settings);
        }

        /// <summary>
        /// Приводит значения к допустимым диапазонам. Возвращает список исправлений для журнала.
        /// </summary>
        public IList<string> Validate()
        {
            var fixes = new List<string>();

            int ClampInt(string name, int value, int min, int max, int fallback)
            {
                if (value < min || value > max)
                {
                    fixes.Add($"{name}: значение {value} вне диапазона {min}…{max}, установлено {fallback}");
                    return fallback;
                }
                return value;
            }

            double ClampDouble(string name, double value, double min, double max, double fallback)
            {
                if (double.IsNaN(value) || value < min || value > max)
                {
                    fixes.Add($"{name}: значение {value} вне диапазона {min}…{max}, установлено {fallback}");
                    return fallback;
                }
                return value;
            }

            T CheckEnum<T>(string name, T value, T fallback) where T : struct
            {
                if (!Enum.IsDefined(typeof(T), value))
                {
                    fixes.Add($"{name}: недопустимое значение, установлено {fallback}");
                    return fallback;
                }
                return value;
            }

            if (SchemaVersion <= 0) SchemaVersion = CurrentSchemaVersion;
            LastSourceFolder = LastSourceFolder ?? "";
            OutputFolder = OutputFolder ?? "";
            FfmpegPath = (FfmpegPath ?? "").Trim();
            FfprobePath = (FfprobePath ?? "").Trim();
            WhisperCliPath = (WhisperCliPath ?? "").Trim();
            ModelPath = (ModelPath ?? "").Trim();

            OutputMode = CheckEnum(nameof(OutputMode), OutputMode, OutputMode.NextToVideo);
            ExistingResultPolicy = CheckEnum(nameof(ExistingResultPolicy), ExistingResultPolicy, ExistingResultPolicy.Skip);
            EmptyResultBehavior = CheckEnum(nameof(EmptyResultBehavior), EmptyResultBehavior, EmptyResultBehavior.WriteMarker);
            SpellCheckMode = CheckEnum(nameof(SpellCheckMode), SpellCheckMode, SpellCheckMode.SafeFix);
            LogVerbosity = CheckEnum(nameof(LogVerbosity), LogVerbosity, LogVerbosity.Normal);
            WorkerPriority = CheckEnum(nameof(WorkerPriority), WorkerPriority, WorkerPriority.BelowNormal);

            if (string.IsNullOrWhiteSpace(Language) || !string.Equals(Language.Trim(), "ru", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(Language, "ru", StringComparison.Ordinal))
                    fixes.Add($"Language: «{Language}» заменён на «ru» — VidLex рассчитан на русскую речь");
                Language = "ru";
            }

            TimestampIntervalSeconds = ClampInt(nameof(TimestampIntervalSeconds), TimestampIntervalSeconds, 0, 3600, 0);
            ParagraphPauseSeconds = ClampDouble(nameof(ParagraphPauseSeconds), ParagraphPauseSeconds, 0.5, 60, 3.0);
            AudioTrackIndex = ClampInt(nameof(AudioTrackIndex), AudioTrackIndex, -1, 63, -1);
            ChunkLengthSeconds = ClampInt(nameof(ChunkLengthSeconds), ChunkLengthSeconds, 60, 3600, 600);
            Threads = ClampInt(nameof(Threads), Threads, 0, 64, 0);
            BeamSize = ClampInt(nameof(BeamSize), BeamSize, 1, 10, 5);
            SilenceThresholdDb = ClampDouble(nameof(SilenceThresholdDb), SilenceThresholdDb, -80, -10, -35);
            MinSilenceSeconds = ClampDouble(nameof(MinSilenceSeconds), MinSilenceSeconds, 0.1, 10, 0.5);
            MaxParallelJobs = ClampInt(nameof(MaxParallelJobs), MaxParallelJobs, 1, 4, 1);
            SentencePauseMs = ClampInt(nameof(SentencePauseMs), SentencePauseMs, 200, 5000, 700);
            MaxLogFileSizeMb = ClampInt(nameof(MaxLogFileSizeMb), MaxLogFileSizeMb, 1, 500, 10);
            LogRetentionDays = ClampInt(nameof(LogRetentionDays), LogRetentionDays, 1, 3650, 30);

            CustomWords = (CustomWords ?? new List<string>())
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => w.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var exts = (VideoExtensions ?? new List<string>())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim().ToLowerInvariant())
                .Select(e => e.StartsWith(".", StringComparison.Ordinal) ? e : "." + e)
                .Distinct()
                .ToList();
            if (exts.Count == 0)
            {
                fixes.Add("VideoExtensions: список пуст, восстановлен стандартный набор");
                exts = DefaultVideoExtensions.ToList();
            }
            VideoExtensions = exts;

            return fixes;
        }
    }
}
