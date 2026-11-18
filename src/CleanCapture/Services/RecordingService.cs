using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CleanCapture.Models;
using Windows.Graphics.Capture;

namespace CleanCapture.Services;

/// <summary>
/// 录屏总指挥：把“画面 + 系统声 + FFmpeg”三件事串起来。
/// 开始 → 暂停 → 继续 → 停止，四个状态都在这里管。
///
/// 真实流程：
/// 1. 开始：GraphicsCaptureSource 抓画面 → 一帧帧写进 FFmpeg 的标准输入；
///    同时 SystemAudioCapture 用 NAudio 环回录系统声，写成临时 WAV。
/// 2. 暂停：不再给 FFmpeg 写新帧（FFmpeg 进程先留着），计时也停表；
///    恢复时继续写帧。注意：暂停期间画面时间会断一下，这是 MVP 的取舍，
///    换来的是实现简单、不容易把 FFmpeg 写死。
/// 3. 停止：关画面源 → 关 FFmpeg（等它把尾巴写完）→
///    如果有系统声，就用 FFmpeg 把“无声视频 + WAV”合成最终文件；
///    没声音就直接把视频改成最终文件名。临时文件随后删掉。
///
/// 文件名：最终文件在桌面，临时文件在系统临时文件夹，录完就删，不留垃圾。
/// </summary>
public sealed class RecordingService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _lock = new();

    private GraphicsCaptureSource? _captureSource;
    private SystemAudioCapture? _audioCapture;
    private Process? _ffmpeg;
    private string? _ffmpegPath;
    private string? _tempVideoPath;
    private string? _tempFolder;
    private string? _finalPath;

    private CancellationTokenSource? _timerCts;
    private TimeSpan _elapsed;
    private DateTime _segmentStart;
    private bool _isPaused;

    public bool IsRecording { get; private set; }

    public bool IsPaused => _isPaused;

    public TimeSpan Elapsed => _elapsed + (IsRecording && !_isPaused
        ? DateTime.Now - _segmentStart
        : TimeSpan.Zero);

    /// <summary>每秒触发一次，主窗口用它刷新“已录 00:12”这种显示。</summary>
    public event Action<TimeSpan, string>? StatusChanged;

    public RecordingService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// 录之前先算一遍大概占多少空间，返回给界面弹提示。
    /// 用户点“知道了，开始录”才真正开始，点取消就不录。
    /// </summary>
    public static string BuildSizeWarning(AppSettings settings, int width, int height)
    {
        var lossless = settings.Quality == RecordingQuality.Lossless;
        var (_, text) = FFmpegService.EstimateSizePerMinute(width, height, settings.FrameRate, lossless);
        var total = lossless
            ? "录 10 分钟大概要几个 GB，录 30 分钟前请先看一眼 C 盘 / 桌面所在盘还剩多少空间。"
            : "录 30 分钟一般在 1–2 GB 左右，桌面空间不够时请先改保存路径。";
        return $"{settings.QualityDisplayName}\n{text}\n{total}";
    }

    /// <summary>
    /// 开始录屏。item 是画面来源（全屏 / 某个窗口），region 是区域录屏时的矩形。
    /// 区域录屏的做法：先抓全屏，再让 FFmpeg 按 region 裁剪——
    /// 这里为了 MVP 简单，先只支持“全屏”和“窗口”两种 item，
    /// 区域录屏由调用方先弹出 RegionSelectorWindow 选好矩形，
    /// 再把矩形对应的屏幕建成 GraphicsCaptureItem 传进来（见 MainWindow）。
    /// </summary>
    public void Start(GraphicsCaptureItem item, System.Drawing.Rectangle? crop = null)
    {
        lock (_lock)
        {
            if (IsRecording)
            {
                return;
            }

            if (!FFmpegService.TryFindFFmpeg(out _ffmpegPath))
            {
                throw new InvalidOperationException(
                    "没有找到 ffmpeg.exe。请按 README 第 4 节下载后放在 CleanCapture.exe 旁边，再试一次。");
            }

            _tempFolder = Path.Combine(Path.GetTempPath(), "CleanCapture");
            Directory.CreateDirectory(_tempFolder);

            var lossless = _settings.Quality == RecordingQuality.Lossless;
            var videoExt = lossless ? "mkv" : "mp4";
            var tempVideoName = $"temp-video-{DateTime.Now:yyyyMMdd-HHmmss}.{videoExt}";
            _tempVideoPath = Path.Combine(_tempFolder, tempVideoName);

            var finalExt = lossless ? "mkv" : "mp4";
            _finalPath = Path.Combine(
                _settings.SaveFolder,
                SettingsService.MakeFileName("recording", finalExt));

            // 1. 起 FFmpeg：画面从标准输入流进来。
            var args = FFmpegService.BuildVideoArguments(
                item.Size.Width, item.Size.Height, _settings.FrameRate, lossless, _tempVideoPath, crop);
            _ffmpeg = FFmpegService.Start(_ffmpegPath, args);

            // 2. 起系统声（只录系统声，不开麦克风）。
            if (_settings.RecordSystemAudio)
            {
                try
                {
                    _audioCapture = new SystemAudioCapture();
                    _audioCapture.Start(_tempFolder);
                }
                catch
                {
                    // 没声卡、没默认播放设备时，录屏还能继续，只是没声音。
                    _audioCapture = null;
                }
            }

            // 3. 起画面源：每一帧都写进 FFmpeg。
            _captureSource = new GraphicsCaptureSource();
            _captureSource.FrameReady += OnFrameReady;
            _captureSource.Start(item);

            IsRecording = true;
            _isPaused = false;
            _elapsed = TimeSpan.Zero;
            _segmentStart = DateTime.Now;

            // 4. 起计时器：每秒报一次时长和状态。
            _timerCts = new CancellationTokenSource();
            _ = TickLoopAsync(_timerCts.Token);
        }
    }

    private void OnFrameReady(byte[] frame, int width, int height)
    {
        // 暂停时直接丢帧，不写 FFmpeg；恢复后继续写。
        if (_isPaused || _ffmpeg == null || _ffmpeg.HasExited)
        {
            return;
        }

        try
        {
            var stdin = _ffmpeg.StandardInput.BaseStream;
            stdin.Write(frame, 0, frame.Length);
            stdin.Flush();
        }
        catch
        {
            // FFmpeg 挂了（比如磁盘满了），这里不要抛到画面回调里，
            // 主窗口的计时循环会发现进程已退出并提示用户。
        }
    }

    private async Task TickLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && IsRecording)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                var status = _isPaused ? "已暂停" : "录制中";
                if (_ffmpeg is { HasExited: true })
                {
                    status = "FFmpeg 已退出，可能是磁盘满了或编码出错";
                }

                StatusChanged?.Invoke(Elapsed, status);
            }
        }
        catch (TaskCanceledException)
        {
            // 停止时取消计时器，是正常情况。
        }
    }

    public void Pause()
    {
        if (!IsRecording || _isPaused)
        {
            return;
        }

        _elapsed += DateTime.Now - _segmentStart;
        _isPaused = true;
        StatusChanged?.Invoke(_elapsed, "已暂停");
    }

    public void Resume()
    {
        if (!IsRecording || !_isPaused)
        {
            return;
        }

        _segmentStart = DateTime.Now;
        _isPaused = false;
        StatusChanged?.Invoke(Elapsed, "录制中");
    }

    /// <summary>
    /// 停止并返回最终文件路径。混流失败时返回临时视频路径，不会把成片弄丢。
    /// </summary>
    public string? Stop()
    {
        lock (_lock)
        {
            if (!IsRecording)
            {
                return null;
            }

            IsRecording = false;
            _timerCts?.Cancel();
            _timerCts?.Dispose();
            _timerCts = null;

            if (_isPaused)
            {
                _isPaused = false;
            }
            else
            {
                _elapsed += DateTime.Now - _segmentStart;
            }

            // 先停画面，再停声音，最后关 FFmpeg：顺序不能反，
            // 反了可能最后一两帧写不进去。
            try
            {
                _captureSource?.Stop();
            }
            catch
            {
                // 同上。
            }

            try
            {
                _audioCapture?.Stop();
            }
            catch
            {
                // 同上。
            }

            try
            {
                var stdin = _ffmpeg?.StandardInput.BaseStream;
                stdin?.Flush();
                stdin?.Close();
            }
            catch
            {
                // 同上。
            }

            try
            {
                // 给 FFmpeg 最多 10 秒把尾巴写完，写不完就杀掉，别卡住界面。
                if (_ffmpeg is { HasExited: false })
                {
                    if (!_ffmpeg.WaitForExit(10_000))
                    {
                        _ffmpeg.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
                // 同上。
            }

            var result = FinishMux();
            CleanupTempFiles();
            return result;
        }
    }

    /// <summary>把临时视频 + 临时 WAV 合成最终文件；没声音就直接改名。</summary>
    private string? FinishMux()
    {
        if (_tempVideoPath == null || !File.Exists(_tempVideoPath) || _finalPath == null)
        {
            return null;
        }

        // 临时视频本身已经是能播的文件：没声音时直接挪到桌面就行。
        var hasAudio = _audioCapture is { HasAudio: true }
            && _audioCapture.TempWavPath is { } wav
            && File.Exists(wav);

        if (!hasAudio)
        {
            try
            {
                Directory.CreateDirectory(_settings.SaveFolder);
                File.Move(_tempVideoPath, _finalPath);
                return _finalPath;
            }
            catch
            {
                return _tempVideoPath;
            }
        }

        // 有声音：再跑一次 FFmpeg 混流，画面不重新编码，速度很快。
        var muxArgs = FFmpegService.BuildMuxArguments(
            _tempVideoPath, _audioCapture!.TempWavPath!, _finalPath);
        var ok = FFmpegService.RunOnce(_ffmpegPath ?? "ffmpeg.exe", muxArgs);
        if (ok && File.Exists(_finalPath))
        {
            return _finalPath;
        }

        // 混流失败也别丢成片：把无声视频留下来，用户至少还有画面。
        return _tempVideoPath;
    }

    private void CleanupTempFiles()
    {
        try
        {
            if (_tempVideoPath != null && File.Exists(_tempVideoPath)
                && _finalPath != null && File.Exists(_finalPath)
                && !string.Equals(_tempVideoPath, _finalPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(_tempVideoPath);
            }
        }
        catch
        {
            // 临时文件删不掉也不影响成片，下次启动系统自己会清临时文件夹。
        }

        try
        {
            if (_audioCapture?.TempWavPath is { } wav && File.Exists(wav))
            {
                File.Delete(wav);
            }
        }
        catch
        {
            // 同上。
        }

        _captureSource?.Dispose();
        _captureSource = null;
        _audioCapture?.Dispose();
        _audioCapture = null;
        _ffmpeg?.Dispose();
        _ffmpeg = null;
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            Stop();
        }

        _captureSource?.Dispose();
        _audioCapture?.Dispose();
        _ffmpeg?.Dispose();
    }
}
