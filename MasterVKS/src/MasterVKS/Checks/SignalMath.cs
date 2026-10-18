using System;
using System.Collections.Generic;

namespace MasterVks.Checks
{
    internal static class SignalMath
    {
        /// <summary>Амплитуда частоты <paramref name="frequency"/> в отрезке сигнала (алгоритм Гёрцеля).</summary>
        public static double ToneAmplitude(float[] x, int start, int count, double frequency, int rate)
        {
            if (count <= 0) return 0;
            double w = 2 * Math.PI * frequency / rate;
            double coeff = 2 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            int end = Math.Min(x.Length, start + count);
            for (int i = start; i < end; i++)
            {
                // окно Ханна уменьшает «просачивание» соседних частот
                double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - start) / (count - 1 > 0 ? count - 1 : 1));
                double s = x[i] * win + coeff * s1 - s2;
                s2 = s1;
                s1 = s;
            }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            if (power < 0) power = 0;
            // 4/N — нормировка амплитуды с учётом окна Ханна (усиление 0.5)
            return 4.0 * Math.Sqrt(power) / count;
        }

        public static double Rms(float[] x, int start, int count)
        {
            if (count <= 0) return 0;
            double sum = 0;
            int end = Math.Min(x.Length, start + count);
            for (int i = start; i < end; i++) sum += (double)x[i] * x[i];
            return Math.Sqrt(sum / Math.Max(1, end - start));
        }

        public static double Peak(float[] x, int start, int count)
        {
            float peak = 0;
            int end = Math.Min(x.Length, start + count);
            for (int i = start; i < end; i++)
            {
                float v = Math.Abs(x[i]);
                if (v > peak) peak = v;
            }
            return peak;
        }

        public static double ToDb(double amplitude)
        {
            return amplitude <= 1e-10 ? -200 : 20 * Math.Log10(amplitude);
        }

        public static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0) return double.NaN;
            var sorted = new List<double>(values);
            sorted.Sort();
            double idx = (sorted.Count - 1) * p;
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
        }
    }
}
