using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FastGen.Core
{
    /// <summary>
    /// Строит RTF с подсветкой шаблона одним проходом. Это в десятки раз быстрее,
    /// чем перекрашивать RichTextBox через Select()/SelectionColor, и не дёргает прокрутку.
    ///   {…}  — текст синий, исходный (первый) вариант — жирный синий;
    ///   { } | — красные; [ ] — фиолетовые жирные; &lt;…&gt; — красные жирные;
    ///   « и », « или » — зелёные; непарная скобка — на жёлтом фоне.
    /// </summary>
    public static class RtfBuilder
    {
        private const byte Black = 1, Blue = 2, Red = 3, Green = 4, Magenta = 5;
        private const int HighlightYellow = 6;

        private static readonly Regex AngleRegex = new Regex(@"<[^<>\r\n]{1,200}>", RegexOptions.Compiled);
        private static readonly Regex ConnectorRegex = new Regex(@" (?:и|или) ", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string Build(string text, string fontName, float fontSizePt)
        {
            text = text ?? string.Empty;
            int n = text.Length;
            var color = new byte[n];
            var bold = new bool[n];
            var mark = new bool[n];
            for (int i = 0; i < n; i++) color[i] = Black;

            var pairs = new List<KeyValuePair<int, int>>();
            var unmatched = new List<int>();
            SpinSyntax.FindBracketPairs(text, pairs, unmatched);

            // 1) всё внутри {…} — синим
            foreach (var p in pairs)
            {
                if (text[p.Key] != '{') continue;
                for (int i = p.Key + 1; i < p.Value; i++) color[i] = Blue;
            }

            // 2) исходный (первый) вариант каждой конструкции — жирным
            foreach (var p in pairs)
            {
                if (text[p.Key] != '{') continue;
                int end = FirstTopLevelPipe(text, p.Key + 1, p.Value);
                for (int i = p.Key + 1; i < end; i++) bold[i] = true;
            }

            // 3) « и » / « или » — зелёным
            foreach (Match m in ConnectorRegex.Matches(text))
                for (int i = m.Index; i < m.Index + m.Length; i++) { color[i] = Green; bold[i] = false; }

            // 4) [ ] — фиолетовые жирные, <…> — красные жирные
            foreach (var p in pairs)
            {
                if (text[p.Key] != '[') continue;
                color[p.Key] = Magenta; bold[p.Key] = true;
                color[p.Value] = Magenta; bold[p.Value] = true;
            }
            foreach (Match m in AngleRegex.Matches(text))
                for (int i = m.Index; i < m.Index + m.Length; i++) { color[i] = Red; bold[i] = true; }

            // 5) все '|' — красные
            for (int i = 0; i < n; i++)
                if (text[i] == '|') { color[i] = Red; bold[i] = false; }

            // 6) фигурные скобки — красные жирные
            foreach (var p in pairs)
            {
                if (text[p.Key] != '{') continue;
                color[p.Key] = Red; bold[p.Key] = true;
                color[p.Value] = Red; bold[p.Value] = true;
            }

            // 7) непарные скобки — на жёлтом фоне
            foreach (int u in unmatched)
            {
                color[u] = Red; bold[u] = true; mark[u] = true;
            }

            return Emit(text, color, bold, mark, fontName, fontSizePt);
        }

        private static int FirstTopLevelPipe(string text, int from, int to)
        {
            int depth = 0;
            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (c == '{' || c == '[') depth++;
                else if ((c == '}' || c == ']') && depth > 0) depth--;
                else if (c == '|' && depth == 0) return i;
            }
            return to;
        }

        private static string Emit(string text, byte[] color, bool[] bold, bool[] mark, string fontName, float sizePt)
        {
            var sb = new StringBuilder(text.Length * 2 + 512);
            sb.Append(@"{\rtf1\ansi\ansicpg1251\deff0\uc1");
            sb.Append(@"{\fonttbl{\f0\fmodern\fprq1\fcharset204 ").Append(EscapeFontName(fontName)).Append(";}}");
            sb.Append(@"{\colortbl ;\red0\green0\blue0;\red0\green0\blue255;\red255\green0\blue0;\red0\green128\blue0;\red255\green0\blue255;\red255\green255\blue0;}");
            int halfPoints = (int)System.Math.Round(sizePt * 2);
            sb.Append(@"\viewkind4\uc1\pard\f0\fs").Append(halfPoints.ToString(CultureInfo.InvariantCulture));

            int curColor = -1;
            int curBold = -1;
            int curMark = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;

                int col = color[i];
                int b = bold[i] ? 1 : 0;
                int m = mark[i] ? 1 : 0;
                if (col != curColor || b != curBold || m != curMark)
                {
                    if (col != curColor) sb.Append(@"\cf").Append(col);
                    if (b != curBold) sb.Append(b == 1 ? @"\b" : @"\b0");
                    if (m != curMark) sb.Append(m == 1 ? @"\highlight" + HighlightYellow : @"\highlight0");
                    sb.Append(' ');
                    curColor = col; curBold = b; curMark = m;
                }

                AppendChar(sb, c);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendChar(StringBuilder sb, char c)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); return;
                case '{': sb.Append(@"\{"); return;
                case '}': sb.Append(@"\}"); return;
                case '\n': sb.Append("\\par\r\n"); return;
                case '\t': sb.Append(@"\tab "); return;
            }

            if (c < 0x20) return;
            if (c < 0x80)
            {
                sb.Append(c);
                return;
            }
            sb.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
        }

        private static string EscapeFontName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Courier New";
            return name.Replace("\\", "").Replace("{", "").Replace("}", "").Replace(";", "");
        }
    }
}
