using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using URLChecker.Properties;
using xNet;

namespace URLChecker
{
    // Параметры прокси, с которыми работают потоки проверки
    public class ProxyConfig
    {
        public string Type = "HTTP";
        public string Host = "";
        public int Port = 8080;
        public string User = "";
        public string Password = "";

        public string Address
        {
            get { return Host + ":" + Port; }
        }

        public ProxyClient CreateClient(int timeout)
        {
            ProxyClient proxy;
            string user = String.IsNullOrEmpty(User) ? null : User;
            string password = String.IsNullOrEmpty(Password) ? null : Password;

            switch (Type)
            {
                case "SOCKS4":
                    proxy = new Socks4ProxyClient(Host, Port, user);
                    break;
                case "SOCKS5":
                    proxy = new Socks5ProxyClient(Host, Port, user, password);
                    break;
                default:
                    proxy = new HttpProxyClient(Host, Port, user, password);
                    break;
            }
            if (timeout > 0)
            {
                proxy.ConnectTimeout = timeout;
                proxy.ReadWriteTimeout = timeout;
            }
            return proxy;
        }

        public static ProxyConfig FromSettings()
        {
            return new ProxyConfig
            {
                Type = Settings.Default.proxyType,
                Host = Settings.Default.proxyHost.Trim(),
                Port = Settings.Default.proxyPort,
                User = Settings.Default.proxyUser,
                Password = UnprotectPassword(Settings.Default.proxyPassword)
            };
        }

        // Пароль хранится в настройках зашифрованным (DPAPI, для текущего пользователя Windows)
        public static string ProtectPassword(string password)
        {
            if (String.IsNullOrEmpty(password))
                return "";
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string UnprotectPassword(string stored)
        {
            if (String.IsNullOrEmpty(stored))
                return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    public class Network
    {
        public HttpStatusCode status;

        // null - прокси выключен
        static volatile ProxyConfig activeProxy;

        public static ProxyConfig ActiveProxy
        {
            get { return activeProxy; }
        }

        // Перечитать настройки прокси (при запуске, включении/выключении и после сохранения настроек)
        public static void ReloadProxy()
        {
            if (Settings.Default.proxyEnabled && Settings.Default.proxyHost.Trim() != "")
                activeProxy = ProxyConfig.FromSettings();
            else
                activeProxy = null;
        }

        static void ApplyProxy(HttpRequest request, int timeout)
        {
            ProxyConfig config = activeProxy;
            if (config != null)
                request.Proxy = config.CreateClient(timeout);
        }

        public string GetContentFromURL(string url)
        {
            using (var request = new HttpRequest())
            {
                request.ConnectTimeout = Convert.ToInt32(Settings.Default.timeout);
                ApplyProxy(request, request.ConnectTimeout);
                //request.CharacterSet = Encoding.GetEncoding(1251);
                //request.CharacterSet = Encoding.UTF8;
                request.UserAgent = Http.ChromeUserAgent();
                //request.IgnoreProtocolErrors = true;
                //request.ReadWriteTimeout = 1000;

                // Отправляем запрос.
                try
                {                    
                    HttpResponse response = request.Get(url);
                    status = response.StatusCode;
                    // Принимаем тело сообщения в виде строки.
                    string content = response.ToString();
                    return content;
                }
                catch (Exception E)
                {
                    status = HttpStatusCode.None;
                }
                return "";
            }
        }
        public string GetRobotsFromURL(string url)
        {
            using (var request = new HttpRequest())
            {
                request.UserAgent = Http.ChromeUserAgent();
                ApplyProxy(request, Convert.ToInt32(Settings.Default.timeout));
                // Отправляем запрос.
                HttpResponse response = request.Get(url);
                status = response.StatusCode;
                // Принимаем тело сообщения в виде строки.
                string content = response.ToString();
                return content;
            }
        }


    }
}
