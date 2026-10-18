using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using MasterVks.Core;
using MasterVks.UI;

namespace MasterVks
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\MasterVKS.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // Программа уже открыта — просто показываем её окно.
                    MainForm.SignalExistingInstance();
                    return;
                }

                Log.Init();
                Log.Info("========== МастерВКС " + typeof(Program).Assembly.GetName().Version + " запущена, Windows " + Environment.OSVersion.Version + ", " + (Environment.Is64BitProcess ? "64" : "32") + " бит ==========");

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                {
                    // Ошибка в интерфейсе не должна закрывать программу и обрывать запись.
                    Log.Error("Необработанная ошибка в интерфейсе", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log.Error("Критическая ошибка (программа будет закрыта). Запись, которая шла, восстановится при следующем запуске.", e.ExceptionObject as Exception);
                };

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Theme.InitScale();

                try
                {
                    Native.timeBeginPeriod(1);
                }
                catch (Exception)
                {
                }
                PowerHelper.DisableThrottling(System.Diagnostics.Process.GetCurrentProcess().Handle);

                AppSettings settings = AppSettings.Load();
                // Обновляем путь в автозапуске (папку с программой могли перенести) и убираем запись старой версии.
                Autostart.Apply(settings.Autostart);
                bool autostart = args.Any(a => string.Equals(a, Autostart.Argument, StringComparison.OrdinalIgnoreCase));
                try
                {
                    Application.Run(new MainForm(settings, autostart));
                }
                catch (Exception ex)
                {
                    Log.Error("Программа завершилась из-за ошибки", ex);
                    MessageBox.Show("Программа завершилась из-за ошибки:\n\n" + ex.Message + "\n\nПодробности сохранены в журнале:\n" + Log.Directory,
                        "МастерВКС", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    try
                    {
                        Native.timeEndPeriod(1);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }
}
