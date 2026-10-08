using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FastGen.Core
{
    /// <summary>Простые настройки в FastGen.ini рядом с программой (ключ=значение).</summary>
    public sealed class AppSettings
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;

        public AppSettings(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || line.TrimStart().StartsWith(";")) continue;
                        _values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch
            {
                // повреждённый ini — работаем с настройками по умолчанию
            }
        }

        public string GetString(string key, string def)
        {
            return _values.TryGetValue(key, out var v) ? v : def;
        }

        public int GetInt(string key, int def)
        {
            return _values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : def;
        }

        public bool GetBool(string key, bool def)
        {
            if (!_values.TryGetValue(key, out var v)) return def;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public void Set(string key, string value) { _values[key] = value ?? string.Empty; }
        public void Set(string key, int value) { _values[key] = value.ToString(CultureInfo.InvariantCulture); }
        public void Set(string key, bool value) { _values[key] = value ? "1" : "0"; }

        public void Save()
        {
            try
            {
                var lines = new List<string>();
                foreach (var kv in _values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(_path, lines, new UTF8Encoding(false));
            }
            catch
            {
                // нет прав на запись в папку программы — не критично
            }
        }
    }

    public static class FileText
    {
        /// <summary>Читает текст: строгий UTF-8, при ошибке — Windows-1251 (файлы TextExpert).</summary>
        public static string ReadAuto(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;

            var strict = new UTF8Encoding(false, true);
            try
            {
                return strict.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1251).GetString(bytes);
            }
        }

        /// <summary>Пишет UTF-8 без BOM с переводами строк Windows, через временный файл.</summary>
        public static void WriteSafe(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, normalized, new UTF8Encoding(false));
            CommitTemp(tmp, path);
        }

        /// <summary>Подменяет path файлом tmp. На сетевых дисках File.Replace бывает недоступен — тогда копируем.</summary>
        public static void CommitTemp(string tmp, string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (IOException)
            {
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
        }
    }
}
