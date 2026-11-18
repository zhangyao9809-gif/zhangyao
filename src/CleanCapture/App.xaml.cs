using System;
using System.Windows;
using CleanCapture.Services;

namespace CleanCapture;

/// <summary>
/// 程序入口：只做三件事——读设置、建托盘、注册快捷键。
/// 截图和录屏的具体活都不在这里，在各自的 Service 里。
/// </summary>
public partial class App : Application
{
    public static SettingsService Settings { get; private set; } = null!;

    public static HotkeyService Hotkeys { get; private set; } = null!;

    public static TrayService Tray { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = new SettingsService();

        // FFmpeg 不在，直接弹一次提示就好，不要每次启动都弹。
        if (!FFmpegService.TryFindFFmpeg(out _))
        {
            MessageBox.Show(
                "没有找到 ffmpeg.exe，录屏现在还不能用，截图还能用。\n\n" +
                "请按 README 第 4 节下载一次，解压后把 ffmpeg.exe 放在 CleanCapture.exe 旁边，再重启本程序。",
                "CleanCapture：缺 FFmpeg",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Tray = new TrayService();
        Hotkeys = new HotkeyService();

        // 注意：快捷键不能在这里注册——这时候主窗口还没建好，没有窗口句柄，
        // RegisterHotKey 会失败。真正的注册在 MainWindow 的 Loaded 事件里做，
        // 见 MainWindow.xaml.cs。注册失败（被别的软件占了）时，
        // HotkeyService 内部会弹提示，程序本身继续跑，按钮点按都还能用。
    }

    /// <summary>托盘菜单“退出”调这里：先注销快捷键、收托盘，再真正退出。</summary>
    public static void ExitApplication()
    {
        try
        {
            Hotkeys?.Dispose();
            Tray?.Dispose();
        }
        catch
        {
            // 退出时不要再弹错，直接走。
        }

        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Hotkeys?.Dispose();
            Tray?.Dispose();
        }
        catch
        {
            // 同上。
        }

        base.OnExit(e);
    }
}
