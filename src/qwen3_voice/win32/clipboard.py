"""剪贴板读写(Win32 API,不走 pyperclip)。"""

from __future__ import annotations

import ctypes
import time

from .api import kernel32, user32

CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002

# 剪贴板是全局资源,别的进程可能正占着它(OpenClipboard 会失败)。
# 读不出来时必须重试 —— 而且**读失败要和"里面没有文本"分开**:
# 把"读失败"当成"没有文本"是个危险的误判:注入逻辑会据此决定
# "没有原内容需要归还",用户剪贴板里的东西就丢了。
# 所以下面有 :func:`read_text` 返回 (ok, text) 两段信息。
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


def read_text() -> tuple[bool, str | None]:
    """读剪贴板里的 Unicode 文本,返回 ``(ok, text)``。

    **ok 说的是"读到了没有",不是"有没有文本"** —— 这两件事必须分得清:

    - ``(True, None)``  读成功了,剪贴板里**确实没有文本**
      (空的,或者只有图片之类读不出文本的内容)
    - ``(False, None)`` **读失败**:打不开剪贴板(被别人占着)、锁内存失败、
      或者中途抛了异常

    为什么要分:注入逻辑要拿原内容来"用完还回去"。分不清的话,
    "读失败"会被当成"原剪贴板是空的" → 归还那一步被跳过 →
    用户原来的内容被 ``EmptyClipboard`` 清掉,**永远还不回来**。
    那是真丢数据,比"多留一份识别结果在剪贴板"严重得多。

    ``get_text()`` 是这个函数的兼容包装(只给文本,不管 ok),
    读失败时它和"没有文本"一样返回 None —— 调用方要区分就用本函数。
    """
    try:
        if not _open_clipboard():
            return False, None
        try:
            handle = user32.GetClipboardData(CF_UNICODETEXT)
            if not handle:
                # 读到了,只是里面没有 CF_UNICODETEXT 格式
                return True, None
            ptr = kernel32.GlobalLock(handle)
            if not ptr:
                return False, None
            try:
                return True, ctypes.c_wchar_p(ptr).value
            finally:
                kernel32.GlobalUnlock(handle)
        finally:
            user32.CloseClipboard()
    except Exception:
        return False, None


def get_text() -> str | None:
    """读取剪贴板中的 Unicode 文本,没有文本时返回 None。

    注意:读失败也返回 None —— 要区分"读失败"和"没有文本",
    请用 :func:`read_text`。
    """
    ok, text = read_text()
    return text if ok else None


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
