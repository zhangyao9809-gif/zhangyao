using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CleanCapture.Models;
using CleanCapture.Services;

namespace CleanCapture;

/// <summary>
/// 主窗口：只是个“遥控器”，真正的活在 Service 里。
/// 点按钮 / 按快捷键 / 点托盘菜单，最后都走到这里的几个方法：
/// 区域截图、全屏截图、窗口截图、开始/停止/暂停录屏、打开设置。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ScreenshotService _screenshots;
    private readonly RecordingService _recording;
    private readonly AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();

        _settings = App.Settings.Current;
        _screenshots = new ScreenshotService(_settings);
        _recording = new RecordingService(_settings);
        _recording.StatusChanged += OnRecordingStatusChanged;

        // 快捷键要等窗口句柄建好才能注册，所以放在 Loaded 里，
        // 不能放在 App 启动时（那时候主窗口还没出来）。
        Loaded += (_, _) => App.Hotkeys.RegisterAll(_settings);

        // 快捷键和托盘菜单都接到这里的方法上。
        App.Hotkeys.HotkeyPressed += OnHotkeyPressed;
        App.Tray.ShowMainWindowRequested += () => Dispatcher.Invoke(ShowWindow);
        App.Tray.RegionScreenshotRequested += () => Dispatcher.Invoke(DoRegionScreenshot);
        App.Tray.FullscreenScreenshotRequested += () => Dispatcher.Invoke(DoFullscreenScreenshot);
        App.Tray.ToggleRecordingRequested += () => Dispatcher.Invoke(ToggleRecording);
        App.Tray.OpenSettingsRequested += () => Dispatcher.Invoke(OpenSettings);
        App.Tray.ExitRequested += () => Dispatcher.Invoke(App.ExitApplication);

        RefreshSizeHint();
        Closing += (_, e) =>
        {
            // 点右上角 × 不是退出，是缩到托盘；想退出请点托盘菜单“退出”。
            e.Cancel = true;
            Hide();
            App.Tray.ShowBalloon("CleanCapture 还在跑", "已缩到托盘，快捷键还能用，右键托盘图标可以退出。");
        };
    }

    // ---------------- 截图 ----------------

    private void OnRegionShotClick(object sender, RoutedEventArgs e) => DoRegionScreenshot();

    private void OnFullscreenShotClick(object sender, RoutedEventArgs e) => DoFullscreenScreenshot();

    private void OnWindowShotClick(object sender, RoutedEventArgs e) => DoWindowScreenshot();

    private void DoRegionScreenshot()
    {
        var selector = new RegionSelectorWindow();
        selector.ShowDialog();
        if (selector.SelectedRegion is not { } region)
        {
            return;
        }

        RunSafely(() =>
        {
            var path = _screenshots.CaptureRegion(region);
            StatusText.Text = $"截图已存到桌面：{System.IO.Path.GetFileName(path)}";
            App.Tray.ShowBalloon("截图好了", System.IO.Path.GetFileName(path));
        });
    }

    private void DoFullscreenScreenshot()
    {
        RunSafely(() =>
        {
            var path = _screenshots.CaptureFullscreen();
            StatusText.Text = $"截图已存到桌面：{System.IO.Path.GetFileName(path)}";
        });
    }

    private void DoWindowScreenshot()
    {
        var picked = PickWindow("选一个窗口来截图");
        if (picked == null)
        {
            return;
        }

        RunSafely(() =>
        {
            var path = _screenshots.CaptureWindow(picked.Value.Handle, picked.Value.Title);
            StatusText.Text = $"截图已存到桌面：{System.IO.Path.GetFileName(path)}";
        });
    }

    // ---------------- 录屏 ----------------

    private void OnRecordClick(object sender, RoutedEventArgs e) => ToggleRecording();

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (!_recording.IsRecording)
        {
            return;
        }

        if (_recording.IsPaused)
        {
            _recording.Resume();
            PauseButton.Content = "暂停";
        }
        else
        {
            _recording.Pause();
            PauseButton.Content = "继续";
        }
    }

    private void ToggleRecording()
    {
        if (_recording.IsRecording)
        {
            var path = _recording.Stop();
            RecordButton.Content = "开始录屏";
            PauseButton.IsEnabled = false;
            PauseButton.Content = "暂停";
            App.Tray.SetRecording(false, TimeSpan.Zero);
            StatusText.Text = path == null
                ? "录制已停止，但没有生成文件，请看一下磁盘空间和 FFmpeg 是否正常。"
                : $"录制好了：{System.IO.Path.GetFileName(path)}";
            return;
        }

        // 还没开始录：先看录什么范围。
        // 2026-11-18 改走 GDI：画面范围不再用 GraphicsCaptureItem，
        // 改用 CaptureTarget（全屏就是主屏幕矩形，窗口就是窗口在屏幕上的位置），
        // 见 GdiCaptureSource.cs。区域录屏仍是先抓全屏、再让 FFmpeg 裁剪。
        var scope = (RecordScopeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全屏";
        Rectangle? region = null;
        CaptureTarget? target = null;

        if (scope == "区域…")
        {
            var selector = new RegionSelectorWindow();
            selector.ShowDialog();
            if (selector.SelectedRegion is not { } picked)
            {
                return;
            }

            region = picked;
            target = CaptureTarget.ForPrimaryScreen();
        }
        else if (scope == "窗口…")
        {
            var picked = PickWindow("选一个窗口来录");
            if (picked == null)
            {
                return;
            }

            target = CaptureTarget.ForWindow(picked.Value.Handle);
        }
        else
        {
            target = CaptureTarget.ForPrimaryScreen();
        }

        if (target == null)
        {
            MessageBox.Show(
                "拿不到这个窗口的位置，可能是窗口关了或最小化了。\n请换个窗口试试，或者改录全屏。",
                "CleanCapture：拿不到画面",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 录之前先把“大概占多少空间”说清楚，真无损尤其要提醒。
        var width = region?.Width ?? target.Width;
        var height = region?.Height ?? target.Height;
        var warning = RecordingService.BuildSizeWarning(_settings, width, height);
        var answer = MessageBox.Show(
            $"{warning}\n\n只录系统声，不录麦克风。\n保存到：{_settings.SaveFolder}\n\n点“确定”开始录，点“取消”就不录。",
            "开始录屏？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        RunSafely(() =>
        {
            _recording.Start(target, region);
            RecordButton.Content = "停止录屏";
            PauseButton.IsEnabled = true;
            StatusText.Text = "录制中 00:00";
        });
    }

    private void OnRecordingStatusChanged(TimeSpan elapsed, string status)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"{status} {elapsed:mm\\:ss}";
            App.Tray.SetRecording(_recording.IsRecording, elapsed);
        });
    }

    // ---------------- 快捷键 / 设置 / 托盘 ----------------

    private void OnHotkeyPressed(string action)
    {
        Dispatcher.Invoke(() =>
        {
            switch (action)
            {
                case "RegionScreenshot":
                    DoRegionScreenshot();
                    break;
                case "FullscreenScreenshot":
                    DoFullscreenScreenshot();
                    break;
                case "ToggleRecording":
                    ToggleRecording();
                    break;
                case "PauseRecording":
                    OnPauseClick(this, new RoutedEventArgs());
                    break;
            }
        });
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e) => Hide();

    private void OpenSettings()
    {
        var window = new SettingsWindow();
        window.ShowDialog();
        RefreshSizeHint();
    }

    private void ShowWindow()
    {
        Show();
        Activate();
        WindowState = WindowState.Normal;
    }

    private void RefreshSizeHint()
    {
        var (_, text) = FFmpegService.EstimateSizePerMinute(1920, 1080, _settings.FrameRate,
            _settings.Quality == RecordingQuality.Lossless);
        SizeHintText.Text = $"当前：{_settings.QualityDisplayName}，{_settings.FrameRate} 帧/秒。1080p 下{text}。";
    }

    /// <summary>弹一个简单的窗口列表，让用户点一个窗口。取消返回 null。</summary>
    private static WindowInfo? PickWindow(string title)
    {
        var windows = WindowListHelper.ListWindows();
        if (windows.Count == 0)
        {
            MessageBox.Show("现在没有能截 / 能录的窗口。", "CleanCapture", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
        };

        var list = new ListBox { ItemsSource = windows.Select(w => w.Title).ToList() };
        list.SelectedIndex = 0;
        list.MouseDoubleClick += (_, _) => dialog.DialogResult = true;

        var okButton = new Button { Content = "确定", Margin = new Thickness(0, 8, 0, 0) };
        okButton.Click += (_, _) => dialog.DialogResult = true;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(list);
        panel.Children.Add(okButton);
        dialog.Content = panel;

        var result = dialog.ShowDialog();
        if (result != true || list.SelectedIndex < 0)
        {
            return null;
        }

        return windows[list.SelectedIndex];
    }

    /// <summary>把可能出错的操作包起来，出错弹一句人话，不让程序闪退。</summary>
    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"这一步没做成：{ex.Message}\n\n如果反复出现，先看 README 第 7 节的常见问题。",
                "CleanCapture：没做成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
