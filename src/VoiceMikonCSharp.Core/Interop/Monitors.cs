using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.Core.Interop;

/// <summary>
/// 一块显示器。坐标全是**物理像素**。
///
/// 翻译自 voice-mikon/src/voice_mikon/win32/monitors.py 的 Monitor。
/// </summary>
public sealed record Monitor(
    string Device,
    NativeMethods.RECT WorkArea,
    NativeMethods.RECT MonitorRect,
    double Scale)
{
    /// <summary>物理像素点 (x, y) 是否落在这块屏上。</summary>
    public bool Contains(int x, int y)
        => MonitorRect.Left <= x && x < MonitorRect.Right
           && MonitorRect.Top <= y && y < MonitorRect.Bottom;
}

/// <summary>
/// 显示器枚举（多显示器 + 每屏不同缩放）。
///
/// 翻译自 voice-mikon/src/voice_mikon/win32/monitors.py。
///
/// GetSystemMetrics 只能回答主屏的问题，多屏场景全错 —— 这里用
/// EnumDisplayMonitors 逐个枚举。
///
/// 两个容易踩的坑：
/// 1. **DPI 不能用 GetDeviceCaps(LOGPIXELSX)**。那是"调用线程的 DPI 上下文"，
///    进程声明了 Per-Monitor V2 之后不同线程拿到的结果不一样（实测：主线程 96、
///    探测线程 168），位置会算飞。这里用 GetDpiForMonitor(MDT_EFFECTIVE_DPI) ——
///    它给的是显示器本身的有效缩放，与线程无关。
/// 2. **工作区不是整块屏幕**。rcWork 才是任务栏之外的可用区域。
/// </summary>
public static class Monitors
{
    /// <summary>枚举当前所有显示器。</summary>
    public static IReadOnlyList<Monitor> ListMonitors()
    {
        var found = new List<Monitor>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr lParam)
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
            {
                found.Add(new Monitor(
                    info.szDevice,
                    info.rcWork,
                    info.rcMonitor,
                    MonitorScale(hMonitor)));
            }

            // 继续枚举下一块
            return true;
        }

        // 委托是局部变量,但 EnumDisplayMonitors 是同步调用 —— 枚举期间委托存活,
        // 不会出现 Python 版"回调被 GC 掉就崩进程"的情况。
        NativeMethods.MonitorEnumProc proc = Callback;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// 物理像素点 (x, y) 所在的那块显示器。
    ///
    /// 找不到（点在缝隙里、或枚举失败）就返回 null —— 调用方该走兜底，别猜。
    /// </summary>
    public static Monitor? MonitorAtPoint(int x, int y)
    {
        foreach (Monitor mon in ListMonitors())
        {
            if (mon.Contains(x, y))
            {
                return mon;
            }
        }

        return null;
    }

    /// <summary>
    /// 按设备名找显示器（用来还原"上次拖到哪块屏上"）。
    ///
    /// 设备名对不上（换机器、拔了显示器）就返回 null —— 调用方该退回当前位置
    /// 或主屏，而不是硬套一个旧坐标。
    /// </summary>
    public static Monitor? MonitorByDevice(string? device)
    {
        if (string.IsNullOrEmpty(device))
        {
            return null;
        }

        foreach (Monitor mon in ListMonitors())
        {
            if (mon.Device == device)
            {
                return mon;
            }
        }

        return null;
    }

    /// <summary>
    /// 主显示器（胶囊的"固定底部"模式默认落在这里）。
    ///
    /// 主屏 = 虚拟桌面左上角那块，即 (0, 0) 所在的那块。
    /// </summary>
    public static Monitor? PrimaryMonitor()
    {
        foreach (Monitor mon in ListMonitors())
        {
            int l = mon.MonitorRect.Left;
            int t = mon.MonitorRect.Top;
            if (l <= 0 && 0 < l + 1 && t <= 0 && 0 < t + 1)
            {
                return mon;
            }
        }

        IReadOnlyList<Monitor> all = ListMonitors();
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>显示器的有效缩放系数（拿不到就退回系统 DPI）。</summary>
    private static double MonitorScale(IntPtr hMonitor)
    {
        try
        {
            int hr = NativeMethods.GetDpiForMonitor(
                hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _);

            if (hr == 0 && dpiX > 0)
            {
                return Math.Max(1.0, dpiX / 96.0);
            }
        }
        catch (DllNotFoundException)
        {
            // shcore.dll 在个别精简系统上可能没有 —— 退回系统 DPI
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            uint dpi = NativeMethods.GetDpiForSystem();
            if (dpi > 0)
            {
                return Math.Max(1.0, dpi / 96.0);
            }
        }
        catch (Exception)
        {
            // 忽略
        }

        return 1.0;
    }
}
