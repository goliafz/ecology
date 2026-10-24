using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using VidLex.App.Services;
using VidLex.App.ViewModels;
using VidLex.Core;
using VidLex.Core.Models;

namespace VidLex.App
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly VidLexHost _host;
        private bool _closingConfirmed;

        public MainWindow(MainViewModel vm, VidLexHost host)
        {
            InitializeComponent();
            WindowTheme.ApplyDarkTitleBar(this);
            FitToWorkArea();
            _vm = vm;
            _host = host;
            DataContext = vm;
            Title = MainViewModel.WindowTitle;

            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.CurrentPage)) UpdateNav();
            };
            UpdateNav();

            // Прогресс на кнопке в панели задач Windows.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => UpdateTaskbar();
            timer.Start();
        }

        /// <summary>На небольших экранах (1366×768 и меньше) окно не должно выходить за пределы рабочей области.</summary>
        private void FitToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            if (Width > area.Width * 0.95) Width = Math.Max(MinWidth, area.Width * 0.95);
            if (Height > area.Height * 0.95) Height = Math.Max(Math.Min(MinHeight, area.Height), area.Height * 0.95);
            if (MinWidth > area.Width) MinWidth = area.Width;
            if (MinHeight > area.Height) MinHeight = area.Height;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetDroppedFolder(e) != null && !_vm.Home.IsBusy ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>Папку можно перетащить на окно — она будет просканирована.</summary>
        private async void Window_Drop(object sender, DragEventArgs e)
        {
            var folder = GetDroppedFolder(e);
            if (folder == null || _vm.Home.IsBusy) return;
            _vm.CurrentPage = Page.Home;
            try
            {
                await _vm.Home.ScanAsync(folder);
            }
            catch (Exception ex)
            {
                _host.Log.Error("Ошибка сканирования перетащенной папки", ex, stage: "Сканирование");
            }
        }

        private static string GetDroppedFolder(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            var items = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (items == null || items.Length == 0) return null;
            var first = items[0];
            if (System.IO.Directory.Exists(first)) return first;
            if (System.IO.File.Exists(first)) return System.IO.Path.GetDirectoryName(first);
            return null;
        }

        private void UpdateNav()
        {
            NavHome.Tag = _vm.CurrentPage == Page.Home ? "Active" : null;
            NavSettings.Tag = _vm.CurrentPage == Page.Settings ? "Active" : null;
            NavLog.Tag = _vm.CurrentPage == Page.Log ? "Active" : null;
            NavAbout.Tag = _vm.CurrentPage == Page.About ? "Active" : null;
        }

        private void UpdateTaskbar()
        {
            var home = _vm.Home;
            if (!home.IsBusy)
            {
                Taskbar.ProgressState = TaskbarItemProgressState.None;
                return;
            }
            Taskbar.ProgressState = home.IsPaused ? TaskbarItemProgressState.Paused
                : home.ErrorCount > 0 ? TaskbarItemProgressState.Error
                : TaskbarItemProgressState.Normal;
            Taskbar.ProgressValue = Math.Max(0, Math.Min(1, home.OverallPercent / 100.0));
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_closingConfirmed || !_vm.Home.IsBusy)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            var dialogs = new DialogService();
            if (!dialogs.Confirm("Идёт обработка",
                    "Остановить обработку и закрыть VidLex?\n\nПрогресс длинных видео будет сохранён: при следующем запуске программа предложит продолжить.",
                    "Остановить и закрыть", "Продолжить работу"))
                return;

            IsEnabled = false;
            _host.Log.Info("Закрытие окна во время обработки: остановка очереди с сохранением контрольных точек", stage: "Приложение");
            await _vm.Home.StopForExitAsync(TimeSpan.FromSeconds(15));
            _closingConfirmed = true;
            Close();
        }
    }
}
