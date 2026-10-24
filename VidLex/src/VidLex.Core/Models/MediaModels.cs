using System;
using System.Collections.Generic;

namespace VidLex.Core.Models
{
    /// <summary>Сведения о медиафайле, полученные от ffprobe.</summary>
    public sealed class MediaInfo
    {
        public TimeSpan? Duration { get; set; }
        public string FormatName { get; set; } = "";
        public List<AudioStreamInfo> AudioStreams { get; } = new List<AudioStreamInfo>();
        public bool HasVideo { get; set; }
    }

    public sealed class AudioStreamInfo
    {
        /// <summary>Порядковый номер среди аудиодорожек (для -map 0:a:N).</summary>
        public int AudioIndex { get; set; }
        /// <summary>Абсолютный индекс потока в контейнере.</summary>
        public int StreamIndex { get; set; }
        public string Codec { get; set; } = "";
        public int Channels { get; set; }
        public int SampleRate { get; set; }
        public string Language { get; set; } = "";
        public string Title { get; set; } = "";
        public bool IsDefault { get; set; }

        public override string ToString()
        {
            var lang = string.IsNullOrEmpty(Language) ? "язык не указан" : Language;
            var title = string.IsNullOrEmpty(Title) ? "" : $", «{Title}»";
            return $"#{AudioIndex} ({Codec}, {Channels} кан., {lang}{title})";
        }
    }

    /// <summary>Интервал тишины в секундах от начала аудио.</summary>
    public struct TimeRange
    {
        public TimeRange(double start, double end)
        {
            Start = start;
            End = end;
        }

        public double Start { get; }
        public double End { get; }
        public double Length => End - Start;
        public double Middle => (Start + End) / 2;

        public override string ToString() => $"{Start:0.00}–{End:0.00}";
    }

    /// <summary>Фрагмент аудио для распознавания.</summary>
    public sealed class AudioChunk
    {
        public int Index { get; set; }
        /// <summary>Начало фрагмента в исходном аудио, секунды.</summary>
        public double Start { get; set; }
        /// <summary>Конец фрагмента (включая перекрытие), секунды.</summary>
        public double End { get; set; }
        /// <summary>«Чистая» граница со следующим фрагментом; сегменты после неё берутся из следующего фрагмента.</summary>
        public double NominalEnd { get; set; }
        /// <summary>true, если граница проходит по паузе (без перекрытия).</summary>
        public bool CutAtSilence { get; set; }
        public double Length => End - Start;
    }

    /// <summary>Слово (или знак препинания, прикреплённый к слову) с временем в исходном видео.</summary>
    public sealed class TimedWord
    {
        public string Text { get; set; } = "";
        /// <summary>Начало, секунды от начала видео.</summary>
        public double Start { get; set; }
        public double End { get; set; }
        /// <summary>Средняя вероятность токенов слова (если известна), иначе NaN.</summary>
        public double Probability { get; set; } = double.NaN;
        /// <summary>true — время рассчитано пропорционально, а не получено от движка.</summary>
        public bool TimeEstimated { get; set; }

        public override string ToString() => $"{Text}@{Start:0.00}";
    }

    /// <summary>Сегмент распознавания, как его вернул движок.</summary>
    public sealed class TranscriptSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = "";
        public List<TimedWord> Words { get; set; } = new List<TimedWord>();
        public int ChunkIndex { get; set; }

        public double AverageProbability
        {
            get
            {
                double sum = 0;
                int n = 0;
                foreach (var w in Words)
                {
                    if (double.IsNaN(w.Probability)) continue;
                    sum += w.Probability;
                    n++;
                }
                return n == 0 ? double.NaN : sum / n;
            }
        }

        public override string ToString() => $"[{Start:0.00}-{End:0.00}] {Text}";
    }

    /// <summary>Готовая фраза (предложение) после постобработки.</summary>
    public sealed class TranscriptSentence
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = "";
        public bool StartEstimated { get; set; }

        public override string ToString() => $"[{Start:0.00}] {Text}";
    }

    /// <summary>Итог постобработки.</summary>
    public sealed class ProcessedTranscript
    {
        public List<TranscriptSentence> Sentences { get; } = new List<TranscriptSentence>();
        public int SpellingCorrections { get; set; }
        public int UnknownWords { get; set; }
        public int SentenceBreaksAdded { get; set; }
        public int RemovedHallucinations { get; set; }
        public bool IsEmpty => Sentences.Count == 0;
    }
}
