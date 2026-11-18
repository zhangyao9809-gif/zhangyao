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
        RefreshHotkeyHint();

        // 系统声音开关：按保存下来的设置显示，默认开。
        SystemAudioToggle.IsChecked = _settings.RecordSystemAudio;
        UpdateSystemAudioLabel();

        // 麦克风开关：按保存下来的设置显示，默认关。UI 上默认也是关的，
        // 和设置里的默认值一致，不会界面上显示开、实际是关。
        MicrophoneToggle.IsChecked = _settings.RecordMicrophone;
        UpdateMicrophoneLabel();
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

    private async void DoRegionScreenshot()
    {
        var selector = new RegionSelectorWindow();
        selector.ShowDialog();
        if (selector.SelectedRegion is not { } region)
        {
            return;
        }

        // 2026-11-18：截图前先把主窗口藏起来，不然框选的区域里会拍到它自己；
        // 藏起来要等一下（约 300ms）让画面真正消失再截，截完再回来。
        Hide();
        try
        {
            await System.Threading.Tasks.Task.Delay(300);
            RunSafely(() =>
            {
                var path = _screenshots.CaptureRegion(region);
                StatusText.Text = $"截图已存到桌面：{System.IO.Path.GetFileName(path)}";
                App.Tray.ShowBalloon("截图好了", System.IO.Path.GetFileName(path));
            });
        }
        finally
        {
            ShowWindow();
        }
    }

    private async void DoFullscreenScreenshot()
    {
        // 全屏截图不藏窗口的话，截出来的图里就有 CleanCapture 自己。
        Hide();
        try
        {
            await System.Threading.Tasks.Task.Delay(300);
            RunSafely(() =>
            {
                var path = _screenshots.CaptureFullscreen();
                StatusText.Text = $"截图已存到桌面：{System.IO.Path.GetFileName(path)}";
            });
        }
        finally
        {
            ShowWindow();
        }
    }

    private async void DoWindowScreenshot()
    {
        var picked = PickWindow("选一个窗口来截图");
        if (picked == null)
        {
            return;
        }

        // 目标窗口可能被主窗口盖住，先藏起来再截。
        Hide();
        try
        {
            await System.Threading.Tasks.Task.Delay(300);
            RunSafely(() =>
            {
                var path = _screenshots.CaptureWindow(picked.Handle, picked.Title);
                StatusText.Text = $"截图已存到桌面：{System.IO.Path.GetFileName(path)}";
            });
        }
        finally
        {
            ShowWindow();
        }
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
            PauseButtonText.Text = "暂停";
        }
        else
        {
            _recording.Pause();
            PauseButtonText.Text = "继续";
        }
    }

    /// <summary>停止按钮：录制中才有效，和“开始录屏”再按一次是同一个停止动作。</summary>
    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_recording.IsRecording)
        {
            ToggleRecording();
        }
    }

    /// <summary>系统声音开关：真开关，关了这次录屏就没有声音；麦克风永远不录。</summary>
    private void OnSystemAudioToggleChanged(object sender, RoutedEventArgs e)
    {
        _settings.RecordSystemAudio = SystemAudioToggle.IsChecked == true;
        App.Settings.Save();
        UpdateSystemAudioLabel();
    }

    private void UpdateSystemAudioLabel()
    {
        SystemAudioStateText.Text = _settings.RecordSystemAudio ? "开启" : "关闭";
    }

    /// <summary>麦克风开关：真开关，默认关；打开后这次录屏会录人声。</summary>
    private void OnMicrophoneToggleChanged(object sender, RoutedEventArgs e)
    {
        _settings.RecordMicrophone = MicrophoneToggle.IsChecked == true;
        App.Settings.Save();
        UpdateMicrophoneLabel();
    }

    private void UpdateMicrophoneLabel()
    {
        MicrophoneStateText.Text = _settings.RecordMicrophone ? "开启" : "关闭";
    }

    /// <summary>底部状态栏：截图目录 / 录屏目录，打开实际保存文件的文件夹。</summary>
    private void OnOpenShotFolderClick(object sender, RoutedEventArgs e) => OpenSaveFolder();

    private void OnOpenRecordFolderClick(object sender, RoutedEventArgs e) => OpenSaveFolder();

    private void OpenSaveFolder()
    {
        try
        {
            var folder = _settings.SaveFolder;
            if (!System.IO.Directory.Exists(folder))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"打不开保存目录：{ex.Message}",
                "CleanCapture：打不开目录",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void ToggleRecording()
    {
        if (_recording.IsRecording)
        {
            var path = _recording.Stop();
            PauseButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            RecordButton.IsEnabled = true;
            PauseButtonText.Text = "暂停";
            App.Tray.SetRecording(false, TimeSpan.Zero);
            StatusText.Text = path == null
                ? "录制已停止，但没有生成文件，请看一下磁盘空间和 FFmpeg 是否正常。"
                : $"录制好了：{System.IO.Path.GetFileName(path)}";
            // 录完把主窗口请回来，让用户看到结果；红点和呼吸动画停掉。
            RecDot.Visibility = Visibility.Collapsed;
            (FindResource("RecDotPulse") as System.Windows.Media.Animation.Storyboard)?.Stop();
            ShowWindow();
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

            target = CaptureTarget.ForWindow(picked.Handle);
        }
        else
        {
            target = CaptureTarget.ForPrimaryScreen();
        }

        if (target == null)
        {
            System.Windows.MessageBox.Show(
                "拿不到这个窗口的位置，可能是窗口关了或最小化了。\n请换个窗口试试，或者改录全屏。",
                "CleanCapture：拿不到画面",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 录之前先把“大概占多少空间”说清楚，真无损尤其要提醒。
        var width = region?.Width ?? target.Width;
        var height = region?.Height ?? target.Height;
        var audioNote = _settings.RecordSystemAudio
            ? "系统声：开启。"
            : "系统声：已关，这次录屏没有系统声。";
        audioNote += _settings.RecordMicrophone
            ? "麦克风：开启，会录人声。"
            : "麦克风：关闭，不录人声。";
        var warning = RecordingService.BuildSizeWarning(_settings, width, height);
        var answer = System.Windows.MessageBox.Show(
            $"{warning}\n\n{audioNote}\n保存到：{_settings.SaveFolder}\n\n点“确定”开始录，点“取消”就不录。",
            "开始录屏？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        // 2026-11-18：开始录之前先把主窗口藏起来（等 300ms 让它彻底消失），
        // 不然录出来的视频第一帧就是 CleanCapture 自己；停录时再请回来。
        Hide();
        await System.Threading.Tasks.Task.Delay(300);
        RunSafely(() =>
        {
            _recording.Start(target, region);
            // 麦克风开关开了但打不开（没插、被独占）：录屏照常继续，
            // 只录系统声，这里提醒一次，免得用户以为录上了人声。
            if (_recording.MicrophoneFailed)
            {
                System.Windows.MessageBox.Show(
                    "麦克风打不开（没插麦克风、被别的程序占了，或者驱动有问题），这次录屏只录系统声，没有人声。\n\n想录人声的话，检查一下麦克风再试。",
                    "麦克风打不开",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            // 开始录之后：开始按钮变灰防误点，暂停 / 停止可用，红点亮起并呼吸。
            RecordButton.IsEnabled = false;
            PauseButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            RecDot.Visibility = Visibility.Visible;
            (FindResource("RecDotPulse") as System.Windows.Media.Animation.Storyboard)?.Begin(RecDot, true);
            StatusText.Text = "正在录制 · 00:00:00";
        });
        // 万一开始录失败了（比如 FFmpeg 出问题），别把窗口一直藏着，请回来让用户看到报错，
        // 按钮状态也恢复成没录的样子。
        if (!_recording.IsRecording)
        {
            RecordButton.IsEnabled = true;
            PauseButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            RecDot.Visibility = Visibility.Collapsed;
            ShowWindow();
        }
    }

    private void OnRecordingStatusChanged(TimeSpan elapsed, string status)
    {
        Dispatcher.Invoke(() =>
        {
            // 底部状态栏：正在录制 · 00:00:00 / 已暂停 · 00:00:00。
            var label = status == "录制中" ? "正在录制" : status;
            StatusText.Text = $"{label} · {elapsed:hh\\:mm\\:ss}";
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
        RefreshHotkeyHint();
    }

    private void ShowWindow()
    {
        Show();
        Activate();
        WindowState = WindowState.Normal;
    }

    private void RefreshSizeHint()
    {
        // 预估按最终存盘的尺寸算：这里拿 1920×1080 当典型屏幕估，具体那次录多少
        // 以开始录屏时的提示为准（按实际画面尺寸算）。
        var (w, h) = _settings.EffectiveOutputSize(1920, 1080);
        var (_, text) = FFmpegService.EstimateSizePerMinute(w, h, _settings.FrameRate,
            _settings.Quality == RecordingQuality.Lossless);
        SizeHintText.Text = $"当前：{_settings.QualityDisplayName}，{_settings.FrameRate} 帧/秒，{_settings.ResolutionDisplayName}。{w}×{h} 下{text}。";
    }

    /// <summary>快捷键横条：四个键帽分别显示当前生效的键，改了设置就跟着变。</summary>
    private void RefreshHotkeyHint()
    {
        HotkeyRegionText.Text = _settings.HotkeyRegionScreenshot;
        HotkeyFullscreenText.Text = _settings.HotkeyFullscreenScreenshot;
        HotkeyRecordText.Text = _settings.HotkeyToggleRecording;
        HotkeyPauseText.Text = _settings.HotkeyPauseRecording;
    }

    /// <summary>弹一个简单的窗口列表，让用户点一个窗口。取消返回 null。</summary>
    private static WindowInfo? PickWindow(string title)
    {
        var windows = WindowListHelper.ListWindows();
        if (windows.Count == 0)
        {
            System.Windows.MessageBox.Show("现在没有能截 / 能录的窗口。", "CleanCapture", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current.MainWindow,
        };

        var list = new System.Windows.Controls.ListBox { ItemsSource = windows.Select(w => w.Title).ToList() };
        list.SelectedIndex = 0;
        list.MouseDoubleClick += (_, _) => dialog.DialogResult = true;

        var okButton = new System.Windows.Controls.Button { Content = "确定", Margin = new Thickness(0, 8, 0, 0) };
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
            System.Windows.MessageBox.Show(
                $"这一步没做成：{ex.Message}\n\n如果反复出现，先看 README 第 7 节的常见问题。",
                "CleanCapture：没做成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
