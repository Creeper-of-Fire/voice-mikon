"""UI 层的单元测试:推送过滤、胶囊显隐时机、定点修复。

这些都不需要真的开窗口 —— 用假的 pywebview 窗口对象顶替,
测的是**逻辑**而不是 GUI 集成(GUI 行为要真人冒烟)。
"""

import re
import threading
import types

import pytest

from qwen3_voice.core.controller import State, UIEvent
from qwen3_voice.ui.capsule import WIN_H, WIN_W, VoiceCapsule
from qwen3_voice.ui.panel import VoicePanel, _EventPump


class FakeWindow:
    """假的 pywebview 窗口。

    只实现我们用到的那几个接口:``evaluate_js`` 记录脚本,
    ``transparent`` 用来验证"掐掉 pywebview 透明 hack"的那处定点修复。
    """

    def __init__(self, namespace="__qwen"):
        self.namespace = namespace
        self.transparent = False
        self.scripts: list[str] = []
        self.native = None      # 没有 native → _native_handle() 返回 0

    def evaluate_js(self, script):
        self.scripts.append(script)
        return {"sh": 0, "ih": 0}

    def methods(self) -> list[str]:
        """按调用顺序列出被调用的前端方法名(去重)。

        脚本里每个方法名出现两次 —— 一次是"存在性检查",一次是真正的
        调用 —— 所以这里按首次出现去重。
        """
        pat = re.escape(self.namespace) + r"\.(\w+)"
        return list(dict.fromkeys(re.findall(pat, "\n".join(self.scripts))))


def _make_panel() -> VoicePanel:
    panel = VoicePanel.__new__(VoicePanel)
    panel._state = State.IDLE
    panel._frontend_ready = threading.Event()
    panel._frontend_ready.set()
    panel._window = FakeWindow("__qwen")
    return panel


def _make_capsule() -> VoiceCapsule:
    cap = VoiceCapsule.__new__(VoiceCapsule)
    cap._state = State.IDLE
    cap._gui_ready = threading.Event()
    cap._gui_ready.set()
    cap._destroyed = False
    cap._last_preview = ""
    cap._want_visible = False
    cap._guard_started = True     # 别让测试起真的守卫线程
    cap._hide_timer = None
    cap._hide_lock = threading.Lock()
    cap._focus_restore = 0
    cap._target_hwnd = 0
    # 位置模式相关的新状态(绕过了 __init__,得手动补上)
    cap._anchor = None
    cap._dragging = False
    cap._drag_lock = threading.Lock()
    cap._window = FakeWindow("__capsule")
    cap.controller = None
    return cap


# ---------------- 推送按窗口过滤 ----------------

def test_panel_only_receives_three_methods():
    """面板瘦身:只吃 state / model / hotkey,别的都不推。"""
    panel = _make_panel()

    panel.handle_event(UIEvent(kind="state", state=State.RECORDING))
    panel.handle_event(UIEvent(kind="model", model_tag="1.7B"))
    panel.handle_event(UIEvent(kind="preview", text="你好"))
    panel.handle_event(UIEvent(kind="result", text="你好"))
    panel.handle_event(UIEvent(kind="error", text="出错"))
    panel.handle_event(UIEvent(kind="cancelled", text="已取消"))
    panel.push_levels([0.1, 0.2], 1.0)

    assert panel._window.methods() == ["setState", "setModel"]


def test_capsule_receives_full_set():
    """胶囊是录音过程的唯一载体:全套都要推。"""
    cap = _make_capsule()

    cap.handle_event(UIEvent(kind="state", state=State.RECORDING))
    cap.handle_event(UIEvent(kind="preview", text="你"))
    cap.handle_event(UIEvent(kind="result", text="你好"))
    cap.handle_event(UIEvent(kind="error", text="没听清"))
    cap.handle_event(UIEvent(kind="cancelled", text="已取消"))

    methods = cap._window.methods()
    assert "setState" in methods
    assert "resetForRecording" in methods
    assert "setPreview" in methods
    assert "setResult" in methods
    assert "setError" in methods
    assert "setCancelled" in methods


def test_levels_only_for_windows_that_want_them():
    """电平只有胶囊要 —— 面板不该为了没人要的窗口去采样。"""
    assert VoicePanel.wants_levels is False
    assert VoiceCapsule.wants_levels is True


# ---------------- 电平分发 ----------------

class FakeSink:
    def __init__(self, wants_levels=True, explode=False):
        self.wants_levels = wants_levels
        self.explode = explode
        self.events: list[UIEvent] = []
        self.levels_calls: list[tuple] = []

    def handle_event(self, event):
        if self.explode:
            raise RuntimeError("boom")
        self.events.append(event)

    def push_levels(self, levels, elapsed):
        self.levels_calls.append((levels, elapsed))


class FakeRecorder:
    def __init__(self):
        self.levels_called = 0

    def levels(self):
        self.levels_called += 1
        return [0.5, 0.25]

    @property
    def elapsed(self):
        return 3.0


class FakeController:
    def __init__(self):
        self.recorder = FakeRecorder()

        class Cfg:
            silence_rms = 0.002

        self.config = Cfg()


def test_pump_samples_levels_only_when_someone_wants_them():
    ctrl = FakeController()
    pump = _EventPump(ctrl)
    sink = FakeSink(wants_levels=False)
    pump.add_sink(sink)

    pump._push_levels()

    assert ctrl.recorder.levels_called == 0
    assert sink.levels_calls == []


def test_pump_pushes_levels_to_wanting_sinks():
    ctrl = FakeController()
    pump = _EventPump(ctrl)
    sink = FakeSink(wants_levels=True)
    pump.add_sink(sink)
    pump._state = State.RECORDING

    pump._push_levels()

    assert ctrl.recorder.levels_called == 1
    assert len(sink.levels_calls) == 1
    levels, elapsed = sink.levels_calls[0]
    assert elapsed == 3.0
    assert all(0.0 <= v <= 1.0 for v in levels)


def test_pump_one_broken_sink_does_not_break_others():
    ctrl = FakeController()
    pump = _EventPump(ctrl)
    pump.add_sink(FakeSink(explode=True))
    good = FakeSink()
    pump.add_sink(good)

    pump._dispatch(UIEvent(kind="state", state=State.IDLE))

    assert len(good.events) == 1


# ---------------- 胶囊显隐时机 ----------------

def test_capsule_shows_when_recording_starts():
    cap = _make_capsule()
    cap.handle_event(UIEvent(kind="state", state=State.RECORDING))
    assert cap._want_visible is True


def test_new_recording_cancels_pending_hide_timer():
    """上一轮的隐藏定时器必须作废,否则刚开始的胶囊会被藏掉。"""
    cap = _make_capsule()
    cap.handle_event(UIEvent(kind="result", text="你好"))
    assert cap._hide_timer is not None

    cap.handle_event(UIEvent(kind="state", state=State.RECORDING))
    assert cap._hide_timer is None


def test_capsule_hides_itself_after_result():
    cap = _make_capsule()
    cap.handle_event(UIEvent(kind="result", text="你好"))
    assert cap._hide_timer is not None
    # 定时器是 daemon,测试结束前不用等它跑完
    cap._cancel_hide_timer()


def test_capsule_hide_clears_want_visible():
    cap = _make_capsule()
    cap._want_visible = True
    cap._hide()
    assert cap._want_visible is False


# ---------------- 定点修复:掐掉 pywebview 的透明 hack ----------------

def test_before_show_disables_pywebview_transparent_hack():
    """pywebview 对透明窗口会在导航开始时 form.Show()+form.Activate(),
    那会让胶囊在启动时自己冒出来并占住前台。

    ``_on_before_show`` 必须把 ``transparent`` 标记清掉 ——
    此时 BrowserForm 已经按 transparent=True 设好了 WebView2 的
    DefaultBackgroundColor=Transparent(透明不受影响),但导航还没开始。
    """
    cap = _make_capsule()
    cap._window.transparent = True

    cap._on_before_show()

    assert cap._window.transparent is False


def test_guard_hides_the_window_when_nobody_wants_it():
    """守卫的"该露着"判断:非常驻模式下还是老规矩 —— 没人要求显示就藏回去。

    这一条原来写的是"断言自己刚赋的值"(恒真、零覆盖),换成了真正
    盯住守卫判断依据的断言。
    """
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig())

    cap._want_visible = False
    assert cap._window_should_be_visible() is False

    cap._want_visible = True
    assert cap._window_should_be_visible() is True


def test_guard_keeps_the_ball_visible_in_orb_mode():
    """常驻球模式:球一直杵在那儿,守卫不能把它藏回去。

    "_want_visible" 的语义这时变了 —— "该露着"由球的模式决定,
    而不是"这一轮胶囊正在显示"。
    """
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig(floating_orb=True))

    # 球还没就位(前端还没报命中区)时,窗口不能先露脸
    cap._orb_shown = False
    assert cap._window_should_be_visible() is False

    cap._orb_shown = True
    assert cap._window_should_be_visible() is True


# ---------------- 窗口尺寸 ----------------

def test_capsule_window_size_is_fixed():
    """窗口尺寸必须是常量。

    透明窗口一旦被 resize,WebView2 的透明背景就会失效(整块客户区
    变成 Form 的灰底),色键和"resize 后重设透明"都绕不过去 ——
    所以唯一可靠的做法就是**别 resize**。这条测试是为了防止
    以后有人"顺手加个高度自适应"。
    """
    assert isinstance(WIN_W, int) and isinstance(WIN_H, int)
    # 高度要装得下"文字条最大高 + 间隙 + 胶囊 + 上下留白"
    assert WIN_H >= 160 + 10 + 40 + 20
    # 宽度要装得下文字条的最大宽度
    assert WIN_W >= 480 + 16


def test_capsule_has_no_resize_path():
    """胶囊不该再有 fit/resize 的代码路径。"""
    import inspect

    source = inspect.getsource(VoiceCapsule)
    assert "_schedule_fit" not in source
    assert "resize_window_no_activate" not in source


# ---------------- 点击胶囊不丢注入落点 ----------------

class _FakeController:
    def __init__(self):
        self.stopped = 0
        self.cancelled = 0
        self.started = 0

    def stop_recording(self):
        self.stopped += 1

    def cancel_recording(self):
        self.cancelled += 1

    def start_recording(self):
        self.started += 1
        return True


def test_recording_snapshots_target_hwnd(monkeypatch):
    """进入录音时快照前台窗口 —— 它是这一轮文字的注入落点。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_capsule()
    monkeypatch.setattr(cap_mod, "get_foreground_window", lambda: 4321)

    cap.handle_event(UIEvent(kind="state", state=State.RECORDING))

    assert cap._target_hwnd == 4321


def test_stop_button_gives_back_focus(monkeypatch):
    """点 ✓ 时前台若被点击激活到胶囊上,先还给注入落点再停录音。"""
    import types

    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_capsule()
    cap.controller = _FakeController()
    cap._target_hwnd = 4321          # 录音开始时的用户窗口
    # 假的 native 句柄:胶囊 hwnd = 777
    cap._window.native = types.SimpleNamespace(
        Handle=types.SimpleNamespace(ToInt64=lambda: 777))

    restored = []
    monkeypatch.setattr(cap_mod, "get_foreground_window", lambda: 777)
    monkeypatch.setattr(
        cap_mod, "restore_foreground",
        lambda target: restored.append(target))

    from qwen3_voice.ui.capsule import _CapsuleApi
    _CapsuleApi(cap).stop_recording()

    # 先归还前台(还给用户窗口),再停录音
    assert restored == [4321]
    assert cap.controller.stopped == 1


def test_stop_button_keeps_focus_when_user_switched(monkeypatch):
    """用户点完 ✓ 已经自己切去别的窗口 → 不硬拽回录音时的窗口。"""
    import types

    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_capsule()
    cap.controller = _FakeController()
    cap._target_hwnd = 4321
    cap._window.native = types.SimpleNamespace(
        Handle=types.SimpleNamespace(ToInt64=lambda: 777))

    restored = []
    # 前台既不是胶囊(9999 是用户切过去的新窗口)
    monkeypatch.setattr(cap_mod, "get_foreground_window", lambda: 9999)
    monkeypatch.setattr(
        cap_mod, "restore_foreground",
        lambda target: restored.append(target))

    from qwen3_voice.ui.capsule import _CapsuleApi
    _CapsuleApi(cap).stop_recording()

    assert restored == []            # 没动焦点
    assert cap.controller.stopped == 1


# ---------------- 窗口区域裁形(SetWindowRgn) ----------------
#
# 胶囊是**固定尺寸**的透明窗口,可见内容只有"文字条 + 胶囊"两块,
# 其余是透明空白 —— 但空白**属于窗口**,会吃掉底下应用的点击。
# 前端把两块内容的矩形上报上来,Python 侧裁掉空白区的命中区。

def _make_region_capsule() -> VoiceCapsule:
    """带一个假句柄的胶囊(裁区域要拿到 hwnd)。"""
    import types

    cap = _make_capsule()
    cap._window.native = types.SimpleNamespace(
        Handle=types.SimpleNamespace(ToInt64=lambda: 777))
    return cap


def _capture_regions(monkeypatch, scale: float = 1.75):
    """把 set_window_regions 换成假的,返回它收到的调用列表。"""
    import qwen3_voice.ui.capsule as cap_mod

    calls: list[tuple[int, list]] = []
    monkeypatch.setattr(
        cap_mod, "set_window_regions",
        lambda hwnd, rects: calls.append((hwnd, list(rects))) or True)
    monkeypatch.setattr(VoiceCapsule, "_scale", lambda self: scale)
    return calls


def test_update_regions_converts_logical_to_physical(monkeypatch):
    """前端报的是 CSS 逻辑像素,SetWindowRgn 要的是**物理**像素。

    本机 175% 缩放 → 逻辑 × 1.75,再每边外扩 8 物理像素的余量
    (取整误差 + 抗锯齿边缘,不留余量会把按钮切掉)。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_region_capsule()
    calls = _capture_regions(monkeypatch, scale=1.75)

    cap_mod._CapsuleApi(cap).update_regions({
        "textbar": [40, 20, 200, 40],
        "pill": [100, 120, 120, 40],
    })

    assert len(calls) == 1
    hwnd, rects = calls[0]
    assert hwnd == 777
    # 文字条:40×1.75=70 → 70-8=62 ; 20×1.75=35 → 35-8=27 ;
    #         (40+200)×1.75=420 → 420+8=428 ; (20+40)×1.75=105 → 105+8=113
    assert rects[0] == (62, 27, 428, 113)
    # 胶囊:100×1.75=175 → 167 ; 120×1.75=210 → 202 ;
    #       (100+120)×1.75=385 → 393 ; (120+40)×1.75=280 → 288
    assert rects[1] == (167, 202, 393, 288)


def test_hidden_block_is_excluded_from_the_region_union(monkeypatch):
    """全 0 表示那块隐藏 —— 不参与并集,也不能当成一块空白矩形。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_region_capsule()
    calls = _capture_regions(monkeypatch, scale=1.75)

    cap_mod._CapsuleApi(cap).update_regions({
        "textbar": [0, 0, 0, 0],
        "pill": [100, 120, 120, 40],
    })

    assert len(calls) == 1
    _, rects = calls[0]
    assert len(rects) == 1
    assert rects[0] == (167, 202, 393, 288)


def test_both_hidden_falls_back_to_minimal_region(monkeypatch):
    """两块都隐藏(正常不会发生):裁成 1×1 的最小区域。

    宁可"窗口点不到",也不要"整片空白继续挡着底下的应用"。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_region_capsule()
    calls = _capture_regions(monkeypatch, scale=1.75)

    cap_mod._CapsuleApi(cap).update_regions({
        "textbar": [0, 0, 0, 0],
        "pill": [0, 0, 0, 0],
    })

    assert len(calls) == 1
    _, rects = calls[0]
    assert rects == [(0, 0, 1, 1)]


def test_malformed_report_does_not_clip_the_window(monkeypatch):
    """前端报了个半截数据 —— 宁可这一帧不裁,也不能把窗口裁歪。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_region_capsule()
    calls = _capture_regions(monkeypatch)

    api = cap_mod._CapsuleApi(cap)
    api.update_regions({"textbar": [1, 2]})       # 少一维
    api.update_regions({"textbar": "不是矩形"})     # 类型不对
    api.update_regions("不是 payload")
    api.update_regions(None)

    assert calls == []


def test_window_is_not_clipped_before_the_first_report(monkeypatch):
    """前端第一次上报之前**不裁**(保持整窗)。

    那时还没量准,贸然裁会把可见内容切掉;而且此时窗口多半还是隐藏的,
    裁了也白裁。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_region_capsule()
    calls = _capture_regions(monkeypatch)
    # 把 _show 用到的 Win32 调用全换成假的,别真去操作窗口
    monkeypatch.setattr(cap_mod, "set_tool_window_style", lambda h: True)
    monkeypatch.setattr(cap_mod, "set_no_activate_tree", lambda h: 0)
    monkeypatch.setattr(cap_mod, "show_window_no_activate", lambda h: True)
    monkeypatch.setattr(cap_mod, "bring_to_top", lambda h: True)
    monkeypatch.setattr(cap_mod, "move_window_to", lambda *a: True)

    cap._show()

    assert calls == []


def test_throttle_merges_region_reports_and_keeps_the_last():
    """前端 60ms 内的多次变化要合并成一次,而且**最后一次不能丢**。

    这是纯 JS 逻辑(setTimeout 尾沿触发),Python 侧测不到 —— 用 node
    配一个最小 DOM 跑**真实的** capsule.js 来验。没装 node 就跳过。
    """
    import json
    import shutil
    import subprocess
    from pathlib import Path

    node = shutil.which("node")
    if not node:
        pytest.skip("没装 node,跳过前端节流测试")

    harness = Path(__file__).with_name("capsule_js_harness.js")
    steps = [
        {"op": "advance", "ms": 100},                    # 首帧:上报一次
        {"op": "call", "fn": "setPreview", "args": ["你好"]},
        {"op": "call", "fn": "setPreview", "args": ["你好世界你好世界"]},
        {"op": "advance", "ms": 100},                    # 合并成一次
    ]
    out = subprocess.run(
        [node, str(harness), json.dumps(steps, ensure_ascii=False)],
        capture_output=True, text=True, encoding="utf-8", timeout=60)
    assert out.returncode == 0, out.stderr
    records = json.loads(out.stdout)["calls"]

    reports = [c["args"][0] for c in records if c["name"] == "update_regions"]
    # 首帧一次 + 两次内容变化合并成一次
    assert len(reports) == 2
    # 第二次报的是**最后**那次预览(8 个字 → 宽 180),
    # 不是中间那次(2 个字 → 宽 120)—— 最后一次不能被节流掐掉
    assert reports[1]["textbar"][2] == 180
    assert reports[1]["pill"][2] == 180


# ---------------- 主题(三档:跟随系统 / 白天 / 黑夜) ----------------

class FakeConfig:
    """带配置字段的假配置。

    面板和胶囊只会读它、写它、存它 —— 够用了。
    """

    def __init__(self, theme="auto", capsule_position="follow",
                 show_preview=True, floating_orb=False, orb_position="follow"):
        self.theme = theme
        self.hotkey = "ctrl+alt+space"
        self.capsule_position = capsule_position
        self.show_preview = show_preview
        self.floating_orb = floating_orb
        self.orb_position = orb_position
        self.capsule_geometry = ""
        self.capsule_monitor = ""
        self.orb_geometry = ""
        self.orb_monitor = ""
        self.saves = 0

    def save(self):
        self.saves += 1


class ThemeSink:
    """记录收到过几次主题事件的推送目标(当胶囊用)。"""

    def __init__(self):
        self.themes: list[str] = []
        self.events: list[UIEvent] = []

    def handle_event(self, event):
        self.events.append(event)
        if event.kind == "theme":
            self.themes.append(event.text)

    def push_levels(self, levels, elapsed):
        pass


def _make_themed_panel() -> VoicePanel:
    """带 config 和推送线程的面板(不建真窗口)。"""
    panel = _make_panel()
    panel.controller = types.SimpleNamespace(config=FakeConfig())
    panel._pump = _EventPump(panel.controller)
    panel._pump.add_sink(panel)
    return panel


def test_set_theme_saves_config_and_pushes_to_both_windows():
    """面板改主题:存配置 → 推面板前端 → 推胶囊。"""
    panel = _make_themed_panel()
    sink = ThemeSink()
    panel._pump.add_sink(sink)

    panel._set_theme("light")

    assert panel.controller.config.theme == "light"
    assert panel.controller.config.saves == 1

    # 主题事件是**投进队列**的,由唯一的推送线程分发给所有窗口
    panel._pump._dispatch(panel._pump._events.get_nowait())
    assert "setTheme" in panel._window.methods()   # 面板前端
    assert sink.themes == ["light"]                # 胶囊


def test_set_theme_rejects_unknown_mode():
    """写坏的档位不能传下去 —— 按 auto 处理。"""
    panel = _make_themed_panel()
    sink = ThemeSink()
    panel._pump.add_sink(sink)

    panel._set_theme("solarized")

    assert panel.controller.config.theme == "auto"
    panel._pump._dispatch(panel._pump._events.get_nowait())
    assert sink.themes == ["auto"]


def test_capsule_applies_theme_event():
    """胶囊收到主题事件 → 推 setTheme 给前端。"""
    cap = _make_capsule()

    cap.handle_event(UIEvent(kind="theme", text="dark"))

    assert "setTheme" in cap._window.methods()


def test_capsule_pushes_theme_before_resetting_for_recording():
    """主题必须在 resetForRecording **之前**推到胶囊。

    面板改主题是异步的(走推送线程),用户改完马上按热键的话,
    setTheme 可能还堵在队列里 —— 显示前补推一次,配色才不会错。
    """
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig(theme="light"))

    cap.handle_event(UIEvent(kind="state", state=State.RECORDING))

    methods = cap._window.methods()
    assert "setTheme" in methods
    assert methods.index("setTheme") < methods.index("resetForRecording")


def test_capsule_frontend_ready_pushes_theme():
    """前端晚就绪时要把当前主题补推一次。"""
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig(theme="dark"))

    cap._on_frontend_ready()

    assert "setTheme" in cap._window.methods()


def test_theme_background_color_follows_the_mode(monkeypatch):
    """建窗底色:强制档用对应色,auto 档读系统。"""
    import qwen3_voice.ui.panel as panel_mod

    monkeypatch.setattr(panel_mod, "system_prefers_dark", lambda: True)
    assert panel_mod.theme_background_color("light") == panel_mod.WINDOW_BG_LIGHT
    assert panel_mod.theme_background_color("dark") == panel_mod.WINDOW_BG_DARK
    assert panel_mod.theme_background_color("auto") == panel_mod.WINDOW_BG_DARK

    monkeypatch.setattr(panel_mod, "system_prefers_dark", lambda: False)
    assert panel_mod.theme_background_color("auto") == panel_mod.WINDOW_BG_LIGHT


# ---------------- 胶囊位置模式 + 预览开关 ----------------

def test_set_capsule_position_saves_config_and_pushes_to_both_windows():
    """面板改位置模式:存配置 → 推面板前端 → 推胶囊。"""
    panel = _make_themed_panel()
    sink = ThemeSink()
    panel._pump.add_sink(sink)

    panel._set_capsule_position("custom")

    assert panel.controller.config.capsule_position == "custom"
    assert panel.controller.config.saves == 1

    panel._pump._dispatch(panel._pump._events.get_nowait())
    assert "setCapsulePosition" in panel._window.methods()   # 面板前端
    assert sink.events[-1].kind == "capsule_position"         # 胶囊


def test_set_capsule_position_rejects_unknown_mode():
    """写坏的档位不能传下去 —— 按 follow 处理。"""
    panel = _make_themed_panel()
    sink = ThemeSink()
    panel._pump.add_sink(sink)

    panel._set_capsule_position("solarized")

    assert panel.controller.config.capsule_position == "follow"
    panel._pump._dispatch(panel._pump._events.get_nowait())
    assert sink.events[-1].text == "follow"


def test_set_preview_saves_config_and_pushes_to_both_windows():
    """面板关预览:存配置 → 推面板前端 → 推胶囊。"""
    panel = _make_themed_panel()
    sink = ThemeSink()
    panel._pump.add_sink(sink)

    panel._set_preview(False)

    assert panel.controller.config.show_preview is False
    assert panel.controller.config.saves == 1

    panel._pump._dispatch(panel._pump._events.get_nowait())
    assert "setPreviewEnabled" in panel._window.methods()
    assert sink.events[-1].kind == "preview_enabled"
    assert sink.events[-1].text == "0"


def test_capsule_applies_position_event():
    """胶囊收到位置模式事件 → 推 setPositionMode 给前端。"""
    cap = _make_capsule()

    cap.handle_event(UIEvent(kind="capsule_position", text="custom"))

    assert "setPositionMode" in cap._window.methods()


def test_capsule_applies_preview_event():
    """胶囊收到预览开关事件 → 推 setPreviewEnabled 给前端。"""
    cap = _make_capsule()

    cap.handle_event(UIEvent(kind="preview_enabled", text="0"))

    assert "setPreviewEnabled" in cap._window.methods()


def test_capsule_pushes_preview_flag_before_resetting_for_recording():
    """预览开关必须在 resetForRecording **之前**推到胶囊。

    面板改开关是异步的(走推送线程),用户改完马上按热键的话,
    setPreviewEnabled 可能还堵在队列里 —— 显示前补推一次,
    文字条才不会闪一下才消失。
    """
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig(show_preview=False))

    cap.handle_event(UIEvent(kind="state", state=State.RECORDING))

    methods = cap._window.methods()
    assert "setPreviewEnabled" in methods
    assert methods.index("setPreviewEnabled") < methods.index("resetForRecording")


def test_capsule_skips_preview_text_when_preview_is_off():
    """预览关着:一个字都不往前端送(文字条整块不出现)。"""
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig(show_preview=False))

    cap.handle_event(UIEvent(kind="preview", text="你好"))

    assert "setPreview" not in cap._window.methods()


def test_capsule_still_pushes_preview_text_when_preview_is_on():
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig(show_preview=True))

    cap.handle_event(UIEvent(kind="preview", text="你好"))

    assert "setPreview" in cap._window.methods()


# ---------------- 胶囊拖动(自定义位置) ----------------

def _drag_capsule(monkeypatch, mode="custom"):
    """造一个能拖的胶囊,并把摆位相关的 Win32 调用换成假的。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(
        config=FakeConfig(capsule_position=mode))
    cap._window.native = types.SimpleNamespace(
        Handle=types.SimpleNamespace(ToInt64=lambda: 777))
    return cap, cap_mod


def test_drag_moves_the_window_to_an_absolute_position(monkeypatch):
    """拖动发的是**绝对位置**(逻辑像素),不是增量。"""
    cap, cap_mod = _drag_capsule(monkeypatch)
    moves: list[tuple] = []
    monkeypatch.setattr(cap_mod, "move_window_to",
                        lambda hwnd, x, y: moves.append((hwnd, x, y)) or True)
    monkeypatch.setattr(VoiceCapsule, "_current_scale", lambda self: 1.75)

    cap_mod._CapsuleApi(cap).move_capsule(100, 200)

    assert moves == [(777, 175, 350)]     # 逻辑 × 缩放 = 物理


def test_drag_end_saves_the_position_in_custom_mode(monkeypatch):
    """自定义模式:松手后把位置记下来(相对工作区的逻辑偏移)。"""
    cap, cap_mod = _drag_capsule(monkeypatch)
    monkeypatch.setattr(cap_mod, "get_window_rect",
                        lambda hwnd: (100, 200, 612, 460))
    monkeypatch.setattr(VoiceCapsule, "_monitor_at_window",
                        lambda self: types.SimpleNamespace(
                            work_area=(0, 0, 1920, 1040),
                            scale=1.0,
                            device="\\\\.\\DISPLAY1"))

    cap_mod._CapsuleApi(cap).end_drag()

    assert cap.controller.config.capsule_geometry == "100,200"
    assert cap.controller.config.capsule_monitor == "\\\\.\\DISPLAY1"
    assert cap.controller.config.saves == 1


def test_drag_end_does_not_save_position_outside_custom_mode(monkeypatch):
    """跟随/固定底部模式下拖了也不记 —— 位置不归拖拽说了算。"""
    cap, cap_mod = _drag_capsule(monkeypatch, mode="follow")
    monkeypatch.setattr(cap_mod, "get_window_rect",
                        lambda hwnd: (100, 200, 612, 460))

    cap_mod._CapsuleApi(cap).end_drag()

    assert cap.controller.config.capsule_geometry == ""
    assert cap.controller.config.saves == 0


def test_drag_flags_are_visible_to_the_focus_guard(monkeypatch):
    """拖动期间守卫不抢焦点,松手后恢复 —— 拖到一半不会被抢断。"""
    cap, cap_mod = _drag_capsule(monkeypatch)

    cap_mod._CapsuleApi(cap).begin_drag()
    assert cap._is_dragging() is True

    cap_mod._CapsuleApi(cap).end_drag()
    assert cap._is_dragging() is False


# ---------------- 前端主题逻辑(node 跑真实 JS) ----------------

def _run_js_harness(harness_name: str, steps: list[dict]) -> dict:
    """跑一个前端 harness,返回它吐的 JSON。没装 node 就跳过。"""
    import json
    import shutil
    import subprocess
    from pathlib import Path

    node = shutil.which("node")
    if not node:
        pytest.skip("没装 node,跳过前端测试")

    harness = Path(__file__).with_name(harness_name)
    out = subprocess.run(
        [node, str(harness), json.dumps(steps, ensure_ascii=False)],
        capture_output=True, text=True, encoding="utf-8", timeout=60)
    assert out.returncode == 0, out.stderr
    return json.loads(out.stdout)


@pytest.mark.parametrize("mode", ["light", "dark"])
def test_panel_frontend_sets_theme_attribute(mode):
    """面板:setTheme('light'/'dark') → <html> 上设 data-theme,当前档高亮。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setTheme", "args": [mode]}])

    assert data["html"].get("data-theme") == mode
    assert data["active"]["theme"] == mode


def test_panel_frontend_auto_removes_theme_attribute():
    """auto 要**移除**属性 —— 这样 CSS 的媒体查询才能接管(跟随系统)。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setTheme", "args": ["auto"]}])

    assert "data-theme" not in data["html"]
    assert data["active"]["theme"] == "auto"


def test_panel_theme_click_notifies_python():
    """点主题段的某一档:本地立刻换 + 通知 Python 存档。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "click", "group": "themeSeg", "index": 1}])

    assert data["html"].get("data-theme") == "light"
    assert data["active"]["theme"] == "light"
    themes = [c for c in data["calls"] if c["name"] == "set_theme"]
    assert themes and themes[0]["args"] == ["light"]


@pytest.mark.parametrize("mode", ["light", "dark"])
def test_capsule_frontend_sets_theme_attribute(mode):
    """胶囊:和面板同一套机制 —— setTheme 设 data-theme。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setTheme", "args": [mode]}])

    assert data["html"].get("data-theme") == mode


def test_capsule_frontend_auto_removes_theme_attribute():
    """auto:属性移除,交给 CSS 媒体查询。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setTheme", "args": ["auto"]}])

    assert "data-theme" not in data["html"]


# ---------------- 胶囊的滚动电平条(node 跑真实 JS) ----------------
#
# 规格来自 MiniMax Code 的 asr-volume-bars(旧面板时代就是这么做的,
# 见 a1a3ea2 的 panel.js):13 根定宽小条、每 80ms 推进一根、从右往左滚、
# 三槽轮转(过渡 / 当前 / 衰减)、高度按**绝对电平**映射。

BAR_COUNT = 13          # 可见条数(MCode 原版)
BAR_POOL = BAR_COUNT + 1   # 多一根是屏外的回收位
BAR_TICK_MS = 80        # 推进周期
BAR_STEP_PX = 6         # 步进 = 条宽 3px + 间隙 3px


def _bar_px(bar: dict) -> float:
    """把 "18.80px" 变成 18.8。"""
    return float(str(bar["height"]).replace("px", ""))


def _capsule_wave(steps: list[dict]) -> list[dict]:
    """跑胶囊前端,返回波形区的条(按 age 升序,age=0 是最新、最靠右)。"""
    data = _run_js_harness("capsule_js_harness.js", steps)
    return data["wave"]


def test_capsule_wave_has_thirteen_bars_plus_one_recycler():
    """可视 13 根,另加 1 根屏外的回收位(复用同一个 DOM 节点当新条)。"""
    wave = _capsule_wave([{"op": "advance", "ms": 10}])

    assert len(wave) == BAR_POOL
    # age 从 0(最新、最右)到 13(最老、最左,已经滑出视口)
    assert [b["age"] for b in wave] == list(range(BAR_POOL))


def test_capsule_wave_advances_one_bar_every_80ms():
    """每 80ms 推进一根 —— 被 tick 改写过的条数正好等于推进次数。"""
    wave = _capsule_wave([
        {"op": "call", "fn": "setLevels", "args": ["[0.8]"]},
        {"op": "advance", "ms": BAR_TICK_MS * 3},
    ])

    touched = [b for b in wave if b["touched"]]
    assert len(touched) == 3
    # 改写过的正好是最新的三根
    assert [b["age"] for b in touched] == [0, 1, 2]


def test_capsule_wave_scrolls_from_right_to_left():
    """滚动方向:新条从右边进,旧条往左滑(offset 越来越负)。"""
    wave = _capsule_wave([
        {"op": "call", "fn": "setLevels", "args": ["[0.8]"]},
        {"op": "advance", "ms": BAR_TICK_MS * 4},
    ])

    # age 越大越靠左;步进 6px
    assert [b["offset"] for b in wave] == [-i * BAR_STEP_PX for i in range(BAR_POOL)]
    # 最新的一根贴在右边缘(胶囊里它是"刚进来的那根")
    assert wave[0]["offset"] == 0


def test_capsule_wave_uses_three_slot_rotation():
    """三槽轮转:过渡 / 当前 / 衰减轮流用,相邻条天然错开。

    电平恒定时,三根一组地重复:"当前"=电平对应的高度,"衰减"更低,
    "过渡"夹在两者之间 —— 这就是"流动感"的来源。
    """
    wave = _capsule_wave([
        {"op": "call", "fn": "setLevels", "args": ["[0.8]"]},
        {"op": "advance", "ms": BAR_TICK_MS * 9},
    ])
    # 只看被 tick 改写过的条(预填的条还是 2px 的静音基线)
    live = [b for b in wave if b["touched"]]

    cur = [_bar_px(b) for b in live if b["age"] % 3 == 1]     # 当前槽
    decay = [_bar_px(b) for b in live if b["age"] % 3 == 0]    # 衰减槽
    trans = [_bar_px(b) for b in live if b["age"] % 3 == 2]    # 过渡槽

    assert len(cur) == 3 and len(decay) == 3
    # 同一槽的几根高度一致(电平恒定)
    assert len(set(cur)) == 1
    assert len(set(decay)) == 1
    # 过渡槽:前两根已经收敛到同一个值(最老那根是第一拍,还没收敛)
    assert len(set(trans[:2])) == 1
    # 衰减 < 过渡 < 当前
    assert decay[0] < trans[0] < cur[0]


def test_capsule_wave_bar_transition_is_interpolated():
    """过渡插值:过渡槽的高度 = 衰减 + 65% × (当前 − 衰减),不是硬跳。"""
    wave = _capsule_wave([
        {"op": "call", "fn": "setLevels", "args": ["[0.8]"]},
        {"op": "advance", "ms": BAR_TICK_MS * 9},
    ])

    cur = _bar_px(next(b for b in wave if b["age"] == 1))
    decay = _bar_px(next(b for b in wave if b["age"] == 0))
    trans = _bar_px(next(b for b in wave if b["age"] == 2))

    assert trans == pytest.approx(decay + 0.65 * (cur - decay), abs=0.02)


def test_capsule_wave_uses_absolute_level_not_window_max():
    """条高是**绝对电平** —— 前端绝不按窗口内最大值归一化。

    喂一串最大值只有 0.2 的电平:按窗口内归一化的话条子会顶到 20px,
    按绝对电平则只有十来像素。
    """
    quiet = _capsule_wave([
        {"op": "call", "fn": "setLevels", "args": ["[0.1, 0.2]"]},
        {"op": "advance", "ms": BAR_TICK_MS * 9},
    ])
    loud = _capsule_wave([
        {"op": "call", "fn": "setLevels", "args": ["[0.1, 0.9]"]},
        {"op": "advance", "ms": BAR_TICK_MS * 9},
    ])

    quiet_h = _bar_px(next(b for b in quiet if b["age"] == 1))
    loud_h = _bar_px(next(b for b in loud if b["age"] == 1))

    # 归一化的话两串都会顶到 20px(MAX_HEIGHT)
    assert quiet_h < 14
    assert loud_h > 15
    assert loud_h > quiet_h


def test_capsule_wave_stops_when_recording_ends():
    """离开录音阶段:条子不再推进(胶囊里换状态文字)。"""
    wave = _capsule_wave([
        {"op": "call", "fn": "setLevels", "args": ["[0.8]"]},
        {"op": "advance", "ms": BAR_TICK_MS * 2},
        {"op": "call", "fn": "setResult", "args": ["识别完了"]},
        {"op": "advance", "ms": BAR_TICK_MS * 5},
    ])

    # 停了之后一根都没再被改写
    assert len([b for b in wave if b["touched"]]) == 2


# ---------------- 面板前端:位置档位 + 预览开关 ----------------

def test_panel_position_click_notifies_python():
    """点「自定义」档:本地立刻高亮 + 通知 Python 存档。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "click", "group": "positionSeg", "index": 2}])

    assert data["active"]["position"] == "custom"
    positions = [c for c in data["calls"] if c["name"] == "set_capsule_position"]
    assert positions and positions[0]["args"] == ["custom"]


@pytest.mark.parametrize("mode", ["follow", "bottom", "custom"])
def test_panel_position_highlight_follows_python(mode):
    """Python 推 setCapsulePosition → 高亮切到对应档。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setCapsulePosition", "args": [mode]}])

    assert data["active"]["position"] == mode


def test_panel_preview_toggle_notifies_python():
    """拨预览开关:本地立刻生效 + 通知 Python 存档。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "toggle", "id": "previewToggle", "checked": False}])

    assert data["preview"] is False
    previews = [c for c in data["calls"] if c["name"] == "set_preview"]
    assert previews and previews[0]["args"] == [False]


def test_panel_preview_switch_follows_python():
    """Python 推 setPreviewEnabled(false) → 开关拨到关。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setPreviewEnabled", "args": [False]}])

    assert data["preview"] is False


# ---------------- 胶囊前端:预览显隐 ----------------

def test_capsule_preview_off_hides_the_textbar():
    """预览关掉:body 上加 no-preview(文字条整块不出现)。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPreviewEnabled", "args": [False]}])

    assert "no-preview" in data["body"]


def test_capsule_preview_on_keeps_the_textbar():
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPreviewEnabled", "args": [True]}])

    assert "no-preview" not in data["body"]


def test_capsule_result_with_preview_off_shows_only_the_label():
    """预览关着:最终结果**不进文字条**,胶囊里显示「已输入」。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPreviewEnabled", "args": [False]},
        {"op": "call", "fn": "setResult", "args": ["你好世界"]},
    ])

    assert data["textbar"] == ""
    assert data["label"] == "已输入"


def test_capsule_error_with_preview_off_goes_to_the_label():
    """预览关着:错误文案进胶囊的 label 位(一行截断)。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPreviewEnabled", "args": [False]},
        {"op": "call", "fn": "setError", "args": ["没听清,请再说一遍"]},
    ])

    assert data["textbar"] == ""
    assert data["label"] == "没听清,请再说一遍"


def test_capsule_preview_with_preview_on_still_writes_the_textbar():
    """预览开着:老样子,文字条照常显示。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPreview", "args": ["你好"]},
    ])

    assert data["textbar"] == "你好"


# ---------------- 胶囊前端:拖动(自定义位置) ----------------

def test_capsule_drag_sends_absolute_position():
    """拖动发的是**绝对位置**:起点窗口位置 + 鼠标位移。

    绝对目标天然不漂 —— 某一次 IPC 丢了,下一次直接覆盖。
    """
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPositionMode", "args": ["custom"]},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mousemove", "screenX": 150, "screenY": 260},
        {"op": "advance", "ms": 20},
        {"op": "mouse", "type": "mouseup", "screenX": 150, "screenY": 260},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "begin_drag" in names
    assert "end_drag" in names
    moves = [c for c in data["calls"] if c["name"] == "move_capsule"]
    assert moves and moves[0]["args"] == [50, 60]     # 起点(0,0) + 位移(50,60)


def test_capsule_drag_is_disabled_outside_custom_mode():
    """跟随/固定底部模式下胶囊不拖 —— 拖了也没意义。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPositionMode", "args": ["follow"]},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mousemove", "screenX": 150, "screenY": 260},
        {"op": "advance", "ms": 20},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "begin_drag" not in names
    assert "move_capsule" not in names


def test_capsule_drag_ignores_the_buttons():
    """点在 ✕ / ✓ 上不触发拖动 —— 那是按钮,不是把手。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPositionMode", "args": ["custom"]},
        {"op": "mouse", "target": "okBtn", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mousemove", "screenX": 150, "screenY": 260},
        {"op": "advance", "ms": 20},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "begin_drag" not in names
    assert "move_capsule" not in names


def test_capsule_position_mode_marks_the_window_draggable():
    """自定义模式 → body 加 can-drag(鼠标指针变成 move)。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setPositionMode", "args": ["custom"]}])

    assert "can-drag" in data["body"]


# ---------------- 注入失败的出口:原文 + 「复制」按钮 ----------------


def test_capsule_error_passes_payload_to_frontend():
    """错误事件带的原文要送到前端 —— 前端要靠它显示全文、亮出复制按钮。"""
    cap = _make_capsule()

    cap.handle_event(UIEvent(kind="error", text="文本注入失败",
                             payload="识别原文"))

    assert "setError" in cap._window.methods()
    assert "识别原文" in "\n".join(cap._window.scripts)


def test_capsule_error_with_payload_waits_longer(monkeypatch):
    """带原文的错误要多留一会儿 —— 用户得有时间点「复制」。"""
    import qwen3_voice.ui.capsule as cap_mod

    delays = []
    cap = _make_capsule()
    monkeypatch.setattr(cap, "_schedule_hide", delays.append)

    cap.handle_event(UIEvent(kind="error", text="文本注入失败",
                             payload="识别原文"))
    cap.handle_event(UIEvent(kind="error", text="没听清"))

    assert delays[0] > delays[1]


def test_copy_result_writes_clipboard_and_hides_capsule(monkeypatch):
    """「复制」按钮:写进剪贴板并**读回校验**,确认写进去了才收胶囊。

    用户要的是"确认"—— 确认文字真的进了剪贴板,而不是我们试了一下。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_capsule()
    cap._want_visible = True
    written = []

    def fake_set(text):
        written.append(text)
        return True

    monkeypatch.setattr(cap_mod.clipboard, "set_text", fake_set)
    monkeypatch.setattr(cap_mod.clipboard, "get_text", lambda: "识别原文")

    ok = cap_mod._CapsuleApi(cap).copy_result("识别原文")

    assert ok is True
    assert written == ["识别原文"]
    assert cap._want_visible is False      # 拿到了,胶囊就收掉


def test_copy_result_keeps_capsule_when_verify_fails(monkeypatch):
    """读回来不是同一份 → 当作失败,胶囊留在原地让用户再试。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_capsule()
    cap._want_visible = True

    monkeypatch.setattr(cap_mod.clipboard, "set_text", lambda text: True)
    monkeypatch.setattr(cap_mod.clipboard, "get_text", lambda: "别的东西")

    ok = cap_mod._CapsuleApi(cap).copy_result("识别原文")

    assert ok is False
    assert cap._want_visible is True       # 没拿到,胶囊不收


def test_copy_result_rejects_empty_text(monkeypatch):
    """空文本没意义 —— 直接拒绝,别去动用户的剪贴板。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_capsule()
    called = []
    monkeypatch.setattr(cap_mod.clipboard, "set_text",
                        lambda text: called.append(text) or True)

    assert cap_mod._CapsuleApi(cap).copy_result("   ") is False
    assert called == []


# ---------------- 前端:错误态的原文与复制按钮(node 跑真实 JS) ----------------


def test_capsule_error_shows_text_and_enables_copy():
    """带原文的错误:文字条显示**识别原文**,胶囊上出现「复制」按钮。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setError",
         "args": ["文本注入失败", "刚说出口的话"]},
    ])

    assert data["textbar"] == "刚说出口的话"
    assert "can-copy" in data["pill"]
    assert "error" in data["pill"]


def test_capsule_error_without_payload_has_no_copy_button():
    """没有原文的错误(没听清)不给复制按钮 —— 没东西可复制。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setError", "args": ["没听清,请再说一遍"]},
    ])

    assert data["textbar"] == "没听清,请再说一遍"
    assert "can-copy" not in data["pill"]


def test_capsule_copy_button_sends_the_text_to_python():
    """点「复制」→ 把识别原文发给 Python。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setError",
         "args": ["文本注入失败", "刚说出口的话"]},
        {"op": "mouse", "target": "copyBtn", "type": "click"},
    ])

    copies = [c for c in data["calls"] if c["name"] == "copy_result"]
    assert copies and copies[0]["args"] == ["刚说出口的话"]


def test_capsule_copy_button_reports_failure_to_user():
    """Python 说没写进去 → 胶囊不能装作成功,要让用户再试一次。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setError",
         "args": ["文本注入失败", "刚说出口的话"]},
        {"op": "setCopyResult", "value": False},
        {"op": "mouse", "target": "copyBtn", "type": "click"},
    ])

    assert "复制失败" in data["label"]
    # 没拿到东西,按钮还得留着
    assert "can-copy" in data["pill"]


def test_capsule_copy_button_clears_state_on_success():
    """写成功了 → 前端把原文和按钮清干净(胶囊随后被 Python 收掉)。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setError",
         "args": ["文本注入失败", "刚说出口的话"]},
        {"op": "setCopyResult", "value": True},
        {"op": "mouse", "target": "copyBtn", "type": "click"},
    ])

    assert "can-copy" not in data["pill"]


def test_capsule_new_recording_clears_the_copy_state():
    """新一轮录音 → 上一轮错误态留下的原文和按钮要清干净。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setError",
         "args": ["文本注入失败", "刚说出口的话"]},
        {"op": "call", "fn": "resetForRecording", "args": []},
    ])

    assert "can-copy" not in data["pill"]
    assert "recording" in data["pill"]
    # 上一轮的原文被清空了(只剩占位符)
    assert data["textbar"] == "正在聆听…"


# ---------------- 常驻悬浮球 ----------------
#
# 悬浮球和胶囊是**同一个窗口**的两种形态。这一组盯的是:
#   - 焦点陷阱:点球开始录音时必须先把前台还给用户
#   - 位置记忆:拖球松手记住的是**球**的位置
#   - 点击/拖动的分界(前端那部分在下面的 node harness 里验)


def _orb_capsule(orb_position="follow"):
    """开着常驻球模式、带一个假句柄的胶囊。"""
    cap = _make_region_capsule()
    cap.controller = types.SimpleNamespace(
        config=FakeConfig(floating_orb=True, orb_position=orb_position))
    return cap


def test_orb_mode_is_off_by_default():
    """默认关:老用户看到的还是"按热键才弹胶囊"。"""
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig())

    assert cap._orb_enabled() is False


def test_frontend_ready_pushes_the_orb_mode():
    """前端晚就绪时要补推一次常驻球模式。"""
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig(floating_orb=True))

    cap._on_frontend_ready()

    assert "setOrbMode" in cap._window.methods()


def test_orb_click_restores_the_focus_before_recording(monkeypatch):
    """点悬浮球开始录音:**先**把前台还给用户,再开始录音。

    这是焦点陷阱:鼠标点在 WebView2 的子窗口上会激活整棵窗口树,
    前台被胶囊抢走。注入靠"粘贴到前台窗口",不还回去的话,这一轮
    识别完的文字会贴到胶囊自己身上,而不是用户的输入框里。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _orb_capsule()
    ctrl = _FakeController()
    cap.controller = ctrl
    cap._last_foreground = 4321        # 用户原来所在的窗口

    restored = []
    monkeypatch.setattr(cap_mod, "get_foreground_window", lambda: 777)
    monkeypatch.setattr(cap_mod, "restore_foreground",
                        lambda target: restored.append(target))
    # 记下"开始录音"发生时,前台已经归还了几次
    order = []
    original_start = ctrl.start_recording

    def start():
        order.append(("start", len(restored)))
        original_start()

    monkeypatch.setattr(ctrl, "start_recording", start)

    cap_mod._CapsuleApi(cap).start_recording()

    assert restored == [4321]
    # 归还焦点在前,开始录音在后
    assert order == [("start", 1)]
    assert ctrl.started == 1
    # 注入落点也记下了 —— 文字要贴到用户那个窗口
    assert cap._target_hwnd == 4321


def test_orb_drag_end_saves_the_orb_position(monkeypatch):
    """拖球松手:记住**球**的位置(相对工作区的逻辑偏移 + 显示器设备名)。

    只有**自定义档**才记 —— 跟随档下球的位置由光标决定,轮询线程
    一直在挪窗口,那个"挪过"不是用户拖的。
    """
    import qwen3_voice.ui.capsule as cap_mod

    cap = _orb_capsule(orb_position="custom")
    # 窗口摆在 (100, 200),物理 512×260,缩放 1.0
    monkeypatch.setattr(cap_mod, "get_window_rect",
                        lambda hwnd: (100, 200, 612, 460))
    monkeypatch.setattr(VoiceCapsule, "_monitor_at_window",
                        lambda self: types.SimpleNamespace(
                            work_area=(0, 0, 1920, 1040),
                            scale=1.0,
                            device="\\\\.\\DISPLAY1"))

    cap_mod._CapsuleApi(cap).end_drag()

    # 球在窗口的底边居中处:左边 = 100 + (512-40)//2 = 336,
    #                     下边 = 460 - (10 + 40) = 410
    assert cap.controller.config.orb_geometry == "336,410"
    assert cap.controller.config.orb_monitor == "\\\\.\\DISPLAY1"
    assert cap.controller.config.saves == 1


def test_orb_click_without_movement_does_not_save_the_position(monkeypatch):
    """点一下(没拖动)不该写配置 —— 位置没变,存了也是白存。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _orb_capsule()
    monkeypatch.setattr(cap_mod, "get_window_rect",
                        lambda hwnd: (100, 200, 612, 460))

    cap_mod._CapsuleApi(cap).begin_drag()
    cap_mod._CapsuleApi(cap).end_drag()

    assert cap.controller.config.orb_geometry == ""
    assert cap.controller.config.saves == 0


def test_orb_drag_that_moved_saves_the_position(monkeypatch):
    """真的挪过了才存位置。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _orb_capsule(orb_position="custom")
    rect = [(100, 200, 612, 460)]
    monkeypatch.setattr(cap_mod, "get_window_rect", lambda hwnd: rect[0])
    monkeypatch.setattr(VoiceCapsule, "_monitor_at_window",
                        lambda self: types.SimpleNamespace(
                            work_area=(0, 0, 1920, 1040),
                            scale=1.0,
                            device="\\\\.\\DISPLAY1"))

    cap_mod._CapsuleApi(cap).begin_drag()      # 起手位置
    rect[0] = (150, 260, 662, 520)             # 窗口被挪走了
    cap_mod._CapsuleApi(cap).end_drag()

    assert cap.controller.config.orb_geometry != ""
    assert cap.controller.config.saves == 1


def test_orb_hide_keeps_the_window_visible(monkeypatch):
    """常驻模式:一轮结束后胶囊收成球,窗口**不藏**(球还在等着)。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _orb_capsule()
    cap._want_visible = True
    hidden = []
    monkeypatch.setattr(cap_mod, "hide_window",
                        lambda hwnd: hidden.append(hwnd) or True)

    cap._hide()

    assert cap._want_visible is False
    assert hidden == []            # 窗口没被藏


def test_capsule_hide_hides_the_window_outside_orb_mode(monkeypatch):
    """非常驻模式:老规矩 —— 一轮结束就藏窗口。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_region_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig())
    hidden = []
    monkeypatch.setattr(cap_mod, "hide_window",
                        lambda hwnd: hidden.append(hwnd) or True)

    cap._hide()

    assert hidden == [777]


def test_orb_toggle_event_shows_and_hides_the_ball(monkeypatch):
    """面板拨开关 → 胶囊把球显示出来 / 收回去。"""
    import qwen3_voice.ui.capsule as cap_mod

    cap = _make_region_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig())
    shown = []
    monkeypatch.setattr(VoiceCapsule, "_show_orb",
                        lambda self: shown.append("show") or True)
    monkeypatch.setattr(VoiceCapsule, "_hide_orb",
                        lambda self: shown.append("hide") or True)

    cap.handle_event(UIEvent(kind="orb_enabled", text="1"))
    cap.handle_event(UIEvent(kind="orb_enabled", text="0"))

    assert shown == ["show", "hide"]
    assert "setOrbMode" in cap._window.methods()


def test_hide_delay_is_pushed_only_in_orb_mode():
    """常驻模式:前端自己收起胶囊,所以 Python 要把"停多久"告诉它。"""
    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig())

    cap.handle_event(UIEvent(kind="result", text="你好"))
    assert "setHideDelay" not in cap._window.methods()

    cap = _make_capsule()
    cap.controller = types.SimpleNamespace(config=FakeConfig(floating_orb=True))
    cap.handle_event(UIEvent(kind="result", text="你好"))
    assert "setHideDelay" in cap._window.methods()


# ---------------- 常驻悬浮球:前端(node 跑真实 JS) ----------------


def test_capsule_orb_click_starts_recording():
    """点球(位移在阈值内)→ 开始录音,不动位置。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mousemove", "screenX": 102, "screenY": 201},
        {"op": "mouse", "type": "mouseup", "screenX": 102, "screenY": 201},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "start_recording" in names
    assert "move_orb" not in names


def test_capsule_orb_drag_moves_without_recording():
    """拖球(超过阈值)→ 挪位置,不开始录音。

    跟随档下球**拖不动**(位置由光标决定),所以要先切到自定义档。
    """
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setOrbPosition", "args": ["custom"]},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mousemove", "screenX": 150, "screenY": 260},
        {"op": "mouse", "type": "mouseup", "screenX": 150, "screenY": 260},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "move_orb" in names
    assert "start_recording" not in names
    moves = [c for c in data["calls"] if c["name"] == "move_orb"]
    # 绝对目标位置:起点窗口位置(0,0) + 鼠标位移
    assert moves[0]["args"] == [50, 60]


def test_capsule_orb_drag_threshold_is_five_pixels():
    """阈值:位移 5px 以内算点击,超过 5px 算拖动。"""
    click = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setOrbPosition", "args": ["custom"]},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mousemove", "screenX": 105, "screenY": 200},
        {"op": "mouse", "type": "mouseup", "screenX": 105, "screenY": 200},
    ])
    names = [c["name"] for c in click["calls"]]
    assert "start_recording" in names
    assert "move_orb" not in names

    drag = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setOrbPosition", "args": ["custom"]},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mousemove", "screenX": 106, "screenY": 200},
        {"op": "mouse", "type": "mouseup", "screenX": 106, "screenY": 200},
    ])
    names = [c["name"] for c in drag["calls"]]
    assert "move_orb" in names
    assert "start_recording" not in names


def test_capsule_orb_follow_mode_is_not_draggable():
    """跟随档:球**拖不动** —— 位置由光标决定,拖了也会被拉回去。

    但**点击**仍然要能开始录音 —— 球就是录音入口,这个不能砍。
    """
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setOrbPosition", "args": ["follow"]},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mousemove", "screenX": 150, "screenY": 260},
        {"op": "mouse", "type": "mouseup", "screenX": 150, "screenY": 260},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "move_orb" not in names, "跟随档下不该挪球"
    assert "start_recording" not in names, "挪了就不该当成点击"


def test_capsule_orb_follow_mode_click_starts_recording():
    """跟随档:点一下(没拖)照样开始录音。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setOrbPosition", "args": ["follow"]},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mouseup", "screenX": 100, "screenY": 200},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "start_recording" in names


def test_capsule_orb_ignores_clicks_when_the_mode_is_off():
    """常驻模式关着:球不可点,更不该触发录音。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mouseup", "screenX": 100, "screenY": 200},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "start_recording" not in names


def test_capsule_orb_expanded_pill_does_not_start_a_new_recording():
    """展开成胶囊后再点它:不能又开一轮录音,也不该按下就当拖拽。

    展开态的胶囊不是拖拽把手(位置归球)。要是按下就调 begin_drag,
    守卫在按住期间**不归还前台** —— 按住不放的话前台就留在胶囊上,
    用户的键盘输入会打到胶囊里(功能级问题,不是观感问题)。
    """
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setState", "args": ["recording"]},
        {"op": "advance", "ms": 100},
        {"op": "mouse", "target": "pill", "type": "mousedown",
         "screenX": 100, "screenY": 200},
        {"op": "mouse", "type": "mouseup", "screenX": 100, "screenY": 200},
    ])

    names = [c["name"] for c in data["calls"]]
    assert "start_recording" not in names
    assert "begin_drag" not in names


def test_capsule_orb_expands_into_the_pill_when_recording():
    """常驻模式:开始录音 → 球展开成胶囊。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "resetForRecording", "args": []},
    ])

    assert "orb-mode" in data["body"]
    assert "orb-expanded" in data["body"]
    assert "orb-idle" not in data["body"]


def test_capsule_orb_collapses_back_to_the_ball_after_the_hide_delay():
    """一轮结束 → 停够时间 → 收回成球(继续常驻等下一轮)。"""
    steps = [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setHideDelay", "args": [1500]},
        {"op": "call", "fn": "resetForRecording", "args": []},
        {"op": "call", "fn": "setResult", "args": ["你好"]},
    ]

    still = _run_js_harness("capsule_js_harness.js",
                            steps + [{"op": "advance", "ms": 1000}])
    assert "orb-expanded" in still["body"]

    done = _run_js_harness("capsule_js_harness.js",
                           steps + [{"op": "advance", "ms": 2000}])
    assert "orb-idle" in done["body"]
    assert "orb-expanded" not in done["body"]


def test_capsule_orb_reports_only_the_ball_as_the_hit_region():
    """常驻模式待机:命中区只有球那一小块 —— 大片空白不能吃底下的点击。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "advance", "ms": 100},
    ])

    reports = [c["args"][0] for c in data["calls"] if c["name"] == "update_regions"]
    assert reports
    last = reports[-1]
    # 球**就是**收窄的胶囊:命中区报的是那颗 40×40 的球
    # (位置和展开态的胶囊同轴 —— 都在窗口底边居中)
    assert last["pill"] == [170, 120, 40, 40]
    assert last["textbar"][2] == 0


def test_capsule_orb_reports_the_pill_when_expanded():
    """展开成胶囊后:命中区回到"文字条 + 胶囊"。"""
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "resetForRecording", "args": []},
        {"op": "advance", "ms": 100},
    ])

    reports = [c["args"][0] for c in data["calls"] if c["name"] == "update_regions"]
    assert reports
    last = reports[-1]
    assert last["pill"][2] > 0


# ---------------- 面板前端:常驻悬浮球开关 ----------------


def test_panel_orb_toggle_notifies_python():
    """拨悬浮球开关:本地立刻生效 + 通知 Python 存档。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "toggle", "id": "orbToggle", "checked": True}])

    assert data["orb"] is True
    orbs = [c for c in data["calls"] if c["name"] == "set_orb"]
    assert orbs and orbs[0]["args"] == [True]


def test_panel_orb_switch_follows_python():
    """Python 推 setOrbEnabled(true) → 开关拨到开。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setOrbEnabled", "args": [True]}])

    assert data["orb"] is True


def test_panel_orb_position_click_notifies_python():
    """拨球的位置档位:本地立刻生效 + 通知 Python 存档。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "click", "group": "orbPositionSeg", "index": 2}])

    assert data["active"]["orbPosition"] == "custom"
    calls = [c for c in data["calls"] if c["name"] == "set_orb_position"]
    assert calls and calls[0]["args"] == ["custom"]


def test_panel_orb_position_pinned_click_notifies_python():
    """「记住位置」档:点它同样本地生效 + 通知 Python。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "click", "group": "orbPositionSeg", "index": 1}])

    assert data["active"]["orbPosition"] == "pinned"
    calls = [c for c in data["calls"] if c["name"] == "set_orb_position"]
    assert calls and calls[0]["args"] == ["pinned"]


def test_panel_orb_position_follows_python():
    """Python 推 setOrbPosition('custom') → 档位拨到自定义。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setOrbPosition", "args": ["custom"]}])

    assert data["active"]["orbPosition"] == "custom"


def test_panel_orb_position_follows_python_to_pinned():
    """Python 推 setOrbPosition('pinned') → 档位拨到记住位置。"""
    data = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setOrbPosition", "args": ["pinned"]}])

    assert data["active"]["orbPosition"] == "pinned"


def test_set_orb_position_saves_config_and_pushes_to_both_windows():
    """面板改球的位置档位:存配置 → 推面板前端 → 推胶囊。"""
    panel = _make_themed_panel()
    sink = ThemeSink()
    panel._pump.add_sink(sink)

    panel._set_orb_position("custom")

    assert panel.controller.config.orb_position == "custom"
    assert panel.controller.config.saves == 1

    panel._pump._dispatch(panel._pump._events.get_nowait())
    assert "setOrbPosition" in panel._window.methods()   # 面板前端
    assert sink.events[-1].kind == "orb_position"         # 胶囊


def test_set_orb_position_rejects_unknown_mode():
    """写坏的档位不能传下去 —— 按 follow 处理。"""
    panel = _make_themed_panel()
    sink = ThemeSink()
    panel._pump.add_sink(sink)

    panel._set_orb_position("solarized")

    assert panel.controller.config.orb_position == "follow"
    panel._pump._dispatch(panel._pump._events.get_nowait())
    assert sink.events[-1].text == "follow"


def test_panel_orb_switch_hides_the_unused_position_row():
    """球开 → 只显示「球位置」;球关 → 只显示「位置」。

    两个选项同屏但只有一个生效(常驻球开着时胶囊的位置由球决定),
    是被点名的设计混乱 —— 现在任何时刻屏幕上只有起作用的那个。
    """
    on = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setOrbEnabled", "args": [True]}])
    assert on["rows"] == {"position": True, "orbPosition": False}

    off = _run_js_harness("panel_js_harness.js", [
        {"op": "call", "fn": "setOrbEnabled", "args": [False]}])
    assert off["rows"] == {"position": False, "orbPosition": True}


def test_panel_rows_default_to_the_orb_off_layout():
    """初始(球关):「位置」显示、「球位置」隐藏 —— 和 HTML 初始一致。"""
    data = _run_js_harness("panel_js_harness.js", [])
    assert data["rows"] == {"position": False, "orbPosition": True}


# ---------------- 动效与命中区:描边被裁掉的坑 ----------------
#
# 展开/收起动效期间,胶囊带着 CSS transform(translateY / scale)。
# getBoundingClientRect() 会把 transform 算进去 —— 量到的是"动画中"的
# 矩形。Python 侧按它裁窗口(SetWindowRgn),等于按变形后的形状去裁,
# 胶囊描边的两端就被切掉了;动画停下来后又没人重报,裁形就永久停在
# 那个错位状态(实测:上下边线在两端断开)。
#
# 所以 rectOf 必须量**未变换**的矩形(offset* 系列不受 transform 影响),
# 并且动效结束时再补报一次。


def test_region_report_ignores_css_transform():
    """胶囊带着 transform 时,上报的命中区仍是**未变形**的矩形。

    这条守护的是"胶囊描边在两端断开"那个真机 bug:
    按变形后的矩形裁窗口,描边两端就被 SetWindowRgn 切掉了。
    """
    data = _run_js_harness("capsule_js_harness.js", [
        # 给胶囊挂上展开动效里的 transform:缩小 10% + 下移 14px
        {"op": "setTransform", "target": "pill", "scale": 0.9, "dy": 14},
        {"op": "call", "fn": "setState", "args": ["recording"]},
        {"op": "advance", "ms": 200},
    ])

    reports = [c["args"][0] for c in data["calls"] if c["name"] == "update_regions"]
    assert reports, "应该上报过命中区"
    # 未变形的胶囊:180×40 @ (100,120)
    assert reports[-1]["pill"] == [100, 120, 180, 40]


def test_region_report_ignores_transform_on_the_ball_shaped_pill():
    """球形态的胶囊也一样:带着 scale 时,上报的仍是静止尺寸的球。

    球现在**就是**收窄的胶囊,所以这条守护的是"整颗胶囊带 transform 时,
    命中区仍然和视觉形状一致" —— 量到变形后的矩形,描边就会被
    SetWindowRgn 切掉(真机症状:球的上半截多一条白线)。
    """
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "setTransform", "target": "pill", "scale": 1.28},
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "advance", "ms": 200},
    ])

    reports = [c["args"][0] for c in data["calls"] if c["name"] == "update_regions"]
    assert reports
    assert reports[-1]["pill"] == [170, 120, 40, 40]


def test_the_hit_region_is_reported_while_the_shape_morphs():
    """球 ↔ 胶囊的形变期间要**持续**上报命中区。

    形变是 0.3s 的宽度过渡,期间布局宽度每帧都在变。只在头尾各报
    一次的话,中间那 0.3s 里窗口形状停在"球"那一小块,胶囊多出来的
    那部分点不到(会穿透到底下的应用)。
    """
    data = _run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "advance", "ms": 200},
    ])

    reports = _region_reports(data)
    assert len(reports) >= 3, "形变期间要持续上报命中区"
    # 球形态下报的一定是那颗 40×40 的球
    assert reports[-1]["pill"] == [170, 120, 40, 40]


def test_expanding_reports_the_region_again_after_the_motion():
    """展开动效结束后要**再报一次**命中区。

    动效期间上报的是过渡中的形状;停下来之后没人重报,
    窗口形状就永远停在错位状态(实测:描边两端断着)。
    """
    before = len(_region_reports(_run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setState", "args": ["recording"]},
        {"op": "advance", "ms": 100},
    ])))

    # 动效停下来(定时器兜底:520ms > delay .12s + duration .28s)
    after = len(_region_reports(_run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "call", "fn": "setState", "args": ["recording"]},
        {"op": "advance", "ms": 100},
        {"op": "advance", "ms": 600},
    ])))

    assert after > before, "动效结束后必须再报一次命中区"


def test_transition_end_triggers_a_region_report():
    """transitionend 也要触发重报 —— 动效真正停下来的那一刻。

    定时器只是兜底(prefers-reduced-motion 下压根没有 transitionend)。
    """
    before = len(_region_reports(_run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "advance", "ms": 200},
    ])))

    after = len(_region_reports(_run_js_harness("capsule_js_harness.js", [
        {"op": "call", "fn": "setOrbMode", "args": [True]},
        {"op": "advance", "ms": 200},
        {"op": "fireTransitionEnd", "target": "pill"},
    ])))

    assert after > before, "transitionend 必须触发一次命中区重报"


def _region_reports(data: dict) -> list:
    """从 harness 输出里挑出所有命中区上报。"""
    return [c["args"][0] for c in data["calls"] if c["name"] == "update_regions"]
