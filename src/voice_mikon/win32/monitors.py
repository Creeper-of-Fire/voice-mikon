"""显示器枚举(多显示器 + 每屏不同缩放)。

胶囊要"贴着输入光标出现",就必须先回答三个问题:

1. 光标/窗口在**哪块屏**上?(多屏时每块屏的坐标原点、缩放都可能不同)
2. 那块屏的**可用区域**(工作区)有多大?(任务栏会占掉一块,
   摆位必须用工作区,不然胶囊会被任务栏盖住底边)
3. 那块屏的**缩放**是多少?(换算逻辑像素 ↔ 物理像素要用)

``GetSystemMetrics`` 只能回答主屏的问题,多屏场景全错 —— 这里用
``EnumDisplayMonitors`` 逐个枚举。

两个容易踩的坑:

1. **DPI 不能用 ``GetDeviceCaps(LOGPIXELSX)``**。那是"调用线程的 DPI
   上下文",进程声明了 Per-Monitor V2 之后不同线程拿到的结果不一样
   (实测:主线程 96、探测线程 168),位置会算飞。这里用
   ``GetDpiForMonitor(MDT_EFFECTIVE_DPI)`` —— 它给的是显示器本身的
   有效缩放,与线程无关。

2. **工作区不是整块屏幕**。``rcWork`` 才是任务栏之外的可用区域。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
from dataclasses import dataclass

user32 = ctypes.windll.user32

# MDT_EFFECTIVE_DPI:拿"有效 DPI"(含系统缩放与用户的缩放调整)
MDT_EFFECTIVE_DPI = 0

# shcore.dll 在 Win8.1+ 都有;个别精简系统可能没有 —— 取不到就退回系统 DPI
try:
    shcore = ctypes.windll.shcore
except OSError:                                   # pragma: no cover
    shcore = None


class MONITORINFOEX(ctypes.Structure):
    """``MONITORINFOEXW`` —— 比 MONITORINFO 多一个设备名。

    设备名(如 ``\\\\.\\DISPLAY1``)是显示器的稳定标识:用来记住
    "用户把胶囊拖到了哪块屏上",下次开机那块屏还在就还放在那儿。
    """

    _fields_ = [
        ("cbSize", wt.DWORD),
        ("rcMonitor", wt.RECT),
        ("rcWork", wt.RECT),
        ("dwFlags", wt.DWORD),
        ("szDevice", wt.WCHAR * 32),
    ]


MONITORENUMPROC = ctypes.WINFUNCTYPE(
    wt.BOOL, wt.HMONITOR, wt.HDC, ctypes.POINTER(wt.RECT), wt.LPARAM)

user32.GetMonitorInfoW.argtypes = [wt.HMONITOR, ctypes.c_void_p]
user32.GetMonitorInfoW.restype = wt.BOOL
user32.EnumDisplayMonitors.argtypes = [
    wt.HDC, ctypes.c_void_p, MONITORENUMPROC, wt.LPARAM]
user32.EnumDisplayMonitors.restype = wt.BOOL

if shcore is not None:
    shcore.GetDpiForMonitor.argtypes = [
        wt.HMONITOR, ctypes.c_int,
        ctypes.POINTER(wt.UINT), ctypes.POINTER(wt.UINT)]
    shcore.GetDpiForMonitor.restype = ctypes.c_long


@dataclass(frozen=True)
class Monitor:
    """一块显示器。坐标全是**物理像素**。"""

    device: str                                   # 稳定标识,如 \\.\DISPLAY1
    work_area: tuple[int, int, int, int]          # 任务栏之外的可用区域
    monitor_rect: tuple[int, int, int, int]       # 整块屏幕
    scale: float                                  # 缩放系数(1.75 = 175%)

    def contains(self, x: int, y: int) -> bool:
        """物理像素点 (x, y) 是否落在这块屏上。"""
        l, t, r, b = self.monitor_rect
        return l <= x < r and t <= y < b


def _monitor_scale(hmon) -> float:
    """显示器的有效缩放系数(拿不到就退回系统 DPI)。"""
    if shcore is not None:
        try:
            dpi_x = wt.UINT(0)
            dpi_y = wt.UINT(0)
            hr = shcore.GetDpiForMonitor(
                hmon, MDT_EFFECTIVE_DPI,
                ctypes.byref(dpi_x), ctypes.byref(dpi_y))
            if hr == 0 and dpi_x.value:
                return max(1.0, dpi_x.value / 96.0)
        except Exception:
            pass
    try:
        dpi = user32.GetDpiForSystem()
        if dpi:
            return max(1.0, dpi / 96.0)
    except Exception:
        pass
    return 1.0


def list_monitors() -> list[Monitor]:
    """枚举当前所有显示器。"""
    found: list[Monitor] = []

    def _enum(hmon, _hdc, _lprc, _lparam):
        info = MONITORINFOEX()
        info.cbSize = ctypes.sizeof(MONITORINFOEX)
        if user32.GetMonitorInfoW(hmon, ctypes.byref(info)):
            work = info.rcWork
            mon = info.rcMonitor
            found.append(Monitor(
                device=info.szDevice,
                work_area=(work.left, work.top, work.right, work.bottom),
                monitor_rect=(mon.left, mon.top, mon.right, mon.bottom),
                scale=_monitor_scale(hmon),
            ))
        return True          # 继续枚举下一块

    # 回调必须保持引用:ctypes 的回调对象被 GC 掉之后,
    # 枚举过程中调用它就是访问野指针(会直接崩进程)
    proc = MONITORENUMPROC(_enum)
    user32.EnumDisplayMonitors(None, None, proc, 0)
    return found


def monitor_at_point(x: int, y: int) -> Monitor | None:
    """物理像素点 (x, y) 所在的那块显示器。

    找不到(点在缝隙里、或枚举失败)就返回 None —— 调用方该走兜底,
    别猜。
    """
    for mon in list_monitors():
        if mon.contains(x, y):
            return mon
    return None


def monitor_by_device(device: str) -> Monitor | None:
    """按设备名找显示器(用来还原"上次拖到哪块屏上")。

    设备名对不上(换机器、拔了显示器)就返回 None —— 调用方该退回
    当前位置或主屏,而不是硬套一个旧坐标。
    """
    if not device:
        return None
    for mon in list_monitors():
        if mon.device == device:
            return mon
    return None


def primary_monitor() -> Monitor | None:
    """主显示器(胶囊的"固定底部"模式默认落在这里)。

    主屏 = 虚拟桌面左上角那块,即 ``(0, 0)`` 所在的那块。
    """
    for mon in list_monitors():
        l, t, _, _ = mon.monitor_rect
        if l <= 0 < l + 1 and t <= 0 < t + 1:
            return mon
    mons = list_monitors()
    return mons[0] if mons else None
