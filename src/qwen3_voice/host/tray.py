
from __future__ import annotations

import threading
from dataclasses import dataclass
from typing import Any, Callable

from ..logging_setup import get_logger
from ..notify import NotificationHub, notify

log = get_logger("host.tray")

STATE_COLORS: dict[str, tuple[int, int, int, int]] = {
    "idle": (70, 70, 70, 255),
    "recording": (200, 40, 40, 255),
    "processing": (210, 160, 30, 255),
    "warn": (210, 160, 30, 255),
    "error": (200, 40, 40, 255),
}

FG = (255, 255, 255, 255)

DEFAULT_TITLE = "voice-bar"

@dataclass
class TrayMenuItem:

    label: str
    key: str = ""
    action: Callable[[], None] | None = None
    separator: bool = False
    default: bool = False
    enabled: bool = True

    def __post_init__(self) -> None:
        if not self.key:
            self.key = self.label

def make_icon(state: str = "idle"):
    from PIL import Image, ImageDraw

    size = 64
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    color = STATE_COLORS.get(state, STATE_COLORS["idle"])
    d.ellipse([2, 2, size - 2, size - 2], fill=color)

    d.rounded_rectangle([28, 12, 36, 34], radius=4, fill=FG)
    d.arc([21, 24, 43, 46], start=0, end=180, fill=FG, width=3)
    d.line([32, 42, 32, 52], fill=FG, width=3)
    return img

class Tray:

    def __init__(self, notifier: NotificationHub | None = None,
                 title: str = DEFAULT_TITLE) -> None:
        self._notifier = notifier or notify
        self._title = title
        self._lock = threading.RLock()
        self._icon: Any = None
        self._thread: threading.Thread | None = None
        self._state = "idle"
        self._menu: list[TrayMenuItem] = []
        self._listeners: list[Callable[[str, Any], None]] = []
        self._stop = threading.Event()

    def start(self) -> bool:
        import pystray

        with self._lock:
            if self._icon is not None:
                return True
            try:
                self._icon = pystray.Icon(
                    "voicebar",
                    make_icon(self._state),
                    self._title,
                    self._build_menu())
            except Exception as e:
                log.exception("创建托盘图标失败")
                self._notifier.publish(
                    "error", "tray_failed", "托盘图标创建失败", reason=str(e))
                return False

            self._stop.clear()
            self._thread = threading.Thread(
                target=self._run, name="host-tray", daemon=True)
            self._thread.start()
        return True

    def stop(self) -> None:
        self._stop.set()
        icon = self._icon
        if icon is not None:
            try:
                icon.stop()
            except Exception:
                log.debug("停托盘异常", exc_info=True)
        thread = self._thread
        if thread is not None:
            thread.join(timeout=2.0)
        with self._lock:
            self._icon = None
            self._thread = None

    def _run(self) -> None:
        icon = self._icon
        if icon is None:
            return
        try:
            icon.run()
        except Exception:
            log.exception("托盘线程退出")

    def set_icon_state(self, state: str) -> bool:
        with self._lock:
            self._state = state
            icon = self._icon
        if icon is None:
            return False
        try:
            icon.icon = make_icon(state)
            return True
        except Exception:
            log.exception("切托盘图标状态失败")
            return False

    def set_menu(self, items: list[TrayMenuItem]) -> bool:
        with self._lock:
            self._menu = list(items)
            icon = self._icon
        if icon is None:
            return False
        try:
            icon.menu = self._build_menu()
            return True
        except Exception:
            log.exception("设置托盘菜单失败")
            return False

    def on_event(self, cb: Callable[[str, Any], None]) -> None:
        with self._lock:
            self._listeners.append(cb)

    def _build_menu(self):
        import pystray

        entries = []
        for item in self._menu:
            if item.separator:
                entries.append(pystray.Menu.SEPARATOR)
                continue
            entries.append(pystray.MenuItem(
                item.label,
                lambda it=item: self._on_menu(it),
                default=item.default,
                enabled=item.enabled,
            ))
        return pystray.Menu(*entries)

    def _on_menu(self, item: TrayMenuItem) -> None:
        try:
            if item.action is not None:
                item.action()
        except Exception as e:
            log.exception("托盘菜单动作异常: %s", item.label)
            self._notifier.publish(
                "error", "tray_action_failed",
                f"托盘菜单「{item.label}」执行失败", reason=str(e))
        with self._lock:
            listeners = list(self._listeners)
        for cb in listeners:
            try:
                cb(item.key, item)
            except Exception:
                log.exception("托盘事件回调异常")
