"""全局热键注册与监听。

关键点:
  - RegisterHotKey 是 **线程局部** 的:注册在哪个线程,热键消息就发到那个线程的
    消息队列。所以必须在专门的线程里注册 + 跑 GetMessage 循环。
  - 系统级注册(不 hook 整个键盘),不需要管理员权限,不会和 IDE 快捷键打架。
  - 用 WM_HOTKEY 而不是低级键盘 hook,所以拿不到「按下/松开」语义 ——
    本项目用「按一次开始,再按一次结束」的切换式交互。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import threading
from typing import Callable

from .api import (
    MOD_ALT,
    MOD_CONTROL,
    MOD_NOREPEAT,
    MOD_SHIFT,
    MOD_WIN,
    WM_HOTKEY,
    user32,
)

# 特殊键名 -> 虚拟键码
_NAMED_KEYS = {
    "space": 0x20,
    "enter": 0x0D,
    "return": 0x0D,
    "tab": 0x09,
    "esc": 0x1B,
    "escape": 0x1B,
    "backspace": 0x08,
    "insert": 0x2D,
    "delete": 0x2E,
    "home": 0x24,
    "end": 0x23,
    "pageup": 0x21,
    "pagedown": 0x22,
    "up": 0x26,
    "down": 0x28,
    "left": 0x25,
    "right": 0x27,
}


class HotkeyError(Exception):
    """热键无法注册(通常是被别的程序占用)。"""


def parse_hotkey(spec: str) -> tuple[int, int]:
    """'ctrl+alt+space' -> (modifiers, virtual_key)"""
    modifiers = 0
    vk: int | None = None

    for raw in spec.lower().split("+"):
        part = raw.strip()
        if not part:
            continue
        if part == "ctrl":
            modifiers |= MOD_CONTROL
        elif part == "alt":
            modifiers |= MOD_ALT
        elif part == "shift":
            modifiers |= MOD_SHIFT
        elif part in ("win", "super", "cmd", "meta"):
            modifiers |= MOD_WIN
        elif part in _NAMED_KEYS:
            vk = _NAMED_KEYS[part]
        elif part.startswith("f") and part[1:].isdigit():
            n = int(part[1:])
            if 1 <= n <= 24:
                vk = 0x6F + n
        elif len(part) == 1:
            vk = ord(part.upper())
        else:
            raise ValueError(f"无法识别的热键片段: {raw!r}")

    if vk is None:
        raise ValueError("热键缺少主键")
    return modifiers | MOD_NOREPEAT, vk


class HotkeyListener:
    """在独立线程里注册并监听一个全局热键。

    用法:
        listener = HotkeyListener("ctrl+alt+space", on_trigger)
        listener.start()
        ...
        listener.stop()
    """

    def __init__(self, spec: str, on_trigger: Callable[[], None], hotkey_id: int = 1):
        self.spec = spec
        self.on_trigger = on_trigger
        self.hotkey_id = hotkey_id
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()
        self._registered = False
        self._thread_id: int | None = None

    @property
    def registered(self) -> bool:
        return self._registered

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="hotkey", daemon=True)
        self._thread.start()
        # 等线程里注册完成或失败
        self._stop.wait(0.05)
        for _ in range(100):
            if self._registered or not self._thread.is_alive():
                break
            self._stop.wait(0.05)

    def _run(self) -> None:
        try:
            modifiers, vk = parse_hotkey(self.spec)
        except ValueError as e:
            self._stop.set()
            raise HotkeyError(str(e)) from e

        if not user32.RegisterHotKey(None, self.hotkey_id, modifiers, vk):
            err = ctypes.get_last_error()
            self._stop.set()
            raise HotkeyError(
                f"热键 {self.spec!r} 注册失败(err={err}),可能已被其它程序占用"
            )

        self._registered = True
        self._thread_id = ctypes.windll.kernel32.GetCurrentThreadId()

        msg = wt.MSG()
        try:
            while not self._stop.is_set():
                got = user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
                if got in (0, -1):
                    break
                if msg.message == WM_HOTKEY:
                    try:
                        self.on_trigger()
                    except Exception:
                        pass
        finally:
            user32.UnregisterHotKey(None, self.hotkey_id)
            self._registered = False

    def stop(self) -> None:
        self._stop.set()
        if self._thread_id:
            # 往热键线程投递 WM_QUIT,让它退出 GetMessage 循环
            user32.PostQuitMessage(0)
        if self._thread is not None:
            self._thread.join(timeout=3)
            self._thread = None
