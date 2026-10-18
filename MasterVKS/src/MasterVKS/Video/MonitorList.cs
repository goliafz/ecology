using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MasterVks.Core;

namespace MasterVks.Video
{
    internal sealed class MonitorDesc
    {
        public string DeviceName;
        public Rectangle Bounds;
        public bool IsPrimary;
        public int Number;

        public string DisplayName
        {
            get
            {
                string text = "Экран " + Number + " · " + Bounds.Width + " × " + Bounds.Height;
                if (IsPrimary) text += " · основной";
                return text;
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal static class MonitorList
    {
        /// <summary>Список экранов в настоящих пикселях (без учёта масштабирования Windows).</summary>
        public static List<MonitorDesc> Enumerate()
        {
            var result = new List<MonitorDesc>();
            try
            {
                using (new PerMonitorDpiScope())
                {
                    Native.MonitorEnumProc callback = delegate(IntPtr monitor, IntPtr hdc, ref Native.RECT rect, IntPtr data)
                    {
                        var info = new Native.MONITORINFOEX();
                        info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFOEX));
                        if (Native.GetMonitorInfo(monitor, ref info))
                        {
                            var r = info.rcMonitor;
                            result.Add(new MonitorDesc
                            {
                                DeviceName = info.szDevice,
                                Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                                IsPrimary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0
                            });
                        }
                        return true;
                    };
                    Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
                    GC.KeepAlive(callback);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось получить список экранов", ex);
            }

            int fallback = 1;
            foreach (var m in result)
            {
                m.Number = ParseDisplayNumber(m.DeviceName);
                if (m.Number <= 0) m.Number = fallback;
                fallback++;
            }
            return result.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
        }

        public static MonitorDesc Find(string deviceName)
        {
            var all = Enumerate();
            MonitorDesc found = null;
            if (!string.IsNullOrEmpty(deviceName))
                found = all.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            return found;
        }

        public static MonitorDesc Primary()
        {
            var all = Enumerate();
            return all.FirstOrDefault(m => m.IsPrimary) ?? all.FirstOrDefault();
        }

        private static int ParseDisplayNumber(string device)
        {
            if (string.IsNullOrEmpty(device)) return 0;
            int i = device.Length;
            while (i > 0 && char.IsDigit(device[i - 1])) i--;
            int n;
            return int.TryParse(device.Substring(i), out n) ? n : 0;
        }
    }
}
