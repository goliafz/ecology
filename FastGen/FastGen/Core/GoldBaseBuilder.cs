using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FastGen.Core
{
    /// <summary>
    /// Сборка GoldBase.txt из готовых шаблонов (ResultDB.txt).
    /// Берутся только «листовые» конструкции {a|b|c} без вложенных скобок. Набор отбрасывается целиком, если
    /// хоть один вариант: длиннее 4 слов; содержит точку, запятую, «;», кавычки, круглые скобки, адрес сайта;
    /// содержит слова «это», «и», «или»; пустой.
    /// Набор, целиком входящий в более широкий, не сохраняется; более узкие наборы удаляются при появлении широкого.
    /// </summary>
    public sealed class GoldBaseBuilder
    {
        private static readonly Regex SiteRegex = new Regex(@"[A-Za-z0-9\-]+\.[A-Za-z0-9\-]{2,}", RegexOptions.Compiled);
        private static readonly char[] WordSeparators = { ' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '(', ')', '«', '»', '"', '\'' };

        // набор (отсортированные слова через '|') → его слова
        private readonly Dictionary<string, string[]> _sets = new Dictionary<string, string[]>(StringComparer.Ordinal);
        // слово → ключи наборов, где оно встречается (для быстрого поиска над-/подмножеств)
        private readonly Dictionary<string, HashSet<string>> _byWord = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        public int TotalConstructs { get; private set; }
        public int AcceptedSets { get; private set; }
        public int Count { get { return _sets.Count; } }

        public IEnumerable<string> Lines
        {
            get { return _sets.Keys.OrderBy(k => k, StringComparer.Ordinal); }
        }

        /// <summary>Загружает существующую базу (режим «Дополнить базу»).</summary>
        public void AddExistingLines(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                var words = line.Split('|').Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0)
                                .Distinct(StringComparer.Ordinal).ToList();
                if (words.Count < 2) continue;
                words.Sort(StringComparer.Ordinal);
                AddSet(words);
            }
        }

        /// <summary>Разбирает текст шаблонов и добавляет подходящие наборы.</summary>
        public void AddTemplateText(string text)
        {
            var stack = new Stack<int>();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{')
                {
                    stack.Push(i);
                    continue;
                }
                if (c != '}' || stack.Count == 0) continue;

                int start = stack.Pop();
                int len = i - start - 1;
                if (len <= 0) continue;

                string inside = text.Substring(start + 1, len);
                if (inside.IndexOf('{') >= 0 || inside.IndexOf('}') >= 0) continue; // не листовая
                if (inside.IndexOf('|') < 0) continue;

                TotalConstructs++;

                var words = ParseSet(inside);
                if (words == null || words.Count < 2) continue;

                if (AddSet(words)) AcceptedSets++;
            }
        }

        private static List<string> ParseSet(string inside)
        {
            var cleaned = new List<string>();
            foreach (var raw in inside.Split('|'))
            {
                if (raw.IndexOf('.') >= 0) return null;

                string v = raw.Trim().Trim(' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '«', '»', '"', '\'', '(', ')');
                if (v.Length == 0) return null;
                if (v.IndexOfAny(new[] { '(', ')', ',', ';', '"', '«', '»', '\'', '[', ']', '<', '>' }) >= 0) return null;

                var words = v.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0 || words.Length > 4) return null;

                string lower = string.Join(" ", words).ToLowerInvariant();
                var lw = lower.Split(' ');
                if (lw.Contains("это") || lw.Contains("и") || lw.Contains("или")) return null;
                if (SiteRegex.IsMatch(v)) return null;

                cleaned.Add(lower);
            }

            var res = cleaned.Distinct(StringComparer.Ordinal).ToList();
            res.Sort(StringComparer.Ordinal);
            return res;
        }

        /// <summary>Добавляет набор с учётом над-/подмножеств. true — набор добавлен.</summary>
        private bool AddSet(List<string> words)
        {
            string key = string.Join("|", words);
            if (_sets.ContainsKey(key)) return false;

            // новый набор входит в существующий? Тогда все его слова есть в том наборе:
            // достаточно проверить наборы, содержащие первое слово.
            if (_byWord.TryGetValue(words[0], out var withFirst))
            {
                foreach (var k in withFirst)
                {
                    var existing = _sets[k];
                    if (existing.Length >= words.Count && words.All(w => Array.BinarySearch(existing, w, StringComparer.Ordinal) >= 0))
                        return false;
                }
            }

            // существующие наборы, целиком входящие в новый, — удаляем
            var newSet = new HashSet<string>(words, StringComparer.Ordinal);
            var toRemove = new List<string>();
            var checkedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in words)
            {
                if (!_byWord.TryGetValue(w, out var keys)) continue;
                foreach (var k in keys)
                {
                    if (!checkedKeys.Add(k)) continue;
                    var existing = _sets[k];
                    if (existing.Length <= words.Count && existing.All(newSet.Contains))
                        toRemove.Add(k);
                }
            }
            foreach (var k in toRemove) RemoveSet(k);

            var arr = words.ToArray(); // уже отсортирован
            _sets[key] = arr;
            foreach (var w in arr)
            {
                if (!_byWord.TryGetValue(w, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    _byWord[w] = keys;
                }
                keys.Add(key);
            }
            return true;
        }

        private void RemoveSet(string key)
        {
            if (!_sets.TryGetValue(key, out var words)) return;
            _sets.Remove(key);
            foreach (var w in words)
                if (_byWord.TryGetValue(w, out var keys)) keys.Remove(key);
        }

        // ------------------------------------------------------------------
        // Анализ папки с шаблонами
        // ------------------------------------------------------------------

        /// <summary>Простая эвристика: кириллицы не меньше латиницы и хотя бы 10 букв.</summary>
        public static bool IsRussianText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            int cyr = 0, latin = 0;
            foreach (char ch in text)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) latin++;
                else if (ch >= 'Ѐ' && ch <= 'ԯ') cyr++;
            }
            return cyr >= 10 && cyr >= latin;
        }

        /// <summary>
        /// Собирает все русские шаблоны из папки (с подпапками) в один файл, без пустых строк.
        /// Возвращает (найдено файлов, записано файлов).
        /// </summary>
        public static int[] CollectTemplates(string folder, string mask, string resultPath, Action<string> log)
        {
            var files = Directory.GetFiles(folder, mask, SearchOption.AllDirectories);
            if (log != null) log("Найдено файлов по маске: " + files.Length);

            string fullResult = Path.GetFullPath(resultPath);
            int written = 0, processed = 0;
            string tmp = resultPath + ".tmp";

            using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                foreach (var file in files)
                {
                    processed++;
                    if (string.Equals(Path.GetFullPath(file), fullResult, StringComparison.OrdinalIgnoreCase)) continue;

                    string text;
                    try
                    {
                        text = FileText.ReadAuto(file);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log("Пропущен (" + ex.Message + "): " + file);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (!IsRussianText(text.Length > 2000 ? text.Substring(0, 2000) : text)) continue;

                    foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line)) writer.WriteLine(line.TrimEnd());
                    written++;

                    if (log != null && (processed % 50 == 0 || processed == files.Length))
                        log("Обработано: " + processed + "/" + files.Length + ", записано: " + written);
                }
            }

            FileText.CommitTemp(tmp, resultPath);
            return new[] { files.Length, written };
        }
    }
}
