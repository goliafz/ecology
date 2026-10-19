using System;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace MasterVks.Core
{
    public enum CheckMode
    {
        Silent = 0,
        Audible = 1
    }

    public enum VideoQuality
    {
        /// <summary>Визуально без потерь (CRF 18).</summary>
        Maximum = 0,
        /// <summary>Хорошее качество, файлы меньше (CRF 23).</summary>
        High = 1,
        /// <summary>Математически без потерь (CRF 0) — очень большие файлы.</summary>
        Lossless = 2
    }

    /// <summary>
    /// Настройки программы. Хранятся в %LOCALAPPDATA%\MasterVKS\settings.xml.
    /// Сохраняются атомарно, чтобы сбой питания не испортил файл.
    /// </summary>
    public sealed class AppSettings
    {
        public string OutputFolder { get; set; }

        public string MonitorDevice { get; set; }

        public string Mic1Id { get; set; }
        public string Mic1Name { get; set; }

        public string Mic2Id { get; set; }
        public string Mic2Name { get; set; }

        public string Mic3Id { get; set; }
        public string Mic3Name { get; set; }

        /// <summary>Пусто — звук с устройства вывода по умолчанию (следуем за Windows).</summary>
        public string OutputDeviceId { get; set; }
        public string OutputDeviceName { get; set; }

        public int FrameRate { get; set; }
        public VideoQuality Quality { get; set; }
        public bool ShowCursor { get; set; }

        public CheckMode StartupCheckMode { get; set; }
        public CheckMode ManualCheckMode { get; set; }

        public bool ConfirmStop { get; set; }
        public bool HotkeysEnabled { get; set; }
        public bool Autostart { get; set; }
        public bool StartMinimizedOnAutostart { get; set; }
        public bool KeepMkvAfterConvert { get; set; }

        public bool MixMic1 { get; set; }
        public bool MixMic2 { get; set; }
        public bool MixMic3 { get; set; }
        public bool MixSystem { get; set; }

        /// <summary>Задержка звука относительно видео, мс (компенсация буферов звука).</summary>
        public int AudioOffsetMs { get; set; }

        public string FfmpegPath { get; set; }

        /// <summary>Название ВКС для имён папки и файла (необязательно).</summary>
        public string MeetingName { get; set; }

        /// <summary>Версия формата настроек: нужна, чтобы обновить значения по умолчанию у старых настроек.</summary>
        public int SettingsVersion { get; set; }

        /// <summary>Устройства уже подобраны хотя бы один раз (первый запуск прошёл).</summary>
        public bool DevicesInitialized { get; set; }

        public AppSettings()
        {
            OutputFolder = AppPaths.DefaultRecordingsFolder;
            FrameRate = 30;
            Quality = VideoQuality.Maximum;
            ShowCursor = true;
            StartupCheckMode = CheckMode.Silent;
            ManualCheckMode = CheckMode.Audible;
            ConfirmStop = true;
            HotkeysEnabled = true;
            Autostart = false;
            StartMinimizedOnAutostart = true;
            KeepMkvAfterConvert = true;
            MixMic1 = true;
            MixMic2 = true;
            MixMic3 = true;
            MixSystem = true;
            AudioOffsetMs = 70;
        }

        public const int CurrentSettingsVersion = 3;

        public void Normalize()
        {
            if (SettingsVersion < 2)
            {
                // Версия 1.0.2: исходный MKV по умолчанию сохраняется рядом с MP4.
                KeepMkvAfterConvert = true;
            }
            if (SettingsVersion < 3)
            {
                // Версия 1.2: появился третий микрофон, по умолчанию он тоже в общей дорожке.
                MixMic3 = true;
            }
            SettingsVersion = CurrentSettingsVersion;
            if (MeetingName == null) MeetingName = "";
            if (FrameRate != 15 && FrameRate != 25 && FrameRate != 30 && FrameRate != 60) FrameRate = 30;
            if (AudioOffsetMs < -500 || AudioOffsetMs > 1000) AudioOffsetMs = 70;
            if (string.IsNullOrWhiteSpace(OutputFolder)) OutputFolder = AppPaths.DefaultRecordingsFolder;
            if (!Enum.IsDefined(typeof(VideoQuality), Quality)) Quality = VideoQuality.Maximum;
            if (!Enum.IsDefined(typeof(CheckMode), StartupCheckMode)) StartupCheckMode = CheckMode.Silent;
            if (!Enum.IsDefined(typeof(CheckMode), ManualCheckMode)) ManualCheckMode = CheckMode.Audible;
        }

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }

        private static readonly object FileLock = new object();

        public static AppSettings Load()
        {
            string path = AppPaths.SettingsFile;
            try
            {
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
                        settings.Normalize();
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Файл настроек повреждён, начинаю с настроек по умолчанию", ex);
                try
                {
                    File.Copy(path, path + ".broken", true);
                }
                catch
                {
                }
            }
            var fresh = new AppSettings();
            fresh.Normalize();
            return fresh;
        }

        public void Save()
        {
            lock (FileLock)
            {
                try
                {
                    string path = AppPaths.SettingsFile;
                    string tmp = path + ".tmp";
                    using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        new XmlSerializer(typeof(AppSettings)).Serialize(writer, this);
                        writer.Flush();
                        stream.Flush(true);
                    }
                    if (File.Exists(path))
                        File.Replace(tmp, path, null);
                    else
                        File.Move(tmp, path);
                }
                catch (Exception ex)
                {
                    Log.Warn("Не удалось сохранить настройки", ex);
                }
            }
        }
    }
}
