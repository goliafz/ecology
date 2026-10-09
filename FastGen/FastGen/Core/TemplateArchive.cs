using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FastGen.Core
{
    /// <summary>
    /// Дописывает готовые шаблоны в ResultDB.txt, чтобы они участвовали в проверке соседей и в «Сборке».
    /// Один и тот же текст дважды не пишется: отпечатки уже добавленных текстов лежат в «ResultDB.txt.added».
    /// </summary>
    public static class TemplateArchive
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string HashesPath(string resultPath)
        {
            return resultPath + ".added";
        }

        /// <summary>Непустые строки шаблона (как их пишет «Анализ»).</summary>
        public static List<string> TemplateLines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => l.Trim().Length > 0)
                .ToList();
        }

        /// <summary>
        /// Дописывает шаблон в конец файла. false — нечего добавлять (нет конструкций) или этот текст уже добавлен.
        /// </summary>
        public static bool Append(string resultPath, string text)
        {
            var lines = TemplateLines(text);
            if (lines.Count == 0 || !lines.Any(l => l.IndexOf('{') >= 0)) return false;

            string hash = Hash(string.Join("\n", lines));
            string hashesPath = HashesPath(resultPath);
            if (File.Exists(hashesPath) && File.ReadLines(hashesPath).Any(h => h.Trim() == hash)) return false;

            string dir = Path.GetDirectoryName(Path.GetFullPath(resultPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            if (File.Exists(resultPath) && !EndsWithNewLine(resultPath)) sb.Append("\r\n");
            foreach (var l in lines) sb.Append(l).Append("\r\n");
            File.AppendAllText(resultPath, sb.ToString(), Utf8NoBom);
            File.AppendAllText(hashesPath, hash + "\r\n", Utf8NoBom);
            return true;
        }

        private static bool EndsWithNewLine(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return true;
                fs.Seek(-1, SeekOrigin.End);
                return fs.ReadByte() == '\n';
            }
        }

        private static string Hash(string s)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
