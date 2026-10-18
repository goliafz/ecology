using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MasterVks.Recording
{
    /// <summary>
    /// Имена папок и файлов записи.
    /// Без названия:  2026-10-18\2026-10-18_16-57_ВКС.mp4
    /// С названием:   Планёрка_2026-10-18\Планёрка_2026-10-18_16-57_ВКС.mp4
    /// </summary>
    internal static class RecordingNames
    {
        public const int MaxNameLength = 60;

        /// <summary>Убирает из названия символы, которые Windows не разрешает в именах файлов.</summary>
        public static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            char[] invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToArray();
            var sb = new StringBuilder();
            foreach (char c in name.Trim())
                sb.Append(char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string result = sb.ToString();
            while (result.Contains("  ")) result = result.Replace("  ", " ");
            if (result.Length > MaxNameLength) result = result.Substring(0, MaxNameLength);
            // Windows не любит точки и пробелы в конце имени папки.
            return result.TrimEnd('.', ' ').Trim();
        }

        public static string FolderName(string meetingName, DateTime started)
        {
            return Prefix(meetingName) + started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static string BaseName(string meetingName, DateTime started)
        {
            return Prefix(meetingName) + started.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture) + "_ВКС";
        }

        private static string Prefix(string meetingName)
        {
            string clean = Sanitize(meetingName);
            return clean.Length == 0 ? "" : clean + "_";
        }
    }
}
