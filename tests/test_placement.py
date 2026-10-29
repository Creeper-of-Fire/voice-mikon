"""胶囊摆位的单元测试:三种模式的计算 + 位置记忆 + 光标定位回退链。

这些都是**纯逻辑**,不需要开窗口 —— 算位置、换算偏移、挑定位信号
都不涉及 GUI,可以在没有屏幕的环境里跑。

真机行为(光标到底能不能跟住、拖拽顺不顺手)要靠真人冒烟。
"""

import re
from pathlib import Path

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

# 胶囊前端样式表(球形态的尺寸就是从这儿来的)
CAPSULE_CSS = (Path(__file__).resolve().parents[1]
               / "src" / "qwen3_voice" / "ui" / "static" / "capsule.css")


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


# ---------------- 常驻悬浮球:位置计算与记忆 ----------------
#
# 悬浮球和胶囊**共用同一个窗口**(固定尺寸,不能 resize),球固定待在
# 窗口的底边居中处。所以"球在哪"就是"窗口摆哪",两边互为逆运算。


def _css_value(css: str, selector: str, prop: str) -> str:
    """从 CSS 里抠出某个选择器的某个属性值(纯文本解析,够用)。

    选择器要求出现在**行首**:否则 ``.recording-pill`` 会先撞上
    ``.can-drag .recording-pill``(它也在文件里,而且没有 height)。
    """
    m = re.search(r"(?:^|\n)[ \t]*" + re.escape(selector) + r"\s*\{([^}]*)\}", css)
    assert m, f"CSS 里找不到选择器: {selector}"
    m2 = re.search(re.escape(prop) + r"\s*:\s*([^;]+)", m.group(1))
    assert m2, f"{selector} 里找不到属性 {prop}"
    return m2.group(1).strip()


def test_the_ball_is_exactly_as_big_as_the_capsule():
    """球和胶囊**一样大**:球的直径必须等于胶囊的高度。

    球不是另一个元素 —— 它就是这颗胶囊收窄成的正圆。不等大的话,
    球横向长开时走的轨迹和胶囊对不上,窗口的命中区(SetWindowRgn,
    按上报的矩形裁)就会切到球上 —— 真机症状是"球的上半截多一条
    白线",像被切出一个球冠。
    """
    css = CAPSULE_CSS.read_text(encoding="utf-8")

    assert _css_value(css, ".recording-pill", "height") == f"{placement.ORB_SIZE}px"


def test_the_ball_form_is_the_pill_narrowed_to_the_ball_size():
    """CSS 里球形态的宽度也必须等于 ORB_SIZE —— 前后端只有一个真相源。

    改了一边忘了另一边,球就会和胶囊对不上(和上面那条一样的后果)。
    """
    css = CAPSULE_CSS.read_text(encoding="utf-8")

    selector = "body.orb-mode.orb-idle .recording-pill"
    assert _css_value(css, selector, "max-width") == f"{placement.ORB_SIZE}px"


def test_orb_inset_is_centered_at_the_bottom():
    """球在窗口里底边居中 → 和胶囊同一横坐标,展开时不用挪位置。"""
    assert placement.orb_inset_x(512) == (512 - placement.ORB_SIZE) // 2


def test_orb_screen_position_is_the_bottom_center():
    """窗口位置 → 球的左上角(物理像素)。"""
    x, y = placement.orb_screen_position(100, 200, 512, 260, 1.0)

    assert x == 100 + (512 - placement.ORB_SIZE) // 2
    assert y == 200 + 260 - (placement.ORB_INSET_Y + placement.ORB_SIZE)


def test_orb_window_position_is_the_inverse():
    """球的位置 → 窗口位置:和上面互为逆运算。"""
    x, y = placement.orb_screen_position(100, 200, 512, 260, 1.0)

    win = placement.orb_window_position(x, y, 512, 260, 1.0, WORK)

    assert win == (100, 200)


def test_orb_position_is_clamped_into_the_work_area():
    """拖出屏幕的球要夹回来 —— 窗口不能跟着甩出去。"""
    x, y = placement.clamp_orb_position(-500, -500, 512, 260, 1.0, WORK)

    assert x >= WORK[0]
    assert y >= WORK[1]
    # 纵向还得多留一截:球的上方要放得下展开后的胶囊(文字条 + 胶囊)
    assert y >= WORK[1] + 260 - (placement.ORB_INSET_Y + placement.ORB_SIZE)


def test_orb_position_from_geometry_restores_the_saved_offset():
    """记忆的偏移 → 窗口位置(存的是相对工作区的逻辑偏移,换屏不飞)。"""
    second = (1920, 0, 3840, 1040)

    win = placement.orb_position_from_geometry("100,300", second, 1.0,
                                                512, 260)

    assert win[0] == 2020 - (512 - placement.ORB_SIZE) // 2
    assert win[1] == 300 + (placement.ORB_INSET_Y + placement.ORB_SIZE) - 260


def test_orb_position_from_geometry_clamps_a_too_high_saved_position():
    """记住的位置太高(换了块矮屏)也要夹回来 —— 展开后文字条不能出屏。"""
    win = placement.orb_position_from_geometry("100,10", WORK, 1.0, 512, 260)

    assert win[1] == (260 - (placement.ORB_INSET_Y + placement.ORB_SIZE)
                      + placement.ORB_INSET_Y + placement.ORB_SIZE - 260)


def test_orb_position_from_geometry_returns_none_for_garbage():
    """记忆坏掉(手改过配置文件)就当没有,别硬套一个坐标。"""
    assert placement.orb_position_from_geometry(
        "不是数字", WORK, 1.0, 512, 260) is None
    assert placement.orb_position_from_geometry(
        "", WORK, 1.0, 512, 260) is None


def test_default_orb_position_is_the_bottom_right_corner():
    """默认摆在工作区右下角(不挡中间的编辑区,也不挡任务栏)。"""
    x, y = placement.default_orb_position(WORK, 1.0)

    assert x == WORK[2] - placement.ORB_MARGIN - placement.ORB_SIZE
    assert y == WORK[3] - placement.ORB_MARGIN - placement.ORB_SIZE


def test_orb_position_round_trips_through_the_saved_offset():
    """球的屏幕坐标 → 记忆的偏移 → 还原回来,位置不变。"""
    x, y = placement.orb_screen_position(100, 200, 512, 260, 1.0)
    geometry = encode_offset(x, y, WORK, 1.0)

    win = placement.orb_position_from_geometry(geometry, WORK, 1.0, 512, 260)

    assert win is not None
    back_x, back_y = placement.orb_screen_position(
        win[0], win[1], 512, 260, 1.0)
    assert (back_x, back_y) == (x, y)


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


def test_floating_orb_defaults_to_off():
    """默认关:老用户看到的还是"按热键才弹胶囊",一点不变。"""
    assert Config().floating_orb is False


def test_config_round_trips_the_orb_fields():
    """常驻球的开关与位置记忆要能存能读。"""
    cfg = Config()
    cfg.floating_orb = True
    cfg.orb_geometry = "12,34"
    cfg.orb_monitor = "\\\\.\\DISPLAY2"

    restored = Config.from_dict(cfg.to_dict())

    assert restored.floating_orb is True
    assert restored.orb_geometry == "12,34"
    assert restored.orb_monitor == "\\\\.\\DISPLAY2"
