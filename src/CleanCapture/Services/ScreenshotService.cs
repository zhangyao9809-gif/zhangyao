using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CleanCapture.Models;

namespace CleanCapture.Services;

/// <summary>
/// 截图：全屏、区域、窗口三种，全部存成 PNG（真无损）。
/// 全屏 / 区域用系统自带的 CopyFromScreen（最稳、最快）；
/// 窗口用 PrintWindow（能抓到被别的窗口盖住一部分的窗口）。
/// 高 DPI 下的位置换算在调用方（RegionSelectorWindow）做，
/// 这里只认“真实像素”的矩形，不再二次换算，避免错位。
/// </summary>
public sealed class ScreenshotService
{
    private readonly AppSettings _settings;

    public ScreenshotService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>截整个主屏幕（多屏时是“主屏”，不是所有屏拼起来）。</summary>
    public string CaptureFullscreen()
    {
        var bounds = GetPrimaryScreenBounds();
        return CaptureRegion(bounds);
    }

    /// <summary>按真实像素矩形截一块。bounds 的单位是像素，不是 WPF 的逻辑单位。</summary>
    public string CaptureRegion(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentException("截图区域宽高必须大于 0。");
        }

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        }

        return SaveBitmap(bitmap, "screenshot");
    }

    /// <summary>截一个窗口：hwnd 是窗口句柄，见 WindowListHelper。</summary>
    public string CaptureWindow(IntPtr hwnd, string windowTitle)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("窗口句柄为空。");
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException("拿不到这个窗口的位置，可能窗口已经关了。");
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("这个窗口现在是最小化或没有大小，截不了。");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                // PrintWindow 会让目标窗口自己把画面画到我们的位图上，
                // 比 CopyFromScreen 更适合截单个窗口。
                var ok = NativeMethods.PrintWindow(hwnd, hdc, 0);
                if (!ok)
                {
                    // PrintWindow 失败时退回 CopyFromScreen：按窗口位置截一块。
                    graphics.ReleaseHdc(hdc);
                    hdc = IntPtr.Zero;
                    graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                    return SaveBitmap(bitmap, "screenshot");
                }
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
        }

        return SaveBitmap(bitmap, "screenshot");
    }

    /// <summary>把位图存成 PNG，并按设置决定要不要同时复制到剪贴板。</summary>
    private string SaveBitmap(Bitmap bitmap, string kind)
    {
        var folder = _settings.SaveFolder;
        Directory.CreateDirectory(folder);

        var fileName = SettingsService.MakeFileName(kind, "png");
        var fullPath = Path.Combine(folder, fileName);
        bitmap.Save(fullPath, ImageFormat.Png);

        if (_settings.CopyScreenshotToClipboard)
        {
            try
            {
                // Clipboard 要在 STA 线程上跑，主窗口本身就是 STA，直接调即可。
                // 如果是从快捷键的后台线程调过来，调用方要先切回 UI 线程。
                System.Windows.Clipboard.SetImage(BitmapToBitmapSource(bitmap));
            }
            catch
            {
                // 剪贴板正被别的程序占着时，图已经存好了，只是不复制，
                // 不要因为这个弹错。
            }
        }

        return fullPath;
    }

    private static BitmapSource BitmapToBitmapSource(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }

    private static Rectangle GetPrimaryScreenBounds()
    {
        // SystemParameters.PrimaryScreenWidth 是逻辑单位（WPF 单位），
        // 要乘以 DPI 缩放才是真实像素；这里直接用 WinForms 的办法拿真实像素。
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? new Rectangle(0, 0, 1920, 1080);
        return bounds;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
