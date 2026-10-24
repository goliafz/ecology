using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using VidLex.App.Services;
using VidLex.Core;
using VidLex.Core.Models;

namespace VidLex.App.ViewModels
{
    /// <summary>Страница «Журнал»: записи текущего сеанса с фильтром, подробности, файлы журналов.</summary>
    public sealed class LogViewModel : ObservableObject
    {
        private const int MaxEntries = 5000;
        private readonly VidLexHost _host;
        private readonly IDialogService _dialogs;
        private readonly List<LogEntry> _pending = new List<LogEntry>();
        private readonly DispatcherTimer _flushTimer;
        private Option<LogLevel?> _levelFilter;
        private string _search = "";
        private LogEntry _selected;
        private bool _autoScroll = true;

        public LogViewModel(VidLexHost host, IDialogService dialogs)
        {
            _host = host;
            _dialogs = dialogs;
            View = CollectionViewSource.GetDefaultView(Entries);
            View.Filter = Filter;
            Levels = new List<Option<LogLevel?>>
            {
                new Option<LogLevel?>(null, "Все уровни"),
                new Option<LogLevel?>(LogLevel.Error, "Только ошибки"),
                new Option<LogLevel?>(LogLevel.Warning, "Предупреждения и ошибки"),
                new Option<LogLevel?>(LogLevel.Info, "INFO и выше"),
                new Option<LogLevel?>(LogLevel.Debug, "Подробно (DEBUG)")
            };
            _levelFilter = Levels[0];

            foreach (var e in host.Log.GetRecent()) Entries.Add(e);
            // Записи приходят из разных потоков — копим и добавляем пачкой в потоке интерфейса.
            host.Log.EntryAdded += e =>
            {
                lock (_pending) _pending.Add(e);
            };
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _flushTimer.Tick += (s, e) => FlushPending();
            _flushTimer.Start();

            CopyCommand = new RelayCommand(Copy, () => Selected != null);
            CopyAllVisibleCommand = new RelayCommand(CopyAllVisible);
            OpenFolderCommand = new RelayCommand(() => HomeViewModel.OpenInExplorer(_host.Log.LogsDirectory, null));
            DeleteOldCommand = new RelayCommand(DeleteOld);
        }

        public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();
        public ICollectionView View { get; }
        public List<Option<LogLevel?>> Levels { get; }
        public string LogsDirectory => _host.Log.LogsDirectory;

        public Option<LogLevel?> LevelFilter
        {
            get => _levelFilter;
            set
            {
                if (Set(ref _levelFilter, value)) View.Refresh();
            }
        }

        public string Search
        {
            get => _search;
            set
            {
                if (Set(ref _search, value)) View.Refresh();
            }
        }

        public LogEntry Selected
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value)) OnPropertyChanged(nameof(SelectedText));
            }
        }

        public string SelectedText => _selected?.ToClipboardText() ?? "Выберите запись, чтобы увидеть подробности.";

        public bool AutoScroll
        {
            get => _autoScroll;
            set => Set(ref _autoScroll, value);
        }

        /// <summary>Сигнал представлению прокрутить к последней записи.</summary>
        public event Action<LogEntry> NewEntriesAdded;

        public ICommand CopyCommand { get; }
        public ICommand CopyAllVisibleCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand DeleteOldCommand { get; }

        private void FlushPending()
        {
            List<LogEntry> batch;
            lock (_pending)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToList();
                _pending.Clear();
            }
            foreach (var e in batch) Entries.Add(e);
            while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
            if (AutoScroll) NewEntriesAdded?.Invoke(batch.Last());
        }

        private bool Filter(object o)
        {
            var e = (LogEntry)o;
            var min = _levelFilter?.Value;
            if (min.HasValue)
            {
                if (min.Value == LogLevel.Debug) { /* все уровни, включая DEBUG */ }
                else if (e.Level < min.Value) return false;
            }
            else if (e.Level == LogLevel.Debug && _host.Log.Verbosity != LogVerbosity.Detailed)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(_search))
            {
                var s = _search.Trim();
                return (e.Message ?? "").IndexOf(s, StringComparison.CurrentCultureIgnoreCase) >= 0
                       || (e.FileName ?? "").IndexOf(s, StringComparison.CurrentCultureIgnoreCase) >= 0
                       || (e.Stage ?? "").IndexOf(s, StringComparison.CurrentCultureIgnoreCase) >= 0;
            }
            return true;
        }

        private void Copy()
        {
            if (Selected == null) return;
            SetClipboard(Selected.ToClipboardText());
        }

        private void CopyAllVisible()
        {
            var sb = new StringBuilder();
            foreach (LogEntry e in View) sb.AppendLine(e.ToFileLine());
            SetClipboard(sb.ToString());
        }

        private static void SetClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // Буфер обмена занят другим приложением.
            }
        }

        private void DeleteOld()
        {
            int days = _host.Settings.Current.LogRetentionDays;
            var files = Directory.Exists(LogsDirectory) ? Directory.GetFiles(LogsDirectory, "VidLex_*.log") : new string[0];
            int candidates = files.Count(f => File.GetLastWriteTime(f) < DateTime.Now.Date.AddDays(-days));
            if (candidates == 0)
            {
                _dialogs.Show("Очистка журналов", $"Журналов старше {days} дн. нет.");
                return;
            }
            if (!_dialogs.Confirm("Удалить старые журналы?", $"Будут удалены файлы журнала старше {days} дн.: {candidates} шт. Текущий журнал сохранится.", "Удалить", "Отмена"))
                return;
            int deleted = _host.Log.DeleteOldLogs(days);
            _host.Log.Info($"Удалено старых файлов журнала: {deleted}", stage: "Журнал");
        }
    }

    public sealed class AboutViewModel : ObservableObject
    {
        private readonly VidLexHost _host;

        public AboutViewModel(VidLexHost host)
        {
            _host = host;
            OpenDataFolderCommand = new RelayCommand(() => HomeViewModel.OpenInExplorer(_host.Paths.DataRoot, null));
        }

        public string Version => VidLexHost.AppVersion;
        public string DataFolder => _host.Paths.DataRoot;
        public ICommand OpenDataFolderCommand { get; }
    }
}
