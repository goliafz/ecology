using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FastGen.Core
{
    /// <summary>Регистр букв и простые преобразования выделенного текста.</summary>
    public static class TextCase
    {
        public static bool IsAllUpper(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int letters = 0;
            foreach (char c in s)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (!char.IsUpper(c)) return false;
            }
            return letters > 0;
        }

        public static bool StartsWithUpper(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (char.IsLetter(c)) return char.IsUpper(c);
            return false;
        }

        private static bool EveryWordCapitalized(string s)
        {
            var words = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return false;
            foreach (var w in words)
            {
                char first = w.FirstOrDefault(char.IsLetter);
                if (first != default(char) && !char.IsUpper(first)) return false;
            }
            return true;
        }

        /// <summary>
        /// Подгоняет регистр синонима под образец из текста:
        ///  «КОМПАНИЯ» → «ФИРМА»; «Компания» → «Фирма»; «Российская Федерация» → «Каждое Слово»;
        ///  иначе синоним остаётся как в базе.
        /// </summary>
        public static string ApplyCase(string template, string synonym)
        {
            if (string.IsNullOrEmpty(synonym) || string.IsNullOrEmpty(template))
                return synonym;

            int letters = template.Count(char.IsLetter);
            if (letters > 1 && IsAllUpper(template))
                return synonym.ToUpper();

            if (StartsWithUpper(template))
                return EveryWordCapitalized(template) ? CapitalizeEachWord(synonym) : CapitalizeFirstLetter(synonym);

            return synonym;
        }

        /// <summary>Первая буква — заглавная, остальное без изменений.</summary>
        public static string CapitalizeFirstLetter(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetter(s[i]))
                    return s.Substring(0, i) + char.ToUpper(s[i]) + s.Substring(i + 1);
            }
            return s;
        }

        /// <summary>Первая буква — строчная, остальное без изменений (аббревиатуры не трогаем).</summary>
        public static string LowerFirstLetter(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsLetter(s[i])) continue;
                // «РФ», «США» — не трогаем
                if (i + 1 < s.Length && char.IsUpper(s[i + 1])) return s;
                return s.Substring(0, i) + char.ToLower(s[i]) + s.Substring(i + 1);
            }
            return s;
        }

        public static string CapitalizeEachWord(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            bool atWordStart = true;
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    sb.Append(atWordStart ? char.ToUpper(c) : c);
                    atWordStart = false;
                }
                else
                {
                    sb.Append(c);
                    atWordStart = c == ' ' || c == '-' || c == '\t';
                }
            }
            return sb.ToString();
        }

        /// <summary>Сохраняет пробелы по краям, преобразует середину.</summary>
        private static string TransformCore(string selected, Func<string, string> f)
        {
            if (string.IsNullOrEmpty(selected)) return selected;
            int left = 0, right = selected.Length - 1;
            while (left <= right && char.IsWhiteSpace(selected[left])) left++;
            while (right >= left && char.IsWhiteSpace(selected[right])) right--;
            if (left > right) return selected;

            string core = selected.Substring(left, right - left + 1);
            string res = f(core);
            if (res == null) return null;
            return selected.Substring(0, left) + res + selected.Substring(right + 1);
        }

        /// <summary>Кнопка «Текст»: «каждое слово с заглавной» ↔ «всё строчными».</summary>
        public static string ToggleTitleCase(string selected)
        {
            return TransformCore(selected, core =>
            {
                string capitalized = CapitalizeEachWord(core.ToLower());
                return core == capitalized ? core.ToLower() : capitalized;
            });
        }

        /// <summary>Кнопка «ТЕКСТ»: ВЕРХНИЙ ↔ нижний регистр.</summary>
        public static string ToggleUpper(string selected)
        {
            return TransformCore(selected, core =>
            {
                var letters = core.Where(char.IsLetter).ToArray();
                if (letters.Length == 0) return core;
                return letters.All(char.IsUpper) ? core.ToLower() : core.ToUpper();
            });
        }

        /// <summary>
        /// Ротация двух частей: «Семечки и кокосы» → «{Семечки и кокосы|Кокосы и семечки}».
        /// Возвращает null, если разделитель не найден или частей не две.
        /// </summary>
        public static string Rotate(string selected)
        {
            return TransformCore(selected, core =>
            {
                string delim = null;
                foreach (var d in new[] { " или ", " и ", ", ", "; " })
                {
                    if (core.Contains(d)) { delim = d; break; }
                }
                if (delim == null) return null;

                var parts = core.Split(new[] { delim }, StringSplitOptions.None);
                if (parts.Length != 2) return null;

                string first = parts[0].Trim();
                string second = parts[1].Trim();
                if (first.Length == 0 || second.Length == 0) return null;

                string firstRight = first, secondRight = second;
                if (StartsWithUpper(first))
                {
                    secondRight = CapitalizeFirstLetter(second);
                    firstRight = LowerFirstLetter(first);
                }

                return "{" + first + delim + second + "|" + secondRight + delim + firstRight + "}";
            });
        }

        private static string DetectSeparator(string text)
        {
            return text.Contains(";") ? ";" : ",";
        }

        private static List<string> SplitItems(string text, string sep)
        {
            return text.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(x => x.Trim())
                       .Where(x => x.Length > 0)
                       .ToList();
        }

        /// <summary>Вариант 1: «a, b, c» → «[&lt;,&gt; a | b | c ]».</summary>
        public static string EnumVariant1(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return null;
            string sep = DetectSeparator(text);
            var items = SplitItems(text, sep);
            if (items.Count == 0) return null;
            return "[<" + sep + "> " + string.Join(" | ", items) + " ]";
        }

        /// <summary>Вариант 2: «a, b и c» → «[&lt;,&gt; a | b &lt;и&gt;| c ]».</summary>
        public static string EnumVariant2(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return null;

            string sep = DetectSeparator(text);
            string lastSep = null;
            if (text.Contains(" или ")) lastSep = "или";
            else if (text.Contains(" и ")) lastSep = "и";

            if (lastSep != null)
            {
                var parts = text.Split(new[] { " " + lastSep + " " }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var firstPart = SplitItems(parts[0], sep);
                    string lastPart = parts[1].Trim();
                    if (firstPart.Count > 0 && lastPart.Length > 0)
                        return "[<" + sep + "> " + string.Join(" | ", firstPart) + " <" + lastSep + ">| " + lastPart + " ]";
                }
            }
            return EnumVariant1(text);
        }

        /// <summary>Схлопывает повторяющиеся пробелы.</summary>
        public static string NormalizeSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            bool prevSpace = false;
            foreach (char c in text)
            {
                if (c == ' ')
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
            return sb.ToString();
        }
    }
}
