"""悬浮球「跟随输入 / 记住位置 / 常驻」三档的单元测试。

三档是**三套不同的行为**,这里盯住共性与差异:

- 只有**真光标**(UIA / 传统控件光标 / 空输入框兜底)聚焦在输入处,
  follow 和 pinned 才露脸;custom 常驻;
- follow 跟着光标走、不可拖;pinned/custom 用**同一份**记住的位置,
  都可拖、拖完记住 —— 差别只在显隐;
- 跟随档松手**不写位置**(那个"挪过"是轮询线程挪的,不是用户拖的);
- 切档必须先用线程代数(epoch)停掉旧轮询线程再摆位 —— 否则旧线程
  醒来会把球又挪回去,看起来就是"切了档没反应"。

这些都是纯逻辑 + 假对象,不碰真实窗口、不抢前台焦点。
"""

from __future__ import annotations

import threading
import types

from qwen3_voice.config import Config
from qwen3_voice.core.controller import UIEvent
from qwen3_voice.ui.capsule import VoiceCapsule
from qwen3_voice.win32.caret import Anchor


# ---------------- 光标判定:只要真光标,不许拿鼠标冒充 ----------------

def test_require_caret_stops_at_the_two_real_caret_levels(monkeypatch):
    """``require_caret=True``:只用 uia / caret 两级,拿不到就返回 None。

    后两级(鼠标位置 / 前台窗口中心)说的根本不是"光标" ——
    鼠标在哪跟光标在哪没有必然关系(用户可能只是把鼠标挪过来看一眼)。
    拿它们冒充的话,球会满屏乱跳。
    """
    import qwen3_voice.win32.caret as caret

    monkeypatch.setattr(caret, "get_caret_point_uia", lambda: None)
    monkeypatch.setattr(caret, "get_caret_rect_gui", lambda hwnd=0: None)
    # edit 级(空输入框兜底)也是真光标判定的一环,一并挡住,
    # 不然这条用例会真的去问系统的 UIA 焦点 —— 测试就不纯了
    monkeypatch.setattr(caret, "get_focused_edit_rect_uia", lambda: None)
    monkeypatch.setattr(caret, "get_cursor_point", lambda: (10, 20))

    # 普通模式:一路降到鼠标位置
    anchor = caret.capture_anchor(foreground_hwnd=1234)
    assert anchor is not None
    assert anchor.kind == "cursor"

    # 要求真光标:两级都拿不到 → None,**不降级**
    assert caret.capture_anchor(foreground_hwnd=1234, require_caret=True) is None


def test_require_caret_accepts_the_uia_level(monkeypatch):
    """UIA 那一级拿到光标 → 就是真光标。"""
    import qwen3_voice.win32.caret as caret

    monkeypatch.setattr(caret, "get_caret_point_uia", lambda: (30, 40))

    anchor = caret.capture_anchor(foreground_hwnd=1234, require_caret=True)
    assert anchor is not None
    assert (anchor.x, anchor.y, anchor.kind) == (30, 40, "uia")


def test_require_caret_accepts_the_traditional_caret_level(monkeypatch):
    """传统控件的光标(记事本那种)也算真光标。"""
    import qwen3_voice.win32.caret as caret

    monkeypatch.setattr(caret, "get_caret_point_uia", lambda: None)
    monkeypatch.setattr(caret, "get_caret_rect_gui",
                        lambda hwnd=0: (10, 20, 12, 30))

    anchor = caret.capture_anchor(foreground_hwnd=1234, require_caret=True)
    assert anchor is not None
    assert anchor.kind == "caret"
    # 取字符矩形的**左下角**(胶囊贴在文字下面,像输入法候选框)
    assert (anchor.x, anchor.y) == (10, 30)


def test_caret_anchor_is_not_taken_from_the_capsule_itself(monkeypatch):
    """前台窗口就是胶囊自己 → 直接放弃。

    这时候问出来的"光标"是胶囊自己的(WebView2 里也有文本),
    拿来摆位只会把球算到眼前。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _follow_capsule()
    monkeypatch.setattr(cap_mod, "get_foreground_window", lambda: 777)
    cap._native_handle = lambda: 777

    assert cap._capture_follow_anchor() is None


# ---------------- 轮询一轮的行为 ----------------

def _follow_capsule(orb_position: str = "follow",
                    floating_orb: bool = True) -> VoiceCapsule:
    """一个只够跑跟随逻辑的假胶囊(绕过了 __init__)。"""
    cap = VoiceCapsule.__new__(VoiceCapsule)
    cap._destroyed = False
    cap._want_visible = False
    cap._orb_shown = False
    cap._orb_regions_ready = True
    cap._last_foreground = 0
    cap._drag_lock = threading.Lock()
    cap._drag_start_rect = None
    # 轮询线程的代数:tick 里要对着它检查,测试对象直接给 0
    cap._follow_epoch = 0
    cap.controller = types.SimpleNamespace(
        config=types.SimpleNamespace(floating_orb=floating_orb,
                                     orb_position=orb_position))
    return cap


def test_follow_tick_hides_the_ball_when_there_is_no_caret():
    """没有真光标 → 球收起来。

    "不在输入框里就别杵在那儿" —— 跟随档的球是**跟着光标**的,
    没有光标就没有它该在的地方。
    """
    cap = _follow_capsule()
    cap._capture_follow_anchor = lambda: None
    hidden = []
    cap._hide_orb = lambda: hidden.append(True)

    cap._follow_tick(0)

    assert hidden == [True]


def test_follow_tick_places_and_shows_the_ball_at_the_caret():
    """有真光标 → 露脸(摆位在露脸那条路里做)。"""
    cap = _follow_capsule()
    anchor = Anchor(100, 200, "uia")
    cap._capture_follow_anchor = lambda: anchor
    shown = []
    cap._show_orb = lambda a=None: shown.append(a)

    cap._follow_tick(0)

    assert shown == [anchor]


def test_follow_tick_only_moves_the_ball_when_it_is_already_shown():
    """已经露着脸了 → 只挪位置,不重复走"露脸"那条路。"""
    cap = _follow_capsule()
    cap._orb_shown = True
    anchor = Anchor(100, 200, "uia")
    cap._capture_follow_anchor = lambda: anchor
    placed = []
    cap._place_follow = lambda a: placed.append(a) or True
    shown = []
    cap._show_orb = lambda a=None: shown.append(a)

    cap._follow_tick(0)

    assert placed == [anchor]
    assert shown == []


def test_follow_tick_keeps_the_window_during_recording():
    """录音中:胶囊正在录,窗口**不能**被轮询藏掉。"""
    cap = _follow_capsule()
    cap._want_visible = True
    hidden = []
    cap._hide_orb = lambda: hidden.append(True)

    cap._follow_tick(0)

    assert hidden == []


def test_follow_tick_waits_for_the_hit_region_before_showing():
    """命中区还没裁好 → 先别露脸。

    窗口是固定尺寸的透明窗,没裁形状之前那一片空白会把底下应用的
    点击全吃掉 —— 球得等 Python 裁好了再露面。
    """
    cap = _follow_capsule()
    cap._orb_regions_ready = False
    cap._capture_follow_anchor = lambda: Anchor(1, 2, "uia")
    shown = []
    cap._show_orb = lambda a=None: shown.append(a)
    scheduled = []
    cap._schedule_show_orb = lambda: scheduled.append(True)

    cap._follow_tick(0)

    assert shown == []
    assert scheduled == [True]


def test_follow_tick_does_nothing_when_the_orb_is_off():
    """悬浮球关着 → 轮询什么都不做。"""
    cap = _follow_capsule(floating_orb=False)
    called = []
    cap._capture_follow_anchor = lambda: called.append(True)
    cap._hide_orb = lambda: called.append("hide")

    cap._follow_tick(0)

    assert called == []


# ---------------- 跟随档不写位置 ----------------

def test_follow_mode_does_not_save_the_orb_position(monkeypatch):
    """跟随档:松手**不**写位置。

    球的位置是光标说了算,轮询线程一直在挪窗口 —— 那个"挪过"
    不是用户拖的,写进配置只会存下一个莫名其妙的位置。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _follow_capsule(orb_position="follow")
    cap._drag_start_rect = (100, 200, 612, 460)
    cap._give_back_focus = lambda *a, **k: None
    saved = []
    cap._save_orb_position = lambda: saved.append(True)
    # 窗口确实被挪过了(轮询挪的)
    monkeypatch.setattr(cap_mod, "get_window_rect",
                        lambda hwnd: (150, 260, 662, 520))

    cap._end_drag()

    assert saved == []


def test_custom_mode_saves_the_orb_position(monkeypatch):
    """自定义档:拖过就记住(老行为不变)。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _follow_capsule(orb_position="custom")
    cap._drag_start_rect = (100, 200, 612, 460)
    cap._give_back_focus = lambda *a, **k: None
    saved = []
    cap._save_orb_position = lambda: saved.append(True)
    monkeypatch.setattr(cap_mod, "get_window_rect",
                        lambda hwnd: (150, 260, 662, 520))

    cap._end_drag()

    assert saved == [True]


# ---------------- 档位本身的约定 ----------------

def test_orb_position_defaults_to_follow():
    """默认跟随:球跟着光标走,是用户最常有的期待。"""
    assert Config().orb_position == "follow"


def test_orb_position_has_three_modes():
    """三档(设计由用户拍板):跟随输入 / 记住位置 / 常驻。

    pinned/custom 共用一份持久化位置,差别只在显隐。
    和胶囊的 CAPSULE_POSITION_MODES(follow/bottom/custom)是两套
    独立设计 —— 球的档里没有 bottom。
    """
    from qwen3_voice.config import ORB_POSITION_MODES

    assert ORB_POSITION_MODES == ("follow", "pinned", "custom")


def test_config_rejects_an_unknown_orb_position():
    """手改配置文件写了个不存在的档位 → 校验要报出来。"""
    cfg = Config()
    cfg.orb_position = "top"

    issues = cfg.validate()

    assert any("orb_position" in issue for issue in issues)


def test_orb_position_round_trips_through_the_config():
    """档位要能存能读。"""
    cfg = Config()
    cfg.orb_position = "custom"

    restored = Config.from_dict(cfg.to_dict())

    assert restored.orb_position == "custom"


# ---------------- 记住位置档(pinned):显隐同跟随,位置归用户 ----------------

def test_pinned_tick_shows_the_ball_when_the_caret_appears():
    """记住位置档:光标聚焦到输入处 → 露脸(摆位走记忆的位置)。"""
    cap = _follow_capsule(orb_position="pinned")
    anchor = Anchor(100, 200, "uia")
    cap._capture_follow_anchor = lambda: anchor
    shown = []
    cap._show_orb = lambda a=None: shown.append(a)

    cap._follow_tick(0)

    assert shown == [anchor]


def test_pinned_tick_does_not_move_the_ball_once_shown():
    """记住位置档已露脸:位置固定在记忆处,轮询什么都不用做。"""
    cap = _follow_capsule(orb_position="pinned")
    cap._orb_shown = True
    called = []
    cap._capture_follow_anchor = lambda: Anchor(1, 2, "uia")
    cap._place_follow = lambda a: called.append("move")
    cap._show_orb = lambda a=None: called.append("show")

    cap._follow_tick(0)

    assert called == []


def test_place_orb_pinned_uses_the_saved_position(monkeypatch):
    """记住位置档:_place_orb 摆到**记住的位置**(和 custom 同一套摆位)。

    两档共用一份 orb_geometry —— 差别只在显隐,摆位必须完全一样。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _follow_capsule(orb_position="pinned")
    cap._native_handle = lambda: 777
    cap._window_size = lambda: (512, 260)
    cap._saved_orb_monitor = lambda: None
    cap._saved_orb_geometry = lambda: "100,300"
    mon = types.SimpleNamespace(work_area=(0, 0, 1920, 1040), scale=1.0,
                                device="\\\\.\\DISPLAY1")
    monkeypatch.setattr(cap_mod, "primary_monitor", lambda: mon)
    moved = []
    monkeypatch.setattr(cap_mod, "move_window_to",
                        lambda hwnd, x, y: moved.append((x, y)))

    assert cap._place_orb() is True
    assert len(moved) == 1


def test_pinned_mode_saves_the_orb_position(monkeypatch):
    """记住位置档:拖过就**记**住 —— 位置归用户管(和 custom 一致)。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _follow_capsule(orb_position="pinned")
    cap._drag_start_rect = (100, 200, 612, 460)
    cap._give_back_focus = lambda *a, **k: None
    saved = []
    cap._save_orb_position = lambda: saved.append(True)
    monkeypatch.setattr(cap_mod, "get_window_rect",
                        lambda hwnd: (150, 260, 662, 520))

    cap._end_drag()

    assert saved == [True]


# ---------------- 切档竞态:旧轮询线程必须先停 ----------------

def test_follow_tick_aborts_when_the_epoch_changed_during_capture():
    """捕获光标的那几百毫秒里切了档 → 结果作废,绝不再动窗口。

    这正是"切了档没反应"的根因:旧线程攥着一个刚捕获的锚点醒来,
    把球又挪回光标处(或藏掉),盖掉新档位的摆位。
    """
    cap = _follow_capsule()

    def _slow_capture():
        cap._follow_epoch = 1      # 模拟:捕获期间发生了切档(换代)
        return Anchor(100, 200, "uia")

    cap._capture_follow_anchor = _slow_capture
    hidden, shown, placed = [], [], []
    cap._hide_orb = lambda: hidden.append(True)
    cap._show_orb = lambda a=None: shown.append(a)
    cap._place_follow = lambda a: placed.append(a)

    cap._follow_tick(0)

    assert hidden == []
    assert shown == []
    assert placed == []


def test_switching_to_custom_stops_the_follow_thread_before_placing():
    """切档事件:先停轮询线程,再摆位 —— 顺序反了就会被旧线程盖掉。"""
    cap = _follow_capsule(orb_position="custom")
    cap._gui_ready = threading.Event()
    cap._gui_ready.set()
    order = []
    cap._stop_follow_thread = lambda: order.append("stop")
    cap._call_js = lambda *a, **k: order.append("js")
    cap._show_orb = lambda *a, **k: order.append("show")

    cap.handle_event(UIEvent(kind="orb_position", text="custom"))

    assert order == ["stop", "js", "show"]


def test_switching_to_pinned_also_stops_the_thread():
    """切到记住位置档同样要先停线程 —— 它也要接管显隐,不能跟旧的抢。"""
    cap = _follow_capsule(orb_position="pinned")
    cap._gui_ready = threading.Event()
    cap._gui_ready.set()
    order = []
    cap._stop_follow_thread = lambda: order.append("stop")
    cap._show_orb = lambda *a, **k: order.append("show")

    cap.handle_event(UIEvent(kind="orb_position", text="pinned"))

    assert order == ["stop", "show"]


def test_stop_follow_thread_bumps_the_epoch():
    """停线程 = 换代:epoch +1,醒来的旧线程立刻收手。"""
    cap = _follow_capsule()
    cap._follow_lock = threading.Lock()
    before = cap._follow_epoch

    cap._stop_follow_thread()

    assert cap._follow_epoch == before + 1
