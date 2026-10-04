using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using URLChecker.Properties;
using xNet;

namespace URLChecker
{
    public class Network
    {
        public HttpStatusCode status;

        public string GetContentFromURL(string url)
        {
            using (var request = new HttpRequest())
            {
                request.ConnectTimeout = Convert.ToInt32(Settings.Default.timeout);
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
