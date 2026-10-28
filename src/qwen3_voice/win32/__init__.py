"""Win32 相关功能集合。"""

from .api import (
    get_class_name,
    get_foreground_window,
    get_window_title,
    restore_foreground,
    send_ctrl_v,
)
from .clipboard import get_text, set_text
from .hotkey import HotkeyError, HotkeyListener, parse_hotkey
from .inject import paste_text

__all__ = [
    "get_class_name",
    "get_foreground_window",
    "get_window_title",
    "restore_foreground",
    "send_ctrl_v",
    "get_text",
    "set_text",
    "HotkeyError",
    "HotkeyListener",
    "parse_hotkey",
    "paste_text",
]
