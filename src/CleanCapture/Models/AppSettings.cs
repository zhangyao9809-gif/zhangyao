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

/// <summary>录屏分辨率档：原画不缩放；1080p / 720p 把画面缩小再存，文件更小。</summary>
public enum RecordingResolution
{
    /// <summary>默认：跟屏幕 / 框选区域的原始分辨率一样，不缩放。</summary>
    Original,

    /// <summary>高缩到 1080，宽按比例跟着缩（1920×1080 的屏就是原样）。</summary>
    P1080,

    /// <summary>高缩到 720，宽按比例跟着缩。</summary>
    P720
}

/// <summary>
/// 全部设置都存在这里。文件位置见 SettingsService：
/// 程序旁边的 CleanCapture.settings.json，绿色版、换电脑直接拷走。
/// 注意：这里故意没有“开机自启”开关，立项时已经定死：开机不自启。
/// 麦克风开关 2026-11-18 加上：用户决定要，默认关闭，打开才录人声。
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

    /// <summary>录屏分辨率：原画 / 1080p / 720p，设置窗口里三选一。</summary>
    public RecordingResolution Resolution { get; set; } = RecordingResolution.Original;

    /// <summary>
    /// 系统声音开关：默认开。主窗口的“系统声音”开关可以关掉它（这次录屏就没声音）。
    /// </summary>
    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>
    /// 麦克风开关：默认关。2026-11-18 用户决定加上，UI 上默认也是关的；
    /// 打开后才录人声，和系统声混成一路。关着时录出来和以前一样，只有系统声。
    /// </summary>
    public bool RecordMicrophone { get; set; } = false;

    /// <summary>录屏时是否录鼠标光标。默认录，方便看操作。</summary>
    public bool ShowCursor { get; set; } = true;

    /// <summary>截图后是否自动复制到剪贴板。默认是。</summary>
    public bool CopyScreenshotToClipboard { get; set; } = true;

    // ---- 全局快捷键（默认值，设置窗口里可以改）----
    // 2026-11-18 改过一次：原来默认的 Ctrl+Shift+S / F / R / P
    // 在用户电脑上注册失败（错误码 1409，被别的软件占了），
    // 所以改成 Ctrl+Alt+S / F / R / P。微信 Alt+A、QQ Ctrl+Alt+A
    // 本来就避开了；如果这组也被占了，去设置里换一个组合键，
    // 比如 Ctrl+Alt+Shift+S。改完记得重启程序再试。
    public string HotkeyRegionScreenshot { get; set; } = "Ctrl+Alt+S";
    public string HotkeyFullscreenScreenshot { get; set; } = "Ctrl+Alt+F";
    public string HotkeyToggleRecording { get; set; } = "Ctrl+Alt+R";
    public string HotkeyPauseRecording { get; set; } = "Ctrl+Alt+P";

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

        // 注意：系统声音开关不再这里强制 true 了——主窗口的开关是真的，
        // 关了就是关了；麦克风开关默认关，打开才录人声。

        // 分辨率档只有三档，设置文件被手改坏了就回默认原画。
        if (!Enum.IsDefined(Resolution))
        {
            Resolution = RecordingResolution.Original;
        }

        // 2026-11-18 快捷键迁移：老版本默认的 Ctrl+Shift+S / F / R / P
        // 在用户电脑上已被别的软件占用（注册失败 1409），新默认改成
        // Ctrl+Alt+S / F / R / P。已经存下来的设置文件里如果还是
        // 老的默认值，启动时自动换成新的；用户自己改过的键不动。
        HotkeyRegionScreenshot = MigrateHotkey(HotkeyRegionScreenshot, "Ctrl+Shift+S", "Ctrl+Alt+S");
        HotkeyFullscreenScreenshot = MigrateHotkey(HotkeyFullscreenScreenshot, "Ctrl+Shift+F", "Ctrl+Alt+F");
        HotkeyToggleRecording = MigrateHotkey(HotkeyToggleRecording, "Ctrl+Shift+R", "Ctrl+Alt+R");
        HotkeyPauseRecording = MigrateHotkey(HotkeyPauseRecording, "Ctrl+Shift+P", "Ctrl+Alt+P");
    }

    private static string MigrateHotkey(string current, string oldDefault, string newDefault)
    {
        // 设置文件里缺了这一项（null 或空）时直接给新默认，别留 null，
        // 否则每次启动都会弹“快捷键写错了”。
        if (string.IsNullOrWhiteSpace(current))
        {
            return newDefault;
        }

        return string.Equals(current.Trim(), oldDefault, StringComparison.OrdinalIgnoreCase)
            ? newDefault
            : current;
    }

    [JsonIgnore]
    public string QualityDisplayName => Quality switch
    {
        RecordingQuality.Lossless => "真无损（FFV1 / MKV，文件很大）",
        _ => "高清近无损（H.264 CRF 16，默认）",
    };

    [JsonIgnore]
    public string ResolutionDisplayName => Resolution switch
    {
        RecordingResolution.P1080 => "1080p",
        RecordingResolution.P720 => "720p",
        _ => "原画",
    };

    /// <summary>
    /// 这一路录制最终存盘的高：选了 1080p / 720p 且画面比它高时才缩小，
    /// 画面本来就比它矮（比如 720p 的屏选 1080p）不放大，直接返回 null。
    /// </summary>
    public int? OutputHeightFor(int sourceHeight)
    {
        var target = Resolution switch
        {
            RecordingResolution.P1080 => 1080,
            RecordingResolution.P720 => 720,
            _ => 0,
        };
        return target > 0 && sourceHeight > target ? target : null;
    }

    /// <summary>按分辨率档算最终存盘的宽高（宽按比例缩，并取整到偶数）。</summary>
    public (int Width, int Height) EffectiveOutputSize(int width, int height)
    {
        var targetHeight = OutputHeightFor(height);
        if (targetHeight == null)
        {
            return (width / 2 * 2, height / 2 * 2);
        }

        var h = targetHeight.Value;
        var w = (int)Math.Round(width * (h / (double)height)) / 2 * 2;
        return (w, h);
    }
}
