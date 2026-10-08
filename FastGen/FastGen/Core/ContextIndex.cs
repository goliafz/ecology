using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FastGen.Core
{
    /// <summary>
    /// Пары соседних слов («собирать кубик», «удобная навигация»), встречавшиеся в ваших готовых шаблонах.
    /// Синоним подходит, если с левым и правым соседом он уже стоял рядом в каком-нибудь тексте:
    /// так «собирать кубик → копить кубик» отбрасывается, а «удобная → комфортная навигация» остаётся.
    /// В шаблоне учитываются все варианты: для «{a|b} {c|d}» запоминаются ac, ad, bc, bd.
    /// </summary>
    public sealed class ContextIndex
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;
        private const string FileMagic = "FGCTX1";

        private readonly long[] _sorted;

        private ContextIndex(long[] sorted)
        {
            _sorted = sorted;
        }

        public int Count { get { return _sorted.Length; } }

        public bool HasPair(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            return Array.BinarySearch(_sorted, Hash(left.ToLowerInvariant(), right.ToLowerInvariant())) >= 0;
        }

        private static long Hash(string a, string b)
        {
            ulong h = FnvOffset;
            foreach (char c in a) { h ^= c; h *= FnvPrime; }
            h ^= 1; h *= FnvPrime;
            foreach (char c in b) { h ^= c; h *= FnvPrime; }
            return unchecked((long)h);
        }

        // ------------------------------------------------------------------
        // Построение
        // ------------------------------------------------------------------

        public static ContextIndex Build(IEnumerable<string> templateLines)
        {
            var set = new HashSet<long>();
            foreach (var line in templateLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int i = 0;
                new Analyzer(line, set).ParseSeq(ref i, '\0', '\0');
            }
            var arr = new long[set.Count];
            set.CopyTo(arr);
            Array.Sort(arr);
            return new ContextIndex(arr);
        }

        public static ContextIndex FromPairs(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var set = new HashSet<long>();
            foreach (var p in pairs) set.Add(Hash(p.Key.ToLowerInvariant(), p.Value.ToLowerInvariant()));
            var arr = new long[set.Count];
            set.CopyTo(arr);
            Array.Sort(arr);
            return new ContextIndex(arr);
        }

        /// <summary>Результат разбора фрагмента: слова на входе/выходе и проходим ли он насквозь.</summary>
        private sealed class Part
        {
            public readonly HashSet<string> First = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Last = new HashSet<string>(StringComparer.Ordinal);
            public bool CanBeEmpty = true;   // может не дать ни одного слова
            public bool Blocked;             // внутри есть знак препинания
        }

        private sealed class Analyzer
        {
            private readonly string _t;
            private readonly HashSet<long> _out;

            public Analyzer(string text, HashSet<long> output)
            {
                _t = text;
                _out = output;
            }

            private void Pairs(IEnumerable<string> left, IEnumerable<string> right)
            {
                foreach (var a in left)
                    foreach (var b in right)
                        _out.Add(Hash(a, b));
            }

            /// <summary>Последовательность до символа stop1/stop2 (или конца строки).</summary>
            public Part ParseSeq(ref int i, char stop1, char stop2)
            {
                var seq = new Part();
                var carry = new HashSet<string>(StringComparer.Ordinal); // слова, после которых стоим
                bool atStart = true;
                var text = new StringBuilder();

                while (true)
                {
                    bool end = i >= _t.Length || _t[i] == stop1 || _t[i] == stop2;
                    bool open = !end && _t[i] == '{' && SpinSyntax.FindMatchingClose(_t, i) > 0;

                    if (end || open)
                    {
                        if (text.Length > 0)
                        {
                            Append(seq, carry, ref atStart, ParseText(text.ToString()));
                            text.Clear();
                        }
                        if (end) break;

                        i++; // '{'
                        var choice = new Part { CanBeEmpty = false };
                        while (true)
                        {
                            var v = ParseSeq(ref i, '|', '}');
                            choice.First.UnionWith(v.First);
                            choice.Last.UnionWith(v.Last);
                            choice.CanBeEmpty |= v.CanBeEmpty && !v.Blocked;
                            choice.Blocked |= v.Blocked;
                            if (i >= _t.Length) break;
                            char c = _t[i++];
                            if (c == '}') break;
                        }
                        Append(seq, carry, ref atStart, choice);
                        continue;
                    }

                    text.Append(_t[i]);
                    i++;
                }

                seq.Last.UnionWith(carry);
                seq.CanBeEmpty = atStart && !seq.Blocked;
                return seq;
            }

            private void Append(Part seq, HashSet<string> carry, ref bool atStart, Part item)
            {
                Pairs(carry, item.First);
                if (atStart) seq.First.UnionWith(item.First);

                if (item.Blocked)
                {
                    seq.Blocked = true;
                    atStart = false;
                    carry.Clear();
                    carry.UnionWith(item.Last);
                    return;
                }

                if (item.CanBeEmpty)
                {
                    carry.UnionWith(item.Last);   // вариант может быть пустым — предыдущие слова «видны» дальше
                    return;
                }

                atStart = false;
                carry.Clear();
                carry.UnionWith(item.Last);
            }

            /// <summary>Обычный текст: слова и знаки препинания (пары через знак не образуются).</summary>
            private Part ParseText(string s)
            {
                var part = new Part();
                string prev = null;
                bool sawWord = false;

                int i = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (TextNav.IsWordChar(c))
                    {
                        var r = TextNav.WordAt(s, i);
                        string w = s.Substring(r.Start, r.Length).ToLowerInvariant();
                        if (prev != null) _out.Add(Hash(prev, w));
                        else if (!sawWord && !part.Blocked) part.First.Add(w);
                        prev = w;
                        sawWord = true;
                        i = r.End;
                        continue;
                    }

                    if (!char.IsWhiteSpace(c) && c != '[' && c != ']' && c != '<' && c != '>')
                    {
                        // знак препинания (дефис между пробелами — тоже)
                        part.Blocked = true;
                        prev = null;
                    }
                    else if (c == '\n' || c == '\r')
                    {
                        part.Blocked = true;
                        prev = null;
                    }
                    i++;
                }

                if (prev != null) part.Last.Add(prev);
                part.CanBeEmpty = !sawWord && !part.Blocked;
                return part;
            }
        }

        // ------------------------------------------------------------------
        // Файл
        // ------------------------------------------------------------------

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(FileMagic);
                bw.Write(_sorted.Length);
                foreach (var h in _sorted) bw.Write(h);
            }
            FileText.CommitTemp(tmp, path);
        }

        public static ContextIndex Load(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadString() != FileMagic) throw new InvalidDataException("Неизвестный формат " + Path.GetFileName(path));
                int n = br.ReadInt32();
                var arr = new long[n];
                for (int k = 0; k < n; k++) arr[k] = br.ReadInt64();
                return new ContextIndex(arr);
            }
        }

        /// <summary>
        /// Загружает Context.bin, если он новее файла шаблонов, иначе строит заново из шаблонов и сохраняет.
        /// Возвращает null, если нет ни того, ни другого.
        /// </summary>
        public static ContextIndex LoadOrBuild(string indexPath, string templatesPath)
        {
            bool haveIndex = File.Exists(indexPath);
            bool haveTemplates = File.Exists(templatesPath);

            if (haveIndex && (!haveTemplates || File.GetLastWriteTimeUtc(indexPath) >= File.GetLastWriteTimeUtc(templatesPath)))
            {
                try { return Load(indexPath); }
                catch (InvalidDataException) { /* пересоберём */ }
                catch (EndOfStreamException) { /* пересоберём */ }
            }

            if (!haveTemplates) return null;

            var index = Build(File.ReadLines(templatesPath, new UTF8Encoding(false)));
            try { index.Save(indexPath); }
            catch (IOException) { /* нет прав на запись — работаем из памяти */ }
            catch (UnauthorizedAccessException) { }
            return index;
        }
    }
}
