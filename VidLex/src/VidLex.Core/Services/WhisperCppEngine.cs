using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>
    /// Распознавание фрагмента через whisper-cli (whisper.cpp) как отдельный процесс.
    ///
    /// Почему файл ответов (@args.txt): whisper-cli на Windows получает argv в ANSI-кодировке системы,
    /// поэтому кириллические пути к модели и подсказка с русскими терминами искажаются на нерусской локали.
    /// Содержимое файла ответов читается как байты UTF-8 — так передаются и путь к модели
    /// (whisper.cpp сам переводит его из UTF-8 в UTF-16), и подсказка. Аудиофрагмент и выходной JSON
    /// имеют латинские имена и задаются относительно рабочей папки.
    /// </summary>
    public sealed class WhisperCppEngine : ITranscriptionEngine
    {
        private static readonly Regex SegmentLineRx = new Regex(
            @"^\[(\d{2}:\d{2}:\d{2}[.,]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[.,]\d{3})\]", RegexOptions.Compiled);
        private static readonly Regex ProgressRx = new Regex(@"progress\s*=\s*(\d+)\s*%", RegexOptions.Compiled);
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");
        private static readonly Encoding Utf8Strict = new UTF8Encoding(false, false);

        private readonly IProcessRunner _runner;
        private readonly Func<ToolPaths> _tools;
        private readonly ILogService _log;

        public WhisperCppEngine(IProcessRunner runner, Func<ToolPaths> tools, ILogService log)
        {
            _runner = runner;
            _tools = tools;
            _log = log;
        }

        public string EngineName => "whisper.cpp (whisper-cli)";

        public async Task<List<TranscriptSegment>> TranscribeChunkAsync(ChunkTranscriptionRequest r, IProgress<ChunkProgress> progress, CancellationToken ct)
        {
            var cli = _tools()?.WhisperCli;
            if (string.IsNullOrEmpty(cli)) throw new ExternalToolException("Не найден whisper-cli — распознавание невозможно.");
            if (!PathUtil.IsAscii(r.ChunkFileName)) throw new ArgumentException("Имя фрагмента должно состоять из латинских символов", nameof(r));

            var baseName = Path.GetFileNameWithoutExtension(r.ChunkFileName);
            var jsonPath = Path.Combine(r.WorkingDirectory, baseName + ".json");
            var argsFile = "args-" + baseName + ".txt";
            AtomicFile.TryDelete(jsonPath);

            var args = BuildArguments(r, baseName);
            // Перевод строки — разделитель аргументов в файле ответов; внутри значений их быть не должно.
            File.WriteAllText(Path.Combine(r.WorkingDirectory, argsFile),
                string.Join("\n", args.Select(a => a.Replace('\r', ' ').Replace('\n', ' '))) + "\n",
                new UTF8Encoding(false));

            double lastFraction = 0, lastConfirmed = 0;
            void Report()
            {
                progress?.Report(new ChunkProgress { Fraction = lastFraction, ConfirmedSeconds = lastConfirmed });
            }

            var spec = new ProcessSpec
            {
                FileName = cli,
                Arguments = { "@" + argsFile },
                WorkingDirectory = r.WorkingDirectory,
                Priority = r.Priority,
                TailLines = 60
            };

            var result = await _runner.RunAsync(spec,
                line =>
                {
                    var m = SegmentLineRx.Match(line);
                    if (m.Success && TimeFormat.TryParseClock(m.Groups[2].Value, out var end))
                    {
                        lastConfirmed = Math.Max(lastConfirmed, end);
                        if (r.ChunkLengthSeconds > 0)
                            lastFraction = Math.Max(lastFraction, Math.Min(1, end / r.ChunkLengthSeconds));
                        Report();
                    }
                },
                line =>
                {
                    var m = ProgressRx.Match(line);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
                    {
                        lastFraction = Math.Max(lastFraction, pct / 100.0);
                        Report();
                    }
                },
                ct).ConfigureAwait(false);

            AtomicFile.TryDelete(Path.Combine(r.WorkingDirectory, argsFile));

            if (result.ExitCode != 0 || !File.Exists(jsonPath))
                throw new ExternalToolException(ExplainFailure(result), details: $"Код завершения {result.ExitCode}.{Environment.NewLine}{result.CommandLine}{Environment.NewLine}{result.StdErrTail}");

            _log?.Debug($"whisper-cli: фрагмент {r.ChunkIndex + 1} распознан за {result.Elapsed.TotalSeconds:0.0} с", stage: "Распознавание");

            var bytes = File.ReadAllBytes(jsonPath);
            AtomicFile.TryDelete(jsonPath);
            return ParseJson(bytes, r.OffsetSeconds, r.ChunkIndex);
        }

        public static List<string> BuildArguments(ChunkTranscriptionRequest r, string baseName)
        {
            var args = new List<string>
            {
                "-m", Path.GetFullPath(r.ModelPath),
                "-l", string.IsNullOrWhiteSpace(r.Language) ? "ru" : r.Language,
                "-f", r.ChunkFileName,
                "-of", baseName,
                "-oj", "-ojf",
                "-pp",
                "-bs", r.BeamSize.ToString(CultureInfo.InvariantCulture)
            };
            if (r.Threads > 0)
            {
                args.Add("-t");
                args.Add(r.Threads.ToString(CultureInfo.InvariantCulture));
            }
            if (!r.UseGpu) args.Add("-ng");
            if (!r.CarryContext)
            {
                args.Add("-mc");
                args.Add("0");
            }
            var prompt = (r.Prompt ?? "").Trim();
            if (prompt.Length > 0)
            {
                args.Add("--prompt");
                args.Add(prompt);
            }
            return args;
        }

        private static string ExplainFailure(ProcessResult result)
        {
            var err = result.StdErrTail ?? "";
            if (err.IndexOf("failed to open", StringComparison.OrdinalIgnoreCase) >= 0 && err.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("failed to initialize whisper context", StringComparison.OrdinalIgnoreCase) >= 0)
                return "whisper.cpp не смог загрузить модель. Проверьте файл модели в настройках.";
            if (err.IndexOf("failed to allocate", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("out of memory", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Недостаточно памяти для модели распознавания. Выберите модель меньшего размера или закройте другие программы.";
            if (err.IndexOf("failed to read audio", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("failed to open audio", StringComparison.OrdinalIgnoreCase) >= 0)
                return "whisper.cpp не смог прочитать подготовленный аудиофрагмент.";
            if (err.IndexOf("unknown argument", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("response file", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Версия whisper-cli несовместима с VidLex (нужна whisper.cpp 1.8 или новее).";
            return $"whisper-cli завершился с ошибкой (код {result.ExitCode}).";
        }

        /// <summary>
        /// Разбор JSON (-ojf). Файл читается как Latin-1 (байт = символ), чтобы токены,
        /// разрезающие многобайтовые символы UTF-8, корректно склеивались в слова.
        /// </summary>
        public static List<TranscriptSegment> ParseJson(byte[] bytes, double offsetSeconds, int chunkIndex)
        {
            var raw = Latin1.GetString(bytes);
            // whisper-cli не экранирует управляющие символы внутри строк — заменяем их пробелами.
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw) sb.Append(c < 0x20 ? ' ' : c);
            var root = JObject.Parse(sb.ToString());

            var segments = new List<TranscriptSegment>();
            foreach (var seg in (root["transcription"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var from = (double?)seg["offsets"]?["from"] ?? 0;
                var to = (double?)seg["offsets"]?["to"] ?? from;
                var segment = new TranscriptSegment
                {
                    Start = offsetSeconds + from / 1000.0,
                    End = offsetSeconds + to / 1000.0,
                    Text = DecodeUtf8((string)seg["text"] ?? "").Trim(),
                    ChunkIndex = chunkIndex
                };

                var tokens = seg["tokens"] as JArray;
                if (tokens != null && tokens.Count > 0)
                    segment.Words = BuildWords(tokens, offsetSeconds);
                if (segment.Words.Count == 0 && segment.Text.Length > 0)
                    segment.Words = EstimateWords(segment.Text, segment.Start, segment.End);

                if (segment.Words.Count > 0 || segment.Text.Length > 0)
                    segments.Add(segment);
            }
            return segments;
        }

        private static List<TimedWord> BuildWords(JArray tokens, double offset)
        {
            var words = new List<TimedWord>();
            var current = new StringBuilder();
            double start = 0, end = 0, probSum = 0;
            int probCount = 0;

            void Flush()
            {
                var text = DecodeUtf8(current.ToString()).Trim();
                if (text.Length > 0)
                {
                    words.Add(new TimedWord
                    {
                        Text = text,
                        Start = start,
                        End = Math.Max(start, end),
                        Probability = probCount > 0 ? probSum / probCount : double.NaN
                    });
                }
                current.Clear();
                probSum = 0;
                probCount = 0;
            }

            foreach (var t in tokens.OfType<JObject>())
            {
                var text = (string)t["text"] ?? "";
                var id = (int?)t["id"] ?? 0;
                if (id >= 50257 || (text.StartsWith("[_", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal)))
                    continue;
                var t0 = offset + ((double?)t["offsets"]?["from"] ?? 0) / 1000.0;
                var t1 = offset + ((double?)t["offsets"]?["to"] ?? 0) / 1000.0;
                var p = (double?)t["p"];

                bool startsWord = text.Length > 0 && text[0] == ' ';
                if (startsWord && current.Length > 0) Flush();
                if (current.Length == 0) start = t0;
                current.Append(text);
                end = t1;
                if (p.HasValue)
                {
                    probSum += p.Value;
                    probCount++;
                }
            }
            if (current.Length > 0) Flush();
            return words;
        }

        /// <summary>Если токенов нет, время слов распределяется пропорционально длине (помечается как оценка).</summary>
        public static List<TimedWord> EstimateWords(string text, double start, double end)
        {
            var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<TimedWord>();
            if (parts.Length == 0) return result;
            int totalChars = parts.Sum(p => p.Length + 1);
            double pos = 0;
            double span = Math.Max(0, end - start);
            foreach (var p in parts)
            {
                var s = start + span * pos / totalChars;
                pos += p.Length + 1;
                var e = start + span * pos / totalChars;
                result.Add(new TimedWord { Text = p, Start = s, End = e, TimeEstimated = true });
            }
            return result;
        }

        private static string DecodeUtf8(string latin1)
        {
            return Utf8Strict.GetString(Latin1.GetBytes(latin1));
        }
    }
}
