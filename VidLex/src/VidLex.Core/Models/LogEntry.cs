using System;
using System.Text;

namespace VidLex.Core.Models
{
    /// <summary>Запись журнала.</summary>
    public sealed class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        /// <summary>Имя видеофайла (может быть пустым).</summary>
        public string FileName { get; set; } = "";
        /// <summary>Этап обработки (может быть пустым).</summary>
        public string Stage { get; set; } = "";
        public string Message { get; set; } = "";
        /// <summary>Технические подробности ошибки (тип исключения, стек, вывод процесса).</summary>
        public string Details { get; set; } = "";

        public string LevelText => LevelName(Level);

        public static string LevelName(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DEBUG";
                case LogLevel.Info: return "INFO";
                case LogLevel.Warning: return "WARNING";
                case LogLevel.Error: return "ERROR";
                default: return level.ToString().ToUpperInvariant();
            }
        }

        /// <summary>Строка для файла журнала: поля разделены « | », подробности — с отступом на следующих строках.</summary>
        public string ToFileLine()
        {
            var sb = new StringBuilder();
            sb.Append(Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" | ").Append(LevelText.PadRight(7));
            sb.Append(" | ").Append(string.IsNullOrEmpty(Stage) ? "-" : Stage);
            sb.Append(" | ").Append(string.IsNullOrEmpty(FileName) ? "-" : FileName);
            sb.Append(" | ").Append(OneLine(Message));
            if (!string.IsNullOrWhiteSpace(Details))
            {
                foreach (var line in Details.Replace("\r\n", "\n").Split('\n'))
                    sb.Append(Environment.NewLine).Append("    ").Append(line);
            }
            return sb.ToString();
        }

        /// <summary>Полный текст записи для копирования в буфер обмена.</summary>
        public string ToClipboardText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Время: {Timestamp:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine($"Уровень: {LevelText}");
            if (!string.IsNullOrEmpty(FileName)) sb.AppendLine($"Файл: {FileName}");
            if (!string.IsNullOrEmpty(Stage)) sb.AppendLine($"Этап: {Stage}");
            sb.AppendLine($"Сообщение: {Message}");
            if (!string.IsNullOrWhiteSpace(Details))
            {
                sb.AppendLine("Подробности:");
                sb.AppendLine(Details);
            }
            return sb.ToString();
        }

        private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");
    }
}
