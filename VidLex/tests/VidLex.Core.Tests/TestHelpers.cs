using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VidLex.Core.Abstractions;
using VidLex.Core.Models;

namespace VidLex.Core.Tests
{
    /// <summary>Временная папка, удаляемая после теста.</summary>
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vidlex-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

        public string CreateFile(string relative, string content = "x")
        {
            var full = Combine(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // Временные файлы теста — не критично.
            }
        }
    }

    /// <summary>Журнал в памяти для проверок.</summary>
    internal sealed class MemoryLog : ILogService
    {
        public List<LogEntry> Entries { get; } = new List<LogEntry>();
        public LogVerbosity Verbosity { get; set; } = LogVerbosity.Detailed;
        public string LogsDirectory => "";
        public event Action<LogEntry> EntryAdded;

        public void Log(LogLevel level, string message, string fileName = null, string stage = null, string details = null)
        {
            var e = new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message ?? "", FileName = fileName ?? "", Stage = stage ?? "", Details = details ?? "" };
            lock (Entries) Entries.Add(e);
            EntryAdded?.Invoke(e);
        }

        public void Debug(string message, string fileName = null, string stage = null, string details = null) => Log(LogLevel.Debug, message, fileName, stage, details);
        public void Info(string message, string fileName = null, string stage = null, string details = null) => Log(LogLevel.Info, message, fileName, stage, details);
        public void Warning(string message, string fileName = null, string stage = null, string details = null) => Log(LogLevel.Warning, message, fileName, stage, details);
        public void Error(string message, Exception exception = null, string fileName = null, string stage = null, string details = null)
            => Log(LogLevel.Error, message, fileName, stage, (details ?? "") + exception);

        public IReadOnlyList<LogEntry> GetRecent()
        {
            lock (Entries) return Entries.ToList();
        }

        public int DeleteOldLogs(int olderThanDays) => 0;
        public void Flush() { }

        public bool Has(LogLevel level, string fragment)
        {
            lock (Entries) return Entries.Any(e => e.Level == level && (e.Message + " " + e.Details).IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    internal static class Seg
    {
        /// <summary>Сегмент с равномерно распределёнными по времени словами.</summary>
        public static TranscriptSegment Make(double start, double end, string text)
        {
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var seg = new TranscriptSegment { Start = start, End = end, Text = text };
            double step = (end - start) / Math.Max(1, words.Length);
            for (int i = 0; i < words.Length; i++)
                seg.Words.Add(new TimedWord { Text = words[i], Start = start + i * step, End = start + (i + 1) * step - step * 0.1 });
            return seg;
        }

        public static string DictionaryDir => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dictionaries");
    }
}
