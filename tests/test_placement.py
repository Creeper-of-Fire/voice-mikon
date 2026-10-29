"""胶囊摆位的单元测试:三种模式的计算 + 位置记忆 + 光标定位回退链。

这些都是**纯逻辑**,不需要开窗口 —— 算位置、换算偏移、挑定位信号
都不涉及 GUI,可以在没有屏幕的环境里跑。

真机行为(光标到底能不能跟住、拖拽顺不顺手)要靠真人冒烟。
"""

import pytest

from qwen3_voice.config import Config
from qwen3_voice.ui import placement
from qwen3_voice.ui.placement import (
    bottom_position,
    clamp_to_work_area,
    custom_position,
    decode_offset,
    encode_offset,
    follow_position,
)
from qwen3_voice.win32 import caret


# 一个 1920×1080 的工作区(物理像素)
WORK = (0, 0, 1920, 1040)


# ---------------- 跟随输入:贴着光标摆 ----------------

def test_follow_position_prefers_below_the_anchor():
    """优先放在锚点**下方** —— 像输入法候选框,贴着字但不盖住字。"""
    x, y = follow_position(anchor_x=900, anchor_y=400,
                           win_w=512, win_h=260, work_area=WORK)

    # 水平居中于锚点
    assert x == 900 - 512 // 2
    # 垂直:锚点 + 间隙
    assert y == 400 + placement.ANCHOR_GAP


def test_follow_position_flips_above_when_there_is_no_room_below():
    """下方放不下(会顶出工作区底边)就翻到上方。"""
    x, y = follow_position(anchor_x=900, anchor_y=1000,
                           win_w=512, win_h=260, work_area=WORK)

    assert y == 1000 - placement.ANCHOR_GAP - 260


def test_follow_position_clamps_into_the_work_area():
    """锚点贴着左边缘时,胶囊不能跑出工作区。"""
    x, y = follow_position(anchor_x=10, anchor_y=400,
                           win_w=512, win_h=260, work_area=WORK)

    left, top, right, bottom = WORK
    assert x >= left + placement.EDGE_MARGIN
    assert y >= top + placement.EDGE_MARGIN
    assert x + 512 <= right - placement.EDGE_MARGIN


def test_follow_position_uses_the_work_area_of_the_anchor_screen():
    """锚点在哪块屏就用那块屏的工作区 —— 多屏时不会摆到别的屏去。"""
    second = (1920, 0, 3840, 1040)
    x, y = follow_position(anchor_x=2000, anchor_y=400,
                           win_w=512, win_h=260, work_area=second)

    assert x >= second[0]
    assert y >= second[1]


def test_clamp_never_goes_out_of_a_tiny_work_area():
    """窗口比工作区还大时也不能算出负坐标。"""
    tiny = (0, 0, 100, 100)
    x, y = clamp_to_work_area(-500, -500, 512, 260, tiny)

    assert x >= tiny[0]
    assert y >= tiny[1]


# ---------------- 固定底部 ----------------

def test_bottom_position_is_centered_at_the_bottom():
    """固定底部:工作区底部居中(原来的行为)。"""
    x, y = bottom_position(win_w=512, win_h=260, work_area=WORK,
                           bottom_margin=20)

    assert x == (1920 - 512) // 2
    assert y == 1040 - 260 - 20


# ---------------- 自定义位置的记忆与还原 ----------------

def test_encode_offset_is_relative_to_the_work_area():
    """存的是**相对工作区左上角**的逻辑偏移,不是绝对屏幕坐标。"""
    # 窗口在 (100, 200)(物理),缩放 1.75,工作区原点 (0, 0)
    geometry = encode_offset(100, 200, WORK, 1.75)

    assert geometry == "57,114"      # 100/1.75=57.1 → 57 ; 200/1.75=114.3 → 114


def test_encode_offset_accounts_for_a_non_zero_work_area_origin():
    """副屏的工作区原点不是 0 —— 偏移必须减掉它。"""
    second = (1920, 0, 3840, 1040)
    geometry = encode_offset(2020, 175, second, 1.0)

    assert geometry == "100,175"


def test_custom_position_restores_the_saved_offset():
    """还原:工作区原点 + 偏移×缩放。"""
    second = (1920, 0, 3840, 1040)
    # 上次在副屏记住了 "100,175"(逻辑像素)
    x, y = custom_position("100,175", second, 1.0, 512, 260)

    assert x == 2020
    assert y == 175


def test_custom_position_scales_the_offset_back_to_physical():
    """偏移存的是逻辑像素,还原时要乘回缩放。"""
    x, y = custom_position("100,175", WORK, 1.75, 512, 260)

    assert x == int(round(100 * 1.75))
    assert y == int(round(175 * 1.75))


def test_custom_position_returns_none_for_unparsable_geometry():
    """记忆坏掉(手改过配置文件)就当没有,别硬套一个坐标。"""
    assert custom_position("不是数字", WORK, 1.0, 512, 260) is None
    assert custom_position("1,2,3", WORK, 1.0, 512, 260) is None
    assert custom_position("", WORK, 1.0, 512, 260) is None


def test_decode_offset_parses_the_two_number_format():
    assert decode_offset("12,34") == (12, 34)
    assert decode_offset("12") is None
    assert decode_offset("a,b") is None


def test_custom_position_is_clamped_into_the_work_area():
    """记忆的位置离屏了(换了个更小的屏)也要夹回工作区。"""
    x, y = custom_position("99999,99999", WORK, 1.0, 512, 260)

    assert x + 512 <= WORK[2] - placement.EDGE_MARGIN
    assert y + 260 <= WORK[3] - placement.EDGE_MARGIN


# ---------------- 光标定位的回退链 ----------------

def _stub_anchor_sources(monkeypatch, uia=None, gui=None, cursor=None,
                          foreground=None):
    """把四级定位手段全部换成假的。"""
    monkeypatch.setattr(caret, "get_caret_point_uia",
                        lambda: uia)
    monkeypatch.setattr(caret, "get_caret_rect_gui",
                        lambda hwnd=0: gui)
    monkeypatch.setattr(caret, "get_cursor_point",
                        lambda: cursor)
    if foreground is None:
        monkeypatch.setattr(caret, "get_window_rect", lambda hwnd: None,
                            raising=False)
    else:
        monkeypatch.setattr("qwen3_voice.win32.api.get_window_rect",
                            lambda hwnd: foreground)


def test_capture_anchor_prefers_the_uia_caret(monkeypatch):
    """UIA 拿得到就用它 —— 最准的一档。"""
    _stub_anchor_sources(monkeypatch, uia=(120, 340),
                          gui=(10, 20, 11, 40),
                          cursor=(5, 6))

    anchor = caret.capture_anchor(foreground_hwnd=999)

    assert anchor is not None
    assert anchor.kind == "uia"
    assert (anchor.x, anchor.y) == (120, 340)


def test_capture_anchor_falls_back_to_the_gui_caret(monkeypatch):
    """UIA 拿不到(应用不支持)→ 用传统控件的光标矩形。"""
    _stub_anchor_sources(monkeypatch, uia=None,
                          gui=(10, 20, 11, 40),
                          cursor=(5, 6))

    anchor = caret.capture_anchor(foreground_hwnd=999)

    assert anchor is not None
    assert anchor.kind == "caret"
    # 取矩形底边:胶囊贴在文字**下面**
    assert (anchor.x, anchor.y) == (10, 40)


def test_capture_anchor_falls_back_to_the_mouse(monkeypatch):
    """传统控件也没有 → 用鼠标位置(次优,但总比没有强)。"""
    _stub_anchor_sources(monkeypatch, uia=None, gui=None, cursor=(5, 6))

    anchor = caret.capture_anchor(foreground_hwnd=999)

    assert anchor is not None
    assert anchor.kind == "cursor"
    assert (anchor.x, anchor.y) == (5, 6)


def test_capture_anchor_falls_back_to_the_foreground_window(monkeypatch):
    """最后一档:只知道前台窗口在哪(取窗口中心当锚点)。"""
    _stub_anchor_sources(monkeypatch, uia=None, gui=None, cursor=None,
                          foreground=(100, 200, 500, 400))

    anchor = caret.capture_anchor(foreground_hwnd=999)

    assert anchor is not None
    assert anchor.kind == "window"
    assert (anchor.x, anchor.y) == (300, 300)


def test_capture_anchor_returns_none_when_nothing_is_available(monkeypatch):
    """一点信号都拿不到 → 返回 None,调用方走最终兜底。"""
    _stub_anchor_sources(monkeypatch, uia=None, gui=None, cursor=None,
                          foreground=None)

    assert caret.capture_anchor(foreground_hwnd=0) is None


def test_capture_anchor_ignores_the_own_window(monkeypatch):
    """前台窗口是胶囊自己 → 那个锚点毫无意义,直接放弃。"""
    _stub_anchor_sources(monkeypatch, uia=None, gui=None, cursor=None,
                          foreground=(100, 200, 500, 400))

    assert caret.capture_anchor(foreground_hwnd=777,
                                exclude_hwnd=777) is None


def test_gui_caret_rect_is_converted_to_screen_coordinates(monkeypatch):
    """``rcCaret`` 的坐标基准是**焦点控件客户区**,不是屏幕坐标。

    真机踩出来的坑:窗体在屏幕 (200,200)、焦点控件在 (208,231) 时,
    ``rcCaret`` 给的是 (0,0) —— 直接拿来当屏幕坐标,胶囊会跑到屏幕
    左上角。所以必须用 ``ClientToScreen`` 换算。
    """
    import ctypes
    import ctypes.wintypes as wt

    from qwen3_voice.win32 import caret as caret_mod

    def fake_get_gui_thread_info(thread_id, ptr):
        info = ctypes.cast(
            ptr, ctypes.POINTER(caret_mod.GUITHREADINFO)).contents
        info.flags = caret_mod.GUI_CARETVISIBLE
        info.hwndFocus = 4242
        info.rcCaret = wt.RECT(3, 5, 4, 27)
        return True

    def fake_client_to_screen(hwnd, point_ptr):
        point = ctypes.cast(point_ptr, ctypes.POINTER(wt.POINT)).contents
        point.x += 1000
        point.y += 2000
        return True

    monkeypatch.setattr(caret_mod.user32, "GetGUIThreadInfo",
                        fake_get_gui_thread_info)
    monkeypatch.setattr(caret_mod.user32, "ClientToScreen",
                        fake_client_to_screen)
    monkeypatch.setattr(caret_mod.user32, "GetWindowThreadProcessId",
                        lambda hwnd, pid: 99)

    rect = caret_mod.get_caret_rect_gui(1234)

    assert rect == (1003, 2005, 1004, 2027)


# ---------------- 配置校验 ----------------

def test_config_rejects_unknown_capsule_position():
    """写坏的档位要能被发现(而不是悄悄当成 follow)。"""
    cfg = Config()
    cfg.capsule_position = "solarized"

    issues = cfg.validate()

    assert any("capsule_position" in i for i in issues)


@pytest.mark.parametrize("mode", ["follow", "bottom", "custom"])
def test_config_accepts_the_three_position_modes(mode):
    cfg = Config()
    cfg.capsule_position = mode

    assert cfg.validate() == []


def test_config_round_trips_the_new_fields():
    """新字段要能存能读 —— 面板改完重启还在。"""
    cfg = Config()
    cfg.capsule_position = "custom"
    cfg.show_preview = False
    cfg.capsule_geometry = "12,34"
    cfg.capsule_monitor = "\\\\.\\DISPLAY2"

    restored = Config.from_dict(cfg.to_dict())

    assert restored.capsule_position == "custom"
    assert restored.show_preview is False
    assert restored.capsule_geometry == "12,34"
    assert restored.capsule_monitor == "\\\\.\\DISPLAY2"
