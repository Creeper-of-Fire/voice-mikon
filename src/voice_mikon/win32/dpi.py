"""DPI 感知。

Windows 高分屏(本机 175%)下,不声明 DPI 感知的话:
  - tkinter 不知道真实缩放,控件尺寸和字体全是错的
  - 窗口实际尺寸会是设定值的 1.75 倍,布局互相重叠

必须在创建任何窗口之前调用 set_dpi_awareness()。
"""

from __future__ import annotations

import ctypes

_set = False

# PROCESS_PER_MONITOR_DPI_AWARE = 2
# PROCESS_SYSTEM_DPI_AWARE = 1
_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4


def set_dpi_awareness() -> str:
    """声明进程 DPI 感知。幂等,返回实际生效的模式。"""
    global _set
    if _set:
        return "already"

    # Win10 1703+ : SetProcessDpiAwarenessContext
    try:
        fn = ctypes.windll.user32.SetProcessDpiAwarenessContext
        fn.argtypes = [ctypes.c_int]
        if fn(_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) == 0:
            _set = True
            return "per-monitor-v2"
    except (AttributeError, OSError):
        pass

    # Win8.1+ : SetProcessDpiAwareness
    try:
        fn = ctypes.windll.shcore.SetProcessDpiAwareness
        fn.argtypes = [ctypes.c_int]
        if fn(2) == 0:
            _set = True
            return "per-monitor"
    except (AttributeError, OSError):
        pass

    # Vista+ : SetProcessDPIAware(仅 system aware)
    try:
        if ctypes.windll.user32.SetProcessDPIAware() != 0:
            _set = True
            return "system"
    except (AttributeError, OSError):
        pass

    return "none"
