using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;

namespace CleanCapture.Services;

/// <summary>窗口列表：给“窗口截图 / 窗口录屏”用的，列出当前能录的窗口。</summary>
public sealed record WindowInfo(IntPtr Handle, string Title)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Handle.ToString() : Title;
}

public static class WindowListHelper
{
    /// <summary>列出所有可见、有标题、不是最小化的顶层窗口（不含自己）。</summary>
    public static List<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        var self = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == self)
            {
                return true;
            }

            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            if (NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            var length = NativeMethods.GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            var builder = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
            var title = builder.ToString();
            if (!string.IsNullOrWhiteSpace(title))
            {
                result.Add(new WindowInfo(hwnd, title));
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>给 Graphics Capture 用的窗口画面项：传窗口句柄就能拿到。</summary>
    public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        try
        {
            return GraphicsCaptureItem.CreateFromWindow(hwnd);
        }
        catch
        {
            // 有些窗口（比如管理员权限的窗口）普通权限拿不到，
            // 返回 null，调用方提示用户换个窗口或用全屏录制。
            return null;
        }
    }

    /// <summary>给 Graphics Capture 用的全屏画面项：传主显示器句柄。</summary>
    public static GraphicsCaptureItem? CreateItemForPrimaryScreen()
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(
                IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            return GraphicsCaptureItem.CreateFromMonitor(monitor);
        }
        catch
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    }
}
