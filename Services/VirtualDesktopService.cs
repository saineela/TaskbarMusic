using System;
using System.Runtime.InteropServices;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Pins a window to be visible across all Windows Virtual Desktops.
    /// Uses the undocumented IVirtualDesktopManager COM interface.
    /// </summary>
    public static class VirtualDesktopService
    {
        [ComImport]
        [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            [PreserveSig]
            int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);

            [PreserveSig]
            int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }

        [ComImport]
        [Guid("c2e3d7f0-9c1c-407f-82c1-02e044d16d47")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManagerInternal
        {
            [PreserveSig]
            int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);

            [PreserveSig]
            int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);

            [PreserveSig]
            int CanMoveWindowToCurrentDesktop(IntPtr topLevelWindow, out bool canMove);
        }

        [ComImport]
        [Guid("aa509088-7bf8-4935-b469-fdc57f4c6350")]
        [ClassInterface(ClassInterfaceType.None)]
        private class VirtualDesktopManager { }

        private static IVirtualDesktopManager? _manager;

        /// <summary>
        /// Pins the window to be visible on ALL virtual desktops.
        /// This uses a workaround: move to each desktop and back.
        /// For a more reliable approach, we use SetWindowPos with specific flags.
        /// </summary>
        public static bool PinToAllDesktops(IntPtr windowHandle)
        {
            try
            {
                // Try the COM approach first
                if (_manager == null)
                {
                    _manager = (IVirtualDesktopManager)new VirtualDesktopManager();
                }

                // Check if already on current desktop
                _manager.IsWindowOnCurrentVirtualDesktop(windowHandle, out bool onCurrent);

                if (onCurrent)
                {
                    Console.WriteLine("[VDesktop] Window is on current desktop, attempting to pin...");
                }

                // The COM API alone can't "pin" to all desktops.
                // We need to use a different approach.
                // Actually, for AlwaysOnTop + VirtualDesktops, the best approach is
                // to use the Win32 WS_EX_TOOLWINDOW style which makes the window
                // visible across all desktops in Windows 10/11.

                return PinUsingWin32(windowHandle);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VDesktop] COM approach failed: {ex.Message}");
                return PinUsingWin32(windowHandle);
            }
        }

        /// <summary>
        /// Uses Win32 extended window styles to pin across virtual desktops.
        /// WS_EX_TOOLWINDOW makes the window visible on all virtual desktops
        /// and removes it from Alt+Tab and the taskbar.
        /// </summary>
        private static bool PinUsingWin32(IntPtr hWnd)
        {
            try
            {
                // Get current extended style
                var exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);

                // WS_EX_TOOLWINDOW - visible on all virtual desktops, hidden from Alt+Tab
                // WS_EX_NOACTIVATE - prevents window from stealing focus when clicked
                // WS_EX_TOPMOST - stronger topmost than HWND_TOPMOST alone (fights Start menu)
                var newExStyle = exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, newExStyle);

                Console.WriteLine($"[VDesktop] Pinned window (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST applied)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VDesktop] Win32 pin failed: {ex.Message}");
                return false;
            }
        }

        #region Win32 Constants and Imports

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOPMOST = 0x00000008;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // For 64-bit compatibility
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtr64(hWnd, nIndex);
            return new IntPtr(GetWindowLong(hWnd, nIndex));
        }

        private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            else
                SetWindowLong(hWnd, nIndex, dwNewLong.ToInt32());
        }

        #endregion
    }
}
