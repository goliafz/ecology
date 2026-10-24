using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VidLex.Core.Infrastructure
{
    public static class PathUtil
    {
        private static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

        public static StringComparison PathComparison => IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static StringComparer PathComparer => IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>Относительный путь (аналог Path.GetRelativePath, которого нет в .NET Framework).</summary>
        public static string GetRelativePath(string root, string fullPath)
        {
            if (string.IsNullOrEmpty(root)) return fullPath;
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var f = Path.GetFullPath(fullPath);
            if (f.StartsWith(r, PathComparison))
                return f.Substring(r.Length);

            var rootUri = new Uri(r);
            var fileUri = new Uri(f);
            if (!string.Equals(rootUri.Scheme, fileUri.Scheme, StringComparison.OrdinalIgnoreCase))
                return f;
            var rel = Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString());
            return rel.Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>Короткий стабильный хеш строки (для имён временных папок и идентификаторов).</summary>
        public static string StableHash(string text)
        {
            var normalized = IsWindows ? (text ?? "").ToUpperInvariant() : (text ?? "");
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static bool IsAscii(string s) => s != null && s.All(c => c < 128);

        /// <summary>Заменяет недопустимые для имени файла символы на «_».</summary>
        public static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToArray();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(invalid.Contains(c) || c < 32 ? '_' : c);
            var result = sb.ToString().Trim().TrimEnd('.');
            return string.IsNullOrEmpty(result) ? "_" : result;
        }

        public static bool IsSameOrSubPath(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)) return false;
            var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return c.StartsWith(p, PathComparison);
        }

        public static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);
        }
    }

    public static class TimeFormat
    {
        /// <summary>ЧЧ:ММ:СС (часы не ограничены 24).</summary>
        public static string Clock(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        public static string Clock(double seconds) => Clock(TimeSpan.FromSeconds(Math.Max(0, seconds)));

        /// <summary>Человекочитаемая длительность: «1 ч 05 мин», «3 мин 20 с», «45 с».</summary>
        public static string Human(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} ч {t.Minutes:00} мин";
            if (t.TotalMinutes >= 1) return $"{t.Minutes} мин {t.Seconds:00} с";
            return $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))} с";
        }

        /// <summary>Разбор «чч:мм:сс.ммм» или «чч:мм:сс,ммм» в секунды.</summary>
        public static bool TryParseClock(string s, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Trim().Replace(',', '.').Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    return false;
                total = total * 60 + v;
            }
            seconds = total;
            return true;
        }
    }

    /// <summary>Сравнение «по-человечески»: «Урок 2» раньше «Урок 10».</summary>
    public sealed class NaturalStringComparer : System.Collections.Generic.IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new NaturalStringComparer();

        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x.Substring(si, i - si).TrimStart('0');
                    var b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    int c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++;
                    j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
