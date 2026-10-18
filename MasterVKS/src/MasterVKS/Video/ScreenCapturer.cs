using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using MasterVks.Core;

namespace MasterVks.Video
{
    /// <summary>
    /// Захват одного экрана через GDI в заранее выделенный буфер (BGRA, сверху вниз).
    /// Должен создаваться и использоваться в одном потоке, переведённом в Per-Monitor DPI.
    /// Если разрешение экрана меняется во время записи (например, подключили HDMI),
    /// картинка вписывается в исходный размер с чёрными полями — запись не прерывается.
    /// </summary>
    internal sealed class ScreenCapturer : IDisposable
    {
        private readonly string _requestedDevice;
        private readonly bool _drawCursor;
        private readonly IntPtr _memDc;
        private readonly IntPtr _dib;
        private readonly IntPtr _oldBitmap;
        private readonly IntPtr _bits;

        private Rectangle _source;
        private string _sourceDevice;
        private long _lastRefresh;
        private double _scale = 1;
        private int _offsetX;
        private int _offsetY;

        private IntPtr _lastCursor = IntPtr.Zero;
        private int _hotX;
        private int _hotY;
        private bool _disposed;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public int FrameBytes
        {
            get { return Width * Height * 4; }
        }

        /// <summary>Текущий источник отличается от того, с которого начали (другой экран или размер).</summary>
        public bool IsLetterboxed { get; private set; }

        public string SourceDevice
        {
            get { return _sourceDevice; }
        }

        /// <summary>Сообщение для журнала, когда источник изменился. Забирается и обнуляется вызывающим.</summary>
        public string PendingNotice { get; set; }

        public ScreenCapturer(string deviceName, bool drawCursor)
        {
            _requestedDevice = deviceName;
            _drawCursor = drawCursor;

            MonitorDesc monitor = MonitorList.Find(deviceName) ?? MonitorList.Primary();
            if (monitor == null) throw new InvalidOperationException("Windows не сообщает ни об одном экране.");

            _source = monitor.Bounds;
            _sourceDevice = monitor.DeviceName;
            Width = Math.Max(2, monitor.Bounds.Width & ~1);
            Height = Math.Max(2, monitor.Bounds.Height & ~1);

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new InvalidOperationException("Нет доступа к экрану.");
            try
            {
                _memDc = Native.CreateCompatibleDC(screen);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
            if (_memDc == IntPtr.Zero) throw new Win32Exception("Не удалось подготовить захват экрана.");

            var bmi = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                    biWidth = Width,
                    biHeight = -Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                },
                bmiColors = new uint[4]
            };
            IntPtr bits;
            _dib = Native.CreateDIBSection(_memDc, ref bmi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Native.DeleteDC(_memDc);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось выделить память под кадр " + Width + "×" + Height + ".");
            }
            _bits = bits;
            _oldBitmap = Native.SelectObject(_memDc, _dib);
            _lastRefresh = Clock.Now;
        }

        /// <summary>Снимает кадр в <paramref name="destination"/>. false — экран сейчас недоступен (например, окно UAC).</summary>
        public bool Capture(byte[] destination)
        {
            if (_disposed) throw new ObjectDisposedException("ScreenCapturer");
            RefreshSourceIfDue();

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            bool ok;
            try
            {
                bool sameSize = _source.Width >= Width && _source.Height >= Height
                                && _source.Width - Width <= 1 && _source.Height - Height <= 1;
                if (sameSize)
                {
                    _scale = 1;
                    _offsetX = 0;
                    _offsetY = 0;
                    IsLetterboxed = !string.Equals(_sourceDevice, _requestedDevice, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_requestedDevice);
                    ok = Native.BitBlt(_memDc, 0, 0, Width, Height, screen, _source.X, _source.Y, Native.SRCCOPY);
                }
                else
                {
                    IsLetterboxed = true;
                    _scale = Math.Min(Width / (double)_source.Width, Height / (double)_source.Height);
                    int w = Math.Max(1, (int)Math.Round(_source.Width * _scale));
                    int h = Math.Max(1, (int)Math.Round(_source.Height * _scale));
                    _offsetX = (Width - w) / 2;
                    _offsetY = (Height - h) / 2;
                    Native.PatBlt(_memDc, 0, 0, Width, Height, Native.BLACKNESS);
                    Native.SetStretchBltMode(_memDc, Native.HALFTONE);
                    Native.SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
                    ok = Native.StretchBlt(_memDc, _offsetX, _offsetY, w, h, screen, _source.X, _source.Y, _source.Width, _source.Height, Native.SRCCOPY);
                }
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
            if (!ok) return false;

            if (_drawCursor) DrawCursor();
            Native.GdiFlush();
            Marshal.Copy(_bits, destination, 0, FrameBytes);
            return true;
        }

        private void RefreshSourceIfDue()
        {
            long now = Clock.Now;
            if (now - _lastRefresh < Clock.Frequency) return;
            _lastRefresh = now;

            MonitorDesc monitor = MonitorList.Find(_requestedDevice);
            if (monitor == null) monitor = MonitorList.Find(_sourceDevice) ?? MonitorList.Primary();
            if (monitor == null) return;
            if (monitor.Bounds != _source || !string.Equals(monitor.DeviceName, _sourceDevice, StringComparison.OrdinalIgnoreCase))
            {
                PendingNotice = "Экран изменился: теперь " + monitor.DisplayName + ". Запись продолжается, картинка вписана в исходный размер " + Width + " × " + Height + ".";
                _source = monitor.Bounds;
                _sourceDevice = monitor.DeviceName;
            }
        }

        private void DrawCursor()
        {
            try
            {
                var info = new Native.CURSORINFO { cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO)) };
                if (!Native.GetCursorInfo(ref info)) return;
                if ((info.flags & Native.CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return;

                if (info.hCursor != _lastCursor)
                {
                    Native.ICONINFO iconInfo;
                    if (Native.GetIconInfo(info.hCursor, out iconInfo))
                    {
                        _hotX = iconInfo.xHotspot;
                        _hotY = iconInfo.yHotspot;
                        if (iconInfo.hbmMask != IntPtr.Zero) Native.DeleteObject(iconInfo.hbmMask);
                        if (iconInfo.hbmColor != IntPtr.Zero) Native.DeleteObject(iconInfo.hbmColor);
                    }
                    else
                    {
                        _hotX = 0;
                        _hotY = 0;
                    }
                    _lastCursor = info.hCursor;
                }

                int x = _offsetX + (int)Math.Round((info.ptScreenPos.X - _source.X) * _scale) - _hotX;
                int y = _offsetY + (int)Math.Round((info.ptScreenPos.Y - _source.Y) * _scale) - _hotY;
                if (x < -128 || y < -128 || x > Width || y > Height) return;
                Native.DrawIconEx(_memDc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
            }
            catch (Exception)
            {
                // курсор — не главное, кадр важнее
            }
        }

        /// <summary>Уменьшенная копия экрана для превью в окне программы.</summary>
        public static Bitmap CaptureThumbnail(string deviceName, int maxWidth, int maxHeight)
        {
            using (new PerMonitorDpiScope())
            {
                MonitorDesc monitor = MonitorList.Find(deviceName) ?? MonitorList.Primary();
                if (monitor == null || monitor.Bounds.Width <= 0 || monitor.Bounds.Height <= 0) return null;

                double scale = Math.Min(maxWidth / (double)monitor.Bounds.Width, maxHeight / (double)monitor.Bounds.Height);
                int tw = Math.Max(1, (int)(monitor.Bounds.Width * scale));
                int th = Math.Max(1, (int)(monitor.Bounds.Height * scale));

                IntPtr screen = Native.GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return null;
                IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    mem = Native.CreateCompatibleDC(screen);
                    bmp = Native.CreateCompatibleBitmap(screen, tw, th);
                    if (mem == IntPtr.Zero || bmp == IntPtr.Zero) return null;
                    old = Native.SelectObject(mem, bmp);
                    Native.SetStretchBltMode(mem, Native.HALFTONE);
                    Native.SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
                    bool ok = Native.StretchBlt(mem, 0, 0, tw, th, screen, monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height, Native.SRCCOPY);
                    Native.SelectObject(mem, old);
                    old = IntPtr.Zero;
                    return ok ? Image.FromHbitmap(bmp) : null;
                }
                finally
                {
                    if (old != IntPtr.Zero) Native.SelectObject(mem, old);
                    if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
                    if (mem != IntPtr.Zero) Native.DeleteDC(mem);
                    Native.ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

        /// <summary>Доля «почти чёрных» точек в кадре (0..1) — проверяем по сетке, чтобы не тратить время.</summary>
        public static double DarkShare(byte[] frame, int width, int height)
        {
            int dark = 0, total = 0;
            int stepX = Math.Max(1, width / 64), stepY = Math.Max(1, height / 36);
            for (int y = 0; y < height; y += stepY)
            {
                int row = y * width * 4;
                for (int x = 0; x < width; x += stepX)
                {
                    int i = row + x * 4;
                    if (frame[i] < 8 && frame[i + 1] < 8 && frame[i + 2] < 8) dark++;
                    total++;
                }
            }
            return total == 0 ? 1 : dark / (double)total;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_oldBitmap != IntPtr.Zero) Native.SelectObject(_memDc, _oldBitmap);
                if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
                if (_memDc != IntPtr.Zero) Native.DeleteDC(_memDc);
            }
            catch (Exception)
            {
            }
        }
    }
}
