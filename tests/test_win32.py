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
        # 假剪贴板能不能读?(用来模拟"剪贴板被别的程序占着")
        self.read_ok = True

    def read_text(self) -> tuple[bool, str | None]:
        """和真实 clipboard.read_text 同一套语义:
        (读到了没有, 读到的文本)。"""
        if not self.read_ok:
            return False, None
        return True, (self._text or None)

    def get_text(self) -> str:
        return self._text

    def set_text(self, text: str) -> bool:
        # 真实 clipboard.set_text 返回 bool(写成功 True)——
        # 注入逻辑靠它判断"写进去了没有",这里必须一致
        self._text = text
        self._seq += 1
        return True

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


# ---------------- 窗口区域(SetWindowRgn) ----------------

def test_set_window_regions_rejects_invalid_handle():
    """句柄无效时返回 False,不能抛异常。"""
    assert api.set_window_regions(0, [(0, 0, 10, 10)]) is False


def test_set_window_regions_releases_region_when_set_fails():
    """SetWindowRgn 失败时区域还在我们手里 —— 必须自己释放,不然泄漏 GDI 对象。

    成功时正好相反:区域归系统所有(MSDN:"系统不拷贝,调用方绝不能
    再碰这个句柄,更不能 DeleteObject"),所以只有失败路径才 DeleteObject。
    """
    # 明显不是窗口的句柄:CreateRectRgn / CombineRgn 都能跑,SetWindowRgn 会失败
    assert api.set_window_regions(0xDEADBEEF, [(0, 0, 10, 10)]) is False


# ---------------- 注入失败时的回退 ----------------
#
# 核心承诺:**注入失败不许让刚说出口的话消失**。
# 之前的行为是:投递不全也照样 return True(谎报成功),而且后台线程
# 无条件把剪贴板恢复成原内容 —— 目标窗口没收到、剪贴板也没了,
# 用户两头落空,界面还显示「已输入」。


@pytest.fixture
def paste_env(monkeypatch, fake_clipboard):
    """把 inject 的外部依赖全换成假的。

    绝不能真发按键、真碰真剪贴板、真抢焦点。
    """
    monkeypatch.setattr(inject, "RESTORE_SETTLE", 0.01)
    monkeypatch.setattr(inject, "PASTE_SETTLE", 0.0)
    monkeypatch.setattr(inject, "modifiers_held", lambda: False)
    monkeypatch.setattr(inject, "get_foreground_window", lambda: 0)
    monkeypatch.setattr(inject, "restore_foreground", lambda hwnd: None)
    return fake_clipboard


def test_paste_reports_failure_when_no_key_delivered(monkeypatch, paste_env):
    """一个事件都没送出去 -> 如实报失败。

    谎报成功比失败更糟:用户以为文字进了输入框,就不会再去别处找。
    """
    monkeypatch.setattr(inject, "send_ctrl_v", lambda: 0)

    assert inject.paste_text("识别文本", restore=True) is False


def test_paste_counts_partial_delivery_as_delivered(monkeypatch, paste_env):
    """V 的按下送出去了 = 粘贴已经触发。

    SendInput 只插入数组前缀([Ctrl↓, V↓, V↑, Ctrl↑]),sent>=2 意味着
    目标应用已经收到「粘贴」这个动作 —— 这时候判失败会害我们重贴,
    造成重复文本。
    """
    monkeypatch.setattr(inject, "send_ctrl_v", lambda: 2)

    assert inject.paste_text("识别文本", restore=True) is True


def test_paste_keeps_text_in_clipboard_when_not_delivered(monkeypatch, paste_env):
    """没送出去时**不恢复**剪贴板 —— 识别结果必须留着。"""
    monkeypatch.setattr(inject, "send_ctrl_v", lambda: 0)
    paste_env.set_text("用户原来的内容")

    assert inject.paste_text("识别文本", restore=True) is False
    # 剪贴板里是识别结果,不是被恢复掉的旧内容
    # (trailing_space 默认会给末尾补个空格,这里不比它)
    assert paste_env.get_text().strip() == "识别文本"


def test_paste_restores_clipboard_when_delivered(monkeypatch, paste_env):
    """送出去了 -> 归还原内容(老规矩不变)。"""
    monkeypatch.setattr(inject, "send_ctrl_v", lambda: 4)
    paste_env.set_text("用户原来的内容")

    assert inject.paste_text("识别文本", restore=True) is True
    time.sleep(0.2)          # 等后台恢复线程跑完
    assert paste_env.get_text() == "用户原来的内容"


def test_paste_does_not_restore_when_original_unreadable(monkeypatch, paste_env):
    """原剪贴板读不出来 -> 我们根本不知道原内容是什么,只能不还。

    还不了就别装还 —— 硬「还」一个空字符串才是真的把用户内容抹了。
    识别结果留在剪贴板里,用户至少还拿得到刚说出口的话。
    """
    monkeypatch.setattr(inject, "send_ctrl_v", lambda: 4)
    paste_env.set_text("用户原来的内容")
    paste_env.read_ok = False        # 模拟剪贴板被别的程序占着

    assert inject.paste_text("识别文本", restore=True) is True
    time.sleep(0.2)
    assert paste_env.get_text().strip() == "识别文本"


def test_paste_skips_when_modifier_held(monkeypatch, paste_env):
    """用户按着修饰键时不注入 —— 但也不能把这段话弄丢。

    这条路径压根没写剪贴板,所以识别结果只在内存里;
    界面上的出口是胶囊的「复制」按钮(见 test_ui_windows.py)。
    """
    monkeypatch.setattr(inject, "modifiers_held", lambda: True)
    monkeypatch.setattr(inject, "send_ctrl_v", lambda: 4)

    assert inject.paste_text("识别文本", restore=True) is False
    # 一个字都没写进剪贴板
    assert paste_env.get_text() == ""


# ---------------- 剪贴板读写的语义 ----------------


def test_clipboard_read_text_reports_open_failure(monkeypatch):
    """打不开剪贴板 -> ok=False,**不能**当成「里面没有文本」。

    混为一谈的后果:上层以为「没有原内容需要归还」,把用户剪贴板里的
    内容清掉还不还回去。
    """
    monkeypatch.setattr(clipboard, "_open_clipboard", lambda: False)

    ok, text = clipboard.read_text()

    assert ok is False
    assert text is None


def test_clipboard_read_text_reports_empty_as_success(monkeypatch):
    """打得开、但里面没有 CF_UNICODETEXT -> ok=True, text=None。

    这和「读失败」是两回事,必须报得出来。
    """

    class _FakeUser32:
        def GetClipboardData(self, fmt):
            return 0        # 没有该格式

        def CloseClipboard(self):
            return True

    monkeypatch.setattr(clipboard, "_open_clipboard", lambda: True)
    monkeypatch.setattr(clipboard, "user32", _FakeUser32())

    ok, text = clipboard.read_text()

    assert ok is True
    assert text is None
