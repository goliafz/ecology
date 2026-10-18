using System;
using System.Collections.Generic;
using LetopisVks.Audio;

namespace LetopisVks.Checks
{
    /// <summary>Копит звук всех источников на время проверки.</summary>
    internal sealed class AudioCollector
    {
        private readonly object _lock = new object();
        private readonly List<float> _mic1 = new List<float>();
        private readonly List<float> _mic2 = new List<float>();
        private readonly List<float> _sys = new List<float>();
        private int _mic1Real, _mic2Real, _sysReal, _blocks;

        public void OnBlock(AudioBlock block)
        {
            lock (_lock)
            {
                _mic1.AddRange(block.Mic1);
                _mic2.AddRange(block.Mic2);
                for (int i = 0; i < block.Frames; i++) _sys.Add((block.Sys[i * 2] + block.Sys[i * 2 + 1]) * 0.5f);
                _mic1Real += block.Mic1Real;
                _mic2Real += block.Mic2Real;
                _sysReal += block.SysReal;
                _blocks++;
            }
        }

        /// <summary>Текущая позиция (в сэмплах) — чтобы отметить начало и конец сигнала.</summary>
        public int Position
        {
            get { lock (_lock) return _mic1.Count; }
        }

        public Snapshot Take()
        {
            lock (_lock)
            {
                return new Snapshot
                {
                    Mic1 = _mic1.ToArray(),
                    Mic2 = _mic2.ToArray(),
                    Sys = _sys.ToArray(),
                    Mic1Real = _mic1Real,
                    Mic2Real = _mic2Real,
                    SysReal = _sysReal,
                    TotalFrames = _blocks * AudioEngine.BlockFrames
                };
            }
        }

        internal sealed class Snapshot
        {
            public float[] Mic1, Mic2, Sys;
            public int Mic1Real, Mic2Real, SysReal, TotalFrames;
        }
    }

    /// <summary>Результат анализа одного источника.</summary>
    internal sealed class SourceAnalysis
    {
        public double RealShare;
        public bool AllZero;
        public double NoiseFloorDb;
        public double LoudDb;
        public double PeakDb;
        public double ClipShare;
        public bool Hum;
        public double HumDb;
        /// <summary>Насколько тестовый сигнал громче фона, дБ (NaN — сигнал не проигрывался).</summary>
        public double ToneSnrDb = double.NaN;
        /// <summary>Момент появления тестового сигнала, сэмпл (-1 — не найден).</summary>
        public int ToneOnset = -1;
    }

    internal static class AudioAnalyzer
    {
        private const int Rate = AudioEngine.Rate;

        /// <param name="toneStart">Сэмпл, с которого проигрывался сигнал (-1 — не проигрывался).</param>
        public static SourceAnalysis Analyze(float[] x, int realFrames, int totalFrames, int skipFrames,
                                             int toneStart, int toneEnd, double[] toneFrequencies)
        {
            var a = new SourceAnalysis();
            int usable = Math.Max(0, x.Length - skipFrames);
            a.RealShare = totalFrames > 0 ? realFrames / (double)totalFrames : 0;

            double peak = SignalMath.Peak(x, skipFrames, usable);
            a.PeakDb = SignalMath.ToDb(peak);
            a.AllZero = peak == 0;

            // Уровни по окнам 100 мс
            int win = Rate / 10;
            var levels = new List<double>();
            for (int s = skipFrames; s + win <= x.Length; s += win) levels.Add(SignalMath.ToDb(SignalMath.Rms(x, s, win)));
            a.NoiseFloorDb = levels.Count > 0 ? SignalMath.Percentile(levels, 0.1) : -200;
            a.LoudDb = levels.Count > 0 ? SignalMath.Percentile(levels, 0.9) : -200;

            int clipped = 0;
            for (int i = skipFrames; i < x.Length; i++)
                if (Math.Abs(x[i]) >= 0.999f) clipped++;
            a.ClipShare = usable > 0 ? clipped / (double)usable : 0;

            // Гул сети 50 Гц и гармоники: окна по 200 мс (ровно 10 периодов)
            int humWin = Rate / 5;
            var humRatios = new List<double>();
            var humLevels = new List<double>();
            for (int s = skipFrames; s + humWin <= x.Length; s += humWin)
            {
                if (toneStart >= 0 && s + humWin > toneStart && s < toneEnd + Rate / 2) continue;
                double rms = SignalMath.Rms(x, s, humWin);
                if (rms <= 1e-9) continue;
                double h1 = SignalMath.ToneAmplitude(x, s, humWin, 50, Rate);
                double h2 = SignalMath.ToneAmplitude(x, s, humWin, 100, Rate);
                double h3 = SignalMath.ToneAmplitude(x, s, humWin, 150, Rate);
                double humRms = Math.Sqrt((h1 * h1 + h2 * h2 + h3 * h3) / 2);
                humRatios.Add(humRms / rms);
                humLevels.Add(SignalMath.ToDb(humRms));
            }
            if (humRatios.Count > 0)
            {
                double ratio = SignalMath.Percentile(humRatios, 0.5);
                a.HumDb = SignalMath.Percentile(humLevels, 0.5);
                a.Hum = ratio > 0.6 && a.HumDb > -60;
            }

            if (toneStart >= 0 && toneFrequencies != null && toneFrequencies.Length > 0)
                AnalyzeTone(x, a, toneStart, toneEnd, toneFrequencies);

            return a;
        }

        private static void AnalyzeTone(float[] x, SourceAnalysis a, int toneStart, int toneEnd, double[] freqs)
        {
            int win = Rate / 50; // 20 мс
            int searchEnd = Math.Min(x.Length, toneEnd + Rate * 6 / 10);
            var baseline = new List<double>();
            double best = 0;
            int onset = -1;

            int baseStart = Math.Max(0, toneStart - Rate / 2);
            for (int s = baseStart; s + win <= toneStart; s += win) baseline.Add(ToneEnergy(x, s, win, freqs));
            double floor = baseline.Count > 0 ? SignalMath.Percentile(baseline, 0.5) : 0;
            floor = Math.Max(floor, 1e-7);

            for (int s = toneStart; s + win <= searchEnd; s += win)
            {
                double e = ToneEnergy(x, s, win, freqs);
                if (e > best) best = e;
                if (onset < 0 && e > floor * 10) onset = s;
            }
            a.ToneSnrDb = SignalMath.ToDb(best / floor);
            a.ToneOnset = onset;
        }

        private static double ToneEnergy(float[] x, int start, int count, double[] freqs)
        {
            double max = 0;
            foreach (double f in freqs) max = Math.Max(max, SignalMath.ToneAmplitude(x, start, count, f, Rate));
            return max;
        }
    }
}
