using System;
using System.IO;
using System.Windows;
using CleanCapture.Services;

namespace CleanCapture;

/// <summary>
/// 程序入口：只做三件事——读设置、建托盘、注册快捷键。
/// 截图和录屏的具体活都不在这里，在各自的 Service 里。
/// </summary>
public partial class App : System.Windows.Application
{
    public static SettingsService Settings { get; private set; } = null!;

    public static HotkeyService Hotkeys { get; private set; } = null!;

    public static TrayService Tray { get; private set; } = null!;

    public App()
    {
        // 2026-11-18：打不开不能悄悄没反应。任何没接住的异常，
        // 都写一份报错日志到桌面、再弹个框把原因告诉用户，
        // 这样才知道到底是哪一步出的问题，而不是双击了半天什么都没有。
        DispatcherUnhandledException += (_, e) =>
        {
            ReportCrash("界面线程出错", e.Exception);
            e.Handled = true;
            Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ReportCrash("程序出错", e.ExceptionObject as Exception);
        };
    }

    /// <summary>报错日志写两份：桌面一份（好找）、程序旁边一份（备用）。写失败就跳过，不再报错。</summary>
    private static void ReportCrash(string title, Exception? ex)
    {
        var detail = ex == null ? "没有拿到具体的错误信息。" : ex.ToString();
        var text = $"CleanCapture 启动/运行失败（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）\n\n{detail}\n";

        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CleanCapture-启动失败.txt"),
            Path.Combine(AppContext.BaseDirectory, "CleanCapture-启动失败.txt"),
        })
        {
            try
            {
                File.WriteAllText(path, text);
            }
            catch
            {
                // 日志写不出来也别再弹错。
            }
        }

        try
        {
            System.Windows.MessageBox.Show(
                "CleanCapture 没能正常打开，原因已经写进桌面上的 CleanCapture-启动失败.txt。\n\n" +
                "请把那个文件发给我，我来修。\n\n错误摘要：\n" + (ex?.Message ?? "未知错误"),
                "CleanCapture：" + title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // 弹框失败就静默退出，日志已经留下了。
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            StartUpCore();
        }
        catch (Exception ex)
        {
            // 启动时任何一步炸了，都不要悄悄退出：留日志、弹原因。
            ReportCrash("启动时出错", ex);
            Shutdown();
        }
    }

    private void StartUpCore()
    {
        Settings = new SettingsService();

        // FFmpeg 不在，直接弹一次提示就好，不要每次启动都弹。
        if (!FFmpegService.TryFindFFmpeg(out _))
        {
            System.Windows.MessageBox.Show(
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
