using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FastGen.Core
{
    public enum SynonymSource
    {
        User,      // «Моя база» (UserBase.txt) — списки, сохранённые вручную (Ctrl+PgDn)
        Frequent,  // ранее выбранные вами синонимы (syn_usage.txt)
        Gold,      // GoldBase.txt — собранная из ваших шаблонов
        Dict       // DICT.DBF — большая база TextExpert
    }

    public sealed class SynonymCandidate
    {
        public string Text;
        public SynonymSource Source;
        public int UsageCount;

        public override string ToString() { return Text; }
    }

    /// <summary>
    /// Все базы синонимов в одном месте. Ключи и синонимы хранятся в нижнем регистре.
    /// Методы вызываются из UI-потока; только загрузка DICT.DBF идёт в фоне
    /// и подменяет словарь одним присваиванием.
    /// </summary>
    public sealed class SynonymStore
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private Dictionary<string, string[]> _gold = NewDict<string[]>();
        private Dictionary<string, List<string>> _user = NewDict<List<string>>();
        private volatile Dictionary<string, string[]> _dict;
        private readonly Dictionary<string, Dictionary<string, int>> _usage = NewDict<Dictionary<string, int>>();
        private readonly Dictionary<string, HashSet<string>> _rejected = NewDict<HashSet<string>>();

        public string GoldPath { get; set; }
        public string UserPath { get; set; }
        public string UsagePath { get; set; }
        public string RejectedPath { get; set; }

        public int GoldCount { get { return _gold.Count; } }
        public int UserCount { get { return _user.Count; } }
        public int DictCount { get { var d = _dict; return d == null ? 0 : d.Count; } }
        public bool DictLoaded { get { return _dict != null; } }

        private static Dictionary<string, T> NewDict<T>()
        {
            return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        }

        public static string Normalize(string s)
        {
            if (s == null) return string.Empty;
            s = s.Trim();
            if (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = TextCase.NormalizeSpaces(s);
            return s.ToLowerInvariant();
        }

        // ------------------------------------------------------------------
        // Загрузка
        // ------------------------------------------------------------------

        /// <summary>GoldBase.txt: строки «слово|синоним|синоним», все слова строки — взаимные синонимы.</summary>
        public void LoadGold()
        {
            _gold = LoadGroupFile(GoldPath);
        }

        /// <summary>
        /// Для каждого слова — все слова из строк, где оно встречается. Чем чаще пара встречалась вместе
        /// (в разных строках базы), тем выше синоним в списке.
        /// </summary>
        public static Dictionary<string, string[]> LoadGroupFile(string path)
        {
            var res = NewDict<string[]>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return res;

            var tmp = NewDict<Dictionary<string, int>>();          // слово → (синоним → сколько раз вместе)
            var order = NewDict<List<string>>();                   // порядок первого появления
            foreach (var line in File.ReadLines(path, Utf8NoBom))
            {
                var parts = SplitLine(line);
                if (parts.Count < 2) continue;

                foreach (var w in parts)
                {
                    string key = Normalize(w);
                    if (!tmp.TryGetValue(key, out var counts))
                    {
                        counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        tmp[key] = counts;
                        order[key] = new List<string>();
                    }
                    foreach (var syn in parts)
                    {
                        if (string.Equals(Normalize(syn), key, StringComparison.Ordinal)) continue;
                        if (counts.TryGetValue(syn, out int c)) counts[syn] = c + 1;
                        else
                        {
                            counts[syn] = 1;
                            order[key].Add(syn);
                        }
                    }
                }
            }

            foreach (var kv in tmp)
            {
                if (kv.Value.Count == 0) continue;
                var counts = kv.Value;
                var list = order[kv.Key];
                var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < list.Count; i++) idx[list[i]] = i;
                res[kv.Key] = list.OrderByDescending(x => counts[x]).ThenBy(x => idx[x]).ToArray();
            }
            return res;
        }

        private static List<string> SplitLine(string line)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(line)) return res;
            foreach (var p in line.Split('|'))
            {
                string t = TextCase.NormalizeSpaces(p.Trim()).ToLowerInvariant();
                if (t.Length == 0) continue;
                if (!res.Contains(t, StringComparer.OrdinalIgnoreCase)) res.Add(t);
            }
            return res;
        }

        /// <summary>UserBase.txt: «ключ|синоним1|синоним2» — первый элемент ключ, порядок сохраняется.</summary>
        public void LoadUser()
        {
            var res = NewDict<List<string>>();
            if (!string.IsNullOrEmpty(UserPath) && File.Exists(UserPath))
            {
                foreach (var line in File.ReadLines(UserPath, Utf8NoBom))
                {
                    var parts = SplitLine(line);
                    if (parts.Count < 1) continue;
                    string key = Normalize(parts[0]);
                    res[key] = parts.Skip(1).ToList();
                }
            }
            _user = res;
        }

        public void SaveUser()
        {
            if (string.IsNullOrEmpty(UserPath)) return;
            var lines = _user
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + (kv.Value.Count > 0 ? "|" + string.Join("|", kv.Value) : string.Empty));
            WriteAllLinesSafe(UserPath, lines);
        }

        /// <summary>syn_usage.txt: «слово|синоним|счётчик».</summary>
        public void LoadUsage()
        {
            _usage.Clear();
            if (string.IsNullOrEmpty(UsagePath) || !File.Exists(UsagePath)) return;

            foreach (var line in File.ReadLines(UsagePath, Utf8NoBom))
            {
                var parts = line.Split('|');
                if (parts.Length != 3) continue;
                string baseWord = Normalize(parts[0]);
                string syn = Normalize(parts[1]);
                if (baseWord.Length == 0 || syn.Length == 0) continue;
                if (!int.TryParse(parts[2].Trim(), out int count) || count <= 0) continue;

                if (!_usage.TryGetValue(baseWord, out var d))
                {
                    d = NewDict<int>();
                    _usage[baseWord] = d;
                }
                d[syn] = count;
            }
        }

        public void SaveUsage()
        {
            if (string.IsNullOrEmpty(UsagePath)) return;
            var lines = new List<string>();
            foreach (var kvBase in _usage.OrderBy(k => k.Key, StringComparer.Ordinal))
                foreach (var kvSyn in kvBase.Value)
                    if (kvSyn.Value > 0) lines.Add(kvBase.Key + "|" + kvSyn.Key + "|" + kvSyn.Value);
            WriteAllLinesSafe(UsagePath, lines);
        }

        /// <summary>Пишем во временный файл и подменяем — чтобы не потерять базу при сбое.</summary>
        public static void WriteAllLinesSafe(string path, IEnumerable<string> lines)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines, Utf8NoBom);
            FileText.CommitTemp(tmp, path);
        }

        /// <summary>Подключает уже загруженный (в фоне) словарь DICT.DBF.</summary>
        public void SetDict(Dictionary<string, string[]> dict)
        {
            _dict = dict;
        }

        // ------------------------------------------------------------------
        // Поиск
        // ------------------------------------------------------------------

        public bool HasUserEntry(string phrase)
        {
            return _user.ContainsKey(Normalize(phrase));
        }

        /// <summary>Есть ли в базах запись для слова/фразы.</summary>
        public bool Contains(string phrase, bool includeDict)
        {
            string key = Normalize(phrase);
            if (key.Length == 0) return false;
            return ContainsExact(key, includeDict) || (key.IndexOf('ё') >= 0 && ContainsExact(key.Replace('ё', 'е'), includeDict));
        }

        private bool ContainsExact(string key, bool includeDict)
        {
            if (_user.ContainsKey(key) || _gold.ContainsKey(key) || _usage.ContainsKey(key)) return true;
            var d = _dict;
            return includeDict && d != null && d.ContainsKey(key);
        }

        public int GetUsage(string baseWord, string synonym)
        {
            if (_usage.TryGetValue(Normalize(baseWord), out var d) && d.TryGetValue(Normalize(synonym), out int c))
                return c;
            return 0;
        }

        /// <summary>
        /// Кандидаты в синонимы в порядке приоритета: моя база → частые → GoldBase → DICT.DBF.
        /// Сам исходный ключ в список не попадает.
        /// </summary>
        public List<SynonymCandidate> GetCandidates(string phrase, bool includeDict)
        {
            var result = GetCandidatesExact(Normalize(phrase), includeDict);
            if (result.Count == 0 && phrase != null && phrase.IndexOfAny(YoChars) >= 0)
            {
                // «ещё» ищем и как «еще» — в базах буква ё часто не используется
                string key = Normalize(phrase);
                result = GetCandidatesExact(key.Replace('ё', 'е'), includeDict);
                result.RemoveAll(c => string.Equals(Normalize(c.Text), key, StringComparison.Ordinal));
            }
            return result;
        }

        private static readonly char[] YoChars = { 'ё', 'Ё' };

        private List<SynonymCandidate> GetCandidatesExact(string key, bool includeDict)
        {
            var result = new List<SynonymCandidate>();
            if (key.Length == 0) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
            _usage.TryGetValue(key, out var usage);

            _rejected.TryGetValue(key, out var rejected);

            Action<string, SynonymSource> add = (s, src) =>
            {
                string t = (s ?? string.Empty).Trim();
                if (t.Length == 0) return;
                // отклонённые вами пары больше не предлагаются (кроме записанных в «Мою базу»)
                if (src != SynonymSource.User && rejected != null && rejected.Contains(Normalize(t))) return;
                if (!seen.Add(Normalize(t))) return;
                int cnt = 0;
                if (usage != null) usage.TryGetValue(Normalize(t), out cnt);
                result.Add(new SynonymCandidate { Text = t, Source = src, UsageCount = cnt });
            };

            if (_user.TryGetValue(key, out var userList))
                foreach (var s in userList) add(s, SynonymSource.User);

            if (usage != null)
                foreach (var kv in usage.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
                    add(kv.Key, SynonymSource.Frequent);

            if (_gold.TryGetValue(key, out var gold))
                foreach (var s in gold) add(s, SynonymSource.Gold);

            var dict = _dict;
            if (includeDict && dict != null && dict.TryGetValue(key, out var dsyn))
                foreach (var s in dsyn) add(s, SynonymSource.Dict);

            return result;
        }

        // ------------------------------------------------------------------
        // Изменение
        // ------------------------------------------------------------------

        /// <summary>Сохраняет список синонимов для слова в «Мою базу» (как Ctrl+PgDn в TextExpert).</summary>
        public void SetUserEntry(string phrase, IEnumerable<string> synonyms)
        {
            string key = Normalize(phrase);
            if (key.Length == 0) return;
            var list = new List<string>();
            foreach (var s in synonyms)
            {
                string t = TextCase.NormalizeSpaces((s ?? string.Empty).Trim()).ToLowerInvariant();
                if (t.Length == 0 || t.IndexOf('|') >= 0) continue;
                if (Normalize(t) == key) continue;
                if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
            }
            _user[key] = list;
            if (_rejected.TryGetValue(key, out var rej))
                foreach (var t in list) rej.Remove(Normalize(t));
        }

        public bool RemoveUserEntry(string phrase)
        {
            return _user.Remove(Normalize(phrase));
        }

        /// <summary>+1 к частоте использования каждого синонима для слова.</summary>
        public void RegisterUsage(string baseWord, IEnumerable<string> synonyms)
        {
            string key = Normalize(baseWord);
            if (key.Length == 0) return;
            if (!_usage.TryGetValue(key, out var d))
            {
                d = NewDict<int>();
                _usage[key] = d;
            }
            foreach (var s in synonyms)
            {
                string syn = Normalize(s);
                if (syn.Length == 0 || syn == key || syn.IndexOf('|') >= 0 || syn.IndexOf('{') >= 0) continue;
                d.TryGetValue(syn, out int c);
                d[syn] = c + 1;
                if (_rejected.TryGetValue(key, out var rej)) rej.Remove(syn);
            }
        }

        /// <summary>
        /// Запоминает синонимы, которые вы убрали из конструкции: в этом слове они больше не предлагаются
        /// автоматически, а их «частота» обнуляется.
        /// </summary>
        public void RegisterRejected(string baseWord, IEnumerable<string> synonyms)
        {
            string key = Normalize(baseWord);
            if (key.Length == 0) return;
            if (!_rejected.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _rejected[key] = set;
            }
            _usage.TryGetValue(key, out var usage);
            foreach (var s in synonyms)
            {
                string syn = Normalize(s);
                if (syn.Length == 0 || syn == key || syn.IndexOfAny(new[] { '|', '{', '}' }) >= 0) continue;
                set.Add(syn);
                if (usage != null) usage.Remove(syn);
            }
        }

        public bool IsRejected(string baseWord, string synonym)
        {
            return _rejected.TryGetValue(Normalize(baseWord), out var set) && set.Contains(Normalize(synonym));
        }

        /// <summary>syn_rejected.txt: «слово|синоним».</summary>
        public void LoadRejected()
        {
            _rejected.Clear();
            if (string.IsNullOrEmpty(RejectedPath) || !File.Exists(RejectedPath)) return;
            foreach (var line in File.ReadLines(RejectedPath, Utf8NoBom))
            {
                var parts = line.Split('|');
                if (parts.Length != 2) continue;
                RegisterRejectedRaw(Normalize(parts[0]), Normalize(parts[1]));
            }
        }

        private void RegisterRejectedRaw(string key, string syn)
        {
            if (key.Length == 0 || syn.Length == 0) return;
            if (!_rejected.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _rejected[key] = set;
            }
            set.Add(syn);
        }

        public void SaveRejected()
        {
            if (string.IsNullOrEmpty(RejectedPath)) return;
            var lines = new List<string>();
            foreach (var kv in _rejected.OrderBy(k => k.Key, StringComparer.Ordinal))
                foreach (var syn in kv.Value.OrderBy(x => x, StringComparer.Ordinal))
                    lines.Add(kv.Key + "|" + syn);
            WriteAllLinesSafe(RejectedPath, lines);
        }

        // ------------------------------------------------------------------
        // DICT.DBF
        // ------------------------------------------------------------------

        /// <summary>
        /// Читает DICT.DBF (dBASE, cp866) без драйверов BDE.
        /// Формат TextExpert: первое символьное поле — слово, остальные — его синонимы.
        /// progress(прочитано, всего) вызывается из фонового потока.
        /// </summary>
        public static Dictionary<string, string[]> ReadDbf(string path, Action<int, int> progress)
        {
            Encoding enc;
            try { enc = Encoding.GetEncoding(866); }
            catch { enc = Encoding.Default; }

            var tmp = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var pool = new Dictionary<string, string>(StringComparer.Ordinal);
            Func<string, string> intern = s =>
            {
                if (pool.TryGetValue(s, out var existing)) return existing;
                pool[s] = s;
                return s;
            };

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var br = new BinaryReader(fs))
            {
                br.ReadByte();                     // версия
                br.ReadBytes(3);                   // дата
                int recordCount = br.ReadInt32();
                int headerLen = br.ReadUInt16();
                int recordLen = br.ReadUInt16();
                br.ReadBytes(20);

                var fields = new List<KeyValuePair<int, int>>(); // offset, length (только символьные)
                int offset = 1;                                   // первый байт записи — признак удаления
                while (fs.Position < headerLen)
                {
                    byte first = br.ReadByte();
                    if (first == 0x0D) break;                     // конец описателей полей
                    br.ReadBytes(10);                             // остаток имени
                    char type = (char)br.ReadByte();
                    br.ReadInt32();
                    int len = br.ReadByte();
                    int dec = br.ReadByte();
                    br.ReadBytes(14);

                    if (type == 'C' && dec > 0) len += dec << 8;  // длинные символьные поля (Clipper)
                    if (type == 'C' && len > 0) fields.Add(new KeyValuePair<int, int>(offset, len));
                    offset += len;
                }

                if (fields.Count < 2)
                    throw new InvalidDataException("В DICT.DBF меньше двух символьных полей.");

                fs.Position = headerLen;
                var rec = new byte[recordLen];
                int step = Math.Max(1, recordCount / 200);

                for (int r = 0; r < recordCount; r++)
                {
                    int read = 0;
                    while (read < recordLen)
                    {
                        int n = fs.Read(rec, read, recordLen - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < recordLen) break;
                    if (rec[0] == (byte)'*') continue;            // удалённая запись

                    string key = null;
                    List<string> list = null;
                    foreach (var f in fields)
                    {
                        if (f.Key + f.Value > recordLen) break;
                        string v = enc.GetString(rec, f.Key, f.Value).Trim();
                        if (v.Length == 0) continue;
                        v = v.ToLowerInvariant();

                        if (key == null)
                        {
                            key = Normalize(v);
                            if (!tmp.TryGetValue(key, out list))
                            {
                                list = new List<string>(8);
                                tmp[key] = list;
                            }
                            continue;
                        }
                        if (Normalize(v) == key) continue;
                        v = intern(v);
                        if (!list.Contains(v)) list.Add(v);
                    }

                    if (progress != null && r % step == 0) progress(r, recordCount);
                }
                if (progress != null) progress(recordCount, recordCount);
            }

            var res = new Dictionary<string, string[]>(tmp.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tmp)
                if (kv.Value.Count > 0) res[kv.Key] = kv.Value.ToArray();
            return res;
        }
    }
}
