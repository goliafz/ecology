using System;
using System.Drawing;
using System.Runtime.InteropServices;
using MasterVks.Core;

namespace MasterVks.UI
{
    /// <summary>
    /// Цветной кружок поверх кнопки программы на панели задач (штатный механизм Windows).
    /// Нужен, когда программа закреплена на панели задач: тогда Windows рисует значок
    /// ярлыка, а не окна, и кружок виден только через это наложение.
    /// </summary>
    internal sealed class TaskbarOverlay : IDisposable
    {
        [ComImport]
        [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            // ITaskbarList
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);

            // ITaskbarList2
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

            // ITaskbarList3
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int flags);
            void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
            void UnregisterTab(IntPtr hwndTab);
            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
            void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
            void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
        }

        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [ClassInterface(ClassInterfaceType.None)]
        private class TaskbarList
        {
        }

        private ITaskbarList3 _taskbar;
        private Icon _icon;
        private bool _failed;

        public static uint ButtonCreatedMessage()
        {
            try
            {
                return Native.RegisterWindowMessage("TaskbarButtonCreated");
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public void Apply(IntPtr hwnd, AppIconState state, string description)
        {
            if (_failed || hwnd == IntPtr.Zero) return;
            try
            {
                if (_taskbar == null)
                {
                    _taskbar = (ITaskbarList3)new TaskbarList();
                    _taskbar.HrInit();
                }
                Icon icon = IconFactory.CreateOverlay(state);
                _taskbar.SetOverlayIcon(hwnd, icon.Handle, description);
                Icon old = _icon;
                _icon = icon;
                if (old != null) old.Dispose();
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Warn("Не удалось показать кружок на панели задач (остаётся цветной значок окна)", ex);
            }
        }

        public void Dispose()
        {
            if (_icon != null) _icon.Dispose();
            _icon = null;
            try
            {
                if (_taskbar != null) Marshal.ReleaseComObject(_taskbar);
            }
            catch (Exception)
            {
            }
            _taskbar = null;
        }
    }
}
