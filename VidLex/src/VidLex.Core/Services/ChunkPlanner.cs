using System;
using System.Collections.Generic;
using System.Linq;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>
    /// Делит длинную запись на фрагменты около заданной длины.
    /// Граница ставится в середину ближайшей паузы (слово не разрезается, перекрытие не нужно).
    /// Если паузы рядом нет, фрагменты режутся «жёстко» с перекрытием, а дубли на стыке
    /// убирает <see cref="SegmentMerger"/>.
    /// </summary>
    public static class ChunkPlanner
    {
        /// <summary>Насколько раньше целевой длины можно искать паузу, секунды.</summary>
        public const double SearchBackSeconds = 90;
        /// <summary>Насколько позже целевой длины можно искать паузу, секунды.</summary>
        public const double SearchForwardSeconds = 30;
        /// <summary>Перекрытие при жёстком разрезе, секунды.</summary>
        public const double HardCutOverlapSeconds = 4;
        /// <summary>Минимальная длина паузы, пригодной для разреза, секунды.</summary>
        public const double MinCutSilenceSeconds = 0.3;

        public static List<AudioChunk> Plan(double totalSeconds, IList<TimeRange> silences, double targetSeconds)
        {
            var chunks = new List<AudioChunk>();
            if (totalSeconds <= 0) return chunks;
            targetSeconds = Math.Max(30, targetSeconds);
            var usable = (silences ?? new List<TimeRange>()).Where(s => s.Length >= MinCutSilenceSeconds).OrderBy(s => s.Start).ToList();

            double start = 0;
            int index = 0;
            while (start < totalSeconds - 0.05)
            {
                if (totalSeconds - start <= targetSeconds + SearchForwardSeconds)
                {
                    chunks.Add(new AudioChunk { Index = index, Start = start, End = totalSeconds, NominalEnd = totalSeconds, CutAtSilence = true });
                    break;
                }

                double ideal = start + targetSeconds;
                double windowFrom = Math.Max(start + targetSeconds / 3, ideal - SearchBackSeconds);
                double windowTo = ideal + SearchForwardSeconds;
                var best = usable
                    .Where(s => s.Middle > windowFrom && s.Middle < windowTo)
                    // Предпочитаем более длинные паузы, но штрафуем за удаление от целевой точки.
                    .OrderByDescending(s => Math.Min(s.Length, 2.0) - Math.Abs(s.Middle - ideal) / 60.0)
                    .Cast<TimeRange?>()
                    .FirstOrDefault();

                if (best.HasValue)
                {
                    double cut = best.Value.Middle;
                    chunks.Add(new AudioChunk { Index = index, Start = start, End = cut, NominalEnd = cut, CutAtSilence = true });
                    start = cut;
                }
                else
                {
                    double cut = ideal;
                    chunks.Add(new AudioChunk
                    {
                        Index = index,
                        Start = start,
                        End = Math.Min(totalSeconds, cut + HardCutOverlapSeconds),
                        NominalEnd = cut,
                        CutAtSilence = false
                    });
                    start = cut;
                }
                index++;
            }
            return chunks;
        }
    }

    /// <summary>
    /// Склейка результатов фрагментов в единый поток слов.
    /// На стыке с перекрытием выполняется выравнивание: ищется самое длинное совпадение
    /// последовательностей слов в зоне перекрытия, и повтор берётся только один раз.
    /// </summary>
    public static class SegmentMerger
    {
        /// <summary>
        /// Объединяет сегменты фрагментов. chunkSegments[i] — сегменты i-го фрагмента (времена уже абсолютные).
        /// </summary>
        public static List<TranscriptSegment> Merge(IList<AudioChunk> chunks, IList<List<TranscriptSegment>> chunkSegments)
        {
            var merged = new List<TranscriptSegment>();
            for (int i = 0; i < chunkSegments.Count; i++)
            {
                var current = chunkSegments[i].Select(Clone).ToList();
                if (i == 0 || merged.Count == 0)
                {
                    merged.AddRange(current);
                    continue;
                }

                var prevChunk = chunks[i - 1];
                if (prevChunk.CutAtSilence)
                {
                    // Граница по паузе: фрагменты не перекрываются, ничего выравнивать не нужно.
                    merged.AddRange(current);
                    continue;
                }

                JoinOverlap(merged, current, prevChunk.NominalEnd, prevChunk.End);
            }
            return merged.Where(s => s.Words.Count > 0).ToList();
        }

        private static void JoinOverlap(List<TranscriptSegment> left, List<TranscriptSegment> right, double boundary, double overlapEnd)
        {
            const double margin = 1.0;
            var leftWords = Flatten(left);
            var rightWords = Flatten(right);

            // Хвост левого фрагмента и голова правого в зоне перекрытия (с запасом на неточность времён).
            var leftTail = leftWords.Where(x => x.Word.Start >= boundary - margin).ToList();
            var rightHead = rightWords.Where(x => x.Word.Start <= overlapEnd + margin).ToList();

            int bestLen = 0, bestL = -1, bestR = -1;
            for (int a = 0; a < leftTail.Count; a++)
            {
                for (int b = 0; b < rightHead.Count; b++)
                {
                    int len = 0;
                    while (a + len < leftTail.Count && b + len < rightHead.Count
                           && Normalize(leftTail[a + len].Word.Text) == Normalize(rightHead[b + len].Word.Text)
                           && Normalize(leftTail[a + len].Word.Text).Length > 0)
                        len++;
                    // При равной длине предпочитаем совпадение, ближе всего лежащее к границе.
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestL = a;
                        bestR = b;
                    }
                }
            }

            HashSet<TimedWord> dropLeft;
            HashSet<TimedWord> dropRight;
            // Одно совпавшее короткое слово («и», «в») — слабое доказательство; требуем либо 2+ слова,
            // либо одно слово длиной от 4 букв.
            bool reliable = bestLen >= 2 || (bestLen == 1 && Normalize(leftTail[bestL].Word.Text).Length >= 4);
            if (reliable)
            {
                // Левый фрагмент оставляем до конца совпадения, правый — после него.
                dropLeft = new HashSet<TimedWord>(leftTail.Skip(bestL + bestLen).Select(x => x.Word));
                dropRight = new HashSet<TimedWord>(rightHead.Take(bestR + bestLen).Select(x => x.Word));
            }
            else
            {
                // Совпадение не найдено — делим по середине перекрытия по времени.
                double mid = (boundary + overlapEnd) / 2;
                dropLeft = new HashSet<TimedWord>(leftWords.Where(x => x.Word.Start >= mid).Select(x => x.Word));
                dropRight = new HashSet<TimedWord>(rightWords.Where(x => x.Word.Start < mid).Select(x => x.Word));
            }

            foreach (var s in left) s.Words.RemoveAll(dropLeft.Contains);
            foreach (var s in right) s.Words.RemoveAll(dropRight.Contains);
            left.RemoveAll(s => s.Words.Count == 0);
            foreach (var s in left.Concat(right)) RefreshSegment(s);
            left.AddRange(right.Where(s => s.Words.Count > 0));
        }

        private static List<(TranscriptSegment Segment, TimedWord Word)> Flatten(IEnumerable<TranscriptSegment> segments)
        {
            return segments.SelectMany(s => s.Words.Select(w => (s, w))).ToList();
        }

        private static void RefreshSegment(TranscriptSegment s)
        {
            if (s.Words.Count == 0) return;
            s.Start = s.Words[0].Start;
            s.End = Math.Max(s.Words[s.Words.Count - 1].End, s.Start);
            s.Text = string.Join(" ", s.Words.Select(w => w.Text));
        }

        /// <summary>Нормализация для сравнения: нижний регистр, без знаков препинания, ё → е.</summary>
        public static string Normalize(string word)
        {
            var sb = new System.Text.StringBuilder(word.Length);
            foreach (var c in word.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c == 'ё' ? 'е' : c);
            }
            return sb.ToString();
        }

        private static TranscriptSegment Clone(TranscriptSegment s)
        {
            return new TranscriptSegment
            {
                Start = s.Start,
                End = s.End,
                Text = s.Text,
                ChunkIndex = s.ChunkIndex,
                Words = s.Words.Select(w => new TimedWord
                {
                    Text = w.Text,
                    Start = w.Start,
                    End = w.End,
                    Probability = w.Probability,
                    TimeEstimated = w.TimeEstimated
                }).ToList()
            };
        }
    }
}
