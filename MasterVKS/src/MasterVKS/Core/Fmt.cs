using System;
using System.Globalization;

namespace MasterVks.Core
{
    /// <summary>Человеческие подписи: размеры, длительности, склонения.</summary>
    internal static class Fmt
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public static string Plural(long n, string one, string few, string many)
        {
            long a = Math.Abs(n) % 100;
            long b = a % 10;
            if (a > 10 && a < 20) return many;
            if (b > 1 && b < 5) return few;
            if (b == 1) return one;
            return many;
        }

        public static string Size(long bytes)
        {
            if (bytes < 0) bytes = 0;
            const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
            if (bytes >= gb) return (bytes / gb).ToString(bytes >= 100 * gb ? "0" : "0.0", Ru) + " ГБ";
            if (bytes >= mb) return (bytes / mb).ToString("0", Ru) + " МБ";
            if (bytes >= kb) return (bytes / kb).ToString("0", Ru) + " КБ";
            return bytes + " Б";
        }

        public static string Timer(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        /// <summary>«1 ч 20 мин», «45 мин», «больше 100 ч».</summary>
        public static string Duration(TimeSpan t)
        {
            if (t.TotalHours >= 100) return "больше 100 ч";
            if (t.TotalMinutes < 1) return "меньше минуты";
            int h = (int)t.TotalHours;
            int m = t.Minutes;
            if (h == 0) return m + " мин";
            if (m == 0) return h + " ч";
            return h + " ч " + m + " мин";
        }

        public static string Db(double db)
        {
            if (double.IsNegativeInfinity(db) || db < -120) return "−∞ дБ";
            return db.ToString("0", Ru).Replace("-", "−") + " дБ";
        }
    }
}
