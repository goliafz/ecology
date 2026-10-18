using System;
using System.IO;

namespace MasterVks.Core
{
    internal static class AppPaths
    {
        public const string AppFolderName = "MasterVKS";

        /// <summary>Папка прежней версии (программа раньше называлась «Летопись ВКС»).</summary>
        private const string OldAppFolderName = "LetopisVKS";
        private static bool _migrated;

        public static string ExeDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string DataDirectory
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(local, AppFolderName);
                if (!_migrated)
                {
                    _migrated = true;
                    MigrateFromOldName(Path.Combine(local, OldAppFolderName), dir);
                }
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Переносит настройки и незавершённые записи из папки прежней версии, один раз.</summary>
        private static void MigrateFromOldName(string oldDir, string newDir)
        {
            try
            {
                if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;
                Directory.CreateDirectory(newDir);
                string oldSettings = Path.Combine(oldDir, "settings.xml");
                if (File.Exists(oldSettings)) File.Copy(oldSettings, Path.Combine(newDir, "settings.xml"));
                string oldPending = Path.Combine(oldDir, "pending");
                if (Directory.Exists(oldPending))
                {
                    string newPending = Path.Combine(newDir, "pending");
                    Directory.CreateDirectory(newPending);
                    foreach (string file in Directory.GetFiles(oldPending, "*.xml"))
                        File.Copy(file, Path.Combine(newPending, Path.GetFileName(file)));
                }
            }
            catch (Exception)
            {
                // Не получилось — просто начнём с настроек по умолчанию.
            }
        }

        public static string LogDirectory
        {
            get
            {
                string dir = Path.Combine(DataDirectory, "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string PendingDirectory
        {
            get
            {
                string dir = Path.Combine(DataDirectory, "pending");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string SettingsFile
        {
            get { return Path.Combine(DataDirectory, "settings.xml"); }
        }

        public static string DefaultRecordingsFolder
        {
            get
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(docs, "Записи ВКС");
            }
        }
    }
}
