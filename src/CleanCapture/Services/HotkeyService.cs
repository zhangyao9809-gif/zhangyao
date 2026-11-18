using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CleanCapture.Models;

namespace CleanCapture.Services;

/// <summary>
/// 全局快捷键：哪怕 CleanCapture 的窗口不在最前面，按了也有反应。
/// 原理是向 Windows 注册热键（RegisterHotKey），系统收到后会发一条
/// WM_HOTKEY 消息给我们，我们在消息里判断是哪个键，再去截图 / 录屏。
///
/// 默认键（设置窗口里可以改）：
/// * 区域截图 Ctrl+Shift+S
/// * 全屏截图 Ctrl+Shift+F
/// * 开始 / 停止录屏 Ctrl+Shift+R
/// * 暂停 / 继续录屏 Ctrl+Shift+P
/// 故意避开微信 Alt+A、QQ Ctrl+Alt+A；如果还是冲突（比如别的软件也占了），
/// 注册时会失败，程序会弹一次提示让你换个键，不要硬抢。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly Dictionary<int, Action> _actions = new();
    private HwndSource? _source;
    private bool _disposed;

    /// <summary>按下快捷键时触发，参数是“动作名”，比如 RegionScreenshot。</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>把设置里的四个快捷键一次性注册掉。注册失败的会弹提示，但不中断其他键。</summary>
    public void RegisterAll(AppSettings settings)
    {
        // MainWindow 建好之后才有窗口句柄，这里先等主窗口出来。
        var mainWindow = System.Windows.Application.Current?.MainWindow;
        if (mainWindow == null)
        {
            return;
        }

        var handle = new WindowInteropHelper(mainWindow).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        RegisterOne(1, settings.HotkeyRegionScreenshot, "RegionScreenshot");
        RegisterOne(2, settings.HotkeyFullscreenScreenshot, "FullscreenScreenshot");
        RegisterOne(3, settings.HotkeyToggleRecording, "ToggleRecording");
        RegisterOne(4, settings.HotkeyPauseRecording, "PauseRecording");
    }

    private void RegisterOne(int id, string hotkeyText, string actionName)
    {
        if (!TryParse(hotkeyText, out var modifiers, out var key))
        {
            System.Windows.MessageBox.Show(
                $"快捷键“{hotkeyText}”写错了，{actionName} 这次没注册上。\n请去设置里改成像 Ctrl+Shift+S 这样的写法。",
                "CleanCapture：快捷键写错了",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var handle = new WindowInteropHelper(System.Windows.Application.Current.MainWindow).Handle;
        var ok = NativeMethods.RegisterHotKey(handle, id, modifiers, key);
        if (!ok)
        {
            var error = Marshal.GetLastWin32Error();
            System.Windows.MessageBox.Show(
                $"快捷键“{hotkeyText}”注册失败（错误码 {error}），很可能被别的软件占了。\n" +
                "请去设置里换一个组合键，比如把 Ctrl+Shift+S 换成 Ctrl+Alt+S。",
                "CleanCapture：快捷键被占了",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _actions[id] = () => HotkeyPressed?.Invoke(actionName);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (_actions.TryGetValue(id, out var action))
            {
                action();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 把“Ctrl+Shift+S”这样的文字转成 Windows 能认的修饰键 + 虚拟键码。
    /// 支持 Ctrl、Alt、Shift 三种修饰键，字母、数字、F1–F12、PrintScreen。
    /// </summary>
    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i].ToLowerInvariant();
            if (part is "ctrl" or "control")
            {
                modifiers |= 0x0002;
            }
            else if (part == "alt")
            {
                modifiers |= 0x0001;
            }
            else if (part == "shift")
            {
                modifiers |= 0x0004;
            }
            else if (part is "win" or "windows")
            {
                modifiers |= 0x0008;
            }
            else
            {
                return false;
            }
        }

        var last = parts[^1].ToUpperInvariant();
        if (last.Length == 1)
        {
            // 字母 / 数字：虚拟键码就是字符本身的 ASCII 码。
            key = last[0];
            return true;
        }

        if (last.StartsWith("F", StringComparison.Ordinal)
            && int.TryParse(last.AsSpan(1), out var f)
            && f is >= 1 and <= 12)
        {
            key = (uint)(0x70 + f - 1);
            return true;
        }

        if (last is "PRINTSCREEN" or "PRTSC")
        {
            key = 0x2C;
            return true;
        }

        return false;
    }

    /// <summary>注销全部热键。退出程序、改设置重新注册之前都要先调一次。</summary>
    public void UnregisterAll()
    {
        if (_source == null)
        {
            return;
        }

        var handle = new WindowInteropHelper(System.Windows.Application.Current.MainWindow).Handle;
        foreach (var id in _actions.Keys)
        {
            NativeMethods.UnregisterHotKey(handle, id);
        }

        _actions.Clear();
        _source.RemoveHook(WndProc);
        _source = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
