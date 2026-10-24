using System.Collections.Generic;
using System.Linq;
using VidLex.Core.Abstractions;
using VidLex.Core.Models;
using VidLex.Core.Services;
using Xunit;

namespace VidLex.Core.Tests
{
    public class TextCorrectionTests
    {
        private static readonly TextCorrectionService Service = new TextCorrectionService(new MemoryLog(), Seg.DictionaryDir);

        private static string Run(IList<TranscriptSegment> segs, TextProcessingOptions opts = null, IList<TimeRange> silences = null)
        {
            var r = Service.Process(segs, silences ?? new List<TimeRange>(), opts ?? new TextProcessingOptions());
            return string.Join(" ", r.Sentences.Select(s => s.Text));
        }

        [Fact]
        public void SplitsRunOnSentenceFromSpecification()
        {
            // Пример из ТЗ: слитная фраза без знаков препинания.
            var segs = new List<TranscriptSegment> { Seg.Make(0, 6, "Сегодня мы изучим программу далее рассмотрим настройки после этого начнём работу") };
            Assert.Equal("Сегодня мы изучим программу. Далее рассмотрим настройки. После этого начнём работу.", Run(segs));
        }

        [Fact]
        public void DoesNotSplitAfterFunctionWord()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 5, "Сегодня мы далее рассмотрим настройки программы") };
            Assert.Equal("Сегодня мы далее рассмотрим настройки программы.", Run(segs));
        }

        [Fact]
        public void DoesNotSplitOnTrailingAdverbMarker()
        {
            // «теперь» в конце фразы — наречие, а не начало нового предложения.
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "Давайте посмотрим что получилось теперь") };
            Assert.Equal("Давайте посмотрим что получилось теперь.", Run(segs));
        }

        [Fact]
        public void SegmentsAreNeverGluedTogether()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 2, "первая часть фразы"),
                Seg.Make(2.05, 4, "продолжение фразы")
            };
            var text = Run(segs);
            Assert.Equal("Первая часть фразы продолжение фразы.", text);
            Assert.DoesNotContain("фразыпродолжение", text);
        }

        [Fact]
        public void AddsPeriodAtSegmentBoundaryWhenNextStartsWithCapitalAfterPause()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 2, "Мы закончили первую часть"),
                Seg.Make(3.5, 5, "Теперь перейдём ко второй")
            };
            Assert.Equal("Мы закончили первую часть. Теперь перейдём ко второй.", Run(segs));
        }

        [Fact]
        public void DoesNotAddPeriodBeforeProperNameWithoutPause()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 2, "Мы приехали в"),
                Seg.Make(2.02, 3, "Москву вчера вечером")
            };
            Assert.Equal("Мы приехали в Москву вчера вечером.", Run(segs));
        }

        [Fact]
        public void DoesNotPunctuateEverySegmentBlindly()
        {
            // Конец сегмента ≠ конец предложения: продолжение со строчной буквы без паузы.
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 2, "Программа обрабатывает видео"),
                Seg.Make(2.05, 4, "и сохраняет результат в файл")
            };
            Assert.Equal("Программа обрабатывает видео и сохраняет результат в файл.", Run(segs));
        }

        [Fact]
        public void LowercasesCommonWordAfterCommaAtBoundary()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 2, "Сначала откроем настройки,"),
                Seg.Make(2.1, 4, "Затем выберем папку.")
            };
            Assert.Equal("Сначала откроем настройки, затем выберем папку.", Run(segs));
        }

        [Fact]
        public void NormalizesSpacesAndPunctuation()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 3, "Привет ,  это   тест !! Всё работает ,, правда ?"),
            };
            Assert.Equal("Привет, это тест! Всё работает, правда?", Run(segs));
        }

        [Fact]
        public void SplitsGluedSentenceInsideToken()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "Это первая фраза.Это вторая фраза") };
            Assert.Equal("Это первая фраза. Это вторая фраза.", Run(segs));
        }

        [Fact]
        public void KeepsUrlsAndDecimalNumbers()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "Откройте сайт example.com и введите 3,5 процента") };
            Assert.Equal("Откройте сайт example.com и введите 3,5 процента.", Run(segs));
        }

        [Fact]
        public void AbbreviationDoesNotEndSentence()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 4, "Можно выбрать формат т.е. расширение файла и т.д. по желанию.") };
            var r = Service.Process(segs, new List<TimeRange>(), new TextProcessingOptions());
            Assert.Single(r.Sentences);
        }

        [Fact]
        public void RemovesNonSpeechAnnotations()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 2, "[музыка]"),
                Seg.Make(2, 4, "Добрый день. [аплодисменты] Начинаем (смех) лекцию."),
            };
            Assert.Equal("Добрый день. Начинаем лекцию.", Run(segs));
        }

        [Fact]
        public void KeepsNonSpeechAnnotationsWhenDisabled()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 2, "Начинаем [музыка] лекцию.") };
            Assert.Contains("[музыка]", Run(segs, new TextProcessingOptions { RemoveNonSpeechAnnotations = false }));
        }

        [Fact]
        public void RemovesSubtitleCreditsHallucination()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 3, "Спасибо, что пришли на лекцию."),
                Seg.Make(30, 34, "Субтитры сделал DimaTorzok"),
            };
            Assert.Equal("Спасибо, что пришли на лекцию.", Run(segs));
        }

        [Fact]
        public void RemovesSoftHallucinationOnlyInSilence()
        {
            var silences = new List<TimeRange> { new TimeRange(10, 40) };
            var inSilence = new List<TranscriptSegment> { Seg.Make(0, 3, "Основная часть лекции."), Seg.Make(12, 16, "Продолжение следует...") };
            Assert.Equal("Основная часть лекции.", Run(inSilence, silences: silences));

            var spoken = new List<TranscriptSegment> { Seg.Make(0, 3, "Основная часть лекции."), Seg.Make(3, 6, "Продолжение следует.") };
            Assert.Equal("Основная часть лекции. Продолжение следует.", Run(spoken, silences: silences));
        }

        [Fact]
        public void CollapsesRepetitionLoop()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(0, 2, "Это важный момент лекции."),
                Seg.Make(2, 4, "Это важный момент лекции."),
                Seg.Make(4, 6, "Это важный момент лекции."),
                Seg.Make(6, 8, "Это важный момент лекции."),
                Seg.Make(8, 10, "Идём дальше."),
            };
            Assert.Equal("Это важный момент лекции. Идём дальше.", Run(segs));
        }

        [Fact]
        public void KeepsMeaningfulRepeats()
        {
            // Повтор дважды — может быть речью («Да. Да.»), не удаляется.
            var segs = new List<TranscriptSegment> { Seg.Make(0, 1, "Да, конечно."), Seg.Make(1, 2, "Да, конечно.") };
            Assert.Equal("Да, конечно. Да, конечно.", Run(segs));
        }

        [Fact]
        public void SafeSpellFixCorrectsObviousTypo()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "Откройте настройки програмы и выберите папку.") };
            var r = Service.Process(segs, new List<TimeRange>(), new TextProcessingOptions());
            Assert.Equal("Откройте настройки программы и выберите папку.", r.Sentences[0].Text);
            Assert.Equal(1, r.SpellingCorrections);
        }

        [Fact]
        public void DoesNotTouchUnknownNamesAndTerms()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "Ходаковский показал VidLex и фреймворк Kubernetes на митапе.") };
            Assert.Equal("Ходаковский показал VidLex и фреймворк Kubernetes на митапе.", Run(segs));
        }

        [Fact]
        public void CustomWordsAreNeverCorrected()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "Запустите видлексер для обработки.") };
            var withWord = Run(segs, new TextProcessingOptions { CustomWords = new List<string> { "видлексер" } });
            Assert.Contains("видлексер", withWord);
        }

        [Fact]
        public void SpellCheckOffKeepsText()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "Откройте настройки програмы.") };
            Assert.Contains("програмы", Run(segs, new TextProcessingOptions { SpellCheckMode = SpellCheckMode.Off }));
        }

        [Fact]
        public void RemovesReplacementCharacters()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "Привет ��мир.") };
            Assert.Equal("Привет мир.", Run(segs));
        }

        [Fact]
        public void NormalizesShoutedSegmentButKeepsShortAbbreviations()
        {
            var segs = new List<TranscriptSegment> { Seg.Make(0, 3, "СЕГОДНЯ МЫ ОБСУДИМ ОТЧЁТ МВД ЗА ГОД") };
            Assert.Equal("Сегодня мы обсудим отчёт МВД за год.", Run(segs));
        }

        [Fact]
        public void SentenceStartTimesComeFromWords()
        {
            var segs = new List<TranscriptSegment>
            {
                Seg.Make(3, 6, "Первая фраза здесь."),
                Seg.Make(12, 15, "Вторая фраза здесь.")
            };
            var r = Service.Process(segs, new List<TimeRange>(), new TextProcessingOptions());
            Assert.Equal(2, r.Sentences.Count);
            Assert.Equal(3, r.Sentences[0].Start, 3);
            Assert.Equal(12, r.Sentences[1].Start, 3);
        }

        [Fact]
        public void EmptyInputGivesEmptyTranscript()
        {
            var r = Service.Process(new List<TranscriptSegment>(), new List<TimeRange>(), new TextProcessingOptions());
            Assert.True(r.IsEmpty);
        }

        [Fact]
        public void DoesNotDeleteMeaningfulWords()
        {
            var text = "Ну вот, значит, мы это самое, сделали всё как надо, да, как надо.";
            var segs = new List<TranscriptSegment> { Seg.Make(0, 5, text) };
            Assert.Equal(text, Run(segs));
        }

        [Fact]
        public void PunctuationNormalizerHandlesEdgeCases()
        {
            Assert.Equal("…", TextCorrectionService.NormalizeWordPunctuation("…."));
            Assert.Equal("так...", TextCorrectionService.NormalizeWordPunctuation("так....."));
            Assert.Equal("так.", TextCorrectionService.NormalizeWordPunctuation("так.."));
            Assert.Equal("так?!", TextCorrectionService.NormalizeWordPunctuation("так?!"));
            Assert.Equal("так.", TextCorrectionService.NormalizeWordPunctuation("так,."));
        }
    }
}
