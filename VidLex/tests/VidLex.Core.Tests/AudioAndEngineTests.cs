using System.Collections.Generic;
using System.Linq;
using System.Text;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;
using VidLex.Core.Services;
using Xunit;

namespace VidLex.Core.Tests
{
    public class ChunkPlannerTests
    {
        [Fact]
        public void ShortAudioIsSingleChunk()
        {
            var chunks = ChunkPlanner.Plan(300, new List<TimeRange>(), 600);
            Assert.Single(chunks);
            Assert.Equal(0, chunks[0].Start);
            Assert.Equal(300, chunks[0].End);
        }

        [Fact]
        public void CutsAtSilenceNearTarget()
        {
            var silences = new List<TimeRange> { new TimeRange(300, 301), new TimeRange(590, 592), new TimeRange(1250, 1251) };
            var chunks = ChunkPlanner.Plan(1800, silences, 600);
            Assert.True(chunks[0].CutAtSilence);
            Assert.Equal(591, chunks[0].End, 3);
            Assert.Equal(591, chunks[1].Start, 3);
        }

        [Fact]
        public void HardCutUsesOverlapWhenNoSilence()
        {
            var chunks = ChunkPlanner.Plan(1500, new List<TimeRange>(), 600);
            Assert.False(chunks[0].CutAtSilence);
            Assert.Equal(600, chunks[0].NominalEnd, 3);
            Assert.Equal(600 + ChunkPlanner.HardCutOverlapSeconds, chunks[0].End, 3);
            Assert.Equal(600, chunks[1].Start, 3);
        }

        [Fact]
        public void ChunksCoverWholeAudioWithoutGaps()
        {
            var silences = Enumerable.Range(1, 40).Select(i => new TimeRange(i * 97.0, i * 97.0 + 0.6)).ToList();
            var chunks = ChunkPlanner.Plan(4000, silences, 600);
            Assert.Equal(0, chunks[0].Start);
            Assert.Equal(4000, chunks.Last().End, 3);
            for (int i = 1; i < chunks.Count; i++)
                Assert.Equal(chunks[i - 1].NominalEnd, chunks[i].Start, 6);
            Assert.All(chunks, c => Assert.True(c.Length <= 600 + ChunkPlanner.SearchForwardSeconds + ChunkPlanner.HardCutOverlapSeconds + 0.001));
        }
    }

    public class SegmentMergerTests
    {
        private static string Text(IEnumerable<TranscriptSegment> segs) => string.Join(" ", segs.SelectMany(s => s.Words).Select(w => w.Text));

        [Fact]
        public void RemovesDuplicateWordsInOverlap()
        {
            var chunks = new List<AudioChunk>
            {
                new AudioChunk { Index = 0, Start = 0, End = 14, NominalEnd = 10, CutAtSilence = false },
                new AudioChunk { Index = 1, Start = 10, End = 20, NominalEnd = 20, CutAtSilence = true }
            };
            // Перекрытие 10–14 с: оба фрагмента распознали «мы перейдём к практике».
            var left = new List<TranscriptSegment> { Seg.Make(0, 8, "Сегодня изучаем основы программы"), Seg.Make(9, 13.5, "затем мы перейдём к практике") };
            var right = new List<TranscriptSegment> { Seg.Make(10.2, 13.6, "мы перейдём к практике"), Seg.Make(14, 19, "и разберём примеры") };
            var merged = SegmentMerger.Merge(chunks, new List<List<TranscriptSegment>> { left, right });
            Assert.Equal("Сегодня изучаем основы программы затем мы перейдём к практике и разберём примеры", Text(merged));
        }

        [Fact]
        public void SilenceBoundaryKeepsEverything()
        {
            var chunks = new List<AudioChunk>
            {
                new AudioChunk { Index = 0, Start = 0, End = 10, NominalEnd = 10, CutAtSilence = true },
                new AudioChunk { Index = 1, Start = 10, End = 20, NominalEnd = 20, CutAtSilence = true }
            };
            var merged = SegmentMerger.Merge(chunks, new List<List<TranscriptSegment>>
            {
                new List<TranscriptSegment> { Seg.Make(0, 9, "первая часть") },
                new List<TranscriptSegment> { Seg.Make(11, 19, "вторая часть") }
            });
            Assert.Equal("первая часть вторая часть", Text(merged));
        }

        [Fact]
        public void FallsBackToTimeSplitWithoutTextMatch()
        {
            var chunks = new List<AudioChunk>
            {
                new AudioChunk { Index = 0, Start = 0, End = 14, NominalEnd = 10, CutAtSilence = false },
                new AudioChunk { Index = 1, Start = 10, End = 20, NominalEnd = 20, CutAtSilence = true }
            };
            var left = new List<TranscriptSegment> { Seg.Make(0, 14, "раз два три четыре пять шесть семь") };   // слова каждые 2 с
            var right = new List<TranscriptSegment> { Seg.Make(10, 20, "альфа бета гамма дельта эпсилон") }; // слова каждые 2 с
            var merged = SegmentMerger.Merge(chunks, new List<List<TranscriptSegment>> { left, right });
            // Середина перекрытия — 12 с: слева слова до 12 с, справа — с 12 с.
            Assert.Equal("раз два три четыре пять шесть бета гамма дельта эпсилон", Text(merged));
        }
    }

    public class ParsingTests
    {
        [Fact]
        public void ParsesFfprobeJsonAndPicksRussianTrack()
        {
            const string json = @"{""streams"":[
                {""index"":0,""codec_type"":""video"",""codec_name"":""h264"",""disposition"":{""default"":1,""attached_pic"":0}},
                {""index"":1,""codec_type"":""audio"",""codec_name"":""aac"",""channels"":2,""sample_rate"":""48000"",""tags"":{""language"":""eng""},""disposition"":{""default"":1}},
                {""index"":2,""codec_type"":""audio"",""codec_name"":""ac3"",""channels"":6,""sample_rate"":""48000"",""tags"":{""language"":""rus"",""title"":""Дубляж""},""disposition"":{""default"":0}}],
                ""format"":{""format_name"":""matroska,webm"",""duration"":""5025.120000""}}";
            var info = MediaProbeService.Parse(json);
            Assert.True(info.HasVideo);
            Assert.Equal(2, info.AudioStreams.Count);
            Assert.Equal(5025.12, info.Duration.Value.TotalSeconds, 2);
            var picked = MediaProbeService.SelectAudioStream(info, -1, out var note);
            Assert.Equal(1, picked.AudioIndex);
            Assert.Contains("русская", note);
            Assert.Equal(0, MediaProbeService.SelectAudioStream(info, 0, out _).AudioIndex);
            Assert.Equal(0, MediaProbeService.SelectAudioStream(info, 7, out var fallbackNote).AudioIndex);
            Assert.Contains("нет", fallbackNote);
        }

        [Fact]
        public void NoAudioStreams()
        {
            var info = MediaProbeService.Parse(@"{""streams"":[{""index"":0,""codec_type"":""video""}],""format"":{""duration"":""5""}}");
            Assert.Null(MediaProbeService.SelectAudioStream(info, -1, out _));
        }

        [Fact]
        public void ParsesSilenceDetectOutput()
        {
            var lines = new[]
            {
                "[silencedetect @ 0x1] silence_start: 0",
                "[silencedetect @ 0x1] silence_end: 1.5 | silence_duration: 1.5",
                "[silencedetect @ 0x1] silence_start: 10.25",
                "[silencedetect @ 0x1] silence_end: 12.75 | silence_duration: 2.5",
                "[silencedetect @ 0x1] silence_start: 58.1"
            };
            var r = SilenceDetectionService.ParseSilences(lines, 60);
            Assert.Equal(3, r.Count);
            Assert.Equal(10.25, r[1].Start, 3);
            Assert.Equal(60, r[2].End, 3);
        }

        [Fact]
        public void ParsesWhisperJsonWithSplitUtf8Tokens()
        {
            // Буква «ш» (D1 88) разрезана между двумя токенами — так бывает в старых версиях whisper.cpp.
            var latin1 = Encoding.GetEncoding("ISO-8859-1");
            string Raw(string s) => latin1.GetString(Encoding.UTF8.GetBytes(s));
            var sh = Encoding.UTF8.GetBytes("ш");
            string json = "{\"transcription\":[{\"offsets\":{\"from\":1000,\"to\":3000},\"text\":\"" + Raw(" Наша речь.") + "\",\"tokens\":[" +
                          "{\"text\":\"[_BEG_]\",\"offsets\":{\"from\":1000,\"to\":1000},\"id\":50364,\"p\":0.9}," +
                          "{\"text\":\"" + Raw(" На") + latin1.GetString(new[] { sh[0] }) + "\",\"offsets\":{\"from\":1000,\"to\":1300},\"id\":100,\"p\":0.9}," +
                          "{\"text\":\"" + latin1.GetString(new[] { sh[1] }) + Raw("а") + "\",\"offsets\":{\"from\":1300,\"to\":1600},\"id\":101,\"p\":0.8}," +
                          "{\"text\":\"" + Raw(" речь") + "\",\"offsets\":{\"from\":1700,\"to\":2500},\"id\":102,\"p\":0.95}," +
                          "{\"text\":\".\",\"offsets\":{\"from\":2500,\"to\":2600},\"id\":13,\"p\":0.99}," +
                          "{\"text\":\"[_TT_150]\",\"offsets\":{\"from\":3000,\"to\":3000},\"id\":50514,\"p\":0.5}]}]}";
            var bytes = latin1.GetBytes(json);
            var segs = WhisperCppEngine.ParseJson(bytes, 600, 1);
            Assert.Single(segs);
            Assert.Equal("Наша речь.", segs[0].Text);
            Assert.Equal(new[] { "Наша", "речь." }, segs[0].Words.Select(w => w.Text).ToArray());
            Assert.Equal(601.0, segs[0].Words[0].Start, 3);
            Assert.Equal(601.7, segs[0].Words[1].Start, 3);
            Assert.Equal(601.0, segs[0].Start, 3);
        }

        [Fact]
        public void WhisperArgumentsUseRelativeChunkAndOptionalFlags()
        {
            var r = new ChunkTranscriptionRequest
            {
                WorkingDirectory = "/tmp/x",
                ChunkFileName = "chunk-001.wav",
                ModelPath = "/models/ggml-small.bin",
                Prompt = "Термины: VidLex.",
                Threads = 4,
                BeamSize = 5,
                UseGpu = false,
                CarryContext = false
            };
            var args = WhisperCppEngine.BuildArguments(r, "chunk-001");
            Assert.Contains("chunk-001.wav", args);
            Assert.Equal("ru", args[args.IndexOf("-l") + 1]);
            Assert.Contains("-ng", args);
            Assert.Equal("0", args[args.IndexOf("-mc") + 1]);
            Assert.Equal("Термины: VidLex.", args[args.IndexOf("--prompt") + 1]);
            Assert.Contains("-ojf", args);
        }

        [Theory]
        [InlineData("simple", "simple")]
        [InlineData("с пробелом", "\"с пробелом\"")]
        [InlineData("", "\"\"")]
        [InlineData("C:\\Папка с пробелом\\", "\"C:\\Папка с пробелом\\\\\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        public void QuotesArgumentsForWindowsCommandLine(string input, string expected)
        {
            Assert.Equal(expected, ProcessRunner.Quote(input));
        }
    }
}
