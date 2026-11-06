"""Win32 API 绑定。

把 ctypes 的签名声明集中在一处 —— 之前踩过的坑:
ctypes 默认把返回值当 c_int,64 位句柄(HWND/HGLOBAL)会被截断,
写到非法地址会让进程直接崩(0xC0000005)。所以这里逐个显式声明。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
gdi32 = ctypes.windll.gdi32

# ---------------- 修饰键与虚拟键码 ----------------

MOD_ALT = 0x0001
MOD_CONTROL = 0x0002
MOD_SHIFT = 0x0004
MOD_WIN = 0x0008
MOD_NOREPEAT = 0x4000

WM_HOTKEY = 0x0312
WM_QUIT = 0x0012
WM_GETTEXT = 0x000D
WM_GETTEXTLENGTH = 0x000E

SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_NOZORDER = 0x0004
SWP_NOACTIVATE = 0x0010
HWND_TOPMOST = -1

KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004

INPUT_KEYBOARD = 1

VK_BACK = 0x08
VK_TAB = 0x09
VK_RETURN = 0x0D
VK_SHIFT = 0x10
VK_CONTROL = 0x11
VK_MENU = 0x12
VK_SPACE = 0x20
VK_ESCAPE = 0x1B
VK_LEFT = 0x25
VK_UP = 0x26
VK_RIGHT = 0x27
VK_DOWN = 0x28
VK_V = 0x56
VK_LWIN = 0x5B
VK_RWIN = 0x5C

# 字母/数字 A-Z -> 虚拟键码
for _i in range(26):
    globals()[f"VK_{chr(65 + _i)}"] = 0x41 + _i
for _i in range(10):
    globals()[f"VK_{_i}"] = 0x30 + _i
for _i in range(1, 25):
    globals()[f"VK_F{_i}"] = 0x6F + _i

# ---------------- 签名声明 ----------------

# 全局内存
kernel32.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
kernel32.GlobalAlloc.restype = wt.HGLOBAL
kernel32.GlobalLock.argtypes = [wt.HGLOBAL]
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalUnlock.argtypes = [wt.HGLOBAL]
kernel32.GlobalUnlock.restype = wt.BOOL
kernel32.GlobalFree.argtypes = [wt.HGLOBAL]
kernel32.GlobalFree.restype = wt.HGLOBAL
kernel32.GetCurrentThreadId.restype = wt.DWORD

# 窗口
user32.GetForegroundWindow.restype = wt.HWND
user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.SetForegroundWindow.argtypes = [wt.HWND]
user32.SetForegroundWindow.restype = wt.BOOL
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
user32.GetWindowThreadProcessId.restype = wt.DWORD
user32.AttachThreadInput.argtypes = [wt.DWORD, wt.DWORD, wt.BOOL]
user32.AttachThreadInput.restype = wt.BOOL
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.IsWindowVisible.restype = wt.BOOL
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]
user32.FindWindowW.restype = wt.HWND
user32.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
user32.ShowWindow.restype = wt.BOOL

# 窗口几何
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.PostMessageW.restype = wt.BOOL
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetWindowRect.restype = wt.BOOL
user32.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
user32.GetCursorPos.restype = wt.BOOL
user32.SetWindowPos.argtypes = [
    wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int,
    ctypes.c_int, ctypes.c_int, wt.UINT,
]
user32.SetWindowPos.restype = wt.BOOL

# 工作区(任务栏之外的可用区域)与显示器信息
user32.SystemParametersInfoW.argtypes = [wt.UINT, wt.UINT, ctypes.c_void_p, wt.UINT]
user32.SystemParametersInfoW.restype = wt.BOOL
user32.GetMonitorInfoW.argtypes = [wt.HMONITOR, ctypes.c_void_p]
user32.GetMonitorInfoW.restype = wt.BOOL
user32.MonitorFromPoint.argtypes = [wt.POINT, wt.DWORD]
user32.MonitorFromPoint.restype = wt.HMONITOR
user32.MonitorFromWindow.argtypes = [wt.HWND, wt.DWORD]
user32.MonitorFromWindow.restype = wt.HMONITOR

# 模态消息框(热键注册失败之类的启动期错误)
user32.MessageBoxW.argtypes = [wt.HWND, wt.LPCWSTR, wt.LPCWSTR, wt.UINT]
user32.MessageBoxW.restype = ctypes.c_int

# ---------------- 纯悬浮窗的扩展样式 ----------------
# 做法照 cmd-dock 的路子(src/win32.rs):把窗口从"普通应用窗口"改成
# "工具窗口",于是它不进任务栏、也不进 Alt+Tab,点击还不抢焦点。
GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW = 0x00000080      # 不在任务栏、不在 Alt+Tab
WS_EX_APPWINDOW = 0x00040000       # 一旦置上,上面那条就失效
WS_EX_NOACTIVATE = 0x08000000      # 点击不抢焦点
SW_HIDE = 0
SW_SHOWNOACTIVATE = 4              # 显示但不激活(不抢焦点)
SPI_GETWORKAREA = 0x0030
MONITOR_DEFAULTTONEAREST = 2
MB_OK = 0x00000000
MB_ICONERROR = 0x00000010

user32.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
user32.GetWindowLongPtrW.restype = ctypes.c_ssize_t
user32.SetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int, ctypes.c_ssize_t]
user32.SetWindowLongPtrW.restype = ctypes.c_ssize_t

# ---------------- 窗口区域(SetWindowRgn) ----------------
# 把窗口的**命中区域**裁成指定形状:区域外的像素既不画也不吃鼠标,
# 点击会直接穿透到底下的窗口。胶囊用它把大片透明空白从窗口里挖掉
# (胶囊是固定尺寸的透明窗口,空白区虽然看不见,却会挡住底下的应用)。

# CombineRgn 的合并方式(wingdi.h):RGN_AND=1, RGN_OR=2, RGN_XOR=3,
# RGN_DIFF=4, RGN_COPY=5 —— **没有 0**!曾把 RGN_OR 写成 0(那是
# ERROR),CombineRgn 静默失败,第二块矩形被丢弃,窗口只剩一块区域
# (真机 GetWindowRgn 实测抓到的)。
RGN_OR = 2
# CombineRgn 的返回值(wingdi.h):0 = ERROR,1 = NULLREGION,
# 2 = SIMPLEREGION(单矩形),3 = COMPLEXREGION(多矩形)
NULLREGION = 1
SIMPLEREGION = 2
COMPLEXREGION = 3

gdi32.CreateRectRgn.argtypes = [ctypes.c_int, ctypes.c_int,
                                ctypes.c_int, ctypes.c_int]
gdi32.CreateRectRgn.restype = wt.HRGN
gdi32.CombineRgn.argtypes = [wt.HRGN, wt.HRGN, wt.HRGN, ctypes.c_int]
gdi32.CombineRgn.restype = ctypes.c_int
gdi32.DeleteObject.argtypes = [wt.HANDLE]
gdi32.DeleteObject.restype = wt.BOOL
user32.SetWindowRgn.argtypes = [wt.HWND, wt.HRGN, wt.BOOL]
user32.SetWindowRgn.restype = wt.BOOL

# 剪贴板
user32.OpenClipboard.argtypes = [wt.HWND]
user32.OpenClipboard.restype = wt.BOOL
user32.CloseClipboard.restype = wt.BOOL
user32.EmptyClipboard.restype = wt.BOOL
user32.GetClipboardData.argtypes = [wt.UINT]
user32.GetClipboardData.restype = wt.HANDLE
user32.SetClipboardData.argtypes = [wt.UINT, wt.HANDLE]
user32.SetClipboardData.restype = wt.HANDLE

# 热键
user32.RegisterHotKey.argtypes = [wt.HWND, ctypes.c_int, wt.UINT, wt.UINT]
user32.RegisterHotKey.restype = wt.BOOL
user32.UnregisterHotKey.argtypes = [wt.HWND, ctypes.c_int]
user32.UnregisterHotKey.restype = wt.BOOL
user32.GetMessageW.argtypes = [ctypes.POINTER(wt.MSG), wt.HWND, wt.UINT, wt.UINT]
user32.GetMessageW.restype = ctypes.c_int
user32.PostQuitMessage.argtypes = [ctypes.c_int]
# PostThreadMessageW:往**指定线程**的消息队列投递消息。
# ⚠ user32 里**没有**裸的 PostThreadMessage 导出(只有 A/W 两个后缀版) ——
#   写成 PostThreadMessage 会在 import 时就 AttributeError。
# PostQuitMessage 只会投给**调用线程自己**的队列 —— 想叫停别的线程
# 的 GetMessage 循环,必须用它(见 hotkey.py 的 stop())。
user32.PostThreadMessageW.argtypes = [wt.DWORD, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.PostThreadMessageW.restype = wt.BOOL

# 键盘输入
user32.keybd_event.argtypes = [wt.BYTE, wt.BYTE, wt.DWORD, ctypes.c_ulong]


# ---- SendInput ----
# keybd_event 是老 API,投递成功与否完全不知道;SendInput 会返回实际
# 投递的事件数,注入失败时才能识别出来(比如被 UIPI 拦掉)。

class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD),
                ("dwExtraInfo", ctypes.c_size_t)]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wt.DWORD), ("wParamL", wt.WORD), ("wParamH", wt.WORD)]


class _INPUTunion(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _INPUTunion)]


user32.SendInput.argtypes = [wt.UINT, ctypes.c_void_p, ctypes.c_int]
user32.SendInput.restype = wt.UINT

# 键盘状态
user32.GetAsyncKeyState.argtypes = [ctypes.c_int]
user32.GetAsyncKeyState.restype = ctypes.c_short

# 剪贴板归属
user32.GetClipboardSequenceNumber.restype = wt.DWORD
user32.GetClipboardOwner.restype = wt.HWND
user32.IsWindow.argtypes = [wt.HWND]
user32.IsWindow.restype = wt.BOOL

# ---------------- 通用辅助 ----------------


def get_foreground_window() -> int:
    """当前前台窗口句柄。"""
    return user32.GetForegroundWindow()


def get_window_title(hwnd: int) -> str:
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value


def get_class_name(hwnd: int) -> str:
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def restore_foreground(hwnd: int) -> bool:
    """把焦点还给指定窗口。

    AttachThreadInput 是关键:不挂线程的话 SetForegroundWindow 在
    后台线程调用时经常失败(Windows 的前台锁策略)。
    """
    if not hwnd:
        return False
    try:
        fg = get_foreground_window()
        cur_thread = kernel32.GetCurrentThreadId()
        pid = wt.DWORD(0)
        target_thread = user32.GetWindowThreadProcessId(fg, ctypes.byref(pid))
        attached = False
        if target_thread and target_thread != cur_thread:
            user32.AttachThreadInput(target_thread, cur_thread, True)
            attached = True
        ok = bool(user32.SetForegroundWindow(hwnd))
        if attached:
            user32.AttachThreadInput(target_thread, cur_thread, False)
        return ok
    except Exception:
        try:
            return bool(user32.SetForegroundWindow(hwnd))
        except Exception:
            return False


def send_ctrl_v() -> int:
    """模拟 Ctrl+V,返回**实际投递**的事件数(4 = 全部成功)。

    用 SendInput 而不是 keybd_event:后者投递成功与否完全不可知,
    被 UIPI 拦掉时也照样"成功",表现出来就是"按了热键没反应"。
    SendInput 只插入数组前缀,返回实际插入的个数 —— 靠它才能知道
    到底有没有发出去,以及发出去几个。

    投递不全时**只补发抬起事件**,绝不重贴:重贴会造成重复文本。
    """
    keys = []
    for vk, up in ((VK_CONTROL, False), (VK_V, False), (VK_V, True), (VK_CONTROL, True)):
        ki = KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP if up else 0, 0, 0)
        keys.append(INPUT(INPUT_KEYBOARD, _INPUTunion(ki=ki)))

    sent = user32.SendInput(len(keys), (INPUT * len(keys))(*keys), ctypes.sizeof(INPUT))
    if sent < len(keys):
        # 只把**确实按下过**的键抬起来:
        #   索引 0 = Ctrl down,索引 1 = V down
        release = []
        if sent >= 2:
            release.append(VK_V)
        if sent >= 1:
            release.append(VK_CONTROL)
        if release:
            ups = []
            for vk in release:
                ki = KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, 0)
                ups.append(INPUT(INPUT_KEYBOARD, _INPUTunion(ki=ki)))
            user32.SendInput(len(ups), (INPUT * len(ups))(*ups), ctypes.sizeof(INPUT))
    return int(sent)


def key_is_down(vk: int) -> bool:
    """某个键当前是否被按住。"""
    return bool(user32.GetAsyncKeyState(vk) & 0x8000)


def get_window_rect(hwnd: int) -> tuple[int, int, int, int] | None:
    """窗口在屏幕上的**物理像素**位置 (left, top, right, bottom)。"""
    rect = wt.RECT()
    if not user32.GetWindowRect(wt.HWND(hwnd), ctypes.byref(rect)):
        return None
    return rect.left, rect.top, rect.right, rect.bottom


def move_window_to(hwnd: int, x: int, y: int) -> bool:
    """把窗口左上角移到**物理像素** (x, y),不改大小、不抢焦点、不动 Z 序。"""
    return bool(user32.SetWindowPos(
        wt.HWND(hwnd), None, x, y, 0, 0,
        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))


def modifiers_held() -> bool:
    """用户此刻是否正按着 Ctrl / Alt / Shift / Win。

    按着的时候发 Ctrl+V,目标应用收到的是 Ctrl+Alt+V 之类,
    粘贴根本不会触发 —— 这种时候宁可放弃注入,也不要往目标窗口
    塞一个莫名其妙的东西。
    """
    return any(key_is_down(vk) for vk in (VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN))


def set_tool_window_style(hwnd: int) -> bool:
    """把窗口改成"工具窗口":不进任务栏、不进 Alt+Tab、点击不抢焦点。

    三个扩展样式各司其职:
      WS_EX_TOOLWINDOW  不在任务栏、也不在 Alt+Tab 列表
      去掉 WS_EX_APPWINDOW  否则上面那条会被抵消(它是"按应用窗口处理")
      WS_EX_NOACTIVATE  点击它不抢焦点 —— 点面板按钮时,底下正在编辑的
                        窗口不会失焦,识别结果也就不会贴错地方
    """
    if not hwnd:
        return False
    try:
        ex = user32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE)
        new_ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW
        user32.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new_ex)
        return True
    except Exception:
        return False


def make_tool_window(hwnd: int) -> bool:
    """同上,但**先隐藏再改样式**。

    任务栏只在窗口显示时重新评估可见性,顺序反了的话会先冒出一个
    任务栏按钮、然后才消失(能看到它闪一下)。cmd-dock 也是这个顺序。
    """
    if not hwnd:
        return False
    try:
        user32.ShowWindow(hwnd, SW_HIDE)
    except Exception:
        pass
    return set_tool_window_style(hwnd)


def is_window_visible(hwnd: int) -> bool:
    """窗口当前是否可见。

    胶囊要用它判断"pywebview 是不是偷偷把窗口 Show 出来了" ——
    透明窗口的 pywebview 建窗流程会自己 Show 一次,窗口不该在那时候露面。
    """
    if not hwnd:
        return False
    try:
        return bool(user32.IsWindowVisible(wt.HWND(hwnd)))
    except Exception:
        return False


def show_window_no_activate(hwnd: int) -> bool:
    """显示窗口但**不激活**它(不抢焦点)。

    胶囊窗必须这么显示:它出现的时机正是用户在别的窗口里打字的时候,
    一旦抢了焦点,后面注入的文字就没有落点了 —— 光标会被抢到胶囊上,
    文字全丢。

    纯 Win32 调用,不走 pywebview 的 ``window.show()`` ——
    那个内部会调 WinForms 的 ``Activate()``,必然抢焦点。
    """
    if not hwnd:
        return False
    try:
        user32.ShowWindow(hwnd, SW_SHOWNOACTIVATE)
        return True
    except Exception:
        return False


def hide_window(hwnd: int) -> bool:
    """隐藏窗口(Win32 直调,比 pywebview 的 hide 轻量、不阻塞)。"""
    if not hwnd:
        return False
    try:
        user32.ShowWindow(hwnd, SW_HIDE)
        return True
    except Exception:
        return False


def resize_window_no_activate(hwnd: int, width: int, height: int) -> bool:
    """把窗口改成指定的**物理像素**尺寸。

    不用 pywebview 的 ``window.resize()``:它在 winforms 后端里是
    ``SetWindowPos(hwnd, None, x, y, w, h, SWP_SHOWWINDOW)`` ——
    两个副作用都要命:

    - ``SWP_SHOWWINDOW`` 会**顺带把隐藏的窗口显示出来**
      (面板/胶囊明明是隐藏的,却因为一次 resize 冒了出来);
    - 没有 ``SWP_NOACTIVATE``,会**抢焦点** —— 胶囊最忌讳这个,
      抢了焦点后面注入的文字就没有落点了。

    这里自己发 SetWindowPos,三个都不要:NOMOVE / NOZORDER / NOACTIVATE,
    也不带 SWP_SHOWWINDOW。
    """
    if not hwnd:
        return False
    try:
        return bool(user32.SetWindowPos(
            wt.HWND(hwnd), None, 0, 0, int(width), int(height),
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE))
    except Exception:
        return False


def bring_to_top(hwnd: int) -> bool:
    """把窗口提到**置顶组的最顶端**(不移动、不改大小、不抢焦点)。

    别的置顶窗口(比如各种 AI 助手、录屏工具)可能正好压在胶囊上面,
    显示时把我们提到最顶端才看得见。
    """
    if not hwnd:
        return False
    try:
        return bool(user32.SetWindowPos(
            wt.HWND(hwnd), wt.HWND(HWND_TOPMOST), 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
    except Exception:
        return False


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD),
                ("rcMonitor", wt.RECT),
                ("rcWork", wt.RECT),
                ("dwFlags", wt.DWORD)]


def get_work_area(hwnd: int = 0) -> tuple[int, int, int, int] | None:
    """工作区(任务栏之外的可用区域)的**物理像素**矩形。

    传 hwnd 就取该窗口所在显示器的作区,否则取主显示器的工作区。

    不能用 ``GetSystemMetrics(SM_CYSCREEN)`` 算底部位置 ——
    那是整块屏幕的高度,任务栏在下面,窗口会被任务栏盖住一半。
    """
    try:
        if hwnd:
            monitor = user32.MonitorFromWindow(
                wt.HWND(hwnd), MONITOR_DEFAULTTONEAREST)
            if monitor:
                info = MONITORINFO()
                info.cbSize = ctypes.sizeof(MONITORINFO)
                if user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
                    r = info.rcWork
                    return r.left, r.top, r.right, r.bottom

        rect = wt.RECT()
        if user32.SystemParametersInfoW(
                SPI_GETWORKAREA, 0, ctypes.byref(rect), 0):
            return rect.left, rect.top, rect.right, rect.bottom
    except Exception:
        pass
    return None


def set_no_activate_tree(hwnd: int) -> int:
    """给窗口及其**所有后代子窗口**加 WS_EX_NOACTIVATE,返回改了几个。

    只给顶层窗口加是不够的:WebView2 在里面自建了子窗口
    (Chrome_WidgetWin 一族),鼠标实际点到的是**子窗口** ——
    子窗口没有 NOACTIVATE 的话,点击照样激活整棵窗口树,
    前台被胶囊抢走。实测后果很具体:用户点胶囊上的 ✓ 结束录音,
    前台变成胶囊,随后的 Ctrl+V 注入发给了胶囊自己,文字全丢。

    每次"显示胶囊"时都要重刷一遍:WebView2 导航/重建页面时
    可能换掉子窗口,新窗口不会继承扩展样式。
    """
    if not hwnd:
        return 0

    changed = 0

    def _add_noactivate(h, _lparam):
        nonlocal changed
        try:
            ex = user32.GetWindowLongPtrW(h, GWL_EXSTYLE)
            if not ex & WS_EX_NOACTIVATE:
                user32.SetWindowLongPtrW(
                    h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE)
                changed += 1
        except Exception:
            pass
        return True  # 继续枚举

    WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    _add_noactivate(hwnd, 0)
    user32.EnumChildWindows(
        wt.HWND(hwnd), WNDENUMPROC(_add_noactivate), 0)
    return changed


def set_window_regions(hwnd: int, rects: list[tuple[int, int, int, int]],
                       ellipses: set[tuple[int, int, int, int]] | None = None) -> bool:
    """把窗口的命中区域裁成几块的**并集**(物理像素、窗口坐标)。

    为什么要裁:胶囊是**固定尺寸**的透明窗口 —— 透明窗口一旦被 resize,
    WebView2 的透明背景就废(见 capsule.py 的说明),所以窗口里留了一大片
    空白。那片空白虽然**看不见**,却**属于窗口**:鼠标点上去会被胶囊吃掉,
    底下的应用就点不到了。用 SetWindowRgn 把窗口的形状改成"文字条 + 胶囊",
    空白区就不再属于窗口,点击直接穿透 —— 不 resize、不动透明实现。

    坐标是**相对窗口左上角**的物理像素(MSDN:窗口区域的坐标相对窗口
    左上角,不是客户区)。胶囊是 frameless 的,窗口原点 == 客户区原点,
    所以前端 ``getBoundingClientRect()`` 的视口坐标可以直接拿来用。

    ``ellipses`` 里的那些块用**椭圆**而不是矩形(坐标相等匹配)。

    为什么需要椭圆:窗口裁出来的区域里,WebView2 透明的地方透出的是
    **Form 的灰底**(pywebview 的 transparent 分支不设 Form.BackColor,
    默认就是 WinForms 的 #F0F0F0)。所以裁形只要比不透明内容大一点,
    就会露出一圈灰。球形态尤其明显:球是**圆形**,用矩形裁就是四个角
    露灰(用户看到的"球周围包着一小块灰色背景")。用椭圆裁,形状精确
    贴合圆球,一丝灰都不露。

    区域句柄的生命周期(照 MSDN 的语义写,别想当然):
      - ``SetWindowRgn`` **成功后区域归系统所有**,而且系统**不拷贝**,
        调用方绝不能再碰这个句柄、更不能 DeleteObject —— 系统自己会删;
      - 失败时区域还在我们手里,必须 DeleteObject 释放,不然泄漏 GDI 对象。
    """
    if not hwnd or not rects:
        return False

    ellipses = ellipses or set()
    region = None
    try:
        for (left, top, right, bottom) in rects:
            piece = None
            if (left, top, right, bottom) in ellipses:
                piece = gdi32.CreateEllipticRgn(int(left), int(top),
                                                int(right), int(bottom))
            else:
                piece = gdi32.CreateRectRgn(int(left), int(top),
                                            int(right), int(bottom))
            if not piece:
                continue
            if region is None:
                region = piece
                continue
            # MSDN 明说"三个区域不必互不相同",所以 dst 直接复用自己
            combined = gdi32.CombineRgn(region, region, piece, RGN_OR)
            # 合并完这块就没用了,还是我们自己的句柄 —— 删掉
            gdi32.DeleteObject(piece)
            if not combined:
                # 返回 0 = 空区域或出错,这块没并进去(继续用已有的)
                continue
        if region is None:
            return False
        if user32.SetWindowRgn(wt.HWND(hwnd), region, True):
            # 成功:区域归系统了,再碰它就是用已释放的句柄
            region = None
            return True
        return False
    except Exception:
        return False
    finally:
        if region is not None:
            # 只有失败路径会走到这:区域还在我们手里,得自己释放
            gdi32.DeleteObject(region)


def show_error_box(text: str, caption: str = "Qwen3 语音输入") -> bool:
    """弹一个系统级错误对话框。

    面板瘦身后没有状态行了,启动期的致命错误(比如热键注册失败)
    用这个弹窗告诉用户 —— 托盘图标上的提示太容易被忽略。
    """
    try:
        user32.MessageBoxW(None, text, caption, MB_OK | MB_ICONERROR)
        return True
    except Exception:
        return False
