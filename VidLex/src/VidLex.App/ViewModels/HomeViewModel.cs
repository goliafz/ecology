using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using VidLex.App.Services;
using VidLex.Core;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;
using VidLex.Core.Services;

namespace VidLex.App.ViewModels
{
    public sealed class StatusFilterOption
    {
        public string Title { get; set; }
        public JobStatus? Status { get; set; }
        public bool ActiveOnly { get; set; }
        public override string ToString() => Title;
    }

    /// <summary>Главная страница: выбор папки, очередь, прогресс.</summary>
    public sealed class HomeViewModel : ObservableObject
    {
        private readonly VidLexHost _host;
        private readonly IDialogService _dialogs;
        private readonly DispatcherTimer _timer;
        private CancellationTokenSource _probeCts;
        private volatile bool _statusesDirty;

        private string _sourceFolder = "";
        private bool _isScanning;
        private string _scanStatus = "";
        private string _searchText = "";
        private StatusFilterOption _statusFilter;
        private VideoJob _selectedJob;
        private string _toolProblems = "";
        private string _lastRunSummary = "";
        private QueueState _queueState = QueueState.Idle;

        public HomeViewModel(VidLexHost host, IDialogService dialogs)
        {
            _host = host;
            _dialogs = dialogs;

            JobsView = CollectionViewSource.GetDefaultView(Jobs);
            JobsView.Filter = FilterJob;

            StatusFilters = new List<StatusFilterOption> { new StatusFilterOption { Title = "Все статусы" }, new StatusFilterOption { Title = "В работе", ActiveOnly = true } };
            foreach (JobStatus s in Enum.GetValues(typeof(JobStatus)))
                StatusFilters.Add(new StatusFilterOption { Title = StatusNames.Get(s), Status = s });
            _statusFilter = StatusFilters[0];

            ChooseFolderCommand = new AsyncCommand(ChooseFolderAsync, () => !IsBusy && !IsScanning, OnError);
            RescanCommand = new AsyncCommand(() => ScanAsync(SourceFolder), () => !IsBusy && !IsScanning && Directory.Exists(SourceFolder), OnError);
            ClearCommand = new RelayCommand(ClearList, () => !IsBusy && !IsScanning && Jobs.Count > 0);
            StartCommand = new AsyncCommand(StartAsync, () => !IsBusy && !IsScanning && Jobs.Any(j => j.Status == JobStatus.Waiting || j.Status == JobStatus.Stopped), OnError);
            PauseCommand = new RelayCommand(() => _host.Queue.Pause(), () => QueueState == QueueState.Running);
            ResumeCommand = new RelayCommand(() => _host.Queue.Resume(), () => QueueState == QueueState.Paused || QueueState == QueueState.PausePending);
            StopCommand = new RelayCommand(Stop, () => QueueState == QueueState.Running || QueueState == QueueState.Paused || QueueState == QueueState.PausePending);
            OpenResultsCommand = new RelayCommand(OpenResultsFolder);
            RetryErrorsCommand = new RelayCommand(RetryErrors, () => !IsBusy && Jobs.Any(j => j.Status == JobStatus.Error || j.Status == JobStatus.Skipped));
            OpenJobResultCommand = new RelayCommand(p => OpenJobResult(p as VideoJob), p => p is VideoJob j && File.Exists(j.OutputPath));
            ShowInFolderCommand = new RelayCommand(p => ShowInFolder(p as VideoJob), p => p is VideoJob);
            RetryJobCommand = new RelayCommand(p => RetryJob(p as VideoJob), p => !IsBusy && p is VideoJob j && j.Status != JobStatus.Waiting);
            CopyErrorCommand = new RelayCommand(p => CopyError(p as VideoJob), p => p is VideoJob j && !string.IsNullOrEmpty(j.Error));
            OpenSettingsCommand = new RelayCommand(() => NavigateToSettings?.Invoke());

            _host.Queue.StateChanged += s => RunOnUi(() => QueueState = s);
            _host.Queue.JobFinished += j => _statusesDirty = true;

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => RefreshProgress();
            _timer.Start();

            _sourceFolder = _host.Settings.Current.LastSourceFolder ?? "";
            CheckTools();
        }

        public Action NavigateToSettings { get; set; }

        // ---------- Данные ----------

        public ObservableCollection<VideoJob> Jobs { get; } = new ObservableCollection<VideoJob>();
        public ICollectionView JobsView { get; }
        public List<StatusFilterOption> StatusFilters { get; }

        public string SourceFolder
        {
            get => _sourceFolder;
            private set => Set(ref _sourceFolder, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            private set => Set(ref _isScanning, value);
        }

        public string ScanStatus
        {
            get => _scanStatus;
            private set => Set(ref _scanStatus, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value)) JobsView.Refresh();
            }
        }

        public StatusFilterOption StatusFilter
        {
            get => _statusFilter;
            set
            {
                if (Set(ref _statusFilter, value)) JobsView.Refresh();
            }
        }

        public VideoJob SelectedJob
        {
            get => _selectedJob;
            set => Set(ref _selectedJob, value);
        }

        public string ToolProblems
        {
            get => _toolProblems;
            private set => Set(ref _toolProblems, value);
        }

        public string LastRunSummary
        {
            get => _lastRunSummary;
            private set => Set(ref _lastRunSummary, value);
        }

        public QueueState QueueState
        {
            get => _queueState;
            private set
            {
                if (Set(ref _queueState, value))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(QueueStateText));
                    OnPropertyChanged(nameof(IsPaused));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsBusy => QueueState != QueueState.Idle;
        public bool IsPaused => QueueState == QueueState.Paused || QueueState == QueueState.PausePending;

        public string QueueStateText
        {
            get
            {
                switch (QueueState)
                {
                    case QueueState.Running: return "Идёт обработка";
                    case QueueState.PausePending: return "Пауза: ожидание завершения этапа…";
                    case QueueState.Paused: return "Приостановлено";
                    case QueueState.Stopping: return "Остановка…";
                    default: return "Готово к работе";
                }
            }
        }

        // ---------- Счётчики и прогресс (обновляются таймером) ----------

        private int _found, _processed, _queued, _errors;
        private double _overallPercent;
        private string _overallText = "", _remainingText = "—", _remainingHint = "", _elapsedText = "00:00:00";
        private VideoJob _currentJob;

        public int FoundCount { get => _found; private set => Set(ref _found, value); }
        public int ProcessedCount { get => _processed; private set => Set(ref _processed, value); }
        public int QueuedCount { get => _queued; private set => Set(ref _queued, value); }
        public int ErrorCount { get => _errors; private set => Set(ref _errors, value); }
        public double OverallPercent { get => _overallPercent; private set => Set(ref _overallPercent, value); }
        public string OverallText { get => _overallText; private set => Set(ref _overallText, value); }
        public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }
        public string RemainingHint { get => _remainingHint; private set => Set(ref _remainingHint, value); }
        public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

        public VideoJob CurrentJob
        {
            get => _currentJob;
            private set
            {
                if (Set(ref _currentJob, value)) OnPropertyChanged(nameof(HasCurrentJob));
            }
        }

        public bool HasCurrentJob => _currentJob != null;

        // ---------- Команды ----------

        public ICommand ChooseFolderCommand { get; }
        public ICommand RescanCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand OpenResultsCommand { get; }
        public ICommand RetryErrorsCommand { get; }
        public ICommand OpenJobResultCommand { get; }
        public ICommand ShowInFolderCommand { get; }
        public ICommand RetryJobCommand { get; }
        public ICommand CopyErrorCommand { get; }
        public ICommand OpenSettingsCommand { get; }

        // ---------- Компоненты ----------

        public void CheckTools()
        {
            var tools = _host.RefreshTools();
            ToolProblems = tools.IsComplete ? "" : string.Join(Environment.NewLine, tools.Problems);
        }

        // ---------- Сканирование ----------

        private async Task ChooseFolderAsync()
        {
            var folder = _dialogs.PickFolder("Выберите папку с видео (вложенные папки тоже будут обработаны)", SourceFolder);
            if (string.IsNullOrEmpty(folder)) return;
            await ScanAsync(folder);
        }

        public async Task ScanAsync(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            if (!Directory.Exists(folder))
            {
                _dialogs.Show("Папка не найдена", $"Папка «{folder}» не существует или недоступна.", DialogKind.Warning);
                return;
            }

            _probeCts?.Cancel();
            IsScanning = true;
            ScanStatus = "Поиск видео…";
            LastRunSummary = "";
            try
            {
                var settings = _host.Settings.Current;
                _host.Log.Info($"Выбрана исходная папка: {folder}", stage: "Сканирование");
                var progress = new Progress<int>(n => ScanStatus = $"Поиск видео… найдено {n}");
                var result = await _host.Discovery.ScanAsync(folder, settings.VideoExtensions, progress, CancellationToken.None);

                Jobs.Clear();
                foreach (var f in result.Files)
                {
                    var job = new VideoJob(f, folder);
                    try
                    {
                        var fi = new FileInfo(f);
                        job.FileSize = fi.Length;
                        job.LastWriteTimeUtc = fi.LastWriteTimeUtc;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Сведения о файле не критичны — проблема проявится при обработке.
                    }
                    job.PropertyChanged += OnJobPropertyChanged;
                    Jobs.Add(job);
                }
                SourceFolder = folder;

                var list = string.Join(Environment.NewLine, Jobs.Take(2000).Select(j => j.RelativePath));
                if (Jobs.Count > 2000) list += Environment.NewLine + $"… и ещё {Jobs.Count - 2000}";
                _host.Log.Info($"Сканирование завершено: найдено видео {result.Files.Count}, просмотрено папок {result.FoldersScanned}, " +
                               $"недоступных папок {result.InaccessibleFolders}, файлов других форматов {result.SkippedUnsupported}",
                               stage: "Сканирование", details: list);
                ScanStatus = result.Files.Count == 0
                    ? "Видео поддерживаемых форматов не найдены"
                    : result.InaccessibleFolders > 0 ? $"Недоступных папок: {result.InaccessibleFolders} (см. журнал)" : "";

                var s = settings.Clone();
                s.LastSourceFolder = folder;
                _host.Settings.Save(s);

                StartDurationProbe();
            }
            finally
            {
                IsScanning = false;
                RefreshProgress();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Длительности видео читаются в фоне — для общего прогресса и оценки времени.</summary>
        private void StartDurationProbe()
        {
            _probeCts?.Cancel();
            var cts = new CancellationTokenSource();
            _probeCts = cts;
            var jobs = Jobs.ToList();
            if (string.IsNullOrEmpty(_host.Tools.Ffprobe)) return;
            Task.Run(async () =>
            {
                using (var gate = new SemaphoreSlim(2))
                {
                    var tasks = jobs.Select(async j =>
                    {
                        await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                        try
                        {
                            if (j.Duration.HasValue || cts.IsCancellationRequested) return;
                            var info = await _host.Probe.ProbeAsync(j.FullPath, cts.Token).ConfigureAwait(false);
                            if (info.Duration.HasValue && !j.Duration.HasValue) j.Duration = info.Duration;
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            // Повреждённый файл: подробную ошибку покажет обработка.
                            _host.Log.Debug("Не удалось заранее прочитать длительность: " + ex.Message, j.FileName, "Сканирование");
                        }
                        finally
                        {
                            gate.Release();
                        }
                    });
                    try
                    {
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            });
        }

        private void ClearList()
        {
            _probeCts?.Cancel();
            foreach (var j in Jobs) j.PropertyChanged -= OnJobPropertyChanged;
            Jobs.Clear();
            LastRunSummary = "";
            ScanStatus = "";
            _host.Log.Info("Список видео очищен", stage: "Сканирование");
            RefreshProgress();
        }

        // ---------- Обработка ----------

        private async Task StartAsync()
        {
            var settings = _host.Settings.Current.Clone();
            settings.Validate();
            var tools = _host.RefreshTools(settings);
            ToolProblems = tools.IsComplete ? "" : string.Join(Environment.NewLine, tools.Problems);
            if (!tools.IsComplete)
            {
                _host.Log.Error("Обработка не начата: не найдены необходимые компоненты", details: ToolProblems, stage: "Очередь");
                _dialogs.Show("Не хватает компонентов", "Обработка не может начаться. Укажите пути в «Настройках» или запустите scripts\\Install-Dependencies.ps1.",
                    DialogKind.Error, ToolProblems);
                return;
            }

            if (settings.OutputMode == OutputMode.SingleFolder)
            {
                var problem = _host.Export.CheckWritable(settings.OutputFolder);
                if (problem != null)
                {
                    _host.Log.Error("Обработка не начата: папка результатов недоступна", details: problem, stage: "Очередь");
                    _dialogs.Show("Папка результатов недоступна", problem, DialogKind.Error);
                    return;
                }
            }

            LastRunSummary = "";
            _host.StateStore.Save(SourceFolder, Jobs, true);
            var summary = await _host.Queue.RunAsync(Jobs.ToList(), settings, tools, SourceFolder);
            _statusesDirty = true;
            RefreshProgress();

            LastRunSummary = (summary.WasStopped ? "Обработка остановлена" : "Обработка завершена") +
                             $" за {TimeFormat.Clock(summary.Elapsed)}: завершено {summary.Completed}, пропущено {summary.Skipped}, ошибок {summary.Errors}" +
                             (summary.NotStarted + summary.Stopped > 0 ? $", не обработано {summary.NotStarted + summary.Stopped}" : "") + ".";
            if (summary.Errors > 0) LastRunSummary += " Подробности — в столбце «Ошибка» и на странице «Журнал».";
        }

        private void Stop()
        {
            if (!_dialogs.Confirm("Остановить обработку?", "Текущее видео будет прервано, его временные файлы удалены. Остальные видео останутся в очереди.", "Остановить", "Продолжить работу"))
                return;
            _host.Queue.Stop();
        }

        /// <summary>Остановка при закрытии окна: прогресс длинных файлов сохраняется для продолжения.</summary>
        public async Task<bool> StopForExitAsync(TimeSpan timeout)
        {
            if (!IsBusy) return true;
            _host.Queue.Stop(keepCheckpoints: true);
            var sw = Stopwatch.StartNew();
            while (_host.Queue.IsBusy && sw.Elapsed < timeout) await Task.Delay(100);
            // Помечаем очередь как прерванную, чтобы при следующем запуске предложить продолжить.
            _host.StateStore.Save(SourceFolder, Jobs, true);
            return !_host.Queue.IsBusy;
        }

        private void RetryErrors()
        {
            int n = 0;
            foreach (var j in Jobs.Where(j => j.Status == JobStatus.Error || j.Status == JobStatus.Skipped))
            {
                j.Reset();
                n++;
            }
            _host.Log.Info($"Видео возвращены в очередь для повторной обработки: {n}", stage: "Очередь");
            _statusesDirty = true;
            RefreshProgress();
        }

        private void RetryJob(VideoJob job)
        {
            if (job == null) return;
            job.Reset();
            _statusesDirty = true;
            RefreshProgress();
        }

        // ---------- Папки и файлы ----------

        private void OpenResultsFolder()
        {
            var s = _host.Settings.Current;
            string folder = s.OutputMode == OutputMode.SingleFolder ? s.OutputFolder
                : SelectedJob != null ? Path.GetDirectoryName(SelectedJob.FullPath)
                : SourceFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                _dialogs.Show("Папка не найдена", "Папка результатов ещё не выбрана или не существует.", DialogKind.Warning);
                return;
            }
            OpenInExplorer(folder, null);
        }

        private void OpenJobResult(VideoJob job)
        {
            if (job == null || !File.Exists(job.OutputPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo(job.OutputPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _dialogs.Show("Не удалось открыть файл", ex.Message, DialogKind.Error);
            }
        }

        private void ShowInFolder(VideoJob job)
        {
            if (job == null) return;
            var target = File.Exists(job.OutputPath) ? job.OutputPath : job.FullPath;
            OpenInExplorer(Path.GetDirectoryName(target), target);
        }

        public static void OpenInExplorer(string folder, string select)
        {
            try
            {
                if (select != null && File.Exists(select))
                    Process.Start("explorer.exe", "/select,\"" + select + "\"");
                else
                    Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch
            {
                // Проводник недоступен — молча игнорируем.
            }
        }

        private void CopyError(VideoJob job)
        {
            if (job == null) return;
            try
            {
                Clipboard.SetText($"{job.RelativePath}{Environment.NewLine}{job.Error}");
            }
            catch
            {
                // Буфер обмена занят.
            }
        }

        // ---------- Фильтр и обновление ----------

        private bool FilterJob(object o)
        {
            var j = (VideoJob)o;
            if (!string.IsNullOrWhiteSpace(_searchText) &&
                j.RelativePath.IndexOf(_searchText.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0)
                return false;
            var f = _statusFilter;
            if (f == null || (f.Status == null && !f.ActiveOnly)) return true;
            if (f.ActiveOnly) return !j.IsFinished && j.Status != JobStatus.Waiting && j.Status != JobStatus.Stopped;
            return j.Status == f.Status;
        }

        private void OnJobPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VideoJob.Status)) _statusesDirty = true;
        }

        private void RefreshProgress()
        {
            var jobs = Jobs.ToList();
            FoundCount = jobs.Count;
            ProcessedCount = jobs.Count(j => j.Status == JobStatus.Completed || j.Status == JobStatus.Skipped);
            ErrorCount = jobs.Count(j => j.Status == JobStatus.Error);
            QueuedCount = jobs.Count(j => j.Status == JobStatus.Waiting || j.Status == JobStatus.Stopped);

            var current = jobs.FirstOrDefault(j => !j.IsFinished && j.Status != JobStatus.Waiting && j.Status != JobStatus.Stopped);
            CurrentJob = current;

            if (IsBusy)
            {
                var snap = _host.Estimator.Compute(jobs, _host.Queue.GetLiveProgress(), _host.Queue.Parallelism);
                OverallPercent = snap.OverallPercent;
                OverallText = $"{snap.OverallPercent:0} % · обработано {snap.Done} из {snap.Total}, осталось {snap.Remaining}";
                RemainingText = IsPaused ? "на паузе" : snap.RemainingText;
                RemainingHint = snap.Basis;
                if (_host.Queue.StartedAt.HasValue) ElapsedText = TimeFormat.Clock(DateTime.Now - _host.Queue.StartedAt.Value);
            }
            else
            {
                var snap = _host.Estimator.Compute(jobs, null, 1);
                OverallPercent = jobs.Count == 0 ? 0 : snap.OverallPercent;
                OverallText = jobs.Count == 0 ? "Выберите папку с видео" : $"{snap.OverallPercent:0} % · обработано {snap.Done} из {snap.Total}";
                RemainingText = "—";
                RemainingHint = "";
            }

            if (_statusesDirty)
            {
                _statusesDirty = false;
                if (_statusFilter != null && (_statusFilter.Status != null || _statusFilter.ActiveOnly)) JobsView.Refresh();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public void RestoreFromSnapshot(QueueSnapshot snapshot)
        {
            int restored = QueueStateStore.ApplySnapshot(snapshot, Jobs);
            _host.Log.Info($"Очередь восстановлена после прерванного сеанса: статусов перенесено {restored}, ожидают обработки {Jobs.Count(j => j.Status == JobStatus.Waiting)}",
                stage: "Восстановление");
            LastRunSummary = "Очередь восстановлена после прерванного сеанса. Нажмите «Начать обработку», чтобы продолжить.";
            RefreshProgress();
        }

        private void OnError(Exception ex)
        {
            _host.Log.Error("Ошибка операции интерфейса: " + ex.Message, ex, stage: "Интерфейс");
            _dialogs.Show("Ошибка", ex.Message, DialogKind.Error, ex.ToString());
        }

        private static void RunOnUi(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a();
            else d.BeginInvoke(a);
        }
    }
}
