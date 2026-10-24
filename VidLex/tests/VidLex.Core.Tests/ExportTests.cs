using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VidLex.Core.Models;
using VidLex.Core.Services;
using Xunit;

namespace VidLex.Core.Tests
{
    public class ExportTests
    {
        private static ProcessedTranscript Transcript(params (double start, double end, string text)[] items)
        {
            var t = new ProcessedTranscript();
            foreach (var i in items) t.Sentences.Add(new TranscriptSentence { Start = i.start, End = i.end, Text = i.text });
            return t;
        }

        private static readonly ProcessedTranscript Sample = Transcript(
            (3.4, 8, "Сегодня мы рассмотрим основные принципы работы программы."),
            (12.9, 16, "Затем перейдём к практическим примерам."));

        [Fact]
        public void RenderWithoutTimestamps()
        {
            var s = new AppSettings { AddTimestamps = false, SplitIntoParagraphs = false };
            var text = new TranscriptExportService(new MemoryLog()).Render(Sample, s);
            Assert.Equal("Сегодня мы рассмотрим основные принципы работы программы. Затем перейдём к практическим примерам.\r\n", text);
        }

        [Fact]
        public void RenderWithTimestampsMatchesSpecificationExample()
        {
            var s = new AppSettings { AddTimestamps = true };
            var text = new TranscriptExportService(new MemoryLog()).Render(Sample, s);
            Assert.Equal("[00:00:03] Сегодня мы рассмотрим основные принципы работы программы.\r\n[00:00:12] Затем перейдём к практическим примерам.\r\n", text);
        }

        [Fact]
        public void TimestampIntervalGroupsSentencesWithoutChangingTimes()
        {
            var t = Transcript((0, 2, "А."), (5, 7, "Б."), (31, 33, "В."), (40, 41, "Г."), (75, 76, "Д."));
            var s = new AppSettings { AddTimestamps = true, TimestampIntervalSeconds = 30 };
            var text = new TranscriptExportService(new MemoryLog()).Render(t, s);
            Assert.Equal("[00:00:00] А. Б.\r\n\r\n[00:00:31] В. Г.\r\n\r\n[00:01:15] Д.\r\n", text);
        }

        [Fact]
        public void ParagraphsSplitOnLongPauses()
        {
            var t = Transcript((0, 2, "Первая."), (2.5, 4, "Вторая."), (10, 12, "Третья."));
            var text = new TranscriptExportService(new MemoryLog()).Render(t, new AppSettings());
            Assert.Equal("Первая. Вторая.\r\n\r\nТретья.\r\n", text);
        }

        [Fact]
        public void EmptyResultMarker()
        {
            var svc = new TranscriptExportService(new MemoryLog());
            Assert.Equal("[Речь не обнаружена]\r\n", svc.Render(new ProcessedTranscript(), new AppSettings()));
            Assert.Equal("", svc.Render(new ProcessedTranscript(), new AppSettings { EmptyResultBehavior = EmptyResultBehavior.WriteEmptyFile }));
        }

        [Fact]
        public void NextToVideoUsesSameBaseNameAndHandlesSameNameDifferentExtension()
        {
            using (var d = new TempDir())
            {
                var a = new VideoJob(d.CreateFile("Курс/Урок 01.mp4"), d.Path);
                var b = new VideoJob(d.CreateFile("Курс/Урок 01.mkv"), d.Path);
                var c = new VideoJob(d.CreateFile("Курс/Урок 02.avi"), d.Path);
                var svc = new TranscriptExportService(new MemoryLog());
                var s = new AppSettings();
                svc.PlanOutputs(new List<VideoJob> { a, b, c }, s);
                Assert.Equal(d.Combine("Курс", "Урок 01.txt"), svc.Decide(b, s).TargetPath);
                Assert.Equal(d.Combine("Курс", "Урок 01 [mp4].txt"), svc.Decide(a, s).TargetPath);
                Assert.Equal(d.Combine("Курс", "Урок 02.txt"), svc.Decide(c, s).TargetPath);
            }
        }

        [Fact]
        public void SingleFolderFlattensAndResolvesCollisionsPredictably()
        {
            using (var d = new TempDir())
            {
                var out1 = d.Combine("Результаты");
                var j1 = new VideoJob(d.CreateFile("src/Модуль 1/Урок 01.mp4"), d.Combine("src"));
                var j2 = new VideoJob(d.CreateFile("src/Модуль 2/Урок 01.mp4"), d.Combine("src"));
                var j3 = new VideoJob(d.CreateFile("src/Введение.mp4"), d.Combine("src"));
                var s = new AppSettings { OutputMode = OutputMode.SingleFolder, OutputFolder = out1 };
                var svc = new TranscriptExportService(new MemoryLog());
                svc.PlanOutputs(new List<VideoJob> { j1, j2, j3 }, s);
                Assert.Equal(Path.Combine(out1, "Урок 01 (Модуль 1).txt"), svc.Decide(j1, s).TargetPath);
                Assert.Equal(Path.Combine(out1, "Урок 01 (Модуль 2).txt"), svc.Decide(j2, s).TargetPath);
                Assert.Equal(Path.Combine(out1, "Введение.txt"), svc.Decide(j3, s).TargetPath);
            }
        }

        [Fact]
        public void ExistingResultPolicies()
        {
            using (var d = new TempDir())
            {
                var job = new VideoJob(d.CreateFile("Видео.mp4"), d.Path);
                var existing = d.CreateFile("Видео.txt", "старый результат");
                var svc = new TranscriptExportService(new MemoryLog());
                var jobs = new List<VideoJob> { job };

                var skip = new AppSettings { ExistingResultPolicy = ExistingResultPolicy.Skip };
                svc.PlanOutputs(jobs, skip);
                var dec = svc.Decide(job, skip);
                Assert.True(dec.Skip);

                var overwrite = new AppSettings { ExistingResultPolicy = ExistingResultPolicy.Overwrite };
                svc.PlanOutputs(jobs, overwrite);
                dec = svc.Decide(job, overwrite);
                Assert.False(dec.Skip);
                Assert.Equal(existing, dec.TargetPath);

                var createNew = new AppSettings { ExistingResultPolicy = ExistingResultPolicy.CreateNew };
                svc.PlanOutputs(jobs, createNew);
                dec = svc.Decide(job, createNew);
                Assert.Equal(d.Combine("Видео (2).txt"), dec.TargetPath);
            }
        }

        [Fact]
        public void SingleFolderNeverOverwritesForeignFile()
        {
            using (var d = new TempDir())
            {
                var outDir = d.Combine("out");
                var foreign = d.CreateFile("out/Урок.txt", "чужой файл");
                var job = new VideoJob(d.CreateFile("src/Урок.mp4"), d.Combine("src"));
                var s = new AppSettings { OutputMode = OutputMode.SingleFolder, OutputFolder = outDir, ExistingResultPolicy = ExistingResultPolicy.Overwrite };
                var log = new MemoryLog();
                var svc = new TranscriptExportService(log);
                svc.PlanOutputs(new List<VideoJob> { job }, s);
                var dec = svc.Decide(job, s);
                Assert.Equal(Path.Combine(outDir, "Урок (2).txt"), dec.TargetPath);
                svc.Save(job, dec.TargetPath, "новый", s);
                Assert.Equal("чужой файл", File.ReadAllText(foreign));
                Assert.True(log.Has(LogLevel.Warning, "не для этого видео"));

                // Повторный запуск: VidLex узнаёт свой результат по манифесту и применяет политику к нему.
                var svc2 = new TranscriptExportService(new MemoryLog());
                var skip = new AppSettings { OutputMode = OutputMode.SingleFolder, OutputFolder = outDir, ExistingResultPolicy = ExistingResultPolicy.Skip };
                svc2.PlanOutputs(new List<VideoJob> { job }, skip);
                var again = svc2.Decide(job, skip);
                Assert.True(again.Skip);
                Assert.Equal(Path.Combine(outDir, "Урок (2).txt"), again.TargetPath);
            }
        }

        [Fact]
        public void SaveWritesUtf8WithBomAndCyrillic()
        {
            using (var d = new TempDir())
            {
                var job = new VideoJob(d.CreateFile("Лекция.mp4"), d.Path);
                var svc = new TranscriptExportService(new MemoryLog());
                var target = d.Combine("Лекция.txt");
                svc.Save(job, target, "Съешь же ещё этих мягких французских булок, да выпей чаю.\r\n", new AppSettings { WriteUtf8Bom = true });
                var bytes = File.ReadAllBytes(target);
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
                Assert.Equal("Съешь же ещё этих мягких французских булок, да выпей чаю.\r\n", new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
                Assert.Empty(Directory.GetFiles(d.Path, "*.tmp"));
            }
        }

        [Fact]
        public void SaveWithoutBom()
        {
            using (var d = new TempDir())
            {
                var job = new VideoJob(d.CreateFile("a.mp4"), d.Path);
                var target = d.Combine("a.txt");
                new TranscriptExportService(new MemoryLog()).Save(job, target, "Привет", new AppSettings { WriteUtf8Bom = false });
                Assert.Equal(0xD0, File.ReadAllBytes(target)[0]);
            }
        }

        [Fact]
        public void CheckWritableReportsProblem()
        {
            using (var d = new TempDir())
            {
                var svc = new TranscriptExportService(new MemoryLog());
                Assert.Null(svc.CheckWritable(d.Combine("новая папка")));
                // Путь, где «папка» — это файл: создать нельзя.
                var file = d.CreateFile("файл");
                Assert.NotNull(svc.CheckWritable(Path.Combine(file, "внутри")));
            }
        }
    }
}
