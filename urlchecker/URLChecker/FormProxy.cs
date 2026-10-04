using System;
using System.Threading;
using System.Windows.Forms;
using URLChecker.Properties;
using xNet;

namespace URLChecker
{
    public partial class FormProxy : Form
    {
        static readonly string[] types = { "HTTP", "SOCKS4", "SOCKS5" };

        // Отметить «Использовать прокси» при открытии (когда окно открыто кнопкой включения)
        public bool EnableOnOpen { get; set; }

        public FormProxy()
        {
            InitializeComponent();
        }

        private void FormProxy_Shown(object sender, EventArgs e)
        {
            chkEnabled.Checked = Settings.Default.proxyEnabled || EnableOnOpen;
            int index = Array.IndexOf(types, Settings.Default.proxyType);
            cbType.SelectedIndex = index >= 0 ? index : 0;
            tHost.Text = Settings.Default.proxyHost;
            nPort.Value = Math.Max(nPort.Minimum, Math.Min(nPort.Maximum, Settings.Default.proxyPort));
            tUser.Text = Settings.Default.proxyUser;
            tPassword.Text = ProxyConfig.UnprotectPassword(Settings.Default.proxyPassword);
        }

        ProxyConfig GetConfig()
        {
            return new ProxyConfig
            {
                Type = types[Math.Max(cbType.SelectedIndex, 0)],
                Host = tHost.Text.Trim(),
                Port = Convert.ToInt32(nPort.Value),
                User = tUser.Text.Trim(),
                Password = tPassword.Text
            };
        }

        // Разбор строки вида ip:port или ip:port:login:password, вставленной в поле адреса
        private void tHost_Leave(object sender, EventArgs e)
        {
            string[] parts = tHost.Text.Trim().Split(':');
            int port;
            if (parts.Length < 2 || !int.TryParse(parts[1], out port) || port < 1 || port > 65535)
                return;

            tHost.Text = parts[0];
            nPort.Value = port;
            if (parts.Length >= 4)
            {
                tUser.Text = parts[2];
                tPassword.Text = String.Join(":", parts, 3, parts.Length - 3);
            }
        }

        private void chkShowPassword_CheckedChanged(object sender, EventArgs e)
        {
            tPassword.UseSystemPasswordChar = !chkShowPassword.Checked;
        }

        private void bSave_Click(object sender, EventArgs e)
        {
            tHost_Leave(this, null);
            ProxyConfig config = GetConfig();
            if (chkEnabled.Checked && config.Host == "")
            {
                MessageBox.Show("Укажите адрес прокси-сервера.", "Прокси", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                tHost.Focus();
                return;
            }

            Settings.Default.proxyEnabled = chkEnabled.Checked;
            Settings.Default.proxyType = config.Type;
            Settings.Default.proxyHost = config.Host;
            Settings.Default.proxyPort = config.Port;
            Settings.Default.proxyUser = config.User;
            Settings.Default.proxyPassword = ProxyConfig.ProtectPassword(config.Password);
            Settings.Default.Save();

            DialogResult = DialogResult.OK;
            Close();
        }

        private void bCheck_Click(object sender, EventArgs e)
        {
            tHost_Leave(this, null);
            ProxyConfig config = GetConfig();
            if (config.Host == "")
            {
                lCheck.Text = "Укажите адрес прокси-сервера";
                return;
            }

            bCheck.Enabled = false;
            lCheck.ForeColor = System.Drawing.SystemColors.ControlText;
            lCheck.Text = "Проверка...";
            int timeout = Math.Max(Convert.ToInt32(Settings.Default.timeout), 10000);

            Thread check = new Thread(() =>
            {
                string result;
                bool ok = false;
                try
                {
                    using (var request = new HttpRequest())
                    {
                        request.ConnectTimeout = timeout;
                        request.UserAgent = Http.ChromeUserAgent();
                        request.Proxy = config.CreateClient(timeout);
                        string ip = request.Get("http://api.ipify.org/").ToString().Trim();
                        result = "Прокси работает. Внешний IP: " + ip;
                        ok = true;
                    }
                }
                catch (Exception E)
                {
                    result = "Ошибка: " + E.Message;
                }

                if (IsDisposed)
                    return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        lCheck.ForeColor = ok ? System.Drawing.Color.Green : System.Drawing.Color.FromArgb(192, 0, 0);
                        lCheck.Text = result;
                        bCheck.Enabled = true;
                    }));
                }
                catch (InvalidOperationException)
                {
                    // Окно закрыли во время проверки
                }
            });
            check.IsBackground = true;
            check.Start();
        }
    }
}
