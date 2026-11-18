using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CleanCapture.Services;

/// <summary>
/// 窗口列表：给“窗口截图 / 窗口录屏”用的，列出当前能录的窗口。
/// 录屏的画面范围见 GdiCaptureSource.CaptureTarget（全屏是主屏幕矩形，
/// 窗口是窗口在屏幕上的位置），2026-11-18 去 Vortice 后不再用
/// GraphicsCaptureItem。
/// </summary>
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

    private static class NativeMethods
    {
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
    }
}
