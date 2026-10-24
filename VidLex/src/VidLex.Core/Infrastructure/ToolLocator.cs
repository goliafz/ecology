using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VidLex.Core.Models;

namespace VidLex.Core.Infrastructure
{
    /// <summary>Найденные пути к внешним компонентам.</summary>
    public sealed class ToolPaths
    {
        public string Ffmpeg { get; set; }
        public string Ffprobe { get; set; }
        public string WhisperCli { get; set; }
        public string Model { get; set; }

        /// <summary>Список проблем на русском языке; пусто — всё найдено.</summary>
        public List<string> Problems { get; } = new List<string>();

        public bool IsComplete => Problems.Count == 0;
    }

    /// <summary>
    /// Ищет ffmpeg, ffprobe, whisper-cli и модель: сначала явный путь из настроек,
    /// затем папка tools рядом с приложением, затем %LocalAppData%\VidLex, затем PATH.
    /// </summary>
    public sealed class ToolLocator
    {
        private readonly AppPaths _paths;

        public ToolLocator(AppPaths paths)
        {
            _paths = paths;
        }

        private static string Exe(string name) => ProcessControl.IsWindows ? name + ".exe" : name;

        public ToolPaths Locate(AppSettings s)
        {
            var result = new ToolPaths();

            result.Ffmpeg = Resolve(s.FfmpegPath, Exe("ffmpeg"), new[] { "ffmpeg", Path.Combine("ffmpeg", "bin") });
            var ffmpegDir = result.Ffmpeg != null ? Path.GetDirectoryName(result.Ffmpeg) : null;
            result.Ffprobe = Resolve(s.FfprobePath, Exe("ffprobe"), new[] { "ffmpeg", Path.Combine("ffmpeg", "bin") }, ffmpegDir);
            result.WhisperCli = Resolve(s.WhisperCliPath, Exe("whisper-cli"), new[] { "whisper", Path.Combine("whisper", "Release") });
            result.Model = ResolveModel(s.ModelPath);

            if (result.Ffmpeg == null)
                result.Problems.Add(Missing("FFmpeg (ffmpeg.exe)", s.FfmpegPath));
            if (result.Ffprobe == null)
                result.Problems.Add(Missing("FFprobe (ffprobe.exe)", s.FfprobePath));
            if (result.WhisperCli == null)
                result.Problems.Add(Missing("whisper.cpp (whisper-cli.exe)", s.WhisperCliPath));
            if (result.Model == null)
            {
                result.Problems.Add(string.IsNullOrWhiteSpace(s.ModelPath)
                    ? $"Модель распознавания не найдена. Укажите файл ggml-*.bin в настройках или поместите его в «{_paths.ModelsDirectory}»."
                    : $"Файл модели не найден: «{s.ModelPath}».");
            }
            else
            {
                var problem = ValidateModelFile(result.Model);
                if (problem != null) result.Problems.Add(problem);
            }
            return result;
        }

        private static string Missing(string what, string configured)
        {
            return string.IsNullOrWhiteSpace(configured)
                ? $"Не найден {what}. Запустите scripts\\Install-Dependencies.ps1 или укажите путь в настройках."
                : $"Не найден {what} по указанному пути: «{configured}».";
        }

        /// <summary>Проверка сигнатуры файла модели ggml (magic 0x67676d6c).</summary>
        public static string ValidateModelFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length < 1024 * 1024)
                    return $"Файл модели «{info.Name}» слишком мал ({info.Length} байт) — вероятно, загрузка не завершилась.";
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buf = new byte[4];
                    if (fs.Read(buf, 0, 4) != 4 || BitConverter.ToUInt32(buf, 0) != 0x67676d6c)
                        return $"Файл «{info.Name}» не является моделью whisper.cpp в формате ggml.";
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"Не удалось прочитать файл модели «{path}»: {ex.Message}";
            }
        }

        private string Resolve(string configured, string exeName, IEnumerable<string> toolSubdirs, string extraDir = null)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? Path.GetFullPath(configured) : null;

            var candidates = new List<string>();
            if (extraDir != null) candidates.Add(Path.Combine(extraDir, exeName));
            foreach (var sub in toolSubdirs)
            {
                candidates.Add(Path.Combine(_paths.ToolsDirectory, sub, exeName));
                candidates.Add(Path.Combine(_paths.DataRoot, "tools", sub, exeName));
            }
            candidates.Add(Path.Combine(_paths.ToolsDirectory, exeName));
            candidates.Add(Path.Combine(_paths.AppDirectory, exeName));

            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null) return Path.GetFullPath(found);
            return FindInPath(exeName);
        }

        public static string FindInPath(string exeName)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Некорректный элемент PATH — пропускаем.
                }
            }
            return null;
        }

        private string ResolveModel(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? Path.GetFullPath(configured) : null;

            foreach (var dir in new[] { _paths.ModelsDirectory, Path.Combine(_paths.AppDirectory, "models"), Path.Combine(_paths.ToolsDirectory, "models") })
            {
                if (!Directory.Exists(dir)) continue;
                var models = Directory.GetFiles(dir, "ggml-*.bin")
                    .Where(f => !Path.GetFileName(f).StartsWith("ggml-silero", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(ModelRank)
                    .ToList();
                if (models.Count > 0) return models[0];
            }
            return null;
        }

        /// <summary>Чем «качественнее» модель, тем выше ранг — при нескольких моделях берётся лучшая.</summary>
        private static int ModelRank(string path)
        {
            var n = Path.GetFileName(path).ToLowerInvariant();
            if (n.Contains(".en")) return 0;
            if (n.Contains("large-v3-turbo")) return 60;
            if (n.Contains("large")) return 50;
            if (n.Contains("medium")) return 40;
            if (n.Contains("small")) return 30;
            if (n.Contains("base")) return 20;
            if (n.Contains("tiny")) return 10;
            return 5;
        }
    }
}
