using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VmNext.Shell;

/// <summary>
/// 全局热键接收窗口（message-only window）。
///
/// 为什么需要它：覆盖层窗口带 WS_EX_NOACTIVATE，永远拿不到键盘焦点，
/// 所以 KeyDown 事件根本不会触发（Demo A/B 都要用键盘，又不能抢用户焦点）。
/// 正确做法是 RegisterHotKey —— 系统把组合键消息投递给指定 HWND，与焦点无关。
/// 真正的语音输入工具同样需要全局热键，所以这一层是重构骨架的一部分，不是 demo 专用。
/// </summary>
public sealed class HotKeyWindow : IDisposable
{
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 委托必须保持静态引用，否则会被 GC 回收，导致窗口过程变成野指针。
    private static readonly WndProcDelegate WndProc = WindowProcedure;

    private static readonly Dictionary<IntPtr, HotKeyWindow> Instances = new();
    private static bool _classRegistered;

    private readonly Dictionary<int, uint> _registered = new();
    private IntPtr _handle;

    /// <summary>热键被按下时触发，参数是注册时用的 id。</summary>
    public event EventHandler<int>? HotKeyPressed;

    /// <summary>创建 message-only 窗口（不出现在屏幕上）。</summary>
    public HotKeyWindow()
    {
        EnsureWindowClassRegistered();

        _handle = NativeMethods.CreateWindowExW(
            0,
            WindowClassName,
            "VmNextHotKeyWindow",
            0,
            0, 0, 0, 0,
            new IntPtr(-3), // HWND_MESSAGE：消息专用窗口
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_handle != IntPtr.Zero)
        {
            Instances[_handle] = this;
        }
    }

    private const string WindowClassName = "VmNextHotKeyWindow";

    /// <summary>注册全局热键。返回 false 表示该组合键已被别的程序占用。</summary>
    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        if (NativeMethods.RegisterHotKey(_handle, id, modifiers, virtualKey))
        {
            _registered[id] = virtualKey;
            return true;
        }

        return false;
    }

    /// <summary>注销热键。</summary>
    public void Unregister(int id)
    {
        if (_handle != IntPtr.Zero && _registered.Remove(id))
        {
            NativeMethods.UnregisterHotKey(_handle, id);
        }
    }

    /// <summary>释放窗口（退出前务必调用，否则热键会一直被占用）。</summary>
    public void Dispose()
    {
        foreach (int id in new List<int>(_registered.Keys))
        {
            Unregister(id);
        }

        if (_handle != IntPtr.Zero)
        {
            Instances.Remove(_handle);
            NativeMethods.DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static void EnsureWindowClassRegistered()
    {
        if (_classRegistered)
        {
            return;
        }

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = WindowClassName,
        };

        // 返回 0 也可能只是"类已存在"，这里不区分
        NativeMethods.RegisterClassExW(ref wc);
        _classRegistered = true;
    }

    private static IntPtr WindowProcedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && Instances.TryGetValue(hWnd, out var instance))
        {
            instance.HotKeyPressed?.Invoke(instance, wParam.ToInt32());
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
