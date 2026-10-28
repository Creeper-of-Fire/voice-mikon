"""Win32 层测试:热键解析、剪贴板、焦点。

这些是真实调用 Windows API 的集成测试 —— 剪贴板和焦点必须真的能用,
不能 mock 掉(之前踩的句柄截断坑就是 mock 测不出来的)。
"""

import time

import pytest

from qwen3_voice.win32 import api, clipboard
from qwen3_voice.win32.hotkey import parse_hotkey


# ---------------- 热键解析 ----------------

@pytest.mark.parametrize("spec,expect_vk", [
    ("ctrl+alt+space", 0x20),
    ("ctrl+shift+k", ord("K")),
    ("alt+f9", 0x78),
    ("win+space", 0x20),
    ("ctrl+shift+enter", 0x0D),
    ("ctrl+alt+1", ord("1")),
    ("f12", 0x7B),
])
def test_parse_hotkey_vk(spec, expect_vk):
    _, vk = parse_hotkey(spec)
    assert vk == expect_vk


def test_parse_hotkey_modifiers():
    mods, _ = parse_hotkey("ctrl+alt+space")
    assert mods & api.MOD_CONTROL
    assert mods & api.MOD_ALT
    assert mods & api.MOD_NOREPEAT


def test_parse_hotkey_ignores_case_and_spaces():
    a = parse_hotkey("Ctrl+Alt+Space")
    b = parse_hotkey("  ctrl + alt + space ")
    assert a == b


def test_parse_hotkey_rejects_garbage():
    with pytest.raises(ValueError):
        parse_hotkey("ctrl+")
    with pytest.raises(ValueError):
        parse_hotkey("ctrl+完全不存在的键")


# ---------------- 剪贴板 ----------------

def test_clipboard_roundtrip():
    original = clipboard.get_text()
    text = "剪贴板测试 _ABC_ 123 中文标点,。!"

    assert clipboard.set_text(text) is True
    time.sleep(0.15)
    assert clipboard.get_text() == text

    if original is not None:
        clipboard.set_text(original)
        time.sleep(0.15)
        assert clipboard.get_text() == original


def test_clipboard_handles_empty_text():
    original = clipboard.get_text()
    assert clipboard.set_text("") is True
    time.sleep(0.1)
    assert clipboard.get_text() in ("", None)
    if original is not None:
        clipboard.set_text(original)


# ---------------- 焦点 ----------------

def test_foreground_window():
    hwnd = api.get_foreground_window()
    assert hwnd != 0
    assert isinstance(api.get_window_title(hwnd), str)


def test_restore_foreground_to_same_window():
    hwnd = api.get_foreground_window()
    result = api.restore_foreground(hwnd)
    time.sleep(0.2)
    assert result is True
    assert api.get_foreground_window() == hwnd


def test_restore_foreground_invalid_handle():
    assert api.restore_foreground(0) is False
