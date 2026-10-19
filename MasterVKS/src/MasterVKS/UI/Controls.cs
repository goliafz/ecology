using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MasterVks.Checks;

namespace MasterVks.UI
{
    /// <summary>Белая карточка со скруглёнными углами и заголовком.</summary>
    internal class CardPanel : Panel
    {
        private string _title;

        public CardPanel(string title)
        {
            _title = title;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
            int top = string.IsNullOrEmpty(title) ? Theme.S(14) : Theme.S(46);
            Padding = new Padding(Theme.S(18), top, Theme.S(18), Theme.S(14));
            ControlAdded += (s, e) => PaintChildrenWhite(e.Control);
        }

        public string Title
        {
            get { return _title; }
            set
            {
                _title = value;
                Invalidate();
            }
        }

        private static void PaintChildrenWhite(Control c)
        {
            if (c is ComboBox || c is TextBox) return;
            c.BackColor = Theme.Card;
            c.ControlAdded += (s, e) => PaintChildrenWhite(e.Control);
            foreach (Control child in c.Controls) PaintChildrenWhite(child);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            Theme.PrepareGraphics(g);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(rect, Theme.Sf(14)))
            using (var fill = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.Border, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (!string.IsNullOrEmpty(_title))
            {
                var titleRect = new Rectangle(Theme.S(18), Theme.S(14), Width - Theme.S(36), Theme.S(24));
                Theme.DrawText(g, _title, Theme.CardTitle, titleRect, Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    internal enum ButtonKind
    {
        Primary,
        Danger,
        Secondary,
        Ghost
    }

    internal enum Glyph
    {
        None,
        Record,
        Stop,
        Pause,
        Play,
        Check,
        Folder,
        Gear,
        Info,
        Refresh
    }

    /// <summary>Современная кнопка: скругления, наведение, нажатие, фокус с клавиатуры.</summary>
    internal sealed class ModernButton : Control
    {
        private bool _hover;
        private bool _down;
        private ButtonKind _kind;
        private Glyph _glyph;

        public ModernButton(string text, ButtonKind kind, Glyph glyph = Glyph.None)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Text = text;
            _kind = kind;
            _glyph = glyph;
            Font = Theme.ButtonFont;
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = Theme.S(36);
        }

        public ButtonKind Kind
        {
            get { return _kind; }
            set
            {
                _kind = value;
                Invalidate();
            }
        }

        public Glyph Glyph
        {
            get { return _glyph; }
            set
            {
                _glyph = value;
                Invalidate();
            }
        }

        public void PerformClick()
        {
            if (Enabled && Visible) OnClick(EventArgs.Empty);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _down = true;
                Focus();
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                PerformClick();
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            Theme.PrepareGraphics(g);

            Color fill, border, fore;
            switch (_kind)
            {
                case ButtonKind.Primary:
                    fill = _down ? Theme.Blend(Theme.AccentHover, Color.Black, 0.1f) : (_hover ? Theme.AccentHover : Theme.Accent);
                    border = fill;
                    fore = Color.White;
                    break;
                case ButtonKind.Danger:
                    fill = _down ? Theme.Blend(Theme.RedHover, Color.Black, 0.1f) : (_hover ? Theme.RedHover : Theme.Red);
                    border = fill;
                    fore = Color.White;
                    break;
                case ButtonKind.Secondary:
                    fill = _down ? Theme.Track : (_hover ? Theme.Hover : Color.White);
                    border = Theme.Border;
                    fore = Theme.Text;
                    break;
                default:
                    fill = _down ? Theme.Track : (_hover ? Theme.Hover : (Parent != null ? Parent.BackColor : Theme.Card));
                    border = fill;
                    fore = Theme.Accent;
                    break;
            }
            if (!Enabled)
            {
                fill = _kind == ButtonKind.Ghost ? (Parent != null ? Parent.BackColor : Theme.Card) : Theme.GraySoft;
                border = _kind == ButtonKind.Secondary ? Theme.Border : fill;
                fore = Theme.Faint;
            }

            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(rect, Theme.Sf(9)))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border, 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            if (Focused && ShowFocusCues)
            {
                var focus = new RectangleF(Theme.Sf(3), Theme.Sf(3), Width - Theme.Sf(7), Height - Theme.Sf(7));
                using (GraphicsPath path = Theme.RoundRect(focus, Theme.Sf(7)))
                using (var pen = new Pen(Color.FromArgb(160, fore), 1f) { DashStyle = DashStyle.Dot })
                    g.DrawPath(pen, path);
            }

            Size textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int glyphSize = _glyph == Glyph.None ? 0 : (int)(Font.Height * 0.9f);
            int gap = _glyph == Glyph.None || string.IsNullOrEmpty(Text) ? 0 : Theme.S(8);
            int total = glyphSize + gap + textSize.Width;
            int x = (Width - total) / 2;
            if (_glyph != Glyph.None)
            {
                var glyphRect = new Rectangle(x, (Height - glyphSize) / 2, glyphSize, glyphSize);
                GlyphPainter.Draw(g, _glyph, glyphRect, fore);
            }
            var textRect = new Rectangle(x + glyphSize + gap, 0, textSize.Width + Theme.S(4), Height);
            Theme.DrawText(g, Text, Font, textRect, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal static class GlyphPainter
    {
        public static void Draw(Graphics g, Glyph glyph, Rectangle r, Color color)
        {
            Theme.PrepareGraphics(g);
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(color, Math.Max(1.6f, r.Width / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
                switch (glyph)
                {
                    case Glyph.Record:
                        float d = r.Width * 0.72f;
                        g.FillEllipse(brush, cx - d / 2, cy - d / 2, d, d);
                        break;
                    case Glyph.Stop:
                        float s = r.Width * 0.62f;
                        using (GraphicsPath p = Theme.RoundRect(new RectangleF(cx - s / 2, cy - s / 2, s, s), s * 0.18f)) g.FillPath(brush, p);
                        break;
                    case Glyph.Pause:
                        float bw = r.Width * 0.2f, bh = r.Height * 0.66f;
                        g.FillRectangle(brush, cx - bw * 1.4f, cy - bh / 2, bw, bh);
                        g.FillRectangle(brush, cx + bw * 0.4f, cy - bh / 2, bw, bh);
                        break;
                    case Glyph.Play:
                        float h = r.Height * 0.7f;
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(cx - h * 0.35f, cy - h / 2),
                            new PointF(cx + h * 0.5f, cy),
                            new PointF(cx - h * 0.35f, cy + h / 2)
                        });
                        break;
                    case Glyph.Check:
                        g.DrawLines(pen, new[]
                        {
                            new PointF(r.X + r.Width * 0.18f, cy + r.Height * 0.02f),
                            new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.74f),
                            new PointF(r.X + r.Width * 0.84f, r.Y + r.Height * 0.28f)
                        });
                        break;
                    default:
                        DrawIconFont(g, glyph, r, color);
                        break;
                }
            }
        }

        private static void DrawIconFont(Graphics g, Glyph glyph, Rectangle r, Color color)
        {
            string ch;
            string fallback;
            switch (glyph)
            {
                case Glyph.Folder:
                    ch = "";
                    fallback = "▤";
                    break;
                case Glyph.Gear:
                    ch = "";
                    fallback = "⚙";
                    break;
                case Glyph.Info:
                    ch = "";
                    fallback = "i";
                    break;
                case Glyph.Refresh:
                    ch = "";
                    fallback = "↻";
                    break;
                default:
                    return;
            }
            Font font = Theme.Icons;
            var bounds = new Rectangle(r.X - r.Width / 2, r.Y - r.Height / 2, r.Width * 2, r.Height * 2);
            if (font != null)
                TextRenderer.DrawText(g, ch, font, bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            else
                TextRenderer.DrawText(g, fallback, Theme.Semibold, bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Индикатор уровня звука с подписью состояния справа.</summary>
    internal sealed class SourceMeter : Control
    {
        private float _level;
        private string _status = "";
        private Color _statusColor = Theme.Muted;
        private bool _active;

        public SourceMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = Theme.S(16);
            Font = Theme.Small;
        }

        public void SetState(float level, bool active, string status, Color statusColor)
        {
            bool changed = Math.Abs(level - _level) > 0.002f || active != _active || status != _status || statusColor != _statusColor;
            _level = level;
            _active = active;
            _status = status ?? "";
            _statusColor = statusColor;
            if (changed) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            Theme.PrepareGraphics(g);

            int statusWidth = Math.Min(Theme.S(190), Width / 2);
            float barHeight = Theme.Sf(6);
            var bar = new RectangleF(0, (Height - barHeight) / 2f, Width - statusWidth - Theme.S(10), barHeight);
            using (GraphicsPath track = Theme.RoundRect(bar, barHeight / 2))
            using (var trackBrush = new SolidBrush(Theme.Track))
                g.FillPath(trackBrush, track);

            if (_active && bar.Width > 4)
            {
                double db = _level <= 0.000001f ? -100 : 20 * Math.Log10(_level);
                float pos = (float)Math.Max(0, Math.Min(1, (db + 60) / 60));
                if (pos > 0.005f)
                {
                    var fill = new RectangleF(bar.X, bar.Y, Math.Max(barHeight, bar.Width * pos), bar.Height);
                    using (GraphicsPath fillPath = Theme.RoundRect(fill, barHeight / 2))
                    using (var brush = new LinearGradientBrush(bar, Theme.Green, Theme.Red, LinearGradientMode.Horizontal))
                    {
                        var blend = new ColorBlend
                        {
                            Colors = new[] { Theme.Hex("#22C55E"), Theme.Hex("#22C55E"), Theme.Hex("#EAB308"), Theme.Hex("#EF4444") },
                            Positions = new[] { 0f, 0.7f, 0.85f, 1f }
                        };
                        brush.InterpolationColors = blend;
                        g.FillPath(brush, fillPath);
                    }
                }
            }

            var statusRect = new Rectangle(Width - statusWidth, 0, statusWidth, Height);
            Theme.DrawText(g, _status, Font, statusRect, _statusColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>«Таблетка» состояния: цветная точка и подпись.</summary>
    internal sealed class StatusPill : Control
    {
        private Color _dot = Theme.Gray;
        private Color _back = Theme.GraySoft;
        private Color _fore = Theme.Text;
        private bool _pulse;

        public StatusPill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Font = Theme.Semibold;
            Height = Theme.S(32);
        }

        public void SetState(string text, Color dot, Color back, Color fore, bool pulse)
        {
            bool changed = text != Text || dot != _dot || back != _back || fore != _fore || pulse != _pulse;
            Text = text;
            _dot = dot;
            _back = back;
            _fore = fore;
            _pulse = pulse;
            Width = TextRenderer.MeasureText(text, Font).Width + Theme.S(44);
            if (changed || pulse) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            Theme.PrepareGraphics(g);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(rect, Height / 2f))
            using (var brush = new SolidBrush(_back))
                g.FillPath(brush, path);

            float d = Theme.Sf(10);
            float cx = Theme.Sf(16), cy = Height / 2f;
            if (_pulse)
            {
                double phase = (Environment.TickCount % 1400) / 1400.0;
                float ring = d * (1f + (float)phase * 0.9f);
                int alpha = (int)(110 * (1 - phase));
                using (var halo = new SolidBrush(Color.FromArgb(Math.Max(0, alpha), _dot)))
                    g.FillEllipse(halo, cx - ring / 2, cy - ring / 2, ring, ring);
            }
            using (var dot = new SolidBrush(_dot))
                g.FillEllipse(dot, cx - d / 2, cy - d / 2, d, d);

            var textRect = new Rectangle(Theme.S(28), 0, Width - Theme.S(36), Height);
            Theme.DrawText(g, Text, Font, textRect, _fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>Переключатель из двух вариантов («Тихая» / «Со звуком»).</summary>
    internal sealed class SegmentedToggle : Control
    {
        private readonly string[] _options;
        private readonly Color?[] _dots;
        private int _selected;
        private int _hover = -1;

        public event EventHandler SelectedChanged;

        public SegmentedToggle(params string[] options)
        {
            _options = options;
            _dots = new Color?[options.Length];
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Font = Theme.Base;
            Height = Theme.S(32);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value == _selected || value < 0 || value >= _options.Length) return;
                _selected = value;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>Цветная точка перед подписью варианта (null — без точки).</summary>
        public void SetDot(int index, Color? color)
        {
            if (index < 0 || index >= _dots.Length || _dots[index] == color) return;
            _dots[index] = color;
            Invalidate();
        }

        private int IndexAt(Point p)
        {
            int w = Width / _options.Length;
            return Math.Max(0, Math.Min(_options.Length - 1, p.X / Math.Max(1, w)));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != _hover)
            {
                _hover = i;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left)
            {
                Focus();
                SelectedIndex = IndexAt(e.Location);
            }
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, _selected - 1);
            if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(_options.Length - 1, _selected + 1);
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            Theme.PrepareGraphics(g);
            var outer = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.RoundRect(outer, Theme.Sf(9)))
            using (var brush = new SolidBrush(Theme.Track))
                g.FillPath(brush, path);

            float w = (Width - Theme.Sf(6)) / _options.Length;
            for (int i = 0; i < _options.Length; i++)
            {
                var seg = new RectangleF(Theme.Sf(3) + i * w, Theme.Sf(3), w, Height - Theme.Sf(7));
                bool selected = i == _selected;
                if (selected)
                {
                    using (GraphicsPath path = Theme.RoundRect(seg, Theme.Sf(7)))
                    using (var brush = new SolidBrush(Color.White))
                    using (var pen = new Pen(Theme.Border))
                    {
                        g.FillPath(brush, path);
                        g.DrawPath(pen, path);
                    }
                }
                Color fore = !Enabled ? Theme.Faint : (selected ? Theme.Text : (i == _hover ? Theme.Text : Theme.Muted));
                Font font = selected ? Theme.Semibold : Font;
                if (_dots[i].HasValue)
                {
                    float d = Theme.Sf(8);
                    int textWidth = TextRenderer.MeasureText(g, _options[i], font, Size.Empty, TextFormatFlags.NoPadding).Width;
                    float total = d + Theme.Sf(7) + textWidth;
                    float x = seg.X + Math.Max(Theme.Sf(4), (seg.Width - total) / 2);
                    using (var dot = new SolidBrush(_dots[i].Value))
                        g.FillEllipse(dot, x, seg.Y + (seg.Height - d) / 2, d, d);
                    var textRect = new Rectangle((int)(x + d + Theme.Sf(7)), (int)seg.Y, (int)(seg.Right - x - d - Theme.Sf(7)), (int)seg.Height);
                    Theme.DrawText(g, _options[i], font, textRect, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
                else
                {
                    Theme.DrawText(g, _options[i], font, Rectangle.Round(seg), fore,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }
            if (Focused && ShowFocusCues)
            {
                using (GraphicsPath path = Theme.RoundRect(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Theme.Sf(8)))
                using (var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot })
                    g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Строка результата проверки: значок состояния, название, итог.</summary>
    internal sealed class CheckRowView : Control
    {
        private CheckItem _item;
        private bool _hover;

        public CheckRowView(CheckItem item)
        {
            _item = item;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = Theme.S(44);
            Cursor = Cursors.Hand;
        }

        public CheckItem Item
        {
            get { return _item; }
            set
            {
                _item = value;
                Invalidate();
            }
        }

        public bool IsSpinning
        {
            get { return _item != null && _item.Status == CheckStatus.Running; }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        public static void StatusColors(CheckStatus status, out Color main, out Color soft)
        {
            switch (status)
            {
                case CheckStatus.Ok:
                    main = Theme.Green;
                    soft = Theme.GreenSoft;
                    break;
                case CheckStatus.Warning:
                    main = Theme.Yellow;
                    soft = Theme.YellowSoft;
                    break;
                case CheckStatus.Error:
                    main = Theme.Red;
                    soft = Theme.RedSoft;
                    break;
                case CheckStatus.Running:
                    main = Theme.Accent;
                    soft = Theme.AccentSoft;
                    break;
                default:
                    main = Theme.Gray;
                    soft = Theme.GraySoft;
                    break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Parent != null ? Parent.BackColor : Theme.Card;
            g.Clear(back);
            Theme.PrepareGraphics(g);
            if (_item == null) return;

            if (_hover)
            {
                using (GraphicsPath path = Theme.RoundRect(new RectangleF(0, 1, Width - 1, Height - 3), Theme.Sf(8)))
                using (var brush = new SolidBrush(Theme.Hover))
                    g.FillPath(brush, path);
            }

            Color main, soft;
            StatusColors(_item.Status, out main, out soft);
            float d = Theme.Sf(24);
            var circle = new RectangleF(Theme.Sf(6), (Height - d) / 2f, d, d);
            using (var brush = new SolidBrush(soft))
                g.FillEllipse(brush, circle);

            using (var pen = new Pen(main, Theme.Sf(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = circle.X + d / 2, cy = circle.Y + d / 2, k = d / 24f;
                switch (_item.Status)
                {
                    case CheckStatus.Ok:
                        g.DrawLines(pen, new[] { new PointF(cx - 5 * k, cy), new PointF(cx - 1.5f * k, cy + 3.5f * k), new PointF(cx + 5 * k, cy - 4 * k) });
                        break;
                    case CheckStatus.Warning:
                        g.DrawLine(pen, cx, cy - 5 * k, cx, cy + 1 * k);
                        using (var b = new SolidBrush(main)) g.FillEllipse(b, cx - 1.4f * k, cy + 3.2f * k, 2.8f * k, 2.8f * k);
                        break;
                    case CheckStatus.Error:
                        g.DrawLine(pen, cx - 4 * k, cy - 4 * k, cx + 4 * k, cy + 4 * k);
                        g.DrawLine(pen, cx + 4 * k, cy - 4 * k, cx - 4 * k, cy + 4 * k);
                        break;
                    case CheckStatus.Running:
                        float angle = (Environment.TickCount % 1000) * 0.36f;
                        g.DrawArc(pen, cx - 6 * k, cy - 6 * k, 12 * k, 12 * k, angle, 270);
                        break;
                    case CheckStatus.Skipped:
                        g.DrawLine(pen, cx - 4 * k, cy, cx + 4 * k, cy);
                        break;
                    default:
                        using (var dotPen = new Pen(Theme.Faint, Theme.Sf(1.5f))) g.DrawEllipse(dotPen, cx - 4 * k, cy - 4 * k, 8 * k, 8 * k);
                        break;
                }
            }

            int textX = Theme.S(42);
            int textW = Width - textX - Theme.S(6);
            int lineH = Theme.S(19);
            int top = (Height - lineH * 2) / 2;
            Theme.DrawText(g, _item.Title, Theme.Semibold, new Rectangle(textX, top, textW, lineH), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Color summaryColor = _item.Status == CheckStatus.Error ? Theme.Red : (_item.Status == CheckStatus.Warning ? Theme.Yellow : Theme.Muted);
            Theme.DrawText(g, _item.Summary, Theme.Small, new Rectangle(textX, top + lineH, textW, lineH), summaryColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Превью записываемого экрана.</summary>
    internal sealed class ScreenPreview : Control
    {
        private Image _image;
        private string _badge;
        private Color _badgeColor;
        private string _placeholder = "Нет картинки";
        private string _caption;

        public ScreenPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        public void SetImage(Image image)
        {
            if (image == _image) return;
            Image old = _image;
            _image = image;
            Invalidate();
            if (old != null) old.Dispose();
        }

        /// <summary>Надпись, пока картинки нет.</summary>
        public string Placeholder
        {
            get { return _placeholder; }
            set
            {
                if (value == _placeholder) return;
                _placeholder = value;
                Invalidate();
            }
        }

        public bool HasImage
        {
            get { return _image != null; }
        }

        /// <summary>Подпись внизу картинки (например, когда сделан снимок).</summary>
        public void SetCaption(string caption)
        {
            if (caption == _caption) return;
            _caption = caption;
            Invalidate();
        }

        public void SetBadge(string badge, Color color)
        {
            if (badge == _badge && color == _badgeColor) return;
            _badge = badge;
            _badgeColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            Theme.PrepareGraphics(g);
            var rect = new RectangleF(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.RoundRect(rect, Theme.Sf(10)))
            {
                using (var bg = new SolidBrush(Theme.Hex("#1F2937"))) g.FillPath(bg, path);
                if (_image != null)
                {
                    Region oldClip = g.Clip;
                    g.SetClip(path);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float scale = Math.Min(rect.Width / _image.Width, rect.Height / _image.Height);
                    float w = _image.Width * scale, h = _image.Height * scale;
                    g.DrawImage(_image, rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
                    g.Clip = oldClip;
                }
                else
                {
                    var textRect = Rectangle.Round(RectangleF.Inflate(rect, -Theme.Sf(8), -Theme.Sf(4)));
                    Theme.DrawText(g, _placeholder ?? "", Theme.Small, textRect, Theme.Faint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                }
                if (!string.IsNullOrEmpty(_caption))
                {
                    int h = TextRenderer.MeasureText(_caption, Theme.Small).Height + Theme.S(6);
                    var strip = new RectangleF(rect.X, rect.Bottom - h, rect.Width, h);
                    Region oldClip = g.Clip;
                    g.SetClip(path);
                    using (var shade = new SolidBrush(Color.FromArgb(170, 17, 24, 39))) g.FillRectangle(shade, strip);
                    g.Clip = oldClip;
                    Theme.DrawText(g, _caption, Theme.Small, Rectangle.Round(new RectangleF(strip.X + Theme.Sf(8), strip.Y, strip.Width - Theme.Sf(16), strip.Height)),
                        Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }

            if (!string.IsNullOrEmpty(_badge))
            {
                Size size = TextRenderer.MeasureText(_badge, Theme.Small);
                var badge = new RectangleF(Theme.Sf(8), Theme.Sf(8), size.Width + Theme.Sf(12), size.Height + Theme.Sf(4));
                using (GraphicsPath p = Theme.RoundRect(badge, badge.Height / 2))
                using (var brush = new SolidBrush(_badgeColor))
                    g.FillPath(brush, p);
                Theme.DrawText(g, _badge, Theme.Small, Rectangle.Round(badge), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _image != null)
            {
                _image.Dispose();
                _image = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Подпись над полем ввода.</summary>
    internal sealed class FieldLabel : Label
    {
        public FieldLabel(string text)
        {
            Text = text;
            Font = Theme.Small;
            ForeColor = Theme.Muted;
            AutoSize = false;
            AutoEllipsis = true;
            Dock = DockStyle.Fill;
            Height = Theme.S(19);
            Margin = new Padding(0, Theme.S(2), 0, 0);
            TextAlign = ContentAlignment.BottomLeft;
            UseMnemonic = false;
        }
    }
}
