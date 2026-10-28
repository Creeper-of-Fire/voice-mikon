"""剪贴板读写(Win32 API,不走 pyperclip)。"""

from __future__ import annotations

import ctypes

from .api import kernel32, user32

CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002


def get_text() -> str | None:
    """读取剪贴板中的 Unicode 文本,没有文本时返回 None。"""
    try:
        if not user32.OpenClipboard(None):
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


def set_text(text: str) -> bool:
    """把文本写入剪贴板,成功返回 True。"""
    try:
        if not user32.OpenClipboard(None):
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
