using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MasterVks.Audio;
using MasterVks.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MasterVks.Checks
{
    /// <summary>Что известно о передаче микрофонов в Телемост.</summary>
    internal sealed class FeedProbe
    {
        public bool Enabled;
        public FeedState State;
        public string Error;
        /// <summary>Номера микрофонов, которые передаются.</summary>
        public List<int> Mics = new List<int>();
        /// <summary>Дошёл ли проверочный сигнал до «CABLE Output» (null — не проверяли).</summary>
        public bool? Heard;
        public string CaptureError;
        /// <summary>Windows выводит весь звук в виртуальный кабель (бывает сразу после установки).</summary>
        public bool DefaultRenderIsCable;
        /// <summary>«CABLE Output» выбран микрофоном по умолчанию в Windows.</summary>
        public bool DefaultCaptureIsCable;
    }

    /// <summary>Правила оценки передачи в Телемост. Без обращений к Windows, проверяются автотестами.</summary>
    internal static class FeedRules
    {
        public const string InstallSteps =
            "1. Откройте страницу vb-audio.com/Cable и скачайте «VB-CABLE Driver Pack» (бесплатно).\n" +
            "2. Распакуйте архив, нажмите правой кнопкой на VBCABLE_Setup_x64.exe и выберите «Запуск от имени администратора».\n" +
            "3. Нажмите «Install Driver» и перезагрузите компьютер.\n" +
            "4. Проверьте, что звук Windows по-прежнему идёт на телевизор, а не в «CABLE Input».\n" +
            "5. В Телемосте выберите микрофон «CABLE Output».";

        public static HealthVerdict Verdict(FeedProbe p)
        {
            if (!p.Enabled)
                return new HealthVerdict(CheckStatus.Skipped, "Выключено",
                    "Если Телемост должен слышать все микрофоны, включите «Передавать микрофоны в Телемост» в настройках, раздел «Звук».");
            if (p.State == FeedState.NoDriver)
                return new HealthVerdict(CheckStatus.Warning, "Нужен бесплатный драйвер VB-Cable",
                    "Телемост умеет брать только один микрофон. Программа сводит все микрофоны в один виртуальный, а для этого нужен драйвер VB-Cable:\n" + InstallSteps,
                    true);
            if (p.State == FeedState.Error)
                return new HealthVerdict(CheckStatus.Warning, "Не работает: " + (p.Error ?? "неизвестная ошибка"),
                    "Программа сама пробует подключиться снова каждые несколько секунд. Если не помогает, перезагрузите компьютер или переустановите VB-Cable.");
            if (p.Mics.Count == 0)
                return new HealthVerdict(CheckStatus.Warning, "Не выбран ни один микрофон",
                    "Выберите микрофоны в списках слева: их звук и пойдёт в Телемост.");
            if (p.DefaultRenderIsCable)
                return new HealthVerdict(CheckStatus.Warning, "Звук Windows уходит в виртуальный кабель, а не на телевизор",
                    "Так бывает сразу после установки VB-Cable: собеседников не будет слышно. Откройте «Параметры → Звук → Вывод» и выберите телевизор или колонки.");
            if (p.Heard == false)
                return new HealthVerdict(CheckStatus.Warning, "Звук не доходит до микрофона «CABLE Output»",
                    (string.IsNullOrEmpty(p.CaptureError) ? "" : p.CaptureError + ". ") +
                    "Проверьте, что «CABLE Output» не выключен в «Параметры → Звук → Ввод». Если не поможет, переустановите VB-Cable и перезагрузите компьютер.");

            string details = "В Телемосте в настройках звука выберите микрофон «CABLE Output (VB-Audio Virtual Cable)»" +
                             (p.DefaultCaptureIsCable ? " (он же выбран по умолчанию в Windows)" : "") + ". " +
                             "Громче звучит тот микрофон, рядом с которым говорят, остальные приглушаются. Эхо убирает сам Телемост. " +
                             "Голос доходит до Телемоста примерно на 0,1–0,15 секунды позже, в разговоре это незаметно. Пока программа открыта, передача работает и без записи.";
            return new HealthVerdict(CheckStatus.Ok, "Работает: в Телемост идут " + MicsText(p.Mics), details);
        }

        /// <summary>«микрофон 1», «микрофоны 1 и 2», «микрофоны 1, 2 и 3».</summary>
        public static string MicsText(IList<int> mics)
        {
            if (mics.Count == 1) return "микрофон " + mics[0];
            string head = string.Join(", ", mics.Take(mics.Count - 1));
            return "микрофоны " + head + " и " + mics[mics.Count - 1];
        }
    }

    /// <summary>Проверка на деле: неслышный тон в кабель и запись с «CABLE Output».</summary>
    internal static class CableLoopTest
    {
        public const double ProbeFrequency = 15000;
        private static readonly Guid FloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        /// <summary>Вызывать из фонового (MTA) потока.</summary>
        public static void Run(MicFeed feed, FeedProbe probe)
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                probe.DefaultRenderIsCable = DefaultIsCable(enumerator, DataFlow.Render, Role.Multimedia);
                probe.DefaultCaptureIsCable = DefaultIsCable(enumerator, DataFlow.Capture, Role.Communications) ||
                                              DefaultIsCable(enumerator, DataFlow.Capture, Role.Console);

                MMDevice device = null;
                foreach (MMDevice d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    try
                    {
                        if (VirtualCable.IsOutput(d.FriendlyName))
                        {
                            device = d;
                            break;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                if (device == null)
                {
                    probe.Heard = false;
                    probe.CaptureError = "В Windows нет включённого микрофона «CABLE Output»";
                    return;
                }

                var samples = new List<float>();
                int rate = 48000;
                WasapiCapture capture = null;
                try
                {
                    capture = new WasapiCapture(device, false, 50);
                    WaveFormat format = capture.WaveFormat;
                    rate = format.SampleRate;
                    capture.DataAvailable += (s, e) =>
                    {
                        lock (samples) Append(samples, e.Buffer, e.BytesRecorded, format);
                    };
                    capture.StartRecording();
                    Thread.Sleep(300);
                    int startAt;
                    lock (samples) startAt = samples.Count;
                    feed.Probe(ProbeFrequency, 700);
                    Thread.Sleep(1000);
                    capture.StopRecording();
                    Thread.Sleep(100);

                    float[] x;
                    lock (samples) x = samples.ToArray();
                    // Ищем тон в окне после его начала (с запасом на задержку буферов).
                    double best = 0;
                    int window = rate / 5;
                    for (int from = startAt; from + window <= x.Length; from += window / 2)
                        best = Math.Max(best, SignalMath.ToneAmplitude(x, from, window, ProbeFrequency, rate));
                    probe.Heard = best > 0.0015;
                    Log.Info("Микрофоны в Телемост: проверочный тон на «CABLE Output» " + (best * 1000).ToString("0.0") + "‰ (" + x.Length + " сэмплов, " + rate + " Гц)");
                }
                catch (Exception ex)
                {
                    Log.Warn("Не удалось послушать «CABLE Output»", ex);
                    probe.Heard = false;
                    probe.CaptureError = "Не удалось послушать «CABLE Output»: " + AudioSource.Humanize(ex);
                }
                finally
                {
                    if (capture != null) capture.Dispose();
                }
            }
        }

        private static bool DefaultIsCable(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
        {
            try
            {
                if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return false;
                string name = enumerator.GetDefaultAudioEndpoint(flow, role).FriendlyName;
                return flow == DataFlow.Render ? VirtualCable.IsInput(name) : VirtualCable.IsOutput(name);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Первый канал записи в float, для форматов, которые отдаёт Windows.</summary>
        private static void Append(List<float> into, byte[] buffer, int bytes, WaveFormat format)
        {
            int channels = Math.Max(1, format.Channels);
            int bits = format.BitsPerSample;
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
            var ext = format as WaveFormatExtensible;
            if (ext != null) isFloat = ext.SubFormat == FloatSubFormat;
            int step = bits / 8 * channels;
            for (int pos = 0; pos + step <= bytes; pos += step)
            {
                float v;
                if (isFloat && bits == 32) v = BitConverter.ToSingle(buffer, pos);
                else if (bits == 16) v = BitConverter.ToInt16(buffer, pos) / 32768f;
                else if (bits == 24) v = ((buffer[pos] << 8 | buffer[pos + 1] << 16 | buffer[pos + 2] << 24) >> 8) / 8388608f;
                else if (bits == 32) v = BitConverter.ToInt32(buffer, pos) / 2147483648f;
                else v = 0;
                into.Add(v);
            }
        }
    }
}
