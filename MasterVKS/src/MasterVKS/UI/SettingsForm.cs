using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MasterVks.Core;
using MasterVks.Recording;

namespace MasterVks.UI
{
    /// <summary>Дополнительные настройки: качество, проверка, горячие клавиши, автозапуск, FFmpeg.</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly ComboBox _fps;
        private readonly ComboBox _quality;
        private readonly CheckBox _cursor;
        private readonly CheckBox _mixMic1, _mixMic2, _mixSystem;
        private readonly NumericUpDown _offset;
        private readonly RadioButton _startupSilent, _startupAudible;
        private readonly CheckBox _confirmStop, _hotkeys, _autostart, _autostartMinimized, _keepMkv;
        private readonly Label _ffmpegStatus;
        private string _ffmpegPath;

        public SettingsForm(AppSettings settings, bool recording)
        {
            Text = "Настройки · МастерВКС";
            Font = Theme.Base;
            BackColor = Theme.Background;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(680), Theme.S(640));
            _ffmpegPath = settings.FfmpegPath;
            DialogKeys.CloseOnEscape(this);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(Theme.S(16), Theme.S(12), Theme.S(16), Theme.S(4)) };
            var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, BackColor = Theme.Background };
            int width = Theme.S(630);

            // --- Видео ---
            var video = Section("Видео", width);
            _fps = Combo(new object[] { "15 кадров в секунду, экономно", "25 кадров в секунду", "30 кадров в секунду (рекомендуется)", "60 кадров в секунду: очень плавно, нужен мощный компьютер" });
            _fps.SelectedIndex = settings.FrameRate == 15 ? 0 : settings.FrameRate == 25 ? 1 : settings.FrameRate == 60 ? 3 : 2;
            _quality = Combo(new object[] { "Максимальное, как на экране (рекомендуется)", "Хорошее, файлы примерно вдвое меньше", "Без потерь: очень большие файлы, только для особых случаев" });
            _quality.SelectedIndex = (int)settings.Quality;
            _cursor = Check("Показывать курсор мыши в записи", settings.ShowCursor);
            AddField(video, "Плавность", _fps);
            AddField(video, "Качество картинки", _quality);
            video.Controls.Add(_cursor);
            if (recording)
            {
                _fps.Enabled = false;
                _quality.Enabled = false;
                _cursor.Enabled = false;
                video.Controls.Add(Note("Видео можно настроить, когда запись остановлена."));
            }
            stack.Controls.Add(video);

            // --- Звук ---
            var audio = Section("Звук", width);
            audio.Controls.Add(Note("В файле всегда четыре звуковые дорожки: общая, «Микрофон 1», «Микрофон 2» и «Звук собеседников». Здесь можно выбрать, что попадает в общую:"));
            _mixMic1 = Check("Микрофон 1", settings.MixMic1);
            _mixMic2 = Check("Микрофон 2", settings.MixMic2);
            _mixSystem = Check("Звук собеседников", settings.MixSystem);
            audio.Controls.Add(_mixMic1);
            audio.Controls.Add(_mixMic2);
            audio.Controls.Add(_mixSystem);
            _offset = new NumericUpDown { Minimum = -500, Maximum = 1000, Increment = 10, Value = settings.AudioOffsetMs, Width = Theme.S(100) };
            AddField(audio, "Сдвиг звука относительно картинки, мс (если губы не совпадают со звуком; обычно 70)", _offset);
            stack.Controls.Add(audio);

            // --- Проверка ---
            var check = Section("Проверка при запуске программы", width);
            _startupSilent = Radio("Тихая: ничего не звучит", settings.StartupCheckMode == CheckMode.Silent);
            _startupAudible = Radio("Со звуком: звучит короткая мелодия, и программа проверяет, что её слышат микрофоны", settings.StartupCheckMode == CheckMode.Audible);
            check.Controls.Add(_startupSilent);
            check.Controls.Add(_startupAudible);
            stack.Controls.Add(check);

            // --- Удобство ---
            var comfort = Section("Удобство", width);
            _confirmStop = Check("Спрашивать подтверждение перед остановкой записи", settings.ConfirmStop);
            _hotkeys = Check("Горячие клавиши: Ctrl+Alt+R начинает или останавливает запись, Ctrl+Alt+P ставит паузу", settings.HotkeysEnabled);
            _autostart = Check("Запускать программу вместе с Windows", settings.Autostart);
            _autostartMinimized = Check("При автозапуске сразу сворачивать окно", settings.StartMinimizedOnAutostart);
            _keepMkv = Check("Сохранять исходный MKV рядом с MP4 (рекомендуется)", settings.KeepMkvAfterConvert);
            comfort.Controls.Add(_confirmStop);
            comfort.Controls.Add(_hotkeys);
            comfort.Controls.Add(_autostart);
            comfort.Controls.Add(_autostartMinimized);
            comfort.Controls.Add(_keepMkv);
            comfort.Controls.Add(Note("MKV это исходная запись, она переживает даже внезапное выключение. После остановки из неё делается привычный MP4, и в папке с датой лежат оба файла. Если галочку снять, MKV будет удаляться, как только MP4 проверен и совпал по длительности: места занято вдвое меньше."));
            stack.Controls.Add(comfort);

            // --- FFmpeg ---
            var ff = Section("Модуль записи FFmpeg", width);
            _ffmpegStatus = new Label { AutoSize = false, Width = width - Theme.S(32), Height = Theme.S(40), ForeColor = Theme.Muted, UseMnemonic = false };
            ff.Controls.Add(_ffmpegStatus);
            var ffButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            var choose = new ModernButton("Указать ffmpeg.exe…", ButtonKind.Secondary) { Size = new Size(Theme.S(190), Theme.S(34)), Margin = new Padding(0, 0, Theme.S(8), 0) };
            choose.Click += (s, e) => ChooseFfmpeg();
            var download = new ModernButton("Где скачать", ButtonKind.Secondary) { Size = new Size(Theme.S(140), Theme.S(34)), Margin = new Padding(0, 0, Theme.S(8), 0) };
            download.Click += (s, e) => Shell.OpenUrl(Ffmpeg.DownloadPage);
            var openApp = new ModernButton("Папка программы", ButtonKind.Secondary) { Size = new Size(Theme.S(160), Theme.S(34)), Margin = new Padding(0) };
            openApp.Click += (s, e) => Shell.OpenFolder(AppPaths.ExeDirectory);
            ffButtons.Controls.Add(choose);
            ffButtons.Controls.Add(download);
            ffButtons.Controls.Add(openApp);
            ff.Controls.Add(ffButtons);
            ff.Controls.Add(Note("Нужен файл ffmpeg.exe из архива ffmpeg-release-essentials.zip (сайт gyan.dev, папка bin). Проще всего положить его рядом с MasterVKS.exe."));
            stack.Controls.Add(ff);

            // --- Журнал ---
            var logs = Section("Журнал работы", width);
            logs.Controls.Add(Note("Программа подробно записывает, что происходило. Если что-то пойдёт не так, журнал поможет разобраться."));
            var openLog = new ModernButton("Открыть папку журнала", ButtonKind.Secondary) { Size = new Size(Theme.S(220), Theme.S(34)) };
            openLog.Click += (s, e) => Shell.OpenFolder(AppPaths.LogDirectory);
            logs.Controls.Add(openLog);
            stack.Controls.Add(logs);

            scroll.Controls.Add(stack);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(60), BackColor = Theme.Background };
            var ok = new ModernButton("Сохранить", ButtonKind.Primary) { Size = new Size(Theme.S(140), Theme.S(38)), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            var cancel = new ModernButton("Отмена", ButtonKind.Secondary) { Size = new Size(Theme.S(120), Theme.S(38)), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            ok.Click += (s, e) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            cancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            bottom.Resize += (s, e) =>
            {
                ok.Location = new Point(bottom.Width - Theme.S(16) - ok.Width, Theme.S(10));
                cancel.Location = new Point(ok.Left - Theme.S(10) - cancel.Width, Theme.S(10));
            };

            Controls.Add(scroll);
            Controls.Add(bottom);
            UpdateFfmpegStatus();
        }

        public void ApplyTo(AppSettings s)
        {
            if (_fps.Enabled)
            {
                s.FrameRate = new[] { 15, 25, 30, 60 }[Math.Max(0, _fps.SelectedIndex)];
                s.Quality = (VideoQuality)Math.Max(0, _quality.SelectedIndex);
                s.ShowCursor = _cursor.Checked;
            }
            s.MixMic1 = _mixMic1.Checked;
            s.MixMic2 = _mixMic2.Checked;
            s.MixSystem = _mixSystem.Checked;
            s.AudioOffsetMs = (int)_offset.Value;
            s.StartupCheckMode = _startupAudible.Checked ? CheckMode.Audible : CheckMode.Silent;
            s.ConfirmStop = _confirmStop.Checked;
            s.HotkeysEnabled = _hotkeys.Checked;
            s.Autostart = _autostart.Checked;
            s.StartMinimizedOnAutostart = _autostartMinimized.Checked;
            s.KeepMkvAfterConvert = _keepMkv.Checked;
            s.FfmpegPath = _ffmpegPath;
        }

        private void ChooseFfmpeg()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Где лежит ffmpeg.exe";
                dialog.Filter = "ffmpeg.exe|ffmpeg.exe|Программы (*.exe)|*.exe";
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _ffmpegPath = dialog.FileName;
                UpdateFfmpegStatus();
            }
        }

        private void UpdateFfmpegStatus()
        {
            string found = Ffmpeg.Locate(_ffmpegPath);
            if (found != null)
            {
                _ffmpegStatus.ForeColor = Theme.Green;
                _ffmpegStatus.Text = "Найден: " + found;
            }
            else
            {
                _ffmpegStatus.ForeColor = Theme.Red;
                _ffmpegStatus.Text = "Не найден. Без него записать видео не получится.";
            }
        }

        // ---------- Помощники разметки ----------

        private static FlowLayoutPanel Section(string title, int width)
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Width = width,
                MinimumSize = new Size(width, 0),
                MaximumSize = new Size(width, 0),
                BackColor = Color.White,
                Padding = new Padding(Theme.S(16), Theme.S(10), Theme.S(16), Theme.S(12)),
                Margin = new Padding(0, 0, 0, Theme.S(12))
            };
            panel.Controls.Add(new Label { Text = title, Font = Theme.CardTitle, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, Theme.S(6)), UseMnemonic = false });
            return panel;
        }

        private static void AddField(FlowLayoutPanel section, string label, Control control)
        {
            section.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                MaximumSize = new Size(section.MaximumSize.Width - Theme.S(32), 0),
                ForeColor = Theme.Muted,
                Font = Theme.Small,
                Margin = new Padding(0, Theme.S(6), 0, Theme.S(2)),
                UseMnemonic = false
            });
            if (control is ComboBox) control.Width = section.MaximumSize.Width - Theme.S(32);
            section.Controls.Add(control);
        }

        private static ComboBox Combo(object[] items)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            box.Items.AddRange(items);
            return box;
        }

        private static CheckBox Check(string text, bool value)
        {
            return new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, Theme.S(4), 0, Theme.S(2)), UseMnemonic = false };
        }

        private static RadioButton Radio(string text, bool value)
        {
            return new RadioButton { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, Theme.S(4), 0, Theme.S(2)), UseMnemonic = false };
        }

        private static Label Note(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(Theme.S(590), 0),
                ForeColor = Theme.Muted,
                Font = Theme.Small,
                Margin = new Padding(0, Theme.S(4), 0, Theme.S(6)),
                UseMnemonic = false
            };
        }
    }

    internal sealed class AboutForm : Form
    {
        private readonly Bitmap _logo;

        public AboutForm()
        {
            Text = "О программе";
            Font = Theme.Base;
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(460), Theme.S(300));
            _logo = IconFactory.Render(Theme.S(64), AppIconState.Plain);
            DialogKeys.CloseOnEscape(this);

            var logo = new Panel { Location = new Point(Theme.S(24), Theme.S(24)), Size = new Size(Theme.S(64), Theme.S(64)) };
            logo.Paint += (s, e) => e.Graphics.DrawImage(_logo, 0, 0, _logo.Width, _logo.Height);
            Version v = typeof(AboutForm).Assembly.GetName().Version;
            var title = new Label { Text = "МастерВКС", Font = Theme.AppTitle, AutoSize = true, Location = new Point(Theme.S(104), Theme.S(26)), UseMnemonic = false };
            var version = new Label { Text = "Версия " + v.Major + "." + v.Minor + "." + v.Build, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(Theme.S(106), Theme.S(60)), UseMnemonic = false };
            var body = new Label
            {
                Text = "Надёжная запись видеоконференций: экран, два микрофона и голоса собеседников.\n\n" +
                       "Разработчик: Ходаковский Александр\n\n" +
                       "Видео записывается с помощью FFmpeg (ffmpeg.org), звук берётся через библиотеку NAudio.",
                AutoSize = false,
                Location = new Point(Theme.S(24), Theme.S(108)),
                Size = new Size(Theme.S(412), Theme.S(120)),
                UseMnemonic = false
            };
            var ok = new ModernButton("Понятно", ButtonKind.Primary) { Size = new Size(Theme.S(120), Theme.S(38)), Location = new Point(Theme.S(316), Theme.S(240)) };
            ok.Click += (s, e) => Close();
            Controls.Add(logo);
            Controls.Add(title);
            Controls.Add(version);
            Controls.Add(body);
            Controls.Add(ok);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _logo.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class EventsForm : Form
    {
        public EventsForm(System.Collections.Generic.IList<Notice> events)
        {
            Text = "События · МастерВКС";
            Font = Theme.Base;
            BackColor = Theme.Background;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(760), Theme.S(460));
            MinimizeBox = false;
            DialogKeys.CloseOnEscape(this);

            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                ShowItemToolTips = true
            };
            list.Columns.Add("Время", Theme.S(80));
            list.Columns.Add("Что произошло", Theme.S(650));
            for (int i = events.Count - 1; i >= 0; i--)
            {
                Notice n = events[i];
                var item = new ListViewItem(n.Time.ToString("HH:mm:ss")) { ToolTipText = n.Text };
                item.SubItems.Add(n.Text);
                switch (n.Level)
                {
                    case NoticeLevel.Error:
                        item.ForeColor = Theme.Red;
                        break;
                    case NoticeLevel.Warning:
                        item.ForeColor = Theme.Yellow;
                        break;
                    case NoticeLevel.Success:
                        item.ForeColor = Theme.Green;
                        break;
                }
                list.Items.Add(item);
            }

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(56), BackColor = Theme.Background };
            var openLog = new ModernButton("Открыть подробный журнал", ButtonKind.Secondary, Glyph.Folder) { Size = new Size(Theme.S(250), Theme.S(36)), Location = new Point(Theme.S(12), Theme.S(10)) };
            openLog.Click += (s, e) => Shell.OpenFolder(AppPaths.LogDirectory);
            var close = new ModernButton("Закрыть", ButtonKind.Primary) { Size = new Size(Theme.S(120), Theme.S(36)), Anchor = AnchorStyles.Right | AnchorStyles.Top };
            close.Click += (s, e) => Close();
            bottom.Controls.Add(openLog);
            bottom.Controls.Add(close);
            bottom.Resize += (s, e) => close.Location = new Point(bottom.Width - Theme.S(12) - close.Width, Theme.S(10));
            list.Resize += (s, e) =>
            {
                if (list.Columns.Count == 2) list.Columns[1].Width = Math.Max(Theme.S(200), list.ClientSize.Width - list.Columns[0].Width - 4);
            };

            Controls.Add(list);
            Controls.Add(bottom);
        }
    }

    internal static class DialogKeys
    {
        /// <summary>Esc закрывает окно (как «Отмена»).</summary>
        public static void CloseOnEscape(Form form)
        {
            form.KeyPreview = true;
            form.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            };
        }
    }
}
