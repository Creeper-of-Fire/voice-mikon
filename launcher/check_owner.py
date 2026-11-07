"""模拟第三方剪贴板管理器(Ditto 一类)给剪贴板内容标注「来源应用」的取名链路。

为什么需要这个脚本
------------------
本机没装第三方剪贴板管理器,但「剪贴板来源标注」是这次改造的核心验收项。
实测复现的链路是:

    GetClipboardOwner()            -> HWND
    GetWindowThreadProcessId(hwnd) -> PID
    OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, pid)
    QueryFullProcessImageNameW()   -> 完整路径
    basename(完整路径)              -> 来源标注

关键点:它取的是**真正在跑 Python 的那个进程**的映像文件名,不是版本资源里的
FileDescription。所以只有把「跑 Python 的进程」换成 Qwen3Voice.exe,标注才会变。

用法
----
    python check_owner.py            看当前剪贴板 owner 是谁
    python check_owner.py --watch    每 0.5s 轮询一次,owner 变化就打印
    python check_owner.py --text     顺便打印剪贴板里的文本(前 80 字)

跑法不限解释器 —— base 的 python.exe / venv 的 python.exe 都行,它只依赖 ctypes。
"""

from __future__ import annotations

import argparse
import ctypes
import os
import sys
import time
from ctypes import wintypes

# ---------------------------------------------------------------- Win32 绑定

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

user32.GetClipboardOwner.argtypes = []
user32.GetClipboardOwner.restype = wintypes.HWND

user32.GetWindowThreadProcessId.argtypes = [
    wintypes.HWND,
    ctypes.POINTER(wintypes.DWORD),
]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD

user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.restype = ctypes.c_int

user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL

# 读剪贴板正文用。argtypes 必须写全 —— 不写的话 ctypes 按 c_int 传参/返回,
# 64 位下的 HANDLE 会被截断,GetClipboardData 拿到错句柄直接访问冲突(实测)。
user32.OpenClipboard.argtypes = [wintypes.HWND]
user32.OpenClipboard.restype = wintypes.BOOL

user32.CloseClipboard.argtypes = []
user32.CloseClipboard.restype = wintypes.BOOL

user32.IsClipboardFormatAvailable.argtypes = [wintypes.UINT]
user32.IsClipboardFormatAvailable.restype = wintypes.BOOL

user32.GetClipboardData.argtypes = [wintypes.UINT]
user32.GetClipboardData.restype = wintypes.HANDLE

kernel32.GlobalLock.argtypes = [wintypes.HANDLE]
kernel32.GlobalLock.restype = wintypes.LPVOID

kernel32.GlobalUnlock.argtypes = [wintypes.HANDLE]
kernel32.GlobalUnlock.restype = wintypes.BOOL

kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE

kernel32.QueryFullProcessImageNameW.argtypes = [
    wintypes.HANDLE,
    wintypes.DWORD,
    wintypes.LPWSTR,
    ctypes.POINTER(wintypes.DWORD),
]
kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL

kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
TH32CS_SNAPTHREAD = 0x00000004
INVALID_HANDLE_VALUE = -1


class THREADENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ThreadID", wintypes.DWORD),
        ("th32OwnerProcessID", wintypes.DWORD),
        ("tpBasePri", ctypes.c_long),
        ("tpDeltaPri", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
    ]


# 枚举线程用(message-only 窗口不参与 EnumWindows,只能按线程枚举)
kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
kernel32.Thread32First.argtypes = [wintypes.HANDLE, ctypes.POINTER(THREADENTRY32)]
kernel32.Thread32Next.argtypes = [wintypes.HANDLE, ctypes.POINTER(THREADENTRY32)]

user32.EnumThreadWindows.argtypes = [
    wintypes.DWORD,
    ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM),
    wintypes.LPARAM,
]

# message-only 窗口(parent = HWND_MESSAGE)不参与 EnumWindows,
# 官方给的查法是拿 HWND_MESSAGE 当 parent 去 FindWindowEx。
user32.FindWindowExW.argtypes = [
    wintypes.HWND,
    wintypes.HWND,
    wintypes.LPCWSTR,
    wintypes.LPCWSTR,
]
user32.FindWindowExW.restype = wintypes.HWND


# ------------------------------------------------------------------ 各步骤


def image_path_of(pid: int) -> str:
    """PID -> 进程映像的完整路径(第三方管理器取名的最后一步之前那一步)。"""
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        size = wintypes.DWORD(1024)
        buf = ctypes.create_unicode_buffer(size.value)
        if not kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        return buf.value
    finally:
        kernel32.CloseHandle(h)


def class_name_of(hwnd: int) -> str:
    buf = ctypes.create_unicode_buffer(256)
    n = user32.GetClassNameW(hwnd, buf, len(buf))
    return buf.value if n else ""


def clipboard_text() -> str:
    """读剪贴板里的文本(只在有文本时返回)。"""
    try:
        if not user32.OpenClipboard(None):
            return ""
        try:
            if not user32.IsClipboardFormatAvailable(13):  # CF_UNICODETEXT
                return ""
            handle = user32.GetClipboardData(13)
            if not handle:
                return ""
            # 跨进程读内存必须用 GlobalLock
            ptr = kernel32.GlobalLock(handle)
            if not ptr:
                return ""
            try:
                return ctypes.c_wchar_p(ptr).value or ""
            finally:
                kernel32.GlobalUnlock(handle)
        finally:
            user32.CloseClipboard()
    except OSError:
        return ""


def owner_windows(class_filter: str = "Qwen3VoiceClipboardOwner") -> list[tuple[int, int, int, str, str]]:
    """扫描所有 message-only 窗口,挑出 owner 窗口。

    message-only 窗口(parent = HWND_MESSAGE)不参与 EnumWindows,
    官方给的查法是拿 HWND_MESSAGE 当 parent 去 FindWindowEx
    (实测 EnumThreadWindows 也枚举不到它们)。

    返回 (hwnd, tid, pid, class, 映像路径)。
    """
    found: list[tuple[int, int, int, str, str]] = []
    parent = ctypes.c_void_p(-3)  # HWND_MESSAGE
    child = None
    while True:
        hwnd = user32.FindWindowExW(parent, child, None, None)
        if not hwnd:
            break
        child = hwnd
        cls_buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls_buf, 256)
        if cls_buf.value != class_filter:
            continue
        pid = wintypes.DWORD()
        tid = user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        try:
            path = image_path_of(pid.value)
        except OSError:
            path = "(取不到路径)"
        found.append((hwnd, tid, pid.value, cls_buf.value, path))
    return found


def describe() -> tuple[str, str]:
    """返回 (来源标注, 详情行)。"""
    hwnd = user32.GetClipboardOwner()
    if not hwnd:
        return "(无 owner)", "剪贴板当前没有 owner 窗口"

    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value == 0:
        return "(取不到 PID)", f"owner HWND=0x{hwnd:X}"

    try:
        path = image_path_of(pid.value)
    except OSError as e:
        return "(取不到路径)", f"owner HWND=0x{hwnd:X} PID={pid.value} {e}"

    label = os.path.basename(path)
    detail = (
        f"owner HWND=0x{hwnd:X}  PID={pid.value}  class={class_name_of(hwnd)!r}  "
        f"visible={bool(user32.IsWindowVisible(hwnd))}\n  映像路径: {path}"
    )
    return label, detail


def main() -> int:
    ap = argparse.ArgumentParser(description="模拟第三方剪贴板管理器的来源取名")
    ap.add_argument("--watch", action="store_true", help="持续轮询")
    ap.add_argument("--text", action="store_true", help="顺便打印剪贴板文本")
    ap.add_argument(
        "--owner-windows",
        action="store_true",
        help="扫描 message-only 的剪贴板 owner 窗口,报告它归属哪个进程映像",
    )
    args = ap.parse_args()

    def once() -> None:
        label, detail = describe()
        print(f"剪贴板来源标注 = {label}", flush=True)
        print(f"  {detail}", flush=True)
        if args.text:
            t = clipboard_text()
            print(f"  剪贴板文本: {t[:80]!r}", flush=True)
        if args.owner_windows:
            wins = owner_windows()
            if not wins:
                print("  (没找到 Qwen3VoiceClipboardOwner 窗口)", flush=True)
            for hwnd, tid, pid, cls, path in wins:
                print(
                    f"  owner 窗口: hwnd=0x{hwnd:08X} tid={tid} PID={pid} "
                    f"class={cls!r}\n    映像路径: {path}",
                    flush=True,
                )

    if not args.watch:
        once()
        return 0

    last = None
    try:
        while True:
            label, _ = describe()
            if label != last:
                once()
                last = label
            time.sleep(0.5)
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
