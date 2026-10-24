using System.Windows;
using System.Windows.Media;
using VidLex.App.Services;

namespace VidLex.App.Views
{
    public partial class MessageDialog : Window
    {
        public MessageDialog(string title, string message, DialogKind kind, string details, string yesText, string noText)
        {
            InitializeComponent();
            WindowTheme.ApplyDarkTitleBar(this);
            Title = "VidLex — " + title;
            TitleText.Text = title;
            MessageText.Text = message;
            YesButton.Content = yesText;
            if (noText == null)
            {
                NoButton.Visibility = Visibility.Collapsed;
                YesButton.IsCancel = true;
            }
            else
            {
                NoButton.Content = noText;
            }
            if (!string.IsNullOrWhiteSpace(details))
            {
                DetailsPanel.Visibility = Visibility.Visible;
                DetailsText.Text = details;
            }
            string key = kind == DialogKind.Error ? "ErrorBrush" : kind == DialogKind.Warning ? "WarningBrush" : "AccentBrush";
            KindMark.Background = (Brush)FindResource(key);
        }

        private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void CopyDetails_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TitleText.Text + "\r\n" + MessageText.Text + "\r\n\r\n" + DetailsText.Text);
            }
            catch
            {
                // Буфер обмена занят другим приложением — не критично.
            }
        }
    }
}
