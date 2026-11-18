using System;
using System.IO;
using System.Text.Json.Serialization;

namespace CleanCapture.Models;

/// <summary>画质档：只有两档，不要再加第三档来混淆。</summary>
public enum RecordingQuality
{
    /// <summary>默认。高清近无损：H.264，CRF 16，文件小很多，肉眼几乎看不出区别。</summary>
    High,

    /// <summary>真无损：FFV1 / MKV，或 H.264 CRF 0。文件非常大，录之前一定先看预估。</summary>
    Lossless
}

/// <summary>
/// 全部设置都存在这里。文件位置见 SettingsService：
/// 程序旁边的 CleanCapture.settings.json，绿色版、换电脑直接拷走。
/// 注意：这里故意没有“开机自启”和“麦克风”两个开关，
/// 立项时已经定死：开机不自启、只录系统声、不录麦克风。
/// </summary>
public sealed class AppSettings
{
    /// <summary>保存路径，默认就是桌面。用户可以在设置窗口改成别的盘。</summary>
    public string SaveFolder { get; set; } = Environment.GetFolderPath(
        Environment.SpecialFolder.Desktop);

    /// <summary>默认画质档：高清近无损。</summary>
    public RecordingQuality Quality { get; set; } = RecordingQuality.High;

    /// <summary>帧率：30 或 60，设置窗口里二选一。</summary>
    public int FrameRate { get; set; } = 30;

    /// <summary>只录系统声。写死 true，不在界面上给麦克风开关。</summary>
    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>录屏时是否录鼠标光标。默认录，方便看操作。</summary>
    public bool ShowCursor { get; set; } = true;

    /// <summary>截图后是否自动复制到剪贴板。默认是。</summary>
    public bool CopyScreenshotToClipboard { get; set; } = true;

    // ---- 全局快捷键（默认值，设置窗口里可以改）----
    // 故意避开微信 Alt+A、QQ Ctrl+Alt+A 这些常用冲突键。
    // 如果改完发现按了没反应，先看是不是被别的软件占了，见 README 第 7 节。
    public string HotkeyRegionScreenshot { get; set; } = "Ctrl+Shift+S";
    public string HotkeyFullscreenScreenshot { get; set; } = "Ctrl+Shift+F";
    public string HotkeyToggleRecording { get; set; } = "Ctrl+Shift+R";
    public string HotkeyPauseRecording { get; set; } = "Ctrl+Shift+P";

    /// <summary>校验一遍，防止设置文件被手改坏了导致程序起不来。</summary>
    public void Normalize()
    {
        if (FrameRate != 30 && FrameRate != 60)
        {
            FrameRate = 30;
        }

        if (string.IsNullOrWhiteSpace(SaveFolder) || !Directory.Exists(SaveFolder))
        {
            SaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        // 麦克风永远不录：哪怕设置文件里被人改成别的，也在这里纠正回来。
        RecordSystemAudio = true;
    }

    [JsonIgnore]
    public string QualityDisplayName => Quality switch
    {
        RecordingQuality.Lossless => "真无损（FFV1 / MKV，文件很大）",
        _ => "高清近无损（H.264 CRF 16，默认）",
    };
}
