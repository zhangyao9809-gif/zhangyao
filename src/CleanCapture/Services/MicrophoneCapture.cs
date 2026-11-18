using System;
using System.IO;
using NAudio.Wave;

namespace CleanCapture.Services;

/// <summary>
/// 麦克风采集：录人声（对着麦克风说话的声音），和系统声分开录成另一个 WAV，
/// 录完再由 FFmpeg 把两路声音混成一路（见 RecordingService.FinishMux）。
///
/// 2026-11-18 加上的：立项时定的是“麦克风永远不录”，后来用户决定要这个开关，
/// 默认关闭，打开才录。开关在主窗口“录屏”卡片里，和系统声音开关并排。
///
/// 原理（通俗版）：WaveInEvent 就是 Windows 自带的录音接口，
/// 用默认麦克风（系统声音设置里选的那个输入设备）录音。
/// 如果电脑没有麦克风、或者麦克风被别的程序独占了，
/// StartRecording 会直接抛错，RecordingService 会接住并提示用户，
/// 这次录屏就只录系统声，不会把整个录屏搞崩。
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private WaveInEvent? _capture;
    private WaveFileWriter? _writer;

    /// <summary>录出来的临时 WAV 文件完整路径。</summary>
    public string? TempWavPath { get; private set; }

    /// <summary>是否真的采到了声音数据。</summary>
    public bool HasAudio { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>开始录麦克风。tempFolder 用来放临时 WAV，一般是系统临时文件夹。</summary>
    public void Start(string tempFolder)
    {
        if (IsRunning)
        {
            return;
        }

        Directory.CreateDirectory(tempFolder);
        TempWavPath = Path.Combine(tempFolder, $"CleanCapture-mic-{DateTime.Now:yyyyMMdd-HHmmss}.wav");

        // 44.1kHz、16 位、单声道：人声够用了，文件也小。
        // 设备号 -1 就是“默认麦克风”，和系统声音设置里选的输入设备一致。
        _capture = new WaveInEvent
        {
            DeviceNumber = -1,
            WaveFormat = new WaveFormat(44100, 16, 1),
            BufferMilliseconds = 100,
        };
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

        // 没有麦克风、被独占、驱动出问题时，这里会直接抛错，
        // 调用方（RecordingService）接住后按“这次不录麦克风”处理。
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
