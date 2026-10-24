using System;
using System.IO;
using System.Linq;
using System.Windows;
using Ookii.Dialogs.Wpf;
using VidLex.App.Views;

namespace VidLex.App.Services
{
    public enum DialogKind
    {
        Info,
        Warning,
        Error,
        Question
    }

    /// <summary>Диалоги: выбор папок и файлов (системные окна Windows) и сообщения в стиле VidLex.</summary>
    public interface IDialogService
    {
        string PickFolder(string title, string initial);
        string PickFile(string title, string filter, string initial);
        void Show(string title, string message, DialogKind kind = DialogKind.Info, string details = null);
        bool Confirm(string title, string message, string yesText = "Да", string noText = "Нет");
    }

    public sealed class DialogService : IDialogService
    {
        private static Window Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

        public string PickFolder(string title, string initial)
        {
            var dlg = new VistaFolderBrowserDialog
            {
                Description = title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                dlg.SelectedPath = initial.TrimEnd('\\') + "\\";
            return dlg.ShowDialog(Owner) == true ? dlg.SelectedPath : null;
        }

        public string PickFile(string title, string filter, string initial)
        {
            var dlg = new VistaOpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                Multiselect = false
            };
            if (!string.IsNullOrWhiteSpace(initial))
            {
                try
                {
                    var dir = File.Exists(initial) ? Path.GetDirectoryName(initial) : initial;
                    if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
                }
                catch (ArgumentException)
                {
                }
            }
            return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
        }

        public void Show(string title, string message, DialogKind kind = DialogKind.Info, string details = null)
        {
            var w = new MessageDialog(title, message, kind, details, "OK", null) { Owner = Owner };
            w.ShowDialog();
        }

        public bool Confirm(string title, string message, string yesText = "Да", string noText = "Нет")
        {
            var w = new MessageDialog(title, message, DialogKind.Question, null, yesText, noText) { Owner = Owner };
            return w.ShowDialog() == true;
        }
    }
}
