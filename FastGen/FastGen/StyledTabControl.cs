using System.Drawing;
using System.Windows.Forms;

namespace FastGen
{
    /// <summary>
    /// Вкладки. Классическое оформление — синие «плитки» (как было);
    /// новые темы — плоская полоса с подчёркиванием активной вкладки, рисуется целиком сама.
    /// </summary>
    public class StyledTabControl : TabControl
    {
        private static readonly Color ClassicActiveBack = Color.FromArgb(33, 150, 243);
        private static readonly Color ClassicInactiveBack = Color.FromArgb(224, 236, 248);
        private static readonly Color ClassicInactiveFore = Color.FromArgb(60, 60, 60);

        private Font _tabFont = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point);
        private Theme _theme = Theme.Classic;

        public StyledTabControl()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            ItemSize = new Size(180, 40);
            SizeMode = TabSizeMode.Fixed;
        }

        public void ApplyTheme(Theme theme)
        {
            _theme = theme;
            bool custom = !theme.IsClassic;

            var old = _tabFont;
            _tabFont = new Font(custom ? theme.UiFontFamily : "Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point);
            old.Dispose();

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, custom);
            UpdateStyles();
            ItemSize = custom ? new Size(200, 42) : new Size(180, 40);
            Invalidate();
        }

        // ---- классический вид ----
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            base.OnDrawItem(e);
            if (!_theme.IsClassic) return;

            var g = e.Graphics;
            var rect = GetTabRect(e.Index);
            bool selected = e.Index == SelectedIndex;

            using (var b = new SolidBrush(selected ? ClassicActiveBack : ClassicInactiveBack))
                g.FillRectangle(b, rect);

            TextRenderer.DrawText(g, TabPages[e.Index].Text, _tabFont, rect,
                selected ? Color.White : ClassicInactiveFore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // ---- новые темы ----
        protected override void OnPaint(PaintEventArgs e)
        {
            if (_theme.IsClassic)
            {
                base.OnPaint(e);
                return;
            }

            var g = e.Graphics;
            using (var back = new SolidBrush(_theme.Window))
                g.FillRectangle(back, ClientRectangle);

            int stripHeight = ItemSize.Height + 4;
            using (var strip = new SolidBrush(_theme.Header))
                g.FillRectangle(strip, 0, 0, Width, stripHeight);
            using (var line = new Pen(_theme.Border))
                g.DrawLine(line, 0, stripHeight - 1, Width, stripHeight - 1);

            for (int i = 0; i < TabCount; i++)
            {
                var rect = GetTabRect(i);
                bool selected = i == SelectedIndex;

                TextRenderer.DrawText(g, TabPages[i].Text, _tabFont, rect,
                    selected ? _theme.Accent : _theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                if (selected)
                    using (var accent = new SolidBrush(_theme.Accent))
                        g.FillRectangle(accent, rect.Left + 12, stripHeight - 4, rect.Width - 24, 3);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tabFont.Dispose();
            base.Dispose(disposing);
        }
    }
}
