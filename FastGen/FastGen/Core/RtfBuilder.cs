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
    /// <summary>Цвета подсветки шаблона (RGB 0xRRGGBB). ConstructBack &lt; 0 — без фона у конструкций.</summary>
    public sealed class RtfPalette
    {
        public int Text, Construct, FirstVariant, Brace, Pipe, Perm, Tag, Connector, ErrorBack;
        public int ConstructBack = -1;

        /// <summary>Как было: чёрный текст, синие конструкции, красные скобки.</summary>
        public static readonly RtfPalette Classic = new RtfPalette
        {
            Text = 0x000000, Construct = 0x0000FF, FirstVariant = 0x0000FF, Brace = 0xFF0000, Pipe = 0xFF0000,
            Perm = 0xFF00FF, Tag = 0xFF0000, Connector = 0x008000, ErrorBack = 0xFFFF00
        };

        public static readonly RtfPalette Light = new RtfPalette
        {
            Text = 0x1F2328, Construct = 0x0550AE, FirstVariant = 0x033D8B, Brace = 0xCF222E, Pipe = 0xBC4C00,
            Perm = 0x8250DF, Tag = 0xBF3989, Connector = 0x1A7F37, ErrorBack = 0xFFD33D, ConstructBack = 0xEEF4FF
        };

        public static readonly RtfPalette Dark = new RtfPalette
        {
            Text = 0xDCDFE4, Construct = 0x7CB7FF, FirstVariant = 0xB4D5FF, Brace = 0xFF7B72, Pipe = 0xFFA657,
            Perm = 0xD2A8FF, Tag = 0xF778BA, Connector = 0x7EE787, ErrorBack = 0x7A5F00, ConstructBack = 0x232F42
        };
    }

    public static class RtfBuilder
    {
        // номера цветов в \colortbl
        private const byte Black = 1, Blue = 2, Red = 3, Green = 4, Magenta = 5;
        private const byte PipeColor = 7, FirstColor = 8, TagColor = 9;
        private const int HighlightYellow = 6, HighlightConstruct = 10;

        private static readonly Regex AngleRegex = new Regex(@"<[^<>\r\n]{1,200}>", RegexOptions.Compiled);
        private static readonly Regex ConnectorRegex = new Regex(@" (?:и|или) ", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string Build(string text, string fontName, float fontSizePt)
        {
            return Build(text, fontName, fontSizePt, RtfPalette.Classic, 1F);
        }

        /// <param name="lineSpacing">межстрочный интервал: 1 — одинарный, 1.25 — на четверть больше</param>
        public static string Build(string text, string fontName, float fontSizePt, RtfPalette palette, float lineSpacing)
        {
            text = text ?? string.Empty;
            if (palette == null) palette = RtfPalette.Classic;
            int n = text.Length;
            var color = new byte[n];
            var bold = new bool[n];
            var mark = new byte[n];   // 0 — без фона, HighlightConstruct, HighlightYellow
            for (int i = 0; i < n; i++) color[i] = Black;

            var pairs = new List<KeyValuePair<int, int>>();
            var unmatched = new List<int>();
            SpinSyntax.FindBracketPairs(text, pairs, unmatched);

            // 1) всё внутри {…} — синим (и лёгкий фон, если он есть в палитре)
            bool tint = palette.ConstructBack >= 0;
            foreach (var p in pairs)
            {
                if (text[p.Key] != '{') continue;
                for (int i = p.Key + 1; i < p.Value; i++) color[i] = Blue;
                if (tint)
                    for (int i = p.Key; i <= p.Value; i++) mark[i] = HighlightConstruct;
            }

            // 2) исходный (первый) вариант каждой конструкции — жирным
            foreach (var p in pairs)
            {
                if (text[p.Key] != '{') continue;
                int end = FirstTopLevelPipe(text, p.Key + 1, p.Value);
                for (int i = p.Key + 1; i < end; i++)
                {
                    bold[i] = true;
                    if (color[i] == Blue) color[i] = FirstColor;
                }
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
                for (int i = m.Index; i < m.Index + m.Length; i++) { color[i] = TagColor; bold[i] = true; }

            // 5) все '|' — красные
            for (int i = 0; i < n; i++)
                if (text[i] == '|') { color[i] = PipeColor; bold[i] = false; }

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
                color[u] = Red; bold[u] = true; mark[u] = HighlightYellow;
            }

            return Emit(text, color, bold, mark, fontName, fontSizePt, palette, lineSpacing);
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

        private static void AppendColor(StringBuilder sb, int rgb)
        {
            sb.Append(@"\red").Append((rgb >> 16) & 0xFF)
              .Append(@"\green").Append((rgb >> 8) & 0xFF)
              .Append(@"\blue").Append(rgb & 0xFF).Append(';');
        }

        private static string Emit(string text, byte[] color, bool[] bold, byte[] mark, string fontName, float sizePt,
                                   RtfPalette pal, float lineSpacing)
        {
            var sb = new StringBuilder(text.Length * 2 + 512);
            sb.Append(@"{\rtf1\ansi\ansicpg1251\deff0\uc1");
            sb.Append(@"{\fonttbl{\f0\fnil\fcharset204 ").Append(EscapeFontName(fontName)).Append(";}}");

            // порядок важен: номера совпадают с константами выше
            sb.Append(@"{\colortbl ;");
            AppendColor(sb, pal.Text);          // 1
            AppendColor(sb, pal.Construct);     // 2
            AppendColor(sb, pal.Brace);         // 3
            AppendColor(sb, pal.Connector);     // 4
            AppendColor(sb, pal.Perm);          // 5
            AppendColor(sb, pal.ErrorBack);     // 6
            AppendColor(sb, pal.Pipe);          // 7
            AppendColor(sb, pal.FirstVariant);  // 8
            AppendColor(sb, pal.Tag);           // 9
            AppendColor(sb, pal.ConstructBack >= 0 ? pal.ConstructBack : 0xFFFFFF); // 10
            sb.Append('}');

            int halfPoints = (int)System.Math.Round(sizePt * 2);
            sb.Append(@"\viewkind4\uc1\pard");
            if (lineSpacing > 1.01F)
                sb.Append(@"\sl").Append(((int)System.Math.Round(240 * lineSpacing)).ToString(CultureInfo.InvariantCulture)).Append(@"\slmult1");
            sb.Append(@"\f0\fs").Append(halfPoints.ToString(CultureInfo.InvariantCulture));

            int curColor = -1;
            int curBold = -1;
            int curMark = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;

                int col = color[i];
                int b = bold[i] ? 1 : 0;
                int m = mark[i];
                if (col != curColor || b != curBold || m != curMark)
                {
                    if (col != curColor) sb.Append(@"\cf").Append(col);
                    if (b != curBold) sb.Append(b == 1 ? @"\b" : @"\b0");
                    if (m != curMark) sb.Append(m != 0 ? @"\highlight" + m : @"\highlight0");
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
