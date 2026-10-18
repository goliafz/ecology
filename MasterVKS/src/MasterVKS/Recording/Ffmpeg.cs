using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MasterVks.Core;

namespace MasterVks.Recording
{
    /// <summary>Поиск ffmpeg.exe и запуск коротких служебных команд.</summary>
    internal static class Ffmpeg
    {
        public const string DownloadPage = "https://www.gyan.dev/ffmpeg/builds/";
        public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        public static string Locate(string configuredPath)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath.Trim().Trim('"'));
            string exeDir = AppPaths.ExeDirectory;
            candidates.Add(Path.Combine(exeDir, "ffmpeg.exe"));
            candidates.Add(Path.Combine(exeDir, "ffmpeg", "ffmpeg.exe"));
            candidates.Add(Path.Combine(exeDir, "ffmpeg", "bin", "ffmpeg.exe"));
            candidates.Add(Path.Combine(exeDir, "bin", "ffmpeg.exe"));

            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"));
                }
                catch (ArgumentException)
                {
                }
            }

            foreach (string c in candidates)
            {
                try
                {
                    if (File.Exists(c)) return Path.GetFullPath(c);
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        public sealed class RunResult
        {
            public int ExitCode = -1;
            public bool TimedOut;
            public string Output = "";
            public string Error = "";
        }

        /// <summary>Запускает ffmpeg и ждёт завершения. Вывод собирается полностью.</summary>
        public static RunResult Run(string ffmpegPath, IList<string> args, int timeoutMs, Action<string> onErrorLine = null)
        {
            var result = new RunResult();
            var psi = new ProcessStartInfo(ffmpegPath, CommandLine.Join(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? AppPaths.ExeDirectory
            };
            var output = new StringBuilder();
            var error = new StringBuilder();
            using (var process = new Process { StartInfo = psi })
            using (var outDone = new ManualResetEvent(false))
            using (var errDone = new ManualResetEvent(false))
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) outDone.Set();
                    else lock (output) output.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null)
                    {
                        errDone.Set();
                        return;
                    }
                    lock (error) error.AppendLine(e.Data);
                    if (onErrorLine != null)
                    {
                        try
                        {
                            onErrorLine(e.Data);
                        }
                        catch (Exception)
                        {
                        }
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                }

                if (!process.WaitForExit(timeoutMs))
                {
                    result.TimedOut = true;
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                    }
                    process.WaitForExit(5000);
                }
                else
                {
                    process.WaitForExit();
                }
                outDone.WaitOne(2000);
                errDone.WaitOne(2000);
                result.ExitCode = result.TimedOut ? -1 : process.ExitCode;
            }
            lock (output) result.Output = output.ToString();
            lock (error) result.Error = error.ToString();
            return result;
        }

        public static string Version(string ffmpegPath)
        {
            RunResult r = Run(ffmpegPath, new[] { "-hide_banner", "-version" }, 15000);
            if (r.ExitCode != 0) return null;
            Match m = Regex.Match(r.Output, @"ffmpeg version (\S+)");
            return m.Success ? m.Groups[1].Value : "неизвестная версия";
        }

        public static bool HasEncoder(string ffmpegPath, string encoder)
        {
            RunResult r = Run(ffmpegPath, new[] { "-hide_banner", "-encoders" }, 15000);
            return r.ExitCode == 0 && Regex.IsMatch(r.Output, @"\s" + Regex.Escape(encoder) + @"\s");
        }

        /// <summary>Длительность файла по выводу ffmpeg -i. null — неизвестна (например, файл оборван).</summary>
        public static TimeSpan? ProbeDuration(string ffmpegPath, string file)
        {
            RunResult r = Run(ffmpegPath, new[] { "-hide_banner", "-i", file }, 60000);
            Match m = Regex.Match(r.Error, @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
            if (!m.Success) return null;
            double seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                             + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                             + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>Сборка командной строки Windows по правилам CommandLineToArgvW.</summary>
    internal static class CommandLine
    {
        public static string Join(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                    backslashes = 0;
                }
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
