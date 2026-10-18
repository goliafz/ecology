using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace LetopisVks.UI
{
    internal static class Theme
    {
        public static readonly Color Background = Hex("#F2F4F8");
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Hex("#E2E6EE");
        public static readonly Color Text = Hex("#172033");
        public static readonly Color Muted = Hex("#667085");
        public static readonly Color Faint = Hex("#98A2B3");
        public static readonly Color Hover = Hex("#F6F7FA");
        public static readonly Color Track = Hex("#EDF0F5");

        public static readonly Color Accent = Hex("#4F46E5");
        public static readonly Color AccentHover = Hex("#4338CA");
        public static readonly Color AccentSoft = Hex("#EEF0FF");

        public static readonly Color Green = Hex("#16A34A");
        public static readonly Color GreenSoft = Hex("#DCFCE7");
        public static readonly Color Yellow = Hex("#D97706");
        public static readonly Color YellowSoft = Hex("#FEF3C7");
        public static readonly Color Red = Hex("#DC2626");
        public static readonly Color RedHover = Hex("#B91C1C");
        public static readonly Color RedSoft = Hex("#FEE2E2");
        public static readonly Color Gray = Hex("#98A2B3");
        public static readonly Color GraySoft = Hex("#F2F4F7");

        private static float _scale = 1f;

        public static float Scale
        {
            get { return _scale; }
        }

        public static void InitScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _scale = Math.Max(1f, g.DpiX / 96f);
            }
            catch (Exception)
            {
                _scale = 1f;
            }
        }

        public static int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        public static float Sf(float value)
        {
            return value * _scale;
        }

        // Шрифты в пунктах сами масштабируются вместе с DPI.
        public static readonly Font Base = new Font("Segoe UI", 9.75f);
        public static readonly Font Small = new Font("Segoe UI", 8.75f);
        public static readonly Font Semibold = new Font("Segoe UI Semibold", 9.75f);
        public static readonly Font CardTitle = new Font("Segoe UI Semibold", 11.25f);
        public static readonly Font AppTitle = new Font("Segoe UI Semibold", 15f);
        public static readonly Font Timer = new Font("Segoe UI Light", 27f);
        public static readonly Font ButtonFont = new Font("Segoe UI Semibold", 10f);
        public static readonly Font BigButtonFont = new Font("Segoe UI Semibold", 11.5f);
        public static readonly Font Icons = CreateIconFont(11f);
        public static readonly Font IconsSmall = CreateIconFont(9.5f);

        private static Font CreateIconFont(float size)
        {
            foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try
                {
                    var f = new Font(name, size);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        public static Color Hex(string hex)
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb(
                int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber),
                int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber));
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void PrepareGraphics(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPrefix);
        }
    }
}
