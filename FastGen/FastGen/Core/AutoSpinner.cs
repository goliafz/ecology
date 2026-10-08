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

        /// <summary>Исключения из Exceptions.txt: «рф=РФ».</summary>
        public Dictionary<string, string> Exceptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] DefaultStopWords =
        {
            "в", "во", "на", "за", "под", "над", "из", "к", "ко", "о", "об", "обо", "по", "с", "со", "у",
            "для", "до", "без", "при", "про", "через", "от", "ото", "из-за", "из-под",
            "и", "а", "но", "или", "либо", "да", "ни", "не", "же", "ли", "бы", "ведь", "то", "что", "как"
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
                    if (n == 1 && !IsSpinnableWord(phrase, opt)) break;

                    string lookup = phrase;
                    if (n == 1 && opt.Exceptions.TryGetValue(phrase.ToLowerInvariant(), out var canon))
                        lookup = canon;

                    var cands = PickCandidates(store, lookup, opt);
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

        private static bool IsSpinnableWord(string word, SpinOptions opt)
        {
            if (!TextNav.HasLetter(word)) return false;           // числа не трогаем
            if (word.Length < 2) return false;
            if (opt.StopWords.Contains(word)) return false;
            return true;
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
        public static List<SynonymCandidate> PickCandidates(SynonymStore store, string phrase, SpinOptions opt)
        {
            var all = store.GetCandidates(phrase, opt.IncludeDict);
            if (all.Count == 0) return all;

            bool hasUser = all.Any(c => c.Source == SynonymSource.User);
            IEnumerable<SynonymCandidate> chosen = hasUser
                ? all.Where(c => c.Source == SynonymSource.User)
                : all;

            return chosen
                .Where(c => c.Text.IndexOf('|') < 0 && c.Text.IndexOf('{') < 0 && c.Text.IndexOf('}') < 0)
                .Take(Math.Max(1, opt.MaxSynonyms))
                .ToList();
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
