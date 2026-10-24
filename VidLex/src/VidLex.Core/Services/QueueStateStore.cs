using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>Снимок очереди на диске (для восстановления после сбоя).</summary>
    public sealed class QueueSnapshot
    {
        public string SourceFolder { get; set; } = "";
        /// <summary>true, пока очередь работает. Если при запуске VidLex флаг установлен — прошлый сеанс завершился аварийно.</summary>
        public bool IsRunning { get; set; }
        public DateTime SavedAt { get; set; }
        public string AppVersion { get; set; } = "";
        public List<QueueSnapshotItem> Jobs { get; set; } = new List<QueueSnapshotItem>();
    }

    public sealed class QueueSnapshotItem
    {
        public string Path { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public JobStatus Status { get; set; }
        public string OutputPath { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public sealed class QueueStateStore
    {
        private readonly string _path;
        private readonly object _sync = new object();

        public QueueStateStore(string path)
        {
            _path = path;
        }

        public void Save(string sourceFolder, IEnumerable<VideoJob> jobs, bool isRunning)
        {
            var snapshot = new QueueSnapshot
            {
                SourceFolder = sourceFolder ?? "",
                IsRunning = isRunning,
                SavedAt = DateTime.Now,
                AppVersion = typeof(QueueStateStore).Assembly.GetName().Version?.ToString() ?? "",
                Jobs = jobs.Select(j => new QueueSnapshotItem
                {
                    Path = j.FullPath,
                    Status = j.Status,
                    OutputPath = j.OutputPath,
                    Error = j.Error
                }).ToList()
            };
            lock (_sync)
            {
                try
                {
                    AtomicFile.WriteAllText(_path, JsonConvert.SerializeObject(snapshot, Formatting.Indented), new UTF8Encoding(false));
                }
                catch
                {
                    // Состояние очереди — вспомогательная информация.
                }
            }
        }

        public QueueSnapshot Load()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(_path)) return null;
                    return JsonConvert.DeserializeObject<QueueSnapshot>(File.ReadAllText(_path, Encoding.UTF8), JsonDefaults.Settings);
                }
                catch
                {
                    return null;
                }
            }
        }

        public void MarkStopped()
        {
            var s = Load();
            if (s == null || !s.IsRunning) return;
            s.IsRunning = false;
            lock (_sync)
            {
                try
                {
                    AtomicFile.WriteAllText(_path, JsonConvert.SerializeObject(s, Formatting.Indented), new UTF8Encoding(false));
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Переносит статусы из снимка в свежесканированные задания.
        /// Задания, прерванные на середине, возвращаются в «Ожидает» (их контрольные точки будут использованы).
        /// </summary>
        public static int ApplySnapshot(QueueSnapshot snapshot, IEnumerable<VideoJob> jobs)
        {
            if (snapshot == null) return 0;
            var map = snapshot.Jobs.Where(j => j.Path != null).GroupBy(j => j.Path, PathUtil.PathComparer).ToDictionary(g => g.Key, g => g.First(), PathUtil.PathComparer);
            int restored = 0;
            foreach (var job in jobs)
            {
                if (!map.TryGetValue(job.FullPath, out var item)) continue;
                switch (item.Status)
                {
                    case JobStatus.Completed:
                    case JobStatus.Skipped:
                        job.Status = item.Status;
                        job.Progress = 100;
                        job.OutputPath = item.OutputPath ?? "";
                        job.Detail = "Из предыдущего сеанса";
                        restored++;
                        break;
                    case JobStatus.Error:
                        job.Status = JobStatus.Error;
                        job.Error = item.Error ?? "";
                        restored++;
                        break;
                    default:
                        job.Status = JobStatus.Waiting;
                        break;
                }
            }
            return restored;
        }
    }
}
