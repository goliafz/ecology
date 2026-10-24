using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VidLex.Core.Infrastructure
{
    /// <summary>
    /// Реестр запущенных внешних процессов. Нужен для настоящей паузы:
    /// процессы FFmpeg и whisper.cpp замораживаются (NtSuspendProcess / SIGSTOP)
    /// и не расходуют процессор, пока очередь на паузе.
    /// </summary>
    public sealed class ExternalProcessRegistry
    {
        private readonly object _sync = new object();
        private readonly HashSet<Process> _processes = new HashSet<Process>();
        private readonly HashSet<int> _suspended = new HashSet<int>();
        private bool _paused;

        /// <summary>Вызывается при неудачной попытке заморозить процесс (для журнала).</summary>
        public event Action<string> SuspendFailed;

        public int ActiveCount
        {
            get { lock (_sync) return _processes.Count; }
        }

        public void Add(Process p)
        {
            lock (_sync)
            {
                _processes.Add(p);
                if (_paused) SuspendOne(p);
            }
        }

        public void Remove(Process p)
        {
            lock (_sync)
            {
                _processes.Remove(p);
                try { _suspended.Remove(p.Id); } catch (InvalidOperationException) { }
            }
        }

        /// <summary>Заморозить все процессы. Возвращает число успешно приостановленных.</summary>
        public int SuspendAll()
        {
            lock (_sync)
            {
                _paused = true;
                int ok = 0;
                foreach (var p in _processes)
                    if (SuspendOne(p)) ok++;
                return ok;
            }
        }

        public void ResumeAll()
        {
            lock (_sync)
            {
                _paused = false;
                foreach (var p in _processes) ResumeOne(p);
                _suspended.Clear();
            }
        }

        /// <summary>Перед завершением процесса снимаем заморозку (на Linux SIGKILL работает и так, но так надёжнее).</summary>
        public void ResumeForKill(Process p)
        {
            lock (_sync) ResumeOne(p);
        }

        private bool SuspendOne(Process p)
        {
            try
            {
                if (p.HasExited) return false;
                if (_suspended.Contains(p.Id)) return true;
                if (ProcessControl.Suspend(p))
                {
                    _suspended.Add(p.Id);
                    return true;
                }
                SuspendFailed?.Invoke($"Не удалось приостановить процесс {SafeName(p)} (PID {p.Id})");
            }
            catch (Exception ex)
            {
                SuspendFailed?.Invoke($"Не удалось приостановить процесс: {ex.Message}");
            }
            return false;
        }

        private void ResumeOne(Process p)
        {
            try
            {
                if (p.HasExited) return;
                if (_suspended.Remove(p.Id)) ProcessControl.Resume(p);
            }
            catch
            {
                // Процесс мог завершиться между проверками.
            }
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName; } catch { return "?"; }
        }
    }

    public static class ProcessControl
    {
        public static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        public static bool Suspend(Process p)
        {
            if (IsWindows) return NativeWindows.NtSuspendProcess(p.Handle) == 0;
            return NativeUnix.kill(p.Id, NativeUnix.SIGSTOP) == 0;
        }

        public static bool Resume(Process p)
        {
            if (IsWindows) return NativeWindows.NtResumeProcess(p.Handle) == 0;
            return NativeUnix.kill(p.Id, NativeUnix.SIGCONT) == 0;
        }

        private static class NativeWindows
        {
            [DllImport("ntdll.dll")]
            public static extern int NtSuspendProcess(IntPtr processHandle);

            [DllImport("ntdll.dll")]
            public static extern int NtResumeProcess(IntPtr processHandle);
        }

        private static class NativeUnix
        {
            public const int SIGCONT = 18;
            public const int SIGSTOP = 19;

            [DllImport("libc", SetLastError = true)]
            public static extern int kill(int pid, int sig);
        }
    }

    /// <summary>
    /// Windows Job Object с флагом KILL_ON_JOB_CLOSE: если VidLex аварийно завершится,
    /// Windows сама закроет запущенные им ffmpeg и whisper-cli.
    /// </summary>
    public static class ChildProcessGuard
    {
        private static readonly object Sync = new object();
        private static IntPtr _job = IntPtr.Zero;
        private static bool _initFailed;

        public static void Attach(Process process)
        {
            if (!ProcessControl.IsWindows) return;
            try
            {
                lock (Sync)
                {
                    if (_job == IntPtr.Zero && !_initFailed) Init();
                    if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, process.Handle);
                }
            }
            catch
            {
                // Не критично: без Job Object процессы всё равно завершаются при отмене.
            }
        }

        private static void Init()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                _initFailed = true;
                return;
            }
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    CloseHandle(job);
                    _initFailed = true;
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            _job = job;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
