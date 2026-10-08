using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FastGen.Core;
using Xunit;

namespace FastGen.Tests
{
    public class SpinSyntaxTests
    {
        [Fact]
        public void MatchingBracketsRespectNesting()
        {
            string t = "a {b|{c|d}} e";
            Assert.Equal(10, SpinSyntax.FindMatchingClose(t, 2));
            Assert.Equal(9, SpinSyntax.FindMatchingClose(t, 5));
            Assert.Equal(2, SpinSyntax.FindMatchingOpen(t, 10));
        }

        [Fact]
        public void InnermostAndOutermostConstruct()
        {
            string t = "x {a|{b|c}|d} y";
            Assert.True(SpinSyntax.TryGetInnermostConstruct(t, t.IndexOf('b'), out var inner));
            Assert.Equal("{b|c}", t.Substring(inner.Start, inner.Length));
            Assert.True(SpinSyntax.TryGetOutermostConstruct(t, t.IndexOf('b'), out var outer));
            Assert.Equal("{a|{b|c}|d}", t.Substring(outer.Start, outer.Length));
            Assert.False(SpinSyntax.TryGetInnermostConstruct(t, 0, out _));

            string sib = "{x{a}{b}}";
            Assert.True(SpinSyntax.TryGetOutermostConstruct(sib, sib.IndexOf('b'), out var o2));
            Assert.Equal(sib, sib.Substring(o2.Start, o2.Length));
        }

        [Fact]
        public void SplitTopLevelKeepsNested()
        {
            var v = SpinSyntax.GetVariants("{качестве {HD|Full HD}|качестве|[<,> a | b ]}");
            Assert.Equal(new[] { "качестве {HD|Full HD}", "качестве", "[<,> a | b ]" }, v);
        }

        [Fact]
        public void ValidateFindsErrors()
        {
            Assert.True(SpinSyntax.Validate("{a|b} [<,> c | d ]").IsValid);
            Assert.Equal(0, SpinSyntax.Validate("{a|b").ErrorPosition);
            Assert.Equal(3, SpinSyntax.Validate("a|b}").ErrorPosition);
            Assert.False(SpinSyntax.Validate("{a|b]").IsValid);
        }

        [Fact]
        public void CountVariants()
        {
            Assert.Equal(6, SpinSyntax.CountVariants("{a|b} {c|d|e}"));
            Assert.Equal(3, SpinSyntax.CountVariants("{a|{b|c}}"));
            Assert.Equal(6, SpinSyntax.CountVariants("[<,> a | b | c ]"));
            Assert.Equal(1, SpinSyntax.CountVariants("просто текст"));
        }

        [Fact]
        public void GenerateProducesOnlyVariants()
        {
            var rnd = new Random(1);
            var seen = new HashSet<string>();
            for (int i = 0; i < 200; i++)
            {
                string s = SpinSyntax.Generate("{Хороший|Прекрасный} {фильм|{кинофильм|фильмец}}.", rnd);
                Assert.DoesNotContain("{", s);
                Assert.DoesNotContain("|", s);
                seen.Add(s);
            }
            Assert.Equal(6, seen.Count);
        }

        [Fact]
        public void GeneratePermutationWithLastSeparator()
        {
            var rnd = new Random(2);
            for (int i = 0; i < 50; i++)
            {
                string s = SpinSyntax.Generate("[<,> драмы | комедии <и>| боевики ]", rnd);
                var parts = s.Split(new[] { ", ", " и " }, StringSplitOptions.None);
                Assert.Equal(3, parts.Length);
                Assert.Contains(" и ", s);
                Assert.Equal(new[] { "боевики", "драмы", "комедии" }, parts.OrderBy(x => x));
            }

            string plain = SpinSyntax.Generate("[<,> драмы и мелодрамы | комедии | мистика ]", rnd);
            Assert.Equal(2, plain.Split(new[] { ", " }, StringSplitOptions.None).Length - 1);
        }
    }

    public class TextNavTests
    {
        [Fact]
        public void WordAtIncludesInnerHyphen()
        {
            string t = "КиноАртель - онлайн-кинотеатр, 1080p.";
            var w = TextNav.WordAt(t, t.IndexOf("кино"));
            Assert.Equal("онлайн-кинотеатр", t.Substring(w.Start, w.Length));
            var d = TextNav.WordAt(t, t.IndexOf("1080p") + 2);
            Assert.Equal("1080p", t.Substring(d.Start, d.Length));
            Assert.True(TextNav.WordAt(t, t.IndexOf(" - ") + 1).IsEmpty);
        }

        [Fact]
        public void NextTokenTreatsConstructAsOneToken()
        {
            string t = "Здесь {собраны|есть} фильмы [<,> a | b ] и <b>тег</b> http://site.ru/x тут";
            var tok = TextNav.NextToken(t, 0);
            Assert.Equal("Здесь", Sub(t, tok));
            tok = TextNav.NextToken(t, tok.Range.End);
            Assert.Equal(TokenKind.Construct, tok.Kind);
            Assert.Equal("{собраны|есть}", Sub(t, tok));
            tok = TextNav.NextToken(t, tok.Range.End);
            Assert.Equal("фильмы", Sub(t, tok));
            tok = TextNav.NextToken(t, tok.Range.End);
            Assert.Equal("и", Sub(t, tok));          // перестановка пропущена
            tok = TextNav.NextToken(t, tok.Range.End);
            Assert.Equal("тег", Sub(t, tok));        // теги пропущены
            tok = TextNav.NextToken(t, tok.Range.End);
            Assert.Equal("тут", Sub(t, tok));        // ссылка пропущена
            Assert.False(TextNav.NextToken(t, tok.Range.End).IsValid);
        }

        [Fact]
        public void PrevTokenMirrorsNext()
        {
            string t = "Здесь {собраны|есть} фильмы";
            var tok = TextNav.PrevToken(t, t.Length);
            Assert.Equal("фильмы", Sub(t, tok));
            tok = TextNav.PrevToken(t, tok.Range.Start);
            Assert.Equal("{собраны|есть}", Sub(t, tok));
            tok = TextNav.PrevToken(t, tok.Range.Start);
            Assert.Equal("Здесь", Sub(t, tok));
            Assert.False(TextNav.PrevToken(t, tok.Range.Start).IsValid);
        }

        [Fact]
        public void NextTokenFromMiddleOfWordSkipsIt()
        {
            string t = "один два";
            Assert.Equal("два", Sub(t, TextNav.NextToken(t, 2)));
            Assert.Equal("один", Sub(t, TextNav.PrevToken(t, 6)));
        }

        [Fact]
        public void ConstructNavigation()
        {
            string t = "a {b|c} d {e|f} g";
            var c1 = TextNav.NextConstruct(t, 0);
            Assert.Equal("{b|c}", Sub(t, c1));
            var c2 = TextNav.NextConstruct(t, c1.Range.End);
            Assert.Equal("{e|f}", Sub(t, c2));
            Assert.False(TextNav.NextConstruct(t, c2.Range.End).IsValid);
            Assert.Equal("{b|c}", Sub(t, TextNav.PrevConstruct(t, c2.Range.Start)));
        }

        private static string Sub(string t, TextToken tok) => t.Substring(tok.Range.Start, tok.Range.Length);
    }

    public class TextCaseTests
    {
        [Theory]
        [InlineData("компания", "фирма", "фирма")]
        [InlineData("Компания", "фирма", "Фирма")]
        [InlineData("Удобная", "очень удобная", "Очень удобная")]
        [InlineData("КОМПАНИЯ", "фирма", "ФИРМА")]
        [InlineData("Российская Федерация", "русская земля", "Русская Земля")]
        [InlineData("Я", "мы", "Мы")]
        [InlineData("сайт", "web-сайт", "web-сайт")]
        public void ApplyCase(string template, string syn, string expected)
        {
            Assert.Equal(expected, TextCase.ApplyCase(template, syn));
        }

        [Fact]
        public void Rotate()
        {
            Assert.Equal("{Семечки и кокосы|Кокосы и семечки}", TextCase.Rotate("Семечки и кокосы"));
            Assert.Equal(" {a, b|b, a} ", TextCase.Rotate(" a, b "));
            Assert.Null(TextCase.Rotate("один"));
            Assert.Equal("{РФ и США|США и РФ}", TextCase.Rotate("РФ и США"));
        }

        [Fact]
        public void EnumVariants()
        {
            Assert.Equal("[<,> драмы | комедии | детективы ]", TextCase.EnumVariant1("драмы, комедии, детективы"));
            Assert.Equal("[<,> драмы | комедии <и>| детективы ]", TextCase.EnumVariant2("драмы, комедии и детективы"));
        }

        [Fact]
        public void ToggleCases()
        {
            Assert.Equal("Хороший Фильм", TextCase.ToggleTitleCase("хороший фильм"));
            Assert.Equal("хороший фильм", TextCase.ToggleTitleCase("Хороший Фильм"));
            Assert.Equal(" ФИЛЬМ ", TextCase.ToggleUpper(" фильм "));
            Assert.Equal("фильм", TextCase.ToggleUpper("ФИЛЬМ"));
        }
    }

    public class SynonymStoreTests : IDisposable
    {
        private readonly string _dir;

        public SynonymStoreTests()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _dir = Path.Combine(Path.GetTempPath(), "fastgen-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private SynonymStore NewStore()
        {
            var s = new SynonymStore
            {
                GoldPath = Path.Combine(_dir, "GoldBase.txt"),
                UserPath = Path.Combine(_dir, "UserBase.txt"),
                UsagePath = Path.Combine(_dir, "syn_usage.txt")
            };
            return s;
        }

        [Fact]
        public void GoldGroupsAreMergedAndMutual()
        {
            var store = NewStore();
            File.WriteAllLines(store.GoldPath, new[] { "компания|организация", "компания|фирма", "абсолютно все|все" });
            store.LoadGold();

            var c = store.GetCandidates("Компания", false).Select(x => x.Text).ToList();
            Assert.Equal(new[] { "организация", "фирма" }, c);
            Assert.Equal(new[] { "компания" }, store.GetCandidates("фирма", false).Select(x => x.Text));
            Assert.Equal(new[] { "все" }, store.GetCandidates("абсолютно  все", false).Select(x => x.Text));
        }

        [Fact]
        public void GoldSynonymsSortedByCoOccurrence()
        {
            var store = NewStore();
            File.WriteAllLines(store.GoldPath, new[] { "кино|картин", "кино|фильм", "кино|фильм|кинофильм", "фильм|кино" });
            store.LoadGold();
            Assert.Equal(new[] { "фильм", "картин", "кинофильм" }, store.GetCandidates("кино", false).Select(x => x.Text));
        }

        [Fact]
        public void PriorityUserThenFrequentThenGoldThenDict()
        {
            var store = NewStore();
            File.WriteAllLines(store.GoldPath, new[] { "сайт|портал|ресурс" });
            store.LoadGold();
            store.SetDict(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["сайт"] = new[] { "веб-сайт", "портал" } });
            store.RegisterUsage("сайт", new[] { "ресурс" });
            store.SetUserEntry("Сайт", new[] { "площадка" });

            var c = store.GetCandidates("сайт", true);
            Assert.Equal(new[] { "площадка", "ресурс", "портал", "веб-сайт" }, c.Select(x => x.Text));
            Assert.Equal(new[] { SynonymSource.User, SynonymSource.Frequent, SynonymSource.Gold, SynonymSource.Dict }, c.Select(x => x.Source));
            Assert.Equal(1, c[1].UsageCount);
            Assert.DoesNotContain(store.GetCandidates("сайт", false), x => x.Source == SynonymSource.Dict);
        }

        [Fact]
        public void UserAndUsageRoundTrip()
        {
            var store = NewStore();
            store.SetUserEntry("быстро", new[] { "оперативно", "Скоро", "", "быстро" });
            store.RegisterUsage("быстро", new[] { "оперативно", "оперативно" });
            store.SaveUser();
            store.SaveUsage();

            var again = NewStore();
            again.LoadUser();
            again.LoadUsage();
            Assert.Equal(new[] { "оперативно", "скоро" }, again.GetCandidates("Быстро", false).Select(x => x.Text));
            Assert.Equal(2, again.GetUsage("быстро", "оперативно"));
        }

        [Fact]
        public void ReadsDbfWithKeyAndSynonyms()
        {
            string path = Path.Combine(_dir, "DICT.DBF");
            WriteDbf(path, new[]
            {
                new[] { "дом", "здание", "жилище" },
                new[] { "кот", "", "котик" },
                new[] { "дом", "строение", "здание" },
            }, deletedRow: 1);

            int lastProgress = -1;
            var d = SynonymStore.ReadDbf(path, (r, total) => lastProgress = r);
            Assert.Equal(3, lastProgress);
            Assert.Equal(new[] { "здание", "жилище", "строение" }, d["ДОМ"]);
            Assert.False(d.ContainsKey("кот"));      // удалённая запись
            Assert.False(d.ContainsKey("здание"));   // синоним — не ключ (формат TextExpert)
        }

        private static void WriteDbf(string path, string[][] rows, int deletedRow)
        {
            var enc = Encoding.GetEncoding(866);
            int fields = rows[0].Length;
            const int fieldLen = 30;
            int headerLen = 32 + 32 * fields + 1;
            int recordLen = 1 + fields * fieldLen;

            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((byte)0x03);
                bw.Write(new byte[] { 125, 1, 1 });
                bw.Write(rows.Length);
                bw.Write((short)headerLen);
                bw.Write((short)recordLen);
                bw.Write(new byte[20]);
                for (int f = 0; f < fields; f++)
                {
                    var name = new byte[11];
                    Encoding.ASCII.GetBytes(f == 0 ? "WORD" : "SYN" + f).CopyTo(name, 0);
                    bw.Write(name);
                    bw.Write((byte)'C');
                    bw.Write(0);
                    bw.Write((byte)fieldLen);
                    bw.Write((byte)0);
                    bw.Write(new byte[14]);
                }
                bw.Write((byte)0x0D);
                for (int r = 0; r < rows.Length; r++)
                {
                    bw.Write((byte)(r == deletedRow ? '*' : ' '));
                    foreach (var v in rows[r])
                    {
                        var buf = Enumerable.Repeat((byte)' ', fieldLen).ToArray();
                        enc.GetBytes(v.ToUpperInvariant()).CopyTo(buf, 0);
                        bw.Write(buf);
                    }
                }
                bw.Write((byte)0x1A);
            }
        }
    }

    public class AutoSpinnerTests
    {
        private static SynonymStore Store(params string[] goldLines)
        {
            string path = Path.Combine(Path.GetTempPath(), "fastgen-gold-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, goldLines);
            var s = new SynonymStore { GoldPath = path };
            s.LoadGold();
            File.Delete(path);
            return s;
        }

        [Fact]
        public void SpinsWordsKeepingCaseAndPunctuation()
        {
            var store = Store("хороший|прекрасный", "фильм|кинофильм");
            var r = AutoSpinner.Spin("Хороший фильм, хороший.", store, new SpinOptions());
            Assert.Equal("{Хороший|Прекрасный} {фильм|кинофильм}, {хороший|прекрасный}.", r.Text);
            Assert.Equal(3, r.Constructs);
        }

        [Fact]
        public void LeavesExistingConstructsTagsAndLinksAlone()
        {
            var store = Store("фильм|кинофильм", "ru|ру", "href|ссылка");
            string src = "{фильм|кино} [<,> фильм | сериал ] <a href=\"http://фильм.ru/\">фильм</a> www.film.ru фильм";
            var r = AutoSpinner.Spin(src, store, new SpinOptions());
            Assert.Equal("{фильм|кино} [<,> фильм | сериал ] <a href=\"http://фильм.ru/\">{фильм|кинофильм}</a> www.film.ru {фильм|кинофильм}", r.Text);
        }

        [Fact]
        public void PrefersLongestPhrase()
        {
            var store = Store("абсолютно все|все", "все|всё");
            var r = AutoSpinner.Spin("Здесь абсолютно все есть, все.", store, new SpinOptions());
            Assert.Equal("Здесь {абсолютно все|все} есть, {все|абсолютно все|всё}.", r.Text);
        }

        [Fact]
        public void PhraseDoesNotCrossPunctuation()
        {
            var store = Store("абсолютно все|все");
            var r = AutoSpinner.Spin("абсолютно, все", store, new SpinOptions());
            Assert.Equal("абсолютно, {все|абсолютно все}", r.Text);
        }

        [Fact]
        public void StopWordsBadPhrasesExceptionsAndLimit()
        {
            var store = Store("в|во", "буквально|практически", "большой|огромный|крупный|громадный|здоровенный|великий");
            var opt = new SpinOptions
            {
                MaxSynonyms = 2,
                BadPhrases = new List<string> { "буквально  в" },
                Exceptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["рф"] = "РФ" }
            };
            var r = AutoSpinner.Spin("в рф буквально в большой", store, opt);
            Assert.Equal("в РФ буквально в {большой|огромный|крупный}", r.Text);
        }

        [Fact]
        public void UserEntryWins()
        {
            var store = Store("большой|огромный|крупный");
            store.SetUserEntry("большой", new[] { "значительный" });
            var r = AutoSpinner.Spin("большой", store, new SpinOptions());
            Assert.Equal("{большой|значительный}", r.Text);
        }

        [Fact]
        public void SpinIsIdempotent()
        {
            var store = Store("хороший|прекрасный");
            var once = AutoSpinner.Spin("хороший день", store, new SpinOptions()).Text;
            var twice = AutoSpinner.Spin(once, store, new SpinOptions()).Text;
            Assert.Equal(once, twice);
        }
    }

    public class RtfBuilderTests
    {
        [Theory]
        [InlineData("КиноАртель — {онлайн-кинотеатр|это онлайн-кинотеатр}, [<,> драмы | комедии ] и {a|{b|c}}")]
        [InlineData("строка 1\nстрока 2\n\tтаб \\ слеш {незакрытая")]
        [InlineData("")]
        [InlineData("emoji 😀 ок")]
        public void RtfRoundTripsText(string text)
        {
            string rtf = RtfBuilder.Build(text, "Courier New", 14f);
            Assert.Equal(text, RtfToText(rtf));
            Assert.Contains(@"\fs28", rtf);
        }

        [Fact]
        public void UnmatchedBracketIsMarked()
        {
            Assert.Contains(@"\highlight6", RtfBuilder.Build("a {b|c", "Courier New", 14f));
            Assert.DoesNotContain(@"\highlight6", RtfBuilder.Build("a {b|c}", "Courier New", 14f));
        }

        /// <summary>Минимальный разбор RTF: только то, что выдаёт RtfBuilder.</summary>
        private static string RtfToText(string rtf)
        {
            var sb = new StringBuilder();
            int depth = 0;
            int skipDepth = -1;
            int i = 0;
            while (i < rtf.Length)
            {
                char c = rtf[i];
                if (c == '{') { depth++; i++; continue; }
                if (c == '}') { if (depth == skipDepth) skipDepth = -1; depth--; i++; continue; }
                if (c == '\r' || c == '\n') { i++; continue; }
                if (c == '\\')
                {
                    char nx = rtf[i + 1];
                    if (nx == '\\' || nx == '{' || nx == '}')
                    {
                        if (skipDepth < 0) sb.Append(nx);
                        i += 2;
                        continue;
                    }
                    int j = i + 1;
                    while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                    string word = rtf.Substring(i + 1, j - i - 1);
                    int k = j;
                    if (k < rtf.Length && (rtf[k] == '-' || char.IsDigit(rtf[k]))) { k++; while (k < rtf.Length && char.IsDigit(rtf[k])) k++; }
                    string num = rtf.Substring(j, k - j);
                    if (k < rtf.Length && rtf[k] == ' ') k++;
                    if (word == "fonttbl" || word == "colortbl") skipDepth = depth;
                    if (skipDepth < 0)
                    {
                        if (word == "par") sb.Append('\n');
                        else if (word == "tab") sb.Append('\t');
                        else if (word == "u")
                        {
                            sb.Append((char)(ushort)short.Parse(num));
                            if (k < rtf.Length && rtf[k] == '?') k++;
                        }
                    }
                    i = k;
                    continue;
                }
                if (skipDepth < 0 && depth >= 1) sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }

    public class ReadabilityTests
    {
        private static SynonymStore Store(params string[] goldLines)
        {
            string path = Path.Combine(Path.GetTempPath(), "fastgen-gold-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, goldLines);
            var s = new SynonymStore { GoldPath = path };
            s.LoadGold();
            File.Delete(path);
            return s;
        }

        [Fact]
        public void ContextIndexCollectsPairsAcrossConstructs()
        {
            var ctx = ContextIndex.Build(new[] { "Мы {любим|обожаем} {собирать|складывать} кубик, а потом {|быстро }решаем." });
            Assert.True(ctx.HasPair("любим", "собирать"));
            Assert.True(ctx.HasPair("обожаем", "складывать"));
            Assert.True(ctx.HasPair("складывать", "кубик"));
            Assert.True(ctx.HasPair("мы", "обожаем"));
            Assert.True(ctx.HasPair("потом", "решаем"));   // пустой вариант пропускает соседа
            Assert.True(ctx.HasPair("быстро", "решаем"));
            Assert.False(ctx.HasPair("кубик", "а"));       // через запятую пар нет
            Assert.False(ctx.HasPair("собирать", "любим"));
        }

        [Fact]
        public void ContextIndexSaveLoad()
        {
            string path = Path.Combine(Path.GetTempPath(), "fastgen-ctx-" + Guid.NewGuid().ToString("N") + ".bin");
            ContextIndex.Build(new[] { "удобная навигация" }).Save(path);
            var loaded = ContextIndex.Load(path);
            File.Delete(path);
            Assert.True(loaded.HasPair("Удобная", "навигация"));
            Assert.Equal(1, loaded.Count);
        }

        [Fact]
        public void SpinRejectsSynonymsThatNeverStoodNextToNeighbours()
        {
            var store = Store("собирать|копить|складывать");
            var ctx = ContextIndex.Build(new[] { "научиться складывать кубик", "копить деньги", "научиться копить" });
            var r = AutoSpinner.Spin("Хочу научиться собирать кубик.", store, new SpinOptions { Context = ctx });
            Assert.Equal("Хочу научиться {собирать|складывать} кубик.", r.Text);

            // без индекса — по-старому
            r = AutoSpinner.Spin("Хочу научиться собирать кубик.", store, new SpinOptions());
            Assert.Equal("Хочу научиться {собирать|копить|складывать} кубик.", r.Text);
        }

        [Fact]
        public void VariantOfOnlyStopWordsIsDropped()
        {
            var store = Store("для тех|для|для всех");
            var r = AutoSpinner.Spin("сайт для тех, кто", store, new SpinOptions());
            Assert.Equal("сайт {для тех|для всех}, кто", r.Text);
        }

        [Fact]
        public void UserBaseIgnoresContext()
        {
            var store = Store("собирать|копить");
            store.SetUserEntry("собирать", new[] { "копить" });
            var ctx = ContextIndex.Build(new[] { "что-то другое" });
            var r = AutoSpinner.Spin("научиться собирать кубик", store, new SpinOptions { Context = ctx });
            Assert.Equal("научиться {собирать|копить} кубик", r.Text);
        }

        [Fact]
        public void ProperNounsAbbreviationsAndDigitsAreSkipped()
        {
            var store = Store("куба|страна", "фридрих|богатый", "топ|рейтинг", "2х2|2 на 2", "кубик|куб", "здесь|тут");
            var r = AutoSpinner.Spin("Здесь куба Фишера, метод Фридрих и ТОП кубиков 2х2. Кубик здесь.", store, new SpinOptions());
            Assert.Equal("{Здесь|Тут} {куба|страна} Фишера, метод Фридрих и ТОП кубиков 2х2. {Кубик|Куб} {здесь|тут}.", r.Text);
        }

        [Fact]
        public void NeighboursSeeThroughConstructs()
        {
            string t = "{Удобная|Комфортная} навигация помогает";
            AutoSpinner.GetNeighbors(t, t.IndexOf("навигация"), t.IndexOf("навигация") + "навигация".Length, out var l, out var r);
            Assert.Equal("удобная", l);
            Assert.Equal("помогает", r);
            AutoSpinner.GetNeighbors("кубик, а", 0, 5, out l, out r);
            Assert.Null(l);
            Assert.Null(r);
        }

        [Fact]
        public void RejectedPairsAreNotSuggestedAndPersist()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fastgen-rej-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var store = new SynonymStore { GoldPath = Path.Combine(dir, "g.txt"), RejectedPath = Path.Combine(dir, "r.txt"), UsagePath = Path.Combine(dir, "u.txt") };
            File.WriteAllLines(store.GoldPath, new[] { "материалы|статьи|пиломатериалы" });
            store.LoadGold();
            store.RegisterUsage("материалы", new[] { "пиломатериалы" });
            store.RegisterRejected("Материалы", new[] { "Пиломатериалы" });
            store.SaveRejected();

            var again = new SynonymStore { GoldPath = store.GoldPath, RejectedPath = store.RejectedPath };
            again.LoadGold();
            again.LoadRejected();
            Assert.Equal(new[] { "статьи" }, again.GetCandidates("материалы", false).Select(c => c.Text));
            Assert.Equal(0, store.GetUsage("материалы", "пиломатериалы"));

            // записали в «Мою базу» — снова разрешено
            again.SetUserEntry("материалы", new[] { "пиломатериалы" });
            Assert.Contains("пиломатериалы", again.GetCandidates("материалы", false).Select(c => c.Text));
            Directory.Delete(dir, true);
        }
    }
}
