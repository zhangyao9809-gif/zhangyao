using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CleanCapture.Services;

/// <summary>
/// 画面来源：Windows Graphics Capture。
/// 这是 Win10 2004 以后系统自带的录屏接口，比老式“截屏拼视频”的办法
/// 省 CPU、不闪屏，还能只录一个窗口而不把别的窗口也录进去。
///
/// 流程（通俗版）：向系统要一个“画面会话”→ 系统每出一帧就回调一次 →
/// 我们把这一帧从显卡拷到内存 → 按 BGRA 原始格式写给 FFmpeg。
///
/// 需要的条件（csproj 里已写好）：
/// * 项目目标必须是 net8.0-windows10.0.19041.0 或更高；
/// * NuGet 包 Vortice.Windows 提供 Direct3D 11 设备。
/// 首次在 Win11 上录某个窗口时，系统可能会弹一次授权提示，点“允许”即可。
///
/// 注意：下面标着“在 VS 里核对”的两行是 WinRT 和 Vortice 之间的
/// 接口对接点，不同版本写法略有差别，见 BUILD_AND_TEST.md 第 3 节。
/// 除了这两行，整个“建会话 → 收帧 → 拷内存 → 交给 FFmpeg”的流程是完整的。
/// </summary>
public sealed class GraphicsCaptureSource : IDisposable
{
    private ID3D11Device? _d3dDevice;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFramePool? _framePool;

    /// <summary>每一帧准备好时触发：参数是 BGRA 原始字节、宽、高。</summary>
    public event Action<byte[], int, int>? FrameReady;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// 开始抓画面。item 可以是整个屏幕，也可以是一个窗口，
    /// 调用方用 GraphicsCaptureItem.CreateFromWindow / 屏幕枚举造出来再传进来。
    /// 具体怎么选屏幕 / 窗口，见 WindowListHelper 和 RecordingService。
    /// </summary>
    public void Start(GraphicsCaptureItem item)
    {
        if (IsRunning)
        {
            return;
        }

        Width = item.Size.Width;
        Height = item.Size.Height;

        // 1. 建一个 Direct3D 11 设备。BgraSupport 是必须的，
        // Graphics Capture 要求设备支持 BGRA 格式，否则建会话会失败。
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out _d3dDevice);
        result.CheckError();

        // 2. 把 Vortice 的设备包一层，变成 WinRT 能认的 IDirect3DDevice。
        // 在 VS 里核对：Vortice 2.7 的示例是把 _d3dDevice 直接传给
        // Direct3D11Helper.CreateDirect3DDevice，返回值就是下面要用的设备。
        _winrtDevice = Direct3D11Helper.CreateDirect3DDevice(_d3dDevice);

        // 3. 建帧池：一次只留 2 帧在池里，录屏时不积压、不延迟。
        _framePool = Direct3D11CaptureFramePool.Create(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        _framePool.FrameArrived += OnFrameArrived;

        // 4. 建会话并开始。IsCursorCaptureEnabled 就是“录不录鼠标光标”。
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = true;
        _session.StartCapture();
        IsRunning = true;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!IsRunning || _d3dDevice == null)
        {
            return;
        }

        using var frame = sender.TryGetNextFrame();
        if (frame == null)
        {
            return;
        }

        try
        {
            // 把这一帧的纹理从显卡拷到 CPU 能读的内存里，再按行读出来。
            // 在 VS 里核对：frame.Surface 是 WinRT 纹理，要先拿到它底层的
            // ID3D11Texture2D（Vortice 里用 Marshal.GetObjectForIUnknown 或
            // Direct3D11Helper 的对应方法取，见 BUILD_AND_TEST.md）。
            // 拿到纹理后，下面的“建暂存纹理 → CopyResource → Map → 按行拷”
            // 就是固定流程，不用再改。
            var texture = GetTextureFromSurface(frame.Surface);
            if (texture == null)
            {
                return;
            }

            var desc = texture.Description;
            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            using var staging = _d3dDevice.CreateTexture2D(stagingDesc);
            var context = _d3dDevice.ImmediateContext;
            context.CopyResource(texture, staging);

            var mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var width = desc.Width;
                var height = desc.Height;
                var bytes = new byte[width * height * 4];

                // 显卡里每一行的字节数（RowPitch）可能比画面宽×4 多几个字节，
                // 所以要一行一行拷，不能整个内存一把拷过去。
                unsafe
                {
                    var src = (byte*)mapped.DataPointer;
                    for (var y = 0; y < height; y++)
                    {
                        var srcRow = src + y * mapped.RowPitch;
                        var dstOffset = y * width * 4;
                        for (var x = 0; x < width * 4; x++)
                        {
                            bytes[dstOffset + x] = srcRow[x];
                        }
                    }
                }

                FrameReady?.Invoke(bytes, width, height);
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        catch
        {
            // 某一帧坏了就丢掉这一帧，不要把整个录制拖死，下一帧还会继续来。
        }
    }

    /// <summary>
    /// 从 WinRT 纹理取到底层的 D3D11 纹理。
    /// 实现方式取决于 Vortice 版本：一种是用 Marshal 把 IUnknown 包成
    /// ID3D11Texture2D，另一种是用 Direct3D11Helper 的取接口方法。
    /// 在 VS 里第一次编译时，先按 Vortice 2.7 自带的 Graphics Capture
    /// 示例核对这一处，核对通过后这里就不用再动了。
    /// </summary>
    private static ID3D11Texture2D? GetTextureFromSurface(IDirect3DSurface surface)
    {
        // surface 本质上就是一个 COM 对象，底层就是 ID3D11Texture2D。
        // Marshal.GetObjectForIUnknown 会按 COM 接口把它包回来。
        var unknown = Marshal.GetIUnknownForObject(surface);
        try
        {
            var obj = Marshal.GetObjectForIUnknown(unknown);
            return obj as ID3D11Texture2D;
        }
        finally
        {
            Marshal.Release(unknown);
        }
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
            _session?.Dispose();
            _session = null;
        }
        catch
        {
            // 停止时不弹错。
        }

        try
        {
            if (_framePool != null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
                _framePool = null;
            }
        }
        catch
        {
            // 同上。
        }

        _winrtDevice?.Dispose();
        _winrtDevice = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
