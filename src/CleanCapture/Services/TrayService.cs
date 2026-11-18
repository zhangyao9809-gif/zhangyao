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
            // 没有自定义图标文件时先用系统默认图标；
            // 之后想换图标，把 .ico 文件放进项目、设为 Resource，
            // 再在这里改成 new Icon(.....) 即可。
            Icon = System.Drawing.SystemIcons.Application,
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
    }
}
