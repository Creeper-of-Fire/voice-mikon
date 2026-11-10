
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import threading
from typing import Callable

from ..logging_setup import get_logger
from ..notify import NotificationHub, notify
from ..win32.api import (
    MOD_ALT,
    MOD_CONTROL,
    MOD_NOREPEAT,
    MOD_SHIFT,
    MOD_WIN,
    WM_HOTKEY,
    WM_QUIT,
    kernel32,
    user32,
)

log = get_logger("host.hotkey")

WM_HOTKEY_REQUEST = 0x8001

PM_NOREMOVE = 0x0000

NAMED_KEYS = {
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
    pass

def parse_hotkey(spec: str) -> tuple[int, int]:
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
        elif part in NAMED_KEYS:
            vk = NAMED_KEYS[part]
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

class HotkeyManager:

    def __init__(self, notifier: NotificationHub | None = None) -> None:
        self._notifier = notifier or notify
        self._lock = threading.RLock()

        self._callbacks: dict[int, Callable[[], None]] = {}

        self._pending: list[tuple[int, int, int, Callable[[], None]]] = []
        self._next_id = 1
        self._thread: threading.Thread | None = None
        self._thread_id: int | None = None
        self._stop = threading.Event()
        self._ready = threading.Event()
        self._registered_ids: set[int] = set()
        self._unregister_requested = False

    def start(self) -> None:
        with self._lock:
            if self._thread is not None and self._thread.is_alive():
                return
            self._stop.clear()
            self._ready.clear()
            self._thread = threading.Thread(
                target=self._run, name="host-hotkey", daemon=True)
            self._thread.start()
        if not self._ready.wait(timeout=5.0):
            raise HotkeyError("热键线程 5 秒内没有起来")

    def stop(self) -> None:
        self.unregister_all()
        self._stop.set()
        thread = self._thread
        tid = self._thread_id
        if thread is not None and tid:

            user32.PostThreadMessageW(tid, WM_QUIT, 0, 0)
            thread.join(timeout=3)
            if thread.is_alive():

                user32.PostThreadMessageW(tid, WM_QUIT, 0, 0)
                thread.join(timeout=1)
        with self._lock:
            self._thread = None
            self._thread_id = None

    def register(self, combo: str, cb: Callable[[], None]) -> bool:
        try:
            modifiers, vk = parse_hotkey(combo)
        except ValueError as e:
            log.warning("热键 %r 解析失败: %s", combo, e)
            self._notifier.publish(
                "warning", "hotkey_invalid",
                f"快捷键 {combo} 写错了,没注册上", combo=combo, reason=str(e))
            return False

        with self._lock:
            if self._thread is None or not self._thread.is_alive():
                log.warning("热键线程没起来,注册失败")
                self._notifier.publish(
                    "warning", "hotkey_failed",
                    f"快捷键 {combo} 没注册上(热键线程没起来)", combo=combo)
                return False
            hotkey_id = self._next_id
            self._next_id += 1
            self._pending.append((hotkey_id, modifiers, vk, cb))
            tid = self._thread_id

        if tid:

            user32.PostThreadMessageW(tid, WM_HOTKEY_REQUEST, 0, 0)

        ok = self._wait_registered(hotkey_id)
        if not ok:
            self._notifier.publish(
                "warning", "hotkey_failed",
                f"快捷键 {combo} 没注册上,可能已被其它程序占用",
                combo=combo)
            return False
        log.info("热键已注册: %s", combo)
        return True

    def unregister_all(self) -> None:
        with self._lock:
            self._pending.clear()
            self._unregister_requested = True
            tid = self._thread_id
        if tid:
            user32.PostThreadMessageW(tid, WM_HOTKEY_REQUEST, 0, 0)

    def _wait_registered(self, hotkey_id: int, timeout: float = 3.0) -> bool:
        waited = 0.0
        step = 0.02
        while waited < timeout:
            with self._lock:
                if hotkey_id in self._registered_ids:
                    return True
                pending_ids = {item[0] for item in self._pending}
                if hotkey_id not in pending_ids and \
                        hotkey_id not in self._callbacks:

                    return False
            self._stop.wait(step)
            waited += step
        return False

    def _run(self) -> None:
        self._thread_id = ctypes.windll.kernel32.GetCurrentThreadId()

        peek = wt.MSG()
        user32.PeekMessageW(ctypes.byref(peek), None, 0, 0, PM_NOREMOVE)
        self._ready.set()

        msg = wt.MSG()
        try:
            while not self._stop.is_set():
                got = user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
                if got in (0, -1):
                    break
                if msg.message == WM_HOTKEY:
                    self._on_hotkey(int(msg.wParam))
                elif msg.message == WM_HOTKEY_REQUEST:
                    self._process_pending()
        finally:

            self._unregister_all_now()
            self._thread_id = None

    def _process_pending(self) -> None:
        with self._lock:
            pending = list(self._pending)
            do_unregister = self._unregister_requested
            self._unregister_requested = False
        if do_unregister:
            self._unregister_all_now()

        registered: list[tuple[int, Callable[[], None]]] = []
        failed: list[int] = []
        for (hotkey_id, modifiers, vk, cb) in pending:
            if not user32.RegisterHotKey(None, hotkey_id, modifiers, vk):

                err = int(kernel32.GetLastError())
                log.warning("热键注册失败(id=%s err=%s)", hotkey_id, err)
                failed.append(hotkey_id)
                continue
            registered.append((hotkey_id, cb))

        with self._lock:
            done = {hid for hid, _cb in registered} | set(failed)
            if done:
                self._pending = [item for item in self._pending
                                 if item[0] not in done]
            for hotkey_id, cb in registered:
                self._callbacks[hotkey_id] = cb
                self._registered_ids.add(hotkey_id)

    def _unregister_all_now(self) -> None:
        with self._lock:
            ids = list(self._registered_ids)
            self._registered_ids.clear()
            self._callbacks.clear()
        for hotkey_id in ids:
            try:
                user32.UnregisterHotKey(None, hotkey_id)
            except Exception:
                log.debug("注销热键 %s 异常", hotkey_id, exc_info=True)

    def _on_hotkey(self, hotkey_id: int) -> None:
        with self._lock:
            cb = self._callbacks.get(hotkey_id)
        if cb is None:
            return
        try:
            cb()
        except Exception as e:
            log.exception("热键回调异常: %s", e)
            self._notifier.publish(
                "error", "hotkey_callback_failed",
                "快捷键触发的动作失败了", reason=str(e))
