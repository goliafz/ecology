using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MasterVks.Core;
using MasterVks.Recording;

namespace MasterVks.Video
{
    /// <summary>Камера, как её видит Windows (DirectShow).</summary>
    internal sealed class CameraDevice
    {
        public string Name;
        /// <summary>Внутреннее имя устройства: уникально, даже если у двух камер одинаковые названия.</summary>
        public string AltName;

        /// <summary>Что передавать в ffmpeg: внутреннее имя надёжнее, в нём нет двоеточий и кириллицы.</summary>
        public string OpenName
        {
            get { return string.IsNullOrEmpty(AltName) ? Name : AltName; }
        }

        /// <summary>Программные «камеры» (OBS, ManyCam и т. п.): по умолчанию их не выбираем.</summary>
        public bool IsVirtual
        {
            get
            {
                string n = (Name ?? "").ToLowerInvariant();
                return n.Contains("virtual") || n.Contains("obs") || n.Contains("manycam") || n.Contains("snap camera") ||
                       n.Contains("xsplit") || n.Contains("nvidia broadcast") || n.Contains("виртуал");
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>Список камер через ffmpeg (DirectShow), тот же способ, каким камера потом открывается.</summary>
    internal static class CameraDevices
    {
        public static List<CameraDevice> List(string ffmpegPath)
        {
            return TryList(ffmpegPath) ?? new List<CameraDevice>();
        }

        /// <summary>Список камер или null, если ffmpeg не ответил (тогда нельзя считать, что камер нет).</summary>
        public static List<CameraDevice> TryList(string ffmpegPath)
        {
            if (ffmpegPath == null) return null;
            Ffmpeg.RunResult r = Ffmpeg.Run(ffmpegPath, new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" }, 15000);
            if (r.TimedOut || r.Error.IndexOf("dshow", StringComparison.OrdinalIgnoreCase) < 0) return null;
            return Parse(r.Error);
        }

        /// <summary>
        /// Разбирает вывод «ffmpeg -list_devices true -f dshow -i dummy».
        /// Понимает и новый формат (‹"Имя" (video)›), и старый (заголовки разделов).
        /// </summary>
        public static List<CameraDevice> Parse(string text)
        {
            var result = new List<CameraDevice>();
            if (string.IsNullOrEmpty(text)) return result;
            bool oldVideoSection = false;
            CameraDevice last = null;
            bool lastIsVideo = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = Regex.Replace(raw.TrimEnd('\r'), @"^\s*\[[^\]]*\]\s?", "");
                if (line.IndexOf("DirectShow video devices", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    oldVideoSection = true;
                    continue;
                }
                if (line.IndexOf("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    oldVideoSection = false;
                    continue;
                }

                Match alt = Regex.Match(line, "^\\s*Alternative name\\s+\"(.*)\"\\s*$");
                if (alt.Success)
                {
                    if (last != null && lastIsVideo && string.IsNullOrEmpty(last.AltName)) last.AltName = alt.Groups[1].Value;
                    continue;
                }

                Match modern = Regex.Match(line, "^\\s*\"(.+)\"\\s*\\(([^)]*)\\)\\s*$");
                Match plain = Regex.Match(line, "^\\s*\"(.+)\"\\s*$");
                if (modern.Success)
                {
                    last = new CameraDevice { Name = modern.Groups[1].Value };
                    lastIsVideo = modern.Groups[2].Value.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (lastIsVideo) result.Add(last);
                }
                else if (plain.Success)
                {
                    last = new CameraDevice { Name = plain.Groups[1].Value };
                    lastIsVideo = oldVideoSection;
                    if (lastIsVideo) result.Add(last);
                }
            }
            return result;
        }

        /// <summary>Ищет сохранённую камеру: сначала по внутреннему имени, потом по названию.</summary>
        public static CameraDevice Find(IList<CameraDevice> list, string name, string altName)
        {
            if (list == null) return null;
            if (!string.IsNullOrEmpty(altName))
            {
                CameraDevice byAlt = list.FirstOrDefault(c => string.Equals(c.AltName, altName, StringComparison.OrdinalIgnoreCase));
                if (byAlt != null) return byAlt;
            }
            if (string.IsNullOrEmpty(name)) return null;
            return list.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Камера по умолчанию: первая настоящая, иначе первая любая.</summary>
        public static CameraDevice PickDefault(IList<CameraDevice> list)
        {
            if (list == null || list.Count == 0) return null;
            return list.FirstOrDefault(c => !c.IsVirtual) ?? list[0];
        }

        /// <summary>Аргументы ffmpeg для открытия камеры.</summary>
        public static List<string> InputArgs(CameraDevice device)
        {
            return new List<string> { "-f", "dshow", "-rtbufsize", "64M", "-i", "video=" + device.OpenName };
        }
    }

    /// <summary>Один кадр с камеры, уменьшенный: 24 бита на точку, строки подряд (BGR).</summary>
    internal sealed class CameraFrame
    {
        public byte[] Data;
        public int Width;
        public int Height;
        /// <summary>Секунды от запуска камеры.</summary>
        public double Seconds;

        public Bitmap ToBitmap()
        {
            var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int row = Width * 3;
                for (int y = 0; y < Height; y++)
                    Marshal.Copy(Data, y * row, bd.Scan0 + y * bd.Stride, row);
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return bmp;
        }
    }

    /// <summary>
    /// Берёт картинку с камеры через ffmpeg: уменьшенные кадры приходят по трубе.
    /// Камера занята, только пока объект жив: Dispose сразу её освобождает,
    /// чтобы она была свободна для ВКС.
    /// </summary>
    internal sealed class CameraGrabber : IDisposable
    {
        private readonly Process _process;
        private readonly Thread _reader;
        private readonly StringBuilder _log = new StringBuilder();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Action<CameraFrame> _onFrame;
        private volatile bool _disposed;
        private bool _outputSeen;

        public readonly int Width;
        public readonly int Height;

        public int FramesReceived { get; private set; }
        public bool Exited { get; private set; }

        /// <summary>Размер кадра самой камеры (по сообщениям ffmpeg), если удалось узнать.</summary>
        public Size NativeSize { get; private set; }
        public double NativeFps { get; private set; }

        public string LogText
        {
            get
            {
                lock (_log) return _log.ToString();
            }
        }

        /// <param name="inputArgs">Как открыть источник (для камеры: CameraDevices.InputArgs).</param>
        public CameraGrabber(string ffmpegPath, IList<string> inputArgs, int width, int height, Action<CameraFrame> onFrame)
        {
            Width = width & ~1;
            Height = height & ~1;
            _onFrame = onFrame;
            var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "info" };
            args.AddRange(inputArgs);
            args.AddRange(new[]
            {
                "-an",
                "-vf", "scale=" + Width + ":" + Height + ":force_original_aspect_ratio=decrease,pad=" + Width + ":" + Height + ":(ow-iw)/2:(oh-ih)/2",
                "-pix_fmt", "bgr24", "-f", "rawvideo", "-"
            });
            var psi = new ProcessStartInfo(ffmpegPath, CommandLine.Join(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? AppPaths.ExeDirectory
            };
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (_log)
                {
                    if (_log.Length < 64 * 1024) _log.AppendLine(e.Data);
                }
                ParseStreamInfo(e.Data);
            };
            _process.Exited += (s, e) => Exited = true;
            _process.Start();
            _process.BeginErrorReadLine();
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Камера: кадры" };
            _reader.Start();
        }

        private void ParseStreamInfo(string line)
        {
            // Нужен только вход (сама камера); после «Output #0» идёт уже уменьшенная картинка.
            if (line.StartsWith("Output #", StringComparison.Ordinal)) _outputSeen = true;
            if (_outputSeen) return;
            if (line.IndexOf("Video:", StringComparison.Ordinal) < 0 || line.IndexOf("Stream #0:", StringComparison.Ordinal) < 0) return;
            Match size = Regex.Match(line, @"[ ,](\d{2,5})x(\d{2,5})[ ,\[]");
            if (size.Success)
                NativeSize = new Size(int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture));
            Match fps = Regex.Match(line, @"(\d+(?:\.\d+)?) fps");
            if (fps.Success) NativeFps = double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private void ReadLoop()
        {
            int frameBytes = Width * Height * 3;
            try
            {
                Stream stdout = _process.StandardOutput.BaseStream;
                while (!_disposed)
                {
                    var data = new byte[frameBytes];
                    int got = 0;
                    while (got < frameBytes)
                    {
                        int n = stdout.Read(data, got, frameBytes - got);
                        if (n <= 0) return;
                        got += n;
                    }
                    FramesReceived++;
                    if (_onFrame != null && !_disposed)
                    {
                        try
                        {
                            _onFrame(new CameraFrame { Data = data, Width = Width, Height = Height, Seconds = _clock.Elapsed.TotalSeconds });
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Ошибка обработки кадра камеры", ex);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Труба закрылась: ffmpeg завершился или его остановили.
            }
        }

        /// <summary>Отпускает камеру: просит ffmpeg закончить, а если не послушается, завершает его.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        _process.StandardInput.Write("q");
                        _process.StandardInput.Flush();
                        _process.StandardInput.Close();
                    }
                    catch (Exception)
                    {
                    }
                    if (!_process.WaitForExit(1500)) _process.Kill();
                    _process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось остановить ffmpeg камеры", ex);
            }
            _reader.Join(2000);
            _process.Dispose();
        }
    }

    /// <summary>Что видно на кадрах с камеры. Без обращений к Windows, поэтому проверяется автотестами.</summary>
    internal sealed class CameraPicture
    {
        public int Frames;
        /// <summary>Кадров в секунду, считая от первого кадра.</summary>
        public double Fps;
        /// <summary>Средняя яркость 0..255.</summary>
        public double Brightness;
        /// <summary>Разброс яркости по кадру: у однотонной картинки около нуля.</summary>
        public double Contrast;
        /// <summary>Все кадры совпадают байт в байт: камера зависла и повторяет одно и то же.</summary>
        public bool Frozen;

        public static CameraPicture Analyze(IList<CameraFrame> frames)
        {
            var p = new CameraPicture { Frames = frames.Count };
            if (frames.Count == 0) return p;
            if (frames.Count > 1)
            {
                double span = frames[frames.Count - 1].Seconds - frames[0].Seconds;
                p.Fps = span > 0 ? (frames.Count - 1) / span : 0;
            }

            // Яркость по последним кадрам: первые бывают тёмными, пока камера подстраивает выдержку.
            int from = Math.Max(0, frames.Count - 5);
            double sum = 0, contrast = 0;
            int n = 0;
            for (int i = from; i < frames.Count; i++)
            {
                double mean, std;
                Luma(frames[i], out mean, out std);
                sum += mean;
                contrast += std;
                n++;
            }
            p.Brightness = sum / n;
            p.Contrast = contrast / n;

            if (frames.Count >= 4)
            {
                bool allSame = true;
                for (int i = 1; i < frames.Count && allSame; i++)
                    if (!SameBytes(frames[i - 1].Data, frames[i].Data)) allSame = false;
                p.Frozen = allSame;
            }
            return p;
        }

        public static void Luma(CameraFrame f, out double mean, out double std)
        {
            // Каждая четвёртая точка: для оценки хватает, а считается быстро.
            double sum = 0, sum2 = 0;
            int count = 0;
            byte[] d = f.Data;
            for (int i = 0; i + 2 < d.Length; i += 12)
            {
                double y = 0.114 * d[i] + 0.587 * d[i + 1] + 0.299 * d[i + 2];
                sum += y;
                sum2 += y * y;
                count++;
            }
            mean = count > 0 ? sum / count : 0;
            std = count > 0 ? Math.Sqrt(Math.Max(0, sum2 / count - mean * mean)) : 0;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>Почему камера не открылась, по сообщениям ffmpeg.</summary>
    internal enum CameraFailure
    {
        None,
        NotFound,
        Busy,
        Other
    }

    internal static class CameraErrors
    {
        public static CameraFailure Classify(string log)
        {
            string l = (log ?? "").ToLowerInvariant();
            if (l.Contains("could not find video device") || l.Contains("could not enumerate video devices")) return CameraFailure.NotFound;
            if (l.Contains("already in use") || l.Contains("could not run graph") || l.Contains("device or resource busy") ||
                l.Contains("0x800700aa"))
                return CameraFailure.Busy;
            if (string.IsNullOrWhiteSpace(l)) return CameraFailure.None;
            return CameraFailure.Other;
        }

        /// <summary>Последняя содержательная строка ошибки ffmpeg (для подробностей).</summary>
        public static string LastError(string log)
        {
            if (string.IsNullOrEmpty(log)) return "";
            string[] lines = log.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            string found = lines.LastOrDefault(s => s.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                    s.IndexOf("could not", StringComparison.OrdinalIgnoreCase) >= 0);
            found = found ?? lines.LastOrDefault() ?? "";
            found = Regex.Replace(found, @"^\[[^\]]*\]\s*", "");
            return found.Length > 200 ? found.Substring(0, 200) + "…" : found;
        }
    }
}
