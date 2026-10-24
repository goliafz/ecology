using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VidLex.Core.Models;

namespace VidLex.Core.Text
{
    /// <summary>
    /// Отсекает типичные «галлюцинации» моделей Whisper на русском:
    /// титры субтитров из обучающих данных, фразы, «услышанные» в тишине, и зацикленные повторы.
    /// Правила консервативные: фраза из «мягкого» списка удаляется, только если почти целиком
    /// приходится на участок тишины, найденный FFmpeg.
    /// </summary>
    public static class HallucinationFilter
    {
        // Почти всегда галлюцинации: подписи авторов субтитров из обучающих данных.
        private static readonly Regex[] HardPatterns =
        {
            new Regex(@"субтитр\w*\s+(сделал|делал|создавал|создал|подготовил|подогнал|делала|создала)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"редактор\s+субтитров", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"корректор\s+[а-яё]\.\s*[а-яё]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"dimatorzok|диматорзок", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"amara\.org", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        // Возможны в реальной речи — удаляются, только если звучат «в тишине».
        private static readonly Regex[] SoftPatterns =
        {
            new Regex(@"^продолжение следует", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"спасибо за (просмотр|внимание)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"подписывайтесь на (канал|наш канал)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"ставьте лайк", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^(всем )?пока[.!]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^(спасибо|благодарю)[.!]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^(\.\.\.|…)$", RegexOptions.Compiled)
        };

        public static List<TranscriptSegment> Apply(IList<TranscriptSegment> segments, IList<TimeRange> silences, out int removed, out List<string> reasons)
        {
            reasons = new List<string>();
            var result = new List<TranscriptSegment>();
            removed = 0;

            foreach (var s in segments)
            {
                var text = (s.Text ?? "").Trim();
                if (HardPatterns.Any(p => p.IsMatch(text)))
                {
                    removed++;
                    reasons.Add($"[{s.Start:0.0} с] титры субтитров: «{Short(text)}»");
                    continue;
                }

                double silentShare = SilentShare(s, silences);
                bool soft = SoftPatterns.Any(p => p.IsMatch(text));
                double prob = s.AverageProbability;
                if (silentShare >= 0.8 && (soft || (!double.IsNaN(prob) && prob < 0.35)))
                {
                    removed++;
                    reasons.Add($"[{s.Start:0.0} с] фраза в тишине ({silentShare:P0} сегмента): «{Short(text)}»");
                    continue;
                }
                result.Add(s);
            }

            // Зацикливание: один и тот же сегмент из 3+ слов подряд 3 и более раз — оставляем одно вхождение.
            var deduped = new List<TranscriptSegment>();
            for (int i = 0; i < result.Count; i++)
            {
                var key = Key(result[i].Text);
                int run = 1;
                while (i + run < result.Count && key.Length > 0 && Key(result[i + run].Text) == key) run++;
                deduped.Add(result[i]);
                if (run >= 3 && key.Split(' ').Length >= 3)
                {
                    removed += run - 1;
                    reasons.Add($"[{result[i].Start:0.0} с] повтор {run} раз подряд: «{Short(result[i].Text)}»");
                    i += run - 1;
                }
                else if (run > 1)
                {
                    for (int k = 1; k < run; k++) deduped.Add(result[i + k]);
                    i += run - 1;
                }
            }
            return deduped;
        }

        /// <summary>Доля длительности сегмента, приходящаяся на тишину.</summary>
        public static double SilentShare(TranscriptSegment s, IList<TimeRange> silences)
        {
            if (silences == null || silences.Count == 0) return 0;
            double len = s.End - s.Start;
            if (len <= 0.05) return 0;
            double covered = 0;
            foreach (var r in silences)
            {
                var a = Math.Max(r.Start, s.Start);
                var b = Math.Min(r.End, s.End);
                if (b > a) covered += b - a;
            }
            return covered / len;
        }

        private static string Key(string text)
        {
            var words = (text ?? "").ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', '!', '?', '…', ';', ':' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words);
        }

        private static string Short(string text) => text.Length > 80 ? text.Substring(0, 80) + "…" : text;
    }
}
