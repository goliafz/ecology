using System;
using System.Collections.Generic;
using System.Linq;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>Статистика завершённого задания (только активное время, паузы не учитываются).</summary>
    public sealed class JobStatistics
    {
        public double AudioSeconds { get; set; }
        public double ExtractSeconds { get; set; }
        public double SilenceSeconds { get; set; }
        public double RecognitionSeconds { get; set; }
        public double TextSeconds { get; set; }
        public double SaveSeconds { get; set; }
        public double TotalSeconds { get; set; }
        public bool Recognized { get; set; }

        /// <summary>Скорость: секунд обработки на секунду аудио.</summary>
        public double RealTimeFactor => AudioSeconds > 0 ? TotalSeconds / AudioSeconds : double.NaN;

        public override string ToString()
        {
            return $"аудио {TimeFormat.Clock(AudioSeconds)}; извлечение {ExtractSeconds:0.0} с; паузы {SilenceSeconds:0.0} с; " +
                   $"распознавание {RecognitionSeconds:0.0} с; текст {TextSeconds:0.00} с; сохранение {SaveSeconds:0.00} с; " +
                   $"всего {TotalSeconds:0.0} с" + (AudioSeconds > 0 ? $" (×{RealTimeFactor:0.00} от длительности)" : "");
        }
    }

    /// <summary>Живые данные о задании, которое обрабатывается прямо сейчас.</summary>
    public sealed class LiveJobProgress
    {
        public double AudioSeconds;
        /// <summary>Сколько секунд аудио уже распознано (подтверждено движком).</summary>
        public double RecognizedSeconds;
        /// <summary>Активное время распознавания этого задания.</summary>
        public double RecognitionElapsed;
        /// <summary>Активное время задания целиком.</summary>
        public double JobElapsed;
    }

    public sealed class QueueProgressSnapshot
    {
        public double OverallPercent { get; set; }
        public int Total { get; set; }
        public int Done { get; set; }
        public int Remaining { get; set; }
        public TimeSpan? RemainingTime { get; set; }
        /// <summary>«Расчёт времени…», «≈ 12 мин 30 с» и т. п.</summary>
        public string RemainingText { get; set; } = "";
        /// <summary>На чём основана оценка (для подсказки в интерфейсе).</summary>
        public string Basis { get; set; } = "";
    }

    /// <summary>
    /// Оценка времени по фактической статистике сессии.
    /// Скорость = активное время обработки / длительность аудио (взвешенно по длительности,
    /// с экспоненциальным сглаживанием последних заданий). Показываемое значение дополнительно
    /// сглаживается, чтобы не «прыгало» после каждого обновления.
    /// </summary>
    public sealed class ProgressEstimationService
    {
        private const double EmaAlpha = 0.35;
        private const double MinLiveAudioSeconds = 30;
        private const double MinLiveElapsedSeconds = 10;

        private readonly object _sync = new object();
        private readonly List<JobStatistics> _history = new List<JobStatistics>();
        private double _emaRtf = double.NaN;
        private double _displayed = double.NaN;
        private DateTime _displayedAt;
        private readonly Func<DateTime> _clock;

        public ProgressEstimationService(Func<DateTime> clock = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public IReadOnlyList<JobStatistics> History
        {
            get { lock (_sync) return _history.ToList(); }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _history.Clear();
                _emaRtf = double.NaN;
                _displayed = double.NaN;
            }
        }

        /// <summary>Сбрасывает только сглаживание показа (например, после паузы).</summary>
        public void ResetDisplay()
        {
            lock (_sync) _displayed = double.NaN;
        }

        public void AddCompleted(JobStatistics stats)
        {
            if (stats == null || !stats.Recognized || stats.AudioSeconds < 5 || stats.TotalSeconds <= 0) return;
            lock (_sync)
            {
                _history.Add(stats);
                var rtf = stats.RealTimeFactor;
                _emaRtf = double.IsNaN(_emaRtf) ? rtf : EmaAlpha * rtf + (1 - EmaAlpha) * _emaRtf;
            }
        }

        /// <summary>Сессионная скорость: среднее, взвешенное по длительности, смешанное со сглаженной последней.</summary>
        public double SessionRealTimeFactor
        {
            get
            {
                lock (_sync)
                {
                    if (_history.Count == 0) return double.NaN;
                    var weighted = _history.Sum(h => h.TotalSeconds) / _history.Sum(h => h.AudioSeconds);
                    return 0.5 * weighted + 0.5 * _emaRtf;
                }
            }
        }

        public QueueProgressSnapshot Compute(IList<VideoJob> jobs, IList<LiveJobProgress> running, int parallelism)
        {
            var snap = new QueueProgressSnapshot { Total = jobs.Count };
            if (jobs.Count == 0)
            {
                snap.RemainingText = "—";
                return snap;
            }

            snap.Done = jobs.Count(j => j.IsFinished);
            snap.Remaining = jobs.Count - snap.Done;

            // Общий прогресс взвешивается по длительности; неизвестная длительность — средняя по известным.
            var known = jobs.Where(j => j.Duration.HasValue && j.Duration.Value.TotalSeconds > 0).Select(j => j.Duration.Value.TotalSeconds).ToList();
            double avg = known.Count > 0 ? known.Average() : 60;
            double totalWeight = 0, doneWeight = 0;
            foreach (var j in jobs)
            {
                var w = j.Duration?.TotalSeconds > 0 ? j.Duration.Value.TotalSeconds : avg;
                totalWeight += w;
                if (j.IsFinished) doneWeight += w;
                else if (j.Status != JobStatus.Waiting && j.Status != JobStatus.Stopped) doneWeight += w * j.Progress / 100.0;
            }
            snap.OverallPercent = totalWeight > 0 ? Math.Min(100, 100 * doneWeight / totalWeight) : 0;

            if (snap.Remaining == 0)
            {
                snap.RemainingTime = TimeSpan.Zero;
                snap.RemainingText = "Готово";
                return snap;
            }

            // Скорость: сессионная статистика, а при её отсутствии — живая скорость текущего задания.
            double rtf = SessionRealTimeFactor;
            string basis = double.IsNaN(rtf) ? "" : $"по {History.Count} завершённым видео";
            var live = (running ?? new List<LiveJobProgress>())
                .Where(r => r.RecognizedSeconds >= MinLiveAudioSeconds && r.RecognitionElapsed >= MinLiveElapsedSeconds)
                .ToList();
            if (double.IsNaN(rtf) && live.Count > 0)
            {
                // Только скорость распознавания + 10% на остальные этапы.
                rtf = live.Average(r => r.RecognitionElapsed / r.RecognizedSeconds) * 1.1;
                basis = "по скорости распознавания текущего видео";
            }

            if (double.IsNaN(rtf) || rtf <= 0)
            {
                snap.RemainingText = "Расчёт времени…";
                snap.Basis = "Недостаточно данных: оценка появится после распознавания первых минут аудио";
                lock (_sync) _displayed = double.NaN;
                return snap;
            }

            double remainingAudio = 0;
            foreach (var j in jobs)
            {
                if (j.IsFinished) continue;
                var d = j.Duration?.TotalSeconds > 0 ? j.Duration.Value.TotalSeconds : avg;
                remainingAudio += d * (1 - j.Progress / 100.0);
            }

            int effectiveParallel = Math.Max(1, Math.Min(parallelism, snap.Remaining));
            double raw = remainingAudio * rtf / effectiveParallel;

            double shown;
            lock (_sync)
            {
                var now = _clock();
                if (double.IsNaN(_displayed))
                {
                    shown = raw;
                }
                else
                {
                    // Естественный обратный отсчёт + плавное подтягивание к новой оценке.
                    var countdown = Math.Max(0, _displayed - (now - _displayedAt).TotalSeconds);
                    shown = 0.85 * countdown + 0.15 * raw;
                }
                _displayed = shown;
                _displayedAt = now;
            }

            snap.RemainingTime = TimeSpan.FromSeconds(shown);
            snap.RemainingText = "≈ " + TimeFormat.Human(TimeSpan.FromSeconds(RoundForDisplay(shown)));
            snap.Basis = $"Оценка {basis}: скорость обработки ×{rtf:0.00} от длительности видео";
            return snap;
        }

        /// <summary>Округление: оценка не должна выглядеть точнее, чем она есть.</summary>
        public static double RoundForDisplay(double seconds)
        {
            if (seconds < 60) return Math.Ceiling(seconds / 5) * 5;
            if (seconds < 600) return Math.Round(seconds / 15) * 15;
            if (seconds < 3600) return Math.Round(seconds / 60) * 60;
            return Math.Round(seconds / 300) * 300;
        }
    }
}
