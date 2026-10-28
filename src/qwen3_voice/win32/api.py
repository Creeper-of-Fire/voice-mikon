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

KEYEVENTF_KEYUP = 0x0002

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


def send_ctrl_v() -> None:
    """模拟 Ctrl+V。"""
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    user32.keybd_event(VK_V, 0, 0, 0)
    user32.keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0)
    user32.keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0)
