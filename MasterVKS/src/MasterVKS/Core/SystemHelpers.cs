using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MasterVks.Core
{
    /// <summary>Не даём ноутбуку уснуть и погасить экран, пока идёт запись.</summary>
    internal static class PowerHelper
    {
        public static void KeepAwake(bool keep)
        {
            try
            {
                if (keep)
                    Native.SetThreadExecutionState(Native.ExecutionState.Continuous | Native.ExecutionState.SystemRequired | Native.ExecutionState.DisplayRequired);
                else
                    Native.SetThreadExecutionState(Native.ExecutionState.Continuous);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось управлять режимом сна", ex);
            }
        }

        /// <summary>
        /// Отключаем «режим эффективности» Windows 11 для процесса: иначе свёрнутая
        /// программа может получать меньше процессорного времени и терять кадры.
        /// </summary>
        public static void DisableThrottling(IntPtr processHandle)
        {
            try
            {
                var state = new Native.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0
                };
                Native.SetProcessInformation(processHandle, Native.ProcessPowerThrottling, ref state, Marshal.SizeOf(typeof(Native.PROCESS_POWER_THROTTLING_STATE)));
            }
            catch (Exception)
            {
                // старая Windows — просто пропускаем
            }
        }

        public static void BoostCurrentProcess(bool boost)
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass = boost ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Запуск вместе с Windows через ключ Run текущего пользователя.</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MasterVKS";
        private const string OldValueName = "LetopisVKS";
        public const string Argument = "--autostart";

        public static void Apply(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (key.GetValue(OldValueName) != null) key.DeleteValue(OldValueName, false);
                    if (enabled)
                    {
                        string exe = Process.GetCurrentProcess().MainModule.FileName;
                        key.SetValue(ValueName, "\"" + exe + "\" " + Argument);
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось изменить автозапуск", ex);
            }
        }
    }

    internal static class Shell
    {
        public static void OpenFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось открыть папку " + folder, ex);
            }
        }

        public static void SelectFile(string file)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось показать файл " + file, ex);
            }
        }

        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось открыть ссылку " + url, ex);
            }
        }
    }
}
