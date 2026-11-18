using System;
using System.Diagnostics;
using System.IO;

namespace CleanCapture.Services;

/// <summary>
/// FFmpeg 的封装：只负责三件事——找到 ffmpeg.exe、拼命令行、起进程。
/// 本程序不内置 FFmpeg（它个头大、许可证也要求分开发布），
/// 第一次用之前要按 README 第 4 节下载一次，放在 exe 旁边。
/// </summary>
public static class FFmpegService
{
    /// <summary>找 ffmpeg 的顺序：exe 旁边 → exe 旁边的 ffmpeg\bin → 系统 PATH。</summary>
    public static bool TryFindFFmpeg(out string ffmpegPath)
    {
        var baseDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
            "ffmpeg.exe",
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate == "ffmpeg.exe")
                {
                    // 走 PATH：能起起来、能打出版本号，才算找到。
                    var probe = StartProbe(candidate);
                    if (probe)
                    {
                        ffmpegPath = candidate;
                        return true;
                    }
                }
                else if (File.Exists(candidate))
                {
                    ffmpegPath = candidate;
                    return true;
                }
            }
            catch
            {
                // 某一路徑不可用就试下一条，不要直接报错。
            }
        }

        ffmpegPath = string.Empty;
        return false;
    }

    private static bool StartProbe(string ffmpegPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 录屏用的 FFmpeg 命令行。
    /// 输入是原始画面（rawvideo，BGRA，宽×高×帧率），从标准输入流进来；
    /// 音频另外单独录成 WAV（见 SystemAudioCapture），录完再混流，见 RecordingService。
    /// 这样做的好处：画面和声音各走各的路，任何一路出问题都不会把另一路拖死。
    /// outputHeight：选了 1080p / 720p 时的目标高（null = 原画不缩放）。
    /// </summary>
    public static string BuildVideoArguments(
        int width,
        int height,
        int frameRate,
        bool lossless,
        string outputPath,
        System.Drawing.Rectangle? crop = null,
        int? outputHeight = null)
    {
        // -y：同名文件直接覆盖，不停下来问“要不要覆盖”。
        // -f rawvideo -pix_fmt bgra：告诉 FFmpeg，进来的就是一帧帧的原始 BGRA 画面。
        // -s WxH -r 帧率：画面尺寸和帧率要和 GdiCaptureSource 送进来的一致。
        // GDI 位图是 32 位 ARGB，内存里的字节顺序就是 BGRA，和这里的 pix_fmt bgra 是同一套。
        var head = $"-y -f rawvideo -pix_fmt bgra -s {width}x{height} -r {frameRate} -i - ";

        // 滤镜链（按顺序拼，用逗号连成一个 -vf）：
        // 1. 区域录屏：先抓全屏，再用 crop 裁成用户框的那一块。
        //    crop 的宽高必须是偶数（H.264 要求），这里先取整到偶数再拼进去。
        // 2. 分辨率档：选了 1080p / 720p 且画面比它高时，再用 scale 缩小。
        //    scale=-2:高：宽按比例自动算，-2 保证宽是偶数；画面本来就比目标矮时不缩。
        var filters = new System.Collections.Generic.List<string>();
        var effectiveHeight = height;
        if (crop is { Width: > 0, Height: > 0 })
        {
            var w = crop.Value.Width / 2 * 2;
            var h = crop.Value.Height / 2 * 2;
            var x = crop.Value.X / 2 * 2;
            var y = crop.Value.Y / 2 * 2;
            filters.Add($"crop={w}:{h}:{x}:{y}");
            effectiveHeight = h;
        }

        if (outputHeight is { } target && effectiveHeight > target)
        {
            filters.Add($"scale=-2:{target}");
        }

        var filter = filters.Count == 0 ? string.Empty : $" -vf \"{string.Join(",", filters)}\" ";

        string codecPart;
        if (lossless)
        {
            // 真无损档：FFV1，MKV 容器。画质一点不丢，文件非常大。
            // -level 3 是 FFV1 的常规等级；-pix_fmt bgra 保留原始色彩。
            // 注意：这里不要写 -crf，FFV1 没有 CRF 这个概念。
            codecPart = "-c:v ffv1 -level 3 -pix_fmt bgra";
        }
        else
        {
            // 默认高清档：H.264，CRF 16。CRF 越小越清楚、文件越大；
            // 16 是“肉眼几乎看不出压缩痕迹，又比无损小得多”的平衡点。
            // -preset veryfast：编码快、CPU 占用低，录屏时不容易卡；
            // -pix_fmt yuv420p：绝大多数播放器都能直接播；
            // -movflags +faststart：播的时候不用等整个文件下完。
            codecPart = "-c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p -movflags +faststart";
        }

        return $"{head}{filter} {codecPart} \"{outputPath}\"";
    }

    /// <summary>录完后把“只有画面的视频 + 只有系统声的 WAV”合成一个文件。</summary>
    public static string BuildMuxArguments(string videoPath, string audioPath, string finalPath)
    {
        // -c:v copy：画面不重新编码，一秒就能合完，也不会再损失画质。
        // -c:a aac -b:a 192k：系统声转成 AAC，192k 足够清楚。
        // -shortest：以短的那一路为准，防止声音比画面长出一截。
        return $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac -b:a 192k -shortest \"{finalPath}\"";
    }

    /// <summary>
    /// 录完后把“只有画面的视频 + 系统声 WAV + 麦克风 WAV”合成一个文件。
    /// 两路声音先统一成 48kHz 立体声，再用 amix 混成一路：
    /// normalize=0 是不把音量压小（默认会把每路除以路数，听着变轻），
    /// 两路同时很响时可能轻微削波，平时说话 + 系统声不会到那个程度。
    /// </summary>
    public static string BuildMuxArguments(string videoPath, string systemAudioPath, string micAudioPath, string finalPath)
    {
        // [1:a] 是系统声，[2:a] 是麦克风；先各自重采样、转立体声，
        // 再混成一路 [a]，画面照样 -c:v copy 不重新编码。
        const string filter =
            "[1:a]aresample=48000,aformat=channel_layouts=stereo[a1];" +
            "[2:a]aresample=48000,aformat=channel_layouts=stereo[a2];" +
            "[a1][a2]amix=inputs=2:duration=longest:normalize=0[a]";
        return $"-y -i \"{videoPath}\" -i \"{systemAudioPath}\" -i \"{micAudioPath}\" " +
               $"-filter_complex \"{filter}\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest \"{finalPath}\"";
    }

    /// <summary>真无损那一路没有声音时用的混流：直接把视频改名/换容器，不重新编码。</summary>
    public static string BuildRemuxArguments(string videoPath, string finalPath)
    {
        return $"-y -i \"{videoPath}\" -c copy \"{finalPath}\"";
    }

    /// <summary>起一个 FFmpeg 进程，标准输入留给调用方写画面数据。</summary>
    public static Process Start(string ffmpegPath, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        // FFmpeg 的进度、报错都走标准错误流；这里先接住不让它堵死，
        // 真正要看日志时再在 RecordingService 里把最后一行记下来。
        process.ErrorDataReceived += (_, _) => { };
        process.Start();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>跑一条一次性的 FFmpeg 命令（比如录完混流），等它跑完，返回是否成功。</summary>
    public static bool RunOnce(string ffmpegPath, string arguments, int timeoutMs = 120_000)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.Start();
            var error = process.StandardError.ReadToEnd();
            var exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 杀不掉也不再报错，调用方按失败处理即可。
                }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 录之前给用户看的预估：只算个大概，别当成精确值。
    /// 真无损按“原始画面的 15%”估，高清按“每像素每帧约 0.12 字节”估，
    /// 画面越静止越小、越动越大。单位是 MB / 分钟。
    /// </summary>
    public static (double MegabytesPerMinute, string Text) EstimateSizePerMinute(
        int width, int height, int frameRate, bool lossless)
    {
        double bytesPerMinute;
        if (lossless)
        {
            // 原始一帧 = 宽×高×4 字节（BGRA），乘帧率、乘 60 秒，再打 15% 的折。
            bytesPerMinute = width * (double)height * 4 * frameRate * 60 * 0.15;
        }
        else
        {
            bytesPerMinute = width * (double)height * frameRate * 60 * 0.12;
        }

        var mb = bytesPerMinute / 1024 / 1024;
        var label = lossless ? "真无损" : "高清近无损";
        return (mb, $"{label}约 {mb:F0} MB / 分钟（大概值，画面越动越大）");
    }
}
