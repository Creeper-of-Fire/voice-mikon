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
    WM_QUIT,
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
        # 注册结果回传:线程里的异常必须带回 start(),否则会被
        # threading 默认吞掉(pythonw 连 stderr 都没有) —— 那样
        # 注册失败无声无息,热键死了都不知道(实测踩过)。
        self._ready = threading.Event()
        self._error: HotkeyError | None = None

    @property
    def registered(self) -> bool:
        return self._registered

    def start(self) -> None:
        """注册并开始监听。

        注册失败(组合被占用等)会**同步抛 HotkeyError** —— 即使
        实际注册发生在线程里。
        """
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="hotkey", daemon=True)
        self._thread.start()
        # 等注册完成/失败(限时,防卡死)
        if not self._ready.wait(timeout=10.0):
            raise HotkeyError("热键线程 10 秒内没有完成注册(异常)")
        if self._error is not None:
            raise self._error

    def _run(self) -> None:
        try:
            modifiers, vk = parse_hotkey(self.spec)
        except ValueError as e:
            self._error = HotkeyError(str(e))
            self._ready.set()
            self._stop.set()
            return

        if not user32.RegisterHotKey(None, self.hotkey_id, modifiers, vk):
            err = ctypes.get_last_error()
            self._error = HotkeyError(
                f"热键 {self.spec!r} 注册失败(err={err}),可能已被其它程序占用"
            )
            self._ready.set()
            self._stop.set()
            return

        self._registered = True
        self._thread_id = ctypes.windll.kernel32.GetCurrentThreadId()
        self._ready.set()

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
        """停热键并**注销**它。

        注销必须真的发生 —— 热键是**全局**资源,只要本进程还占着,
        别人(比如刚重启的另一个实例)就注册不上同一个组合键。
        而注销写在线程自己的 ``finally`` 里,所以第一步是让线程退出
        GetMessage 循环。
        """
        self._stop.set()
        thread = self._thread
        tid = self._thread_id
        if thread is not None and tid:
            # 往**热键线程**的消息队列投递 WM_QUIT。
            #
            # ⚠ 不能用 PostQuitMessage:它把 WM_QUIT 投给**调用线程**
            #   (也就是主线程)自己的队列,热键线程永远收不到 ——
            #   GetMessage 会一直阻塞,finally 里的 UnregisterHotKey
            #   也就永远不执行:热键一直被占着,而且 join 还会白等满
            #   timeout。症状就是"退出要等好几秒,重启时快捷键注册失败"。
            user32.PostThreadMessageW(tid, WM_QUIT, 0, 0)
            thread.join(timeout=3)
            if thread.is_alive():
                # 还没退出 —— 再投一次(万一第一次落在队列刚建好之前),
                # 仍然退不出来就只能放弃:热键会随进程退出被系统回收。
                user32.PostThreadMessageW(tid, WM_QUIT, 0, 0)
                thread.join(timeout=1)
        self._thread = None
        self._thread_id = None
