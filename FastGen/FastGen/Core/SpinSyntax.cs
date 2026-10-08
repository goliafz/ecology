using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FastGen.Core
{
    /// <summary>
    /// Работа с синтаксисом шаблонов размножения:
    ///   {вариант1|вариант2|...}         — перебор (можно вкладывать);
    ///   [&lt;,&gt; элемент1 | элемент2 | ...]   — перестановка элементов через разделитель;
    ///   элемент с маркером &lt;и&gt; / &lt;или&gt; задаёт разделитель перед последним элементом.
    /// </summary>
    public static class SpinSyntax
    {
        // ---------------- Скобки ----------------

        /// <summary>Позиция парной закрывающей скобки для '{' или '[' в позиции openPos; -1 если нет.</summary>
        public static int FindMatchingClose(string text, int openPos)
        {
            if (text == null || openPos < 0 || openPos >= text.Length) return -1;
            char open = text[openPos];
            char close;
            if (open == '{') close = '}';
            else if (open == '[') close = ']';
            else return -1;

            int depth = 0;
            for (int i = openPos; i < text.Length; i++)
            {
                char c = text[i];
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>Позиция парной открывающей скобки для '}' или ']' в позиции closePos; -1 если нет.</summary>
        public static int FindMatchingOpen(string text, int closePos)
        {
            if (text == null || closePos < 0 || closePos >= text.Length) return -1;
            char close = text[closePos];
            char open;
            if (close == '}') open = '{';
            else if (close == ']') open = '[';
            else return -1;

            int depth = 0;
            for (int i = closePos; i >= 0; i--)
            {
                char c = text[i];
                if (c == close) depth++;
                else if (c == open)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Самая внутренняя конструкция {…}, содержащая символ в позиции charPos
        /// (включая сами скобки). Возвращает false, если символ вне конструкций.
        /// </summary>
        public static bool TryGetInnermostConstruct(string text, int charPos, out TextRange range)
        {
            range = default(TextRange);
            if (string.IsNullOrEmpty(text)) return false;
            if (charPos >= text.Length) charPos = text.Length - 1;
            if (charPos < 0) return false;

            int depth = 0;
            int open = -1;
            int startScan = text[charPos] == '}' ? charPos - 1 : charPos;
            for (int i = startScan; i >= 0; i--)
            {
                char c = text[i];
                if (c == '}') depth++;
                else if (c == '{')
                {
                    if (depth == 0) { open = i; break; }
                    depth--;
                }
            }
            if (open < 0) return false;

            int close = FindMatchingClose(text, open);
            if (close < 0 || close < charPos) return false;

            range = new TextRange(open, close + 1);
            return true;
        }

        /// <summary>Самая внешняя конструкция {…}, содержащая символ charPos.</summary>
        public static bool TryGetOutermostConstruct(string text, int charPos, out TextRange range)
        {
            if (!TryGetInnermostConstruct(text, charPos, out range))
                return false;

            while (true)
            {
                int open = FindEnclosingOpen(text, range.Start);
                if (open < 0) break;
                int close = FindMatchingClose(text, open);
                if (close < range.End - 1) break;
                range = new TextRange(open, close + 1);
            }
            return true;
        }

        /// <summary>Ближайшая незакрытая '{' левее позиции pos (не включая pos).</summary>
        private static int FindEnclosingOpen(string text, int pos)
        {
            int depth = 0;
            for (int i = pos - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '}') depth++;
                else if (c == '{')
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return -1;
        }

        /// <summary>
        /// Делит содержимое конструкции (без внешних скобок) по '|' верхнего уровня,
        /// не разрезая вложенные {…} и […].
        /// </summary>
        public static List<string> SplitTopLevel(string inner)
        {
            var result = new List<string>();
            if (inner == null) return result;

            int depth = 0;
            int segStart = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '{' || c == '[') depth++;
                else if ((c == '}' || c == ']') && depth > 0) depth--;
                else if (c == '|' && depth == 0)
                {
                    result.Add(inner.Substring(segStart, i - segStart));
                    segStart = i + 1;
                }
            }
            result.Add(inner.Substring(segStart));
            return result;
        }

        /// <summary>Варианты конструкции "{a|b|c}" (строка целиком, со скобками).</summary>
        public static List<string> GetVariants(string construct)
        {
            if (construct == null || construct.Length < 2 || construct[0] != '{' || construct[construct.Length - 1] != '}')
                return new List<string> { construct ?? string.Empty };
            return SplitTopLevel(construct.Substring(1, construct.Length - 2));
        }

        /// <summary>Собирает конструкцию из вариантов. Один вариант — возвращается без скобок.</summary>
        public static string BuildConstruct(IList<string> variants)
        {
            if (variants == null || variants.Count == 0) return string.Empty;
            if (variants.Count == 1) return variants[0];
            return "{" + string.Join("|", variants) + "}";
        }

        // ---------------- Проверка скобок ----------------

        public sealed class ValidationResult
        {
            public bool IsValid { get { return ErrorPosition < 0; } }
            public int ErrorPosition = -1;
            public string Message = string.Empty;
        }

        /// <summary>Проверка парности {} и [].</summary>
        public static ValidationResult Validate(string text)
        {
            var res = new ValidationResult();
            if (string.IsNullOrEmpty(text)) return res;

            var stack = new Stack<int>();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{' || c == '[')
                {
                    stack.Push(i);
                }
                else if (c == '}' || c == ']')
                {
                    char expectedOpen = c == '}' ? '{' : '[';
                    if (stack.Count == 0)
                    {
                        res.ErrorPosition = i;
                        res.Message = "лишняя «" + c + "»";
                        return res;
                    }
                    int open = stack.Pop();
                    if (text[open] != expectedOpen)
                    {
                        res.ErrorPosition = open;
                        res.Message = "«" + text[open] + "» закрыта скобкой «" + c + "»";
                        return res;
                    }
                }
            }
            if (stack.Count > 0)
            {
                int open = stack.Pop();
                // показываем самую раннюю незакрытую
                while (stack.Count > 0) open = stack.Pop();
                res.ErrorPosition = open;
                res.Message = "не закрыта «" + text[open] + "»";
            }
            return res;
        }

        /// <summary>Пары скобок (open, close) и позиции непарных скобок.</summary>
        public static void FindBracketPairs(string text, List<KeyValuePair<int, int>> pairs, List<int> unmatched)
        {
            var stack = new Stack<int>();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{' || c == '[')
                {
                    stack.Push(i);
                }
                else if (c == '}' || c == ']')
                {
                    char expectedOpen = c == '}' ? '{' : '[';
                    if (stack.Count > 0 && text[stack.Peek()] == expectedOpen)
                    {
                        pairs.Add(new KeyValuePair<int, int>(stack.Pop(), i));
                    }
                    else
                    {
                        unmatched.Add(i);
                    }
                }
            }
            while (stack.Count > 0) unmatched.Add(stack.Pop());
        }

        // ---------------- Разбор, подсчёт и генерация ----------------

        private abstract class Node
        {
            public abstract double Count();
            public abstract void Render(StringBuilder sb, Random rnd);
        }

        private sealed class TextNode : Node
        {
            public string Text;
            public override double Count() { return 1; }
            public override void Render(StringBuilder sb, Random rnd) { sb.Append(Text); }
        }

        private sealed class SeqNode : Node
        {
            public readonly List<Node> Items = new List<Node>();

            public override double Count()
            {
                double r = 1;
                foreach (var n in Items) r *= n.Count();
                return r;
            }

            public override void Render(StringBuilder sb, Random rnd)
            {
                foreach (var n in Items) n.Render(sb, rnd);
            }

            public string RenderToString(Random rnd)
            {
                var sb = new StringBuilder();
                Render(sb, rnd);
                return sb.ToString();
            }
        }

        private sealed class ChoiceNode : Node
        {
            public readonly List<SeqNode> Variants = new List<SeqNode>();

            public override double Count()
            {
                double r = 0;
                foreach (var v in Variants) r += v.Count();
                return Math.Max(r, 1);
            }

            public override void Render(StringBuilder sb, Random rnd)
            {
                if (Variants.Count == 0) return;
                Variants[rnd.Next(Variants.Count)].Render(sb, rnd);
            }
        }

        private sealed class PermNode : Node
        {
            public string Separator;      // разделитель между элементами (",")
            public string LastSeparator;  // разделитель перед последним ("и"), может быть null
            public readonly List<SeqNode> Items = new List<SeqNode>();

            public override double Count()
            {
                double r = 1;
                for (int k = 2; k <= Items.Count; k++) r *= k;
                foreach (var it in Items) r *= it.Count();
                return r;
            }

            public override void Render(StringBuilder sb, Random rnd)
            {
                var order = new List<SeqNode>(Items);
                for (int i = order.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(i + 1);
                    var t = order[i]; order[i] = order[j]; order[j] = t;
                }

                for (int k = 0; k < order.Count; k++)
                {
                    if (k > 0)
                    {
                        bool last = k == order.Count - 1;
                        sb.Append(FormatSeparator(last && LastSeparator != null ? LastSeparator : Separator));
                    }
                    sb.Append(order[k].RenderToString(rnd).Trim());
                }
            }
        }

        private static string FormatSeparator(string sep)
        {
            if (string.IsNullOrEmpty(sep)) return ", ";
            string s = sep.Trim();
            if (s.Length == 0) return " ";
            bool wordy = char.IsLetterOrDigit(s[0]);
            return wordy ? " " + s + " " : s + " ";
        }

        private sealed class Parser
        {
            private readonly string _t;
            private int _i;

            public Parser(string text) { _t = text ?? string.Empty; }

            public SeqNode ParseAll()
            {
                return ParseSeq(TerminatorMode.None, false);
            }

            private enum TerminatorMode { None, Choice, Perm }

            private SeqNode ParseSeq(TerminatorMode mode, bool inPermItem)
            {
                var seq = new SeqNode();
                var buf = new StringBuilder();

                while (_i < _t.Length)
                {
                    char c = _t[_i];

                    if (mode == TerminatorMode.Choice && (c == '|' || c == '}')) break;
                    if (mode == TerminatorMode.Perm && (c == '|' || c == ']')) break;

                    if (c == '{' && FindMatchingClose(_t, _i) >= 0)
                    {
                        Flush(seq, buf);
                        seq.Items.Add(ParseChoice());
                        continue;
                    }

                    if (c == '[' && FindMatchingClose(_t, _i) >= 0)
                    {
                        Flush(seq, buf);
                        seq.Items.Add(ParsePerm());
                        continue;
                    }

                    buf.Append(c);
                    _i++;
                }

                Flush(seq, buf);
                return seq;
            }

            private static void Flush(SeqNode seq, StringBuilder buf)
            {
                if (buf.Length == 0) return;
                seq.Items.Add(new TextNode { Text = buf.ToString() });
                buf.Clear();
            }

            private Node ParseChoice()
            {
                var node = new ChoiceNode();
                _i++; // '{'
                while (true)
                {
                    node.Variants.Add(ParseSeq(TerminatorMode.Choice, false));
                    if (_i >= _t.Length) break;
                    char c = _t[_i];
                    _i++;
                    if (c == '}') break;
                    // c == '|' — следующий вариант
                }
                return node;
            }

            private Node ParsePerm()
            {
                var node = new PermNode();
                _i++; // '['

                // заголовок <разделитель>
                int j = _i;
                while (j < _t.Length && _t[j] == ' ') j++;
                if (j < _t.Length && _t[j] == '<')
                {
                    int gt = _t.IndexOf('>', j);
                    if (gt > j && gt - j <= 20)
                    {
                        node.Separator = _t.Substring(j + 1, gt - j - 1);
                        _i = gt + 1;
                    }
                }

                while (true)
                {
                    var item = ParseSeq(TerminatorMode.Perm, true);
                    string marker = ExtractLastSeparatorMarker(item);
                    if (marker != null) node.LastSeparator = marker;
                    if (!IsBlank(item)) node.Items.Add(item);

                    if (_i >= _t.Length) break;
                    char c = _t[_i];
                    _i++;
                    if (c == ']') break;
                }
                return node;
            }

            private static bool IsBlank(SeqNode seq)
            {
                foreach (var n in seq.Items)
                {
                    var tn = n as TextNode;
                    if (tn == null || !string.IsNullOrWhiteSpace(tn.Text)) return false;
                }
                return true;
            }

            /// <summary>Убирает из текстовых узлов маркер вида &lt;и&gt; и возвращает его содержимое.</summary>
            private static string ExtractLastSeparatorMarker(SeqNode item)
            {
                string found = null;
                foreach (var n in item.Items)
                {
                    var tn = n as TextNode;
                    if (tn == null) continue;
                    int lt = tn.Text.IndexOf('<');
                    while (lt >= 0)
                    {
                        int gt = tn.Text.IndexOf('>', lt + 1);
                        if (gt < 0 || gt - lt > 20) break;
                        string inner = tn.Text.Substring(lt + 1, gt - lt - 1);
                        if (inner.IndexOf('<') >= 0) break;
                        found = inner;
                        tn.Text = tn.Text.Remove(lt, gt - lt + 1);
                        lt = tn.Text.IndexOf('<');
                    }
                }
                return found;
            }
        }

        /// <summary>Количество возможных вариантов текста (может быть очень большим).</summary>
        public static double CountVariants(string template)
        {
            return new Parser(template).ParseAll().Count();
        }

        /// <summary>Генерирует один случайный текст по шаблону.</summary>
        public static string Generate(string template, Random rnd)
        {
            var seq = new Parser(template).ParseAll();
            var sb = new StringBuilder();
            seq.Render(sb, rnd ?? new Random());
            return sb.ToString();
        }

        /// <summary>Человекочитаемое количество вариантов.</summary>
        public static string FormatCount(double count)
        {
            if (double.IsInfinity(count) || double.IsNaN(count)) return "> 1e308";
            if (count < 1e15) return count.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
            return count.ToString("0.0e+0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Полуинтервал [Start, End).</summary>
    public struct TextRange
    {
        public int Start;
        public int End;

        public TextRange(int start, int end)
        {
            Start = start;
            End = end;
        }

        public int Length { get { return End - Start; } }
        public bool IsEmpty { get { return End <= Start; } }
        public bool Contains(int pos) { return pos >= Start && pos < End; }

        public override string ToString() { return "[" + Start + ".." + End + ")"; }
    }
}
