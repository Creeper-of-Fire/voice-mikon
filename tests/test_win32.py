"""Win32 层测试:热键解析、剪贴板、焦点、注入守卫、窗口区域、显示器。

**先读这段,再往这个文件里加用例** —— 这一层的口径分四档,
混了就会踩到"测试把用户数据搞坏了"这类事故:

1. **应该真调 Win32 API**:剪贴板读写、窗口区域(SetWindowRgn)的
   区域生命周期、签名声明。
   句柄截断那类坑**只有真调才测得出来** —— mock 掉就等于没测
   (本文件里那次句柄截断事故,就是 mock 永远发现不了的)。

2. **必须用假实现**:注入路径(发按键、改剪贴板、抢焦点)。
   真发 Ctrl+V 会把文字贴进用户正在编辑的地方,
   真抢焦点会把用户的光标拽走 —— 测试里这两件事都不许发生。

3. **真调但必须原样还原**:剪贴板用例要借用户的剪贴板用一下,
   所以先**快照**(所有内存型格式的字节)、测完原样写回去。
   期间用户复制了新内容就放弃还 —— 宁可让用户的新内容留着,
   也不覆盖它(和 ``inject._restore`` 同一个道理)。

4. **真调但默认不跑**:抢前台焦点的用例必须显式开启
   (``QWEN3_VOICE_TEST_FOCUS=1``),普通跑 pytest 不许碰用户焦点。

一句话:**真调 ≠ 可以破坏用户状态**。真调是为了验 API 行为,
用户状态该还的要还、该避开的要避开。
"""

import ast
import ctypes
import os
import time
from ctypes import wintypes as wt
from pathlib import Path

import pytest

from qwen3_voice.win32 import api, clipboard, dpi, inject, monitors
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


# ---------------- 剪贴板:快照与还原 ----------------
#
# 下面这几个测的是**真实的 Windows 剪贴板 API**(不是假实现):
# GlobalAlloc / GlobalLock / memmove / SetClipboardData / GetClipboardData
# 这一整条链路上,句柄一旦被截断就会写到非法地址、进程直接崩
# (README「ctypes 句柄截断」)。mock 掉这条链路,那个坑就没人守了。
#
# 但真实剪贴板是**用户的数据**:借了必须**原样**还回去 ——
# 而且是**所有格式**都还,不只是文本(从 Word/浏览器复制过来的内容
# 通常还带 HTML Format 等别的格式,只还文本 = 把用户的格式弄丢了)。
# 剪贴板里要是有什么还原不了的格式(位图之类),就老实地跳过测试,
# 绝不拿用户的数据冒险。

_u = ctypes.WinDLL("user32", use_last_error=True)
_k = ctypes.WinDLL("kernel32", use_last_error=True)

# 规矩和 win32/api.py 一样:**每个函数都显式声明 argtypes/restype**。
# 不声明 restype 时 ctypes 默认按 32 位整数处理返回值,64 位句柄会被截断。
# 写这段辅助的时候本人就栽了一次:SetClipboardData 漏了 argtypes,
# 句柄被截断 → OverflowError,顺带把用户的剪贴板清空了。
_u.OpenClipboard.restype = wt.BOOL
_u.OpenClipboard.argtypes = [wt.HWND]
_u.CloseClipboard.restype = wt.BOOL
_u.CloseClipboard.argtypes = []
_u.EmptyClipboard.restype = wt.BOOL
_u.EmptyClipboard.argtypes = []
_u.GetClipboardData.restype = wt.HANDLE
_u.GetClipboardData.argtypes = [wt.UINT]
_u.SetClipboardData.restype = wt.HANDLE
_u.SetClipboardData.argtypes = [wt.UINT, wt.HANDLE]
_u.EnumClipboardFormats.restype = wt.UINT
_u.EnumClipboardFormats.argtypes = [wt.UINT]
_u.CountClipboardFormats.restype = wt.UINT
_u.CountClipboardFormats.argtypes = []
_u.GetClipboardSequenceNumber.restype = wt.DWORD
_u.GetClipboardSequenceNumber.argtypes = []
_u.GetGuiResources.restype = wt.DWORD
_u.GetGuiResources.argtypes = [wt.HANDLE, wt.DWORD]

_k.GetCurrentProcess.restype = wt.HANDLE
_k.GetCurrentProcess.argtypes = []
_k.GlobalAlloc.restype = wt.HGLOBAL
_k.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
_k.GlobalLock.restype = ctypes.c_void_p
_k.GlobalLock.argtypes = [wt.HGLOBAL]
_k.GlobalUnlock.restype = wt.BOOL
_k.GlobalUnlock.argtypes = [wt.HGLOBAL]
_k.GlobalSize.restype = ctypes.c_size_t
_k.GlobalSize.argtypes = [wt.HGLOBAL]
_k.GlobalFree.restype = wt.HGLOBAL
_k.GlobalFree.argtypes = [wt.HGLOBAL]

CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002
GR_GDIOBJECTS = 0

# CF_TEXT / CF_OEMTEXT / CF_LOCALE 是 Windows 从 CF_UNICODETEXT
# **自动合成**出来的(而且是惰性的:没人取过就不 materialize)。
# 所以:
#   - 快照时它们在不在,取决于之前有没有人读过 —— 不是内容的一部分;
#   - 还原时**不要**手动设它们,手动设反而会打乱系统的合成逻辑
#     (实测:手动设过 CF_TEXT 之后,合成的 CF_OEMTEXT 就没了)。
# 交给系统自己合成,内容一样,还不会多此一举。
DERIVED_FORMATS = {1, 7, 16}       # CF_TEXT, CF_OEMTEXT, CF_LOCALE

# 剪贴板是**全局**资源:别的进程(输入法、聊天软件、录屏工具……)可能
# 正好开着它,这时候 OpenClipboard 会返回 ACCESS_DENIED。
# 所以打开要重试 —— 和 win32/clipboard.py 里 _open_clipboard 一个路子。
_OPEN_RETRIES = 10
_OPEN_DELAY = 0.05


def _open_clipboard() -> bool:
    """打开剪贴板,被别人占着就短暂重试。"""
    for i in range(_OPEN_RETRIES):
        if _u.OpenClipboard(None):
            return True
        if i < _OPEN_RETRIES - 1:
            time.sleep(_OPEN_DELAY)
    return False


def _write_clipboard(text: str, attempts: int = 10) -> bool:
    """往剪贴板写文本,被别人占着就重试。

    ``clipboard.set_text`` 自己只重试 4 次(共 80ms),而别的程序
    (输入法、聊天软件、录屏工具……)会时不时开着剪贴板不放。
    这里多给几次机会,免得把**环境噪声**当成用例失败 ——
    但真写不进去还是要如实失败。
    """
    for _ in range(attempts):
        if clipboard.set_text(text):
            return True
        time.sleep(_OPEN_DELAY)
    return False


def _read_clipboard(attempts: int = 10) -> str | None:
    """读剪贴板里的文本,被别的程序占着就重试。

    返回 None 表示:读了几次都读不到,或者剪贴板里本来就没有文本
    (这两种情况在应用里是分开的,见 ``clipboard.read_text``)。
    """
    for _ in range(attempts):
        text = clipboard.get_text()
        if text is not None:
            return text
        time.sleep(_OPEN_DELAY)
    return None


def _capture_clipboard() -> list[tuple[int, bytes]] | None:
    """剪贴板快照:``[(格式号, 字节), ...]``,按格式优先级顺序。

    返回 ``None`` 表示**还原不了** —— 剪贴板里有不是全局内存的格式
    (位图、调色板之类,数据不在 GlobalAlloc 出来的内存里),
    或者剪贴板被别人占着不放。
    这两种情况下宁可跳过测试,也不去碰用户的数据。
    """
    items: list[tuple[int, bytes]] = []
    if not _open_clipboard():
        return None
    try:
        fmt = _u.EnumClipboardFormats(0)
        while fmt:
            handle = _u.GetClipboardData(fmt)
            if not handle:
                if fmt in DERIVED_FORMATS:
                    # 派生的格式取不到就算了(系统有时不合成),
                    # 它本来就不是内容的一部分
                    fmt = _u.EnumClipboardFormats(fmt)
                    continue
                return None
            ptr = _k.GlobalLock(handle)
            if not ptr:
                if fmt in DERIVED_FORMATS:
                    fmt = _u.EnumClipboardFormats(fmt)
                    continue
                return None          # 不是内存对象:还原不了
            try:
                size = _k.GlobalSize(handle)
                items.append((fmt, ctypes.string_at(ptr, size)))
            finally:
                _k.GlobalUnlock(handle)
            fmt = _u.EnumClipboardFormats(fmt)
    finally:
        _u.CloseClipboard()
    return items


def _text_of(items: list[tuple[int, bytes]]) -> str | None:
    """快照里 CF_UNICODETEXT 的文本(没有这个格式就是 None)。"""
    for fmt, data in items:
        if fmt == CF_UNICODETEXT:
            return data.decode("utf-16-le").rstrip("\0")
    return None


def _read_unicode_bytes() -> tuple[bool, bytes | None]:
    """直接读 CF_UNICODETEXT 的原始字节(不枚举格式列表)。

    返回 ``(读到了没有, 字节)``。
    为什么不走枚举:``EnumClipboardFormats`` 在别的程序正动剪贴板时
    会给出不稳定的结果(实测会枚举出空列表),而
    ``GetClipboardData(CF_UNICODETEXT)`` 是直取,稳得多。
    """
    if not _open_clipboard():
        return False, None
    try:
        handle = _u.GetClipboardData(CF_UNICODETEXT)
        if not handle:
            return True, b""          # 读到了,只是没有这个格式
        ptr = _k.GlobalLock(handle)
        if not ptr:
            return False, None
        try:
            size = _k.GlobalSize(handle)
            return True, ctypes.string_at(ptr, size)
        finally:
            _k.GlobalUnlock(handle)
    finally:
        _u.CloseClipboard()


def _text_bytes_of(items: list[tuple[int, bytes]]) -> bytes | None:
    """快照里 CF_UNICODETEXT 的原始字节。"""
    for fmt, data in items:
        if fmt == CF_UNICODETEXT:
            return data
    return None


def _matches_snapshot_text(snapshot: list[tuple[int, bytes]],
                           data: bytes | None) -> bool:
    """还原后读到的字节,和快照里的文本格式是不是一回事。

    这里有两种"没有文本",**不是同一回事**,别混:

    - 快照里有 CF_UNICODETEXT(哪怕内容是空的)→ 还原后必须逐字节相同;
    - 快照里**根本没有** CF_UNICODETEXT(用户剪贴的是图片之类)
      → 还原后也不该有文本格式,这时 ``GetClipboardData`` 拿不到句柄,
        ``_read_unicode_bytes`` 返回的是 ``b""``(读到了,只是没这个格式)。

    早先的断言直接写 ``data == _text_bytes_of(snapshot)``:快照里没有文本
    格式时右边是 ``None``、左边是 ``b""``,两者不等 —— 于是**用户剪贴板
    里本来就没有文本**(很常见:复制了张图片)时,这条用例会莫名其妙地失败,
    而且每次跑都失败(因为剪贴板一直是空的)。判据写错了,不是还原错了。
    """
    expected = _text_bytes_of(snapshot)
    if expected is None:
        # 原来就没有文本格式 → 还原后也不该有
        return data == b""
    return data == expected


def _primary_formats(items: list[tuple[int, bytes]]) -> list[tuple[int, bytes]]:
    """把快照里"系统自动合成的"那几个格式剔掉。

    剪贴板里有 CF_UNICODETEXT 时,CF_TEXT / CF_OEMTEXT / CF_LOCALE
    都是从它派生的 —— 比内容时只比"真实"格式,不然会因为
    "之前有没有人读过"这种历史差异误判。
    """
    formats = {fmt for fmt, _ in items}
    if CF_UNICODETEXT not in formats:
        return list(items)
    return [(fmt, data) for fmt, data in items if fmt not in DERIVED_FORMATS]


def _restore_clipboard(items: list[tuple[int, bytes]],
                       attempts: int = 5) -> bool:
    """把快照原样写回剪贴板(派生的格式交给系统自己合成)。

    剪贴板被别的程序占着时会失败 —— 那就整体重试:
    还不回去 = 用户剪贴板里残留着测试内容,这是不能接受的。
    """
    for _ in range(attempts):
        if not _open_clipboard():
            time.sleep(_OPEN_DELAY)
            continue
        ok = True
        try:
            _u.EmptyClipboard()
            for fmt, data in _primary_formats(items):
                size = len(data)
                handle = _k.GlobalAlloc(GMEM_MOVEABLE, size)
                if not handle:
                    ok = False
                    break
                ptr = _k.GlobalLock(handle)
                if not ptr:
                    _k.GlobalFree(handle)
                    ok = False
                    break
                try:
                    ctypes.memmove(ptr, data, size)
                finally:
                    _k.GlobalUnlock(handle)
                if not _u.SetClipboardData(fmt, handle):
                    _k.GlobalFree(handle)
                    ok = False
                    break
        finally:
            _u.CloseClipboard()
        if ok:
            return True
        time.sleep(_OPEN_DELAY)
    return False


class BorrowedClipboard:
    """一次"借剪贴板"的全过程:快照 → 用 → 原样还回去。

    ``mark_ours()`` 在用例写完剪贴板之后调一下,记下"现在剪贴板里
    是我们写的那份"。还的时候如果变更序号对不上,说明**用户中途复制了
    新内容** —— 那就放弃还:宁可让用户的新内容留着,也不覆盖它。

    ``restore()`` 返回三种状态:
      ``"restored"``  原样还回去了
      ``"skipped"``   用户中途改了剪贴板,故意不还
      ``"failed"``    还不回去 —— 用户剪贴板可能残留了测试内容,要报错
    """

    def __init__(self, snapshot: list[tuple[int, bytes]]):
        self._snapshot = snapshot
        self._ours_seq: int | None = None

    def mark_ours(self) -> None:
        self._ours_seq = int(_u.GetClipboardSequenceNumber())

    def restore(self) -> str:
        if self._ours_seq is not None:
            if int(_u.GetClipboardSequenceNumber()) != self._ours_seq:
                return "skipped"     # 用户中途动了剪贴板:不覆盖
        return "restored" if _restore_clipboard(self._snapshot) else "failed"


@pytest.fixture
def borrowed_clipboard():
    """借一下用户的剪贴板:先快照,用例结束后原样还回去。"""
    snapshot = _capture_clipboard()
    if snapshot is None:
        pytest.skip("剪贴板里有还原不了的格式(位图之类)或正被别的程序占着,"
                    "不碰用户的剪贴板")
    borrow = BorrowedClipboard(snapshot)
    try:
        yield borrow
    finally:
        status = borrow.restore()
        if status == "failed":
            # 兜底:至少把**文本**还回去,别让用户的剪贴板里
            # 残留着测试内容
            original_text = _text_of(snapshot)
            if original_text is not None:
                _write_clipboard(original_text)
            pytest.fail("剪贴板还原失败 —— 用户剪贴板可能残留了测试内容")
        if status == "restored":
            # 还原完了再核对一遍:文本内容必须和快照一模一样
            # (读不到就算了 —— 说明有别的程序正在动剪贴板,验不了)
            ok, data = _read_unicode_bytes()
            if ok:
                assert _matches_snapshot_text(snapshot, data), \
                    "剪贴板还原后内容和原来对不上"


# ---------------- 剪贴板 API(真调) ----------------

def test_clipboard_roundtrip(borrowed_clipboard):
    """写进去的文本要能原样读回来 —— 走的正是会截断句柄的那条链路。

    验的是:GlobalAlloc → GlobalLock → memmove → SetClipboardData →
    GetClipboardData → GlobalLock 读回来,内容一字不差。
    """
    text = "剪贴板测试 _ABC_ 123 中文标点,。!"
    try:
        assert _write_clipboard(text) is True
        borrowed_clipboard.mark_ours()
        assert _read_clipboard() == text
    finally:
        # 借了必须还 —— 不还就是在改用户的剪贴板
        borrowed_clipboard.restore()


def test_clipboard_handles_empty_text(borrowed_clipboard):
    """空字符串也要能写进去(别把"空"当成"失败")。"""
    try:
        assert _write_clipboard("") is True
        borrowed_clipboard.mark_ours()
        assert _read_clipboard() in ("", None)
    finally:
        borrowed_clipboard.restore()


def test_clipboard_snapshot_restore_is_byte_exact():
    """快照 → 改写 → 还原:文本必须**逐字节**回到原样。

    这条守的是"借了能还"本身:只还一半、把用户剪贴板里的内容弄丢,
    也算改了用户的剪贴板。
    """
    snapshot = _capture_clipboard()
    if snapshot is None:
        pytest.skip("剪贴板里有还原不了的格式(位图之类)或正被别的程序占着,"
                    "不碰用户的剪贴板")
    try:
        assert _write_clipboard("临时内容 __还原测试__") is True
        assert _restore_clipboard(snapshot) is True
        ok, data = _read_unicode_bytes()
        if not ok:
            pytest.skip("有别的程序正在动剪贴板,这一轮验不了")
        assert _matches_snapshot_text(snapshot, data)
    finally:
        _restore_clipboard(snapshot)


def test_clipboard_restore_gives_up_when_user_changed_it():
    """用户中途复制了新内容 → 放弃还,绝不覆盖他的新内容。

    和 ``inject._restore`` 是同一条策略:剪贴板只有一份,
    宁可让用户的新内容留着,也不能把它盖掉。
    """
    snapshot = _capture_clipboard()
    if snapshot is None:
        pytest.skip("剪贴板里有还原不了的格式(位图之类)或正被别的程序占着,"
                    "不碰用户的剪贴板")
    borrow = BorrowedClipboard(snapshot)
    try:
        assert _write_clipboard("我们写的内容") is True
        borrow.mark_ours()
        # 模拟"用户在这期间复制了新东西"
        assert _write_clipboard("用户新复制的内容") is True
        assert borrow.restore() == "skipped"
        assert _read_clipboard() == "用户新复制的内容"
    finally:
        # 测试自己造出来的内容,最后还是得收干净
        _restore_clipboard(snapshot)


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
    """假剪贴板 —— **注入路径**上的测试用它,不去碰真实系统剪贴板。

    真实剪贴板是用户的数据:测试期间用户复制的东西会被抢走,
    而且真实剪贴板还可能被别的程序改动,让测试变得不稳定
    (序号对不上 -> 归属守卫生效 -> 恢复被跳过 -> 假失败)。

    注意分工:**剪贴板 API 本身**的用例(上面那几条)是**真调**的,
    靠快照/还原保证不污染用户数据 —— 句柄截断那类坑必须真调才测得出。
    这里的假实现只用来隔离**注入逻辑**(发按键、抢焦点那一层)。
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
#
# 抢用户的前台焦点是不能接受的副作用:光标会被拽走,正在打的字就没了。
# 所以默认跑的用例只看**调用序列**(用假的 user32),真的抢焦点那条
# 必须显式开启。

def test_foreground_window():
    hwnd = api.get_foreground_window()
    assert hwnd != 0
    assert isinstance(api.get_window_title(hwnd), str)


class _FakeFocusUser32:
    """假的 user32:只记录焦点相关的调用。"""

    def __init__(self, foreground=0x1234, target_thread=4242):
        self.calls: list[tuple] = []
        self._foreground = foreground
        self._target_thread = target_thread

    def GetForegroundWindow(self):
        self.calls.append(("GetForegroundWindow",))
        return self._foreground

    def GetWindowThreadProcessId(self, hwnd, pid):
        self.calls.append(("GetWindowThreadProcessId", hwnd))
        return self._target_thread

    def AttachThreadInput(self, t1, t2, attach):
        self.calls.append(("AttachThreadInput", t1, t2, attach))
        return 1

    def SetForegroundWindow(self, hwnd):
        self.calls.append(("SetForegroundWindow", hwnd))
        return 1


class _FakeFocusKernel32:
    def __init__(self, thread_id=77):
        self._thread_id = thread_id

    def GetCurrentThreadId(self):
        return self._thread_id


def test_restore_foreground_attaches_thread_input(monkeypatch):
    """还原焦点必须"挂线程 → 设前台 → 摘线程"三步齐全。

    不挂线程的话,后台线程调 SetForegroundWindow 经常被 Windows 的
    前台锁挡掉(README「还原焦点不许模拟按键」那条坑的另一半)。
    这里用假的 user32 验**调用序列** —— 真的抢焦点见下面那条
    (默认不跑)。
    """
    user32 = _FakeFocusUser32()
    monkeypatch.setattr(api, "user32", user32)
    monkeypatch.setattr(api, "kernel32", _FakeFocusKernel32())

    assert api.restore_foreground(0xABCD) is True

    kinds = [c[0] for c in user32.calls]
    assert kinds == [
        "GetForegroundWindow",
        "GetWindowThreadProcessId",
        "AttachThreadInput",
        "SetForegroundWindow",
        "AttachThreadInput",
    ]
    attach_on = user32.calls[2]
    attach_off = user32.calls[4]
    # 先挂后摘,摘的时候必须是"不挂" —— 忘了摘会锁死用户的输入
    assert attach_on[3] is True
    assert attach_off[3] is False
    # 挂的是"前台窗口所在线程"和"当前线程"这一对
    assert attach_on[1] == 4242
    assert attach_on[2] == 77
    assert user32.calls[3][1] == 0xABCD


def test_restore_foreground_invalid_handle():
    assert api.restore_foreground(0) is False


@pytest.mark.focus
@pytest.mark.skipif(
    not os.environ.get("QWEN3_VOICE_TEST_FOCUS"),
    reason="抢前台焦点会打断用户;设 QWEN3_VOICE_TEST_FOCUS=1 才跑",
)
def test_restore_foreground_to_same_window_real():
    """真把焦点还给当前前台窗口(需要显式开启)。

    默认不跑:这条会调 SetForegroundWindow,在用户正打着字的时候
    可能把窗口顶到前面、切走焦点 —— 那是不能默认发生的事。
    """
    hwnd = api.get_foreground_window()
    result = api.restore_foreground(hwnd)
    time.sleep(0.2)
    assert result is True
    assert api.get_foreground_window() == hwnd


# ---------------- 窗口区域(SetWindowRgn) ----------------

class _FakeGdi32:
    """假 GDI:记录建了哪些区域、删了哪些。"""

    def __init__(self):
        self.created: list[int] = []
        self.deleted: list[int] = []

    def CreateRectRgn(self, left, top, right, bottom):
        handle = 0x1000 + len(self.created)
        self.created.append(handle)
        return handle

    def CombineRgn(self, dst, src1, src2, op):
        return api.SIMPLEREGION

    def DeleteObject(self, handle):
        self.deleted.append(handle)
        return 1


class _FakeRegionUser32:
    """假的 user32:只关心 SetWindowRgn 成功还是失败。"""

    def __init__(self, ok: bool):
        self.ok = ok
        self.set_calls: list[tuple] = []

    def SetWindowRgn(self, hwnd, region, redraw):
        self.set_calls.append((hwnd, region))
        return 1 if self.ok else 0


def test_set_window_regions_rejects_invalid_handle():
    """句柄无效时返回 False,不能抛异常。"""
    assert api.set_window_regions(0, [(0, 0, 10, 10)]) is False


def test_set_window_regions_releases_region_when_set_fails(monkeypatch):
    """SetWindowRgn 失败时区域还在我们手里 —— 必须自己释放,不然泄漏 GDI 对象。

    成功时正好相反:区域归系统所有(MSDN:"系统不拷贝,调用方绝不能
    再碰这个句柄,更不能 DeleteObject"),所以只有失败路径才 DeleteObject。
    """
    gdi = _FakeGdi32()
    user32 = _FakeRegionUser32(ok=False)
    monkeypatch.setattr(api, "gdi32", gdi)
    monkeypatch.setattr(api, "user32", user32)

    assert api.set_window_regions(1234, [(0, 0, 10, 10), (0, 20, 10, 30)]) is False

    # 两块矩形:建了 2 个区域(第二块合并完就被删掉),
    # 失败时最后那块也必须被删 —— 建了几个就得删几个
    assert len(gdi.created) == 2
    assert sorted(gdi.deleted) == sorted(gdi.created)
    # 交给 SetWindowRgn 的那个区域句柄确实被释放了
    assert user32.set_calls[0][1] in gdi.deleted


def test_set_window_regions_does_not_delete_region_on_success(monkeypatch):
    """成功时**不许** DeleteObject —— 区域已经归系统了,再碰就是野指针。"""
    gdi = _FakeGdi32()
    user32 = _FakeRegionUser32(ok=True)
    monkeypatch.setattr(api, "gdi32", gdi)
    monkeypatch.setattr(api, "user32", user32)

    assert api.set_window_regions(1234, [(0, 0, 10, 10)]) is True

    assert gdi.created
    assert gdi.deleted == []


def test_set_window_regions_does_not_leak_gdi_objects():
    """真调 Win32:失败路径连跑一百次,GDI 对象数不许涨。

    这是"失败时释放区域"的**真实**证据 —— 少一次 DeleteObject,
    就会实打实多泄漏一个区域对象(几百次之后 GDI 句柄就见底了)。
    """
    hproc = _k.GetCurrentProcess()
    _u.GetGuiResources(hproc, GR_GDIOBJECTS)      # 预热,别把第一次的初始化算进来
    before = _u.GetGuiResources(hproc, GR_GDIOBJECTS)

    for _ in range(100):
        assert api.set_window_regions(0xDEADBEEF, [(0, 0, 10, 10)]) is False

    after = _u.GetGuiResources(hproc, GR_GDIOBJECTS)
    # 允许一点点抖动(别的线程可能也在用 GDI),但绝不能按调用次数涨
    assert after - before <= 4, f"GDI 对象泄漏:{before} -> {after}"


# ---------------- ctypes 签名声明 ----------------
#
# README 第一条坑:ctypes 默认把返回值当 32 位整数,
# 64 位句柄(HWND/HGLOBAL)会被截断 → 写到非法地址 → 进程崩 0xC0000005。
#
# 难点:ctypes 的"没声明"和"声明成 c_long"在运行时**分不出来**
# (默认 restype 就是 c_long)。所以这里分两头查:
#   1. 静态:解析 api.py 的源码,确认每个返回句柄的函数都写了 .restype
#   2. 运行时:确认这些 restype 是**指针宽度**(64 位),不是 32 位整数

# 返回句柄的函数。没法从源码自动推断"这个函数返回不返回句柄",
# 所以这里列出来 —— 新增返回句柄的绑定时,记得往这张表里加一条。
HANDLE_RETURNING = {
    "GlobalAlloc": "HGLOBAL",
    "GlobalLock": "HGLOBAL(指针)",
    "GlobalFree": "HGLOBAL",
    "GetForegroundWindow": "HWND",
    "FindWindowW": "HWND",
    "MonitorFromPoint": "HMONITOR",
    "MonitorFromWindow": "HMONITOR",
    "CreateRectRgn": "HRGN",
    "GetClipboardData": "HANDLE",
    "SetClipboardData": "HANDLE",
    "GetClipboardOwner": "HWND",
}


def _bound_functions() -> dict[str, object]:
    """api.py 里绑定过的所有 ctypes 函数(按名字索引)。"""
    found: dict[str, object] = {}
    for dll in (api.user32, api.kernel32, api.gdi32):
        for name, value in vars(dll).items():
            if isinstance(value, ctypes._CFuncPtr):
                found.setdefault(name, value)
    return found


def test_handle_returning_functions_declare_restype_in_source():
    """源码层面:每个返回句柄的函数都必须在 api.py 里显式写 .restype。

    漏写 restype 的后果不是"返回值不准",而是 64 位句柄被截成 32 位,
    之后往那个地址写 → 进程直接崩(0xC0000005)。
    """
    source = Path(api.__file__).read_text(encoding="utf-8")
    tree = ast.parse(source)

    declared: set[str] = set()
    for node in ast.walk(tree):
        if not isinstance(node, ast.Assign):
            continue
        for target in node.targets:
            # 形如 `user32.GlobalAlloc.restype = ...`
            if (isinstance(target, ast.Attribute)
                    and target.attr == "restype"
                    and isinstance(target.value, ast.Attribute)):
                declared.add(target.value.attr)

    missing = sorted(name for name in HANDLE_RETURNING if name not in declared)
    assert not missing, f"这些返回句柄的函数没声明 restype:{missing}"


def test_handle_restypes_are_pointer_sized():
    """运行时:返回句柄的函数,restype 必须是指针宽度(64 位)。"""
    pointer_size = ctypes.sizeof(ctypes.c_void_p)
    functions = _bound_functions()

    for name in sorted(HANDLE_RETURNING):
        fn = functions.get(name)
        assert fn is not None, f"{name} 没有绑定"
        assert fn.restype is not None, f"{name} 没有声明 restype"
        assert ctypes.sizeof(fn.restype) == pointer_size, (
            f"{name} 的 restype 是 {fn.restype},宽度 {ctypes.sizeof(fn.restype)}"
            f" —— 句柄会被截断"
        )


def test_global_lock_is_pointer_typed():
    """README 里那条坑的主角:GlobalLock 必须返回指针,不能是整数。

    截断之后的指针写进去 = 写非法地址 = 进程崩 0xC0000005。
    """
    assert api.kernel32.GlobalLock.restype is ctypes.c_void_p
    assert api.kernel32.GlobalAlloc.restype is wt.HGLOBAL
    assert api.user32.GetForegroundWindow.restype is wt.HWND


# ---------------- 显示器与 DPI ----------------
#
# README 明写:多显示器的位置要用 EnumDisplayMonitors、不能用
# GetSystemMetrics;高分屏必须声明 DPI 感知、不能用 GetDeviceCaps。
# 这两条都依赖真实显示器环境,所以拿不到就跳过,绝不让用例在别人
# 机器上炸掉。

def _monitors_or_skip() -> list:
    mons = monitors.list_monitors()
    if not mons:
        pytest.skip("这台机器枚举不到显示器(无头/精简环境)")
    return mons


def test_list_monitors_returns_sane_entries():
    """每块屏:整块屏幕有正面积、工作区落在整块屏幕里面、缩放 >= 1。"""
    for mon in _monitors_or_skip():
        left, top, right, bottom = mon.monitor_rect
        assert right > left and bottom > top

        wl, wt_, wr, wb = mon.work_area
        # 工作区是"任务栏之外的可用区域",必须落在整块屏幕里
        assert left <= wl and top <= wt_
        assert wr <= right and wb <= bottom

        assert mon.scale >= 1.0
        assert mon.device


def test_monitor_at_point_finds_that_monitor():
    """点在那块屏上 → 必须找得到那**块**屏(多屏时坐标原点各不相同)。"""
    mons = _monitors_or_skip()
    mon = mons[0]
    x = (mon.monitor_rect[0] + mon.monitor_rect[2]) // 2
    y = (mon.monitor_rect[1] + mon.monitor_rect[3]) // 2

    found = monitors.monitor_at_point(x, y)
    assert found is not None
    assert found.device == mon.device


def test_monitor_by_device_roundtrip():
    """按设备名找屏:认得的找得到,认不得的返回 None(别硬套旧坐标)。"""
    mons = _monitors_or_skip()
    assert monitors.monitor_by_device(mons[0].device).device == mons[0].device
    assert monitors.monitor_by_device("") is None
    assert monitors.monitor_by_device("\\\\.\\NOSUCHDISPLAY") is None


def test_primary_monitor_exists():
    mons = _monitors_or_skip()
    assert monitors.primary_monitor() is not None


def test_set_dpi_awareness_prefers_per_monitor_v2(monkeypatch):
    """DPI 感知的**选择顺序**:优先 Per-Monitor V2,拿不到才逐级降级。

    真的调 SetProcessDpiAwarenessContext 是**进程级、不可撤销**的
    (声明了就改不回来)—— 测试里真调会把整个 pytest 进程变成 DPI 感知,
    别的用例要是建真窗口,尺寸就会跟着变。所以这里用假的 ctypes 调用
    只验决策顺序,真机效果以 README 的实测记录为准。
    """
    monkeypatch.setattr(dpi, "_set", False)
    fake = _FakeDpiWinDll(user32_result=0)
    monkeypatch.setattr(ctypes, "windll", fake)

    assert dpi.set_dpi_awareness() == "per-monitor-v2"
    assert fake.calls[0] == ("SetProcessDpiAwarenessContext", -4)
    # 幂等:第二次直接返回 already,不再调系统
    assert dpi.set_dpi_awareness() == "already"
    assert len(fake.calls) == 1


def test_set_dpi_awareness_falls_back_to_per_monitor(monkeypatch):
    """V2 不可用(老系统)→ 退到 shcore 的 Per-Monitor。"""
    monkeypatch.setattr(dpi, "_set", False)
    fake = _FakeDpiWinDll(user32_result=1, shcore_result=0)
    monkeypatch.setattr(ctypes, "windll", fake)

    assert dpi.set_dpi_awareness() == "per-monitor"
    assert [c[0] for c in fake.calls] == [
        "SetProcessDpiAwarenessContext", "SetProcessDpiAwareness"]


def test_set_dpi_awareness_falls_back_to_system(monkeypatch):
    """再退一步 → SetProcessDPIAware(只有 system aware)。"""
    monkeypatch.setattr(dpi, "_set", False)
    fake = _FakeDpiWinDll(user32_result=1, shcore_result=1, dpi_aware_result=1)
    monkeypatch.setattr(ctypes, "windll", fake)

    assert dpi.set_dpi_awareness() == "system"


def test_set_dpi_awareness_reports_none_when_all_fail(monkeypatch):
    """全都失败 → 老实报 "none",别假装自己 DPI 感知。"""
    monkeypatch.setattr(dpi, "_set", False)
    fake = _FakeDpiWinDll(user32_result=1, shcore_result=1, dpi_aware_result=0)
    monkeypatch.setattr(ctypes, "windll", fake)

    assert dpi.set_dpi_awareness() == "none"


class _FakeDpiWinDll:
    """假的 ``ctypes.windll``:只回答 DPI 感知那三个 API。

    注意这几个函数必须是**普通函数**(不是绑定方法)——
    dpi.py 里会给它们赋 ``fn.argtypes``,绑定方法不让赋属性。
    """

    def __init__(self, user32_result=0, shcore_result=0, dpi_aware_result=1):
        self.calls: list[tuple] = []
        self._user32_result = user32_result
        self._shcore_result = shcore_result
        self._dpi_aware_result = dpi_aware_result

        outer = self

        def _set_context(ctx):
            outer.calls.append(("SetProcessDpiAwarenessContext", ctx))
            return outer._user32_result

        def _set_dpi_aware():
            outer.calls.append(("SetProcessDPIAware",))
            return outer._dpi_aware_result

        def _set_awareness(ctx):
            outer.calls.append(("SetProcessDpiAwareness", ctx))
            return outer._shcore_result

        self.user32 = _FakeWin32Lib(
            SetProcessDpiAwarenessContext=_set_context,
            SetProcessDPIAware=_set_dpi_aware,
        )
        self.shcore = _FakeWin32Lib(SetProcessDpiAwareness=_set_awareness)


class _FakeWin32Lib:
    """一个假的 DLL:只有给定的几个函数。"""

    def __init__(self, **functions):
        self.__dict__.update(functions)


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
