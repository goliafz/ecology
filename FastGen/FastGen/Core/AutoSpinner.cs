using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FastGen.Core
{
    public sealed class SpinOptions
    {
        /// <summary>Максимум синонимов в одной конструкции (не считая исходного слова).</summary>
        public int MaxSynonyms = 4;

        /// <summary>Максимальная длина фразы в словах, которую ищем в базах целиком.</summary>
        public int MaxPhraseWords = 4;

        /// <summary>Брать ли синонимы из большой базы DICT.DBF.</summary>
        public bool IncludeDict = false;

        /// <summary>Слова, которые никогда не синонимизируются (предлоги, союзы, частицы).</summary>
        public HashSet<string> StopWords = new HashSet<string>(DefaultStopWords, StringComparer.OrdinalIgnoreCase);

        /// <summary>Фразы из BadWord.txt — внутри них ничего не трогаем.</summary>
        public List<string> BadPhrases = new List<string>();

        /// <summary>
        /// Пары соседних слов из ваших шаблонов. Если задано — синоним берётся, только если он уже стоял
        /// рядом с теми же соседями (иначе «собирать кубик» превращается в «копить кубик»).
        /// </summary>
        public ContextIndex Context;

        /// <summary>Не трогать слова с заглавной буквы в середине предложения (имена, названия) и АББРЕВИАТУРЫ.</summary>
        public bool SkipProperNouns = true;

        /// <summary>Исключения из Exceptions.txt: «рф=РФ».</summary>
        public Dictionary<string, string> Exceptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] DefaultStopWords =
        {
            "в", "во", "на", "за", "под", "над", "из", "к", "ко", "о", "об", "обо", "по", "с", "со", "у",
            "для", "до", "без", "при", "про", "через", "от", "ото", "из-за", "из-под",
            "и", "а", "но", "или", "либо", "да", "ни", "не", "же", "ли", "бы", "ведь", "то", "что", "как",
            "кто", "который", "которая", "которое", "которые", "которого", "которой", "которых", "которым",
            "я", "ты", "он", "она", "оно", "мы", "вы", "они", "его", "ее", "её", "их", "им", "ему", "ей", "нас", "вас",
            "уже", "лишь", "тоже", "так"
        };

        public static List<string> LoadBadPhrases(string path)
        {
            var list = new List<string>();
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path, new UTF8Encoding(false)))
            {
                var t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        public static Dictionary<string, string> LoadExceptions(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return dict;
            foreach (var line in File.ReadAllLines(path, new UTF8Encoding(false)))
            {
                var parts = line.Trim().Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;
                var from = parts[0].Trim();
                var to = parts[1].Trim();
                if (from.Length == 0 || to.Length == 0) continue;
                dict[from.ToLowerInvariant()] = to;
            }
            return dict;
        }
    }

    public sealed class SpinResult
    {
        public string Text;
        public int Constructs;
    }

    /// <summary>
    /// Автоматическое размножение: каждое слово/фразу, для которых есть синонимы,
    /// превращает в {исходное|синоним1|синоним2}. Существующие конструкции {…}, […],
    /// HTML-теги и ссылки не трогает.
    /// </summary>
    public static class AutoSpinner
    {
        public static SpinResult Spin(string text, SynonymStore store, SpinOptions opt)
        {
            var res = new SpinResult { Text = text ?? string.Empty };
            if (string.IsNullOrEmpty(text) || store == null) return res;
            if (opt == null) opt = new SpinOptions();

            var blocks = TextNav.GetBlocks(text);
            var blocked = FindBlockedSpans(text, opt.BadPhrases);

            // все слова вне защищённых областей
            var words = new List<TextRange>();
            int pos = 0;
            while (pos < text.Length)
            {
                int bi = TextNav.BlockIndexAt(blocks, pos);
                if (bi >= 0) { pos = blocks[bi].Range.End; continue; }
                if (TextNav.IsWordChar(text[pos]))
                {
                    var w = TextNav.WordAt(text, pos);
                    // слово, «заходящее» в защищённую область, не трогаем
                    if (TextNav.BlockIndexAt(blocks, w.End - 1) < 0)
                        words.Add(w);
                    pos = Math.Max(w.End, pos + 1);
                    continue;
                }
                pos++;
            }

            var sb = new StringBuilder(text.Length * 2);
            int last = 0;
            int k = 0;
            while (k < words.Count)
            {
                var w = words[k];

                // 1) пробуем самую длинную фразу (до MaxPhraseWords слов, только через пробелы)
                int bestCount = 0;
                List<SynonymCandidate> bestCands = null;
                for (int n = Math.Min(opt.MaxPhraseWords, words.Count - k); n >= 1; n--)
                {
                    if (n > 1 && !WordsAdjacent(text, words, k, n)) continue;

                    int start = words[k].Start;
                    int end = words[k + n - 1].End;
                    if (Intersects(blocked, start, end)) continue;

                    string phrase = text.Substring(start, end - start);
                    if (n == 1 && !IsSpinnableWord(text, start, phrase, opt)) break;
                    if (n > 1 && opt.SkipProperNouns && HasProperNoun(text, words, k, n)) continue;

                    string lookup = phrase;
                    if (n == 1 && opt.Exceptions.TryGetValue(phrase.ToLowerInvariant(), out var canon))
                        lookup = canon;

                    GetNeighbors(text, start, end, out string left, out string right);
                    var cands = PickCandidates(store, lookup, opt, left, right);
                    if (cands.Count > 0)
                    {
                        bestCount = n;
                        bestCands = cands;
                        break;
                    }
                }

                if (bestCount == 0)
                {
                    // исключение без синонимов: просто заменяем «рф» → «РФ»
                    string word = w.ToStringIn(text);
                    if (opt.Exceptions.TryGetValue(word.ToLowerInvariant(), out var canonOnly) && !Intersects(blocked, w.Start, w.End))
                    {
                        sb.Append(text, last, w.Start - last);
                        sb.Append(canonOnly);
                        last = w.End;
                    }
                    k++;
                    continue;
                }

                int s0 = words[k].Start;
                int e0 = words[k + bestCount - 1].End;
                string original = text.Substring(s0, e0 - s0);
                if (bestCount == 1 && opt.Exceptions.TryGetValue(original.ToLowerInvariant(), out var canon2))
                    original = canon2;

                var variants = new List<string> { original };
                foreach (var c in bestCands)
                {
                    string v = TextCase.ApplyCase(original, c.Text);
                    if (!variants.Contains(v, StringComparer.OrdinalIgnoreCase)) variants.Add(v);
                }

                sb.Append(text, last, s0 - last);
                if (variants.Count > 1)
                {
                    sb.Append(SpinSyntax.BuildConstruct(variants));
                    res.Constructs++;
                }
                else
                {
                    sb.Append(original);
                }
                last = e0;
                k += bestCount;
            }
            sb.Append(text, last, text.Length - last);
            res.Text = sb.ToString();
            return res;
        }

        private static string ToStringIn(this TextRange r, string text)
        {
            return text.Substring(r.Start, r.Length);
        }

        private static bool IsSpinnableWord(string text, int start, string word, SpinOptions opt)
        {
            if (!TextNav.HasLetter(word)) return false;           // числа не трогаем
            if (word.Length < 2) return false;
            if (opt.StopWords.Contains(word)) return false;
            if (opt.SkipProperNouns)
            {
                if (word.Any(char.IsDigit)) return false;          // 2х2, 1080p, mp3
                if (TextCase.IsAllUpper(word)) return false;       // ТОП, РФ, SEO
                if (TextCase.StartsWithUpper(word) && !IsSentenceStart(text, start)) return false; // Фишер, Москва
                if (word.Any(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z')) return false;      // латиница
            }
            return true;
        }

        private static bool HasProperNoun(string text, List<TextRange> words, int k, int n)
        {
            for (int i = k; i < k + n; i++)
            {
                string w = words[i].ToStringIn(text);
                if (TextCase.IsAllUpper(w) && w.Length > 1) return true;
                if (TextCase.StartsWithUpper(w) && (i > k || !IsSentenceStart(text, words[i].Start))) return true;
            }
            return false;
        }

        /// <summary>Слово стоит в начале предложения (или текста / строки).</summary>
        public static bool IsSentenceStart(string text, int pos)
        {
            int i = pos - 1;
            while (i >= 0 && (text[i] == ' ' || text[i] == '\t' || text[i] == '\u00A0')) i--;
            if (i < 0) return true;
            char c = text[i];
            if (c == '\n' || c == '\r' || c == '.' || c == '!' || c == '?' || c == '…') return true;
            // «Слово» после тега или конструкции в начале строки: <p>Слово, {Здесь|Тут}
            if (c == '>' || c == '}' || c == ']') return IsSentenceStartBefore(text, i);
            return false;
        }

        private static bool IsSentenceStartBefore(string text, int closePos)
        {
            int open;
            if (text[closePos] == '>') open = text.LastIndexOf('<', closePos);
            else open = SpinSyntax.FindMatchingOpen(text, closePos);
            if (open < 0) return false;
            if (text[closePos] == '}' || text[closePos] == ']')
            {
                // внутри конструкции — смотрим, с большой ли буквы её первый вариант
                var inner = text.Substring(open + 1, closePos - open - 1);
                return TextCase.StartsWithUpper(inner);
            }
            return IsSentenceStart(text, open);
        }

        /// <summary>
        /// Соседние слова слева и справа от [start, end) — если между ними только пробелы.
        /// Соседняя конструкция {…} заменяется своим первым (исходным) вариантом.
        /// </summary>
        public static void GetNeighbors(string text, int start, int end, out string left, out string right)
        {
            left = null;
            right = null;

            int i = start - 1;
            while (i >= 0 && (text[i] == ' ' || text[i] == '\u00A0')) i--;
            if (i >= 0)
            {
                if (TextNav.IsWordChar(text[i]))
                {
                    var w = TextNav.WordAt(text, i);
                    left = text.Substring(w.Start, w.Length).ToLowerInvariant();
                }
                else if (text[i] == '}')
                {
                    int open = SpinSyntax.FindMatchingOpen(text, i);
                    if (open >= 0) left = LastWord(SpinSyntax.GetVariants(text.Substring(open, i - open + 1))[0]);
                }
            }

            int j = end;
            while (j < text.Length && (text[j] == ' ' || text[j] == '\u00A0')) j++;
            if (j < text.Length)
            {
                if (TextNav.IsWordChar(text[j]))
                {
                    var w = TextNav.WordAt(text, j);
                    right = text.Substring(w.Start, w.Length).ToLowerInvariant();
                }
                else if (text[j] == '{')
                {
                    int close = SpinSyntax.FindMatchingClose(text, j);
                    if (close > j) right = FirstWord(SpinSyntax.GetVariants(text.Substring(j, close - j + 1))[0]);
                }
            }

            // слова вплотную (без пробела) — это не соседи, а части другого токена
            if (start > 0 && TextNav.IsWordChar(text[start - 1])) left = null;
            if (end < text.Length && TextNav.IsWordChar(text[end])) right = null;
        }

        private static List<string> Words(string s)
        {
            var res = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                if (TextNav.IsWordChar(s[i]))
                {
                    var w = TextNav.WordAt(s, i);
                    res.Add(s.Substring(w.Start, w.Length).ToLowerInvariant());
                    i = w.End;
                }
                else i++;
            }
            return res;
        }

        private static string FirstWord(string s)
        {
            string t = s.TrimStart();
            if (t.Length == 0 || !TextNav.IsWordChar(t[0])) return null;
            var w = Words(t);
            return w.Count > 0 ? w[0] : null;
        }

        private static string LastWord(string s)
        {
            string t = s.TrimEnd();
            if (t.Length == 0 || !TextNav.IsWordChar(t[t.Length - 1])) return null;
            var w = Words(t);
            return w.Count > 0 ? w[w.Count - 1] : null;
        }

        /// <summary>
        /// Подходит ли синоним к соседям по индексу контекста. «Моя база» не проверяется.
        /// Без индекса проверка не выполняется (всё подходит).
        /// </summary>
        public static bool FitsContext(ContextIndex ctx, SynonymCandidate c, string left, string right)
        {
            if (ctx == null || c.Source == SynonymSource.User) return true;
            var w = Words(c.Text);
            if (w.Count == 0) return false;
            if (left == null && right == null)
                return c.Source == SynonymSource.Frequent && c.UsageCount >= 2; // проверить не с чем — только проверенные вами
            bool okLeft = left == null || ctx.HasPair(left, w[0]);
            bool okRight = right == null || ctx.HasPair(w[w.Count - 1], right);
            return okLeft && okRight;
        }

        /// <summary>Слова k..k+n-1 разделены только одиночными пробелами.</summary>
        private static bool WordsAdjacent(string text, List<TextRange> words, int k, int n)
        {
            for (int i = k; i < k + n - 1; i++)
            {
                int gapStart = words[i].End;
                int gapEnd = words[i + 1].Start;
                if (gapEnd - gapStart != 1 || text[gapStart] != ' ') return false;
            }
            return true;
        }

        /// <summary>Синонимы для автоматического размножения: моя база важнее всего.</summary>
        public static List<SynonymCandidate> PickCandidates(SynonymStore store, string phrase, SpinOptions opt,
                                                            string left = null, string right = null)
        {
            var all = store.GetCandidates(phrase, opt.IncludeDict);
            if (all.Count == 0) return all;

            bool hasUser = all.Any(c => c.Source == SynonymSource.User);
            IEnumerable<SynonymCandidate> chosen = hasUser
                ? all.Where(c => c.Source == SynonymSource.User)
                : all;

            return chosen
                .Where(c => c.Text.IndexOf('|') < 0 && c.Text.IndexOf('{') < 0 && c.Text.IndexOf('}') < 0)
                .Where(c => c.Source == SynonymSource.User || !OnlyStopWords(c.Text, opt))   // «для тех» → «для» — нельзя
                .Where(c => FitsContext(opt.Context, c, left, right))
                .Take(Math.Max(1, opt.MaxSynonyms))
                .ToList();
        }

        private static bool OnlyStopWords(string text, SpinOptions opt)
        {
            var w = Words(text);
            return w.Count == 0 || w.All(x => opt.StopWords.Contains(x));
        }

        public static List<TextRange> FindBlockedSpans(string text, List<string> phrases)
        {
            var spans = new List<TextRange>();
            if (string.IsNullOrEmpty(text) || phrases == null) return spans;

            foreach (var phrase in phrases)
            {
                var words = Regex.Split(phrase.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
                if (words.Length == 0) continue;

                string pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"\s+", words.Select(Regex.Escape)) + @"(?![\p{L}\p{N}])";
                foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    spans.Add(new TextRange(m.Index, m.Index + m.Length));
            }
            return spans;
        }

        private static bool Intersects(List<TextRange> spans, int start, int end)
        {
            foreach (var s in spans)
                if (start < s.End && end > s.Start) return true;
            return false;
        }
    }
}
