using System.Windows.Input;
using VidLex.App.Services;
using VidLex.Core;

namespace VidLex.App.ViewModels
{
    public enum Page
    {
        Home,
        Settings,
        Log,
        About
    }

    /// <summary>Главное окно: навигация между страницами.</summary>
    public sealed class MainViewModel : ObservableObject
    {
        private Page _page = Page.Home;

        public MainViewModel(VidLexHost host, IDialogService dialogs)
        {
            Home = new HomeViewModel(host, dialogs);
            Settings = new SettingsViewModel(host, dialogs);
            Log = new LogViewModel(host, dialogs);
            About = new AboutViewModel(host);

            Home.NavigateToSettings = () => CurrentPage = Page.Settings;
            Settings.SettingsSaved = () => Home.CheckTools();

            NavigateCommand = new RelayCommand(p =>
            {
                if (p is Page page) CurrentPage = page;
                else if (p is string s && System.Enum.TryParse(s, out Page parsed)) CurrentPage = parsed;
            });
        }

        public const string WindowTitle = "VidLex — разработчик Ходаковский Александр";

        public HomeViewModel Home { get; }
        public SettingsViewModel Settings { get; }
        public LogViewModel Log { get; }
        public AboutViewModel About { get; }

        public ICommand NavigateCommand { get; }

        public Page CurrentPage
        {
            get => _page;
            set
            {
                // Несохранённые правки настроек не теряются при переходе между страницами.
                if (Set(ref _page, value))
                {
                    OnPropertyChanged(nameof(CurrentViewModel));
                    if (value == Page.Settings) Settings.OnShown();
                }
            }
        }

        public object CurrentViewModel
        {
            get
            {
                switch (_page)
                {
                    case Page.Settings: return Settings;
                    case Page.Log: return Log;
                    case Page.About: return About;
                    default: return Home;
                }
            }
        }
    }
}
