"""拖动会话:把"按住 → 拖 → 松手"这一段收成自足单元。

为什么单独拆一个文件:拖动牵扯的东西(拖动中的标志、按下的窗口位置、
拖动期间冻结焦点守卫、松手后记位置)以前散在 ``VoiceCapsule`` 里,和
窗口显隐、注入落点那些状态混在一处,读代码时看不出"拖动到底管到哪"。
现在这一块自成一体:窗口层只提供它需要的几样东西(窗口句柄、几何
换算、保存位置、归还焦点),拖动流程本身全在这里。

机制**不变**:前端持续发**绝对目标位置**,这边换算成物理像素、夹回
允许的范围,再挪窗口(见 ``DragSession.move``)。球和胶囊是同一个
窗口、共用同一份位置记忆,所以拖哪个都是挪这一个窗口。
"""

from __future__ import annotations

import threading
from typing import Any, Callable

from ..logging_setup import get_logger
from ..win32.api import get_window_rect, get_work_area, move_window_to
from ..win32.monitors import monitor_at_point
from .placement import orb_screen_position, orb_window_position

log = get_logger("host.drag")


class DragSession:
    """一次拖动的会话(球和胶囊共用 —— 它们是同一个窗口)。

    窗口层只给几个提供器,拖动流程本身全在这里:

    ``window_handle``  底层窗口句柄(0 = 还没建好)
    ``window_scale``   窗口所在显示器的缩放系数
    ``window_size``    窗口当前的物理像素尺寸
    ``save_geometry``  记住位置(存 orb.geometry / orb.monitor)
    ``restore_focus``  把前台还给用户当前所在的窗口
    """

    def __init__(self, *, window_handle: Callable[[], int],
                 window_scale: Callable[[], float],
                 window_size: Callable[[], tuple[int, int] | None],
                 save_geometry: Callable[[], None],
                 restore_focus: Callable[[], None],
                 enabled: bool = True):
        self._window_handle = window_handle
        self._window_scale = window_scale
        self._window_size = window_size
        self._save_geometry = save_geometry
        self._restore_focus = restore_focus
        self._enabled = bool(enabled)
        self._lock = threading.Lock()
        # 用户正拖着:这段时间守卫**不**抢回焦点
        self._dragging = False
        # 按下时的窗口位置:松手时对比一下,没动过就别去写配置
        self._drag_start_rect: tuple[int, int, int, int] | None = None

    # ---------- 开关 ----------

    def set_enabled(self, enabled: bool) -> None:
        """开/关拖拽能力(新窗口接入拖拽的公开入口)。

        关掉之后前端发来的拖拽指令一律忽略 —— 窗口不参与拖动,
        也不会去动焦点守卫和位置记忆。
        """
        with self._lock:
            self._enabled = bool(enabled)
            if not self._enabled:
                self._dragging = False
                self._drag_start_rect = None

    def is_enabled(self) -> bool:
        """拖拽能力开没开。"""
        with self._lock:
            return self._enabled

    def is_dragging(self) -> bool:
        """此刻是不是正在拖(守卫靠它决定要不要抢回焦点)。"""
        with self._lock:
            return self._dragging

    # ---------- 流程 ----------

    def begin(self) -> None:
        """用户开始拖。

        拖动期间守卫**不**抢回焦点:焦点来回跳会拖垮 WebView2 的
        鼠标捕获(拖到一半就断了)。松手时统一还一次。
        """
        with self._lock:
            if not self._enabled:
                return
            self._dragging = True
        try:
            self._drag_start_rect = get_window_rect(self._window_handle())
        except Exception:
            self._drag_start_rect = None

    def move(self, x: float, y: float) -> None:
        """拖动中:窗口左上角 → (x, y) 逻辑像素。

        前端每次都从 mousedown 起点算绝对目标发过来(不发增量)——
        某一次调用丢了或慢了,下一次直接覆盖,天然不漂。

        摆位走"球"那套规则:球的位置会被夹回允许的范围(纵向尤其严
        —— 球的上方要留给展开后的胶囊),窗口跟着反推。
        """
        if not self._enabled:
            return
        hwnd = self._window_handle()
        if not hwnd:
            return
        try:
            scale = self._window_scale()
            size = self._window_size()
            if size is None:
                return
            win_w, win_h = size
            win_x = int(round(float(x) * scale))
            win_y = int(round(float(y) * scale))
            # 球在窗口的底边居中处:先算球的屏幕坐标,再夹范围
            ox, oy = orb_screen_position(win_x, win_y, win_w, win_h, scale)
            mon = monitor_at_point(ox, oy)
            area = mon.work_area if mon is not None else get_work_area()
            if area is None:
                return
            nx, ny = orb_window_position(ox, oy, win_w, win_h, scale, area)
            move_window_to(hwnd, int(nx), int(ny))
        except Exception as e:
            log.debug("移动悬浮球失败: %s", e)

    def end(self) -> None:
        """用户松手:归还焦点 + 记住位置。"""
        with self._lock:
            if not self._enabled:
                return
            self._dragging = False
            start_rect = self._drag_start_rect
            self._drag_start_rect = None
        # 这一轮还没开始,没有"注入落点"可言 —— 该还给的是用户
        # **此刻**所在的窗口(守卫动态跟踪的)。
        self._restore_focus()
        # 拖过才记位置(点一下没动就别去写配置)
        if self._moved(start_rect):
            self._save_geometry()

    def _moved(self, start_rect: Any) -> bool:
        """这一下拖动有没有真的挪过位置。

        点球(想开始录音)和拖球(想挪位置)走的是同一套按下/松手
        流程 —— 靠"有没有真的动过"区分。
        """
        if start_rect is None:
            return True
        rect = get_window_rect(self._window_handle())
        if rect is None:
            return False
        return rect[:2] != start_rect[:2]
