using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VidLex.Core.Abstractions;
using VidLex.Core.Models;
using VidLex.Core.Text;

namespace VidLex.Core.Services
{
    /// <summary>
    /// Постобработка распознанного текста. Работает со словами и их временем, поэтому:
    /// — соседние слова всегда разделяются пробелом (склейка невозможна);
    /// — у каждой фразы известно время начала (для временных меток);
    /// — правка формы (пробелы, пунктуация, регистр, однозначные опечатки) не меняет содержание:
    ///   слова не удаляются и не добавляются, кроме пометок неречевых звуков и отфильтрованных галлюцинаций.
    /// </summary>
    public sealed class TextCorrectionService : ITextCorrectionService
    {
        private readonly ILogService _log;
        private readonly Lazy<RussianSpellChecker> _speller;

        /// <summary>Служебные слова: после них предложение не может закончиться.</summary>
        private static readonly HashSet<string> FunctionWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "и", "а", "но", "или", "да", "что", "чтобы", "как", "когда", "если", "где", "куда", "откуда",
            "который", "которая", "которое", "которые", "которого", "которой", "которую", "которым", "которых", "которыми",
            "я", "мы", "вы", "ты", "он", "она", "оно", "они", "нам", "вам", "им", "их", "его", "её", "ее", "мне", "меня", "нас", "вас",
            "это", "этот", "эта", "эти", "этого", "этой", "тот", "та", "то", "те", "того",
            "в", "во", "на", "с", "со", "к", "ко", "по", "о", "об", "обо", "от", "до", "из", "за", "для", "под", "над",
            "при", "про", "без", "через", "у", "между", "перед",
            "не", "ни", "же", "ли", "бы", "уже", "ещё", "еще", "только", "тоже", "также", "даже",
            "будет", "будем", "будут", "буду", "будете", "можно", "нужно", "надо", "давайте", "давай", "хочу", "хотим",
            "вот", "ну", "так", "там", "тут", "здесь", "потому", "очень", "более", "менее", "самый", "самая", "самое"
        };

        /// <summary>Слова-связки, с которых обычно начинается новое предложение.</summary>
        private static readonly string[][] SentenceMarkers =
        {
            new[] { "далее" }, new[] { "затем" }, new[] { "итак" }, new[] { "теперь" }, new[] { "следовательно" },
            new[] { "во-первых" }, new[] { "во-вторых" }, new[] { "в-третьих" }, new[] { "в-четвёртых" }, new[] { "в-четвертых" },
            new[] { "после", "этого" }, new[] { "кроме", "того" }, new[] { "таким", "образом" },
            new[] { "в", "итоге" }, new[] { "в", "заключение" }
        };

        private static readonly Regex NonSpeechParens = new Regex(
            @"^\((музык|смех|аплодисмент|шум|тишин|вздох|кашл|неразборчиво|играет|звук|песн|заставк|music|laugh|applause|noise)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public TextCorrectionService(ILogService log, string dictionaryDirectory)
        {
            _log = log;
            _speller = new Lazy<RussianSpellChecker>(() =>
            {
                var sp = RussianSpellChecker.TryLoad(dictionaryDirectory, out var error);
                if (sp == null) _log?.Warning(error + ". Проверка орфографии отключена.", stage: "Обработка текста");
                else _log?.Debug("Словарь русского языка загружен", stage: "Обработка текста");
                return sp;
            }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>Загрузить словарь заранее (в фоне при запуске), чтобы первое задание не ждало.</summary>
        public void Preload() => _ = _speller.Value;

        private sealed class W
        {
            public string Text;
            public double Start;
            public double End;
            public bool Estimated;
            /// <summary>Последнее слово сегмента движка.</summary>
            public bool SegmentEnd;

            public W Copy(string text) => new W { Text = text, Start = Start, End = End, Estimated = Estimated, SegmentEnd = SegmentEnd };
        }

        public ProcessedTranscript Process(IList<TranscriptSegment> segments, IList<TimeRange> silences, TextProcessingOptions options, string fileNameForLog = null)
        {
            options = options ?? new TextProcessingOptions();
            var result = new ProcessedTranscript();
            var input = (segments ?? new List<TranscriptSegment>()).OrderBy(s => s.Start).ToList();

            if (options.FilterHallucinations)
            {
                input = HallucinationFilter.Apply(input, silences, out var removed, out var reasons);
                result.RemovedHallucinations = removed;
                if (removed > 0)
                    _log?.Warning($"Отброшено фрагментов-«галлюцинаций» распознавания: {removed}", fileNameForLog, "Обработка текста", string.Join(Environment.NewLine, reasons));
            }

            int broken = RemoveBrokenCharacters(input);
            if (broken > 0)
                _log?.Warning($"Движок вернул повреждённые символы (некорректный UTF-8): удалено {broken}", fileNameForLog, "Обработка текста");
            var customKeys = new HashSet<string>((options.CustomWords ?? new List<string>()).Select(NormalizeKey), StringComparer.Ordinal);
            var spellerForCaps = options.SpellCheckMode != SpellCheckMode.Off || options.EnableSentenceSplitting ? _speller.Value : null;
            int capsFixed = NormalizeShoutedSegments(input, customKeys, spellerForCaps);
            if (capsFixed > 0)
                _log?.Debug($"Сегментов, набранных заглавными буквами, приведено к обычному регистру: {capsFixed}", fileNameForLog, "Обработка текста");

            var words = Flatten(input, options.RemoveNonSpeechAnnotations);
            words = SplitGluedWords(words);
            words = AttachPunctuation(words);
            foreach (var w in words) w.Text = NormalizeWordPunctuation(w.Text);
            words.RemoveAll(w => string.IsNullOrWhiteSpace(w.Text));
            if (words.Count == 0) return result;

            var speller = options.SpellCheckMode != SpellCheckMode.Off || options.EnableSentenceSplitting ? _speller.Value : null;
            var custom = new HashSet<string>((options.CustomWords ?? new List<string>()).Select(NormalizeKey), StringComparer.Ordinal);

            result.SentenceBreaksAdded += DecideSegmentBoundaries(words, options, speller);
            if (options.EnableSentenceSplitting)
                result.SentenceBreaksAdded += SplitRunOns(words, options, speller, custom);

            if (options.SpellCheckMode != SpellCheckMode.Off && speller != null)
                SpellCheck(words, options, speller, custom, result, fileNameForLog);

            BuildSentences(words, result);
            return result;
        }

        // ---------- 0. Повреждённые символы и «крик» ----------

        /// <summary>Удаляет символ замены U+FFFD, который появляется, если модель выдала некорректную последовательность байтов.</summary>
        private static int RemoveBrokenCharacters(IList<TranscriptSegment> segments)
        {
            int count = 0;
            foreach (var s in segments)
            {
                count += (s.Text ?? "").Count(c => c == '\uFFFD');
                s.Text = (s.Text ?? "").Replace("\uFFFD", "");
                foreach (var w in s.Words) w.Text = (w.Text ?? "").Replace("\uFFFD", "");
                s.Words.RemoveAll(w => w.Text.Trim().Length == 0);
            }
            return count;
        }

        /// <summary>
        /// Сегмент, где почти все буквы заглавные (модель иногда «кричит»), приводится к строчным.
        /// Аббревиатуры (короткие слова, которых нет в словаре в строчном виде: МВД, ООО) и пользовательские
        /// термины не меняются. Заглавная в начале предложения восстанавливается при сборке предложений.
        /// </summary>
        private static int NormalizeShoutedSegments(IList<TranscriptSegment> segments, HashSet<string> custom, RussianSpellChecker speller)
        {
            int fixedCount = 0;
            foreach (var s in segments)
            {
                var letters = string.Concat(s.Words.Select(w => w.Text)).Where(char.IsLetter).ToList();
                if (s.Words.Count < 3 || letters.Count < 12) continue;
                if (letters.Count(char.IsUpper) < letters.Count * 0.8) continue;
                foreach (var w in s.Words)
                {
                    var core = Core(w.Text);
                    if (custom.Contains(NormalizeKey(core))) continue;
                    if (core.Length <= 4)
                    {
                        // Короткое слово: строчными, только если это обычное слово («МЫ», «ЗА»), а не аббревиатура.
                        bool common = speller != null ? speller.IsKnown(core.ToLowerInvariant()) : false;
                        if (!common) continue;
                    }
                    w.Text = w.Text.ToLowerInvariant();
                }
                s.Text = string.Join(" ", s.Words.Select(w => w.Text));
                fixedCount++;
            }
            return fixedCount;
        }

        // ---------- 1. Слова из сегментов ----------

        private static List<W> Flatten(IList<TranscriptSegment> segments, bool removeAnnotations)
        {
            var list = new List<W>();
            foreach (var s in segments)
            {
                var segWords = s.Words != null && s.Words.Count > 0
                    ? s.Words
                    : WhisperCppEngine.EstimateWords(s.Text ?? "", s.Start, s.End);

                var cleaned = new List<W>();
                bool inBrackets = false;
                foreach (var tw in segWords)
                {
                    var t = (tw.Text ?? "").Trim();
                    if (t.Length == 0) continue;
                    if (removeAnnotations)
                    {
                        if (inBrackets)
                        {
                            if (t.IndexOf(']') >= 0) inBrackets = false;
                            continue;
                        }
                        if (t.StartsWith("[", StringComparison.Ordinal))
                        {
                            if (t.IndexOf(']') < 0) inBrackets = true;
                            continue;
                        }
                        if (t.All(c => c == '♪' || c == '♫' || c == '*' || char.IsWhiteSpace(c))) continue;
                        if (NonSpeechParens.IsMatch(t) && t.EndsWith(")", StringComparison.Ordinal)) continue;
                    }
                    // Внутри слова могут встретиться пробелы (склеенные токены) — делим.
                    foreach (var part in t.Split(new[] { ' ', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        cleaned.Add(new W { Text = part, Start = tw.Start, End = tw.End, Estimated = tw.TimeEstimated });
                }
                if (cleaned.Count == 0) continue;
                cleaned[cleaned.Count - 1].SegmentEnd = true;
                list.AddRange(cleaned);
            }
            return list;
        }

        // ---------- 2. Пунктуация внутри и между словами ----------

        private static readonly Regex GluedSentence = new Regex(@"^(.*[\p{L}\p{N}][.!?…]+[»""]?)([А-ЯЁA-Z].*)$", RegexOptions.Compiled);
        private static readonly Regex GluedComma = new Regex(@"^(.*\p{L}[,;:])(\p{L}.*)$", RegexOptions.Compiled);

        /// <summary>«программы.Затем» → «программы.» «Затем»; «да,конечно» → «да,» «конечно». Адреса и числа не трогаются.</summary>
        private static List<W> SplitGluedWords(List<W> words)
        {
            var result = new List<W>();
            foreach (var w in words)
            {
                var pending = new Queue<W>();
                pending.Enqueue(w);
                while (pending.Count > 0)
                {
                    var cur = pending.Dequeue();
                    var t = cur.Text;
                    if (LooksLikeUrlOrNumber(t))
                    {
                        result.Add(cur);
                        continue;
                    }
                    var m = GluedSentence.Match(t);
                    if (!m.Success) m = GluedComma.Match(t);
                    if (m.Success && m.Groups[1].Length > 1 && m.Groups[2].Length > 0)
                    {
                        var first = cur.Copy(m.Groups[1].Value);
                        first.SegmentEnd = false;
                        result.Add(first);
                        pending.Enqueue(cur.Copy(m.Groups[2].Value));
                    }
                    else
                    {
                        result.Add(cur);
                    }
                }
            }
            return result;
        }

        private static bool LooksLikeUrlOrNumber(string t)
        {
            if (t.IndexOf("://", StringComparison.Ordinal) >= 0 || t.IndexOf('@') >= 0) return true;
            // Латиница с обеих сторон точки — домен или имя файла (example.com, readme.txt).
            if (Regex.IsMatch(t, @"[A-Za-z]\.[A-Za-z]")) return true;
            // Числа: 3.5, 1,5, 12:30.
            if (Regex.IsMatch(t, @"\d[.,:]\d")) return true;
            return false;
        }

        private static bool IsPunctuationOnly(string t) => t.Length > 0 && t.All(c => char.IsPunctuation(c) || char.IsSymbol(c));

        /// <summary>Знаки препинания, стоящие отдельным «словом», присоединяются к предыдущему слову (без пробела перед ними).</summary>
        private static List<W> AttachPunctuation(List<W> words)
        {
            var result = new List<W>();
            foreach (var w in words)
            {
                var t = w.Text;
                if (t == "-" || t == "–" || t == "—" || t == "--")
                {
                    // Тире между словами оставляем отдельным элементом с пробелами вокруг.
                    if (result.Count > 0) result.Add(w.Copy("—"));
                    continue;
                }
                // Ведущая пунктуация («,слово») переносится к предыдущему слову.
                int lead = 0;
                while (lead < t.Length && ",.!?;:…".IndexOf(t[lead]) >= 0) lead++;
                if (lead > 0 && result.Count > 0)
                {
                    var prev = result[result.Count - 1];
                    if (prev.Text != "—") prev.Text += t.Substring(0, lead);
                    prev.SegmentEnd |= lead == t.Length && w.SegmentEnd;
                    t = t.Substring(lead);
                    if (t.Length == 0) continue;
                }
                if (IsPunctuationOnly(t) && result.Count > 0 && !t.StartsWith("«", StringComparison.Ordinal) && !t.StartsWith("(", StringComparison.Ordinal) && t != "\"")
                {
                    var prev = result[result.Count - 1];
                    prev.Text += t;
                    prev.SegmentEnd |= w.SegmentEnd;
                    continue;
                }
                result.Add(w.Copy(t));
            }
            // Тире в начале или в конце текста смысла не несёт.
            while (result.Count > 0 && result[result.Count - 1].Text == "—") result.RemoveAt(result.Count - 1);
            return result;
        }

        /// <summary>Повторяющиеся и конфликтующие знаки: «,,» → «,», «!!» → «!», «,.» → «.», «....» → «...».</summary>
        public static string NormalizeWordPunctuation(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            t = Regex.Replace(t, @"\.{4,}", "...");
            t = Regex.Replace(t, @"(?<!\.)\.\.(?!\.)", ".");
            t = Regex.Replace(t, @",{2,}", ",");
            t = Regex.Replace(t, @"!{2,}", "!");
            t = Regex.Replace(t, @"\?{2,}", "?");
            t = Regex.Replace(t, @";{2,}", ";");
            t = Regex.Replace(t, @":{2,}", ":");
            t = Regex.Replace(t, @"[,;:]+([.!?…])", "$1");
            t = Regex.Replace(t, @"([.!?…])[,;:]+", "$1");
            t = Regex.Replace(t, @"…\.+", "…");
            return t;
        }

        // ---------- 3. Границы сегментов ----------

        private int DecideSegmentBoundaries(List<W> words, TextProcessingOptions options, RussianSpellChecker speller)
        {
            int added = 0;
            for (int i = 0; i < words.Count - 1; i++)
            {
                var w = words[i];
                if (!w.SegmentEnd || w.Text == "—") continue;
                var next = words[i + 1];
                if (HasTerminal(w.Text)) continue;

                var trailing = TrailingPunct(w.Text);
                if (trailing.EndsWith(",", StringComparison.Ordinal) || trailing.EndsWith(";", StringComparison.Ordinal))
                {
                    // «…, Затем» → «…, затем», если это нарицательное слово (не имя).
                    var core = Core(next.Text);
                    if (StartsUpper(next.Text) && core.Length > 1 && !IsAllUpper(core) && core != "Я" && speller != null && speller.IsCommonWord(core))
                        next.Text = LowerFirst(next.Text);
                    continue;
                }
                if (trailing.Length > 0) continue;

                var nextCore = Core(next.Text);
                bool nextUpper = StartsUpper(next.Text) && !IsAllUpper(nextCore) && nextCore != "Я";
                double gapMs = Gap(w, next) * 1000;
                bool pause = !double.IsNaN(gapMs) && gapMs >= options.SentencePauseMs;
                bool common = speller != null && nextCore.Length > 1 && speller.IsCommonWord(nextCore);
                bool marker = MatchMarker(words, i + 1) > 0;
                bool prevFunction = FunctionWords.Contains(Core(w.Text).ToLowerInvariant());

                if (prevFunction) continue;
                if (nextUpper && (pause || common || marker))
                {
                    w.Text += ".";
                    added++;
                }
                else if (!nextUpper && marker && pause && WordsSinceSentenceStart(words, i) >= 3)
                {
                    w.Text += ".";
                    next.Text = UpperFirst(next.Text);
                    added++;
                }
            }
            return added;
        }

        // ---------- 4. Слитные фразы внутри сегментов ----------

        private int SplitRunOns(List<W> words, TextProcessingOptions options, RussianSpellChecker speller, HashSet<string> custom)
        {
            int added = 0;
            for (int i = 1; i < words.Count; i++)
            {
                var prev = words[i - 1];
                var cur = words[i];
                if (prev.Text == "—" || cur.Text == "—") continue;
                if (TrailingPunct(prev.Text).Length > 0) continue;

                var prevCore = Core(prev.Text).ToLowerInvariant();
                if (prevCore.Length == 0 || FunctionWords.Contains(prevCore)) continue;
                int sinceStart = WordsSinceSentenceStart(words, i - 1);
                if (sinceStart < 3) continue;
                int sincePunct = WordsSincePunctuation(words, i - 1);

                var curCore = Core(cur.Text);
                bool split = false;

                // а) Слово-связка («далее», «после этого»…) после содержательного слова,
                //    за которой идут ещё минимум два слова (иначе это может быть наречие в конце фразы).
                int markerLen = MatchMarker(words, i);
                if (markerLen > 0 && sincePunct >= 3 && WordsUntilPunctuation(words, i + markerLen) >= 2)
                    split = true;

                // б) Движок начал слово с заглавной (нарицательное, не имя и не аббревиатура) без точки перед ним.
                // Местоимения («Вы», «Вам») с заглавной пишут из уважения — это не начало предложения.
                bool politePronoun = FunctionWords.Contains(curCore.ToLowerInvariant()) || curCore.StartsWith("Ваш", StringComparison.Ordinal);
                if (!split && StartsUpper(cur.Text) && curCore.Length > 1 && !IsAllUpper(curCore) && curCore != "Я" && !politePronoun
                    && !custom.Contains(NormalizeKey(curCore)) && speller != null && speller.IsCommonWord(curCore))
                    split = true;

                // в) Длинная пауза в речи и заглавная буква после неё.
                if (!split && StartsUpper(cur.Text) && !IsAllUpper(curCore) && curCore != "Я" && !politePronoun)
                {
                    var gap = Gap(prev, cur) * 1000;
                    if (!double.IsNaN(gap) && gap >= options.SentencePauseMs * 1.5 && !custom.Contains(NormalizeKey(curCore)))
                        split = true;
                }

                if (split)
                {
                    prev.Text += ".";
                    cur.Text = UpperFirst(cur.Text);
                    added++;
                }
            }
            return added;
        }

        private static int MatchMarker(List<W> words, int index)
        {
            foreach (var marker in SentenceMarkers)
            {
                if (index + marker.Length > words.Count) continue;
                bool ok = true;
                for (int k = 0; k < marker.Length && ok; k++)
                {
                    var core = Core(words[index + k].Text).ToLowerInvariant();
                    ok = core == marker[k];
                    // Внутри многословной связки не должно быть точки: «после. этого» — не связка.
                    if (ok && k < marker.Length - 1 && HasTerminal(words[index + k].Text)) ok = false;
                }
                if (ok) return marker.Length;
            }
            return 0;
        }

        private static int WordsSinceSentenceStart(List<W> words, int index)
        {
            int n = 0;
            for (int k = index; k >= 0; k--)
            {
                if (k < index && HasTerminal(words[k].Text)) break;
                if (words[k].Text != "—") n++;
            }
            return n;
        }

        private static int WordsSincePunctuation(List<W> words, int index)
        {
            int n = 0;
            for (int k = index; k >= 0; k--)
            {
                if (k < index && TrailingPunct(words[k].Text).Length > 0) break;
                if (words[k].Text == "—" && k < index) break;
                n++;
            }
            return n;
        }

        private static int WordsUntilPunctuation(List<W> words, int index)
        {
            int n = 0;
            for (int k = index; k < words.Count; k++)
            {
                if (words[k].Text == "—") break;
                n++;
                if (TrailingPunct(words[k].Text).Length > 0) break;
            }
            return n;
        }

        // ---------- 5. Орфография ----------

        private void SpellCheck(List<W> words, TextProcessingOptions options, RussianSpellChecker speller, HashSet<string> custom,
            ProcessedTranscript result, string fileName)
        {
            var unknown = new List<string>();
            var fixes = new List<string>();
            var started = DateTime.UtcNow;
            foreach (var w in words)
            {
                var core = Core(w.Text);
                if (core.Length < 4 || !core.All(RussianSpellChecker.IsCyrillicLetter)) continue;
                if (custom.Contains(NormalizeKey(core))) continue;
                if (speller.IsKnown(core)) continue;
                result.UnknownWords++;
                if (unknown.Count < 200) unknown.Add(core);

                if (options.SpellCheckMode != SpellCheckMode.SafeFix) continue;
                // Слова с заглавной буквы могут быть именами — их не исправляем.
                if (!char.IsLower(core[0]) || core.Length < 5) continue;
                // Ограничение по времени: подбор вариантов в Hunspell небыстрый.
                if ((DateTime.UtcNow - started).TotalSeconds > 20) continue;
                var fix = speller.SuggestSafeFix(core);
                if (fix == null) continue;
                int pos = w.Text.IndexOf(core, StringComparison.Ordinal);
                if (pos < 0) continue;
                w.Text = w.Text.Substring(0, pos) + fix + w.Text.Substring(pos + core.Length);
                result.SpellingCorrections++;
                fixes.Add($"{core} → {fix}");
            }
            if (fixes.Count > 0)
                _log?.Info($"Исправлено очевидных опечаток: {fixes.Count}", fileName, "Обработка текста", string.Join(", ", fixes.Take(100)));
            if (unknown.Count > 0)
                _log?.Debug($"Слов, которых нет в словаре (оставлены как есть): {result.UnknownWords}", fileName, "Обработка текста",
                    string.Join(", ", unknown.Distinct().Take(100)));
        }

        // ---------- 6. Предложения ----------

        private static void BuildSentences(List<W> words, ProcessedTranscript result)
        {
            var current = new List<W>();
            void Close()
            {
                if (current.Count == 0) return;
                current[0].Text = UpperFirst(current[0].Text);
                var sb = new StringBuilder();
                foreach (var w in current)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(w.Text);
                }
                var text = sb.ToString();
                text = Regex.Replace(text, @"\s+([,.!?;:…»)])", "$1");
                text = Regex.Replace(text, @"([«(])\s+", "$1");
                text = Regex.Replace(text, @"\s{2,}", " ").Trim();
                if (text.Length > 0)
                {
                    result.Sentences.Add(new TranscriptSentence
                    {
                        Start = current[0].Start,
                        End = current[current.Count - 1].End,
                        Text = text,
                        StartEstimated = current[0].Estimated
                    });
                }
                current.Clear();
            }

            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (current.Count == 0 && w.Text == "—") continue;
                current.Add(w);
                bool abbreviationInside = IsAbbreviation(w.Text) && i + 1 < words.Count && !StartsUpper(words[i + 1].Text);
                if (HasTerminal(w.Text) && !abbreviationInside) Close();
            }
            if (current.Count > 0)
            {
                // Конец записи: если последняя фраза оборвана без знака, ставим точку — это форма, а не содержание.
                var last = current[current.Count - 1];
                if (last.Text.Length > 0 && char.IsLetterOrDigit(last.Text[last.Text.Length - 1])) last.Text += ".";
                Close();
            }
        }

        // ---------- Вспомогательные ----------

        private static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.Ordinal)
        {
            "т.е", "т.д", "т.п", "т.к", "т.н", "т.ч", "и.о", "др", "пр", "см", "руб", "коп", "г", "гг", "в", "вв",
            "тыс", "млн", "млрд", "ул", "д", "кв", "стр", "рис", "напр", "ок", "мин", "сек", "проц", "с", "е", "п"
        };

        /// <summary>Сокращение с точкой («т.е.», «г.», «руб.»): точка в нём не обязательно конец предложения.</summary>
        private static bool IsAbbreviation(string t)
        {
            if (!t.EndsWith(".", StringComparison.Ordinal)) return false;
            var core = t.TrimEnd('.').TrimStart('(', '«').ToLowerInvariant().Replace(" ", "");
            return Abbreviations.Contains(core);
        }

        private static double Gap(W a, W b)
        {
            if (a.Estimated || b.Estimated) return double.NaN;
            return b.Start - a.End;
        }

        private static readonly char[] ClosingChars = { '»', '"', ')', '\'', '”' };

        public static bool HasTerminal(string t)
        {
            var s = (t ?? "").TrimEnd(ClosingChars);
            return s.Length > 0 && ".!?…".IndexOf(s[s.Length - 1]) >= 0;
        }

        private static string TrailingPunct(string t)
        {
            int i = t.Length;
            while (i > 0 && (char.IsPunctuation(t[i - 1]) && t[i - 1] != '-' || Array.IndexOf(ClosingChars, t[i - 1]) >= 0)) i--;
            var tail = t.Substring(i);
            return tail.TrimEnd(ClosingChars).Length == 0 ? "" : tail;
        }

        /// <summary>Слово без окружающих знаков препинания и кавычек.</summary>
        public static string Core(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            int a = 0, b = t.Length;
            while (a < b && !char.IsLetterOrDigit(t[a])) a++;
            while (b > a && !char.IsLetterOrDigit(t[b - 1])) b--;
            return t.Substring(a, b - a);
        }

        private static bool StartsUpper(string t)
        {
            foreach (var c in t)
                if (char.IsLetter(c)) return char.IsUpper(c);
            return false;
        }

        private static bool IsAllUpper(string core) => core.Length > 1 && core.Where(char.IsLetter).All(char.IsUpper);

        private static string UpperFirst(string t)
        {
            for (int i = 0; i < t.Length; i++)
                if (char.IsLetter(t[i]))
                    return t.Substring(0, i) + char.ToUpper(t[i]) + t.Substring(i + 1);
            return t;
        }

        private static string LowerFirst(string t)
        {
            for (int i = 0; i < t.Length; i++)
                if (char.IsLetter(t[i]))
                    return t.Substring(0, i) + char.ToLower(t[i]) + t.Substring(i + 1);
            return t;
        }

        private static string NormalizeKey(string w) => (w ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
    }
}
