using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FastGen.Core
{
    public enum TokenKind
    {
        Word,
        Construct,   // {…}
        Permutation, // […]
        Protected    // HTML-тег, ссылка, e-mail — пропускаются
    }

    public struct TextToken
    {
        public TokenKind Kind;
        public TextRange Range;

        public TextToken(TokenKind kind, int start, int end)
        {
            Kind = kind;
            Range = new TextRange(start, end);
        }

        public bool IsValid { get { return Range.Length > 0; } }
    }

    /// <summary>
    /// Слова и навигация по тексту шаблона. Конструкции {…} считаются одним «токеном»,
    /// перестановки […], HTML-теги и ссылки пропускаются.
    /// </summary>
    public static class TextNav
    {
        private static readonly Regex ProtectedRegex = new Regex(
            @"<[^<>\r\n]{1,500}>" +                                      // HTML-теги
            @"|(?:https?://|www\.)[^\s<>""{}\[\]|]+" +                   // ссылки
            @"|[\w.+-]+@[\w-]+(?:\.[\w-]+)+" +                            // e-mail
            @"|\b[\w-]+(?:\.[\w-]+)*\.(?:ru|рф|su|com|net|org|info|biz|pro|io|me|online|site|club|top|xyz|ua|kz|by|uz|shop|store)\b(?:/[^\s<>""{}\[\]|]*)?",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c);
        }

        private static bool IsJoiner(char c)
        {
            return c == '-' || c == '\'' || c == '’';
        }

        public static bool HasLetter(string s)
        {
            if (s == null) return false;
            foreach (char c in s)
                if (char.IsLetter(c)) return true;
            return false;
        }

        /// <summary>
        /// Слово, содержащее символ в позиции pos (или стоящее сразу слева от каретки).
        /// Дефис/апостроф внутри слова считаются его частью: «онлайн-кинотеатр».
        /// </summary>
        public static TextRange WordAt(string text, int pos)
        {
            if (string.IsNullOrEmpty(text)) return default(TextRange);
            if (pos > text.Length) pos = text.Length;
            if (pos < 0) pos = 0;

            int c = pos;
            if (c >= text.Length || !IsWordChar(text[c]))
            {
                if (c > 0 && IsWordChar(text[c - 1])) c = c - 1;
                else return default(TextRange);
            }
            return ExpandWord(text, c);
        }

        private static TextRange ExpandWord(string text, int c)
        {
            int start = c;
            while (true)
            {
                if (start > 0 && IsWordChar(text[start - 1])) { start--; continue; }
                if (start > 1 && IsJoiner(text[start - 1]) && IsWordChar(text[start - 2])) { start -= 2; continue; }
                break;
            }

            int end = c + 1;
            while (true)
            {
                if (end < text.Length && IsWordChar(text[end])) { end++; continue; }
                if (end + 1 < text.Length && IsJoiner(text[end]) && IsWordChar(text[end + 1])) { end += 2; continue; }
                break;
            }
            return new TextRange(start, end);
        }

        /// <summary>
        /// Области, которые обходятся как единое целое: конструкции {…} и […] верхнего уровня,
        /// а также HTML-теги, ссылки и e-mail вне конструкций. Отсортированы по началу.
        /// </summary>
        public static List<TextToken> GetBlocks(string text)
        {
            var blocks = new List<TextToken>();
            if (string.IsNullOrEmpty(text)) return blocks;

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '{' || c == '[')
                {
                    int close = SpinSyntax.FindMatchingClose(text, i);
                    if (close > i)
                    {
                        blocks.Add(new TextToken(c == '{' ? TokenKind.Construct : TokenKind.Permutation, i, close + 1));
                        i = close + 1;
                        continue;
                    }
                }
                i++;
            }

            var result = new List<TextToken>(blocks);
            foreach (Match m in ProtectedRegex.Matches(text))
            {
                if (Overlaps(blocks, m.Index, m.Index + m.Length)) continue;
                result.Add(new TextToken(TokenKind.Protected, m.Index, m.Index + m.Length));
            }
            result.Sort((a, b) => a.Range.Start.CompareTo(b.Range.Start));
            return result;
        }

        private static bool Overlaps(List<TextToken> blocks, int start, int end)
        {
            foreach (var b in blocks)
                if (start < b.Range.End && end > b.Range.Start) return true;
            return false;
        }

        /// <summary>Индекс блока, содержащего позицию pos, иначе -1 (блоки отсортированы).</summary>
        public static int BlockIndexAt(List<TextToken> blocks, int pos)
        {
            int lo = 0, hi = blocks.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var r = blocks[mid].Range;
                if (pos < r.Start) hi = mid - 1;
                else if (pos >= r.End) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        /// <summary>Следующий токен (слово или конструкция), начинающийся не раньше from.</summary>
        public static TextToken NextToken(string text, int from, List<TextToken> blocks = null)
        {
            if (string.IsNullOrEmpty(text)) return default(TextToken);
            if (blocks == null) blocks = GetBlocks(text);
            if (from < 0) from = 0;

            int i = from;
            while (i < text.Length)
            {
                int bi = BlockIndexAt(blocks, i);
                if (bi >= 0)
                {
                    var b = blocks[bi];
                    if (b.Kind == TokenKind.Construct && b.Range.Start >= from) return b;
                    i = b.Range.End;
                    continue;
                }

                if (IsWordChar(text[i]))
                {
                    var w = ExpandWord(text, i);
                    if (w.Start < from)
                    {
                        // находимся в середине слова — пропускаем его хвост
                        i = w.End;
                        continue;
                    }
                    return new TextToken(TokenKind.Word, w.Start, w.End);
                }
                i++;
            }
            return default(TextToken);
        }

        /// <summary>Предыдущий токен (слово или конструкция), заканчивающийся не позже before.</summary>
        public static TextToken PrevToken(string text, int before, List<TextToken> blocks = null)
        {
            if (string.IsNullOrEmpty(text)) return default(TextToken);
            if (blocks == null) blocks = GetBlocks(text);
            if (before > text.Length) before = text.Length;

            int i = before - 1;
            while (i >= 0)
            {
                int bi = BlockIndexAt(blocks, i);
                if (bi >= 0)
                {
                    var b = blocks[bi];
                    if (b.Kind == TokenKind.Construct && b.Range.End <= before) return b;
                    i = b.Range.Start - 1;
                    continue;
                }

                if (IsWordChar(text[i]))
                {
                    var w = ExpandWord(text, i);
                    if (w.End > before)
                    {
                        i = w.Start - 1;
                        continue;
                    }
                    return new TextToken(TokenKind.Word, w.Start, w.End);
                }
                i--;
            }
            return default(TextToken);
        }

        /// <summary>
        /// Поправляет выделение мышью:
        ///  1) убирает по краям пробелы и «висящие» запятые / точки с запятой;
        ///  2) начало или конец посреди слова — захватывает слово целиком («елодрамы … мистик» → «мелодрамы … мистика»);
        ///  3) выделение выходит за пределы конструкции {…} или […] — расширяет его до её скобок.
        /// Выделение внутри одной конструкции расширяется только до целых слов.
        /// </summary>
        public static TextRange SnapSelection(string text, int start, int end)
        {
            if (string.IsNullOrEmpty(text)) return new TextRange(start, end);
            start = Math.Max(0, Math.Min(start, text.Length));
            end = Math.Max(start, Math.Min(end, text.Length));

            while (start < end && IsEdgeJunk(text[start])) start++;
            while (end > start && IsEdgeJunk(text[end - 1])) end--;
            if (end <= start) return new TextRange(start, start);

            // слова целиком (с дефисом: «онлайн-кинотеатр»)
            if (IsWordChar(text[start])) start = Math.Min(start, WordAt(text, start).Start);
            if (IsWordChar(text[end - 1])) end = Math.Max(end, WordAt(text, end - 1).End);

            var blocks = GetBlocks(text);

            // начало внутри конструкции, а конец — за ней: берём конструкцию целиком
            int bi = BlockIndexAt(blocks, start);
            if (bi >= 0 && blocks[bi].Kind != TokenKind.Protected)
            {
                var r = blocks[bi].Range;
                if (start > r.Start && end >= r.End) start = r.Start;
            }

            // конец внутри конструкции, а начало — до неё
            bi = BlockIndexAt(blocks, end - 1);
            if (bi >= 0 && blocks[bi].Kind != TokenKind.Protected)
            {
                var r = blocks[bi].Range;
                if (end < r.End && start <= r.Start) end = r.End;
            }

            return new TextRange(start, end);
        }

        private static bool IsEdgeJunk(char c)
        {
            return char.IsWhiteSpace(c) || c == ',' || c == ';';
        }

        /// <summary>Следующая конструкция {…} верхнего уровня, начинающаяся не раньше from.</summary>
        public static TextToken NextConstruct(string text, int from, List<TextToken> blocks = null)
        {
            if (blocks == null) blocks = GetBlocks(text);
            foreach (var b in blocks)
                if (b.Kind == TokenKind.Construct && b.Range.Start >= from) return b;
            return default(TextToken);
        }

        /// <summary>Предыдущая конструкция {…} верхнего уровня, заканчивающаяся не позже before.</summary>
        public static TextToken PrevConstruct(string text, int before, List<TextToken> blocks = null)
        {
            if (blocks == null) blocks = GetBlocks(text);
            for (int k = blocks.Count - 1; k >= 0; k--)
                if (blocks[k].Kind == TokenKind.Construct && blocks[k].Range.End <= before) return blocks[k];
            return default(TextToken);
        }
    }
}
