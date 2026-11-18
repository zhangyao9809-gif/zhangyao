using System;
using System.Windows.Forms;
using CleanCapture.Models;

namespace CleanCapture.Services;

/// <summary>
/// 托盘图标：主窗口关了也不退出，缩到右下角托盘里继续等快捷键。
/// 右键托盘图标有菜单：显示主窗口、区域截图、全屏截图、开始/停止录屏、设置、退出。
/// 左键双击托盘图标 = 显示主窗口。
/// 注意：这里用的是 System.Windows.Forms.NotifyIcon（WinForms 的托盘），
/// WPF 本身没有托盘控件，所以 csproj 里开了 UseWindowsForms。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private IntPtr _customHicon;
    private bool _disposed;

    public event Action? ShowMainWindowRequested;
    public event Action? RegionScreenshotRequested;
    public event Action? FullscreenScreenshotRequested;
    public event Action? ToggleRecordingRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _icon = new NotifyIcon
        {
            // 托盘也用专属图标（编进程序集的 icon.png 转过来）；
            // 读不到就回落到系统默认图标，托盘不能因为图标起不来。
            Icon = LoadTrayIcon(),
            Text = "CleanCapture：截图录屏（自用）",
            Visible = true,
        };

        _icon.DoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();

        var menu = new ContextMenuStrip();

        menu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindowRequested?.Invoke());
        menu.Items.Add("区域截图", null, (_, _) => RegionScreenshotRequested?.Invoke());
        menu.Items.Add("全屏截图", null, (_, _) => FullscreenScreenshotRequested?.Invoke());
        menu.Items.Add("开始 / 停止录屏", null, (_, _) => ToggleRecordingRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => OpenSettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon.ContextMenuStrip = menu;
    }

    /// <summary>把编进程序集的 icon.png 转成托盘用的图标。任何一步失败都回落到系统默认图标。</summary>
    private System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/icon.png"))?.Stream;
            if (stream == null)
            {
                return System.Drawing.SystemIcons.Application;
            }

            using var bmp = new System.Drawing.Bitmap(stream);
            _customHicon = bmp.GetHicon();
            return System.Drawing.Icon.FromHandle(_customHicon);
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    /// <summary>录制时托盘提示改一下，让用户一眼看出正在录。</summary>
    public void SetRecording(bool recording, TimeSpan elapsed)
    {
        _icon.Text = recording
            ? $"CleanCapture：录制中 {elapsed:mm\\:ss}"
            : "CleanCapture：截图录屏（自用）";
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();

        // GetHicon() 造出来的句柄要自己还回去，不然每次启动都漏一个。
        if (_customHicon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_customHicon);
            _customHicon = IntPtr.Zero;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
