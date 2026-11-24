using System;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.Core.Interop;

/// <summary>
/// DPI 感知。
///
/// 翻译自 voice-mikon/src/voice_mikon/win32/dpi.py。
///
/// Windows 高分屏（本机 175%）下，不声明 DPI 感知的话：
/// * 控件不知道真实缩放，尺寸和字体全是错的
/// * 窗口实际尺寸会是设定值的 1.75 倍，布局互相重叠
///
/// 必须在创建任何窗口之前调用 <see cref="SetDpiAwareness"/>。
/// </summary>
public static class Dpi
{
    private static bool _set;

    /// <summary>
    /// 声明进程 DPI 感知。幂等，返回实际生效的模式：
    /// "already" / "per-monitor-v2" / "per-monitor" / "system" / "none"。
    /// </summary>
    public static string SetDpiAwareness()
    {
        if (_set)
        {
            return "already";
        }

        // Win10 1703+ : SetProcessDpiAwarenessContext
        try
        {
            if (NativeMethods.SetProcessDpiAwarenessContext(
                    NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            {
                _set = true;
                return "per-monitor-v2";
            }
        }
        catch (Exception)
        {
            // 平台不支持,继续往下退化
        }

        // Win8.1+ : SetProcessDpiAwareness
        try
        {
            if (NativeMethods.SetProcessDpiAwareness(2) == 0)
            {
                _set = true;
                return "per-monitor";
            }
        }
        catch (Exception)
        {
        }

        // Vista+ : SetProcessDPIAware(仅 system aware)
        try
        {
            if (NativeMethods.SetProcessDPIAware())
            {
                _set = true;
                return "system";
            }
        }
        catch (Exception)
        {
        }

        return "none";
    }
}
