using System;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using CleanCapture.Models;
using CleanCapture.Services;

namespace CleanCapture;

/// <summary>
/// 极简设置窗口：只放 MVP 必须的几项。
/// 保存后立即写盘，并重新注册快捷键；下次打开程序直接用新设置。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = App.Settings.Current;

        SaveFolderBox.Text = _settings.SaveFolder;
        QualityBox.SelectedIndex = _settings.Quality == RecordingQuality.Lossless ? 1 : 0;
        FrameRateBox.SelectedIndex = _settings.FrameRate == 60 ? 1 : 0;
        ShowCursorBox.IsChecked = _settings.ShowCursor;
        CopyShotBox.IsChecked = _settings.CopyScreenshotToClipboard;
        HotkeyRegionBox.Text = _settings.HotkeyRegionScreenshot;
        HotkeyFullscreenBox.Text = _settings.HotkeyFullscreenScreenshot;
        HotkeyRecordBox.Text = _settings.HotkeyToggleRecording;
        HotkeyPauseBox.Text = _settings.HotkeyPauseRecording;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选一个文件夹，截图和录屏都存到这里",
            SelectedPath = SaveFolderBox.Text,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            && Directory.Exists(dialog.SelectedPath))
        {
            SaveFolderBox.Text = dialog.SelectedPath;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 快捷键先校验一遍，写错了就留在这个窗口让用户改，不直接关掉。
        var hotkeys = new[]
        {
            (HotkeyRegionBox.Text, "区域截图"),
            (HotkeyFullscreenBox.Text, "全屏截图"),
            (HotkeyRecordBox.Text, "开始 / 停止录屏"),
            (HotkeyPauseBox.Text, "暂停 / 继续"),
        };
        foreach (var (text, name) in hotkeys)
        {
            if (!HotkeyService.TryParse(text, out _, out _))
            {
                System.Windows.MessageBox.Show(
                    $"“{name}”的快捷键“{text}”写错了，请改成像 Ctrl+Shift+S 这样的写法。",
                    "CleanCapture：快捷键写错了",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        _settings.SaveFolder = SaveFolderBox.Text;
        _settings.Quality = QualityBox.SelectedIndex == 1 ? RecordingQuality.Lossless : RecordingQuality.High;
        _settings.FrameRate = FrameRateBox.SelectedIndex == 1 ? 60 : 30;
        _settings.ShowCursor = ShowCursorBox.IsChecked == true;
        _settings.CopyScreenshotToClipboard = CopyShotBox.IsChecked == true;
        _settings.HotkeyRegionScreenshot = HotkeyRegionBox.Text.Trim();
        _settings.HotkeyFullscreenScreenshot = HotkeyFullscreenBox.Text.Trim();
        _settings.HotkeyToggleRecording = HotkeyRecordBox.Text.Trim();
        _settings.HotkeyPauseRecording = HotkeyPauseBox.Text.Trim();
        _settings.Normalize();

        App.Settings.Save();

        // 快捷键改了要重新注册：先注销旧的，再按新设置注册。
        try
        {
            App.Hotkeys.UnregisterAll();
            App.Hotkeys.RegisterAll(_settings);
        }
        catch
        {
            // 注册失败时 HotkeyService 内部已经弹过提示了，这里不再重复弹。
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
