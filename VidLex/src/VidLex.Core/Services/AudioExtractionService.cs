using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>
    /// Извлечение аудио FFmpeg в формат, нужный whisper.cpp: WAV, 16 кГц, моно, PCM 16 бит.
    /// Прогресс берётся из машиночитаемого вывода ffmpeg -progress (out_time_us).
    /// </summary>
    public sealed class AudioExtractionService : IAudioExtractionService
    {
        private readonly IProcessRunner _runner;
        private readonly Func<ToolPaths> _tools;
        private readonly Func<WorkerPriority> _priority;

        public AudioExtractionService(IProcessRunner runner, Func<ToolPaths> tools, Func<WorkerPriority> priority = null)
        {
            _runner = runner;
            _tools = tools;
            _priority = priority ?? (() => WorkerPriority.BelowNormal);
        }

        private string Ffmpeg
        {
            get
            {
                var p = _tools()?.Ffmpeg;
                if (string.IsNullOrEmpty(p)) throw new ExternalToolException("Не найден ffmpeg — извлечение аудио невозможно.");
                return p;
            }
        }

        public async Task ExtractAsync(string inputPath, int audioIndex, string outputWav, TimeSpan? duration, IProgress<double> progress, CancellationToken ct)
        {
            var total = duration?.TotalSeconds ?? 0;
            var spec = new ProcessSpec
            {
                FileName = Ffmpeg,
                Arguments =
                {
                    "-hide_banner", "-nostdin", "-y", "-v", "error",
                    "-i", inputPath,
                    "-map", "0:a:" + audioIndex.ToString(CultureInfo.InvariantCulture),
                    "-vn", "-sn", "-dn",
                    "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav",
                    "-progress", "pipe:1", "-nostats",
                    outputWav
                },
                Priority = _priority(),
                TailLines = 40
            };

            void OnStdOut(string line)
            {
                if (progress == null || total <= 0) return;
                // out_time_us и out_time_ms в ffmpeg оба в микросекундах.
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                {
                    var value = line.Substring(line.IndexOf('=') + 1);
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                        progress.Report(Math.Min(1.0, us / 1_000_000.0 / total));
                }
                else if (line == "progress=end")
                {
                    progress.Report(1.0);
                }
            }

            var result = await _runner.RunAsync(spec, OnStdOut, null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                AtomicFile.TryDelete(outputWav);
                throw new ExternalToolException(
                    "Ошибка извлечения аудио: FFmpeg не смог декодировать звуковую дорожку.",
                    details: $"Код завершения {result.ExitCode}.{Environment.NewLine}{result.CommandLine}{Environment.NewLine}{result.StdErrTail}");
            }
            if (!File.Exists(outputWav) || new FileInfo(outputWav).Length <= 44)
            {
                throw new ExternalToolException(
                    "FFmpeg не создал аудиофайл или аудиодорожка пуста.",
                    details: result.StdErrTail);
            }
            progress?.Report(1.0);
        }

        public async Task ExtractChunkAsync(string sourceWav, double start, double length, string outputWav, CancellationToken ct)
        {
            var spec = new ProcessSpec
            {
                FileName = Ffmpeg,
                Arguments =
                {
                    "-hide_banner", "-nostdin", "-y", "-v", "error",
                    "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                    "-t", length.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", sourceWav,
                    "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav",
                    outputWav
                },
                Priority = _priority()
            };
            var result = await _runner.RunAsync(spec, null, null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(outputWav))
            {
                throw new ExternalToolException(
                    "Не удалось подготовить фрагмент аудио для распознавания.",
                    details: $"Код завершения {result.ExitCode}.{Environment.NewLine}{result.StdErrTail}");
            }
        }
    }

    /// <summary>Поиск пауз фильтром silencedetect. Паузы нужны для разрезания длинных записей, пунктуации и фильтра галлюцинаций.</summary>
    public sealed class SilenceDetectionService : ISilenceDetectionService
    {
        private static readonly Regex StartRx = new Regex(@"silence_start:\s*(-?[0-9.]+)", RegexOptions.Compiled);
        private static readonly Regex EndRx = new Regex(@"silence_end:\s*(-?[0-9.]+)", RegexOptions.Compiled);

        private readonly IProcessRunner _runner;
        private readonly Func<ToolPaths> _tools;
        private readonly Func<WorkerPriority> _priority;

        public SilenceDetectionService(IProcessRunner runner, Func<ToolPaths> tools, Func<WorkerPriority> priority = null)
        {
            _runner = runner;
            _tools = tools;
            _priority = priority ?? (() => WorkerPriority.BelowNormal);
        }

        public async Task<List<TimeRange>> DetectAsync(string wavPath, double thresholdDb, double minSilenceSeconds, double totalSeconds, CancellationToken ct)
        {
            var ffmpeg = _tools()?.Ffmpeg ?? throw new ExternalToolException("Не найден ffmpeg.");
            var lines = new List<string>();
            var filter = string.Format(CultureInfo.InvariantCulture, "silencedetect=noise={0:0.#}dB:d={1:0.###}", thresholdDb, minSilenceSeconds);
            var spec = new ProcessSpec
            {
                FileName = ffmpeg,
                Arguments = { "-hide_banner", "-nostdin", "-nostats", "-v", "info", "-i", wavPath, "-af", filter, "-f", "null", "-" },
                Priority = _priority()
            };
            var result = await _runner.RunAsync(spec, null, line =>
            {
                if (line.IndexOf("silence_", StringComparison.Ordinal) >= 0)
                    lock (lines) lines.Add(line);
            }, ct).ConfigureAwait(false);

            if (result.ExitCode != 0)
                throw new ExternalToolException("Ошибка анализа пауз в аудио.", details: result.StdErrTail);

            return ParseSilences(lines, totalSeconds);
        }

        public static List<TimeRange> ParseSilences(IEnumerable<string> lines, double totalSeconds)
        {
            var result = new List<TimeRange>();
            double? open = null;
            foreach (var line in lines)
            {
                var ms = StartRx.Match(line);
                if (ms.Success)
                {
                    open = Math.Max(0, double.Parse(ms.Groups[1].Value, CultureInfo.InvariantCulture));
                    continue;
                }
                var me = EndRx.Match(line);
                if (me.Success && open.HasValue)
                {
                    var end = double.Parse(me.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (end > open.Value) result.Add(new TimeRange(open.Value, end));
                    open = null;
                }
            }
            if (open.HasValue && totalSeconds > open.Value)
                result.Add(new TimeRange(open.Value, totalSeconds));
            return result;
        }
    }
}
