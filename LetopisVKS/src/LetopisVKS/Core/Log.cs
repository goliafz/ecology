using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace LetopisVks.Core
{
    /// <summary>
    /// Журнал работы. Пишется в %LOCALAPPDATA%\LetopisVKS\logs, по файлу на день.
    /// Любая ошибка внутри журнала проглатывается: журнал не должен ронять запись.
    /// </summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static StreamWriter _writer;
        private static string _currentDate;
        private static string _directory;

        public static string Directory
        {
            get { return _directory; }
        }

        public static string CurrentFile
        {
            get { return _directory == null ? null : Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"); }
        }

        public static void Init()
        {
            try
            {
                _directory = AppPaths.LogDirectory;
                CleanupOld();
            }
            catch
            {
                _directory = null;
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public static void Warn(string message, Exception ex)
        {
            Write("WARN", message, ex);
        }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", message, ex);
        }

        public static void Ffmpeg(string line)
        {
            Write("FFMPEG", line, null);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                lock (Sync)
                {
                    if (_directory == null) return;
                    DateTime now = DateTime.Now;
                    string date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (_writer == null || date != _currentDate)
                    {
                        if (_writer != null) _writer.Dispose();
                        _writer = new StreamWriter(new FileStream(Path.Combine(_directory, date + ".log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
                        _writer.AutoFlush = true;
                        _currentDate = date;
                    }
                    var sb = new StringBuilder();
                    sb.Append(now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(" [").Append(level).Append("] ").Append(message);
                    if (ex != null) sb.Append(Environment.NewLine).Append("    ").Append(ex);
                    _writer.WriteLine(sb.ToString());
                }
            }
            catch
            {
                // журнал не должен мешать работе
            }
        }

        private static void CleanupOld()
        {
            try
            {
                foreach (string file in System.IO.Directory.GetFiles(_directory, "*.log"))
                {
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-60)) File.Delete(file);
                }
            }
            catch
            {
            }
        }
    }
}
