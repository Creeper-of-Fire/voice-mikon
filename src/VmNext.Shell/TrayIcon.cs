using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace VmNext.Shell;

/// <summary>托盘图标的状态。色值照 voice-mikon/src/voice_mikon/host/tray.py 的 <c>STATE_COLORS</c>。</summary>
public enum TrayIconState
{
    /// <summary>待机（灰）。</summary>
    Idle,

    /// <summary>录音中（红）。</summary>
    Recording,

    /// <summary>识别中（黄）。</summary>
    Processing,

    /// <summary>警告（黄）—— 与 processing 同色（Python 版 <c>warn</c> 也是这个值）。</summary>
    Warn,

    /// <summary>错误（红）—— 与 recording 同色。</summary>
    Error,
}

/// <summary>
/// 托盘菜单项。
///
/// <para>文案与顺序照 voice-mikon/src/voice_mikon/__main__.py 的 <c>tray.set_menu([...])</c>。</para>
/// </summary>
/// <param name="Label">显示文字。</param>
/// <param name="Key">回调时传回的标识（Python 版的 <c>key</c>）。</param>
/// <param name="IsSeparator">是不是分隔线。</param>
/// <param name="IsDefault">是不是默认项（双击托盘图标时执行）。</param>
public sealed record TrayMenuEntry(string Label, string Key, bool IsSeparator = false, bool IsDefault = false);

/// <summary>
/// 托盘图标（Win32 <c>Shell_NotifyIcon</c> + <c>TrackPopupMenu</c>）。
///
/// <para>和 <see cref="HotKeyWindow"/> 是同一类东西：**没有 Avalonia 控件**，
/// 靠 Win32 消息驱动，所以放在 Shell 层。它自己起一条线程跑消息循环 ——
/// 托盘图标不依赖任何一个 Avalonia 窗口，胶囊窗口关了它也该还在。</para>
///
/// <para>翻译自 voice-mikon/src/voice_mikon/host/tray.py：
/// <list type="bullet">
/// <item>三色（灰 / 黄 / 红）与图标画法照 <c>make_icon</c>；</item>
/// <item>菜单项文案与顺序照 <c>__main__.py</c> 的 <c>tray.set_menu([...])</c>。</item>
/// </list></para>
///
/// <para><b>不搬</b> pystray：它自己起线程跑消息循环、异常处理套异常处理的那套写法。
/// 这里用最直接的 Win32 做法 —— 一条线程、一个 message-only 窗口、一个消息循环。</para>
///
/// <para>⚠ <b>退出前必须 <c>Dispose</c></b>：否则托盘里会留一枚死图标
/// （进程没了，图标还在，用户点了没反应）。</para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>图标边长（像素）。来源：tray.py 的 <c>make_icon</c> 里的 <c>size = 64</c>。</summary>
    public const int IconSize = 64;

    /// <summary>状态色（COLORREF = 0x00BBGGRR）。来源：tray.py 的 <c>STATE_COLORS</c>。</summary>
    private const uint ColorIdle = 0x00464646;        // rgba(70,70,70,255)
    private const uint ColorRecording = 0x002828C8;    // rgba(200,40,40,255)
    private const uint ColorProcessing = 0x001EA0D2;  // rgba(210,160,30,255)
    private const uint ColorWhite = 0x00FFFFFF;

    /// <summary>自定义消息：更新图标状态。</summary>
    private const uint WmSetState = NativeMethods.WM_APP + 1;

    /// <summary>自定义消息：更新菜单。</summary>
    private const uint WmSetMenu = NativeMethods.WM_APP + 2;

    /// <summary>自定义消息：关闭（退出前用）。</summary>
    private const uint WmShutdown = NativeMethods.WM_APP + 3;

    /// <summary>菜单项的命令 id 起点。</summary>
    private const int MenuCommandBase = 1000;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 委托必须保持静态引用，否则会被 GC 回收，导致窗口过程变成野指针。
    private static readonly WndProcDelegate WndProc = WindowProcedure;

    private static readonly Dictionary<IntPtr, TrayIcon> Instances = new();
    private static bool _classRegistered;

    private readonly object _lock = new();
    private readonly List<TrayMenuEntry> _menu = new();

    private Thread? _thread;
    private IntPtr _handle;
    private IntPtr _icon;
    private TrayIconState _state = TrayIconState.Idle;
    private string _tip = "voice-mikon";
    private bool _disposed;

    /// <summary>
    /// 用户选了某个菜单项。参数是菜单项的 <see cref="TrayMenuEntry.Key"/>。
    ///
    /// <para>⚠ 这个回调在**托盘线程**上触发（Win32 消息线程），不是 UI 线程 ——
    /// 要动 UI / controller 的东西，调用方自己 <c>Dispatcher.UIThread.Post</c>。</para>
    /// </summary>
    public event EventHandler<string>? MenuItemActivated;

    /// <summary>双击托盘图标时触发的菜单项 key（默认项）。</summary>
    public event EventHandler<string>? DefaultItemActivated;

    /// <summary>
    /// 起托盘（建窗口 + 注册图标 + 进消息循环）。
    /// 返回 false 表示托盘起不来（调用方应发一条通知，别静默）。
    /// </summary>
    public bool Start()
    {
        if (_thread is not null)
        {
            return true;
        }

        var ready = new ManualResetEventSlim(false);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                RunTrayLoop(ready);
            }
            catch (Exception ex)
            {
                failure = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "vmnext-tray",
        };

        _thread = thread;
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));

        return failure is null && _handle != IntPtr.Zero;
    }

    /// <summary>
    /// 换图标状态（灰 / 黄 / 红）。可以从任意线程调用。
    /// </summary>
    /// <param name="state">状态。</param>
    public void SetState(TrayIconState state)
    {
        lock (_lock)
        {
            _state = state;
        }

        PostToTray(WmSetState, (IntPtr)(int)state);
    }

    /// <summary>换悬停提示文字。可以从任意线程调用。</summary>
    /// <param name="tip">提示文字。</param>
    public void SetTip(string tip)
    {
        lock (_lock)
        {
            _tip = tip ?? string.Empty;
        }

        PostToTray(WmSetState, (IntPtr)(int)_state);
    }

    /// <summary>
    /// 换菜单。可以从任意线程调用。
    /// </summary>
    /// <param name="entries">菜单项（文案与顺序照 Python 版）。</param>
    public void SetMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        lock (_lock)
        {
            _menu.Clear();
            if (entries is not null)
            {
                _menu.AddRange(entries);
            }
        }

        PostToTray(WmSetMenu, IntPtr.Zero);
    }

    /// <summary>
    /// 收尾：删托盘图标（NIM_DELETE）、销毁窗口、停线程。
    ///
    /// <para>幂等。必须在进程退出前调 —— 否则托盘里留一枚死图标。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 让托盘线程自己做 NIM_DELETE + 销毁（图标句柄属于那一条线程的窗口）。
        PostToTray(WmShutdown, IntPtr.Zero);

        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;

        lock (_lock)
        {
            _icon = IntPtr.Zero;
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>往托盘线程投递一条消息（窗口还没建好时静默忽略）。</summary>
    private void PostToTray(uint message, IntPtr wParam)
    {
        IntPtr handle;
        lock (_lock)
        {
            handle = _handle;
        }

        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.PostMessage(handle, message, wParam, IntPtr.Zero);
    }

    // ==================== 托盘线程 ====================

    private void RunTrayLoop(ManualResetEventSlim ready)
    {
        EnsureWindowClassRegistered();

        _handle = NativeMethods.CreateWindowExW(
            0,
            WindowClassName,
            "VmNextTrayWindow",
            0,
            0, 0, 0, 0,
            new IntPtr(-3), // HWND_MESSAGE：消息专用窗口（不在屏幕上显示）
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            ready.Set();
            return;
        }

        Instances[_handle] = this;
        ready.Set();

        try
        {
            AddIcon();

            while (NativeMethods.PeekMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            Instances.Remove(_handle);

            // 进程退出前一定要把图标从托盘里删掉，否则留一枚点不动的死图标。
            RemoveIcon();

            if (_handle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    private const string WindowClassName = "VmNextTrayWindow";

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

        NativeMethods.RegisterClassExW(ref wc);
        _classRegistered = true;
    }

    private static IntPtr WindowProcedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hWnd, out TrayIcon? instance))
        {
            if (instance.HandleMessage(msg, wParam, lParam))
            {
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>处理托盘窗口的消息。返回 true 表示这条消息已经处理完。</summary>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmSetState:
                ApplyState();
                return true;

            case WmSetMenu:
                ApplyState();
                return true;

            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                ShowContextMenu();
                return true;

            case NativeMethods.WM_LBUTTONDBLCLK:
                InvokeDefault();
                return true;

            case WmShutdown:
                NativeMethods.PostQuitMessage(0);
                return true;

            default:
                return false;
        }
    }

    // ==================== 图标 ====================

    /// <summary>把图标加进托盘。</summary>
    private void AddIcon()
    {
        IntPtr icon = CreateTrayIcon(_state);
        lock (_lock)
        {
            _icon = icon;
        }

        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _handle,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_RBUTTONUP,
            hIcon = icon,
            szTip = _tip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
    }

    /// <summary>把图标从托盘里删掉。</summary>
    private void RemoveIcon()
    {
        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _handle,
            uID = 1,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);

        IntPtr icon;
        lock (_lock)
        {
            icon = _icon;
            _icon = IntPtr.Zero;
        }

        if (icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(icon);
        }
    }

    /// <summary>按当前状态重画图标并更新到托盘。</summary>
    private void ApplyState()
    {
        TrayIconState state;
        string tip;
        lock (_lock)
        {
            state = _state;
            tip = _tip;
        }

        IntPtr oldIcon;
        IntPtr newIcon = CreateTrayIcon(state);

        lock (_lock)
        {
            oldIcon = _icon;
            _icon = newIcon;
        }

        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _handle,
            uID = 1,
            uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            hIcon = newIcon,
            szTip = tip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);

        if (oldIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(oldIcon);
        }
    }

    /// <summary>
    /// 运行时画托盘图标（GDI）。
    ///
    /// <para>画法逐条照 voice-mikon/src/voice_mikon/host/tray.py 的 <c>make_icon</c>：
    /// <code>
    /// size = 64
    /// d.ellipse([2, 2, size - 2, size - 2], fill=color)     # 状态色的圆
    /// d.rounded_rectangle([28, 12, 36, 34], radius=4)      # 麦克风头
    /// d.arc([21, 24, 43, 46], 0, 180, width=3)             # U 形支架
    /// d.line([32, 42, 32, 52], width=3)                    # 支架立杆
    /// </code></para>
    ///
    /// <para>两处实现差异：
    /// <list type="number">
    /// <item><b>弧用两段三次 Bezier 近似</b>，而不是 GDI 的 <c>Arc</c> ——
    /// <c>Arc</c> 的起止点是"角度约定"，方向容易搞反；Bezier 的控制点是我们自己算的，
    /// 画出来一定是从右经下到左的下半弧（= PIL 的 <c>arc(0,180)</c>）。</item>
    /// <item><b>背景是透明的</b>：GDI 位图没有 alpha 通道，所以画完补一遍
    /// （有颜色的像素 alpha 置 255，其余保持 0）。</item>
    /// </list></para>
    /// </summary>
    /// <param name="state">状态。</param>
    /// <returns>图标句柄；失败返回 0。</returns>
    private static IntPtr CreateTrayIcon(TrayIconState state)
    {
        uint color = state switch
        {
            TrayIconState.Recording => ColorRecording,
            TrayIconState.Processing => ColorProcessing,
            TrayIconState.Warn => ColorProcessing,
            TrayIconState.Error => ColorRecording,
            _ => ColorIdle,
        };

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            return IntPtr.Zero;
        }

        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = IconSize,
                biHeight = -IconSize, // 负数 = 自上而下排列
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        IntPtr bits = IntPtr.Zero;
        IntPtr bitmap = NativeMethods.CreateDIBSection(screenDc, ref info, 0, out bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            return IntPtr.Zero;
        }

        IntPtr oldBitmap = NativeMethods.SelectObject(memDc, bitmap);

        // DIB 的初始内容是未定义的 → 先整块清零（= 全透明）。
        int byteCount = IconSize * IconSize * 4;
        Marshal.Copy(new byte[byteCount], 0, bits, byteCount);

        NativeMethods.SetBkMode(memDc, NativeMethods.TRANSPARENT);

        DrawStateCircle(memDc, color);
        DrawMicrophone(memDc);

        // GDI 不写 alpha 通道 → 补一遍：有颜色的像素不透明，其余透明。
        FixAlphaChannel(bits, byteCount);

        var iconInfo = new NativeMethods.ICONINFO
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = bitmap,
            hbmColor = bitmap,
        };

        IntPtr icon = NativeMethods.CreateIconIndirect(ref iconInfo);

        NativeMethods.SelectObject(memDc, oldBitmap);
        NativeMethods.DeleteObject(bitmap);
        NativeMethods.DeleteDC(memDc);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);

        return icon;
    }

    /// <summary>画状态色的圆（tray.py 的 <c>d.ellipse([2, 2, size - 2, size - 2], fill=color)</c>）。</summary>
    private static void DrawStateCircle(IntPtr hdc, uint color)
    {
        IntPtr brush = NativeMethods.CreateSolidBrush(color);
        IntPtr pen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, color);

        IntPtr oldBrush = NativeMethods.SelectObject(hdc, brush);
        IntPtr oldPen = NativeMethods.SelectObject(hdc, pen);

        NativeMethods.Ellipse(hdc, 2, 2, IconSize - 2, IconSize - 2);

        NativeMethods.SelectObject(hdc, oldBrush);
        NativeMethods.SelectObject(hdc, oldPen);
        NativeMethods.DeleteObject(brush);
        NativeMethods.DeleteObject(pen);
    }

    /// <summary>
    /// 画白色的麦克风（tray.py 的后三行）。
    /// </summary>
    private static void DrawMicrophone(IntPtr hdc)
    {
        IntPtr brush = NativeMethods.CreateSolidBrush(ColorWhite);
        IntPtr pen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 3, ColorWhite);

        IntPtr oldBrush = NativeMethods.SelectObject(hdc, brush);
        IntPtr oldPen = NativeMethods.SelectObject(hdc, pen);

        // 麦克风头：圆角矩形 [28,12,36,34]，圆角 4（PIL 的边界是闭区间，
        // GDI 的 right/bottom 是排他边界 → 这里照抄 PIL 的数字，差一像素看不出来）。
        NativeMethods.RoundRect(hdc, 28, 12, 36, 34, 8, 8);

        // U 形支架：椭圆 [21,24,43,46] 的**下半弧**（中心 (32,35)，半径 11）。
        // 用两段三次 Bezier 近似：k = 4/3 · tan(π/8) · r ≈ 6.075。
        NativeMethods.PolyBezier(
            hdc,
            new[]
            {
                new NativeMethods.POINT { X = 43, Y = 35 },
                new NativeMethods.POINT { X = 43, Y = 41 },
                new NativeMethods.POINT { X = 38, Y = 46 },
                new NativeMethods.POINT { X = 32, Y = 46 },
                new NativeMethods.POINT { X = 26, Y = 46 },
                new NativeMethods.POINT { X = 21, Y = 41 },
                new NativeMethods.POINT { X = 21, Y = 35 },
                new NativeMethods.POINT { X = 21, Y = 35 },
            },
            8);

        // 支架立杆：[32,42] → [32,52]。
        NativeMethods.MoveToEx(hdc, 32, 42, IntPtr.Zero);
        NativeMethods.LineTo(hdc, 32, 52);

        NativeMethods.SelectObject(hdc, oldBrush);
        NativeMethods.SelectObject(hdc, oldPen);
        NativeMethods.DeleteObject(brush);
        NativeMethods.DeleteObject(pen);
    }

    /// <summary>
    /// 补 alpha 通道：像素不是纯黑就当作不透明（0xFF），否则全透明。
    ///
    /// <para>边缘的抗锯齿像素会带上一点底色 —— 托盘图标缩到 16×16 之后看不出来。</para>
    /// </summary>
    private static void FixAlphaChannel(IntPtr bits, int byteCount)
    {
        var buffer = new byte[byteCount];
        Marshal.Copy(bits, buffer, 0, byteCount);

        for (int i = 0; i < buffer.Length; i += 4)
        {
            byte b = buffer[i];
            byte g = buffer[i + 1];
            byte r = buffer[i + 2];
            buffer[i + 3] = (b == 0 && g == 0 && r == 0) ? (byte)0 : (byte)0xFF;
        }

        Marshal.Copy(buffer, 0, bits, byteCount);
    }

    // ==================== 菜单 ====================

    /// <summary>弹出右键菜单。</summary>
    private void ShowContextMenu()
    {
        List<TrayMenuEntry> entries;
        lock (_lock)
        {
            entries = new List<TrayMenuEntry>(_menu);
        }

        if (entries.Count == 0)
        {
            return;
        }

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                TrayMenuEntry entry = entries[i];
                if (entry.IsSeparator)
                {
                    NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
                    continue;
                }

                NativeMethods.AppendMenuW(
                    menu,
                    NativeMethods.MF_STRING,
                    (IntPtr)(MenuCommandBase + i),
                    entry.Label);
            }

            NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);

            // 菜单弹在鼠标左上方一点（完全贴着鼠标会盖住图标）。
            uint command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                cursor.X,
                cursor.Y,
                0,
                _handle,
                IntPtr.Zero);

            if (command != 0)
            {
                int index = (int)command - MenuCommandBase;
                if (index >= 0 && index < entries.Count)
                {
                    TrayMenuEntry entry = entries[index];
                    if (!entry.IsSeparator)
                    {
                        MenuItemActivated?.Invoke(this, entry.Key);
                    }
                }
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    /// <summary>双击托盘图标 → 执行默认项。</summary>
    private void InvokeDefault()
    {
        List<TrayMenuEntry> entries;
        lock (_lock)
        {
            entries = new List<TrayMenuEntry>(_menu);
        }

        foreach (TrayMenuEntry entry in entries)
        {
            if (entry.IsDefault && !entry.IsSeparator)
            {
                DefaultItemActivated?.Invoke(this, entry.Key);
                return;
            }
        }
    }
}
