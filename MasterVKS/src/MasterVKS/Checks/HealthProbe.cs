using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using MasterVks.Core;
using Microsoft.Win32;

namespace MasterVks.Checks
{
    /// <summary>
    /// Показания Windows о здоровье компьютера. Всё читается правами обычного
    /// пользователя: пароль администратора не нужен. Любой сбой чтения не ломает
    /// проверку, а просто даёт «узнать не удалось».
    /// </summary>
    internal static class HealthProbe
    {
        private static readonly Guid PowerSaverScheme = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

        // ---------- Питание ----------

        public sealed class PowerState
        {
            public bool? OnAc;
            public int BatteryPercent = -1;
            public bool HasBattery;
            public bool BatterySaver;
            public bool PowerSaverPlan;
        }

        public static PowerState ReadPower()
        {
            var state = new PowerState();
            try
            {
                Native.SYSTEM_POWER_STATUS s;
                if (Native.GetSystemPowerStatus(out s))
                {
                    if (s.ACLineStatus == 0) state.OnAc = false;
                    else if (s.ACLineStatus == 1) state.OnAc = true;
                    state.HasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
                    state.BatteryPercent = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
                    state.BatterySaver = s.SystemStatusFlag == 1;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось узнать состояние питания", ex);
            }
            try
            {
                IntPtr ptr;
                if (Native.PowerGetActiveScheme(IntPtr.Zero, out ptr) == 0 && ptr != IntPtr.Zero)
                {
                    var scheme = (Guid)Marshal.PtrToStructure(ptr, typeof(Guid));
                    Native.LocalFree(ptr);
                    state.PowerSaverPlan = scheme == PowerSaverScheme;
                }
            }
            catch (Exception)
            {
            }
            return state;
        }

        // ---------- Обновления Windows ----------

        public sealed class UpdateState
        {
            public bool RebootPending;
            public bool NoAutoRebootPolicy;
            public int ActiveStart = -1;
            public int ActiveEnd = -1;
            public TimeSpan Uptime;
        }

        public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        public const string UxSettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

        public static UpdateState ReadUpdates()
        {
            var state = new UpdateState();
            state.RebootPending = IsRebootPending();
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PolicyKey))
                {
                    object v = key != null ? key.GetValue("NoAutoRebootWithLoggedOnUsers") : null;
                    state.NoAutoRebootPolicy = v is int && (int)v == 1;
                }
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UxSettingsKey))
                {
                    if (key != null)
                    {
                        object start = key.GetValue("ActiveHoursStart");
                        object end = key.GetValue("ActiveHoursEnd");
                        if (start is int) state.ActiveStart = (int)start;
                        if (end is int) state.ActiveEnd = (int)end;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось прочитать настройки обновлений Windows", ex);
            }
            try
            {
                state.Uptime = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            }
            catch (Exception)
            {
            }
            return state;
        }

        public static bool IsRebootPending()
        {
            try
            {
                foreach (string path in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"
                })
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
                        if (key != null) return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // ---------- Процессор и память ----------

        /// <summary>Загрузка процессора в процентах за <paramref name="sampleMs"/> миллисекунд.</summary>
        public static double CpuLoad(int sampleMs)
        {
            var sampler = new CpuSampler();
            sampler.Sample();
            Thread.Sleep(sampleMs);
            return sampler.Sample();
        }

        public static void Memory(out double availableMb, out double totalMb)
        {
            availableMb = -1;
            totalMb = -1;
            try
            {
                var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) };
                if (Native.GlobalMemoryStatusEx(ref m))
                {
                    availableMb = m.ullAvailPhys / 1048576.0;
                    totalMb = m.ullTotalPhys / 1048576.0;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Самая высокая температура по датчикам Windows, если они есть и показывают правдоподобное число.</summary>
        public static double? Temperature()
        {
            try
            {
                var category = new PerformanceCounterCategory("Thermal Zone Information");
                double best = -1;
                foreach (string instance in category.GetInstanceNames())
                {
                    using (var counter = new PerformanceCounter("Thermal Zone Information", "Temperature", instance, true))
                    {
                        double c = counter.NextValue() - 273.15;
                        if (c > 20 && c < 120) best = Math.Max(best, c);
                    }
                }
                return best > 0 ? best : (double?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------- Диски ----------

        public static List<DiskInfo> Disks()
        {
            var result = new List<DiskInfo>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FriendlyName, HealthStatus, MediaType FROM MSFT_PhysicalDisk")))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(10);
                    foreach (ManagementObject disk in searcher.Get())
                    {
                        using (disk)
                        {
                            int health = Convert.ToInt32(disk["HealthStatus"] ?? 5);
                            int media = Convert.ToInt32(disk["MediaType"] ?? 0);
                            result.Add(new DiskInfo
                            {
                                Name = Convert.ToString(disk["FriendlyName"]) ?? "Диск",
                                Health = health == 0 ? "Healthy" : health == 1 ? "Warning" : health == 2 ? "Unhealthy" : "",
                                Media = media == 3 ? "HDD" : media == 4 ? "SSD" : ""
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось узнать состояние дисков через Storage", ex);
            }
            if (result.Count > 0) return result;

            // Запасной путь: прогноз SMART через Win32_DiskDrive («OK» или «Pred Fail»).
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Model, Status FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject disk in searcher.Get())
                    {
                        using (disk)
                        {
                            string status = Convert.ToString(disk["Status"]) ?? "";
                            result.Add(new DiskInfo
                            {
                                Name = Convert.ToString(disk["Model"]) ?? "Диск",
                                Health = status == "OK" ? "Healthy" : status == "Pred Fail" ? "Unhealthy" : status.Length > 0 ? "Warning" : ""
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось узнать состояние дисков через Win32_DiskDrive", ex);
            }
            return result;
        }

        /// <summary>Пишет 128 МБ в папку записей мимо кэша Windows и меряет скорость. 0 — не удалось.</summary>
        public static double WriteSpeed(string folder)
        {
            string file = Path.Combine(folder, ".mastervks-skorost-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(folder);
                var chunk = new byte[4 * 1024 * 1024];
                new Random().NextBytes(chunk);
                var sw = Stopwatch.StartNew();
                using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    for (int i = 0; i < 32; i++) fs.Write(chunk, 0, chunk.Length);
                    fs.Flush(true);
                }
                double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                return 128 / seconds;
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось измерить скорость записи на диск", ex);
                return 0;
            }
            finally
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (Exception)
                {
                }
            }
        }

        // ---------- Стабильность ----------

        public sealed class StabilityState
        {
            public bool Readable;
            public int UnexpectedShutdowns;
            public int BlueScreens;
            public int HardwareErrors;
            public DateTime? Last;
        }

        /// <summary>Аварийные выключения, синие экраны и аппаратные ошибки за 30 дней из журнала «Система».</summary>
        public static StabilityState Stability()
        {
            var state = new StabilityState();
            const string Days30 = "2592000000";
            state.UnexpectedShutdowns = CountEvents("*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[timediff(@SystemTime) <= " + Days30 + "]]]", state);
            state.BlueScreens = CountEvents("*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= " + Days30 + "]]]", state);
            state.HardwareErrors = CountEvents("*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= " + Days30 + "]]]", state);
            return state;
        }

        private static int CountEvents(string xpath, StabilityState state)
        {
            int count = 0;
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, xpath);
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null && count < 500)
                    {
                        using (record)
                        {
                            count++;
                            if (record.TimeCreated.HasValue && (!state.Last.HasValue || record.TimeCreated.Value > state.Last.Value))
                                state.Last = record.TimeCreated.Value;
                        }
                    }
                }
                state.Readable = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось прочитать журнал Windows", ex);
            }
            return count;
        }
    }

    /// <summary>Загрузка процессора между двумя вызовами <see cref="Sample"/>.</summary>
    internal sealed class CpuSampler
    {
        private ulong _lastIdle, _lastTotal;

        public double Sample()
        {
            try
            {
                Native.FILETIME_LONG idle, kernel, user;
                if (!Native.GetSystemTimes(out idle, out kernel, out user)) return 0;
                ulong total = kernel.Value + user.Value;
                bool first = _lastTotal == 0;
                ulong dTotal = total - _lastTotal, dIdle = idle.Value - _lastIdle;
                _lastIdle = idle.Value;
                _lastTotal = total;
                if (first || dTotal == 0) return 0;
                return Math.Max(0, Math.Min(100, 100.0 * (dTotal - dIdle) / dTotal));
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Защита от самопроизвольной перезагрузки Windows для обновлений.
    /// Нужны права администратора, поэтому программа один раз запускает сама себя
    /// с запросом пароля (UAC) и сразу закрывает эту копию.
    /// </summary>
    internal static class UpdateProtection
    {
        public const string Argument = "--protect-updates";

        /// <summary>Вызывается в копии программы, запущенной с правами администратора.</summary>
        public static int ApplyElevated()
        {
            try
            {
                var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) return 2;
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(HealthProbe.PolicyKey))
                    key.SetValue("NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(HealthProbe.UxSettingsKey))
                {
                    // Самые широкие часы активности, которые разрешает Windows (18 часов).
                    key.SetValue("ActiveHoursStart", 6, RegistryValueKind.DWord);
                    key.SetValue("ActiveHoursEnd", 0, RegistryValueKind.DWord);
                    key.SetValue("SmartActiveHoursState", 0, RegistryValueKind.DWord);
                }
                Log.Info("Защита от самопроизвольной перезагрузки включена");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось включить защиту от перезагрузки", ex);
                return 1;
            }
        }

        /// <summary>Запускает копию с правами администратора и ждёт её. null — успех, иначе текст ошибки.</summary>
        public static string RunElevated()
        {
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, Argument)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "Windows не запустила программу";
                    if (!p.WaitForExit(60000)) return "Настройка не закончилась за минуту";
                    if (p.ExitCode == 0) return null;
                    return p.ExitCode == 2 ? "Нужны права администратора" : "Windows не дала изменить настройку, подробности в журнале";
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return "Вы отменили запрос пароля";
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
