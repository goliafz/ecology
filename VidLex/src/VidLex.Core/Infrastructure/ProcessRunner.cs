using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VidLex.Core.Models;

namespace VidLex.Core.Infrastructure
{
    public sealed class ProcessSpec
    {
        public string FileName { get; set; }
        public IList<string> Arguments { get; set; } = new List<string>();
        public string WorkingDirectory { get; set; }
        public WorkerPriority Priority { get; set; } = WorkerPriority.Normal;
        /// <summary>Сколько последних строк stdout/stderr хранить для диагностики.</summary>
        public int TailLines { get; set; } = 40;
    }

    public sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public bool Canceled { get; set; }
        public string StdOutTail { get; set; } = "";
        public string StdErrTail { get; set; } = "";
        public TimeSpan Elapsed { get; set; }
        public string CommandLine { get; set; } = "";
    }

    public interface IProcessRunner
    {
        Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string> onStdOut, Action<string> onStdErr, CancellationToken ct);
    }

    /// <summary>
    /// Запуск консольных утилит (ffmpeg, ffprobe, whisper-cli) с построчным чтением вывода,
    /// отменой (завершение процесса) и регистрацией для приостановки.
    /// </summary>
    public sealed class ProcessRunner : IProcessRunner
    {
        private readonly ExternalProcessRegistry _registry;

        public ProcessRunner(ExternalProcessRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string> onStdOut, Action<string> onStdErr, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var args = BuildArguments(spec.Arguments);
            var psi = new ProcessStartInfo(spec.FileName, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = string.IsNullOrEmpty(spec.WorkingDirectory) ? Path.GetTempPath() : spec.WorkingDirectory
            };

            var outTail = new TailBuffer(spec.TailLines);
            var errTail = new TailBuffer(spec.TailLines);
            var outDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sw = Stopwatch.StartNew();

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) { outDone.TrySetResult(true); return; }
                    outTail.Add(e.Data);
                    SafeInvoke(onStdOut, e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) { errDone.TrySetResult(true); return; }
                    errTail.Add(e.Data);
                    SafeInvoke(onStdErr, e.Data);
                };
                process.Exited += (s, e) => exited.TrySetResult(true);

                try
                {
                    if (!process.Start())
                        throw new InvalidOperationException($"Не удалось запустить процесс {spec.FileName}");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new ExternalToolException($"Не удалось запустить «{Path.GetFileName(spec.FileName)}»: {ex.Message}", ex);
                }

                ChildProcessGuard.Attach(process);
                TrySetPriority(process, spec.Priority);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _registry.Add(process);

                var canceled = false;
                using (ct.Register(() =>
                {
                    canceled = true;
                    _registry.ResumeForKill(process);
                    TryKill(process);
                }))
                {
                    try
                    {
                        await exited.Task.ConfigureAwait(false);
                        // Дочитываем вывод после завершения (с ограничением, чтобы не зависнуть).
                        await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(5000)).ConfigureAwait(false);
                    }
                    finally
                    {
                        _registry.Remove(process);
                    }
                }

                sw.Stop();
                var result = new ProcessResult
                {
                    ExitCode = SafeExitCode(process),
                    Canceled = canceled || ct.IsCancellationRequested,
                    StdOutTail = outTail.ToString(),
                    StdErrTail = errTail.ToString(),
                    Elapsed = sw.Elapsed,
                    CommandLine = $"\"{spec.FileName}\" {args}"
                };
                if (result.Canceled) throw new OperationCanceledException(ct);
                return result;
            }
        }

        private static void SafeInvoke(Action<string> handler, string line)
        {
            if (handler == null) return;
            try
            {
                handler(line);
            }
            catch
            {
                // Ошибка обработчика вывода не должна ронять процесс чтения.
            }
        }

        private static int SafeExitCode(Process p)
        {
            try
            {
                return p.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        private static void TryKill(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch
            {
                // Процесс уже завершился.
            }
        }

        private static void TrySetPriority(Process p, WorkerPriority priority)
        {
            try
            {
                switch (priority)
                {
                    case WorkerPriority.Idle: p.PriorityClass = ProcessPriorityClass.Idle; break;
                    case WorkerPriority.BelowNormal: p.PriorityClass = ProcessPriorityClass.BelowNormal; break;
                }
            }
            catch
            {
                // Нет прав или процесс уже завершился — приоритет не критичен.
            }
        }

        /// <summary>Склеивает аргументы по правилам разбора командной строки Windows (CommandLineToArgvW).</summary>
        public static string BuildArguments(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a ?? ""));
            }
            return sb.ToString();
        }

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;
            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }
                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private sealed class TailBuffer
        {
            private readonly Queue<string> _lines = new Queue<string>();
            private readonly int _max;

            public TailBuffer(int max) => _max = Math.Max(1, max);

            public void Add(string line)
            {
                lock (_lines)
                {
                    _lines.Enqueue(line);
                    while (_lines.Count > _max) _lines.Dequeue();
                }
            }

            public override string ToString()
            {
                lock (_lines) return string.Join(Environment.NewLine, _lines);
            }
        }
    }

    /// <summary>Ошибка внешней утилиты с понятным пользователю сообщением.</summary>
    public sealed class ExternalToolException : Exception
    {
        public ExternalToolException(string message, Exception inner = null, string details = "")
            : base(message, inner)
        {
            Details = details ?? "";
        }

        /// <summary>Хвост stderr, командная строка и т. п.</summary>
        public string Details { get; }
    }
}
