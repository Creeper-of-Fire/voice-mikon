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
#
# 下面这几个测的是**真实的 Windows 剪贴板 API**(不是假实现),
# 所以它们会短暂占用用户的剪贴板 —— 必须**无条件**把原内容放回去。
# 真实剪贴板里可能还有图片之类读不出文本的内容,那种情况下宁可不动
# 也不能用空字符串把它覆盖掉。

def test_clipboard_roundtrip():
    original = clipboard.get_text()
    text = "剪贴板测试 _ABC_ 123 中文标点,。!"
    try:
        assert clipboard.set_text(text) is True
        time.sleep(0.15)
        assert clipboard.get_text() == text
    finally:
        if original is not None:
            clipboard.set_text(original)
            time.sleep(0.15)


def test_clipboard_handles_empty_text():
    original = clipboard.get_text()
    try:
        assert clipboard.set_text("") is True
        time.sleep(0.1)
        assert clipboard.get_text() in ("", None)
    finally:
        if original is not None:
            clipboard.set_text(original)
            time.sleep(0.1)


# ---------------- 注入守卫 ----------------
# 核心承诺:借了用户的剪贴板,一定要还;但用户中途复制了新内容时,
# **绝不能**用旧内容把它盖掉 —— 那才是真的丢数据。

def test_clipboard_sequence_number_is_readable():
    """序号可读且是非负整数。

    注意**不**断言"set_text 之后序号一定变大" —— Windows 在某些情况下
    (比如内容没变、或被别的程序抢先改过)序号可能不变,那是平台行为,
    不是我们代码的 bug。归属守卫依赖的是"我们写完之后序号没被别人动过",
    不依赖"我们写了就一定变大"。
    """
    seq = clipboard.get_sequence_number()
    assert isinstance(seq, int)
    assert seq >= 0


class FakeClipboard:
    """假剪贴板 —— 测试**不能**碰真实系统剪贴板。

    真实剪贴板是用户的数据:测试期间用户复制的东西会被抢走,
    而且真实剪贴板还可能被别的程序改动,让测试变得不稳定
    (序号对不上 -> 归属守卫生效 -> 恢复被跳过 -> 假失败)。
    """

    def __init__(self):
        self._text = ""
        self._seq = 0

    def get_text(self) -> str:
        return self._text

    def set_text(self, text: str) -> None:
        self._text = text
        self._seq += 1

    def get_sequence_number(self) -> int:
        return self._seq


@pytest.fixture
def fake_clipboard(monkeypatch):
    """把 inject 模块里的剪贴板换成假的。"""
    fake = FakeClipboard()
    monkeypatch.setattr(inject, "clipboard", fake)
    return fake


def test_restore_puts_back_original(monkeypatch, fake_clipboard):
    """没人动过剪贴板 -> 正常归还。"""
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    fake_clipboard.set_text("识别文本")
    published = fake_clipboard.get_sequence_number()
    inject._restore("用户的原始内容", "识别文本", published)
    assert fake_clipboard.get_text() == "用户的原始内容"


def test_restore_skips_when_user_changed_clipboard(monkeypatch, fake_clipboard):
    """用户中途复制了新内容 -> 放弃恢复,用户的新内容优先。"""
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    fake_clipboard.set_text("识别文本")
    published = fake_clipboard.get_sequence_number()
    fake_clipboard.set_text("用户新复制的内容")   # 用户在这期间动了剪贴板
    inject._restore("用户的原始内容", "识别文本", published)
    assert fake_clipboard.get_text() == "用户新复制的内容"


def test_restore_skips_when_content_changed(monkeypatch, fake_clipboard):
    """内容对不上(被别人换掉) -> 也放弃恢复。"""
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    fake_clipboard.set_text("识别文本")
    published = fake_clipboard.get_sequence_number()
    inject._restore("用户的原始内容", "完全不同的东西", published)
    assert fake_clipboard.get_text() == "识别文本"


def test_restore_without_original_leaves_clipboard_alone(monkeypatch, fake_clipboard):
    """原剪贴板没有文本时不动它 —— 宁可把识别结果留在剪贴板里,
    也不要"清空"掉用户剪贴板里的图片之类读不出来的内容。"""
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    fake_clipboard.set_text("识别文本")
    inject._restore(None, "识别文本", fake_clipboard.get_sequence_number())
    assert fake_clipboard.get_text() == "识别文本"


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
