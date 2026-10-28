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

# ---------------- 修饰键与虚拟键码 ----------------

MOD_ALT = 0x0001
MOD_CONTROL = 0x0002
MOD_SHIFT = 0x0004
MOD_WIN = 0x0008
MOD_NOREPEAT = 0x4000

WM_HOTKEY = 0x0312
WM_GETTEXT = 0x000D
WM_GETTEXTLENGTH = 0x000E

SWP_NOSIZE = 0x0001
SWP_NOZORDER = 0x0004
SWP_NOACTIVATE = 0x0010

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

# ---------------- 纯悬浮窗的扩展样式 ----------------
# 做法照 cmd-dock 的路子(src/win32.rs):把窗口从"普通应用窗口"改成
# "工具窗口",于是它不进任务栏、也不进 Alt+Tab,点击还不抢焦点。
GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW = 0x00000080      # 不在任务栏、不在 Alt+Tab
WS_EX_APPWINDOW = 0x00040000       # 一旦置上,上面那条就失效
WS_EX_NOACTIVATE = 0x08000000      # 点击不抢焦点
SW_HIDE = 0

user32.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
user32.GetWindowLongPtrW.restype = ctypes.c_ssize_t
user32.SetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int, ctypes.c_ssize_t]
user32.SetWindowLongPtrW.restype = ctypes.c_ssize_t

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
