using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using MasterVks.Audio;
using MasterVks.Checks;
using MasterVks.Core;
using MasterVks.Recording;
using NAudio.Wave;

namespace MasterVks.SelfTest
{
    internal static class Program
    {
        private static int _passed, _failed;
        private static string _ffmpeg;
        private static string _work;

        private static int Main()
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Log.Init();
            _ffmpeg = FindFfmpeg();
            _work = Path.Combine(Path.GetTempPath(), "mastervks-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_work);
            Console.WriteLine("Рабочая папка: " + _work);
            Console.WriteLine("ffmpeg: " + (_ffmpeg ?? "не найден — тесты FFmpeg будут пропущены"));

            Run("Кольцевой буфер", TestRing);
            Run("Ворота записи (паузы)", TestGate);
            Run("Выравнивание часов устройства: +150 ppm", () => TestDrift(150, 0));
            Run("Выравнивание часов устройства: −150 ppm", () => TestDrift(-150, 0));
            Run("Выравнивание часов + рывки доставки до ±9 мс", () => TestDrift(80, 9));
            Run("Выравнивание часов −200 ppm + рывки до ±15 мс", () => TestDrift(-200, 15));
            Run("Пропадание и возвращение устройства", TestDeviceGap);
            Run("Конвертер: 44.1 кГц стерео float → 48 кГц моно", TestConverterResample);
            Run("Конвертер: PCM16 / PCM24", TestConverterPcm);
            Run("Гёрцель: точность амплитуды", TestGoertzel);
            Run("Анализ: тишина, гул, перегруз, мелодия", TestAnalyzer);
            Run("Фрагмент записи: отбор звука по времени и паузам", TestSegmentGating);
            Run("Фрагмент записи: сведение каналов и ограничитель", TestSegmentMix);
            Run("Умное сведение: без обработки звук проходит точно, с задержкой 1024 сэмпла", TestRoomMixerTransparent);
            Run("Умное сведение: эхо из телевизора в комнате с тремя микрофонами", TestRoomMixerEcho);
            Run("Командная строка Windows: экранирование", TestQuoting);
            Run("Человеческие подписи", TestFmt);
            Run("Имена папок и файлов с названием ВКС", TestNames);
            Run("Старые настройки: MKV теперь сохраняется по умолчанию", TestSettingsMigration);
            Run("Здоровье: правила оценки", TestHealthRules);
            Run("Здоровье: предупреждения во время работы", TestHealthWatch);
            if (_ffmpeg != null)
            {
                Run("FFmpeg: запись по аргументам программы (видео + 7 каналов звука)", TestFfmpegRecording);
                Run("FFmpeg: MKV → MP4, проверка и удаление MKV", TestFinalizeSingle);
                Run("FFmpeg: склейка двух частей после сбоя", TestFinalizeMulti);
                Run("FFmpeg: в папке остаются и MKV, и MP4", TestFinalizeKeepBoth);
                Run("FFmpeg: восстановление оборванной записи (имитация синего экрана)", TestFinalizeTruncated);
            }

            Console.WriteLine();
            Console.WriteLine("ИТОГО: пройдено " + _passed + ", не пройдено " + _failed);
            try
            {
                Directory.Delete(_work, true);
            }
            catch (Exception)
            {
            }
            return _failed == 0 ? 0 : 1;
        }

        // ===================== Инфраструктура =====================

        private static void Run(string name, Action test)
        {
            Console.WriteLine();
            Console.WriteLine("▶ " + name);
            int before = _failed;
            try
            {
                test();
            }
            catch (Exception ex)
            {
                Fail("исключение: " + ex);
            }
            Console.WriteLine(_failed == before ? "  ✔ OK" : "  ✘ ЕСТЬ ОШИБКИ");
        }

        private static void Check(bool condition, string what)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("    ✓ " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _failed++;
            Console.WriteLine("    ✗ " + what);
        }

        private static string FindFfmpeg()
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                foreach (string name in new[] { "ffmpeg", "ffmpeg.exe" })
                {
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        private static T GetField<T>(object obj, string name)
        {
            return (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        }

        // ===================== Тесты =====================

        private static void TestRing()
        {
            var ring = new FloatRing(10, 2);
            var data = Enumerable.Range(0, 16).Select(i => (float)i).ToArray();
            ring.Write(data, 8);
            Check(ring.FramesAvailable == 8, "записано 8 кадров");
            var dst = new float[12];
            int got = ring.Read(dst, 0, 6);
            Check(got == 6 && dst[0] == 0 && dst[11] == 11, "прочитано 6 кадров по порядку");
            ring.Write(data, 8); // переход через край
            Check(ring.FramesAvailable == 10, "при переполнении остаётся ёмкость буфера");
            dst = new float[20];
            ring.Read(dst, 0, 10);
            Check(dst[0] == 12 && dst[1] == 13 && dst[19] == 15, "выброшены самые старые данные, порядок сохранён (" + dst[0] + ")");
        }

        private static void TestGate()
        {
            var gate = new ActivityGate();
            long s = Clock.Frequency;
            Check(!gate.EverOpened && gate.ActiveSeconds(10 * s) == 0, "закрытые ворота — ноль секунд");
            gate.Open(1 * s);
            gate.Close(3 * s);
            gate.Open(5 * s);
            Check(Math.Abs(gate.ActiveSeconds(6 * s) - 3) < 1e-6, "2 с записи + пауза + 1 с = 3 с");
            Check(gate.IsOpenAt(2 * s) && !gate.IsOpenAt(4 * s) && gate.IsOpenAt(7 * s), "IsOpenAt учитывает паузу");
            Check(gate.AnyOpenBetween(2 * s, 4 * s) && !gate.FullyOpenBetween(2 * s, 4 * s), "частично открытый промежуток распознан");
            Check(!gate.AnyOpenBetween(3 * s, 5 * s), "пауза целиком распознана");
            gate.Open(8 * s);
            Check(Math.Abs(gate.ActiveSeconds(6 * s) - 3) < 1e-6, "повторное открытие уже открытых ворот ничего не ломает");
        }

        /// <summary>
        /// Устройство присылает звук пакетами по ~20 мс со своей (чуть неточной) частотой,
        /// сведение забирает по 10 мс. За 30 минут не должно быть ни одного разрыва,
        /// а запас в буфере должен оставаться в разумных пределах.
        /// </summary>
        private static void TestDrift(double ppm, int jitterMs)
        {
            var src = new AudioSource("тест", false);
            src.TestSetActive();
            var rnd = new Random(1);
            double deviceRate = 48000 * (1 + ppm / 1e6);
            double devicePos = 0;
            long deviceSent = 0;
            double phase = 0;
            double nextPacketMs = 0;
            var block = new float[480];
            int minFill = int.MaxValue, maxFill = 0;
            double maxJump = 0;
            float last = 0;
            bool started = false;
            long underrunsAtStart = 0;
            const int totalMs = 30 * 60 * 1000;

            for (int ms = 0; ms < totalMs; ms++)
            {
                if (ms >= nextPacketMs)
                {
                    devicePos = (ms + 20) / 1000.0 * deviceRate;
                    int frames = (int)(devicePos - deviceSent);
                    var packet = new float[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        packet[i] = (float)(0.5 * Math.Sin(phase));
                        phase += 2 * Math.PI * 440 / deviceRate;
                    }
                    src.TestInject(packet, frames);
                    deviceSent += frames;
                    nextPacketMs = ms + 20 + (jitterMs > 0 ? rnd.Next(-jitterMs, jitterMs + 1) : 0);
                }
                if (ms % 10 == 9)
                {
                    int real = src.ReadBlock(block, 480);
                    if (!started && real == 480)
                    {
                        started = true;
                        underrunsAtStart = src.Underruns;
                        last = block[0];
                    }
                    if (started && ms > 5000)
                    {
                        int fill = src.TestBufferedFrames;
                        minFill = Math.Min(minFill, fill);
                        maxFill = Math.Max(maxFill, fill);
                        for (int i = 0; i < 480; i++)
                        {
                            maxJump = Math.Max(maxJump, Math.Abs(block[i] - last));
                            last = block[i];
                        }
                    }
                    else if (started)
                    {
                        last = block[479];
                    }
                }
            }
            long underruns = src.Underruns - underrunsAtStart;
            Check(underruns == 0, "разрывов звука за 30 минут: " + underruns);
            Check(minFill > 300 && maxFill < 48000 * 160 / 1000, "запас в буфере от " + minFill * 1000 / 48000 + " до " + maxFill * 1000 / 48000 + " мс");
            Check(maxJump < 0.08, "звук непрерывный, максимальный скачок между сэмплами " + maxJump.ToString("0.000") + " (норма для синуса ≈ 0.029)");
        }

        private static void TestDeviceGap()
        {
            var src = new AudioSource("тест", false);
            src.TestSetActive();
            var block = new float[480];
            var packet = Enumerable.Repeat(0.25f, 960).ToArray();
            int realTotal = 0, blocks = 0;
            for (int t = 0; t < 400; t++) // 4 секунды, на 2-й секунде устройство пропадает на 0,5 с
            {
                bool deviceAlive = t < 200 || t >= 250;
                if (deviceAlive && t % 2 == 0) src.TestInject(packet, 960);
                realTotal += src.ReadBlock(block, 480);
                blocks++;
            }
            Check(blocks * 480 == 400 * 480, "сведение выдало ровно 4 секунды, несмотря на пропадание");
            Check(realTotal > 48000 * 3.2 && realTotal < 48000 * 3.6, "настоящего звука около 3,4 с (" + realTotal / 48 + " мс), остальное — тишина");
            Check(src.Underruns >= 1, "разрыв замечен и посчитан");
        }

        private static byte[] FloatBytes(float[] samples)
        {
            var b = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, b, 0, b.Length);
            return b;
        }

        private static void TestConverterResample()
        {
            var conv = new CaptureConverter(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), 1);
            var output = new List<float>();
            float[] buf = new float[0];
            double phase = 0;
            for (int chunk = 0; chunk < 200; chunk++) // 2 секунды пачками по 10 мс
            {
                var s = new float[441 * 2];
                for (int i = 0; i < 441; i++)
                {
                    float v = (float)(0.5 * Math.Sin(phase));
                    phase += 2 * Math.PI * 1000 / 44100;
                    s[i * 2] = v;
                    s[i * 2 + 1] = v;
                }
                byte[] bytes = FloatBytes(s);
                int n = conv.Process(bytes, bytes.Length, ref buf);
                for (int i = 0; i < n; i++) output.Add(buf[i]);
            }
            Check(Math.Abs(output.Count - 96000) < 200, "2 с при 44,1 кГц дали " + output.Count + " сэмплов при 48 кГц (ожидалось ≈ 96000)");
            float[] arr = output.ToArray();
            double amp = SignalMath.ToneAmplitude(arr, 24000, 48000, 1000, 48000);
            double leak = SignalMath.ToneAmplitude(arr, 24000, 48000, 1102.5, 48000);
            Check(Math.Abs(amp - 0.5) < 0.03, "амплитуда 1 кГц сохранилась: " + amp.ToString("0.000"));
            Check(leak < 0.01, "высота тона не уехала (на 1102 Гц " + leak.ToString("0.0000") + ")");
        }

        private static void TestConverterPcm()
        {
            var c16 = new CaptureConverter(new WaveFormat(48000, 16, 1), 1);
            var data = new byte[] { 0x00, 0x40, 0x00, 0xC0 }; // +0.5, −0.5
            float[] buf = new float[0];
            int n = c16.Process(data, data.Length, ref buf);
            Check(n == 2 && Math.Abs(buf[0] - 0.5f) < 1e-4 && Math.Abs(buf[1] + 0.5f) < 1e-4, "PCM16 → float");

            var c24 = new CaptureConverter(new WaveFormat(48000, 24, 2), 2);
            data = new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 }; // L=+0.5, R=−0.5
            n = c24.Process(data, data.Length, ref buf);
            Check(n == 1 && Math.Abs(buf[0] - 0.5f) < 1e-4 && Math.Abs(buf[1] + 0.5f) < 1e-4, "PCM24 стерео → float стерео");

            var cMono = new CaptureConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 1);
            data = FloatBytes(new[] { 0.2f, 0.6f });
            n = cMono.Process(data, data.Length, ref buf);
            Check(n == 1 && Math.Abs(buf[0] - 0.4f) < 1e-6, "стерео-микрофон сводится в моно (среднее)");

            var cSurround = new CaptureConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 6), 2);
            data = FloatBytes(new[] { 0.1f, 0.2f, 0.5f, 0f, 0f, 0f });
            n = cSurround.Process(data, data.Length, ref buf);
            Check(n == 1 && Math.Abs(buf[0] - (0.1f + 0.5f * 0.7071f)) < 1e-4, "5.1 → стерео: центральный канал (речь) не теряется");
        }

        private static float[] Sine(double freq, double amp, int frames, int rate = 48000)
        {
            var x = new float[frames];
            for (int i = 0; i < frames; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
            return x;
        }

        private static void TestGoertzel()
        {
            float[] x = Sine(1000, 0.3, 48000);
            double a = SignalMath.ToneAmplitude(x, 0, 4800, 1000, 48000);
            Check(Math.Abs(a - 0.3) < 0.01, "амплитуда 0.3 измерена как " + a.ToString("0.0000"));
            double other = SignalMath.ToneAmplitude(x, 0, 4800, 1500, 48000);
            Check(other < 0.003, "на соседней частоте почти ноль: " + other.ToString("0.00000"));
        }

        private static void TestAnalyzer()
        {
            var rnd = new Random(7);
            int n = 48000 * 4;
            Func<double, float[]> noise = amp =>
            {
                var x = new float[n];
                for (int i = 0; i < n; i++) x[i] = (float)((rnd.NextDouble() * 2 - 1) * amp * Math.Sqrt(3));
                return x;
            };

            float[] quiet = noise(0.003); // ≈ −50 дБ
            SourceAnalysis a = AudioAnalyzer.Analyze(quiet, n, n, 9600, -1, -1, null);
            Check(Math.Abs(a.NoiseFloorDb + 50.5) < 2 && !a.Hum && !a.AllZero && a.ClipShare == 0, "обычный тихий фон: " + a.NoiseFloorDb.ToString("0.0") + " дБ, гула нет");

            float[] hum = noise(0.0005);
            float[] h50 = Sine(50, 0.01, n);
            for (int i = 0; i < n; i++) hum[i] += h50[i];
            a = AudioAnalyzer.Analyze(hum, n, n, 9600, -1, -1, null);
            Check(a.Hum, "гул 50 Гц обнаружен (" + a.HumDb.ToString("0.0") + " дБ)");

            a = AudioAnalyzer.Analyze(new float[n], n, n, 9600, -1, -1, null);
            Check(a.AllZero, "полная цифровая тишина распознана");

            float[] loud = Sine(300, 1.3, n).Select(v => Math.Max(-1f, Math.Min(1f, v))).ToArray();
            a = AudioAnalyzer.Analyze(loud, n, n, 9600, -1, -1, null);
            Check(a.ClipShare > 0.002, "перегруз распознан (" + (a.ClipShare * 100).ToString("0.0") + " % сэмплов)");

            float[] withTone = noise(0.002);
            int toneStart = 48000, toneEnd = 48000 + 48000 * 6 / 10;
            float[] t880 = Sine(880, 0.05, 48000 * 3 / 10);
            int delay = 48000 * 12 / 100; // звук пришёл в микрофон с задержкой 120 мс
            for (int i = 0; i < t880.Length; i++) withTone[toneStart + delay + i] += t880[i];
            a = AudioAnalyzer.Analyze(withTone, n, n, 9600, toneStart, toneEnd, new double[] { 880, 1320, 1760 });
            Check(a.ToneSnrDb > 15, "мелодия услышана: на " + a.ToneSnrDb.ToString("0") + " дБ громче фона");
            int onsetMs = (a.ToneOnset - toneStart) * 1000 / 48000;
            Check(Math.Abs(onsetMs - 120) <= 25, "момент появления мелодии определён: " + onsetMs + " мс (на самом деле 120)");

            a = AudioAnalyzer.Analyze(noise(0.002), n, n, 9600, toneStart, toneEnd, new double[] { 880, 1320, 1760 });
            Check(a.ToneSnrDb < 10, "без мелодии ложного срабатывания нет (" + a.ToneSnrDb.ToString("0") + " дБ)");

            var probe = new float[n];
            float[] p14 = Sine(14000, 0.001, 48000);
            Array.Copy(p14, 0, probe, toneStart + 4800, p14.Length);
            a = AudioAnalyzer.Analyze(probe, n, n, 9600, toneStart, toneEnd + 48000, new double[] { 14000 });
            Check(a.ToneSnrDb >= 20, "неслышный сигнал 14 кГц (−60 дБ) уверенно обнаружен: " + a.ToneSnrDb.ToString("0") + " дБ");
        }

        private static AudioBlock Block(long ticks, float m1, float m2, float sl, float sr, float m3 = 0f)
        {
            var b = new AudioBlock
            {
                SourceTicks = ticks,
                Frames = 480,
                Mic1 = Enumerable.Repeat(m1, 480).ToArray(),
                Mic2 = Enumerable.Repeat(m2, 480).ToArray(),
                Mic3 = Enumerable.Repeat(m3, 480).ToArray(),
                Sys = new float[960],
                Mic1Real = 480,
                Mic2Real = 480,
                Mic3Real = 480,
                SysReal = 480
            };
            for (int i = 0; i < 480; i++)
            {
                b.Sys[i * 2] = sl;
                b.Sys[i * 2 + 1] = sr;
            }
            return b;
        }

        private static void TestSegmentGating()
        {
            var seg = new RecordingSegment(1, Path.Combine(_work, "x.mkv"), 16);
            long f = Clock.Frequency;
            long t0 = 100 * f;
            seg.Gate.Open(t0 + f / 200); // ворота открылись посреди блока (+5 мс)
            seg.Gate.Close(t0 + f / 2);   // пауза с 0,5 с
            seg.Gate.Open(t0 + f * 7 / 10); // продолжение с 0,7 с
            long blockTicks = f / 100;
            for (int i = -10; i < 120; i++) seg.OnAudioBlock(Block(t0 + i * blockTicks, 0.1f, 0.1f, 0, 0), true, true, true, true);
            long samples = GetField<long>(seg, "_audioSamples");
            long expected = (long)(48000 * (1.2 - 0.005 - 0.2));
            Check(Math.Abs(samples - expected) <= 2, "взято " + samples + " сэмплов, ожидалось " + expected + " (до старта и в паузе — ничего)");

            seg.Finish(t0 + f * 3 / 2, true);
            Check(seg.AudioCompleted.WaitOne(0), "при остановке поток звука закрыт");
        }

        private static void TestSegmentMix()
        {
            var seg = new RecordingSegment(1, Path.Combine(_work, "y.mkv"), 16);
            seg.Gate.Open(0);
            seg.OnAudioBlock(Block(Clock.Frequency, 0.5f, 0.5f, 0.2f, -0.2f, 0.3f), true, true, true, true);
            var queue = GetField<BlockingCollection<byte[]>>(seg, "_audioQueue");
            byte[] bytes;
            Check(queue.TryTake(out bytes) && bytes.Length == 480 * 7 * 4, "блок 10 мс = 480 кадров × 7 каналов");
            var s = new float[7];
            Buffer.BlockCopy(bytes, 0, s, 0, 28);
            Check(Math.Abs(s[2] - 0.5f) < 1e-6 && Math.Abs(s[3] - 0.5f) < 1e-6 && Math.Abs(s[4] - 0.3f) < 1e-6 && Math.Abs(s[5] - 0.2f) < 1e-6 && Math.Abs(s[6] + 0.2f) < 1e-6,
                "отдельные дорожки без изменений: мик1=" + s[2] + " мик2=" + s[3] + " мик3=" + s[4] + " L=" + s[5] + " R=" + s[6]);
            Check(s[0] < 1f && s[0] > 0.85f, "общая дорожка не перегружена (ограничитель): L=" + s[0].ToString("0.000"));
            double expectedRight = (0.5 + 0.5 + 0.3) / Math.Sqrt(3) - 0.2;
            Check(Math.Abs(s[1] - expectedRight) < 0.01, "правый канал: три микрофона + звук компьютера = " + s[1].ToString("0.000") + " (ожидалось " + expectedRight.ToString("0.000") + ")");

            var seg2 = new RecordingSegment(2, Path.Combine(_work, "z.mkv"), 16);
            seg2.Gate.Open(0);
            seg2.OnAudioBlock(Block(Clock.Frequency, 0.3f, 0.4f, 0.1f, 0.1f, 0.6f), true, false, false, false);
            GetField<BlockingCollection<byte[]>>(seg2, "_audioQueue").TryTake(out bytes);
            Buffer.BlockCopy(bytes, 0, s, 0, 28);
            Check(Math.Abs(s[0] - 0.3f) < 1e-6 && Math.Abs(s[3] - 0.4f) < 1e-6 && Math.Abs(s[4] - 0.6f) < 1e-6,
                "можно убрать микрофоны 2, 3 и звук компьютера из общей дорожки, отдельные остаются");
        }

        // ===================== Умное сведение =====================

        /// <summary>Похожий на речь сигнал: слоги с меняющимся тоном и паузы.</summary>
        private static float[] Speech(int seed, int samples, double level)
        {
            var rnd = new Random(seed);
            var x = new float[samples];
            int pos = 0;
            double phase = 0;
            while (pos < samples)
            {
                int on = (int)(48000 * (0.12 + rnd.NextDouble() * 0.25));
                int off = (int)(48000 * (0.04 + rnd.NextDouble() * (rnd.NextDouble() < 0.15 ? 0.6 : 0.15)));
                double f0 = 100 + rnd.NextDouble() * 120, f1 = f0 * (0.8 + rnd.NextDouble() * 0.4);
                double amp = level * (0.5 + rnd.NextDouble() * 0.7);
                for (int i = 0; i < on && pos < samples; i++, pos++)
                {
                    double t = i / (double)on;
                    double f = f0 + (f1 - f0) * t;
                    phase += 2 * Math.PI * f / 48000;
                    double v = 0;
                    for (int h = 1; h <= 25; h++) v += Math.Sin(phase * h + h * 0.7) / h;
                    double env = Math.Sin(Math.PI * t);
                    x[pos] = (float)(amp * env * (0.6 * v + 0.15 * (rnd.NextDouble() * 2 - 1)));
                }
                pos += off;
            }
            return x;
        }

        /// <summary>Комната: задержка, затухающий отзвук (гребенчатые фильтры) и громкость.</summary>
        private static float[] Room(float[] src, int delay, double gain, int seed)
        {
            var y = new float[src.Length];
            int[] combs = { 1499 + seed * 37, 2311 + seed * 53, 3121 + seed * 17 };
            var outs = new float[combs.Length][];
            for (int c = 0; c < combs.Length; c++) outs[c] = new float[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                float dry = i >= delay ? src[i - delay] : 0f;
                float wet = 0;
                for (int c = 0; c < combs.Length; c++)
                {
                    float fb = i >= combs[c] ? outs[c][i - combs[c]] : 0f;
                    outs[c][i] = dry + 0.55f * fb;
                    wet += outs[c][i];
                }
                y[i] = (float)(gain * (dry + 0.25f * (wet - combs.Length * dry)));
            }
            return y;
        }

        private static AudioBlock MakeBlock(float[] m1, float[] m2, float[] m3, float[] sys, int offset)
        {
            var b = new AudioBlock { Frames = 480, Mic1 = new float[480], Mic2 = new float[480], Mic3 = new float[480], Sys = new float[960] };
            for (int i = 0; i < 480; i++)
            {
                int j = offset + i;
                b.Mic1[i] = m1 != null && j < m1.Length ? m1[j] : 0f;
                b.Mic2[i] = m2 != null && j < m2.Length ? m2[j] : 0f;
                b.Mic3[i] = m3 != null && j < m3.Length ? m3[j] : 0f;
                float v = sys != null && j < sys.Length ? sys[j] : 0f;
                b.Sys[i * 2] = v;
                b.Sys[i * 2 + 1] = v;
            }
            return b;
        }

        private static float[] RunMixer(RoomMixer mixer, float[] m1, float[] m2, float[] m3, float[] sys, int samples)
        {
            var result = new float[samples];
            var l = new float[480];
            var r = new float[480];
            for (int off = 0; off + 480 <= samples; off += 480)
            {
                mixer.Process(MakeBlock(m1, m2, m3, sys, off), l, r);
                Array.Copy(l, 0, result, off, 480);
            }
            return result;
        }

        private static double Energy(float[] x, int from, int to)
        {
            double e = 0;
            for (int i = Math.Max(0, from); i < Math.Min(to, x.Length); i++) e += x[i] * x[i];
            return e;
        }

        private static void TestRoomMixerTransparent()
        {
            int n = 48000 * 2;
            var rnd = new Random(5);
            var noise = new float[n];
            for (int i = 0; i < n; i++) noise[i] = (float)(0.3 * (rnd.NextDouble() * 2 - 1));
            float[] outp = RunMixer(new RoomMixer(true, true, false, false, false), noise, null, null, null, n);
            double err = 0, sig = 0;
            for (int i = 4800; i < n; i++)
            {
                double d = outp[i] - noise[i - RoomMixer.Latency];
                err += d * d;
                sig += noise[i - RoomMixer.Latency] * noise[i - RoomMixer.Latency];
            }
            Check(10 * Math.Log10(sig / Math.Max(err, 1e-20)) > 60, "один микрофон, собеседники молчат: сигнал совпадает с исходным (точность " + (10 * Math.Log10(sig / Math.Max(err, 1e-20))).ToString("0") + " дБ)");

            var impulse = new float[n];
            impulse[1000] = 0.5f;
            float[] sysOut = RunMixer(new RoomMixer(true, true, true, true, true), null, null, null, impulse, n);
            int peak = Array.IndexOf(sysOut, sysOut.Max());
            Check(peak == 1000 + RoomMixer.Latency && Math.Abs(sysOut[peak] - 0.5f) < 1e-6, "звук собеседников в общей дорожке задержан ровно настолько же: " + (peak - 1000) + " сэмплов");

            var silent = RunMixer(new RoomMixer(true, true, true, true, true), new float[n], new float[n], new float[n], new float[n], n);
            Check(silent.All(v => v == 0f), "тишина на входе: тишина на выходе, без шума и ошибок");

            float[] simple = RunMixer(new RoomMixer(false, true, false, false, false), noise, null, null, null, n);
            Check(simple.Take(1000).Select((v, i) => Math.Abs(v - noise[i])).Max() < 1e-6, "умное сведение выключено: как раньше, без задержки");
        }

        private static void TestRoomMixerEcho()
        {
            const int rate = 48000;
            int n = rate * 50;
            // Собеседники говорят первые 25 с, человек в комнате 25–40 с, последние 10 с говорят одновременно.
            float[] far = Speech(11, n, 0.25);
            for (int i = 25 * rate; i < 40 * rate; i++) far[i] = 0;
            float[] near = Speech(23, n, 0.3);
            for (int i = 0; i < 25 * rate; i++) near[i] = 0;

            int tv = (int)(0.150 * rate);
            var rnd = new Random(3);
            Func<float[], float[], float[], float[]> sum = (a, b, c) =>
            {
                var r = new float[n];
                for (int i = 0; i < n; i++) r[i] = a[i] + b[i] + (c != null ? c[i] : 0f) + (float)(0.0005 * (rnd.NextDouble() * 2 - 1));
                return r;
            };
            // Микрофоны 1 и 2 на столе (говорящий ближе к первому), микрофон 3 над телевизором.
            float[] m1 = sum(Room(near, 0, 1.0, 1), Room(far, tv + 400, 0.25, 2), null);
            float[] m2 = sum(Room(near, 240, 0.35, 3), Room(far, tv + 300, 0.3, 4), null);
            float[] m3 = sum(Room(near, 600, 0.15, 5), Room(far, tv, 0.9, 6), null);
            float[] farOnly = Room(far, 0, 1.0, 9);

            var smart = new RoomMixer(true, true, true, true, false);
            var plain = new RoomMixer(false, true, true, true, false);
            var watch = Stopwatch.StartNew();
            float[] outSmart = RunMixer(smart, m1, m2, m3, far, n);
            double seconds = watch.Elapsed.TotalSeconds;
            float[] outPlain = RunMixer(plain, m1, m2, m3, far, n);

            Check(outSmart.All(v => !float.IsNaN(v) && !float.IsInfinity(v)), "без сбоев в числах");
            Check(seconds < 5, "50 секунд звука обработаны за " + seconds.ToString("0.00") + " с (запас по скорости больше чем в 10 раз)");

            for (int k = 0; k < 3; k++)
            {
                int ms = smart.EchoDelayMs(k);
                int expected = k == 0 ? 158 : k == 1 ? 156 : 150;
                Check(ms >= 0 && Math.Abs(ms - expected) <= 25, "микрофон " + (k + 1) + ": задержка телевизора найдена: " + ms + " мс (на самом деле " + expected + ")");
            }

            int L = RoomMixer.Latency;
            double echoPlain = Energy(outPlain, 12 * rate, 25 * rate);
            double echoSmart = Energy(outSmart, 12 * rate + L, 25 * rate + L);
            double erle = 10 * Math.Log10(echoPlain / Math.Max(echoSmart, 1e-12));
            Check(erle > 12, "говорят только собеседники: эхо в общей дорожке тише на " + erle.ToString("0.0") + " дБ");

            double nearPlain = Energy(outPlain, 28 * rate, 40 * rate);
            double nearSmart = Energy(outSmart, 28 * rate + L, 40 * rate + L);
            double nearDb = 10 * Math.Log10(nearSmart / nearPlain);
            Check(nearDb > -3, "говорит только человек в комнате: голос не приглушён (" + nearDb.ToString("+0.0;-0.0") + " дБ к простому сведению)");

            // Одновременный разговор: сколько голоса из комнаты осталось (по совпадению с чистым голосом в микрофоне 1).
            float[] nearInMic1 = Room(near, 0, 1.0, 1);
            double dot = 0, ref2 = 0;
            for (int i = 40 * rate; i < 50 * rate - L; i++)
            {
                dot += outSmart[i + L] * nearInMic1[i];
                ref2 += nearInMic1[i] * nearInMic1[i];
            }
            double kept = dot / ref2;
            Check(kept > 0.4, "говорят одновременно: голос из комнаты сохранён (" + (20 * Math.Log10(Math.Max(kept, 1e-6))).ToString("0.0") + " дБ)");

            // Телевизор с «улучшайзерами» картинки запаздывает сильнее; звук там же по одному микрофону.
            int n2 = rate * 20;
            float[] far2 = Speech(31, n2, 0.25);
            float[] tvMic = Room(far2, (int)(0.320 * rate), 0.8, 7);
            var slow = new RoomMixer(true, true, false, false, false);
            float[] o2 = RunMixer(slow, tvMic, null, null, far2, n2);
            float[] p2 = RunMixer(new RoomMixer(false, true, false, false, false), tvMic, null, null, far2, n2);
            double erle2 = 10 * Math.Log10(Energy(p2, 8 * rate, 20 * rate - L) / Math.Max(Energy(o2, 8 * rate + L, 20 * rate), 1e-12));
            Check(slow.EchoDelayMs(0) >= 300 && slow.EchoDelayMs(0) <= 350 && erle2 > 12,
                "медленный телевизор (320 мс): задержка " + slow.EchoDelayMs(0) + " мс, эхо тише на " + erle2.ToString("0.0") + " дБ");
        }

        /// <summary>Разбор командной строки по правилам Windows (CommandLineToArgvW).</summary>
        private static List<string> ParseWindows(string cmd)
        {
            var args = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false, any = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                char c = cmd[i];
                if (c == '\\')
                {
                    int n = 0;
                    while (i < cmd.Length && cmd[i] == '\\')
                    {
                        n++;
                        i++;
                    }
                    if (i < cmd.Length && cmd[i] == '"')
                    {
                        cur.Append('\\', n / 2);
                        if (n % 2 == 1) cur.Append('"');
                        else inQuotes = !inQuotes;
                    }
                    else
                    {
                        cur.Append('\\', n);
                        i--;
                    }
                    any = true;
                }
                else if (c == '"')
                {
                    inQuotes = !inQuotes;
                    any = true;
                }
                else if ((c == ' ' || c == '\t') && !inQuotes)
                {
                    if (any) args.Add(cur.ToString());
                    cur.Clear();
                    any = false;
                }
                else
                {
                    cur.Append(c);
                    any = true;
                }
            }
            if (any) args.Add(cur.ToString());
            return args;
        }

        private static void TestQuoting()
        {
            var samples = new[]
            {
                "-i", @"\\.\pipe\MasterVKS-1-video", @"C:\Записи ВКС\2026-10-18\2026-10-18_14-30_ВКС.mkv", @"C:\path with space\",
                "say \"hi\"", @"x\\""y", "", "title=Все вместе",
                "[0:v]scale=out_color_matrix=bt709,format=yuv420p[v];[1:a]pan=stereo|c0=c0|c1=c1[mix]", @"a\b\c"
            };
            List<string> parsed = ParseWindows(CommandLine.Join(samples));
            Check(parsed.Count == samples.Length, "количество аргументов сохранилось (" + parsed.Count + ")");
            bool same = parsed.Count == samples.Length && !samples.Where((s, i) => parsed[i] != s).Any();
            Check(same, "каждый аргумент дошёл до ffmpeg без изменений");
        }

        private static void TestFmt()
        {
            Check(Fmt.Plural(1, "разрыв", "разрыва", "разрывов") == "разрыв" && Fmt.Plural(3, "разрыв", "разрыва", "разрывов") == "разрыва"
                  && Fmt.Plural(11, "разрыв", "разрыва", "разрывов") == "разрывов" && Fmt.Plural(21, "разрыв", "разрыва", "разрывов") == "разрыв", "склонения");
            Check(Fmt.Duration(TimeSpan.FromMinutes(80)) == "1 ч 20 мин" && Fmt.Duration(TimeSpan.FromMinutes(45)) == "45 мин", "длительность");
            Check(Fmt.Timer(TimeSpan.FromSeconds(3725)) == "01:02:05", "таймер");
            Check(Fmt.Size(1536L * 1024 * 1024).StartsWith("1,5"), "размер: " + Fmt.Size(1536L * 1024 * 1024));
        }

        private static void TestNames()
        {
            var t = new DateTime(2026, 10, 18, 16, 57, 30);
            Check(RecordingNames.FolderName("", t) == "2026-10-18" && RecordingNames.BaseName("  ", t) == "2026-10-18_16-57_ВКС",
                "без названия: 2026-10-18 / 2026-10-18_16-57_ВКС");
            Check(RecordingNames.FolderName("Планёрка", t) == "Планёрка_2026-10-18" && RecordingNames.BaseName("Планёрка", t) == "Планёрка_2026-10-18_16-57_ВКС",
                "с названием: Планёрка_2026-10-18 / Планёрка_2026-10-18_16-57_ВКС");
            string odd = RecordingNames.Sanitize("  Отдел: продажи/ВКС?*  ");
            Check(odd == "Отдел_ продажи_ВКС__", "запрещённые в Windows символы заменены: «" + odd + "»");
            Check(RecordingNames.Sanitize("Итоги...") == "Итоги", "точки в конце убраны (Windows их не любит)");
            Check(RecordingNames.Sanitize(new string('а', 100)).Length == RecordingNames.MaxNameLength, "слишком длинное название обрезано");
        }

        private static void TestSettingsMigration()
        {
            string xml = "<?xml version=\"1.0\"?><AppSettings><OutputFolder>C:\\Записи</OutputFolder><KeepMkvAfterConvert>false</KeepMkvAfterConvert></AppSettings>";
            var old = (AppSettings)new System.Xml.Serialization.XmlSerializer(typeof(AppSettings)).Deserialize(new StringReader(xml));
            old.Normalize();
            Check(old.KeepMkvAfterConvert && old.SettingsVersion == AppSettings.CurrentSettingsVersion, "у старых настроек включилось сохранение MKV");
            old.KeepMkvAfterConvert = false;
            old.Normalize();
            Check(!old.KeepMkvAfterConvert, "если потом выключить вручную, выбор сохраняется");
            var fresh = new AppSettings();
            fresh.Normalize();
            Check(fresh.KeepMkvAfterConvert && fresh.MeetingName == "", "новые настройки: MKV сохраняется, название пустое");
        }

        private static void TestHealthRules()
        {
            Check(HealthRules.Power(true, 100, true, false, false).Status == CheckStatus.Ok, "от сети: всё хорошо");
            HealthVerdict battery = HealthRules.Power(false, 80, true, false, false);
            Check(battery.Status == CheckStatus.Warning && battery.Summary.Contains("батареи"), "от батареи: предупреждение «" + battery.Summary + "»");
            Check(HealthRules.Power(false, 20, true, false, false).Status == CheckStatus.Error, "от батареи и мало заряда: ошибка");
            Check(HealthRules.Power(true, 100, true, false, true).Status == CheckStatus.Warning, "схема «Экономия энергии»: предупреждение");

            Check(HealthRules.InActiveHours(8, 17, 10) && !HealthRules.InActiveHours(8, 17, 17) && !HealthRules.InActiveHours(8, 17, 7), "часы активности 8–17");
            Check(HealthRules.InActiveHours(6, 0, 6) && HealthRules.InActiveHours(6, 0, 23) && !HealthRules.InActiveHours(6, 0, 3), "часы активности через полночь 6–24");
            Check(HealthRules.InActiveHours(22, 6, 23) && HealthRules.InActiveHours(22, 6, 2) && !HealthRules.InActiveHours(22, 6, 12), "часы активности 22–6");

            HealthVerdict pending = HealthRules.Updates(true, false, 8, 17, 10, TimeSpan.FromDays(3));
            Check(pending.Status == CheckStatus.Warning && pending.CanFix && pending.Details.Contains("3 дня"), "ждёт перезагрузки: предупреждение, можно защитить");
            HealthVerdict outside = HealthRules.Updates(false, false, 8, 17, 19, TimeSpan.Zero);
            Check(outside.Status == CheckStatus.Warning && outside.CanFix, "вне часов активности без защиты: предупреждение");
            HealthVerdict inside = HealthRules.Updates(false, false, 8, 17, 12, TimeSpan.Zero);
            Check(inside.Status == CheckStatus.Ok && inside.CanFix, "в часы активности: зелёный, но защиту можно включить");
            HealthVerdict guarded = HealthRules.Updates(false, true, 6, 0, 3, TimeSpan.Zero);
            Check(guarded.Status == CheckStatus.Ok && !guarded.CanFix, "защита включена: зелёный, исправлять нечего");

            Check(HealthRules.Processor(20, 8000, 16000, null).Status == CheckStatus.Ok, "процессор и память в норме");
            Check(HealthRules.Processor(95, 8000, 16000, null).Status == CheckStatus.Warning, "процессор загружен: предупреждение");
            Check(HealthRules.Processor(10, 600, 8000, null).Status == CheckStatus.Warning, "мало памяти: предупреждение");
            Check(HealthRules.Processor(10, 8000, 16000, 97).Status == CheckStatus.Warning, "горячо: предупреждение");

            var good = new List<DiskInfo> { new DiskInfo { Name = "NVMe", Health = "Healthy", Media = "SSD" } };
            Check(HealthRules.Disks(good, 900).Status == CheckStatus.Ok, "исправный SSD");
            Check(HealthRules.Disks(new List<DiskInfo> { new DiskInfo { Name = "HDD", Health = "Unhealthy" } }, 50).Status == CheckStatus.Error, "неисправный диск: ошибка");
            Check(HealthRules.Disks(good, 8).Status == CheckStatus.Warning, "медленная запись: предупреждение");
            Check(HealthRules.Disks(new List<DiskInfo>(), 300).Status == CheckStatus.Ok, "состояние неизвестно, но запись работает: зелёный");

            Check(HealthRules.Stability(0, 0, 0, null, true).Status == CheckStatus.Ok, "сбоев не было");
            Check(HealthRules.Stability(1, 0, 0, DateTime.Now, true).Status == CheckStatus.Ok, "одно выключение (могли выключить кнопкой): без тревоги");
            HealthVerdict bsod = HealthRules.Stability(1, 2, 0, DateTime.Now, true);
            Check(bsod.Status == CheckStatus.Warning && bsod.Summary.Contains("2 синих экрана"), "синие экраны: предупреждение «" + bsod.Summary + "»");
        }

        private static void TestHealthWatch()
        {
            var w = new HealthWatch();
            double t = 0;
            Func<HealthSample, List<Notice>> step = s => { t += 5; return w.Feed(s, t); };

            List<Notice> n = step(new HealthSample { OnAc = true, BatteryPercent = 100, CpuPercent = 20, AvailableMb = 8000 });
            Check(n.Count == 0 && !w.HasProblem, "всё в порядке: тишина");

            n = step(new HealthSample { OnAc = false, BatteryPercent = 90, CpuPercent = 20, AvailableMb = 8000 });
            Check(n.Count == 1 && n[0].Level == NoticeLevel.Warning && w.HasProblem, "отключили зарядку: одно предупреждение");
            n = step(new HealthSample { OnAc = false, BatteryPercent = 89, CpuPercent = 20, AvailableMb = 8000 });
            Check(n.Count == 0, "повторно не предупреждает");
            n = step(new HealthSample { OnAc = false, BatteryPercent = 25, CpuPercent = 20, AvailableMb = 8000 });
            Check(n.Count == 1 && n[0].Level == NoticeLevel.Error, "мало заряда: ошибка");
            n = step(new HealthSample { OnAc = true, BatteryPercent = 26, CpuPercent = 20, AvailableMb = 8000 });
            Check(n.Count == 1 && n[0].Level == NoticeLevel.Success && !w.HasProblem, "зарядку вернули: сообщение и значок снова зелёный");

            int cpuNotices = 0;
            for (int i = 0; i < 30; i++) cpuNotices += step(new HealthSample { OnAc = true, CpuPercent = 97, AvailableMb = 8000 }).Count;
            Check(cpuNotices == 1 && w.HasProblem, "процессор полностью загружен 2,5 минуты: ровно одно предупреждение");
            int calm = 0;
            for (int i = 0; i < 20; i++) calm += step(new HealthSample { OnAc = true, CpuPercent = 30, AvailableMb = 8000 }).Count;
            Check(calm == 1 && !w.HasProblem, "нагрузка спала: одно сообщение, что всё в норме");

            int throttle = 0;
            for (int i = 0; i < 20; i++) throttle += step(new HealthSample { OnAc = true, CpuPercent = 60, PerformancePercent = 40, AvailableMb = 8000 }).Count;
            Check(throttle == 1, "процессор сбросил частоту под нагрузкой: одно предупреждение о перегреве");

            n = step(new HealthSample { OnAc = true, CpuPercent = 20, PerformancePercent = 100, AvailableMb = 500, RebootPending = true });
            Check(n.Count == 2, "мало памяти и Windows ждёт перезагрузки: два предупреждения");
            n = step(new HealthSample { OnAc = true, CpuPercent = 20, PerformancePercent = 100, AvailableMb = 500, RebootPending = true });
            Check(n.Count == 0, "без повторов");
        }

        // ===================== FFmpeg =====================

        private static void MkFifo(string path)
        {
            using (Process p = Process.Start("mkfifo", "\"" + path + "\"")) p.WaitForExit();
        }

        /// <summary>
        /// Пишет запись теми же аргументами, что и программа, только каналы — это FIFO Linux.
        /// Видео: кадры 320×240, звук: мик1 = 440 Гц, мик2 = 660 Гц, мик3 = 550 Гц, компьютер = 880 Гц.
        /// </summary>
        private static string Record(string name, double seconds, double killAfter = -1)
        {
            string v = Path.Combine(_work, name + ".v"), a = Path.Combine(_work, name + ".a");
            MkFifo(v);
            MkFifo(a);
            string output = Path.Combine(_work, name + ".mkv");
            List<string> args = RecordingArgs.Build(v, a, 320, 240, 30, VideoQuality.Maximum, output, "Тест " + name);
            var psi = new ProcessStartInfo(_ffmpeg, CommandLine.Join(args))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            var proc = Process.Start(psi);
            proc.OutputDataReceived += (s, e) => { };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.Contains("Guessed Channel Layout")) Console.WriteLine("      ffmpeg: " + e.Data);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var start = Stopwatch.StartNew();
            var vt = new Thread(() =>
            {
                try
                {
                    using (var fs = new FileStream(v, FileMode.Open, FileAccess.Write))
                    {
                        var frame = new byte[320 * 240 * 4];
                        for (int i = 0; i < seconds * 30; i++)
                        {
                            while (start.Elapsed.TotalSeconds < i / 30.0) Thread.Sleep(1);
                            for (int p = 0; p < frame.Length; p += 4)
                            {
                                frame[p] = (byte)(i * 3);
                                frame[p + 1] = 128;
                                frame[p + 2] = (byte)(255 - i * 3);
                            }
                            fs.Write(frame, 0, frame.Length);
                        }
                    }
                }
                catch (IOException)
                {
                }
            });
            var at = new Thread(() =>
            {
                try
                {
                    using (var fs = new FileStream(a, FileMode.Open, FileAccess.Write))
                    {
                        int total = (int)(seconds * 48000);
                        for (int s0 = 0; s0 < total; s0 += 480)
                        {
                            while (start.Elapsed.TotalSeconds < s0 / 48000.0) Thread.Sleep(1);
                            var buf = new float[480 * 7];
                            for (int i = 0; i < 480; i++)
                            {
                                double t = (s0 + i) / 48000.0;
                                float m1 = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * t));
                                float m2 = (float)(0.3 * Math.Sin(2 * Math.PI * 660 * t));
                                float m3 = (float)(0.3 * Math.Sin(2 * Math.PI * 550 * t));
                                float sy = (float)(0.3 * Math.Sin(2 * Math.PI * 880 * t));
                                buf[i * 7] = (m1 + m2 + m3) * 0.577f + sy;
                                buf[i * 7 + 1] = (m1 + m2 + m3) * 0.577f + sy;
                                buf[i * 7 + 2] = m1;
                                buf[i * 7 + 3] = m2;
                                buf[i * 7 + 4] = m3;
                                buf[i * 7 + 5] = sy;
                                buf[i * 7 + 6] = sy;
                            }
                            var bytes = FloatBytes(buf);
                            fs.Write(bytes, 0, bytes.Length);
                        }
                    }
                }
                catch (IOException)
                {
                }
            });
            vt.Start();
            at.Start();
            if (killAfter > 0)
            {
                Thread.Sleep((int)(killAfter * 1000));
                proc.Kill(); // как синий экран: ffmpeg не успевает ничего дописать
                proc.WaitForExit();
                vt.Join(3000);
                at.Join(3000);
            }
            else
            {
                vt.Join();
                at.Join();
                proc.WaitForExit(30000);
            }
            return output;
        }

        private static float[] ExtractTrack(string file, int audioIndex)
        {
            string raw = Path.Combine(_work, "track" + audioIndex + ".f32");
            Ffmpeg.RunResult r = Ffmpeg.Run(_ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", file, "-map", "0:a:" + audioIndex, "-ac", "1", "-f", "f32le", "-ar", "48000", raw }, 60000);
            if (r.ExitCode != 0) return new float[0];
            byte[] b = File.ReadAllBytes(raw);
            var x = new float[b.Length / 4];
            Buffer.BlockCopy(b, 0, x, 0, x.Length * 4);
            return x;
        }

        private static void TestFfmpegRecording()
        {
            string mkv = Record("rec", 4);
            Check(File.Exists(mkv) && new FileInfo(mkv).Length > 10000, "файл MKV создан (" + (File.Exists(mkv) ? new FileInfo(mkv).Length : 0) + " байт)");
            TimeSpan? d = Ffmpeg.ProbeDuration(_ffmpeg, mkv);
            Check(d.HasValue && Math.Abs(d.Value.TotalSeconds - 4) < 0.3, "длительность " + (d.HasValue ? d.Value.TotalSeconds.ToString("0.00") : "?") + " с (ожидалось 4)");

            Ffmpeg.RunResult info = Ffmpeg.Run(_ffmpeg, new[] { "-hide_banner", "-i", mkv }, 30000);
            int audioStreams = info.Error.Split('\n').Count(l => l.Contains("Audio: aac"));
            Check(audioStreams == 5, "в файле 5 звуковых дорожек (" + audioStreams + ")");
            Check(info.Error.Contains("Video: h264") && info.Error.Contains("320x240"), "видео H.264 в исходном размере");
            Check(info.Error.Contains("Все вместе") && info.Error.Contains("Микрофон 1") && info.Error.Contains("Микрофон 3") && info.Error.Contains("Звук собеседников"), "дорожки подписаны по-русски");

            float[] mic1 = ExtractTrack(mkv, 1), mic2 = ExtractTrack(mkv, 2), mic3 = ExtractTrack(mkv, 3), sys = ExtractTrack(mkv, 4);
            Func<float[], double, double> amp = (x, fr) => x.Length > 96000 ? SignalMath.ToneAmplitude(x, 48000, 24000, fr, 48000) : 0;
            Check(amp(mic1, 440) > 0.2 && amp(mic1, 660) < 0.02, "дорожка «Микрофон 1» содержит только свой звук (440 Гц)");
            Check(amp(mic2, 660) > 0.2 && amp(mic2, 440) < 0.02, "дорожка «Микрофон 2» содержит только свой звук (660 Гц)");
            Check(amp(mic3, 550) > 0.2 && amp(mic3, 440) < 0.02, "дорожка «Микрофон 3» содержит только свой звук (550 Гц)");
            Check(amp(sys, 880) > 0.2 && amp(sys, 440) < 0.02, "дорожка «Звук собеседников» — только звук компьютера (880 Гц)");
        }

        private static void TestFinalizeSingle()
        {
            string mkv = Record("single", 3);
            var job = new PendingJournal { Mp4Path = Path.Combine(_work, "single.mp4"), Parts = new List<string> { mkv }, KeepMkv = false, Started = DateTime.Now };
            job.Save();
            FinalizeResult r = PostProcessor.Finalize(_ffmpeg, job);
            Check(r.Success && File.Exists(r.Mp4Path), "MP4 создан: " + r.Message);
            TimeSpan? d = r.Mp4Path != null ? Ffmpeg.ProbeDuration(_ffmpeg, r.Mp4Path) : null;
            Check(d.HasValue && Math.Abs(d.Value.TotalSeconds - 3) < 0.3, "длительность MP4 совпадает (" + (d.HasValue ? d.Value.TotalSeconds.ToString("0.00") : "?") + " с)");
            Check(!File.Exists(mkv), "проверенный MKV удалён, место не занимается дважды");
            Check(!PendingJournal.LoadAll().Any(j => j.Id == job.Id), "запись убрана из журнала незавершённых");
            Ffmpeg.RunResult info = Ffmpeg.Run(_ffmpeg, new[] { "-hide_banner", "-i", r.Mp4Path }, 30000);
            Check(info.Error.Split('\n').Count(l => l.Contains("Audio: aac")) == 5, "в MP4 все 5 звуковых дорожек");
        }

        private static void TestFinalizeMulti()
        {
            string p1 = Record("multi", 2);
            string p2 = Record("multi_часть2", 3);
            var job = new PendingJournal { Mp4Path = Path.Combine(_work, "multi.mp4"), Parts = new List<string> { p1, p2 }, KeepMkv = true, Started = DateTime.Now };
            FinalizeResult r = PostProcessor.Finalize(_ffmpeg, job);
            Check(r.Success, "части склеены: " + r.Message);
            TimeSpan? d = r.Mp4Path != null ? Ffmpeg.ProbeDuration(_ffmpeg, r.Mp4Path) : null;
            Check(d.HasValue && Math.Abs(d.Value.TotalSeconds - 5) < 0.4, "общая длительность 2 + 3 = " + (d.HasValue ? d.Value.TotalSeconds.ToString("0.00") : "?") + " с");
            Check(File.Exists(p1) && File.Exists(p2), "с настройкой «оставлять MKV» исходники на месте");
        }

        private static void TestFinalizeKeepBoth()
        {
            string mkv = Record("both", 2);
            var job = new PendingJournal { Mp4Path = Path.Combine(_work, "both.mp4"), Parts = new List<string> { mkv }, KeepMkv = true, Started = DateTime.Now };
            FinalizeResult r = PostProcessor.Finalize(_ffmpeg, job);
            Check(r.Success && File.Exists(Path.Combine(_work, "both.mp4")) && File.Exists(mkv), "рядом лежат both.mkv и both.mp4");
            Check(r.Message.Contains("MKV лежит рядом"), "сообщение говорит, что исходник сохранён: " + r.Message);
        }

        private static void TestFinalizeTruncated()
        {
            string mkv = Record("crash", 10, 3.5);
            Check(File.Exists(mkv), "после «аварии» файл на диске остался (" + (File.Exists(mkv) ? new FileInfo(mkv).Length : 0) + " байт)");
            var job = new PendingJournal { Mp4Path = Path.Combine(_work, "crash.mp4"), Parts = new List<string> { mkv }, KeepMkv = false, Started = DateTime.Now };
            FinalizeResult r = PostProcessor.Finalize(_ffmpeg, job);
            TimeSpan? d = r.Mp4Path != null ? Ffmpeg.ProbeDuration(_ffmpeg, r.Mp4Path) : null;
            Check(r.Success && d.HasValue && d.Value.TotalSeconds > 1.5, "запись восстановлена: " + (d.HasValue ? d.Value.TotalSeconds.ToString("0.0") : "?") + " с из ~3 с до сбоя");
            Check(File.Exists(mkv), "оборванный MKV не удалён (длительность не проверить — оставляем на всякий случай)");
        }
    }
}
