using System;
using System.Drawing;
using System.Windows.Forms;
using MasterVks.Checks;
using MasterVks.Core;
using MasterVks.Recording;

namespace MasterVks.UI
{
    internal sealed partial class MainForm
    {
        private const string Developer = "Ходаковский Александр";
        private const string WindowTitleBase = "МастерВКС · Разработчик: " + Developer;

        private Panel _header;
        private Bitmap _logo;

        private void BuildUi()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Base;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Text = WindowTitleBase;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(1000), Theme.S(640));
            MinimumSize = new Size(Theme.S(900), Theme.S(600));
            DoubleBuffered = true;
            KeyPreview = true;
            _tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(Theme.S(16), Theme.S(10), Theme.S(16), Theme.S(8)),
                ColumnCount = 2,
                RowCount = 4,
                BackColor = Theme.Background,
                AutoScroll = true
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(420)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(56)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(134)));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(36)));

            _header = BuildHeader();
            root.Controls.Add(_header, 0, 0);
            root.SetColumnSpan(_header, 2);

            CardPanel sources = BuildSourcesCard();
            root.Controls.Add(sources, 0, 1);

            CardPanel checks = BuildCheckCard();
            root.Controls.Add(checks, 1, 1);

            CardPanel record = BuildRecordCard();
            root.Controls.Add(record, 0, 2);
            root.SetColumnSpan(record, 2);

            Panel notices = BuildNoticeBar();
            root.Controls.Add(notices, 0, 3);
            root.SetColumnSpan(notices, 2);

            Controls.Add(root);
            ResumeLayout(true);
        }

        // ---------- Шапка ----------

        private Panel BuildHeader()
        {
            var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = new Padding(Theme.S(4), 0, Theme.S(4), Theme.S(6)) };
            _logo = IconFactory.Render(Theme.S(42), AppIconState.Plain);
            var logo = new Panel { Location = new Point(0, Theme.S(4)), Size = new Size(Theme.S(42), Theme.S(42)), BackColor = Theme.Background };
            logo.Paint += (s, e) => e.Graphics.DrawImage(_logo, 0, 0, _logo.Width, _logo.Height);

            var title = new Label
            {
                Text = "МастерВКС",
                Font = Theme.AppTitle,
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(Theme.S(54), 0),
                BackColor = Theme.Background,
                UseMnemonic = false
            };
            var subtitle = new Label
            {
                Text = "Разработчик: " + Developer,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Location = new Point(Theme.S(56), Theme.S(29)),
                BackColor = Theme.Background,
                UseMnemonic = false
            };

            _pill = new StatusPill { Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Theme.Background };
            _pill.SetState("Запускаюсь…", Theme.Gray, Theme.GraySoft, Theme.Text, false);

            _settingsButton = new ModernButton("Настройки", ButtonKind.Secondary, Glyph.Gear) { Size = new Size(Theme.S(124), Theme.S(36)), BackColor = Theme.Background };
            _settingsButton.Click += (s, e) => OpenSettings();
            _aboutButton = new ModernButton("", ButtonKind.Secondary, Glyph.Info) { Size = new Size(Theme.S(36), Theme.S(36)), BackColor = Theme.Background };
            _aboutButton.Click += (s, e) => OpenAbout();
            _tips.SetToolTip(_aboutButton, "О программе");

            header.Controls.Add(logo);
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(_pill);
            header.Controls.Add(_settingsButton);
            header.Controls.Add(_aboutButton);
            header.Resize += (s, e) => LayoutHeader();
            return header;
        }

        private void LayoutHeader()
        {
            if (_header == null) return;
            int right = _header.ClientSize.Width;
            int y = Theme.S(6);
            _aboutButton.Location = new Point(right - _aboutButton.Width, y);
            _settingsButton.Location = new Point(_aboutButton.Left - Theme.S(8) - _settingsButton.Width, y);
            _pill.Location = new Point(_settingsButton.Left - Theme.S(14) - _pill.Width, y + (Theme.S(36) - _pill.Height) / 2);
        }

        // ---------- Источники ----------

        private CardPanel BuildSourcesCard()
        {
            var card = new CardPanel("Что записываем") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4)) };
            card.Padding = new Padding(card.Padding.Left, Theme.S(42), card.Padding.Right, Theme.S(8));
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = false };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _screenBox = CreateCombo();
            _screenBox.SelectedIndexChanged += OnScreenChanged;
            _mic1Box = CreateCombo();
            _mic1Box.SelectedIndexChanged += OnMicChanged;
            _mic2Box = CreateCombo();
            _mic2Box.SelectedIndexChanged += OnMicChanged;
            _outBox = CreateCombo();
            _outBox.SelectedIndexChanged += OnOutputChanged;
            _mic1Meter = new SourceMeter { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S(4), 0, 0) };
            _mic2Meter = new SourceMeter { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S(4), 0, 0) };
            _sysMeter = new SourceMeter { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S(4), 0, 0) };

            AddRow(table, new FieldLabel("Экран"));
            AddRow(table, _screenBox);
            AddRow(table, new FieldLabel("Микрофон 1 · например, USB-микрофон"));
            AddRow(table, _mic1Box);
            AddRow(table, _mic1Meter);
            AddRow(table, new FieldLabel("Микрофон 2 · например, микрофон камеры"));
            AddRow(table, _mic2Box);
            AddRow(table, _mic2Meter);
            AddRow(table, new FieldLabel("Звук собеседников · всё, что слышно из динамиков или телевизора"));
            AddRow(table, _outBox);
            AddRow(table, _sysMeter);
            AddRow(table, new FieldLabel("Название ВКС · необязательно, войдёт в имя папки и файла"));
            _nameBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = _settings.MeetingName ?? "",
                MaxLength = RecordingNames.MaxNameLength,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, Theme.S(1), 0, Theme.S(1))
            };
            _nameBox.TextChanged += (s, e) => OnMeetingNameChanged();
            _nameBox.Leave += (s, e) => _settings.Save();
            AddRow(table, _nameBox);
            _tips.SetToolTip(_nameBox, "Например, «Планёрка». Тогда запись попадёт в папку «Планёрка_2026-10-18» и будет называться «Планёрка_2026-10-18_16-57_ВКС.mp4».\nЕсли поле пустое: папка «2026-10-18», файл «2026-10-18_16-57_ВКС.mp4».");

            AddRow(table, new FieldLabel("Папка для записей"));

            var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = Theme.S(32), Margin = new Padding(0, Theme.S(2), 0, 0) };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(104)));
            _folderBox = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Text = _settings.OutputFolder,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, Theme.S(5), Theme.S(8), 0)
            };
            _folderButton = new ModernButton("Выбрать…", ButtonKind.Secondary) { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S(1), 0, 0) };
            _folderButton.Click += (s, e) => ChooseFolder();
            folderRow.Controls.Add(_folderBox, 0, 0);
            folderRow.Controls.Add(_folderButton, 1, 0);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(34)));
            table.Controls.Add(folderRow);
            // Пустая последняя строка забирает лишнее место, иначе Windows растягивает строку с папкой.
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) });
            table.RowCount = table.RowStyles.Count;

            card.Controls.Add(table);
            _tips.SetToolTip(_mic1Box, "Основной микрофон. Звук с него пишется в общую дорожку и отдельно.");
            _tips.SetToolTip(_mic2Box, "Второй микрофон (например, встроенный в камеру). Пишется вместе с первым.");
            _tips.SetToolTip(_outBox, "Отсюда берутся голоса участников конференции. Обычно подходит «Как в Windows»: программа сама переключится, если звук уйдёт на телевизор по HDMI.");
            return card;
        }

        private static ComboBox CreateCombo()
        {
            return new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Font = Theme.Base,
                Margin = new Padding(0),
                IntegralHeight = false,
                MaxDropDownItems = 12
            };
        }

        private static void AddRow(TableLayoutPanel table, Control control)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control);
        }

        // ---------- Проверка ----------

        private CardPanel BuildCheckCard()
        {
            var card = new CardPanel("Проверка") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4)) };

            _checkMode = new SegmentedToggle("Тихая", "Со звуком")
            {
                Size = new Size(Theme.S(204), Theme.S(32)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _checkMode.SelectedIndex = _settings.ManualCheckMode == CheckMode.Audible ? 1 : 0;
            _checkMode.SelectedChanged += (s, e) =>
            {
                _settings.ManualCheckMode = _checkMode.SelectedIndex == 1 ? CheckMode.Audible : CheckMode.Silent;
                _settings.Save();
            };
            _tips.SetToolTip(_checkMode, "«Тихая»: ничего не звучит, звук компьютера проверяется неслышным сигналом.\n«Со звуком»: звучит короткая мелодия, и программа проверяет, что её слышат оба микрофона.");

            _checkButton = new ModernButton("Проверить", ButtonKind.Primary, Glyph.Check)
            {
                Size = new Size(Theme.S(132), Theme.S(32)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _checkButton.Click += (s, e) => RunCheck(_checkMode.SelectedIndex == 1 ? CheckMode.Audible : CheckMode.Silent, false);

            card.Controls.Add(_checkMode);
            card.Controls.Add(_checkButton);
            card.Resize += (s, e) =>
            {
                _checkButton.Location = new Point(card.Width - Theme.S(16) - _checkButton.Width, Theme.S(10));
                _checkMode.Location = new Point(_checkButton.Left - Theme.S(10) - _checkMode.Width, Theme.S(10));
            };

            _checkTime = new Label
            {
                Dock = DockStyle.Bottom,
                Height = Theme.S(20),
                Font = Theme.Small,
                ForeColor = Theme.Faint,
                Text = "Проверка запустится автоматически",
                TextAlign = ContentAlignment.BottomLeft,
                UseMnemonic = false
            };

            var list = new Panel { Dock = DockStyle.Fill };
            var items = SystemCheck.CreateItems();
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var row = new CheckRowView(items[i]) { Dock = DockStyle.Top };
                row.Click += (s, e) => ShowCheckDetails(((CheckRowView)s).Item);
                _rows[items[i].Key] = row;
                list.Controls.Add(row);
            }
            card.Controls.Add(list);
            card.Controls.Add(_checkTime);
            return card;
        }

        private void SetRowItem(CheckRowView row, CheckItem item)
        {
            row.Item = item;
            string tip = item.Summary + (string.IsNullOrEmpty(item.Details) ? "" : "\n\n" + item.Details);
            if (item.Status != CheckStatus.Running && item.Status != CheckStatus.Pending) tip += "\n\nНажмите, чтобы открыть подробности.";
            _tips.SetToolTip(row, WrapTip(tip));
        }

        private static string WrapTip(string text)
        {
            const int width = 80;
            var sb = new System.Text.StringBuilder();
            foreach (string paragraph in text.Split('\n'))
            {
                int col = 0;
                foreach (string word in paragraph.Split(' '))
                {
                    if (col > 0 && col + word.Length > width)
                    {
                        sb.Append('\n');
                        col = 0;
                    }
                    else if (col > 0)
                    {
                        sb.Append(' ');
                        col++;
                    }
                    sb.Append(word);
                    col += word.Length;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        // ---------- Запись ----------

        private CardPanel BuildRecordCard()
        {
            var card = new CardPanel(null) { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4)) };
            card.Padding = new Padding(Theme.S(14));
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(196)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(392)));

            _preview = new ScreenPreview { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(16), 0) };
            _tips.SetToolTip(_preview, "Так сейчас выглядит записываемый экран");
            table.Controls.Add(_preview, 0, 0);

            var info = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            info.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(52)));
            info.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(24)));
            info.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _timerLabel = new Label { Dock = DockStyle.Fill, Font = Theme.Timer, ForeColor = Theme.Text, Text = "00:00:00", TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            _recordInfo = new Label { Dock = DockStyle.Fill, Font = Theme.Base, ForeColor = Theme.Muted, Text = "Запись не идёт", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
            _hintLabel = new Label { Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.Faint, Text = "", TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true, UseMnemonic = false };
            info.Controls.Add(_timerLabel, 0, 0);
            info.Controls.Add(_recordInfo, 0, 1);
            info.Controls.Add(_hintLabel, 0, 2);
            table.Controls.Add(info, 1, 0);

            var buttons = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _recordButton = new ModernButton("Начать запись", ButtonKind.Danger, Glyph.Record)
            {
                Size = new Size(Theme.S(232), Theme.S(52)),
                Font = Theme.BigButtonFont,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _recordButton.Click += (s, e) => ToggleRecording(true);
            _pauseButton = new ModernButton("Пауза", ButtonKind.Secondary, Glyph.Pause)
            {
                Size = new Size(Theme.S(140), Theme.S(52)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Enabled = false
            };
            _pauseButton.Click += (s, e) => TogglePause();
            _openFolderButton = new ModernButton("Открыть папку с записями", ButtonKind.Ghost, Glyph.Folder)
            {
                Size = new Size(Theme.S(250), Theme.S(32)),
                Font = Theme.Base,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _openFolderButton.Click += (s, e) => Shell.OpenFolder(_settings.OutputFolder);
            buttons.Controls.Add(_recordButton);
            buttons.Controls.Add(_pauseButton);
            buttons.Controls.Add(_openFolderButton);
            buttons.Resize += (s, e) =>
            {
                int w = buttons.ClientSize.Width;
                _recordButton.Location = new Point(w - _recordButton.Width, Theme.S(4));
                _pauseButton.Location = new Point(_recordButton.Left - Theme.S(12) - _pauseButton.Width, Theme.S(4));
                _openFolderButton.Location = new Point(w - _openFolderButton.Width, _recordButton.Bottom + Theme.S(8));
            };
            table.Controls.Add(buttons, 2, 0);

            card.Controls.Add(table);
            return card;
        }

        // ---------- Лента событий ----------

        private Panel BuildNoticeBar()
        {
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = new Padding(Theme.S(6), Theme.S(2), Theme.S(4), 0) };
            _noticeLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                UseMnemonic = false,
                BackColor = Theme.Background,
                Cursor = Cursors.Hand
            };
            _noticeLabel.Click += (s, e) => OpenEvents();
            _eventsButton = new ModernButton("Все события", ButtonKind.Ghost) { Dock = DockStyle.Right, Width = Theme.S(120), Font = Theme.Base, BackColor = Theme.Background };
            _eventsButton.Click += (s, e) => OpenEvents();
            _showFileButton = new ModernButton("Показать файл", ButtonKind.Ghost, Glyph.Folder) { Dock = DockStyle.Right, Width = Theme.S(150), Font = Theme.Base, BackColor = Theme.Background, Visible = false };
            _showFileButton.Click += (s, e) =>
            {
                if (_lastNoticeFile != null) Shell.SelectFile(_lastNoticeFile);
            };
            bar.Controls.Add(_noticeLabel);
            bar.Controls.Add(_showFileButton);
            bar.Controls.Add(_eventsButton);
            return bar;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_logo != null) _logo.Dispose();
                if (_currentIcon != null) _currentIcon.Dispose();
                if (_tips != null) _tips.Dispose();
                if (_fastTimer != null) _fastTimer.Dispose();
                if (_slowTimer != null) _slowTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
