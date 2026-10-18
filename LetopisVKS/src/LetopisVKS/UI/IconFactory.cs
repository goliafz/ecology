using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace LetopisVks.UI
{
    internal enum AppIconState
    {
        /// <summary>Значок без кружка.</summary>
        Plain,
        /// <summary>Зелёный кружок: записи нет, всё готово.</summary>
        Ready,
        /// <summary>Жёлтый кружок: записи нет, проверка нашла проблемы.</summary>
        Warning,
        /// <summary>Красный кружок: идёт запись.</summary>
        Recording,
        /// <summary>Красный кружок с жёлтой обводкой: идёт запись, но есть проблема.</summary>
        RecordingWarning,
        /// <summary>Красный кружок с паузой.</summary>
        Paused
    }

    /// <summary>
    /// Рисует значок программы для панели задач. Кружок состояния крупный
    /// (почти половина значка), чтобы его было видно даже на маленьких иконках.
    /// </summary>
    internal static class IconFactory
    {
        private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64 };
        private static readonly int[] OverlaySizes = { 16, 20, 24, 32 };

        public static Icon Create(AppIconState state)
        {
            return BuildIcon(Sizes, size => Render(size, state));
        }

        /// <summary>Только кружок — для наложения на кнопку панели задач.</summary>
        public static Icon CreateOverlay(AppIconState state)
        {
            return BuildIcon(OverlaySizes, size => RenderDotOnly(size, state));
        }

        private static Icon BuildIcon(int[] sizes, Func<int, Bitmap> render)
        {
            using (var ms = new MemoryStream())
            {
                WriteIco(ms, sizes, render);
                ms.Position = 0;
                return new Icon(ms);
            }
        }

        public static Bitmap RenderDotOnly(int size, AppIconState state)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                if (state != AppIconState.Plain) DrawDot(g, size, size / 2f, size / 2f, size - 1f, state);
            }
            return bmp;
        }

        public static Bitmap Render(int size, AppIconState state)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;

                // Плашка
                var plate = new RectangleF(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);
                using (GraphicsPath p = Theme.RoundRect(plate, s * 0.22f))
                using (var brush = new LinearGradientBrush(plate, Theme.Hex("#6366F1"), Theme.Hex("#4338CA"), 45f))
                    g.FillPath(brush, p);

                // Видеокамера
                bool withDot = state != AppIconState.Plain;
                float shift = withDot ? -s * 0.06f : 0f;
                var body = new RectangleF(s * 0.17f, s * 0.31f + shift, s * 0.44f, s * 0.36f);
                using (GraphicsPath p = Theme.RoundRect(body, s * 0.08f))
                using (var white = new SolidBrush(Color.White))
                {
                    g.FillPath(white, p);
                    g.FillPolygon(white, new[]
                    {
                        new PointF(s * 0.64f, s * 0.45f + shift),
                        new PointF(s * 0.84f, s * 0.33f + shift),
                        new PointF(s * 0.84f, s * 0.65f + shift),
                        new PointF(s * 0.64f, s * 0.53f + shift)
                    });
                }

                if (withDot)
                {
                    float d = s * 0.52f;
                    DrawDot(g, s, s - d / 2 - s * 0.01f, s - d / 2 - s * 0.01f, d, state);
                }
            }
            return bmp;
        }

        private static void DrawDot(Graphics g, float s, float cx, float cy, float d, AppIconState state)
        {
            Color color;
            switch (state)
            {
                case AppIconState.Ready:
                    color = Theme.Hex("#22C55E");
                    break;
                case AppIconState.Warning:
                    color = Theme.Hex("#F59E0B");
                    break;
                default:
                    color = Theme.Hex("#EF4444");
                    break;
            }
            float ring = Math.Max(1f, d * 0.12f);
            Color ringColor = state == AppIconState.RecordingWarning ? Theme.Hex("#FACC15") : Color.White;
            float ringWidth = state == AppIconState.RecordingWarning ? ring * 1.8f : ring;
            using (var ringBrush = new SolidBrush(ringColor))
                g.FillEllipse(ringBrush, cx - d / 2, cy - d / 2, d, d);
            float inner = d - ringWidth * 2;
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, cx - inner / 2, cy - inner / 2, inner, inner);

            if (state == AppIconState.Paused)
            {
                float bw = inner * 0.16f, bh = inner * 0.5f;
                using (var white = new SolidBrush(Color.White))
                {
                    g.FillRectangle(white, cx - bw * 1.5f, cy - bh / 2, bw, bh);
                    g.FillRectangle(white, cx + bw * 0.5f, cy - bh / 2, bw, bh);
                }
            }
        }

        /// <summary>ICO с 32-битными DIB-картинками — самый совместимый вариант для Windows.</summary>
        private static void WriteIco(Stream stream, int[] sizes, Func<int, Bitmap> render)
        {
            var images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap bmp = render(sizes[i])) images[i] = ToDib(bmp);
            }

            var w = new BinaryWriter(stream);
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
            w.Flush();
        }

        private static byte[] ToDib(Bitmap bmp)
        {
            int size = bmp.Width;
            int maskRow = ((size + 31) / 32) * 4;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(40);
            w.Write(size);
            w.Write(size * 2);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(0);
            w.Write(size * size * 4 + maskRow * size);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[size * 4];
                for (int y = size - 1; y >= 0; y--)
                {
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    w.Write(row);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            w.Write(new byte[maskRow * size]);
            w.Flush();
            return ms.ToArray();
        }
    }
}
