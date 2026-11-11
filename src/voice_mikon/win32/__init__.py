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

# ⚠ 这里**不再**转出 ``paste_text``:注入已经归位到 ``core/inject.py``。
# win32 是纯 ctypes 绑定层,依赖方向只能是 core → win32 —— 从这里
# 反向 import core 会把依赖箭头掰弯,还可能撞出循环 import
# (core.controller → win32.api → win32/__init__ → core.inject → win32.clipboard)。

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
]
