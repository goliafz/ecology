using System;
using System.Threading;
using System.Windows.Forms;

namespace FastGen
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Необработанная ошибка не должна закрывать программу вместе с несохранённым шаблоном
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;

            Application.Run(new MainForm());
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            MessageBox.Show(
                "Произошла ошибка, но работа продолжается:\r\n\r\n" + e.Exception.Message +
                "\r\n\r\nЕсли повторяется — пришлите текст ошибки:\r\n" + e.Exception.GetType().Name,
                "FastGen — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
