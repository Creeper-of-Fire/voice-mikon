"""定位"输入光标在哪"—— 跟随输入模式的位置信号。

胶囊要像输入法候选框一样贴着光标出现,就得先知道光标的屏幕坐标。
但"光标在哪"没有单一可靠的 API,不同应用支持的程度差别很大:

| 手段 | 覆盖范围 | 代价 |
|---|---|---|
| UIA ``TextPattern`` | 现代应用(Chrome / VSCode / Office / Electron) | 慢(几十毫秒),要 comtypes |
| ``GetGUIThreadInfo`` 的 ``rcCaret`` | 传统 Edit 控件(记事本等) | 极快 |
| ``GetCursorPos`` | 谁都能用,但指的是**鼠标**不是光标 | 极快 |
| 前台窗口矩形 | 兜底:只知道窗口在哪 | 极快 |

所以这里做**四级回退**:从最准的往下试,拿到谁用谁。

UIA 那级需要 ``comtypes``(可选依赖 ``pip install -e ".[uia]"``)。
没装、或者调用失败,就直接降到下一级 —— 功能少一档,不该让整个
跟随模式不可用。

坐标一律**物理像素**:Windows 的窗口坐标就是物理像素,逻辑像素
的换算只在胶囊摆放那一步做(那边知道自己的缩放)。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
from dataclasses import dataclass

user32 = ctypes.windll.user32

# GUITHREADINFO.flags 里的这位表示"rcCaret 是有效的"
GUI_CARETVISIBLE = 0x0001


class GUITHREADINFO(ctypes.Structure):
    """``GUITHREADINFO`` —— 取前台线程的焦点/光标信息。"""

    _fields_ = [
        ("cbSize", wt.DWORD),
        ("flags", wt.DWORD),
        ("hwndActive", wt.HWND),
        ("hwndFocus", wt.HWND),
        ("hwndCapture", wt.HWND),
        ("hwndMenuOwner", wt.HWND),
        ("hwndMoveSize", wt.HWND),
        ("hwndCaret", wt.HWND),
        ("rcCaret", wt.RECT),
    ]


user32.GetGUIThreadInfo.argtypes = [wt.DWORD, ctypes.c_void_p]
user32.GetGUIThreadInfo.restype = wt.BOOL
user32.GetWindowThreadProcessId.argtypes = [
    wt.HWND, ctypes.POINTER(wt.DWORD)]
user32.GetWindowThreadProcessId.restype = wt.DWORD
user32.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
user32.GetCursorPos.restype = wt.BOOL
user32.ClientToScreen.argtypes = [wt.HWND, ctypes.c_void_p]
user32.ClientToScreen.restype = wt.BOOL


@dataclass(frozen=True)
class Anchor:
    """一个定位信号点(物理像素)。

    ``kind`` 说明这个点是怎么来的 —— 日志里能一眼看出"这次是降级
    到了第几级":``uia`` / ``caret`` / ``cursor`` / ``window``。
    """

    x: int
    y: int
    kind: str


# ---------------- 第 1 级:UIA ----------------

def _uia_module():
    """加载 UIAutomationCore 的 COM 类型库(需要 comtypes)。

    首次调用会生成类型库缓存(几百毫秒),之后就走缓存。
    程序启动时预热一次(见 ``warmup``),把这笔开销挪到用户按热键之前。
    """
    try:
        import comtypes.client
    except Exception:
        return None
    try:
        return comtypes.client.GetModule("UIAutomationCore.dll")
    except Exception:
        return None


def _uia_automation(UIA):
    """造一个 IUIAutomation 实例。

    **必须**走 ``CreateObject(..., interface=IUIAutomation)``:
    直接 ``UIA.CUIAutomation()`` 拿到的是 coclass 的元类,调不到
    ``GetFocusedElement``。两条路都试,哪条通用哪条。
    """
    try:
        import comtypes.client

        return comtypes.client.CreateObject(
            UIA.CUIAutomation, interface=UIA.IUIAutomation)
    except Exception:
        pass
    try:
        return UIA.CUIAutomation().QueryInterface(UIA.IUIAutomation)
    except Exception:
        return None


def _find_text_pattern(iuia, UIA, element, max_depth: int = 6):
    """从焦点元素往上找支持 TextPattern 的那个。

    为什么往上找而不是往下搜:很多应用(IDE、浏览器)的键盘焦点元素
    **本身**不带 TextPattern,它挂在 TextPattern 宿主里。往下搜全树
    会命中页面上其它无关的文本区域(那光标根本不在那儿)。

    往上找最多 6 层,够了 —— 再高就是应用的外壳了。
    """
    try:
        walker = iuia.ControlViewWalker
    except Exception:
        return None
    cur = element
    for _ in range(max_depth):
        if cur is None:
            return None
        try:
            pattern = cur.GetCurrentPattern(UIA.UIA_TextPatternId)
        except Exception:
            pattern = None      # 这个元素不支持 TextPattern
        if pattern is not None:
            return pattern
        try:
            cur = walker.GetParentElement(cur)
        except Exception:
            return None
    return None


def _rects_to_point(values) -> tuple[int, int] | None:
    """把 ``GetBoundingRectangles`` 的返回值变成一个锚点。

    返回的是 SAFEARRAY of double,**每 4 个 double 一个矩形**
    (left, top, width, height)。锚点取字符矩形的**左下角**:
    左边贴着光标(插入点在字符左侧),下边是行底 —— 胶囊正好落在
    文字**下面**,像输入法候选框。
    """
    try:
        vals = list(values)
    except TypeError:
        return None
    if len(vals) < 4:
        return None
    try:
        left, top, width, height = (float(v) for v in vals[:4])
    except (TypeError, ValueError):
        return None
    if width <= 0 and height <= 0:
        return None
    return int(left), int(top + height)


def _uia_caret_from_selection(text_pattern, UIA) -> tuple[int, int] | None:
    """用 TextPattern 的**选区**反推光标位置。

    为什么不用 ``GetCaretRange``:那个方法在
    ``IUIAutomationTextPattern2`` 上,而实测绝大多数应用(连 Electron
    的编辑器)只实现 v1 —— QI 到 v2 直接 E_NOINTERFACE。v1 上想拿
    光标,只能借"选区"这个概念:

    - 插入点(没有选中任何文字)时,``GetSelection()`` 返回一个**空**
      选区,空选区自己没有矩形(拿不到位置);
    - 把这个空选区**扩到它所在的字符**
      (``ExpandToEnclosingUnit(TextUnit_Character)``),就得到那个
      字符的矩形 —— 它的左下角就是光标位置。

    选区非空(用户选了一段文字)时,直接用选区矩形的左下角。
    """
    try:
        ranges = text_pattern.GetSelection()
    except Exception:
        return None
    try:
        if not ranges.Length:
            return None
        rng = ranges.GetElement(0)
    except Exception:
        return None

    # 选区非空:直接用它的矩形
    try:
        point = _rects_to_point(rng.GetBoundingRectangles())
    except Exception:
        point = None
    if point is not None:
        return point

    # 空选区(插入点):扩到所在字符
    try:
        clone = rng.Clone()
        clone.ExpandToEnclosingUnit(UIA.TextUnit_Character)
        return _rects_to_point(clone.GetBoundingRectangles())
    except Exception:
        return None


def get_caret_point_uia() -> tuple[int, int] | None:
    """用 UI Automation 拿光标位置,拿不到返回 None。

    走 ``GetFocusedElement`` → ``TextPattern`` → 选区/字符矩形。

    任何一步失败(应用不支持 UIA / COM 报错 / 没装 comtypes)都返回
    None,交给下一级。
    """
    UIA = _uia_module()
    if UIA is None:
        return None

    iuia = _uia_automation(UIA)
    if iuia is None:
        return None

    try:
        element = iuia.GetFocusedElement()
    except Exception:
        return None
    if element is None:
        return None

    pattern = _find_text_pattern(iuia, UIA, element)
    if pattern is None:
        return None

    # 优先 v2(有 GetCaretRange,最准);拿不到再退 v1(选区反推)
    for cls in (getattr(UIA, "IUIAutomationTextPattern2", None),
                getattr(UIA, "IUIAutomationTextPattern", None)):
        if cls is None:
            continue
        try:
            tp = pattern.QueryInterface(cls)
        except Exception:
            continue
        if tp is None:
            continue
        # v2 有 GetCaretRange,直接用
        getter = getattr(tp, "GetCaretRange", None)
        if getter is not None:
            try:
                rng = getter(True)
                point = _rects_to_point(rng.GetBoundingRectangles())
                if point is not None:
                    return point
            except Exception:
                pass
        point = _uia_caret_from_selection(tp, UIA)
        if point is not None:
            return point
    return None


def warmup() -> None:
    """提前把 UIA 的类型库加载好(可选依赖缺失时直接跳过)。

    第一次调用 ``GetModule`` + COM 初始化要几百毫秒 —— 那笔开销要是
    落在"用户刚按完热键"那一刻,胶囊会明显迟到。所以启动时先烧掉。
    """
    try:
        get_caret_point_uia()
    except Exception:
        pass


# ---------------- 第 2 级:GetGUIThreadInfo ----------------

def get_caret_rect_gui(foreground_hwnd: int = 0) -> tuple[int, int, int, int] | None:
    """传统控件的光标矩形 ``(left, top, right, bottom)``(**屏幕**坐标),
    拿不到返回 None。

    ⚠️ ``rcCaret`` 的坐标基准是个坑:MSDN 写的是"屏幕坐标",但**实测
    是焦点控件客户区的相对坐标**(真机:窗体放在屏幕 (200,200)、
    焦点控件在 (208,231),而 ``rcCaret`` 给的是 (0,0))。所以这里
    用 ``ClientToScreen`` 换算回屏幕坐标 —— 不换算的话胶囊会跑到
    屏幕左上角去。
    """
    if not foreground_hwnd:
        return None
    try:
        pid = wt.DWORD(0)
        thread_id = user32.GetWindowThreadProcessId(
            wt.HWND(foreground_hwnd), ctypes.byref(pid))
        if not thread_id:
            return None
        info = GUITHREADINFO()
        info.cbSize = ctypes.sizeof(GUITHREADINFO)
        if not user32.GetGUIThreadInfo(thread_id, ctypes.byref(info)):
            return None
        if not info.flags & GUI_CARETVISIBLE:
            return None
        r = info.rcCaret
        if r.right <= r.left and r.bottom <= r.top:
            return None

        left, top = int(r.left), int(r.top)
        # 换算成屏幕坐标(焦点控件的客户端区原点 + 控件内偏移)
        origin = wt.POINT(left, top)
        if info.hwndFocus:
            if user32.ClientToScreen(wt.HWND(info.hwndFocus),
                                    ctypes.byref(origin)):
                left, top = int(origin.x), int(origin.y)
        width = int(r.right - r.left)
        height = int(r.bottom - r.top)
        return left, top, left + width, top + height
    except Exception:
        return None


# ---------------- 第 3 级:鼠标 ----------------

def get_cursor_point() -> tuple[int, int] | None:
    """当前鼠标位置(物理像素)。"""
    try:
        point = wt.POINT()
        if user32.GetCursorPos(ctypes.byref(point)):
            return int(point.x), int(point.y)
    except Exception:
        pass
    return None


# ---------------- 回退链 ----------------

def capture_anchor(foreground_hwnd: int = 0,
                   exclude_hwnd: int = 0) -> Anchor | None:
    """按"越准越优先"的顺序取一个定位信号点。

    顺序:UIA 光标 → 传统控件光标 → 鼠标 → 前台窗口矩形。
    都不行就返回 None(调用方走"主屏底部居中"的最终兜底)。

    ``exclude_hwnd`` 是要**排除**的窗口句柄(一般传胶囊自己的):
    前台窗口要是自己,那锚点毫无意义(会算到自己脑袋上)。
    """
    point = get_caret_point_uia()
    if point is not None:
        return Anchor(point[0], point[1], "uia")

    rect = get_caret_rect_gui(foreground_hwnd)
    if rect is not None:
        left, _top, _right, bottom = rect
        # 取底边:胶囊贴在文字**下面**(输入法候选框的位置)
        return Anchor(int(left), int(bottom), "caret")

    point = get_cursor_point()
    if point is not None:
        return Anchor(point[0], point[1], "cursor")

    # 最后一档:只知道前台窗口在哪 —— 取窗口中心当锚点,
    # 重点是"它在哪块屏上"(工作区由此确定)
    if foreground_hwnd and foreground_hwnd != exclude_hwnd:
        try:
            from .api import get_window_rect

            rect = get_window_rect(foreground_hwnd)
        except Exception:
            rect = None
        if rect is not None:
            left, top, right, bottom = rect
            return Anchor(int((left + right) / 2), int((top + bottom) / 2),
                          "window")
    return None
