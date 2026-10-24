using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VidLex.App.Services;
using VidLex.App.ViewModels;
using VidLex.Core;
using VidLex.Core.Infrastructure;
using VidLex.Core.Models;

namespace VidLex.App
{
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private VidLexHost _host;

        public static VidLexHost Host { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstance = new Mutex(true, "Local\\VidLex.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show("VidLex уже запущен. Переключитесь на открытое окно программы.", "VidLex", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            try
            {
                var paths = new AppPaths();
                var dictionaries = Path.Combine(paths.AppDirectory, "Dictionaries");
                _host = new VidLexHost(paths, dictionaries);
                Host = _host;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось запустить VidLex: " + ex.Message, "VidLex", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            var log = _host.Log;
            log.Info($"Запуск VidLex {VidLexHost.AppVersion} (разработчик: Ходаковский Александр)", stage: "Приложение",
                details: $"ОС: {Environment.OSVersion}; 64-бит: {Environment.Is64BitProcess}; .NET CLR {Environment.Version}; процессоров: {Environment.ProcessorCount}; " +
                         $"данные: {_host.Paths.DataRoot}");

            var dialogs = new DialogService();
            var vm = new MainViewModel(_host, dialogs);
            var window = new MainWindow(vm, _host);
            MainWindow = window;
            window.Show();

            // Словарь грузится в фоне, чтобы первое задание не ждало.
            Task.Run(() => _host.Text.Preload());

            // Параметры запуска: VidLex.exe [--source <папка>] [--start] или просто путь к папке.
            string source = null;
            bool autoStart = false;
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (string.Equals(e.Args[i], "--source", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length) source = e.Args[++i];
                else if (string.Equals(e.Args[i], "--start", StringComparison.OrdinalIgnoreCase)) autoStart = true;
                else if (Directory.Exists(e.Args[i])) source = e.Args[i];
            }

            if (source != null) OpenFromCommandLine(vm, source, autoStart);
            else RecoverPreviousSession(vm, dialogs);
        }

        private async void OpenFromCommandLine(MainViewModel vm, string source, bool autoStart)
        {
            try
            {
                _host.CleanupTemp(keepCheckpoints: true);
                _host.Log.Info($"Папка передана при запуске: {source}" + (autoStart ? " (автозапуск обработки)" : ""), stage: "Приложение");
                await vm.Home.ScanAsync(Path.GetFullPath(source));
                if (autoStart && vm.Home.StartCommand.CanExecute(null)) vm.Home.StartCommand.Execute(null);
            }
            catch (Exception ex)
            {
                _host.Log.Error("Не удалось открыть папку из параметров запуска", ex, stage: "Приложение");
            }
        }

        /// <summary>Если прошлый сеанс прервался во время обработки — предлагаем продолжить.</summary>
        private async void RecoverPreviousSession(MainViewModel vm, IDialogService dialogs)
        {
            try
            {
                var snapshot = _host.StateStore.Load();
                if (snapshot != null && snapshot.IsRunning && Directory.Exists(snapshot.SourceFolder))
                {
                    var unfinished = snapshot.Jobs.Count(j => j.Status != JobStatus.Completed && j.Status != JobStatus.Skipped && j.Status != JobStatus.Error);
                    _host.Log.Warning($"Предыдущая обработка была прервана (закрытие программы или сбой). Необработанных видео: {unfinished}",
                        stage: "Восстановление", details: $"Папка: {snapshot.SourceFolder}; сохранено: {snapshot.SavedAt:dd.MM.yyyy HH:mm:ss}");
                    bool restore = dialogs.Confirm("Продолжить прерванную обработку?",
                        $"Предыдущая обработка папки «{snapshot.SourceFolder}» не была завершена (видео без результата: {unfinished}).\n\n" +
                        "Восстановить очередь? Длинные видео продолжатся с сохранённого места.", "Восстановить", "Не нужно");
                    if (restore)
                    {
                        _host.CleanupTemp(keepCheckpoints: true);
                        await vm.Home.ScanAsync(snapshot.SourceFolder);
                        vm.Home.RestoreFromSnapshot(snapshot);
                        return;
                    }
                    _host.Log.Info("Пользователь отказался от восстановления очереди", stage: "Восстановление");
                    _host.StateStore.MarkStopped();
                }
                _host.CleanupTemp(keepCheckpoints: false);
            }
            catch (Exception ex)
            {
                _host.Log.Error("Ошибка при восстановлении предыдущего сеанса", ex, stage: "Восстановление");
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _host?.Log.Error("Необработанная ошибка интерфейса: " + e.Exception.Message, e.Exception, stage: "Приложение");
            e.Handled = true;
            try
            {
                new DialogService().Show("Непредвиденная ошибка",
                    "Произошла ошибка, но VidLex продолжает работу. Подробности записаны в журнал.", DialogKind.Error, e.Exception.ToString());
            }
            catch
            {
                // Если не удалось показать окно — ошибка уже в журнале.
            }
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            _host?.Log.Error("Критическая ошибка: " + ex?.Message, ex, stage: "Приложение");
            _host?.Log.Flush();
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            _host?.Log.Error("Необработанная ошибка фоновой задачи: " + e.Exception.GetBaseException().Message, e.Exception, stage: "Приложение");
            e.SetObserved();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                _host.Log.Info("Завершение работы VidLex", stage: "Приложение");
                _host.Log.Flush();
                _host.Dispose();
            }
            if (_singleInstance != null)
            {
                try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
                _singleInstance.Dispose();
            }
            base.OnExit(e);
        }
    }
}
