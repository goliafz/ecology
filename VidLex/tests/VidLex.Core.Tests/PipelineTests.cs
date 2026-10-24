using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;
using VidLex.Core.Services;
using Xunit;

namespace VidLex.Core.Tests
{
    internal sealed class FakeProbe : IMediaProbeService
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
        {
            var name = Path.GetFileName(path);
            if (name.Contains("битое")) throw new ExternalToolException("Не удалось прочитать медиаданные: файл повреждён.");
            var info = new MediaInfo { Duration = TimeSpan.FromSeconds(name.Contains("длинное") ? 1500 : 60), HasVideo = true };
            if (!name.Contains("беззвука")) info.AudioStreams.Add(new AudioStreamInfo { AudioIndex = 0, Codec = "aac", Channels = 2 });
            return Task.FromResult(info);
        }
    }

    internal sealed class FakeAudio : IAudioExtractionService
    {
        public int Extractions;

        public Task ExtractAsync(string inputPath, int audioIndex, string outputWav, TimeSpan? duration, IProgress<double> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Extractions);
            var seconds = duration?.TotalSeconds ?? 60;
            using (var fs = new FileStream(outputWav, FileMode.Create))
                fs.SetLength(44 + (long)(seconds * 32000));
            progress?.Report(1);
            return Task.CompletedTask;
        }

        public Task ExtractChunkAsync(string sourceWav, double start, double length, string outputWav, CancellationToken ct)
        {
            File.WriteAllBytes(outputWav, new byte[44]);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeSilence : ISilenceDetectionService
    {
        public Func<string, double, List<TimeRange>> Result = (p, total) => new List<TimeRange>();

        public Task<List<TimeRange>> DetectAsync(string wavPath, double thresholdDb, double minSilenceSeconds, double totalSeconds, CancellationToken ct)
            => Task.FromResult(Result(wavPath, totalSeconds));
    }

    internal sealed class FakeEngine : ITranscriptionEngine
    {
        public string EngineName => "тестовый движок";
        public int Calls;
        public int DelayMs;
        /// <summary>Задание определяется по рабочей папке (Temp\job-&lt;id&gt;).</summary>
        public Func<ChunkTranscriptionRequest, bool> FailFor = _ => false;
        public readonly List<string> Prompts = new List<string>();

        public async Task<List<TranscriptSegment>> TranscribeChunkAsync(ChunkTranscriptionRequest r, IProgress<ChunkProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (Prompts) Prompts.Add(r.Prompt);
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            ct.ThrowIfCancellationRequested();
            if (FailFor(r)) throw new ExternalToolException("whisper-cli завершился с ошибкой (код 3).");
            progress?.Report(new ChunkProgress { Fraction = 1, ConfirmedSeconds = r.ChunkLengthSeconds });
            return new List<TranscriptSegment> { Seg.Make(r.OffsetSeconds + 10, r.OffsetSeconds + 14, $"Фрагмент номер {r.ChunkIndex + 1} распознан.") };
        }
    }

    public class PipelineTests
    {
        private sealed class Rig : IDisposable
        {
            public readonly TempDir Dir = new TempDir();
            public readonly MemoryLog Log = new MemoryLog();
            public readonly FakeAudio Audio = new FakeAudio();
            public readonly FakeSilence Silence = new FakeSilence();
            public readonly FakeEngine Engine = new FakeEngine();
            public readonly AppPaths Paths;
            public readonly JobProcessor Processor;
            public readonly QueueProcessingService Queue;
            public readonly TranscriptExportService Export;
            public readonly ToolPaths Tools = new ToolPaths { Ffmpeg = "ffmpeg", Ffprobe = "ffprobe", WhisperCli = "whisper-cli", Model = "/models/ggml-test.bin" };

            public Rig()
            {
                Paths = new AppPaths(Dir.Combine("data"));
                Paths.EnsureCreated();
                Export = new TranscriptExportService(Log);
                Processor = new JobProcessor(new FakeProbe(), Audio, Silence, Engine, new TextCorrectionService(Log, Seg.DictionaryDir), Export, Log, Paths);
                Queue = new QueueProcessingService(Processor, new ProgressEstimationService(), Log, new ExternalProcessRegistry(), new QueueStateStore(Paths.QueueStateFile), Export);
            }

            public VideoJob Job(string relative)
            {
                var full = Dir.CreateFile(Path.Combine("src", relative), "video");
                var fi = new FileInfo(full);
                return new VideoJob(full, Dir.Combine("src")) { FileSize = fi.Length, LastWriteTimeUtc = fi.LastWriteTimeUtc };
            }

            public void Dispose() => Dir.Dispose();
        }

        [Fact]
        public async Task ErrorInOneFileDoesNotStopQueue()
        {
            using (var rig = new Rig())
            {
                var jobs = new List<VideoJob> { rig.Job("01 норма.mp4"), rig.Job("02 битое.mp4"), rig.Job("03 беззвука.mp4"), rig.Job("04 норма.mkv") };
                var summary = await rig.Queue.RunAsync(jobs, new AppSettings(), rig.Tools, rig.Dir.Combine("src"));
                Assert.Equal(JobStatus.Completed, jobs[0].Status);
                Assert.Equal(JobStatus.Error, jobs[1].Status);
                Assert.Contains("повреждён", jobs[1].Error);
                Assert.Equal(JobStatus.Skipped, jobs[2].Status);
                Assert.Equal(JobStatus.Completed, jobs[3].Status);
                Assert.Equal(2, summary.Completed);
                Assert.Equal(1, summary.Errors);
                Assert.Equal("Фрагмент номер 1 распознан.\r\n", File.ReadAllText(rig.Dir.Combine("src", "01 норма.txt")));
                Assert.Empty(Directory.GetDirectories(rig.Paths.TempDirectory));
                Assert.True(rig.Log.Has(LogLevel.Error, "повреждён"));
            }
        }

        [Fact]
        public async Task EngineFailureIsIsolated()
        {
            using (var rig = new Rig())
            {
                var jobs = new List<VideoJob> { rig.Job("сбой.mp4"), rig.Job("норма.mp4") };
                rig.Engine.FailFor = r => r.WorkingDirectory.EndsWith(jobs[0].Id);
                rig.Engine.DelayMs = 1;
                await rig.Queue.RunAsync(jobs, new AppSettings(), rig.Tools, rig.Dir.Combine("src"));
                Assert.Equal(JobStatus.Error, jobs[0].Status);
                Assert.Equal(JobStatus.Completed, jobs[1].Status);
            }
        }

        [Fact]
        public async Task ExistingResultIsSkippedNotFailed()
        {
            using (var rig = new Rig())
            {
                var job = rig.Job("видео.mp4");
                File.WriteAllText(rig.Dir.Combine("src", "видео.txt"), "старый");
                await rig.Queue.RunAsync(new List<VideoJob> { job }, new AppSettings { ExistingResultPolicy = ExistingResultPolicy.Skip }, rig.Tools, rig.Dir.Combine("src"));
                Assert.Equal(JobStatus.Skipped, job.Status);
                Assert.Equal("старый", File.ReadAllText(rig.Dir.Combine("src", "видео.txt")));
                Assert.Equal(0, rig.Engine.Calls);
            }
        }

        [Fact]
        public async Task SilentAudioWritesNoSpeechMarkerWithoutRunningEngine()
        {
            using (var rig = new Rig())
            {
                rig.Silence.Result = (p, total) => new List<TimeRange> { new TimeRange(0, total) };
                var job = rig.Job("тишина.mp4");
                await rig.Queue.RunAsync(new List<VideoJob> { job }, new AppSettings(), rig.Tools, rig.Dir.Combine("src"));
                Assert.Equal(JobStatus.Completed, job.Status);
                Assert.Equal("[Речь не обнаружена]\r\n", File.ReadAllText(rig.Dir.Combine("src", "тишина.txt")));
                Assert.Equal(0, rig.Engine.Calls);
            }
        }

        [Fact]
        public async Task LongFileIsProcessedInChunksAndTimestampsAreAbsolute()
        {
            using (var rig = new Rig())
            {
                var job = rig.Job("длинное.mp4");
                var s = new AppSettings { AddTimestamps = true, ChunkLengthSeconds = 600 };
                await rig.Queue.RunAsync(new List<VideoJob> { job }, s, rig.Tools, rig.Dir.Combine("src"));
                Assert.Equal(JobStatus.Completed, job.Status);
                Assert.Equal(3, rig.Engine.Calls);
                var lines = File.ReadAllLines(rig.Dir.Combine("src", "длинное.txt"));
                Assert.Equal(new[] { "[00:00:10] Фрагмент номер 1 распознан.", "[00:10:10] Фрагмент номер 2 распознан.", "[00:20:10] Фрагмент номер 3 распознан." }, lines);
            }
        }

        [Fact]
        public async Task StopMarksCurrentJobStoppedAndLeavesOthersWaiting()
        {
            using (var rig = new Rig())
            {
                rig.Engine.DelayMs = 5000;
                var jobs = new List<VideoJob> { rig.Job("a.mp4"), rig.Job("b.mp4") };
                var run = rig.Queue.RunAsync(jobs, new AppSettings(), rig.Tools, rig.Dir.Combine("src"));
                await WaitUntil(() => jobs[0].Status == JobStatus.Recognizing);
                rig.Queue.Stop();
                var summary = await run;
                Assert.True(summary.WasStopped);
                Assert.Equal(JobStatus.Stopped, jobs[0].Status);
                Assert.Equal(JobStatus.Waiting, jobs[1].Status);
                Assert.False(File.Exists(rig.Dir.Combine("src", "a.txt")));
                Assert.Empty(Directory.GetDirectories(rig.Paths.TempDirectory));
                Assert.Equal(QueueState.Idle, rig.Queue.State);
            }
        }

        [Fact]
        public async Task PauseHoldsQueueBetweenJobsUntilResume()
        {
            using (var rig = new Rig())
            {
                rig.Engine.DelayMs = 300;
                var jobs = new List<VideoJob> { rig.Job("a.mp4"), rig.Job("b.mp4") };
                var run = rig.Queue.RunAsync(jobs, new AppSettings(), rig.Tools, rig.Dir.Combine("src"));
                await WaitUntil(() => jobs[0].Status == JobStatus.Recognizing);
                rig.Queue.Pause();
                Assert.Equal(QueueState.Paused, rig.Queue.State);
                await Task.Delay(1500);
                // Первое задание может завершить текущий фрагмент, но второе не начинается.
                Assert.Equal(JobStatus.Waiting, jobs[1].Status);
                Assert.True(rig.Log.Has(LogLevel.Info, "приостановлена"));
                rig.Queue.Resume();
                await run;
                Assert.All(jobs, j => Assert.Equal(JobStatus.Completed, j.Status));
                Assert.True(rig.Log.Has(LogLevel.Info, "продолжена"));
            }
        }

        [Fact]
        public async Task SaveFailureKeepsRecoveredCopy()
        {
            using (var rig = new Rig())
            {
                var job = rig.Job("видео.mp4");
                // Папка результатов указывает на файл — сохранить туда невозможно.
                var blocker = rig.Dir.CreateFile("blocker", "файл");
                var s = new AppSettings { OutputMode = OutputMode.SingleFolder, OutputFolder = Path.Combine(blocker, "out") };
                await rig.Queue.RunAsync(new List<VideoJob> { job }, s, rig.Tools, rig.Dir.Combine("src"));
                Assert.Equal(JobStatus.Error, job.Status);
                Assert.Contains("резервную копию", job.Error);
                var recovered = Directory.GetFiles(rig.Paths.RecoveredDirectory, "*.txt").Single();
                Assert.Contains("Фрагмент номер 1", File.ReadAllText(recovered));
            }
        }

        [Fact]
        public async Task CustomWordsArePassedAsPrompt()
        {
            using (var rig = new Rig())
            {
                var job = rig.Job("видео.mp4");
                var s = new AppSettings { CustomWords = new List<string> { "VidLex", "Ходаковский" } };
                await rig.Queue.RunAsync(new List<VideoJob> { job }, s, rig.Tools, rig.Dir.Combine("src"));
                Assert.Equal("Термины: VidLex, Ходаковский.", rig.Engine.Prompts.Single());
            }
        }

        [Fact]
        public async Task CheckpointAllowsResumeAfterInterruption()
        {
            using (var rig = new Rig())
            {
                rig.Engine.DelayMs = 400;
                var job = rig.Job("длинное.mp4");
                var s = new AppSettings { ChunkLengthSeconds = 600, KeepCheckpoints = true };
                var run = rig.Queue.RunAsync(new List<VideoJob> { job }, s, rig.Tools, rig.Dir.Combine("src"));
                await WaitUntil(() => rig.Engine.Calls >= 2);
                rig.Queue.Stop(keepCheckpoints: true); // как при закрытии приложения
                await run;
                Assert.Equal(JobStatus.Stopped, job.Status);
                Assert.Single(Directory.GetDirectories(rig.Paths.TempDirectory));

                rig.Engine.DelayMs = 0;
                int before = rig.Engine.Calls;
                int extractionsBefore = rig.Audio.Extractions;
                await rig.Queue.RunAsync(new List<VideoJob> { job }, s, rig.Tools, rig.Dir.Combine("src"));
                Assert.Equal(JobStatus.Completed, job.Status);
                Assert.Equal(extractionsBefore, rig.Audio.Extractions); // аудио повторно не извлекалось
                Assert.True(rig.Engine.Calls - before <= 2, "распознаются только оставшиеся фрагменты");
                Assert.True(rig.Log.Has(LogLevel.Info, "Восстановление после сбоя"));
                Assert.Empty(Directory.GetDirectories(rig.Paths.TempDirectory));
            }
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Условие не выполнено");
                await Task.Delay(20);
            }
        }
    }
}
