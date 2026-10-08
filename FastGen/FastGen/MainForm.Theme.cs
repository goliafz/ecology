using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FastGen
{
    /// <summary>
    /// Оформление: переключатель «Классическое / Светлое / Тёмное» в верхней полосе окна.
    /// Классический вид не описан заново, а восстанавливается из снимка, сделанного при запуске,
    /// поэтому он в точности такой, как до появления тем.
    /// </summary>
    public partial class MainForm
    {
        private Theme _theme = Theme.Classic;
        private Panel _header;
        private ComboBox cmbTheme;

        private enum ButtonRole { Secondary, Primary, Accent, Danger, Neutral }

        private sealed class Snapshot
        {
            public Color BackColor, ForeColor;
            public Font Font;
            public Padding Padding;
            public BorderStyle? Border;
            public FlatStyle? Flat;
            public int FlatBorderSize;
            public Color FlatBorderColor, FlatOverColor;
            public bool? UseVisualStyleBackColor;
            public Cursor Cursor;
            public bool HandleHooked;
        }

        private readonly Dictionary<Control, Snapshot> _snapshots = new Dictionary<Control, Snapshot>();

        // ------------------------------------------------------------------
        // Верхняя полоса
        // ------------------------------------------------------------------

        private Panel BuildHeader()
        {
            _header = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 0, 8, 0) };
            _header.Paint += (s, e) =>
            {
                if (_theme.IsClassic) return;
                using (var pen = new Pen(_theme.Border))
                    e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
            };

            var title = new Label
            {
                Text = "FastGen",
                AutoSize = true,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                Padding = new Padding(0, 9, 0, 0)
            };

            var right = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 6, 0, 0)
            };

            var lblSize = new Label { Text = "Шрифт текста:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            var btnSmaller = MakeButton("A−", (s, e) => ChangeEditorFontSize(-1));
            var btnBigger = MakeButton("A+", (s, e) => ChangeEditorFontSize(+1));
            var tips = new ToolTip();
            tips.SetToolTip(btnSmaller, "Мельче (Ctrl+−)");
            tips.SetToolTip(btnBigger, "Крупнее (Ctrl++);  Ctrl+0 — как было");

            var lblTheme = new Label { Text = "Оформление:", AutoSize = true, Margin = new Padding(14, 6, 4, 0) };
            cmbTheme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(0, 2, 0, 0) };
            cmbTheme.Items.AddRange(Theme.All);
            cmbTheme.SelectedIndexChanged += (s, e) =>
            {
                var t = cmbTheme.SelectedItem as Theme;
                if (t != null && t != _theme) ApplyTheme(t);
            };
            tips.SetToolTip(cmbTheme, "«Классическое» — прежний вид программы");

            right.Controls.AddRange(new Control[] { lblSize, btnSmaller, btnBigger, lblTheme, cmbTheme });
            _header.Controls.Add(right);
            _header.Controls.Add(title);
            return _header;
        }

        /// <summary>Редактор внутри панели: в новых темах — поля вокруг текста и тонкая рамка.</summary>
        private Panel HostEditor(RichTextBox box)
        {
            var host = new Panel { Dock = DockStyle.Fill, Margin = box.Margin, Tag = "editorHost" };
            box.Dock = DockStyle.Fill;
            host.Controls.Add(box);
            host.Paint += (s, e) =>
            {
                if (_theme.IsClassic) return;
                using (var pen = new Pen(_theme.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, host.Width - 1, host.Height - 1);
            };
            return host;
        }

        // ------------------------------------------------------------------
        // Применение темы
        // ------------------------------------------------------------------

        private void ApplyTheme(Theme theme)
        {
            _theme = theme;
            _settings.Set("Theme", theme.Key);

            SuspendLayout();
            try
            {
                ApplyThemeTree(this);
                tabControl.ApplyTheme(theme);
                ApplyEditorFonts();
                ApplyListRowHeight();
                TryWinApi(() => SetDarkTitleBar(Handle, theme.IsDark));
            }
            finally
            {
                ResumeLayout(true);
            }

            if (cmbTheme.SelectedItem != theme) cmbTheme.SelectedItem = theme;

            // перекрасить то, что раскрашивается кодом
            _lvBoldFont = null;
            _lvStrikeFont = null;
            RecolorVariantItems();
            UpdateTemplateInfo();
            UpdateReproStatusLabel();
            SetDictStatus(_dictStatusText, _dictStatusKind);
            SetContextStatus(_contextStatusText, _contextStatusKind);
            HighlightSynConstructions(txtReproEditor);
            if (txtEditorResult.TextLength > 0) HighlightSynConstructions(txtEditorResult);
            Invalidate(true);
        }

        /// <summary>
        /// Сначала снимок всего дерева, потом перекраска: иначе дочерние элементы запомнили бы
        /// уже перекрашенный цвет и шрифт родителя (они наследуются) вместо исходных.
        /// </summary>
        private void ApplyThemeTree(Control root)
        {
            SnapshotTree(root);
            ApplyThemeRecursive(root);
        }

        private void SnapshotTree(Control root)
        {
            TakeSnapshot(root);
            foreach (Control child in root.Controls)
                SnapshotTree(child);
        }

        private void ApplyThemeRecursive(Control root)
        {
            ApplyThemeTo(root);
            foreach (Control child in root.Controls)
                ApplyThemeRecursive(child);
        }

        private void ForgetSnapshots(Control root)
        {
            _snapshots.Remove(root);
            foreach (Control child in root.Controls) ForgetSnapshots(child);
        }

        private Snapshot TakeSnapshot(Control c)
        {
            if (_snapshots.TryGetValue(c, out var snap)) return snap;

            snap = new Snapshot
            {
                BackColor = c.BackColor,
                ForeColor = c.ForeColor,
                Font = c.Font,
                Padding = c.Padding,
                Cursor = c.Cursor
            };
            var b = c as Button;
            if (b != null)
            {
                snap.Flat = b.FlatStyle;
                snap.FlatBorderSize = b.FlatAppearance.BorderSize;
                snap.FlatBorderColor = b.FlatAppearance.BorderColor;
                snap.FlatOverColor = b.FlatAppearance.MouseOverBackColor;
                snap.UseVisualStyleBackColor = b.UseVisualStyleBackColor;
            }
            var tp = c as TabPage;
            if (tp != null) snap.UseVisualStyleBackColor = tp.UseVisualStyleBackColor;
            if (c is TextBoxBase) snap.Border = ((TextBoxBase)c).BorderStyle;
            else if (c is ListView) snap.Border = ((ListView)c).BorderStyle;
            else if (c is Panel && !(c is TableLayoutPanel) && !(c is FlowLayoutPanel)) snap.Border = ((Panel)c).BorderStyle;
            var cb = c as ComboBox;
            if (cb != null) snap.Flat = cb.FlatStyle;

            _snapshots[c] = snap;
            return snap;
        }

        private void Restore(Control c, Snapshot s)
        {
            c.BackColor = s.BackColor;
            c.ForeColor = s.ForeColor;
            c.Font = s.Font;
            c.Padding = s.Padding;
            c.Cursor = s.Cursor;

            var b = c as Button;
            if (b != null)
            {
                b.FlatStyle = s.Flat ?? FlatStyle.Standard;
                b.FlatAppearance.BorderSize = s.FlatBorderSize;
                b.FlatAppearance.BorderColor = s.FlatBorderColor;
                b.FlatAppearance.MouseOverBackColor = s.FlatOverColor;
                if (s.UseVisualStyleBackColor == true) b.UseVisualStyleBackColor = true;
            }
            var tp = c as TabPage;
            if (tp != null && s.UseVisualStyleBackColor.HasValue) tp.UseVisualStyleBackColor = s.UseVisualStyleBackColor.Value;
            if (s.Border.HasValue)
            {
                if (c is TextBoxBase) ((TextBoxBase)c).BorderStyle = s.Border.Value;
                else if (c is ListView) ((ListView)c).BorderStyle = s.Border.Value;
                else if (c is Panel) ((Panel)c).BorderStyle = s.Border.Value;
            }
            var cb = c as ComboBox;
            if (cb != null && s.Flat.HasValue) cb.FlatStyle = s.Flat.Value;
        }

        private void ApplyThemeTo(Control c)
        {
            var snap = TakeSnapshot(c);
            HookNativeTheme(c, snap);

            if (_theme.IsClassic)
            {
                Restore(c, snap);
                return;
            }

            var t = _theme;
            Font uiFont = snap.Font != null && snap.Font.FontFamily.Name == "Segoe UI"
                ? CachedFont(t.UiFontFamily, snap.Font.SizeInPoints + 0.5F, snap.Font.Style)
                : snap.Font;

            if (c is Button)
            {
                StyleButton((Button)c, t);
                c.Font = uiFont;
                return;
            }

            if (c is RichTextBox)
            {
                var rtb = (RichTextBox)c;
                rtb.BorderStyle = BorderStyle.None;
                rtb.BackColor = t.Card;
                rtb.ForeColor = t.Palette != null ? FromRgb(t.Palette.Text) : t.Text;
                return; // шрифт — в ApplyEditorFonts
            }

            if (c is TextBoxBase || c is NumericUpDown || c is ComboBox)
            {
                c.BackColor = t.Input;
                c.ForeColor = t.Text;
                c.Font = uiFont;
                if (c is TextBoxBase) ((TextBoxBase)c).BorderStyle = BorderStyle.FixedSingle;
                if (c is ComboBox) ((ComboBox)c).FlatStyle = FlatStyle.Flat;
                return;
            }

            if (c is ListView)
            {
                var lv = (ListView)c;
                lv.BackColor = t.Card;
                lv.ForeColor = t.Text;
                lv.BorderStyle = BorderStyle.FixedSingle;
                lv.Font = CachedFont(t.UiFontFamily, 11F, FontStyle.Regular);
                return;
            }

            if ("editorHost".Equals(c.Tag))
            {
                c.BackColor = t.Card;
                c.Padding = new Padding(t.EditorPadding, t.EditorPadding - 4, 4, 4);
                return;
            }

            if (c == _header || (c.Parent == _header) || (c.Parent != null && c.Parent.Parent == _header))
            {
                c.BackColor = t.Header;
                c.ForeColor = t.Text;
                c.Font = uiFont;
                return;
            }

            var tabPage = c as TabPage;
            if (tabPage != null) tabPage.UseVisualStyleBackColor = false;

            c.BackColor = c is Label || c is CheckBox || c is RadioButton ? Color.Transparent : t.Window;
            c.ForeColor = "muted".Equals(c.Tag) ? t.Muted : t.Text;
            if (!(c is Form)) c.Font = uiFont;
        }

        private readonly Dictionary<string, Font> _fontCache = new Dictionary<string, Font>();

        /// <summary>Один объект Font на сочетание — чтобы переключение тем не плодило шрифты.</summary>
        private Font CachedFont(string family, float size, FontStyle style)
        {
            string key = family + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)style;
            if (!_fontCache.TryGetValue(key, out var f))
            {
                f = new Font(family, size, style);
                _fontCache[key] = f;
            }
            return f;
        }

        private static ButtonRole RoleOf(Button b)
        {
            return b.Tag is ButtonRole ? (ButtonRole)b.Tag : ButtonRole.Secondary;
        }

        private static ButtonRole RoleFromClassicColor(Color? back)
        {
            if (back == null) return ButtonRole.Secondary;
            var c = back.Value;
            if (c.ToArgb() == Color.Red.ToArgb()) return ButtonRole.Danger;
            if (c.ToArgb() == Color.DimGray.ToArgb()) return ButtonRole.Neutral;
            if (c.ToArgb() == Color.RoyalBlue.ToArgb() || c.ToArgb() == Color.SteelBlue.ToArgb()) return ButtonRole.Accent;
            return ButtonRole.Primary;
        }

        private static void StyleButton(Button b, Theme t)
        {
            Color back, hover, fore;
            switch (RoleOf(b))
            {
                case ButtonRole.Primary: back = t.Primary; hover = t.PrimaryHover; fore = Color.White; break;
                case ButtonRole.Accent: back = t.Accent; hover = t.AccentHover; fore = Color.White; break;
                case ButtonRole.Danger: back = t.Danger; hover = t.DangerHover; fore = Color.White; break;
                case ButtonRole.Neutral: back = t.Neutral; hover = t.NeutralHover; fore = Color.White; break;
                default: back = t.Secondary; hover = t.SecondaryHover; fore = t.SecondaryText; break;
            }

            b.UseVisualStyleBackColor = false;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = back;
            b.ForeColor = fore;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = hover;
            bool secondary = RoleOf(b) == ButtonRole.Secondary;
            b.FlatAppearance.BorderSize = secondary ? 1 : 0;
            b.FlatAppearance.BorderColor = t.Border;
            b.Padding = new Padding(8, 3, 8, 3);
            b.Cursor = Cursors.Hand;
        }

        private static Color FromRgb(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        // ------------------------------------------------------------------
        // Шрифт редактора
        // ------------------------------------------------------------------

        private string FontSizeKey { get { return "EditorFontSize." + _theme.Key; } }

        private float EditorFontSize
        {
            get
            {
                int tenths = _settings.GetInt(FontSizeKey, (int)Math.Round(_theme.EditorFontSize * 10));
                return Math.Max(8F, Math.Min(36F, tenths / 10F));
            }
        }

        private void ApplyEditorFonts()
        {
            var font = CachedFont(_theme.EditorFontFamily, EditorFontSize, FontStyle.Regular);
            foreach (var box in new[] { txtReproEditor, txtEditorSource, txtEditorResult })
                box.Font = font;
        }

        private void ChangeEditorFontSize(int delta)
        {
            float size = delta == 0 ? _theme.EditorFontSize : EditorFontSize + delta;
            size = Math.Max(8F, Math.Min(36F, size));
            _settings.Set(FontSizeKey, (int)Math.Round(size * 10));
            ApplyEditorFonts();
            HighlightSynConstructions(txtReproEditor);
            if (txtEditorResult.TextLength > 0) HighlightSynConstructions(txtEditorResult);
            SetHint("Размер шрифта текста: " + size.ToString("0.#"));
        }

        /// <summary>Высота строк списка вариантов (в новых темах — просторнее).</summary>
        private void ApplyListRowHeight()
        {
            if (_theme.ListRowHeight <= 0)
            {
                lvVariants.SmallImageList = null;
                return;
            }
            lvVariants.SmallImageList = new ImageList { ImageSize = new Size(1, _theme.ListRowHeight) };
        }

        // ------------------------------------------------------------------
        // Цвета, которые раскрашивает код
        // ------------------------------------------------------------------

        private string _dictStatusText = string.Empty;
        private StatusKind _dictStatusKind = StatusKind.Muted;
        private string _contextStatusText = string.Empty;
        private StatusKind _contextStatusKind = StatusKind.Muted;

        private void RecolorVariantItems()
        {
            if (lvVariants == null) return;
            for (int i = 0; i < lvVariants.Items.Count; i++)
            {
                var item = lvVariants.Items[i];
                var cand = item.Tag as Core.SynonymCandidate;
                bool strike = item.Font != null && item.Font.Strikeout;
                bool bold = item.Font != null && item.Font.Bold;

                item.BackColor = lvVariants.BackColor;
                item.ForeColor = lvVariants.ForeColor;
                item.Font = strike ? LvStrikeFont : bold ? LvBoldFont : lvVariants.Font;

                if (i == 0)
                {
                    item.ForeColor = _theme.ListOriginalFore;
                    item.BackColor = _theme.ListOriginalBack;
                }
                else if (cand != null && cand.Source == Core.SynonymSource.Dict) item.ForeColor = _theme.ListDict;
                else if (cand == null || cand.Source == Core.SynonymSource.User) item.ForeColor = cand == null ? lvVariants.ForeColor : _theme.ListUser;
            }
        }

        // ------------------------------------------------------------------
        // WinAPI: тёмный заголовок окна, тёмные полосы прокрутки
        // ------------------------------------------------------------------

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        private static void SetDarkTitleBar(IntPtr handle, bool dark)
        {
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)   // Windows 10 20H1+
                DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));        // ранние сборки Windows 10
        }

        private void HookNativeTheme(Control c, Snapshot snap)
        {
            if (!(c is RichTextBox || c is ListView || c is TextBox || c is ComboBox)) return;

            if (!snap.HandleHooked)
            {
                snap.HandleHooked = true;
                c.HandleCreated += (s, e) => ApplyNativeTheme(c);
            }
            if (c.IsHandleCreated) ApplyNativeTheme(c);
        }

        private void ApplyNativeTheme(Control c)
        {
            string name = _theme.IsClassic ? null : _theme.IsDark ? "DarkMode_Explorer" : "Explorer";
            TryWinApi(() => SetWindowTheme(c.Handle, name, null));
        }
    }
}
