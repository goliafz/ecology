using System;
using System.IO;

namespace LetopisVks.Core
{
    internal static class AppPaths
    {
        public const string AppFolderName = "LetopisVKS";

        public static string ExeDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string DataDirectory
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
                Directory.CreateDirectory(dir);
                return dir;
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
