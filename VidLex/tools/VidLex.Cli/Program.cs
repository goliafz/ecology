using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VidLex.Core;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Cli
{
    /// <summary>
    /// VidLex.Cli — консольный запуск конвейера VidLex.
    ///
    /// Пример:
    ///   VidLex.Cli --source "D:\Видео\Курс" --model "C:\models\ggml-large-v3-turbo-q5_0.bin"
    ///   VidLex.Cli --source D:\Видео --output-folder D:\Результаты --timestamps --interval 30
    ///
    /// Настройки берутся из %LocalAppData%\VidLex\settings.json, параметры командной строки
    /// переопределяют их только на время запуска (файл настроек не изменяется).
    /// Код возврата: 0 — без ошибок, 1 — были ошибки в отдельных видео, 2 — не удалось начать.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException)
            {
                // Вывод перенаправлен — кодировка консоли не важна.
            }
            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Критическая ошибка: " + ex);
                return 2;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var opts = ParseArgs(args);
            if (opts.ContainsKey("help") || !opts.ContainsKey("source"))
            {
                PrintHelp();
                return opts.ContainsKey("help") ? 0 : 2;
            }

            var paths = new AppPaths(Get(opts, "data-dir"));
            var dictDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dictionaries");
            using (var host = new VidLexHost(paths, dictDir))
            {
                host.Log.EntryAdded += e =>
                {
                    if (e.Level >= LogLevel.Info || opts.ContainsKey("verbose"))
                        Console.WriteLine($"{e.Timestamp:HH:mm:ss} {e.LevelText,-7} {(string.IsNullOrEmpty(e.FileName) ? "" : "[" + e.FileName + "] ")}{e.Message}");
                    if (e.Level == LogLevel.Error && !string.IsNullOrWhiteSpace(e.Details))
                        Console.WriteLine("        " + e.Details.Replace("\n", "\n        "));
                };
                host.Log.Info($"VidLex.Cli {VidLexHost.AppVersion} запущен", stage: "Запуск");

                var s = host.Settings.Current.Clone();
                Apply(opts, s);
                host.Log.Verbosity = s.LogVerbosity;
                foreach (var fix in s.Validate()) host.Log.Warning("Параметр исправлен: " + fix, stage: "Настройки");

                var tools = host.RefreshTools(s);
                if (!tools.IsComplete)
                {
                    foreach (var p in tools.Problems) Console.Error.WriteLine("Ошибка: " + p);
                    host.Log.Error("Невозможно начать обработку: не найдены необходимые компоненты", details: string.Join(Environment.NewLine, tools.Problems));
                    return 2;
                }

                if (s.OutputMode == OutputMode.SingleFolder)
                {
                    var err = host.Export.CheckWritable(s.OutputFolder);
                    if (err != null)
                    {
                        Console.Error.WriteLine("Ошибка: " + err);
                        return 2;
                    }
                }

                var source = Path.GetFullPath(opts["source"]);
                host.Log.Info($"Исходная папка: {source}", stage: "Сканирование");
                var found = await host.Discovery.ScanAsync(source, s.VideoExtensions, null, CancellationToken.None);
                host.Log.Info($"Найдено видео: {found.Files.Count} (папок просмотрено: {found.FoldersScanned}, файлов других форматов: {found.SkippedUnsupported})", stage: "Сканирование");
                var jobs = found.Files.Select(f =>
                {
                    var fi = new FileInfo(f);
                    return new VideoJob(f, source) { FileSize = fi.Length, LastWriteTimeUtc = fi.LastWriteTimeUtc };
                }).ToList();
                if (jobs.Count == 0) return 0;

                using (var cts = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (o, e) =>
                    {
                        e.Cancel = true;
                        host.Queue.Stop();
                    };
                    int stopAfter = opts.ContainsKey("stop-after-seconds") ? int.Parse(opts["stop-after-seconds"]) : 0;
                    int pauseAt = opts.ContainsKey("pause-test") ? int.Parse(opts["pause-test"]) : 0;
                    void Report()
                    {
                        var snap = host.Estimator.Compute(jobs, host.Queue.GetLiveProgress(), host.Queue.Parallelism);
                        var current = jobs.FirstOrDefault(j => !j.IsFinished && j.Status != JobStatus.Waiting && j.Status != JobStatus.Stopped);
                        Console.WriteLine($"  … очередь {snap.OverallPercent:0.0}% ({snap.Done}/{snap.Total}), осталось {snap.RemainingText}" +
                                          (current != null ? $"; текущее: {current.FileName} — {current.StatusText} {current.Progress:0.0}% {current.Detail}" : "") +
                                          $"; состояние: {host.Queue.State}");
                    }

                    var reporter = Task.Run(async () =>
                    {
                        var started = DateTime.Now;
                        bool pausedOnce = false;
                        while (!cts.IsCancellationRequested)
                        {
                            await Task.Delay(2000).ConfigureAwait(false);
                            if (!host.Queue.IsBusy) continue;
                            Report();
                            var elapsed = (DateTime.Now - started).TotalSeconds;
                            if (pauseAt > 0 && !pausedOnce && elapsed >= pauseAt)
                            {
                                // Тест паузы: процессы замораживаются, прогресс не должен расти.
                                pausedOnce = true;
                                host.Queue.Pause();
                                Console.WriteLine($"  [тест паузы] пауза; внешних процессов: {host.Registry.ActiveCount}");
                                for (int k = 0; k < 4; k++)
                                {
                                    await Task.Delay(2000).ConfigureAwait(false);
                                    Report();
                                }
                                host.Queue.Resume();
                                Console.WriteLine("  [тест паузы] продолжение");
                            }
                            if (stopAfter > 0 && elapsed >= stopAfter)
                            {
                                host.Queue.Stop(opts.ContainsKey("keep-checkpoints"));
                                stopAfter = 0;
                            }
                        }
                    });

                    var summary = await host.Queue.RunAsync(jobs, s, tools, source);
                    cts.Cancel();
                    try { await reporter; } catch (OperationCanceledException) { }

                    Console.WriteLine();
                    Console.WriteLine("Итог:");
                    foreach (var j in jobs)
                        Console.WriteLine($"  {j.StatusText,-12} {j.RelativePath}" + (j.OutputPath.Length > 0 ? $" → {j.OutputPath}" : "") + (j.Error.Length > 0 ? $"  [{j.Error}]" : "") + (j.Detail.Length > 0 ? $"  ({j.Detail})" : ""));
                    host.Log.Flush();
                    return summary.Errors > 0 ? 1 : 0;
                }
            }
        }

        private static void Apply(Dictionary<string, string> o, AppSettings s)
        {
            if (o.ContainsKey("model")) s.ModelPath = o["model"];
            if (o.ContainsKey("whisper")) s.WhisperCliPath = o["whisper"];
            if (o.ContainsKey("ffmpeg")) s.FfmpegPath = o["ffmpeg"];
            if (o.ContainsKey("ffprobe")) s.FfprobePath = o["ffprobe"];
            if (o.ContainsKey("output-folder"))
            {
                s.OutputMode = OutputMode.SingleFolder;
                s.OutputFolder = Path.GetFullPath(o["output-folder"]);
            }
            if (o.ContainsKey("timestamps")) s.AddTimestamps = true;
            if (o.ContainsKey("no-timestamps")) s.AddTimestamps = false;
            if (o.ContainsKey("interval")) s.TimestampIntervalSeconds = int.Parse(o["interval"]);
            if (o.ContainsKey("existing"))
                s.ExistingResultPolicy = (ExistingResultPolicy)Enum.Parse(typeof(ExistingResultPolicy), o["existing"], true);
            if (o.ContainsKey("chunk")) s.ChunkLengthSeconds = int.Parse(o["chunk"]);
            if (o.ContainsKey("threads")) s.Threads = int.Parse(o["threads"]);
            if (o.ContainsKey("parallel")) s.MaxParallelJobs = int.Parse(o["parallel"]);
            if (o.ContainsKey("beam")) s.BeamSize = int.Parse(o["beam"]);
            if (o.ContainsKey("no-gpu")) s.UseGpu = false;
            if (o.ContainsKey("words")) s.CustomWords = o["words"].Split(',').Select(w => w.Trim()).ToList();
            if (o.ContainsKey("verbose")) s.LogVerbosity = LogVerbosity.Detailed;
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
                var key = args[i].Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    result[key] = args[++i];
                else
                    result[key] = "";
            }
            return result;
        }

        private static string Get(Dictionary<string, string> o, string key) => o.TryGetValue(key, out var v) ? v : null;

        private static void PrintHelp()
        {
            Console.WriteLine("VidLex.Cli — транскрибация русскоязычных видео (разработчик: Ходаковский Александр)");
            Console.WriteLine();
            Console.WriteLine("  --source <папка>          исходная папка (обязательно), обходится рекурсивно");
            Console.WriteLine("  --output-folder <папка>   сохранять все TXT в одну папку (по умолчанию — рядом с видео)");
            Console.WriteLine("  --timestamps              добавлять временные метки;  --interval <сек> — период меток");
            Console.WriteLine("  --existing Skip|Overwrite|CreateNew   поведение при существующем TXT");
            Console.WriteLine("  --model <файл.bin>  --whisper <whisper-cli>  --ffmpeg <ffmpeg>  --ffprobe <ffprobe>");
            Console.WriteLine("  --chunk <сек>  --threads <n>  --parallel <n>  --beam <n>  --no-gpu");
            Console.WriteLine("  --words \"термин1, термин2\"  пользовательский словарь");
            Console.WriteLine("  --data-dir <папка>        папка данных вместо %LocalAppData%\\VidLex");
            Console.WriteLine("  --verbose                 подробный журнал");
        }
    }
}
