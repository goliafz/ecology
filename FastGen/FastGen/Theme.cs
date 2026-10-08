using System;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using FastGen.Core;

namespace FastGen
{
    /// <summary>Состояние для строк статуса — цвет берётся из текущей темы.</summary>
    public enum StatusKind
    {
        Ok,
        Saved,
        Warn,
        Error,
        Muted
    }

    /// <summary>
    /// Оформление окна. «Классическое» — ровно тот вид, что был до тем (элементы восстанавливаются
    /// из снимка), «Светлое» и «Тёмное» — современные.
    /// </summary>
    public sealed class Theme
    {
        public string Key;
        public string Title;
        public bool IsClassic;
        public bool IsDark;

        // окно
        public Color Window;      // фон окна и панелей
        public Color Header;      // верхняя полоса и полоса вкладок
        public Color Card;        // фон редактора и списков
        public Color Input;       // поля ввода
        public Color Border;
        public Color Text;
        public Color Muted;

        // кнопки
        public Color Accent, AccentHover;
        public Color Primary, PrimaryHover;
        public Color Danger, DangerHover;
        public Color Neutral, NeutralHover;
        public Color Secondary, SecondaryHover, SecondaryText;

        // состояния
        public Color Ok, Saved, Warn, Error;

        // список вариантов
        public Color ListOriginalFore, ListOriginalBack, ListUser, ListDict;

        // редактор
        public string UiFontFamily;
        public string EditorFontFamily;
        public float EditorFontSize;
        public float LineSpacing;
        public int EditorPadding;
        public int ListRowHeight;
        public RtfPalette Palette;

        public Color Status(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Ok: return Ok;
                case StatusKind.Saved: return Saved;
                case StatusKind.Warn: return Warn;
                case StatusKind.Error: return Error;
                default: return Muted;
            }
        }

        public static readonly Theme Classic = new Theme
        {
            Key = "classic",
            Title = "Классическое",
            IsClassic = true,
            Window = SystemColors.Control,
            Header = SystemColors.Control,
            Card = SystemColors.Window,
            Input = SystemColors.Window,
            Border = SystemColors.ControlDark,
            Text = SystemColors.ControlText,
            Muted = Color.DimGray,
            Ok = Color.DarkGreen,
            Saved = Color.Green,
            Warn = Color.DarkOrange,
            Error = Color.Red,
            ListOriginalFore = Color.Blue,
            ListOriginalBack = Color.FromArgb(235, 242, 255),
            ListUser = Color.FromArgb(0, 120, 60),
            ListDict = Color.Gray,
            UiFontFamily = "Segoe UI",
            EditorFontFamily = "Courier New",
            EditorFontSize = 14F,
            LineSpacing = 1F,
            EditorPadding = 0,
            ListRowHeight = 0,
            Palette = RtfPalette.Classic
        };

        public static readonly Theme Light = new Theme
        {
            Key = "light",
            Title = "Светлое",
            Window = Hex(0xF3F4F6),
            Header = Hex(0xFFFFFF),
            Card = Hex(0xFFFFFF),
            Input = Hex(0xFFFFFF),
            Border = Hex(0xD0D7DE),
            Text = Hex(0x1F2328),
            Muted = Hex(0x656D76),
            Accent = Hex(0x2563EB), AccentHover = Hex(0x1D4ED8),
            Primary = Hex(0x16A34A), PrimaryHover = Hex(0x15803D),
            Danger = Hex(0xDC2626), DangerHover = Hex(0xB91C1C),
            Neutral = Hex(0x4B5563), NeutralHover = Hex(0x374151),
            Secondary = Hex(0xFFFFFF), SecondaryHover = Hex(0xEEF2F7), SecondaryText = Hex(0x1F2328),
            Ok = Hex(0x15803D),
            Saved = Hex(0x15803D),
            Warn = Hex(0xB45309),
            Error = Hex(0xDC2626),
            ListOriginalFore = Hex(0x1D4ED8),
            ListOriginalBack = Hex(0xEEF4FF),
            ListUser = Hex(0x15803D),
            ListDict = Hex(0x9CA3AF),
            UiFontFamily = PickFamily("Segoe UI Variable Text", "Segoe UI"),
            EditorFontFamily = PickFamily("Segoe UI"),
            EditorFontSize = 12.5F,
            LineSpacing = 1.25F,
            EditorPadding = 14,
            ListRowHeight = 28,
            Palette = RtfPalette.Light
        };

        public static readonly Theme Dark = new Theme
        {
            Key = "dark",
            Title = "Тёмное",
            IsDark = true,
            Window = Hex(0x1B1D22),
            Header = Hex(0x23262C),
            Card = Hex(0x1F2126),
            Input = Hex(0x2A2D34),
            Border = Hex(0x3A3E46),
            Text = Hex(0xE6E8EB),
            Muted = Hex(0x9AA0A6),
            Accent = Hex(0x3B82F6), AccentHover = Hex(0x2563EB),
            Primary = Hex(0x16A34A), PrimaryHover = Hex(0x15803D),
            Danger = Hex(0xDC2626), DangerHover = Hex(0xB91C1C),
            Neutral = Hex(0x4B5563), NeutralHover = Hex(0x5B6573),
            Secondary = Hex(0x2A2D34), SecondaryHover = Hex(0x353943), SecondaryText = Hex(0xE6E8EB),
            Ok = Hex(0x4ADE80),
            Saved = Hex(0x4ADE80),
            Warn = Hex(0xFBBF24),
            Error = Hex(0xF87171),
            ListOriginalFore = Hex(0x93C5FD),
            ListOriginalBack = Hex(0x1E2A3D),
            ListUser = Hex(0x4ADE80),
            ListDict = Hex(0x7B818A),
            UiFontFamily = PickFamily("Segoe UI Variable Text", "Segoe UI"),
            EditorFontFamily = PickFamily("Segoe UI"),
            EditorFontSize = 12.5F,
            LineSpacing = 1.25F,
            EditorPadding = 14,
            ListRowHeight = 28,
            Palette = RtfPalette.Dark
        };

        public static readonly Theme[] All = { Classic, Light, Dark };

        public static Theme FromKey(string key)
        {
            return All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Light;
        }

        public override string ToString() { return Title; }

        private static Color Hex(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        /// <summary>Первый установленный шрифт из списка (Segoe UI Variable есть только в Windows 11).</summary>
        private static string PickFamily(params string[] names)
        {
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    foreach (var n in names)
                        if (fonts.Families.Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
                            return n;
                }
            }
            catch
            {
                // нет доступа к списку шрифтов — берём последний вариант
            }
            return names[names.Length - 1];
        }
    }
}
