"""剪贴板读写(Win32 API,不走 pyperclip)。"""

from __future__ import annotations

import ctypes
import time

from .api import kernel32, user32

CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002

# 剪贴板是全局资源,别的进程可能正占着它(OpenClipboard 会失败)。
# 读不出来时必须重试 —— 把"读失败"当成"没有文本"是个危险的误判:
# 注入逻辑会据此决定"没有原内容需要归还",用户剪贴板里的东西就丢了。
_OPEN_RETRIES = 4
_OPEN_DELAY = 0.02


def _open_clipboard() -> bool:
    """打开剪贴板,被占用时短暂重试。"""
    for i in range(_OPEN_RETRIES):
        if user32.OpenClipboard(None):
            return True
        if i < _OPEN_RETRIES - 1:
            time.sleep(_OPEN_DELAY)
    return False


def get_text() -> str | None:
    """读取剪贴板中的 Unicode 文本,没有文本时返回 None。"""
    try:
        if not _open_clipboard():
            return None
        try:
            handle = user32.GetClipboardData(CF_UNICODETEXT)
            if not handle:
                return None
            ptr = kernel32.GlobalLock(handle)
            if not ptr:
                return None
            try:
                return ctypes.c_wchar_p(ptr).value
            finally:
                kernel32.GlobalUnlock(handle)
        finally:
            user32.CloseClipboard()
    except Exception:
        return None


def get_sequence_number() -> int:
    """剪贴板变更序号。

    任何一次剪贴板改动(哪怕内容相同)都会让它递增。用它来判断
    "剪贴板现在还是我写的那份吗" —— 这是避免覆盖用户新复制内容的
    关键:恢复原内容之前先比对序号,用户中途动过就放弃恢复。
    """
    try:
        return int(user32.GetClipboardSequenceNumber())
    except Exception:
        return 0


def set_text(text: str) -> bool:
    """把文本写入剪贴板,成功返回 True。"""
    try:
        if not _open_clipboard():
            return False
        try:
            user32.EmptyClipboard()
            data = text + "\0"
            size = len(data) * ctypes.sizeof(ctypes.c_wchar)
            handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, size)
            if not handle:
                return False
            ptr = kernel32.GlobalLock(handle)
            if not ptr:
                kernel32.GlobalFree(handle)
                return False
            try:
                ctypes.memmove(ptr, ctypes.create_unicode_buffer(data), size)
            finally:
                kernel32.GlobalUnlock(handle)
            if not user32.SetClipboardData(CF_UNICODETEXT, handle):
                kernel32.GlobalFree(handle)
                return False
            # 所有权已交给系统,不能再 free
            return True
        finally:
            user32.CloseClipboard()
    except Exception:
        return False
