using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>Получение длительности и списка дорожек через ffprobe (JSON-вывод).</summary>
    public sealed class MediaProbeService : IMediaProbeService
    {
        private readonly IProcessRunner _runner;
        private readonly Func<ToolPaths> _tools;

        public MediaProbeService(IProcessRunner runner, Func<ToolPaths> tools)
        {
            _runner = runner;
            _tools = tools;
        }

        public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
        {
            var ffprobe = _tools()?.Ffprobe;
            if (string.IsNullOrEmpty(ffprobe))
                throw new ExternalToolException("Не найден ffprobe — невозможно прочитать параметры видео.");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Файл не найден: «{path}»", path);

            var json = new StringBuilder();
            var spec = new ProcessSpec
            {
                FileName = ffprobe,
                Arguments = { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path },
                Priority = WorkerPriority.Normal,
                TailLines = 30
            };
            var result = await _runner.RunAsync(spec, line => json.AppendLine(line), null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new ExternalToolException(
                    "Не удалось прочитать медиаданные: файл повреждён или его формат не поддерживается декодером FFmpeg.",
                    details: $"ffprobe завершился с кодом {result.ExitCode}.{Environment.NewLine}{result.StdErrTail}");
            }
            return Parse(json.ToString());
        }

        public static MediaInfo Parse(string json)
        {
            var info = new MediaInfo();
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                throw new ExternalToolException("ffprobe вернул некорректные данные.", ex, json.Length > 2000 ? json.Substring(0, 2000) : json);
            }

            var format = root["format"] as JObject;
            info.FormatName = (string)format?["format_name"] ?? "";
            var duration = ParseDouble((string)format?["duration"]);

            int audioIndex = 0;
            double maxStreamDuration = 0;
            foreach (var stream in (root["streams"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var type = (string)stream["codec_type"];
                var sd = ParseDouble((string)stream["duration"]);
                if (sd.HasValue) maxStreamDuration = Math.Max(maxStreamDuration, sd.Value);

                if (type == "video")
                {
                    var attached = (int?)stream["disposition"]?["attached_pic"] ?? 0;
                    if (attached == 0) info.HasVideo = true;
                }
                else if (type == "audio")
                {
                    info.AudioStreams.Add(new AudioStreamInfo
                    {
                        AudioIndex = audioIndex++,
                        StreamIndex = (int?)stream["index"] ?? -1,
                        Codec = (string)stream["codec_name"] ?? "",
                        Channels = (int?)stream["channels"] ?? 0,
                        SampleRate = (int)(ParseDouble((string)stream["sample_rate"]) ?? 0),
                        Language = (string)stream["tags"]?["language"] ?? "",
                        Title = (string)stream["tags"]?["title"] ?? "",
                        IsDefault = ((int?)stream["disposition"]?["default"] ?? 0) == 1
                    });
                }
            }

            if (!duration.HasValue && maxStreamDuration > 0) duration = maxStreamDuration;
            if (duration.HasValue && duration.Value > 0) info.Duration = TimeSpan.FromSeconds(duration.Value);
            return info;
        }

        /// <summary>
        /// Выбор дорожки: явный номер из настроек, иначе русская, иначе дорожка по умолчанию, иначе первая.
        /// </summary>
        public static AudioStreamInfo SelectAudioStream(MediaInfo info, int requestedIndex, out string explanation)
        {
            explanation = "";
            var streams = info.AudioStreams;
            if (streams.Count == 0) return null;

            if (requestedIndex >= 0)
            {
                var requested = streams.FirstOrDefault(s => s.AudioIndex == requestedIndex);
                if (requested != null)
                {
                    explanation = $"Выбрана аудиодорожка {requested} согласно настройкам";
                    return requested;
                }
                explanation = $"Аудиодорожки №{requestedIndex} нет (всего {streams.Count}), используется первая: {streams[0]}";
                return streams[0];
            }

            if (streams.Count == 1) return streams[0];

            var russian = streams.FirstOrDefault(s => IsRussian(s.Language));
            if (russian != null)
            {
                explanation = $"В файле {streams.Count} аудиодорожки; выбрана русская: {russian}";
                return russian;
            }
            var def = streams.FirstOrDefault(s => s.IsDefault && s.Channels > 0);
            if (def != null)
            {
                explanation = $"В файле {streams.Count} аудиодорожки; выбрана дорожка по умолчанию: {def}";
                return def;
            }
            var first = streams.FirstOrDefault(s => s.Channels > 0) ?? streams[0];
            explanation = $"В файле {streams.Count} аудиодорожки; выбрана первая подходящая: {first}";
            return first;
        }

        private static bool IsRussian(string lang)
        {
            var l = (lang ?? "").ToLowerInvariant();
            return l == "rus" || l == "ru" || l == "russian";
        }

        private static double? ParseDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s == "N/A") return null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }
    }
}
