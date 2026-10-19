using System.Collections.Generic;
using System.Globalization;
using MasterVks.Core;

namespace MasterVks.Recording
{
    /// <summary>Параметры командной строки ffmpeg для записи и для упаковки в MP4.</summary>
    internal static class RecordingArgs
    {
        public static readonly string[] TrackTitles =
        {
            "Все вместе",
            "Микрофон 1",
            "Микрофон 2",
            "Микрофон 3",
            "Звук собеседников"
        };

        /// <summary>Каналы от программы: общая L, общая R, мик 1, мик 2, мик 3, компьютер L, компьютер R.</summary>
        public const int AudioChannels = 7;

        public static int Crf(VideoQuality quality)
        {
            switch (quality)
            {
                case VideoQuality.High:
                    return 23;
                case VideoQuality.Lossless:
                    return 0;
                default:
                    return 18;
            }
        }

        public static string Preset(VideoQuality quality)
        {
            return quality == VideoQuality.Lossless ? "ultrafast" : "veryfast";
        }

        /// <summary>
        /// Запись: видео (сырые кадры BGRA) и звук (6 каналов float) приходят по двум каналам
        /// от программы, ffmpeg сжимает их в H.264 + AAC и пишет в MKV. MKV остаётся читаемым
        /// даже при внезапном выключении — теряются максимум последние секунды.
        /// </summary>
        public static List<string> Build(string videoPipe, string audioPipe, int width, int height, int fps,
                                         VideoQuality quality, string outputFile, string title)
        {
            string size = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
            var a = new List<string>
            {
                "-hide_banner", "-nostats", "-loglevel", "warning",
                "-progress", "pipe:1", "-stats_period", "1",
                "-y",

                "-thread_queue_size", "16",
                "-f", "rawvideo", "-pix_fmt", "bgr0", "-video_size", size,
                "-framerate", fps.ToString(CultureInfo.InvariantCulture),
                "-i", videoPipe,

                "-thread_queue_size", "1024",
                "-f", "f32le", "-ar", "48000", "-ac", AudioChannels.ToString(CultureInfo.InvariantCulture),
                "-i", audioPipe,

                "-filter_complex",
                "[0:v]scale=out_color_matrix=bt709:out_range=tv,format=yuv420p[v];" +
                "[1:a]asplit=5[a0][a1][a2][a3][a4];" +
                "[a0]pan=stereo|c0=c0|c1=c1[mix];" +
                "[a1]pan=mono|c0=c2[m1];" +
                "[a2]pan=mono|c0=c3[m2];" +
                "[a3]pan=mono|c0=c4[m3];" +
                "[a4]pan=stereo|c0=c5|c1=c6[sys]",
                "-map", "[v]", "-map", "[mix]", "-map", "[m1]", "-map", "[m2]", "-map", "[m3]", "-map", "[sys]",

                "-c:v", "libx264",
                "-preset", Preset(quality),
                "-crf", Crf(quality).ToString(CultureInfo.InvariantCulture),
                "-g", (fps * 2).ToString(CultureInfo.InvariantCulture),
                "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv",

                "-c:a", "aac",
                "-b:a:0", "256k", "-b:a:1", "160k", "-b:a:2", "160k", "-b:a:3", "160k", "-b:a:4", "192k"
            };
            AddTrackMetadata(a);
            a.AddRange(new[]
            {
                "-metadata", "title=" + title,
                "-metadata", "comment=Записано программой «МастерВКС»",
                "-f", "matroska",
                "-cluster_time_limit", "1000",
                "-flush_packets", "1",
                outputFile
            });
            return a;
        }

        /// <summary>Упаковка одного или нескольких MKV в MP4 без пересжатия.</summary>
        public static List<string> BuildRemux(IList<string> inputs, string concatListFile, string outputTmp)
        {
            var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "warning", "-y" };
            if (inputs.Count == 1)
            {
                a.Add("-i");
                a.Add(inputs[0]);
            }
            else
            {
                a.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", concatListFile });
            }
            a.AddRange(new[] { "-map", "0", "-c", "copy" });
            AddTrackMetadata(a);
            a.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", outputTmp });
            return a;
        }

        private static void AddTrackMetadata(List<string> a)
        {
            for (int i = 0; i < TrackTitles.Length; i++)
            {
                string idx = i.ToString(CultureInfo.InvariantCulture);
                a.Add("-metadata:s:a:" + idx);
                a.Add("title=" + TrackTitles[i]);
                a.Add("-metadata:s:a:" + idx);
                a.Add("handler_name=" + TrackTitles[i]);
                a.Add("-metadata:s:a:" + idx);
                a.Add("language=rus");
                a.Add("-disposition:a:" + idx);
                a.Add(i == 0 ? "default" : "0");
            }
        }

        /// <summary>Примерный поток данных (байт/с) — для оценки, хватит ли места.</summary>
        public static double EstimateBytesPerSecond(int width, int height, int fps, VideoQuality quality)
        {
            double bitsPerPixel;
            switch (quality)
            {
                case VideoQuality.High:
                    bitsPerPixel = 0.08;
                    break;
                case VideoQuality.Lossless:
                    bitsPerPixel = 1.2;
                    break;
                default:
                    bitsPerPixel = 0.15;
                    break;
            }
            double videoBits = (double)width * height * fps * bitsPerPixel;
            const double audioBits = (256 + 160 + 160 + 160 + 192) * 1000;
            return (videoBits + audioBits) / 8.0;
        }
    }
}
