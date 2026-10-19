using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using MasterVks.Core;
using MasterVks.Video;

namespace MasterVks.UI
{
    /// <summary>
    /// Живая картинка с камеры в отдельном окне. Камера занята, только пока окно
    /// открыто, и не дольше минуты: потом окно закрывается само, чтобы камера
    /// освободилась для ВКС.
    /// </summary>
    internal sealed class CameraForm : Form
    {
        private const int AutoCloseSeconds = 60;

        private readonly string _ffmpeg;
        private readonly CameraDevice _device;
        private readonly ScreenPreview _view;
        private readonly Label _status;
        private readonly Timer _timer;
        private readonly object _sync = new object();
        private CameraGrabber _grabber;
        private DateTime _openedAt;
        private volatile bool _closing;
        private double _lastShown = -1;
        private int _frames;

        /// <summary>Последний кадр: окно отдаёт его главному окну как снимок.</summary>
        public CameraFrame LastFrame { get; private set; }

        public CameraForm(string ffmpegPath, CameraDevice device)
        {
            _ffmpeg = ffmpegPath;
            _device = device;
            Text = "Камера · " + device.Name;
            Font = Theme.Base;
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(688), Theme.S(486));
            DialogKeys.CloseOnEscape(this);

            _view = new ScreenPreview
            {
                Location = new Point(Theme.S(24), Theme.S(20)),
                Size = new Size(Theme.S(640), Theme.S(360)),
                Placeholder = "Включаю камеру…",
                BackColor = Color.White
            };
            _status = new Label
            {
                Location = new Point(Theme.S(24), Theme.S(390)),
                Size = new Size(Theme.S(640), Theme.S(40)),
                ForeColor = Theme.Muted,
                UseMnemonic = false,
                Text = "Включаю камеру. Обычно это занимает пару секунд."
            };
            var close = new ModernButton("Закрыть", ButtonKind.Primary) { Size = new Size(Theme.S(120), Theme.S(38)), Location = new Point(Theme.S(544), Theme.S(434)) };
            close.Click += (s, e) => Close();
            Controls.Add(_view);
            Controls.Add(_status);
            Controls.Add(close);

            _timer = new Timer { Interval = 500 };
            _timer.Tick += (s, e) => Tick();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _openedAt = DateTime.Now;
            _timer.Start();
            Task.Run(() =>
            {
                try
                {
                    var grabber = new CameraGrabber(_ffmpeg, CameraDevices.InputArgs(_device), Theme.S(640), Theme.S(360), OnFrame);
                    bool late;
                    lock (_sync)
                    {
                        late = _closing;
                        if (!late) _grabber = grabber;
                    }
                    // Окно уже закрыли, пока камера включалась: сразу отпускаем её.
                    if (late) grabber.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Не удалось включить камеру для просмотра", ex);
                    Post(() => ShowError("Камеру включить не удалось: " + ex.Message));
                }
            });
        }

        private void OnFrame(CameraFrame f)
        {
            if (_closing) return;
            _frames++;
            LastFrame = f;
            // Для глаза хватает 15 кадров в секунду, лишние не рисуем.
            if (f.Seconds - _lastShown < 1.0 / 15) return;
            _lastShown = f.Seconds;
            Bitmap bmp = f.ToBitmap();
            Post(() =>
            {
                if (_closing) bmp.Dispose();
                else _view.SetImage(bmp);
            });
        }

        private void Tick()
        {
            int left = AutoCloseSeconds - (int)(DateTime.Now - _openedAt).TotalSeconds;
            if (left <= 0)
            {
                Close();
                return;
            }
            CameraGrabber g = _grabber;
            if (g == null) return;
            if (_frames == 0)
            {
                if (g.Exited) ShowError(null);
                else if ((DateTime.Now - _openedAt).TotalSeconds > 10) ShowError(null);
                return;
            }
            string facts = _device.Name +
                           (g.NativeSize.Width > 0 ? " · " + g.NativeSize.Width + " × " + g.NativeSize.Height : "") +
                           (g.NativeFps > 0 ? " · " + Math.Round(g.NativeFps).ToString(CultureInfo.InvariantCulture) + " кадров/с" : "");
            if (g.Exited)
            {
                _status.Text = facts + ". Камера перестала присылать картинку: возможно, её отключили или забрала другая программа.";
                _view.SetBadge(null, Color.Empty);
                return;
            }
            _view.SetBadge("● ВЖИВУЮ", Theme.Green);
            _status.Text = facts + ". Окно закроется само через " + left + " с, чтобы камера освободилась для ВКС.";
        }

        private void ShowError(string text)
        {
            CameraGrabber g = _grabber;
            if (text == null && g != null)
            {
                CameraFailure failure = CameraErrors.Classify(g.Exited ? g.LogText : "");
                if (failure == CameraFailure.Busy)
                    text = "Камера занята другой программой. Если идёт ВКС, картинку с камеры видно в её окне.";
                else if (failure == CameraFailure.NotFound)
                    text = "Windows не нашла камеру. Проверьте USB-кабель.";
                else if (g.Exited)
                    text = "Камера не открылась. " + CameraErrors.LastError(g.LogText);
                else
                    text = "Камера включилась, но не присылает картинку. Переподключите её, лучше в другой USB-порт.";
            }
            _view.Placeholder = text;
            _status.Text = text;
        }

        private void Post(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (Exception)
            {
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            CameraGrabber g;
            lock (_sync)
            {
                _closing = true;
                g = _grabber;
                _grabber = null;
            }
            // Камеру отпускаем в фоне: ffmpeg может закрываться секунду-две.
            if (g != null) Task.Run(() => g.Dispose());
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
