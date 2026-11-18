using System;
using System.IO;
using NAudio.Wave;

namespace CleanCapture.Services;

/// <summary>
/// 系统声采集：只录电脑里正在放的声音（比如视频、会议、游戏的声音）。
/// 麦克风是另一路（MicrophoneCapture），默认关闭，打开才录人声，
/// 录完两路声音由 FFmpeg 混成一路。
///
/// 原理（通俗版）：Windows 有个“环回”接口，相当于在声卡出口处接一根线，
/// 把要进音箱的声音抄一份下来。NAudio 的 WasapiLoopbackCapture 就是干这个的。
/// 如果电脑当前没有声音在放、或者没有默认播放设备，它会拿不到数据，
/// 这时录出来的 WAV 可能是空的，RecordingService 会在混流前检查并跳过混音。
/// </summary>
public sealed class SystemAudioCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _writer;

    /// <summary>录出来的临时 WAV 文件完整路径。</summary>
    public string? TempWavPath { get; private set; }

    /// <summary>是否真的采到了声音数据。</summary>
    public bool HasAudio { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>开始录系统声。tempFolder 用来放临时 WAV，一般是系统临时文件夹。</summary>
    public void Start(string tempFolder)
    {
        if (IsRunning)
        {
            return;
        }

        Directory.CreateDirectory(tempFolder);
        TempWavPath = Path.Combine(tempFolder, $"CleanCapture-audio-{DateTime.Now:yyyyMMdd-HHmmss}.wav");

        // WasapiLoopbackCapture 没有参数就是“默认播放设备”：
        // 你在 Windows 右下角喇叭图标选了哪个输出，就录哪个。
        _capture = new WasapiLoopbackCapture();
        _writer = new WaveFileWriter(TempWavPath, _capture.WaveFormat);

        _capture.DataAvailable += (_, e) =>
        {
            if (_writer == null || e.BytesRecorded == 0)
            {
                return;
            }

            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _writer.Flush();
            HasAudio = true;
        };

        _capture.RecordingStopped += (_, _) =>
        {
            IsRunning = false;
        };

        _capture.StartRecording();
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;

        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // 停止时设备被拔掉之类的情况，不用弹错，录到的部分还能用。
        }

        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
        catch
        {
            // 同上。
        }

        try
        {
            _capture?.Dispose();
            _capture = null;
        }
        catch
        {
            // 同上。
        }

        // 如果全程一丁点声音都没采到，WAV 只有文件头，混流时跳过它。
        if (TempWavPath != null && File.Exists(TempWavPath))
        {
            var info = new FileInfo(TempWavPath);
            if (info.Length < 1024)
            {
                HasAudio = false;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
