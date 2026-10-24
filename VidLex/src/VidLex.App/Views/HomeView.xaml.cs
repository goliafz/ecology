using System.Windows.Controls;
using System.Windows.Input;
using VidLex.App.ViewModels;

namespace VidLex.App.Views
{
    public partial class HomeView : UserControl
    {
        public HomeView()
        {
            InitializeComponent();
        }

        private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is HomeViewModel vm && vm.SelectedJob != null && vm.OpenJobResultCommand.CanExecute(vm.SelectedJob))
                vm.OpenJobResultCommand.Execute(vm.SelectedJob);
        }
    }
}
