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
gdi32.CreateEllipticRgn.argtypes = [ctypes.c_int, ctypes.c_int,
                                    ctypes.c_int, ctypes.c_int]
gdi32.CreateEllipticRgn.restype = wt.HRGN
gdi32.CreateRoundRectRgn.argtypes = [ctypes.c_int, ctypes.c_int,
                                     ctypes.c_int, ctypes.c_int,
                                     ctypes.c_int, ctypes.c_int]
gdi32.CreateRoundRectRgn.restype = wt.HRGN
gdi32.CombineRgn.argtypes = [wt.HRGN, wt.HRGN, wt.HRGN, ctypes.c_int]
gdi32.CombineRgn.restype = ctypes.c_int
gdi32.DeleteObject.argtypes = [wt.HANDLE]
gdi32.DeleteObject.restype = wt.BOOL
user32.SetWindowRgn.argtypes = [wt.HWND, wt.HRGN, wt.BOOL]
user32.SetWindowRgn.restype = wt.BOOL

# ---------------- 分层窗口(WS_EX_LAYERED):窗口内容自己上传 ----------------
#
# 为什么不用 WinForms 的 TransparencyKey:
#   TransparencyKey 走的是"PrintWindow 抓窗口画面 -> 按键色抠 alpha ->
#   UpdateLayeredWindow"。而 PrintWindow **抓不到 WebView2 的
#   DirectComposition 内容**(实测:重绘一次,WebView2 的画面就被吞掉,
#   屏幕上只剩 Form 的背景色;显隐切换能救回来)。这条路在 WebView2 上不成立。
#
# 自己上传 surface 就没有这个问题,而且带来一个关键好处:
#   **视觉**和**命中测试**读的是两样东西。
#     - 视觉:WebView2 的 DComp 内容盖在 surface 之上,半透明元素照旧
#       与桌面混合(真透明);
#     - 命中:Windows 对 layered 窗口是**逐像素**看 surface 的 alpha 的,
#       alpha=0 处点击穿透,alpha>0 处算本窗口 -> 命中区可以用
#       SetWindowRgn 精确表达(空白区穿透、球/胶囊可点)。
WS_EX_LAYERED = 0x00080000
ULW_ALPHA = 0x00000002          # UpdateLayeredWindow:用位图自带的 alpha
AC_SRC_OVER = 0x00
AC_SRC_ALPHA = 0x01             # BlendFunction.AlphaFormat:源位图自带 alpha
BI_RGB = 0
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    """CreateDIBSection 用的位图头(32bpp、top-down)。"""

    _fields_ = [
        ("biSize", wt.DWORD),
        ("biWidth", wt.LONG),
        ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD),
        ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD),
        ("biXPelsPerMeter", wt.LONG),
        ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [
        ("bmiHeader", BITMAPINFOHEADER),
        ("bmiColors", wt.DWORD * 3),
    ]


class BLENDFUNCTION(ctypes.Structure):
    _fields_ = [
        ("BlendOp", ctypes.c_byte),
        ("BlendFlags", ctypes.c_byte),
        ("SourceConstantAlpha", ctypes.c_byte),
        ("AlphaFormat", ctypes.c_byte),
    ]


gdi32.CreateDIBSection.argtypes = [wt.HDC, ctypes.POINTER(BITMAPINFO),
                                   wt.UINT, ctypes.POINTER(ctypes.c_void_p),
                                   wt.HANDLE, wt.DWORD]
gdi32.CreateDIBSection.restype = wt.HBITMAP
gdi32.CreateCompatibleDC.argtypes = [wt.HDC]
gdi32.CreateCompatibleDC.restype = wt.HDC
gdi32.SelectObject.argtypes = [wt.HDC, wt.HGDIOBJ]
gdi32.SelectObject.restype = wt.HGDIOBJ
gdi32.DeleteDC.argtypes = [wt.HDC]
gdi32.DeleteDC.restype = wt.BOOL
user32.GetDC.argtypes = [wt.HWND]
user32.GetDC.restype = wt.HDC
user32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
user32.UpdateLayeredWindow.argtypes = [
    wt.HWND, wt.HDC,
    ctypes.POINTER(wt.POINT), ctypes.POINTER(wt.SIZE),
    wt.HDC, ctypes.POINTER(wt.POINT),
    wt.DWORD, ctypes.POINTER(BLENDFUNCTION), wt.DWORD,
]
user32.UpdateLayeredWindow.restype = wt.BOOL

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
        cur_thread = kernel32.GetCurrentThreadId()
        attached: list[int] = []
        for target in (get_foreground_window(), hwnd):
            if not target:
                continue
            pid = wt.DWORD(0)
            tid = user32.GetWindowThreadProcessId(
                wt.HWND(target), ctypes.byref(pid))
            if tid and tid != cur_thread and tid not in attached:
                user32.AttachThreadInput(tid, cur_thread, True)
                attached.append(tid)
        ok = bool(user32.SetForegroundWindow(hwnd))
        for tid in attached:
            user32.AttachThreadInput(tid, cur_thread, False)
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


def held_modifiers() -> set[str]:
    """此刻正按着的修饰键集合("ctrl" / "alt" / "shift" / "win")。

    注入的"等松键"策略需要知道**具体是谁**按着:
    只剩 Ctrl 时可以顺势只补一个 V(用户的 Ctrl + 我们的 V = 粘贴),
    Alt/Shift 按着时发什么都不是普通粘贴,只能等。
    """
    mods: set[str] = set()
    if key_is_down(VK_CONTROL):
        mods.add("ctrl")
    if key_is_down(VK_MENU):
        mods.add("alt")
    if key_is_down(VK_SHIFT):
        mods.add("shift")
    if key_is_down(VK_LWIN) or key_is_down(VK_RWIN):
        mods.add("win")
    return mods


def send_v() -> int:
    """只发 V↓/V↑(不带 Ctrl),返回实际投递数(2 = 全部成功)。

    用在"用户自己按住 Ctrl、我们顺势补 V"的路径 —— Ctrl 是用户
    手指按着的,状态真实存在;我们**绝不能替他发 Ctrl↓/Ctrl↑**:
    合成的 Ctrl↓ 会跟物理按键状态打架,合成的 Ctrl↑ 会打断他
    继续按住的本意。只补 V,抬手的事交给用户自己的手指。

    投递不全时只补发 V 的抬起(V↓ 已投 = 粘贴已触发,绝不重发)。
    """
    keys = []
    for vk, up in ((VK_V, False), (VK_V, True)):
        ki = KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP if up else 0, 0, 0)
        keys.append(INPUT(INPUT_KEYBOARD, _INPUTunion(ki=ki)))
    sent = user32.SendInput(len(keys), (INPUT * len(keys))(*keys), ctypes.sizeof(INPUT))
    if 0 < sent < len(keys):
        # V↓ 投出去了但 V↑ 没有:补抬,别留一个"按住的 V"
        ups = [INPUT(INPUT_KEYBOARD,
                     _INPUTunion(ki=KEYBDINPUT(VK_V, 0, KEYEVENTF_KEYUP, 0, 0)))]
        user32.SendInput(len(ups), (INPUT * len(ups))(*ups), ctypes.sizeof(INPUT))
    return int(sent)


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
                       ellipses: set[tuple[int, int, int, int]] | None = None,
                       round_rects: dict[tuple[int, int, int, int], int] | None = None
                       ) -> bool:
    """把窗口的命中区域裁成几块的**并集**(物理像素、窗口坐标)。

    为什么要裁:胶囊是**固定尺寸**的透明窗口 —— 透明窗口一旦被 resize,
    WebView2 的透明背景就废(见 capsule.py 的说明),所以窗口里留了一大片
    空白。那片空白虽然**看不见**,却**属于窗口**:鼠标点上去会被胶囊吃掉,
    底下的应用就点不到了。用 SetWindowRgn 把窗口的形状改成"文字条 + 胶囊",
    空白区就不再属于窗口,点击直接穿透 —— 不 resize、不动透明实现。

    坐标是**相对窗口左上角**的物理像素(MSDN:窗口区域的坐标相对窗口
    左上角,不是客户区)。胶囊是 frameless 的,窗口原点 == 客户区原点,
    所以前端 ``getBoundingClientRect()`` 的视口坐标可以直接拿来用。

    ``ellipses`` 里的那些块用**椭圆**而不是矩形(坐标相等匹配);
    ``round_rects`` 里的块用**圆角矩形**(键是坐标,值是圆角半径的物理像素)。

    形状要**贴合真实外形**:裁出来的区域里,WebView2 透明的地方透出的是
    桌面的颜色(真透明之后)。形状只要比不透明内容大一点,就会露出一圈
    多余的"能点但看不见"的边;反过来比内容小一点,就会把内容的边缘切掉。
    所以:
      - 胶囊是**跑道形**(两端是半圆),用矩形裁会把两端切掉一截 ——
        规格里说的 17.9% 误切就是拿椭圆裁跑道形裁出来的;
      - 球是**圆形**,用矩形裁四个角会多出一块。

    区域句柄的生命周期(照 MSDN 的语义写,别想当然):
      - ``SetWindowRgn`` **成功后区域归系统所有**,而且系统**不拷贝**,
        调用方绝不能再碰这个句柄、更不能 DeleteObject —— 系统自己会删;
      - 失败时区域还在我们手里,必须 DeleteObject 释放,不然泄漏 GDI 对象。
    """
    if not hwnd or not rects:
        return False

    ellipses = ellipses or set()
    round_rects = round_rects or {}
    region = None
    try:
        for (left, top, right, bottom) in rects:
            piece = None
            if (left, top, right, bottom) in ellipses:
                piece = gdi32.CreateEllipticRgn(int(left), int(top),
                                                int(right), int(bottom))
            elif (left, top, right, bottom) in round_rects:
                # 圆角半径 = 宽/高的较小值 -> 变成"跑道形"(胶囊)
                radius = max(0, min(int(round_rects[(left, top, right, bottom)]),
                                    int(right) - int(left),
                                    int(bottom) - int(top)))
                piece = gdi32.CreateRoundRectRgn(int(left), int(top),
                                                 int(right), int(bottom),
                                                 radius, radius)
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


def enable_layered_window(hwnd: int) -> bool:
    """给窗口加 ``WS_EX_LAYERED``:窗口内容改由我们自己上传。

    加了它,窗口才支持**逐像素 alpha** —— 真透明(能透出桌面)和
    "点击按 alpha 决定穿透与否"都靠它。

    必须在窗口显示**之前**加:换扩展样式会让系统重算窗口的样子。
    """
    if not hwnd:
        return False
    try:
        ex = user32.GetWindowLongPtrW(wt.HWND(hwnd), GWL_EXSTYLE)
        if ex & WS_EX_LAYERED:
            return True
        return bool(user32.SetWindowLongPtrW(
            wt.HWND(hwnd), GWL_EXSTYLE, ex | WS_EX_LAYERED))
    except Exception:
        return False


def upload_transparent_surface(hwnd: int, alpha: int = 1) -> bool:
    """给分层窗口上传一张"几乎全透明"的 surface(整窗同一个 alpha)。

    alpha 为什么不是 0:
      Windows 对 layered 窗口是**逐像素**看 surface 的 alpha 决定命中的。
      整窗 alpha=0 的话,系统认为整个窗口都是透明的 —— 连 WebView2
      画出来的那颗球也一起算成透明,球就点不到了(实测)。
      给整窗一个极小的 alpha(默认 1/255):
        - 命中:整窗都算"本窗口",形状交给 ``SetWindowRgn`` 去表达;
        - 视觉:1/255 的黑几乎看不见 —— 实测半透明卡片从 (15,15,255)
          变成 (15,15,254),蓝通道差 1,肉眼与像素验收都够。

    为什么用黑色:surface 的颜色会参与 WebView2 半透明元素的混合
    (DComp 内容盖在 surface 之上,底下就是 surface 的颜色)。黑色
    让混合结果只暗一点点;换成任何有彩色都会把界面染色。

    注意:重新上传会**吞掉 WebView2 的画面**(DWM 要按新的 surface 重新
    裁剪子窗口,而 WebView2 不会主动重绘),所以这张 surface 只在
    窗口尺寸变化时才重传。
    """
    if not hwnd:
        return False
    rect = wt.RECT()
    if not user32.GetWindowRect(wt.HWND(hwnd), ctypes.byref(rect)):
        return False
    width = rect.right - rect.left
    height = rect.bottom - rect.top
    if width <= 0 or height <= 0:
        return False
    alpha = max(0, min(255, int(alpha)))

    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = width
    bmi.bmiHeader.biHeight = -height        # 负数 = top-down,和屏幕坐标一致
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = BI_RGB

    hdc_screen = user32.GetDC(None)
    if not hdc_screen:
        return False
    hbm = None
    hdc_mem = None
    old_obj = None
    try:
        ppv = ctypes.c_void_p()
        hbm = gdi32.CreateDIBSection(hdc_screen, ctypes.byref(bmi),
                                     DIB_RGB_COLORS, ctypes.byref(ppv),
                                     None, 0)
        if not hbm:
            return False
        hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
        if not hdc_mem:
            return False
        old_obj = gdi32.SelectObject(hdc_mem, hbm)
        # BGRA 每像素四字节:[B,G,R,A];RGB 全 0(黑),A 统一
        pixels = bytes((0, 0, 0, alpha)) * (width * height)
        ctypes.memmove(ppv, pixels, len(pixels))

        pt_src = wt.POINT(0, 0)
        pt_dst = wt.POINT(rect.left, rect.top)
        size = wt.SIZE(width, height)
        blend = BLENDFUNCTION(AC_SRC_OVER, 0, 255, AC_SRC_ALPHA)
        return bool(user32.UpdateLayeredWindow(
            wt.HWND(hwnd), hdc_screen,
            ctypes.byref(pt_dst), ctypes.byref(size),
            hdc_mem, ctypes.byref(pt_src),
            0, ctypes.byref(blend), ULW_ALPHA))
    except Exception:
        return False
    finally:
        # 位图被选进内存 DC 后不能直接删(删不掉),先选回原来的
        if hdc_mem and old_obj:
            gdi32.SelectObject(hdc_mem, old_obj)
        if hbm:
            gdi32.DeleteObject(hbm)
        if hdc_mem:
            gdi32.DeleteDC(hdc_mem)
        user32.ReleaseDC(None, hdc_screen)


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
