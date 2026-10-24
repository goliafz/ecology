using System.Windows;
using System.Windows.Controls;
using VidLex.App.ViewModels;
using VidLex.Core.Models;

namespace VidLex.App.Views
{
    public partial class LogView : UserControl
    {
        private LogViewModel _vm;

        public LogView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Unloaded += (s, e) => Detach();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Detach();
            _vm = DataContext as LogViewModel;
            if (_vm != null) _vm.NewEntriesAdded += ScrollTo;
        }

        private void Detach()
        {
            if (_vm != null) _vm.NewEntriesAdded -= ScrollTo;
        }

        private void ScrollTo(LogEntry entry)
        {
            if (entry != null && LogGrid.Items.Contains(entry)) LogGrid.ScrollIntoView(entry);
        }
    }
}
