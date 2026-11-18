using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace CleanCapture.Services;

/// <summary>
/// 录屏要录哪一块：全屏就是主屏幕的整块矩形，窗口就是那个窗口
/// 在屏幕上的位置。宽高在造出来的时候就取整到偶数，
/// 因为后面的 H.264 编码要求宽高都是偶数，奇数会直接报错。
/// </summary>
public sealed record CaptureTarget(Rectangle Bounds, IntPtr WindowHandle, bool IsWindow)
{
    public int Width => Bounds.Width;

    public int Height => Bounds.Height;

    /// <summary>全屏：主屏幕的整块矩形。</summary>
    public static CaptureTarget ForPrimaryScreen()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? new Rectangle(0, 0, 1920, 1080);
        return new CaptureTarget(Normalize(bounds), IntPtr.Zero, false);
    }

    /// <summary>
    /// 窗口：按窗口在屏幕上的位置截。窗口关了、最小化了
    /// 或拿不到位置时返回 null，调用方提示用户换个窗口或改录全屏。
    /// </summary>
    public static CaptureTarget? ForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var bounds = Normalize(new Rectangle(rect.Left, rect.Top, width, height));
        if (bounds.Width < 2 || bounds.Height < 2)
        {
            return null;
        }

        return new CaptureTarget(bounds, hwnd, true);
    }

    /// <summary>宽高向下取整到偶数；小于 2 像素的矩形没法录。</summary>
    private static Rectangle Normalize(Rectangle bounds)
    {
        return new Rectangle(bounds.X, bounds.Y, bounds.Width / 2 * 2, bounds.Height / 2 * 2);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}

/// <summary>
/// 画面来源：GDI 逐帧截图供帧。
///
/// 2026-11-18 修复说明（选了 B 路线，去 Vortice，不换包）：
/// 云端编译报错 NU1101：csproj 里写的 Vortice.Windows 2.7.0
/// 在 NuGet 上根本不存在——Vortice.Windows 只是项目总名，
/// 真正的包是 Vortice.Direct3D11 之类。换包（A 路线）能过 NU1101，
/// 但 GraphicsCaptureSource 里 WinRT 和 Vortice 之间的接口写法
/// （D3D11CreateDevice 的重载、Direct3D11Helper.CreateDirect3DDevice、
/// 从 Surface 取 ID3D11Texture2D）有两三处在没有 Windows 编译的
/// 情况下核对不了，硬换很可能再报一次编译错。
/// 所以改走 GDI：只用本项目截图里已经在用的 CopyFromScreen，
/// 不引任何新包，编译通过的把握最大。
///
/// 做法（通俗版）：按帧率（30 或 60 帧/秒）定时截一张屏幕图，
/// 把每一帧按 BGRA 原始字节写给 FFmpeg。复用的是
/// ScreenshotService.cs 里已经验证过的思路，只是从“截一张”
/// 改成“不停地截、按时送出去”。
///
/// 取舍要说清楚：
/// * CPU 会比 Graphics Capture 高一点，60 帧 1080p 下风扇转起来
///   是正常的；觉得卡就改回 30 帧，自用教程录屏够用。
/// * 窗口录制是按窗口在屏幕上的位置截的：被别的窗口盖住的部分，
///   录到的是盖在上面的那个窗口，不是被盖住的窗口本身。
///   要录的窗口先把它移到最前面、别被盖住。
/// * 鼠标光标是单独画上去的（CopyFromScreen 本身不带光标），
///   见 CaptureFrame 里画光标那几行。
///
/// 帧格式：Bitmap 用 Format32bppArgb，内存里的字节顺序就是
/// B、G、R、A，和 FFmpeg 要的 bgra 是同一套，可以直接送过去，
/// 不用再转格式。
/// </summary>
public sealed class GdiCaptureSource : IDisposable
{
    /// <summary>每一帧准备好时触发：参数是 BGRA 原始字节、宽、高。</summary>
    public event Action<byte[], int, int>? FrameReady;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsRunning { get; private set; }

    private CaptureTarget? _target;
    private int _frameRate = 30;
    private bool _showCursor;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>
    /// 开始抓画面。target 是全屏还是某个窗口，见 CaptureTarget。
    /// 区域录屏时 target 仍是全屏，裁剪交给 FFmpeg（见 RecordingService），
    /// 和原来 Graphics Capture 的做法一样。
    /// </summary>
    public void Start(CaptureTarget target, int frameRate, bool showCursor)
    {
        if (IsRunning)
        {
            return;
        }

        if (target.Width < 2 || target.Height < 2)
        {
            throw new ArgumentException("录制区域宽高必须大于 0。");
        }

        _target = target;
        Width = target.Width;
        Height = target.Height;
        _frameRate = frameRate is 30 or 60 ? frameRate : 30;
        _showCursor = showCursor;

        _running = true;
        IsRunning = true;

        // 抓画面放在后台线程上跑，不占 UI 线程，界面不会卡。
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "CleanCapture-GDI-Capture",
        };
        _thread.Start();
    }

    /// <summary>
    /// 按固定节拍截帧：每 1000/帧率 毫秒截一张。截一张花的时间
    /// 如果超过一拍，就直接跳到下一拍，不积压、不追帧，
    /// 免得越录越延迟。
    /// </summary>
    private void CaptureLoop()
    {
        var intervalMs = 1000.0 / _frameRate;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (_running)
        {
            var frameStart = stopwatch.ElapsedMilliseconds;
            try
            {
                var frame = CaptureFrame();
                if (frame != null)
                {
                    FrameReady?.Invoke(frame, Width, Height);
                }
            }
            catch
            {
                // 某一帧截坏了就丢掉这一帧，不要把整个录制拖死。
            }

            var spent = stopwatch.ElapsedMilliseconds - frameStart;
            var sleep = (int)Math.Round(intervalMs - spent);
            if (sleep > 0)
            {
                Thread.Sleep(sleep);
            }
        }
    }

    /// <summary>
    /// 截一帧：CopyFromScreen 截屏幕 → 有光标就画上光标 →
    /// 按行拷成 BGRA 字节数组。失败返回 null，调用方丢掉这一帧。
    /// </summary>
    private byte[]? CaptureFrame()
    {
        var target = _target;
        if (target == null)
        {
            return null;
        }

        var bounds = target.Bounds;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    bounds.Left, bounds.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);

                if (_showCursor)
                {
                    DrawCursor(graphics, bounds);
                }
            }

            var data = bitmap.LockBits(
                new Rectangle(0, 0, bounds.Width, bounds.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[bounds.Width * bounds.Height * 4];
                // 显卡 / 位图里每一行的字节数（Stride）可能比画面宽×4 多，
                // 所以要一行一行拷，不能整个内存一把拷过去。
                for (var y = 0; y < bounds.Height; y++)
                {
                    var src = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(src, bytes, y * bounds.Width * 4, bounds.Width * 4);
                }

                return bytes;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>
    /// 把鼠标光标画到刚截好的图上。CopyFromScreen 本身不带光标，
    /// 不画这一步，录出来的视频里就看不到鼠标。
    /// 光标在框外面、或者拿不到光标信息时就不画，不报错。
    /// </summary>
    private static void DrawCursor(Graphics graphics, Rectangle bounds)
    {
        try
        {
            var info = new NativeMethods.CURSORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>(),
            };
            if (!NativeMethods.GetCursorInfo(ref info))
            {
                return;
            }

            // flags 的最低位是“正在显示”，没显示（比如正在打字隐藏光标）就不画。
            const int cursorShowing = 0x00000001;
            if ((info.flags & cursorShowing) == 0 || info.hCursor == IntPtr.Zero)
            {
                return;
            }

            var x = info.ptScreenPos.X - bounds.Left;
            var y = info.ptScreenPos.Y - bounds.Top;
            if (x < 0 || y < 0 || x >= bounds.Width || y >= bounds.Height)
            {
                return;
            }

            var hdc = graphics.GetHdc();
            try
            {
                // DI_NORMAL：正常画图标；宽高传 0 表示按光标自己的大小画。
                const uint diNormal = 0x0003;
                NativeMethods.DrawIconEx(hdc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, diNormal);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }
        catch
        {
            // 光标画不上也不影响画面本身，忽略。
        }
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _running = false;

        try
        {
            // 最多等 2 秒让抓帧线程停下来，等不到也不卡住界面。
            _thread?.Join(2000);
        }
        catch
        {
            // 停止时不弹错。
        }

        _thread = null;
        _target = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DrawIconEx(
            IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
            int cxWidth, int cyWidth, int istepIfAniCur,
            IntPtr hbrFlickerFreeDraw, uint diFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
