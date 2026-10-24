using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WeCantSpell.Hunspell;

namespace VidLex.Core.Text
{
    /// <summary>
    /// Проверка орфографии по словарю Hunspell ru_RU (А. Лебедев, лицензия BSD).
    /// Исправление выполняется только в однозначных случаях:
    /// слово с маленькой буквы, только кириллица, длина от 5 букв, нет в словаре и в пользовательском списке,
    /// и словарь предлагает ровно один вариант на расстоянии одной правки (замена, вставка, удаление, перестановка).
    /// Если таких вариантов несколько, выбирается единственный, сохраняющий окончание слова
    /// (последние две буквы несут грамматику: «програмы» → «программы», а не «программ»).
    /// Неизвестные слова (имена, термины, англицизмы) без однозначного варианта не меняются.
    /// </summary>
    public sealed class RussianSpellChecker
    {
        private readonly WordList _words;
        private readonly object _suggestSync = new object();
        private readonly Dictionary<string, string> _suggestCache = new Dictionary<string, string>();

        private RussianSpellChecker(WordList words)
        {
            _words = words;
        }

        /// <summary>Загружает ru_RU.dic/.aff из папки. null, если файлов нет.</summary>
        public static RussianSpellChecker TryLoad(string dictionaryDirectory, out string error)
        {
            error = null;
            try
            {
                var dic = Path.Combine(dictionaryDirectory ?? "", "ru_RU.dic");
                var aff = Path.Combine(dictionaryDirectory ?? "", "ru_RU.aff");
                if (!File.Exists(dic) || !File.Exists(aff))
                {
                    error = $"Словарь русского языка не найден в «{dictionaryDirectory}»";
                    return null;
                }
                return new RussianSpellChecker(WordList.CreateFromFiles(dic, aff));
            }
            catch (Exception ex)
            {
                error = "Не удалось загрузить словарь русского языка: " + ex.Message;
                return null;
            }
        }

        /// <summary>Слово известно словарю (с учётом ё/е).</summary>
        public bool IsKnown(string word)
        {
            if (string.IsNullOrEmpty(word)) return true;
            if (_words.Check(word)) return true;
            if (word.IndexOf('ё') >= 0 || word.IndexOf('Ё') >= 0)
                return _words.Check(word.Replace('ё', 'е').Replace('Ё', 'Е'));
            return false;
        }

        /// <summary>Нарицательное слово: форма с маленькой буквы есть в словаре.</summary>
        public bool IsCommonWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            return IsKnown(word.ToLowerInvariant());
        }

        /// <summary>Однозначное исправление или null.</summary>
        public string SuggestSafeFix(string word)
        {
            lock (_suggestSync)
            {
                if (_suggestCache.TryGetValue(word, out var cached)) return cached;
                string result = null;
                try
                {
                    var candidates = _words.Suggest(word)
                        .Where(s => s.Length > 0 && s.All(IsLowerCyrillicOrHyphen) && s.IndexOf(' ') < 0)
                        .Where(s => DamerauLevenshtein(word, s) == 1)
                        .Distinct()
                        .ToList();
                    if (candidates.Count == 1)
                    {
                        result = candidates[0];
                    }
                    else if (candidates.Count > 1 && word.Length >= 2)
                    {
                        var ending = word.Substring(word.Length - 2);
                        var sameEnding = candidates.Where(c => c.EndsWith(ending, StringComparison.Ordinal)).ToList();
                        if (sameEnding.Count == 1) result = sameEnding[0];
                    }
                }
                catch
                {
                    result = null;
                }
                _suggestCache[word] = result;
                return result;
            }
        }

        public static bool IsCyrillicLetter(char c) => (c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c == 'ё' || c == 'Ё';

        private static bool IsLowerCyrillicOrHyphen(char c) => (c >= 'а' && c <= 'я') || c == 'ё' || c == '-';

        /// <summary>Расстояние Дамерау–Левенштейна (оптимальное выравнивание строк).</summary>
        public static int DamerauLevenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
            return d[a.Length, b.Length];
        }
    }
}
