using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;
using VidLex.Core.Services;
using Xunit;

namespace VidLex.Core.Tests
{
    public class SettingsTests
    {
        [Fact]
        public void MissingFileCreatesDefaults()
        {
            using (var d = new TempDir())
            {
                var path = d.Combine("settings.json");
                var s = new SettingsService(path, new MemoryLog()).Load();
                Assert.Equal(OutputMode.NextToVideo, s.OutputMode);
                Assert.False(s.AddTimestamps);
                Assert.Equal(1, s.MaxParallelJobs);
                Assert.True(File.Exists(path));
            }
        }

        [Fact]
        public void CorruptedFileFallsBackToDefaultsAndKeepsBackup()
        {
            using (var d = new TempDir())
            {
                var path = d.CreateFile("settings.json", "{ \"OutputMode\": \"SingleFolder\", oops");
                var log = new MemoryLog();
                var s = new SettingsService(path, log).Load();
                Assert.Equal(OutputMode.NextToVideo, s.OutputMode);
                Assert.Single(Directory.GetFiles(d.Path, "settings.corrupted-*.json"));
                Assert.True(log.Has(LogLevel.Warning, "повреждён"));
            }
        }

        [Fact]
        public void InvalidSingleValueKeepsOtherSettings()
        {
            using (var d = new TempDir())
            {
                var path = d.CreateFile("settings.json", "{ \"OutputMode\": \"Куда-то\", \"AddTimestamps\": true, \"MaxParallelJobs\": 99 }");
                var s = new SettingsService(path, new MemoryLog()).Load();
                Assert.Equal(OutputMode.NextToVideo, s.OutputMode);
                Assert.True(s.AddTimestamps);
                Assert.Equal(1, s.MaxParallelJobs);
            }
        }

        [Fact]
        public void RoundTripAndListsAreReplacedNotAppended()
        {
            using (var d = new TempDir())
            {
                var path = d.Combine("settings.json");
                var svc = new SettingsService(path, new MemoryLog());
                var s = new AppSettings { AddTimestamps = true, TimestampIntervalSeconds = 30, CustomWords = new List<string> { "VidLex", "Ходаковский" }, VideoExtensions = new List<string> { ".mp4" } };
                svc.Save(s);
                var loaded = new SettingsService(path, new MemoryLog()).Load();
                Assert.True(loaded.AddTimestamps);
                Assert.Equal(30, loaded.TimestampIntervalSeconds);
                Assert.Equal(new[] { "VidLex", "Ходаковский" }, loaded.CustomWords);
                Assert.Equal(new[] { ".mp4" }, loaded.VideoExtensions);
            }
        }

        [Fact]
        public void ValidateClampsValues()
        {
            var s = new AppSettings { ChunkLengthSeconds = 5, Language = "en", BeamSize = 0, VideoExtensions = new List<string> { "MP4", " .Mkv " } };
            var fixes = s.Validate();
            Assert.Equal(600, s.ChunkLengthSeconds);
            Assert.Equal("ru", s.Language);
            Assert.Equal(5, s.BeamSize);
            Assert.Equal(new[] { ".mp4", ".mkv" }, s.VideoExtensions);
            Assert.True(fixes.Count >= 3);
        }
    }

    public class LoggingTests
    {
        [Fact]
        public void WritesStructuredLinesAndFiltersDebug()
        {
            using (var d = new TempDir())
            using (var log = new LoggingService(d.Path))
            {
                log.Info("Запуск", stage: "Приложение");
                log.Debug("Скрыто");
                log.Error("Сбой", new InvalidOperationException("подробности"), "видео.mp4", "Распознавание");
                log.Flush();
                var text = File.ReadAllText(Directory.GetFiles(d.Path, "VidLex_*.log").Single());
                Assert.Contains("| INFO    | Приложение | - | Запуск", text);
                Assert.Contains("| ERROR   | Распознавание | видео.mp4 | Сбой", text);
                Assert.Contains("InvalidOperationException: подробности", text);
                Assert.DoesNotContain("Скрыто", text);
            }
        }

        [Fact]
        public void RotatesBySize()
        {
            using (var d = new TempDir())
            using (var log = new LoggingService(d.Path, maxFileSizeMb: 1))
            {
                var big = new string('ж', 2000);
                for (int i = 0; i < 400; i++) log.Info(big);
                log.Flush();
                var files = Directory.GetFiles(d.Path, "VidLex_*.log");
                Assert.True(files.Length >= 2, "ожидалось несколько файлов после ротации");
            }
        }

        [Fact]
        public void DailyFileNameAndOldLogCleanup()
        {
            using (var d = new TempDir())
            {
                var old = d.CreateFile("VidLex_2020-01-01.log", "старый");
                File.SetLastWriteTime(old, new DateTime(2020, 1, 1));
                using (var log = new LoggingService(d.Path, clock: () => new DateTime(2026, 10, 24, 12, 0, 0)))
                {
                    log.Info("сегодня");
                    log.Flush();
                    Assert.True(File.Exists(d.Combine("VidLex_2026-10-24.log")));
                    Assert.Equal(1, log.DeleteOldLogs(30));
                    Assert.False(File.Exists(old));
                }
            }
        }
    }

    public class DiscoveryTests
    {
        [Fact]
        public void FindsVideosRecursivelyInNaturalOrder()
        {
            using (var d = new TempDir())
            {
                d.CreateFile("Урок 10.mp4");
                d.CreateFile("Урок 2.MKV");
                d.CreateFile("a/b/c/d/e/глубоко.webm");
                d.CreateFile("a/заметки.txt");
                d.CreateFile("a/~$временный.mp4");
                d.CreateFile("Видео (часть 1) — №1 & 'тест'.avi");
                var r = new VideoDiscoveryService(new MemoryLog()).ScanAsync(d.Path, AppSettings.DefaultVideoExtensions, null, CancellationToken.None).Result;
                var rel = r.Files.Select(f => PathUtil.GetRelativePath(d.Path, f).Replace('\\', '/')).ToList();
                Assert.Equal(new[] { "a/b/c/d/e/глубоко.webm", "Видео (часть 1) — №1 & 'тест'.avi", "Урок 2.MKV", "Урок 10.mp4" }, rel);
                Assert.Equal(1, r.SkippedUnsupported);
            }
        }

        [Fact]
        public void MissingFolderThrows()
        {
            var ex = Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                new VideoDiscoveryService(new MemoryLog()).ScanAsync("/нет/такой/папки", AppSettings.DefaultVideoExtensions, null, CancellationToken.None));
            Assert.NotNull(ex.Result);
        }

        [Fact]
        public void NaturalComparer()
        {
            var list = new List<string> { "Урок 10", "Урок 2", "урок 1", "Урок 02b" };
            list.Sort(NaturalStringComparer.Instance);
            Assert.Equal(new[] { "урок 1", "Урок 2", "Урок 02b", "Урок 10" }, list);
        }
    }

    public class UtilTests
    {
        [Fact]
        public void TimeFormats()
        {
            Assert.Equal("00:00:03", TimeFormat.Clock(3.9));
            Assert.Equal("26:01:05", TimeFormat.Clock(TimeSpan.FromSeconds(26 * 3600 + 65)));
            Assert.Equal("1 ч 05 мин", TimeFormat.Human(TimeSpan.FromMinutes(65)));
            Assert.Equal("3 мин 20 с", TimeFormat.Human(TimeSpan.FromSeconds(200)));
            Assert.True(TimeFormat.TryParseClock("00:01:02,500", out var s));
            Assert.Equal(62.5, s, 3);
        }

        [Fact]
        public void SanitizesFileNames()
        {
            Assert.Equal("a_b_c", PathUtil.SanitizeFileName("a:b?c"));
            Assert.Equal("Урок 1", PathUtil.SanitizeFileName("Урок 1."));
        }

        [Fact]
        public void PauseGateBlocksUntilResumed()
        {
            var gate = new PauseGate();
            gate.Pause();
            var wait = gate.WaitIfPausedAsync(CancellationToken.None);
            Assert.False(wait.Wait(200));
            gate.Resume();
            Assert.True(wait.Wait(2000));
            Assert.True(wait.Result);
        }

        [Fact]
        public void PausableStopwatchExcludesPause()
        {
            var gate = new PauseGate();
            using (var sw = new PausableStopwatch(gate))
            {
                Thread.Sleep(100);
                gate.Pause();
                var atPause = sw.Elapsed;
                Thread.Sleep(300);
                Assert.True((sw.Elapsed - atPause).TotalMilliseconds < 50);
                gate.Resume();
            }
        }

        [Fact]
        public void QueueSnapshotRestoresStatuses()
        {
            using (var d = new TempDir())
            {
                var a = new VideoJob(d.CreateFile("a.mp4"), d.Path);
                var b = new VideoJob(d.CreateFile("b.mp4"), d.Path);
                var c = new VideoJob(d.CreateFile("c.mp4"), d.Path);
                a.Status = JobStatus.Completed;
                a.OutputPath = d.Combine("a.txt");
                b.Status = JobStatus.Recognizing;
                c.Status = JobStatus.Error;
                c.Error = "сбой";
                var store = new QueueStateStore(d.Combine("state.json"));
                store.Save(d.Path, new[] { a, b, c }, true);
                var snap = store.Load();
                Assert.True(snap.IsRunning);

                var fresh = new[] { new VideoJob(a.FullPath, d.Path), new VideoJob(b.FullPath, d.Path), new VideoJob(c.FullPath, d.Path) };
                Assert.Equal(2, QueueStateStore.ApplySnapshot(snap, fresh));
                Assert.Equal(JobStatus.Completed, fresh[0].Status);
                Assert.Equal(JobStatus.Waiting, fresh[1].Status);
                Assert.Equal(JobStatus.Error, fresh[2].Status);
                store.MarkStopped();
                Assert.False(store.Load().IsRunning);
            }
        }

        [Fact]
        public void ModelFileValidation()
        {
            using (var d = new TempDir())
            {
                var bad = d.CreateFile("ggml-fake.bin", "не модель");
                Assert.NotNull(ToolLocator.ValidateModelFile(bad));
                var good = d.Combine("ggml-ok.bin");
                var bytes = new byte[2 * 1024 * 1024];
                BitConverter.GetBytes(0x67676d6c).CopyTo(bytes, 0);
                File.WriteAllBytes(good, bytes);
                Assert.Null(ToolLocator.ValidateModelFile(good));
            }
        }
    }

    public class EstimationTests
    {
        private static VideoJob Job(double seconds, JobStatus status = JobStatus.Waiting, double progress = 0)
        {
            return new VideoJob("/v/" + Guid.NewGuid() + ".mp4", "/v") { Duration = TimeSpan.FromSeconds(seconds), Status = status, Progress = progress };
        }

        [Fact]
        public void ShowsCalculatingWithoutData()
        {
            var est = new ProgressEstimationService();
            var jobs = new List<VideoJob> { Job(600, JobStatus.Recognizing, 5), Job(600) };
            var snap = est.Compute(jobs, new List<LiveJobProgress>(), 1);
            Assert.Equal("Расчёт времени…", snap.RemainingText);
            Assert.Null(snap.RemainingTime);
        }

        [Fact]
        public void EstimatesFromCompletedJobs()
        {
            var now = new DateTime(2026, 1, 1);
            var est = new ProgressEstimationService(() => now);
            est.AddCompleted(new JobStatistics { AudioSeconds = 600, TotalSeconds = 300, Recognized = true }); // ×0,5
            var jobs = new List<VideoJob> { Job(600, JobStatus.Completed, 100), Job(1200), Job(600) };
            var snap = est.Compute(jobs, null, 1);
            Assert.Equal(900, snap.RemainingTime.Value.TotalSeconds, 1);
            Assert.StartsWith("≈", snap.RemainingText);
            Assert.Equal(1, snap.Done);
            Assert.Equal(25, snap.OverallPercent, 1);
        }

        [Fact]
        public void UsesLiveRateForFirstJob()
        {
            var est = new ProgressEstimationService();
            var jobs = new List<VideoJob> { Job(1000, JobStatus.Recognizing, 20) };
            var live = new List<LiveJobProgress> { new LiveJobProgress { RecognizedSeconds = 200, RecognitionElapsed = 100, AudioSeconds = 1000 } };
            var snap = est.Compute(jobs, live, 1);
            Assert.NotNull(snap.RemainingTime);
            Assert.Contains("текущего видео", snap.Basis);
        }

        [Fact]
        public void SmoothsSuddenJumps()
        {
            var now = new DateTime(2026, 1, 1);
            var est = new ProgressEstimationService(() => now);
            est.AddCompleted(new JobStatistics { AudioSeconds = 600, TotalSeconds = 600, Recognized = true });
            var jobs = new List<VideoJob> { Job(600, JobStatus.Completed, 100), Job(600) };
            var first = est.Compute(jobs, null, 1).RemainingTime.Value.TotalSeconds; // 600
            now = now.AddSeconds(10);
            jobs.Add(Job(6000)); // резко добавилось 6000 с
            var second = est.Compute(jobs, null, 1).RemainingTime.Value.TotalSeconds;
            Assert.True(second < 600 + 6600 * 0.5, "оценка не должна скачком прыгнуть к новому значению");
            Assert.True(second > first - 10);
        }

        [Fact]
        public void RoundsForDisplay()
        {
            Assert.Equal(45, ProgressEstimationService.RoundForDisplay(41));
            Assert.Equal(3600, ProgressEstimationService.RoundForDisplay(3590));
        }
    }
}
