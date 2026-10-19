using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace MasterVks.Video
{
    /// <summary>Запись Windows о том, какая программа пользовалась камерой.</summary>
    internal sealed class CameraUseEntry
    {
        /// <summary>Имя ключа: путь к программе с «#» вместо «\» или имя пакета из Microsoft Store.</summary>
        public string Key;
        public long LastStart;
        public long LastStop;
        public bool Packaged;
    }

    /// <summary>
    /// Кто сейчас пользуется камерой и разрешён ли к ней доступ. Windows 10 и 11
    /// ведут это в реестре (то же самое показывает значок камеры в области
    /// уведомлений), поэтому узнать можно, не трогая саму камеру и не мешая ВКС.
    /// </summary>
    internal static class CameraUsage
    {
        private const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";

        /// <summary>Причина, по которой Windows не пустит программы к камере, или null, если доступ открыт.</summary>
        public static string PrivacyBlock()
        {
            try
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                {
                    using (RegistryKey policy = hklm.OpenSubKey(PolicyKey))
                    {
                        object v = policy != null ? policy.GetValue("LetAppsAccessCamera") : null;
                        if (v is int && (int)v == 2)
                            return "доступ к камере запрещён групповой политикой (обратитесь к администратору)";
                    }
                    if (IsDenied(hklm, StoreKey))
                        return "в Windows выключен доступ к камере для всего компьютера: «Параметры → Конфиденциальность → Камера»";
                    if (IsDenied(hkcu, StoreKey))
                        return "в Windows выключен доступ к камере: «Параметры → Конфиденциальность → Камера», включите «Доступ к камере»";
                    if (IsDenied(hkcu, StoreKey + @"\NonPackaged"))
                        return "Windows не пускает к камере классические приложения: «Параметры → Конфиденциальность → Камера», включите «Разрешить классическим приложениям доступ к камере»";
                }
            }
            catch (Exception)
            {
                // Нет таких ключей (старая Windows) или нет доступа: считаем, что разрешено.
            }
            return null;
        }

        private static bool IsDenied(RegistryKey root, string path)
        {
            using (RegistryKey key = root.OpenSubKey(path))
            {
                string v = key != null ? key.GetValue("Value") as string : null;
                return string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Программы, которые сейчас держат камеру включённой (кроме ffmpeg самой программы).
        /// Пустой список, если камера свободна или Windows не ведёт учёт.
        /// </summary>
        public static List<string> ActiveApps(string ownFfmpegPath)
        {
            var entries = new List<CameraUseEntry>();
            try
            {
                using (RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    Collect(hkcu, StoreKey, entries);
                    Collect(hkcu, StoreKey + @"\NonPackaged", entries);
                    Collect(hklm, StoreKey, entries);
                    Collect(hklm, StoreKey + @"\NonPackaged", entries);
                }
            }
            catch (Exception)
            {
                return new List<string>();
            }
            return InUse(entries, ownFfmpegPath, IsRunning);
        }

        private static void Collect(RegistryKey root, string path, List<CameraUseEntry> into)
        {
            using (RegistryKey key = root.OpenSubKey(path))
            {
                if (key == null) return;
                bool nonPackaged = path.EndsWith("NonPackaged", StringComparison.OrdinalIgnoreCase);
                foreach (string name in key.GetSubKeyNames())
                {
                    if (!nonPackaged && string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                    using (RegistryKey app = key.OpenSubKey(name))
                    {
                        if (app == null) continue;
                        into.Add(new CameraUseEntry
                        {
                            Key = name,
                            LastStart = ToLong(app.GetValue("LastUsedTimeStart")),
                            LastStop = ToLong(app.GetValue("LastUsedTimeStop")),
                            Packaged = !nonPackaged
                        });
                    }
                }
            }
        }

        private static long ToLong(object v)
        {
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            return 0;
        }

        /// <summary>
        /// Отбирает программы, у которых камера включена (начало есть, конца нет).
        /// Если программа упала, Windows может не записать конец, поэтому для
        /// обычных программ дополнительно проверяем, что процесс ещё жив.
        /// </summary>
        public static List<string> InUse(IEnumerable<CameraUseEntry> entries, string ownFfmpegPath, Func<string, bool> isRunning)
        {
            string own = string.IsNullOrEmpty(ownFfmpegPath) ? null : ownFfmpegPath.Replace('\\', '#');
            var result = new List<string>();
            foreach (CameraUseEntry e in entries)
            {
                if (e.LastStart <= 0 || e.LastStop != 0) continue;
                if (own != null && string.Equals(e.Key, own, StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.Packaged)
                {
                    string exe = ExeName(e.Key);
                    if (string.Equals(exe, "MasterVKS", StringComparison.OrdinalIgnoreCase)) continue;
                    if (isRunning != null && !isRunning(exe)) continue;
                }
                string title = AppTitle(e.Key);
                if (!result.Contains(title)) result.Add(title);
            }
            return result;
        }

        private static string ExeName(string key)
        {
            // Путь записан с «#» вместо «\», поэтому разбираем сами, не полагаясь на Path.
            string file = key;
            int sep = Math.Max(file.LastIndexOf('#'), file.LastIndexOf('\\'));
            if (sep >= 0) file = file.Substring(sep + 1);
            return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
        }

        private static bool IsRunning(string exeName)
        {
            try
            {
                Process[] list = Process.GetProcessesByName(exeName);
                bool any = list.Length > 0;
                foreach (Process p in list) p.Dispose();
                return any;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Понятное название программы по ключу реестра.</summary>
        public static string AppTitle(string key)
        {
            string name;
            bool packaged = key.IndexOf('#') < 0;
            if (packaged)
            {
                // «Microsoft.WindowsCamera_8wekyb3d8bbwe» → «WindowsCamera»
                name = key;
                int us = name.IndexOf('_');
                if (us > 0) name = name.Substring(0, us);
                int dot = name.LastIndexOf('.');
                if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
            }
            else
            {
                name = ExeName(key);
            }

            string l = name.ToLowerInvariant();
            string folder = key.ToLowerInvariant();
            if (l.Contains("telemost") || l.Contains("телемост")) return "Телемост";
            if (l == "browser" && folder.Contains("yandex")) return "Яндекс Браузер";
            if (l == "chrome") return "Google Chrome";
            if (l == "msedge" || l == "msedgewebview2") return "Microsoft Edge";
            if (l == "firefox") return "Firefox";
            if (l == "opera" || (l == "launcher" && folder.Contains("opera"))) return "Opera";
            if (l == "zoom") return "Zoom";
            if (l == "teams" || l == "ms-teams" || l == "msteams") return "Microsoft Teams";
            if (l == "telegram") return "Telegram";
            if (l == "skype") return "Skype";
            if (l == "windowscamera") return "Камера Windows";
            if (l.Contains("ktalk") || folder.Contains("ktalk")) return "Контур.Толк";
            return name;
        }
    }
}
