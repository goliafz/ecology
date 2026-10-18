using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using MasterVks.Core;

namespace MasterVks.Recording
{
    /// <summary>
    /// Запись в журнале незавершённых записей. Создаётся при старте записи и удаляется,
    /// только когда готов проверенный MP4. Если программа или Windows упадут,
    /// при следующем запуске запись будет доделана автоматически.
    /// </summary>
    public sealed class PendingJournal
    {
        public string Id { get; set; }
        public string Mp4Path { get; set; }
        public List<string> Parts { get; set; }
        public bool KeepMkv { get; set; }
        public DateTime Started { get; set; }

        public PendingJournal()
        {
            Id = Guid.NewGuid().ToString("N");
            Parts = new List<string>();
        }

        private string FilePath
        {
            get { return Path.Combine(AppPaths.PendingDirectory, Id + ".xml"); }
        }

        public void Save()
        {
            try
            {
                string tmp = FilePath + ".tmp";
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new XmlSerializer(typeof(PendingJournal)).Serialize(stream, this);
                    stream.Flush(true);
                }
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось сохранить журнал записи", ex);
            }
        }

        public void Delete()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось удалить журнал записи", ex);
            }
        }

        public static List<PendingJournal> LoadAll()
        {
            var result = new List<PendingJournal>();
            try
            {
                foreach (string file in Directory.GetFiles(AppPaths.PendingDirectory, "*.xml"))
                {
                    try
                    {
                        using (var stream = File.OpenRead(file))
                        {
                            var j = (PendingJournal)new XmlSerializer(typeof(PendingJournal)).Deserialize(stream);
                            j.Id = Path.GetFileNameWithoutExtension(file);
                            result.Add(j);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Повреждён журнал записи " + file, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось прочитать журналы записей", ex);
            }
            return result;
        }
    }

    internal sealed class FinalizeResult
    {
        public bool Success;
        public string Mp4Path;
        public string Message;
        public bool MkvKept;
    }

    /// <summary>Превращает MKV (одну или несколько частей) в MP4 без пересжатия и проверяет результат.</summary>
    internal static class PostProcessor
    {
        private static readonly object Gate = new object();

        public static FinalizeResult Finalize(string ffmpegPath, PendingJournal job)
        {
            lock (Gate)
            {
                try
                {
                    return FinalizeCore(ffmpegPath, job);
                }
                catch (Exception ex)
                {
                    Log.Error("Ошибка при создании MP4", ex);
                    return new FinalizeResult { Success = false, MkvKept = true, Message = "Не удалось сделать MP4: " + ex.Message + ". Исходная запись (MKV) сохранена и открывается в VLC." };
                }
            }
        }

        private static FinalizeResult FinalizeCore(string ffmpegPath, PendingJournal job)
        {
            List<string> parts = job.Parts.Where(p =>
            {
                try
                {
                    return File.Exists(p) && new FileInfo(p).Length > 1024;
                }
                catch (Exception)
                {
                    return false;
                }
            }).ToList();

            if (parts.Count == 0)
            {
                job.Delete();
                return new FinalizeResult { Success = false, Message = "Файл записи не найден или пуст, сохранять нечего." };
            }
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                return new FinalizeResult { Success = false, MkvKept = true, Message = "Не найден модуль FFmpeg, поэтому MP4 пока не сделан. Запись сохранена в MKV: " + parts[0] };
            }

            string target = job.Mp4Path;
            string dir = Path.GetDirectoryName(target);
            string baseName = Path.GetFileNameWithoutExtension(target);
            int n = 2;
            while (File.Exists(target))
            {
                target = Path.Combine(dir, baseName + "_" + n + ".mp4");
                n++;
            }
            string tmp = target + ".tmp";
            string listFile = null;
            if (parts.Count > 1)
            {
                listFile = Path.Combine(dir, baseName + "_список_частей.txt");
                var sb = new StringBuilder();
                foreach (string p in parts) sb.Append("file '").Append(p.Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
                File.WriteAllText(listFile, sb.ToString(), new UTF8Encoding(false));
            }

            Log.Info("Создаю MP4: " + target + " из " + parts.Count + " ч.");
            Ffmpeg.RunResult run = Ffmpeg.Run(ffmpegPath, RecordingArgs.BuildRemux(parts, listFile, tmp), 4 * 3600 * 1000, line => Log.Ffmpeg("[mp4] " + line));
            bool tmpOk = File.Exists(tmp) && new FileInfo(tmp).Length > 0;
            if (run.ExitCode != 0 || !tmpOk)
            {
                TryDelete(tmp);
                TryDelete(listFile);
                Log.Warn("MP4 не создан, код " + run.ExitCode + Environment.NewLine + run.Error);
                return new FinalizeResult { Success = false, MkvKept = true, Message = "MP4 сделать не удалось, но запись цела: " + parts[0] + " (открывается в VLC). Подробности в журнале." };
            }

            TimeSpan? mp4Duration = Ffmpeg.ProbeDuration(ffmpegPath, tmp);
            if (!mp4Duration.HasValue || mp4Duration.Value.TotalSeconds < 0.5)
            {
                TryDelete(tmp);
                TryDelete(listFile);
                return new FinalizeResult { Success = false, MkvKept = true, Message = "Готовый MP4 не прошёл проверку, поэтому сохранён исходный файл: " + parts[0] };
            }

            File.Move(tmp, target);
            TryDelete(listFile);

            bool allKnown = true;
            double total = 0;
            foreach (string p in parts)
            {
                TimeSpan? d = Ffmpeg.ProbeDuration(ffmpegPath, p);
                if (!d.HasValue)
                {
                    allKnown = false;
                    break;
                }
                total += d.Value.TotalSeconds;
            }

            double tolerance = 2 + total * 0.01 + (parts.Count - 1) * 1.0;
            bool verified = allKnown && Math.Abs(mp4Duration.Value.TotalSeconds - total) <= tolerance;
            bool deleteMkv = !job.KeepMkv && verified;
            if (deleteMkv)
            {
                foreach (string p in parts) TryDelete(p);
            }
            job.Delete();

            Log.Info("MP4 готов: " + target + ", длительность " + Fmt.Timer(mp4Duration.Value) + (deleteMkv ? ", MKV удалён" : ", MKV сохранён"));
            string message = "Запись сохранена: " + Path.GetFileName(target) + " (" + Fmt.Timer(mp4Duration.Value) + ").";
            if (!deleteMkv) message += job.KeepMkv ? " Исходный MKV лежит рядом." : " Исходный MKV оставлен на всякий случай.";
            return new FinalizeResult { Success = true, Mp4Path = target, MkvKept = !deleteMkv, Message = message };
        }

        private static void TryDelete(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось удалить " + file, ex);
            }
        }
    }
}
