using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace VidLex.Core.Infrastructure
{
    /// <summary>
    /// Каталоги приложения. Данные пользователя — в %LocalAppData%\VidLex.
    /// Переменная окружения VIDLEX_DATA_DIR переопределяет корень (используется тестами).
    /// </summary>
    public sealed class AppPaths
    {
        public AppPaths(string dataRoot = null, string appDirectory = null)
        {
            var env = Environment.GetEnvironmentVariable("VIDLEX_DATA_DIR");
            // Пути всегда абсолютные: внешние утилиты запускаются с другой рабочей папкой.
            DataRoot = Path.GetFullPath(!string.IsNullOrWhiteSpace(dataRoot) ? dataRoot
                : !string.IsNullOrWhiteSpace(env) ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VidLex"));
            AppDirectory = Path.GetFullPath(appDirectory ?? AppDomain.CurrentDomain.BaseDirectory);
        }

        public string DataRoot { get; }
        /// <summary>Папка с exe-файлом приложения.</summary>
        public string AppDirectory { get; }

        public string SettingsFile => Path.Combine(DataRoot, "settings.json");
        public string LogsDirectory => Path.Combine(DataRoot, "Logs");
        public string TempDirectory => Path.Combine(DataRoot, "Temp");
        public string StateDirectory => Path.Combine(DataRoot, "State");
        public string QueueStateFile => Path.Combine(StateDirectory, "queue-state.json");
        public string StatsFile => Path.Combine(StateDirectory, "performance.json");
        /// <summary>Модели, скачанные скриптом установки.</summary>
        public string ModelsDirectory => Path.Combine(DataRoot, "Models");
        /// <summary>Резервные копии расшифровок, которые не удалось сохранить в целевую папку.</summary>
        public string RecoveredDirectory => Path.Combine(DataRoot, "Recovered");
        /// <summary>Папка внешних инструментов рядом с приложением: tools\ffmpeg, tools\whisper.</summary>
        public string ToolsDirectory => Path.Combine(AppDirectory, "tools");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(TempDirectory);
            Directory.CreateDirectory(StateDirectory);
        }
    }

    public static class JsonDefaults
    {
        /// <summary>
        /// Replace — чтобы списки из файла заменяли значения по умолчанию, а не дописывались к ним.
        /// </summary>
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };
    }

    /// <summary>Безопасная запись файла: временный файл + замена.</summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string content, Encoding encoding)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".vidlex-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, encoding))
                {
                    sw.Write(content);
                    sw.Flush();
                    fs.Flush(true);
                }
                ReplaceOrMove(tmp, path);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>Перемещает tmp на место target (атомарно на одном томе NTFS).</summary>
        public static void ReplaceOrMove(string tmp, string target)
        {
            if (File.Exists(target))
            {
                try
                {
                    File.Replace(tmp, target, null, true);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (IOException)
                {
                    // File.Replace не работает на некоторых сетевых и FAT-дисках — запасной путь ниже.
                }
                File.Copy(tmp, target, true);
                File.Delete(tmp);
            }
            else
            {
                File.Move(tmp, target);
            }
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Временный файл будет удалён при следующей очистке.
            }
        }
    }
}
