using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using VidLex.App.Services;
using VidLex.Core;
using VidLex.Core.Models;

namespace VidLex.App.ViewModels
{
    public sealed class Option<T>
    {
        public Option(T value, string title)
        {
            Value = value;
            Title = title;
        }

        public T Value { get; }
        public string Title { get; }
        public override string ToString() => Title;
    }

    /// <summary>Страница настроек. Правится копия настроек; применяются по кнопке «Сохранить».</summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly VidLexHost _host;
        private readonly IDialogService _dialogs;
        private AppSettings _edit;
        private string _customWordsText = "";
        private string _toolStatus = "";
        private string _saveMessage = "";

        public SettingsViewModel(VidLexHost host, IDialogService dialogs)
        {
            _host = host;
            _dialogs = dialogs;
            Reload();

            BrowseWhisperCommand = new RelayCommand(() => Browse("Укажите whisper-cli.exe", "whisper-cli.exe|whisper-cli.exe|Программы (*.exe)|*.exe", v => Edit.WhisperCliPath = v, Edit.WhisperCliPath));
            BrowseModelCommand = new RelayCommand(() => Browse("Укажите модель whisper.cpp (ggml-*.bin)", "Модели ggml (*.bin)|*.bin|Все файлы (*.*)|*.*", v => Edit.ModelPath = v, Edit.ModelPath));
            BrowseFfmpegCommand = new RelayCommand(() => Browse("Укажите ffmpeg.exe", "ffmpeg.exe|ffmpeg.exe|Программы (*.exe)|*.exe", v => Edit.FfmpegPath = v, Edit.FfmpegPath));
            BrowseFfprobeCommand = new RelayCommand(() => Browse("Укажите ffprobe.exe", "ffprobe.exe|ffprobe.exe|Программы (*.exe)|*.exe", v => Edit.FfprobePath = v, Edit.FfprobePath));
            BrowseOutputCommand = new RelayCommand(BrowseOutput);
            CheckToolsCommand = new RelayCommand(CheckTools);
            SaveCommand = new RelayCommand(Save);
            CancelCommand = new RelayCommand(() =>
            {
                Reload();
                SaveMessage = "Изменения отменены";
            });
            ResetCommand = new RelayCommand(ResetDefaults);
            OpenModelsFolderCommand = new RelayCommand(() =>
            {
                Directory.CreateDirectory(_host.Paths.ModelsDirectory);
                HomeViewModel.OpenInExplorer(_host.Paths.ModelsDirectory, null);
            });
        }

        public AppSettings Edit
        {
            get => _edit;
            private set => Set(ref _edit, value);
        }

        /// <summary>Пользовательский словарь: одно слово или термин на строку.</summary>
        public string CustomWordsText
        {
            get => _customWordsText;
            set => Set(ref _customWordsText, value);
        }

        public string ToolStatus
        {
            get => _toolStatus;
            private set => Set(ref _toolStatus, value);
        }

        public string SaveMessage
        {
            get => _saveMessage;
            private set => Set(ref _saveMessage, value);
        }

        public bool IsQueueBusy => _host.Queue.IsBusy;

        // ---------- Варианты для списков ----------

        public IList<Option<ExistingResultPolicy>> ExistingOptions { get; } = new List<Option<ExistingResultPolicy>>
        {
            new Option<ExistingResultPolicy>(ExistingResultPolicy.Skip, "Пропускать видео (уже расшифровано)"),
            new Option<ExistingResultPolicy>(ExistingResultPolicy.Overwrite, "Обрабатывать заново и перезаписывать"),
            new Option<ExistingResultPolicy>(ExistingResultPolicy.CreateNew, "Обрабатывать заново, сохранять как новый файл (2), (3)…")
        };

        public IList<Option<EmptyResultBehavior>> EmptyOptions { get; } = new List<Option<EmptyResultBehavior>>
        {
            new Option<EmptyResultBehavior>(EmptyResultBehavior.WriteMarker, "Создать TXT с пометкой «[Речь не обнаружена]»"),
            new Option<EmptyResultBehavior>(EmptyResultBehavior.WriteEmptyFile, "Создать пустой TXT"),
            new Option<EmptyResultBehavior>(EmptyResultBehavior.DoNotCreate, "Не создавать файл")
        };

        public IList<Option<SpellCheckMode>> SpellOptions { get; } = new List<Option<SpellCheckMode>>
        {
            new Option<SpellCheckMode>(SpellCheckMode.SafeFix, "Исправлять только однозначные опечатки"),
            new Option<SpellCheckMode>(SpellCheckMode.ReportOnly, "Не исправлять, только отмечать в журнале"),
            new Option<SpellCheckMode>(SpellCheckMode.Off, "Выключена")
        };

        public IList<Option<WorkerPriority>> PriorityOptions { get; } = new List<Option<WorkerPriority>>
        {
            new Option<WorkerPriority>(WorkerPriority.BelowNormal, "Ниже среднего (компьютер остаётся отзывчивым)"),
            new Option<WorkerPriority>(WorkerPriority.Normal, "Обычный (быстрее, но заметнее нагрузка)"),
            new Option<WorkerPriority>(WorkerPriority.Idle, "Фоновый (только когда компьютер простаивает)")
        };

        public IList<Option<LogVerbosity>> VerbosityOptions { get; } = new List<Option<LogVerbosity>>
        {
            new Option<LogVerbosity>(LogVerbosity.Normal, "Обычный (INFO, WARNING, ERROR)"),
            new Option<LogVerbosity>(LogVerbosity.Detailed, "Подробный (+ технические DEBUG-записи)")
        };

        public IList<Option<int>> TrackOptions { get; } = new[] { new Option<int>(-1, "Автоматически (русская → по умолчанию → первая)") }
            .Concat(Enumerable.Range(0, 8).Select(i => new Option<int>(i, $"Дорожка №{i + 1}"))).ToList();

        public IList<Option<int>> ParallelOptions { get; } = Enumerable.Range(1, 4)
            .Select(i => new Option<int>(i, i == 1 ? "1 (последовательно, рекомендуется)" : i.ToString())).ToList();

        // ---------- Команды ----------

        public ICommand BrowseWhisperCommand { get; }
        public ICommand BrowseModelCommand { get; }
        public ICommand BrowseFfmpegCommand { get; }
        public ICommand BrowseFfprobeCommand { get; }
        public ICommand BrowseOutputCommand { get; }
        public ICommand CheckToolsCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand OpenModelsFolderCommand { get; }

        public Action SettingsSaved { get; set; }

        public void Reload()
        {
            Edit = _host.Settings.Current.Clone();
            CustomWordsText = string.Join(Environment.NewLine, Edit.CustomWords);
            OnPropertyChanged(nameof(IsQueueBusy));
            CheckTools();
        }

        /// <summary>При открытии страницы обновляем состояние компонентов и очереди, не трогая правки.</summary>
        public void OnShown()
        {
            OnPropertyChanged(nameof(IsQueueBusy));
            CheckTools();
        }

        private void Browse(string title, string filter, Action<string> apply, string current)
        {
            var file = _dialogs.PickFile(title, filter, current);
            if (file == null) return;
            apply(file);
            RefreshEdit();
            CheckTools();
        }

        private void BrowseOutput()
        {
            var folder = _dialogs.PickFolder("Папка для всех TXT-файлов (без подпапок)", Edit.OutputFolder);
            if (folder == null) return;
            Edit.OutputFolder = folder;
            Edit.OutputMode = OutputMode.SingleFolder;
            RefreshEdit();
        }

        /// <summary>Пересоздаёт привязки к Edit, чтобы поля показали новые значения.</summary>
        private void RefreshEdit()
        {
            var e = Edit;
            Edit = null;
            Edit = e;
        }

        private void CheckTools()
        {
            var tools = _host.Locator.Locate(Edit);
            var lines = new List<string>
            {
                Line("whisper-cli", tools.WhisperCli),
                Line("Модель", tools.Model),
                Line("ffmpeg", tools.Ffmpeg),
                Line("ffprobe", tools.Ffprobe)
            };
            if (!tools.IsComplete)
            {
                lines.Add("");
                lines.AddRange(tools.Problems.Select(p => "⚠ " + p));
            }
            ToolStatus = string.Join(Environment.NewLine, lines);
        }

        private static string Line(string name, string path) => path == null ? $"✗ {name}: не найден" : $"✓ {name}: {path}";

        private void Save()
        {
            Edit.CustomWords = (CustomWordsText ?? "")
                .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim()).Where(w => w.Length > 0).ToList();

            if (Edit.OutputMode == OutputMode.SingleFolder && string.IsNullOrWhiteSpace(Edit.OutputFolder))
            {
                _dialogs.Show("Не выбрана папка результатов", "Для режима «Все результаты в отдельной папке» выберите папку.", DialogKind.Warning);
                return;
            }

            var toSave = Edit.Clone();
            var fixes = toSave.Validate();
            try
            {
                _host.Settings.Save(toSave);
            }
            catch (Exception ex)
            {
                _host.Log.Error("Не удалось сохранить настройки", ex, stage: "Настройки");
                _dialogs.Show("Ошибка сохранения", "Не удалось сохранить настройки: " + ex.Message, DialogKind.Error, ex.ToString());
                return;
            }
            _host.Log.Info("Настройки сохранены", stage: "Настройки");
            Reload();
            SaveMessage = (fixes.Count > 0 ? "Сохранено с исправлениями: " + string.Join("; ", fixes) : "Настройки сохранены") +
                          (IsQueueBusy ? ". Новые настройки применятся при следующем запуске обработки." : "");
            SettingsSaved?.Invoke();
        }

        private void ResetDefaults()
        {
            if (!_dialogs.Confirm("Сбросить настройки?", "Все параметры вернутся к значениям по умолчанию (изменения нужно будет сохранить). Пути к компонентам и последняя папка сохранятся.", "Сбросить", "Отмена"))
                return;
            var d = new AppSettings
            {
                WhisperCliPath = Edit.WhisperCliPath,
                ModelPath = Edit.ModelPath,
                FfmpegPath = Edit.FfmpegPath,
                FfprobePath = Edit.FfprobePath,
                LastSourceFolder = Edit.LastSourceFolder
            };
            Edit = d;
            CustomWordsText = "";
            SaveMessage = "Значения по умолчанию восстановлены — нажмите «Сохранить»";
        }
    }
}
