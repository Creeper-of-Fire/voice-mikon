"""Win32 层测试:热键解析、剪贴板、焦点、注入守卫。

这些是真实调用 Windows API 的集成测试 —— 剪贴板和焦点必须真的能用,
不能 mock 掉(之前踩的句柄截断坑就是 mock 测不出来的)。
"""

import time

import pytest

from qwen3_voice.win32 import api, clipboard, inject
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


# ---------------- 注入守卫 ----------------
# 核心承诺:借了用户的剪贴板,一定要还;但用户中途复制了新内容时,
# **绝不能**用旧内容把它盖掉 —— 那才是真的丢数据。

def test_clipboard_sequence_number_advances():
    original = clipboard.get_text()
    try:
        before = clipboard.get_sequence_number()
        clipboard.set_text("序号测试")
        after = clipboard.get_sequence_number()
        assert after != before
    finally:
        if original is not None:
            clipboard.set_text(original)


def test_restore_puts_back_original(monkeypatch):
    """没人动过剪贴板 -> 正常归还。"""
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    original = clipboard.get_text()
    try:
        clipboard.set_text("识别文本")
        published = clipboard.get_sequence_number()
        inject._restore("用户的原始内容", "识别文本", published)
        assert clipboard.get_text() == "用户的原始内容"
    finally:
        if original is not None:
            clipboard.set_text(original)


def test_restore_skips_when_user_changed_clipboard(monkeypatch):
    """用户中途复制了新内容 -> 放弃恢复,用户的新内容优先。"""
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    original = clipboard.get_text()
    try:
        clipboard.set_text("识别文本")
        published = clipboard.get_sequence_number()
        clipboard.set_text("用户新复制的内容")   # 用户在这期间动了剪贴板
        inject._restore("用户的原始内容", "识别文本", published)
        assert clipboard.get_text() == "用户新复制的内容"
    finally:
        if original is not None:
            clipboard.set_text(original)


def test_restore_skips_when_content_changed(monkeypatch):
    """内容对不上(被别人换掉) -> 也放弃恢复。"""
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    original = clipboard.get_text()
    try:
        clipboard.set_text("识别文本")
        published = clipboard.get_sequence_number()
        inject._restore("用户的原始内容", "完全不同的东西", published)
        assert clipboard.get_text() == "识别文本"
    finally:
        if original is not None:
            clipboard.set_text(original)


def test_restore_without_original_leaves_clipboard_alone(monkeypatch):
    """原剪贴板没有文本时不动它 —— 宁可把识别结果留在剪贴板里,
    也不要"清空"掉用户剪贴板里的图片之类读不出来的内容。"""
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    original = clipboard.get_text()
    try:
        clipboard.set_text("识别文本")
        inject._restore(None, "识别文本", clipboard.get_sequence_number())
        assert clipboard.get_text() == "识别文本"
    finally:
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
