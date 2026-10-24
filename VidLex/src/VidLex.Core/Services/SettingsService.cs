using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.Core.Services
{
    /// <summary>
    /// Настройки в JSON. Повреждённый файл не роняет приложение: он переименовывается
    /// в settings.corrupted-ДАТА.json для диагностики, а программа работает со значениями по умолчанию.
    /// </summary>
    public sealed class SettingsService : ISettingsService
    {
        private readonly string _path;
        private readonly ILogService _log;
        private readonly object _sync = new object();
        private AppSettings _current = new AppSettings();

        public SettingsService(string settingsPath, ILogService log)
        {
            _path = settingsPath;
            _log = log;
        }

        public AppSettings Current
        {
            get { lock (_sync) return _current; }
        }

        public event Action<AppSettings> SettingsChanged;

        public AppSettings Load()
        {
            AppSettings loaded;
            if (!File.Exists(_path))
            {
                loaded = new AppSettings();
                _log?.Info("Файл настроек не найден — используются значения по умолчанию", stage: "Настройки");
                loaded.Validate();
                lock (_sync) _current = loaded;
                TrySave(loaded);
                return loaded;
            }

            try
            {
                var json = File.ReadAllText(_path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) throw new JsonException("Файл настроек пуст");
                // Ошибка в отдельном значении (например, неизвестное имя режима) не сбрасывает
                // все настройки: такое поле остаётся со значением по умолчанию.
                // Синтаксические ошибки JSON обрабатываются ниже как повреждение файла.
                var memberErrors = new System.Collections.Generic.List<string>();
                var settings = new JsonSerializerSettings
                {
                    ObjectCreationHandling = JsonDefaults.Settings.ObjectCreationHandling,
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    Error = (sender, args) =>
                    {
                        if (args.ErrorContext.Error is JsonReaderException) return;
                        if (args.CurrentObject is AppSettings && args.ErrorContext.Member != null)
                        {
                            memberErrors.Add($"{args.ErrorContext.Member}: {args.ErrorContext.Error.Message}");
                            args.ErrorContext.Handled = true;
                        }
                    }
                };
                loaded = JsonConvert.DeserializeObject<AppSettings>(json, settings);
                foreach (var err in memberErrors)
                    _log?.Warning("Некорректное значение в файле настроек, используется значение по умолчанию", stage: "Настройки", details: err);
                if (loaded == null) throw new JsonException("Файл настроек не содержит объекта");
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                var backup = PreserveCorrupted();
                _log?.Warning(
                    "Файл настроек повреждён — восстановлены значения по умолчанию" +
                    (backup != null ? $". Повреждённый файл сохранён как «{Path.GetFileName(backup)}»" : ""),
                    stage: "Настройки",
                    details: ex.ToString());
                loaded = new AppSettings();
                loaded.Validate();
                lock (_sync) _current = loaded;
                TrySave(loaded);
                return loaded;
            }

            foreach (var fix in loaded.Validate())
                _log?.Warning("Исправлено значение настройки: " + fix, stage: "Настройки");

            lock (_sync) _current = loaded;
            _log?.Info("Настройки загружены", stage: "Настройки", details: _path);
            return loaded;
        }

        public void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            foreach (var fix in settings.Validate())
                _log?.Warning("Исправлено значение настройки: " + fix, stage: "Настройки");
            var json = JsonConvert.SerializeObject(settings, JsonDefaults.Settings);
            AtomicFile.WriteAllText(_path, json, new UTF8Encoding(false));
            lock (_sync) _current = settings;
            SettingsChanged?.Invoke(settings);
        }

        private void TrySave(AppSettings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, JsonDefaults.Settings);
                AtomicFile.WriteAllText(_path, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log?.Warning("Не удалось сохранить настройки", stage: "Настройки", details: ex.ToString());
            }
        }

        private string PreserveCorrupted()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path) ?? ".";
                var backup = Path.Combine(dir, $"settings.corrupted-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(_path, backup, true);
                return backup;
            }
            catch
            {
                return null;
            }
        }
    }
}
