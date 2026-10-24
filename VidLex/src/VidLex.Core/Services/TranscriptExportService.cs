using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>
    /// Формирование текста TXT и выбор пути сохранения.
    ///
    /// Режим «Рядом с видео»: D:\Курс\Урок 01.mp4 → D:\Курс\Урок 01.txt. Если в одной папке есть
    /// «Урок 01.mp4» и «Урок 01.mkv», второй получает имя «Урок 01 [mkv].txt».
    ///
    /// Режим «Отдельная папка»: все TXT в корне папки результатов, без подпапок. При одинаковых именах
    /// к имени добавляется относительный путь: «Урок 01 (Модуль 2).txt». Принадлежность файлов видео
    /// хранится в скрытом манифесте папки результатов, поэтому чужие файлы никогда не перезаписываются.
    /// </summary>
    public sealed class TranscriptExportService : ITranscriptExportService
    {
        public const string ManifestFileName = ".vidlex-manifest.json";
        public const string NoSpeechMarker = "[Речь не обнаружена]";
        private const int MaxBaseNameLength = 180;

        private readonly ILogService _log;
        private readonly object _sync = new object();
        private readonly Dictionary<string, string> _planned = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Пути, уже выданные заданиям текущей очереди (защита от гонок при параллельной обработке).</summary>
        private readonly HashSet<string> _reserved = new HashSet<string>(PathUtil.PathComparer);

        public TranscriptExportService(ILogService log)
        {
            _log = log;
        }

        // ---------- Планирование имён ----------

        public void PlanOutputs(IList<VideoJob> jobs, AppSettings settings)
        {
            lock (_sync)
            {
                _planned.Clear();
                _reserved.Clear();
                if (settings.OutputMode == OutputMode.NextToVideo)
                    PlanNextToVideo(jobs);
                else
                    PlanSingleFolder(jobs, settings.OutputFolder);
            }
        }

        private void PlanNextToVideo(IList<VideoJob> jobs)
        {
            var groups = jobs.GroupBy(j => Path.Combine(Path.GetDirectoryName(j.FullPath) ?? "", Path.GetFileNameWithoutExtension(j.FullPath)), PathUtil.PathComparer);
            foreach (var g in groups)
            {
                var ordered = g.OrderBy(j => j.FullPath, NaturalStringComparer.Instance).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var job = ordered[i];
                    var dir = Path.GetDirectoryName(job.FullPath) ?? "";
                    var baseName = Path.GetFileNameWithoutExtension(job.FullPath);
                    var name = i == 0 ? baseName : $"{baseName} [{Path.GetExtension(job.FullPath).TrimStart('.').ToLowerInvariant()}]";
                    _planned[job.Id] = Path.Combine(dir, Truncate(name) + ".txt");
                }
            }
        }

        private void PlanSingleFolder(IList<VideoJob> jobs, string outputFolder)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byName = jobs.GroupBy(j => Path.GetFileNameWithoutExtension(j.FullPath), StringComparer.OrdinalIgnoreCase);
            foreach (var g in byName.OrderBy(g => g.Key, NaturalStringComparer.Instance))
            {
                var ordered = g.OrderBy(j => j.RelativePath, NaturalStringComparer.Instance).ToList();
                foreach (var job in ordered)
                {
                    var baseName = Path.GetFileNameWithoutExtension(job.FullPath);
                    string name;
                    if (ordered.Count == 1)
                    {
                        name = baseName;
                    }
                    else
                    {
                        var relDir = Path.GetDirectoryName(job.RelativePath) ?? "";
                        var parts = new List<string>();
                        if (relDir.Length > 0)
                            parts.Add(relDir.Replace(Path.DirectorySeparatorChar, '-').Replace(Path.AltDirectorySeparatorChar, '-').Replace("-", " - "));
                        bool sameDirDup = ordered.Count(o => string.Equals(Path.GetDirectoryName(o.RelativePath) ?? "", relDir, StringComparison.OrdinalIgnoreCase)) > 1;
                        if (sameDirDup) parts.Add(Path.GetExtension(job.FullPath).TrimStart('.').ToLowerInvariant());
                        name = parts.Count == 0 ? baseName : $"{baseName} ({string.Join(", ", parts)})";
                    }
                    name = PathUtil.SanitizeFileName(Truncate(name));
                    var unique = name;
                    for (int n = 2; used.Contains(unique); n++) unique = $"{name} ({n})";
                    used.Add(unique);
                    _planned[job.Id] = Path.Combine(outputFolder ?? "", unique + ".txt");
                }
            }
        }

        private static string Truncate(string name) => name.Length <= MaxBaseNameLength ? name : name.Substring(0, MaxBaseNameLength).TrimEnd();

        public string GetPlannedPath(VideoJob job, AppSettings settings)
        {
            lock (_sync)
            {
                if (_planned.TryGetValue(job.Id, out var p)) return p;
            }
            // Задание не было в плане (например, добавлено позже) — простое имя.
            var baseName = Path.GetFileNameWithoutExtension(job.FullPath) + ".txt";
            return settings.OutputMode == OutputMode.NextToVideo
                ? Path.Combine(Path.GetDirectoryName(job.FullPath) ?? "", baseName)
                : Path.Combine(settings.OutputFolder ?? "", baseName);
        }

        // ---------- Решение о сохранении ----------

        public OutputDecision Decide(VideoJob job, AppSettings settings)
        {
            lock (_sync)
            {
                var planned = GetPlannedPath(job, settings);
                string existingOwn = null;

                if (settings.OutputMode == OutputMode.SingleFolder)
                {
                    var manifest = LoadManifest(settings.OutputFolder);
                    // Ранее сохранённый результат именно этого видео (под любым именем).
                    var own = manifest.Where(kv => PathUtil.PathsEqual(kv.Value, job.FullPath))
                        .Select(kv => Path.Combine(settings.OutputFolder, kv.Key))
                        .FirstOrDefault(File.Exists);
                    if (own != null) existingOwn = own;
                    else if (File.Exists(planned))
                    {
                        // Файл с таким именем есть, но создан не для этого видео — не трогаем его.
                        var foreign = planned;
                        planned = MakeUnique(planned);
                        _log?.Warning($"В папке результатов уже есть «{Path.GetFileName(foreign)}», созданный не для этого видео. Результат будет сохранён как «{Path.GetFileName(planned)}»",
                            job.FileName, "Сохранение");
                    }
                }
                else if (File.Exists(planned))
                {
                    existingOwn = planned;
                }

                if (existingOwn != null)
                {
                    switch (settings.ExistingResultPolicy)
                    {
                        case ExistingResultPolicy.Skip:
                            return new OutputDecision { TargetPath = existingOwn, Skip = true, Reason = $"Результат уже существует: {Path.GetFileName(existingOwn)}" };
                        case ExistingResultPolicy.Overwrite:
                            _reserved.Add(existingOwn);
                            return new OutputDecision { TargetPath = existingOwn, Reason = "Существующий результат будет перезаписан" };
                        case ExistingResultPolicy.CreateNew:
                            var fresh = MakeUnique(existingOwn);
                            _reserved.Add(fresh);
                            return new OutputDecision { TargetPath = fresh, Reason = $"Результат уже есть — новый будет сохранён как {Path.GetFileName(fresh)}" };
                    }
                }

                if (_reserved.Contains(planned)) planned = MakeUnique(planned);
                _reserved.Add(planned);
                return new OutputDecision { TargetPath = planned };
            }
        }

        private string MakeUnique(string path)
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int n = 2; n < 10000; n++)
            {
                var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
                if (!File.Exists(candidate) && !_reserved.Contains(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{name} ({Guid.NewGuid().ToString("N").Substring(0, 6)}){ext}");
        }

        // ---------- Текст ----------

        public string Render(ProcessedTranscript transcript, AppSettings settings)
        {
            if (transcript == null || transcript.IsEmpty)
                return settings.EmptyResultBehavior == EmptyResultBehavior.WriteMarker ? NoSpeechMarker + "\r\n" : "";

            var sb = new StringBuilder();
            var sentences = transcript.Sentences;

            if (settings.AddTimestamps && settings.TimestampIntervalSeconds <= 0)
            {
                foreach (var s in sentences)
                    sb.Append('[').Append(TimeFormat.Clock(Math.Floor(s.Start))).Append("] ").Append(s.Text).Append("\r\n");
                return sb.ToString();
            }

            if (settings.AddTimestamps)
            {
                double lastMark = double.NegativeInfinity;
                var paragraph = new StringBuilder();
                foreach (var s in sentences)
                {
                    if (s.Start - lastMark >= settings.TimestampIntervalSeconds)
                    {
                        FlushParagraph(sb, paragraph);
                        paragraph.Append('[').Append(TimeFormat.Clock(Math.Floor(s.Start))).Append("] ").Append(s.Text);
                        lastMark = s.Start;
                    }
                    else
                    {
                        paragraph.Append(' ').Append(s.Text);
                    }
                }
                FlushParagraph(sb, paragraph);
                return sb.ToString();
            }

            // Без меток: сплошной текст, абзацы — по длинным паузам.
            var para = new StringBuilder();
            TranscriptSentence prev = null;
            foreach (var s in sentences)
            {
                bool newParagraph = settings.SplitIntoParagraphs && prev != null &&
                    (s.Start - prev.End >= settings.ParagraphPauseSeconds || para.Length > 1200);
                if (newParagraph) FlushParagraph(sb, para);
                if (para.Length > 0) para.Append(' ');
                para.Append(s.Text);
                prev = s;
            }
            FlushParagraph(sb, para);
            return sb.ToString();
        }

        private static void FlushParagraph(StringBuilder target, StringBuilder paragraph)
        {
            if (paragraph.Length == 0) return;
            if (target.Length > 0) target.Append("\r\n");
            target.Append(paragraph).Append("\r\n");
            paragraph.Clear();
        }

        // ---------- Запись ----------

        public string Save(VideoJob job, string targetPath, string content, AppSettings settings)
        {
            var encoding = new UTF8Encoding(settings.WriteUtf8Bom);
            AtomicFile.WriteAllText(targetPath, content, encoding);
            if (settings.OutputMode == OutputMode.SingleFolder)
            {
                lock (_sync)
                {
                    try
                    {
                        var manifest = LoadManifest(settings.OutputFolder);
                        manifest[Path.GetFileName(targetPath)] = job.FullPath;
                        SaveManifest(settings.OutputFolder, manifest);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning("Не удалось обновить манифест папки результатов", job.FileName, "Сохранение", ex.Message);
                    }
                }
            }
            return targetPath;
        }

        public string CheckWritable(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "Папка результатов не выбрана.";
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ".vidlex-write-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(probe, "test");
                File.Delete(probe);
                return null;
            }
            catch (Exception ex)
            {
                return $"Нет доступа на запись в папку «{folder}»: {ex.Message}";
            }
        }

        private static Dictionary<string, string> LoadManifest(string folder)
        {
            try
            {
                var path = Path.Combine(folder ?? "", ManifestFileName);
                if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8));
                return new Dictionary<string, string>(data ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveManifest(string folder, Dictionary<string, string> manifest)
        {
            var path = Path.Combine(folder, ManifestFileName);
            // Снимаем «скрытый» перед заменой: File.Replace на скрытом файле может завершиться ошибкой доступа.
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            AtomicFile.WriteAllText(path, JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));
            try
            {
                File.SetAttributes(path, FileAttributes.Hidden);
            }
            catch
            {
                // Атрибут «скрытый» — косметика.
            }
        }
    }
}
