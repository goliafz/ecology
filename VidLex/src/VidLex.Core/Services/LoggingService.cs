using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VidLex.Core.Abstractions;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>
    /// Журнал VidLex. Файлы: Logs\VidLex_ГГГГ-ММ-ДД.log, при превышении размера — VidLex_ГГГГ-ММ-ДД_1.log и т. д.
    /// Запись выполняется фоновым потоком, чтобы не тормозить обработку и интерфейс.
    /// Содержимое расшифровок в журнал не пишется.
    /// </summary>
    public sealed class LoggingService : ILogService, IDisposable
    {
        private const int MemoryCapacity = 5000;
        private const long TotalSizeCapBytes = 500L * 1024 * 1024;

        private readonly BlockingCollection<LogEntry> _queue = new BlockingCollection<LogEntry>(new ConcurrentQueue<LogEntry>());
        private readonly LinkedList<LogEntry> _recent = new LinkedList<LogEntry>();
        private readonly object _recentSync = new object();
        private readonly Thread _writer;
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);
        private readonly Func<DateTime> _clock;
        private long _maxFileBytes;
        private int _pending;
        private bool _disposed;

        public LoggingService(string logsDirectory, int maxFileSizeMb = 10, Func<DateTime> clock = null)
        {
            LogsDirectory = logsDirectory;
            _clock = clock ?? (() => DateTime.Now);
            MaxFileSizeMb = maxFileSizeMb;
            Directory.CreateDirectory(logsDirectory);
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "VidLex log writer" };
            _writer.Start();
        }

        public string LogsDirectory { get; }
        public LogVerbosity Verbosity { get; set; } = LogVerbosity.Normal;

        public int MaxFileSizeMb
        {
            get => (int)(Interlocked.Read(ref _maxFileBytes) / (1024 * 1024));
            set => Interlocked.Exchange(ref _maxFileBytes, Math.Max(1, value) * 1024L * 1024L);
        }

        public event Action<LogEntry> EntryAdded;

        public void Log(LogLevel level, string message, string fileName = null, string stage = null, string details = null)
        {
            if (level == LogLevel.Debug && Verbosity != LogVerbosity.Detailed) return;
            var entry = new LogEntry
            {
                Timestamp = _clock(),
                Level = level,
                FileName = fileName ?? "",
                Stage = stage ?? "",
                Message = message ?? "",
                Details = details ?? ""
            };

            lock (_recentSync)
            {
                _recent.AddLast(entry);
                while (_recent.Count > MemoryCapacity) _recent.RemoveFirst();
            }

            if (!_disposed)
            {
                Interlocked.Increment(ref _pending);
                _idle.Reset();
                try
                {
                    _queue.Add(entry);
                }
                catch (InvalidOperationException)
                {
                    // Журнал уже закрыт (завершение приложения).
                    Interlocked.Decrement(ref _pending);
                }
            }

            try
            {
                EntryAdded?.Invoke(entry);
            }
            catch
            {
                // Подписчик интерфейса не должен ломать журналирование.
            }
        }

        public void Debug(string message, string fileName = null, string stage = null, string details = null)
            => Log(LogLevel.Debug, message, fileName, stage, details);

        public void Info(string message, string fileName = null, string stage = null, string details = null)
            => Log(LogLevel.Info, message, fileName, stage, details);

        public void Warning(string message, string fileName = null, string stage = null, string details = null)
            => Log(LogLevel.Warning, message, fileName, stage, details);

        public void Error(string message, Exception exception = null, string fileName = null, string stage = null, string details = null)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(details)) sb.AppendLine(details.TrimEnd());
            if (exception != null) sb.Append(exception);
            Log(LogLevel.Error, message, fileName, stage, sb.ToString().TrimEnd());
        }

        public IReadOnlyList<LogEntry> GetRecent()
        {
            lock (_recentSync) return _recent.ToList();
        }

        public void Flush()
        {
            if (_disposed) return;
            _idle.Wait(TimeSpan.FromSeconds(5));
        }

        public int DeleteOldLogs(int olderThanDays)
        {
            Flush();
            var border = _clock().Date.AddDays(-Math.Max(0, olderThanDays));
            var current = CurrentFilePrefix();
            int deleted = 0;
            foreach (var file in Directory.GetFiles(LogsDirectory, "VidLex_*.log"))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(current, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.GetLastWriteTime(file) < border)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (IOException)
                {
                    // Файл занят — пропускаем.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return deleted;
        }

        private string CurrentFilePrefix() => "VidLex_" + _clock().ToString("yyyy-MM-dd");

        /// <summary>Текущий файл журнала с учётом ограничения размера.</summary>
        public string GetCurrentLogFile()
        {
            var prefix = CurrentFilePrefix();
            var path = Path.Combine(LogsDirectory, prefix + ".log");
            int n = 0;
            while (File.Exists(path) && new FileInfo(path).Length >= Interlocked.Read(ref _maxFileBytes))
            {
                n++;
                path = Path.Combine(LogsDirectory, $"{prefix}_{n}.log");
            }
            return path;
        }

        private void WriterLoop()
        {
            var utf8 = new UTF8Encoding(false);
            var batch = new List<LogEntry>();
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                batch.Clear();
                batch.Add(first);
                while (batch.Count < 500 && _queue.TryTake(out var more)) batch.Add(more);
                try
                {
                    // Пишем порциями до ~256 КБ, проверяя размер файла перед каждой: так ротация
                    // срабатывает и при всплеске записей.
                    var sb = new StringBuilder();
                    foreach (var e in batch)
                    {
                        sb.AppendLine(e.ToFileLine());
                        if (sb.Length >= 128 * 1024)
                        {
                            File.AppendAllText(GetCurrentLogFile(), sb.ToString(), utf8);
                            sb.Clear();
                        }
                    }
                    if (sb.Length > 0) File.AppendAllText(GetCurrentLogFile(), sb.ToString(), utf8);
                }
                catch
                {
                    // Диск недоступен — запись теряется, но работа приложения продолжается;
                    // записи остаются в памяти и видны на странице «Журнал».
                }
                if (Interlocked.Add(ref _pending, -batch.Count) <= 0)
                {
                    _idle.Set();
                    EnforceTotalSizeCap();
                }
            }
            _idle.Set();
        }

        private void EnforceTotalSizeCap()
        {
            try
            {
                var files = new DirectoryInfo(LogsDirectory).GetFiles("VidLex_*.log").OrderBy(f => f.LastWriteTimeUtc).ToList();
                long total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total <= TotalSizeCapBytes || files.Count <= 1) break;
                    total -= f.Length;
                    f.Delete();
                }
            }
            catch
            {
                // Очистка — дело второстепенное.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            _writer.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }
}
