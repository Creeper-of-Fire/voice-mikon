"""常驻控制面板(pywebview + WebView2)。

面板现在是**配置窗口**:只显示状态、切换模型、退出程序。
录音过程的全部视觉反馈(实时预览文字、电平条、计时)搬到了
**胶囊窗口**(同目录 ``capsule.py``)—— 面板关不关都不影响录音体验。

面板前端是纯静态 HTML/CSS/JS,放在 ``src/qwen3_voice/ui/static/``,
不引入任何前端框架或构建步骤 —— 项目定位是"绿色软件",
``git clone`` + ``pip install -e .`` 就能跑,不该有 node_modules。

Python 与前端之间是双向桥接,方法名两边必须严格对齐
(完整契约见同目录的 ``BRIDGE.md``):

  Python → JS   window.__qwen.setState / setModel / setHotkey
  JS → Python   window.pywebview.api.ready / switch_model / get_snapshot
                / move_window / close_panel / quit_app

线程分工:
  主线程      调 webview.start() 并等它返回(winforms 后端会把 GUI
              消息循环放到它自己起的 STA 线程上,主线程只是阻塞等待)
  推送线程    _EventPump:把控制器事件转成各窗口的前端调用
  控制器线程  录音/识别,回调可能在任意线程触发,所以只往队列里投

两个容易踩的坑,都在下面代码里处理了:

1. ``evaluate_js`` 在 GUI 还没建好时会阻塞 20 秒才抛异常。
   所以所有前端调用都过 ``_gui_ready`` 门控,窗口就绪后才推。

2. 电平必须推**绝对值**(dBFS 映射),不能按窗口内最大值归一化。
   归一化之后波形永远看起来是满的,用户就没法从条的高低看出
   "我说话声音大不大" —— 那条电平条就只剩装饰作用了。
   (电平现在只有胶囊窗口要,面板已经不显示电平了。)
"""

from __future__ import annotations

import json
import math
import os
import queue
import threading
import time
from pathlib import Path
from typing import Any, Protocol

from ..core.controller import State, UIEvent, VoiceController
from ..logging_setup import get_logger
from ..win32.api import (
    get_foreground_window,
    get_window_rect,
    make_tool_window,
    move_window_to,
    resize_window_no_activate,
    restore_foreground,
    set_tool_window_style,
)

log = get_logger("ui.panel")

# 前端静态资源目录
STATIC_DIR = Path(__file__).resolve().parent / "static"

# 设计基准(逻辑像素 = CSS 像素)
# 面板瘦身后只剩标题栏 + 模型行 + 页脚,高度按内容自适应,
# 这里给的是"内容刚好装下"的下限,不准就在 _fit_window 里量。
BASE_W = 240
BASE_H = 88

# 页面加载完成前的窗口底色(占位):跟系统主题走,免得深色系统下闪一下白。
# 真正的配色由 CSS 的 prefers-color-scheme 媒体查询决定,这里只管"开场"。
WINDOW_BG_LIGHT = "#ffffff"
WINDOW_BG_DARK = "#16171a"

# 推送频率
PUSH_INTERVAL = 1.0 / 30.0

# 电平映射区间:dBFS -> 0..1
#
# 用对数域而不是直接用线性 RMS:MiniMax Code 那边靠浏览器的 AGC
# 把 RMS 稳在 0.15 附近,我们没有 AGC,线性映射会因麦克风增益不同
# 而时高时低。对数映射只关心"比底噪高多少",对增益不敏感。
#
# 下限取 -70:实测这个房间的底噪就有 -62 dBFS,下限卡在 -60 会把底噪
# 和"完全无声"混成同一个 0,电平条看着像死了。实际人声约 -35 ~ -20 dBFS,
# 映射到 0.5 ~ 0.71,和底噪拉得开。
DB_FLOOR = -70.0
DB_CEIL = 0.0

# 控制器状态 -> 前端状态字符串
_STATE_NAMES = {
    State.STARTING: "starting",
    State.IDLE: "idle",
    State.RECORDING: "recording",
    State.PROCESSING: "processing",
    State.ERROR: "error",
}


def system_scale() -> float:
    """主显示器的缩放系数(本机 175% -> 1.75)。

    不能用 ``GetDeviceCaps(LOGPIXELSX)``:进程声明了 Per-Monitor V2
    之后,这个值取决于**调用线程**的 DPI 上下文,不同线程拿到的结果
    不一样(实测主线程拿到 96、探测线程拿到 168),位置就会算飞。

    ``GetDpiForSystem()`` 返回的是主显示器的 DPI,与线程无关,可靠。
    """
    try:
        import ctypes

        dpi = ctypes.windll.user32.GetDpiForSystem()
        if dpi:
            return max(1.0, dpi / 96.0)
    except Exception:
        pass
    try:
        import ctypes

        dc = ctypes.windll.user32.GetDC(0)
        try:
            dpi = ctypes.windll.gdi32.GetDeviceCaps(dc, 88)  # LOGPIXELSX
        finally:
            ctypes.windll.user32.ReleaseDC(0, dc)
        if dpi:
            return max(1.0, dpi / 96.0)
    except Exception:
        pass
    return 1.0


def system_prefers_dark() -> bool:
    """系统是不是深色主题。

    读注册表 ``HKCU\\...\\Themes\\Personalize`` 的 ``AppsUseLightTheme``:
    0 = 深色,1 = 浅色。

    只用来给 WebView2 选一个"页面还没加载时的窗口底色" —— 深色系统
    配白底会闪一下白。真正的配色由 CSS 的 ``prefers-color-scheme``
    媒体查询决定(WebView2 直接跟随 Windows 的应用主题)。
    """
    try:
        import winreg

        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
        ) as key:
            value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
        return int(value) == 0
    except Exception as e:
        log.debug("读系统主题失败,按浅色处理: %s", e)
        return False


def dbfs_to_unit(rms: float, floor_rms: float = 0.0) -> float:
    """把线性 RMS 映射到 0..1(对数域)。

    DB_FLOOR dBFS 映射到 0,0 dBFS 映射到 1,中间线性插值。
    对数域而不是线性域,是因为人耳对 loudness 的感知接近对数,
    线性映射会让正常说话的音量全部挤在 0.05 以下。

    `floor_rms` 是**噪声门**:低于它的直接判 0。不加这个门的话,
    安静房间的底噪(-62 dBFS 实测)映射出来还有 0.11,会被前端当成
    "有声音",电平条一直显示 7px 高度,看着像一直在响。
    """
    if rms <= floor_rms or rms <= 1e-6:
        return 0.0
    db = 20.0 * math.log10(rms)
    if db <= DB_FLOOR:
        return 0.0
    if db >= DB_CEIL:
        return 1.0
    return (db - DB_FLOOR) / (DB_CEIL - DB_FLOOR)


class _EventSink(Protocol):
    """推送目标协议(鸭子类型)。

    胶囊窗口也实现这套接口,但**不 import 本模块** ——
    两个 UI 模块互相 import 迟早长成疙瘩。用协议把边界划清楚。
    """

    def handle_event(self, event: UIEvent) -> None:
        """处理一个控制器事件。"""

    def push_levels(self, levels: list[float], elapsed: float) -> None:
        """推送一帧电平数据(30fps)。"""


class _EventPump:
    """控制器事件 → 各窗口前端的推送线程。

    只有**一条**推送线程,而不是每个窗口一条:

    - 胶囊的前端是状态机(setState → setPreview → setResult 必须按
      顺序到达),两条线程并发推同一个 WebView2 会让顺序不可控;
    - 电平数据采样一次就能分发给所有 sink,多线程反而要重复采样。

    每个 sink 按 _EventSink 协议实现两个方法:

    - ``handle_event(event)``:处理控制器事件
    - ``push_levels(levels, elapsed)``:30fps 电平

    面板瘦身之后不再要电平,它把 ``wants_levels`` 留成 False,
    pump 就不会为一个没人要的窗口去采样。
    """

    def __init__(self, controller: VoiceController):
        self.controller = controller
        self._sinks: list[Any] = []
        self._events: queue.Queue[UIEvent] = queue.Queue()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._state = State.STARTING

    # ---------- sink 注册 ----------

    def add_sink(self, sink: Any) -> None:
        self._sinks.append(sink)

    def sinks(self) -> list[Any]:
        return list(self._sinks)

    # ---------- 线程 ----------

    def start(self) -> None:
        self._thread = threading.Thread(
            target=self._loop, name="ui-push", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def on_event(self, event: UIEvent) -> None:
        """控制器回调(任意线程):这里只投递。"""
        self._events.put(event)

    def _loop(self) -> None:
        """把控制器事件和 30fps 电平转成前端调用。

        单独一个线程,是因为 evaluate_js 是阻塞的(等 WebView2 执行完),
        放在 GUI 主线程会卡住窗口响应。
        """
        last = 0.0
        while not self._stop.is_set():
            try:
                while True:
                    self._dispatch(self._events.get_nowait())
            except queue.Empty:
                pass

            now = time.monotonic()
            if now - last >= PUSH_INTERVAL:
                last = now
                self._push_levels()

            self._stop.wait(0.005)

    def _dispatch(self, event: UIEvent) -> None:
        """把一个事件发给所有 sink。"""
        if event.kind == "state" and event.state is not None:
            self._state = event.state
        for sink in list(self._sinks):
            try:
                sink.handle_event(event)
            except Exception as e:
                # 一个 sink 炸了不能拖垮整条推送链
                log.debug("推送事件到窗口失败: %s", e)

    def _push_levels(self) -> None:
        """采样一次电平,分发给需要的 sink。

        推的是**绝对电平**(dBFS 映射后的 0..1)。前端绝不能按窗口内
        最大值归一化 —— 那会让电平条永远看起来是满的,用户就没法从
        条的高低看出"我说话声音大不大"了。
        """
        if self._state != State.RECORDING:
            return
        if not any(getattr(s, "wants_levels", False) for s in self._sinks):
            return
        recorder = getattr(self.controller, "recorder", None)
        if recorder is None:
            return
        try:
            raw = recorder.levels()
            elapsed = recorder.elapsed
        except Exception:
            return
        mapped = [dbfs_to_unit(v, self.controller.config.silence_rms)
                  for v in raw]
        for sink in list(self._sinks):
            try:
                sink.push_levels(mapped, elapsed)
            except Exception as e:
                log.debug("推送电平失败: %s", e)


class _PanelApi:
    """暴露给前端 JS 的桥接对象。

    单独一个类而不是直接用 VoicePanel,是因为 pywebview 的
    ``get_functions`` 会遍历对象的公开属性,遇到非 callable 的对象
    还会**递归**下去 —— 直接把 VoicePanel 交出去,控制器和配置
    的内部属性都会被暴露到网页里。这里只留方法,引用用下划线开头
    (pywebview 会跳过)。
    """

    def __init__(self, panel: VoicePanel):
        self._panel = panel

    def ready(self) -> None:
        """前端 DOM 就绪,请求推一次全量状态。"""
        self._panel._on_frontend_ready()

    def switch_model(self, tag: str) -> None:
        self._panel.controller.switch_model(tag)

    def get_snapshot(self) -> dict:
        return self._panel._snapshot()

    def move_window(self, x: float, y: float) -> None:
        """把窗口左上角移到 (x, y) —— **绝对位置,CSS(逻辑)像素**。

        前端拖动时每次都从 mousedown 起点算绝对目标发过来,
        不发增量 —— 某次调用丢了/慢了也不会漂。
        """
        self._panel._move_window_abs(x, y)

    def close_panel(self) -> None:
        """隐藏面板(不是退出程序)。"""
        self._panel.hide()

    def quit_app(self) -> None:
        """退出整个程序。"""
        self._panel._quit()


class VoicePanel:
    """语音输入控制面板(配置窗口)。"""

    # 面板不显示电平了 —— 电平条搬到了胶囊窗口
    wants_levels = False

    def __init__(self, controller: VoiceController, geometry: str = "",
                 focus_restore: int = 0):
        """`focus_restore` 是**启动时**的前台窗口句柄。

        pywebview 建 hidden 窗口时会偷偷 Show 一次(它的透明 hack),
        那一下会把窗口激活 —— 程序启动就抢走用户的键盘焦点,正在打字
        的用户会丢字。所以窗口建好后要把前台还回去。
        """
        self.controller = controller
        self._focus_restore = focus_restore

        self._state = State.STARTING

        self._scale = system_scale()
        self._visible = False          # 期望可见性(GUI 就绪前只记录)
        self._gui_ready = threading.Event()      # 窗口已创建(可以 show/hide)
        self._frontend_ready = threading.Event()  # 前端已就绪(可以 evaluate_js)
        self._stop = threading.Event()
        self._fit_lock = threading.Lock()   # fit 是耗时的,别让两个同时跑

        # 窗口位置(逻辑像素)。None 表示还没定,会在窗口就绪后摆到右下角。
        self._pos_x: int | None = None
        self._pos_y: int | None = None

        # ---- portable:WebView2 的用户数据必须落在项目内 ----
        # pywebview 会在 start() 里显式设置 WebView2 的 UserDataFolder,
        # 光设 WEBVIEW2_USER_DATA_FOLDER 环境变量会被它覆盖,
        # 所以两个都要给。
        from ..paths import webview_dir

        self._webview_dir = webview_dir()
        try:
            self._webview_dir.mkdir(parents=True, exist_ok=True)
            os.environ["WEBVIEW2_USER_DATA_FOLDER"] = str(self._webview_dir)
        except OSError as e:
            log.warning("创建 WebView2 数据目录失败: %s", e)

        import webview

        # 推送线程:控制器事件从这里分发给面板和胶囊窗口
        self._pump = _EventPump(controller)
        self._pump.add_sink(self)

        self._api = _PanelApi(self)
        index = STATIC_DIR / "index.html"
        # 先解析保存过的位置:create_window 的 x/y 是逻辑像素,得在建窗时给
        saved_x, saved_y = self._parse_geometry(geometry)
        self._window = webview.create_window(
            "Qwen3 语音输入",
            url=index.as_uri(),
            js_api=self._api,
            width=BASE_W,
            height=BASE_H,
            x=saved_x,
            y=saved_y,
            frameless=True,
            hidden=True,
            easy_drag=False,      # 自己实现拖动,才能记住位置
            on_top=True,
            resizable=False,
            background_color=(WINDOW_BG_DARK if system_prefers_dark()
                             else WINDOW_BG_LIGHT),
        )
        self._window.events.before_show += self._on_before_show
        self._window.events.shown += self._on_window_shown

        controller.subscribe(self._pump.on_event)

    # ---------- 推送目标接口 ----------

    def add_sink(self, sink: Any) -> None:
        """再挂一个推送目标(胶囊窗口)。

        面板是推送线程的主人,所以注册也走这里 —— 胶囊窗口不用
        自己去订阅控制器,顺序也就统一由一条推送线程保证。
        """
        self._pump.add_sink(sink)

    def handle_event(self, event: UIEvent) -> None:
        """控制器事件 → 面板前端。

        面板瘦身后只关心三样东西:状态、模型、热键。
        电平/计时/预览/结果都归胶囊窗口了。
        """
        if event.kind == "state" and event.state is not None:
            self._state = event.state
            self._call_js("setState", self._state_name())
        elif event.kind == "model" and event.model_tag:
            self._call_js("setModel", event.model_tag)

    def push_levels(self, levels: list[float], elapsed: float) -> None:
        """面板不显示电平 —— 空实现(接口要留着,免得 pump 判类型)。"""

    # ---------- 几何 ----------

    def _parse_geometry(self, geometry: str) -> tuple[int | None, int | None]:
        """解析保存过的窗口位置。

        保存的是**逻辑像素**(物理像素 ÷ 缩放系数),换一台缩放率不同的
        显示器时位置还能落在合理的地方。pywebview 的 create_window
        的 x/y 也是逻辑像素,直接传即可。
        """
        if not geometry:
            return None, None
        try:
            parts = [int(v) for v in geometry.split(",")]
            if len(parts) == 4:
                x, y, _w, _h = parts
                self._pos_x = x
                self._pos_y = y
                return x, y
        except ValueError:
            log.info("保存的窗口位置无法解析: %s", geometry)
        return None, None

    def _save_geometry(self) -> None:
        """保存窗口位置(逻辑像素)。

        先用 GetWindowRect 读**真实位置**再存 —— 记账式维护的
        ``_pos_x/_pos_y`` 可能和真实窗口脱钩(取整、clamp、
        pywebview 内部换算),读为准。
        """
        self._sync_position_from_window()
        if self._pos_x is None or self._pos_y is None:
            return
        try:
            self.controller.config.panel_geometry = (
                f"{self._pos_x},{self._pos_y},{BASE_W},{BASE_H}")
            self.controller.config.save()
        except Exception as e:
            log.debug("保存窗口位置失败: %s", e)

    def _place_default(self) -> None:
        """没有保存过位置时,摆到主屏幕右下角。"""
        if self._pos_x is not None:
            return
        scale = self._current_scale()
        try:
            import ctypes

            # DPI aware 进程里 GetSystemMetrics 返回物理像素
            sw = ctypes.windll.user32.GetSystemMetrics(0)
            sh = ctypes.windll.user32.GetSystemMetrics(1)
            x = int(sw / scale) - BASE_W - 30
            y = int(sh / scale) - BASE_H - 60
        except Exception:
            x, y = 60, 60
        self._pos_x = x
        self._pos_y = y
        try:
            self._window.move(x, y)
        except Exception as e:
            log.debug("摆默认位置失败: %s", e)

    # ---------- 前端桥接 ----------

    def _call_js(self, method: str, *args: Any) -> bool:
        """调用前端方法。

        前端可能还没加载完,所以在同一个表达式里先判断方法存在再调用 ——
        这样存在性检查和调用之间不会有竞态,也只花一次往返。

        门控是"前端已就绪"而不是"窗口已创建":``evaluate_js`` 在前端
        还没就绪时会**阻塞 20 秒**才抛异常(pywebview 等的是
        ``_pywebviewready`` 事件),不能拿它当普通调用使。
        """
        if not self._frontend_ready.is_set():
            return False
        payload = ", ".join(json.dumps(a, ensure_ascii=False) for a in args)
        script = (
            "(function(){try{"
            f"if(window.__qwen&&typeof window.__qwen.{method}==='function'){{"
            f"window.__qwen.{method}({payload});return true;}}"
            "}catch(e){return false;}return false;})()"
        )
        try:
            self._window.evaluate_js(script)
            return True
        except Exception as e:
            log.debug("调用前端 %s 失败: %s", method, e)
            return False

    def _on_frontend_ready(self) -> None:
        """前端就绪,推一次全量状态 + 补一次窗口尺寸。

        fit 放这里是因为 DOM ready 之后 evaluate_js 一定能执行
        (GUI 就绪那一刻页面可能还没加载完,那时的 fit 会失败,
        靠这里兜底)。fit 是幂等的:量内容高度、resize 到刚好,
        重复执行结果相同。
        """
        self._frontend_ready.set()
        self._call_js("setState", self._state_name())
        self._call_js("setModel", self._model_tag())
        self._call_js("setHotkey", self.controller.config.hotkey)
        try:
            self._fit_window()
        except Exception as e:
            log.debug("前端 ready 后 fit 失败: %s", e)

    def _state_name(self) -> str:
        return _STATE_NAMES.get(self._state, "idle")

    def _model_tag(self) -> str:
        try:
            return getattr(self.controller.engine, "model_tag", "") or ""
        except Exception:
            return ""

    def _snapshot(self) -> dict:
        return {
            "state": self._state_name(),
            "model": self._model_tag(),
            "hotkey": self.controller.config.hotkey,
        }

    # ---------- 窗口生命周期 ----------

    def _apply_visibility(self) -> None:
        """把期望的可见性/位置/尺寸落到窗口上(后台线程里跑)。

        show()/hide() 只记意图,落地统一在这里 —— 并且全程异常兜底:
        窗口层的异常绝不能把调用方(推送线程/托盘线程)带崩
        (发生过:一处裸奔的异常把推线程炸掉,面板全坏)。
        """
        if not self._gui_ready.is_set():
            return
        try:
            # 摆位:有保存位置就精确校准(create_window 的 x/y 会被
            # WinForms AutoScale 再动一次,和初始尺寸缩水是同一个坑),
            # 没有就摆到主屏右下角
            if self._pos_x is not None and self._pos_y is not None:
                hwnd = self._native_handle()
                if hwnd:
                    scale = self._current_scale()
                    move_window_to(hwnd,
                                   int(round(self._pos_x * scale)),
                                   int(round(self._pos_y * scale)))
            else:
                self._place_default()

            if self._visible:
                self._window.show()
            else:
                self._window.hide()
        except Exception as e:
            log.warning("应用窗口可见性失败: %s", e)
        # fit 单独兜底:此时页面可能还没加载完,evaluate_js 会失败,
        # 前端 DOM ready 后(_on_frontend_ready)还会再 fit 一次
        try:
            self._fit_window()
        except Exception as e:
            log.debug("首帧 fit 失败(等前端 ready 再试): %s", e)

    def _schedule_apply(self) -> None:
        """在后台线程里落地可见性。

        落地会调 WinForms 的 Show/Hide,虽然 pywebview 内部有 Invoke
        兜底,但这里仍不在 UI 线程里做 —— shown 事件是在 UI 线程同步
        触发的,那时候再调 show() 是重入,可能把窗口搞挂。
        """
        if not self._gui_ready.is_set():
            return
        threading.Thread(target=self._apply_visibility,
                         name="ui-layout", daemon=True).start()

    def _fit_window(self) -> None:
        """把窗口调整到刚好能容纳内容。

        pywebview 建窗时给的初始尺寸会被 WinForms 的 AutoScale 再缩一次
        (实测:请求 300x260 逻辑像素,实际客户区只有 286x224),底部几个
        像素的内容就被裁掉了。但 ``resize()`` 走的是另一条路径,尺寸是准的
        (实测 resize(300,320) 得到的正好是 320 逻辑像素)。

        所以这里自己量一次内容高度,再 resize 到刚好合适。
        """
        # fit 是耗时的(resize + sleep 等前端布局),同一时间只允许一个在跑:
        # show/hide 和前端 ready 都会触发它,撞在一起会互相改尺寸。
        if not self._fit_lock.acquire(blocking=False):
            return
        try:
            self._fit_window_locked()
        finally:
            self._fit_lock.release()

    def _fit_window_locked(self) -> None:
        need = BASE_H
        for _ in range(3):
            if not self._resize(BASE_W, BASE_H):
                return
            time.sleep(0.25)
            need = self._content_height()
            if need <= BASE_H:
                self._keep_on_screen(BASE_H)
                return
            if not self._resize(BASE_W, need):
                return
            time.sleep(0.25)
        self._keep_on_screen(need)

    def _resize(self, width: int, height: int) -> bool:
        """改窗口尺寸(逻辑像素)。

        不用 pywebview 的 ``window.resize()``:它在 winforms 后端里带着
        ``SWP_SHOWWINDOW``,会把**隐藏着的面板顺带显示出来**。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return False
        try:
            scale = self._current_scale()
            resize_window_no_activate(hwnd, int(round(width * scale)),
                                       int(round(height * scale)))
            return True
        except Exception as e:
            log.debug("调整窗口尺寸失败: %s", e)
            return False

    def _keep_on_screen(self, height: int) -> None:
        """窗口变高后别让它长出屏幕底部。

        resize 只改尺寸不改位置,窗口会往下长。默认摆位在右下角,
        长高一点就出屏了,所以这里把超出屏幕的部分拉回来
        (没超出的不动,免得把用户拖到中间的窗口硬拽回角落)。
        """
        if self._pos_x is None or self._pos_y is None:
            return
        try:
            import ctypes

            sw = ctypes.windll.user32.GetSystemMetrics(0)
            sh = ctypes.windll.user32.GetSystemMetrics(1)
            scale = self._current_scale()
            x = min(self._pos_x, int(sw / scale) - BASE_W)
            y = min(self._pos_y, int(sh / scale) - height)
            if (x, y) != (self._pos_x, self._pos_y):
                self._pos_x, self._pos_y = x, y
                self._window.move(x, y)
        except Exception as e:
            log.debug("把窗口拉回屏幕内失败: %s", e)

    def _content_height(self) -> int:
        """页面内容实际需要的高度(逻辑像素)。

        前端没就绪就返回 0 —— ``evaluate_js`` 在那之前会阻塞 20 秒,
        量高度是给"内容排版好了"用的,那时候才有意义。
        """
        if not self._frontend_ready.is_set():
            return 0
        try:
            info = self._window.evaluate_js(
                "(function(){return {sh:document.body.scrollHeight,"
                "ih:window.innerHeight};})()")
            return int(info.get("sh", 0))
        except Exception as e:
            log.debug("读取内容高度失败: %s", e)
            return 0

    # ---------- 拖动 ----------

    def _current_scale(self) -> float:
        """当前缩放系数。

        优先用**窗口所在显示器**的 DPI(GetDpiForWindow,多显示器
        不同缩放时也准),取不到再退回主显示器的 DPI。
        """
        try:
            import ctypes

            native = getattr(self._window, "native", None)
            hwnd = int(native.Handle.ToInt64()) if native is not None else 0
            if hwnd:
                dpi = ctypes.windll.user32.GetDpiForWindow(ctypes.c_void_p(hwnd))
                if dpi:
                    return max(1.0, dpi / 96.0)
        except Exception:
            pass
        return self._scale

    def _move_window_abs(self, x: float, y: float) -> None:
        """窗口左上角 → (x, y) 逻辑像素。

        用 Win32 SetWindowPos 直接设(物理 = 逻辑 × 当前缩放),
        不走 pywebview 的 move()(内部还有一层换算与消息,更慢)。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            scale = self._current_scale()
            move_window_to(hwnd, int(round(float(x) * scale)),
                           int(round(float(y) * scale)))
            self._pos_x = int(round(float(x)))
            self._pos_y = int(round(float(y)))
        except Exception as e:
            log.debug("移动窗口失败: %s", e)

    def _sync_position_from_window(self) -> None:
        """用窗口的**真实物理位置**回填逻辑坐标。

        任何"我以为窗口在哪"的记账都可能和真实位置脱钩
        (取整、clamp、pywebview 内部换算),所以读为准。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        rect = get_window_rect(hwnd)
        if rect is None:
            return
        try:
            scale = self._current_scale()
        except Exception:
            scale = self._scale
        self._pos_x = int(round(rect[0] / scale))
        self._pos_y = int(round(rect[1] / scale))

    # ---------- 生命周期 ----------

    def _native_handle(self) -> int:
        """底层 Win32 窗口句柄(0 = 还没建好)。"""
        try:
            native = getattr(self._window, "native", None)
            if native is None:
                return 0
            return int(native.Handle.ToInt64())
        except Exception:
            return 0

    def _on_before_show(self) -> None:
        """窗口即将显示:抢先改扩展样式,别让它在任务栏上露脸。"""
        hwnd = self._native_handle()
        if hwnd:
            set_tool_window_style(hwnd)

    def _on_window_shown(self) -> None:
        """窗口已创建:改成纯悬浮窗,然后放行 GUI 就绪。

        这里**只做样式**,不做显示/摆位/尺寸 ——
        shown 是 pywebview 在 UI 线程里同步触发的事件(hidden 窗口
        也会被透明 Show 一次从而触发),在这里再调 window.show()
        是 UI 线程重入,可能把窗口搞挂。显示/摆位/fit 由推线程的
        ``_apply_pending_visibility`` 负责(有异常兜底,炸不了线程)。

        先隐藏再改样式的顺序照旧:任务栏只在窗口显示时重新评估
        可见性,顺序反了会先冒出一个任务栏按钮再消失。
        """
        hwnd = self._native_handle()
        if hwnd:
            make_tool_window(hwnd)
        self._gui_ready.set()
        # pywebview 建 hidden 窗口时会偷偷 Show 一次,那一下会把窗口激活 ——
        # 把前台还给用户原来那个窗口
        self._restore_focus()
        # 首次落地(可见性 + 尺寸)扔到后台线程 —— 这里正处在 UI 线程
        # 的事件回调里,再调 window.show() 是重入,可能把窗口搞挂。
        self._schedule_apply()

    def _restore_focus(self) -> None:
        """把前台窗口还给用户原来那个(见 __init__ 的说明)。

        pywebview 在 shown 之后还会再调一次 ``webview.Focus()``,把窗口
        变成前台 —— 同步还原一次盖不住它,得再延迟还原一次。
        """
        self._restore_focus_once()
        threading.Thread(target=self._restore_focus_later, name="ui-focus",
                         daemon=True).start()

    def _restore_focus_later(self) -> None:
        time.sleep(0.25)
        self._restore_focus_once()

    def _restore_focus_once(self) -> None:
        hwnd = self._native_handle()
        if not hwnd or not self._focus_restore:
            return
        if get_foreground_window() != hwnd:
            return
        restore_foreground(self._focus_restore)

    def show(self) -> None:
        """记录"想要可见"。

        GUI 就绪前只记意图(等窗口就绪后统一落地),就绪后直接落地。
        """
        self._visible = True
        self._schedule_apply()

    def hide(self) -> None:
        self._visible = False
        if self._gui_ready.is_set():
            # 隐藏前把真实位置记下来 —— 用户可能拖完就直接关面板,
            # 之后直接退出程序的话就再没机会读了
            self._save_geometry()
        self._schedule_apply()

    def toggle(self) -> None:
        if self._visible:
            self.hide()
        else:
            self.show()

    def _quit(self) -> None:
        """退出程序:先存窗口位置,再销毁窗口让 webview.start() 返回。"""
        self.destroy()

    def destroy(self) -> None:
        # 重入保护:下面遍历 sinks 调 destroy(),而**面板自己就是
        # 第一个 sink** —— 没有这个标志就是 panel.destroy() 递归
        # panel.destroy()…(实测 window.destroy() 被调了约 994 次,
        # _save_geometry 也就把配置非原子地重写了近千遍)。
        if getattr(self, "_destroyed", False):
            return
        self._destroyed = True
        self._stop.set()
        self._pump.stop()
        try:
            self._save_geometry()
        except Exception:
            pass
        # 别的窗口也得一起销毁:pywebview 的主循环要等**所有**窗口
        # 都没了才返回,只关面板的话程序会变成"托盘没了但还活着"的僵尸。
        for sink in self._pump.sinks():
            destroy = getattr(sink, "destroy", None)
            if callable(destroy):
                try:
                    destroy()
                except Exception as e:
                    log.debug("销毁窗口失败: %s", e)
        try:
            self._window.destroy()
        except Exception:
            pass

    def mainloop(self) -> None:
        """阻塞运行 GUI 主循环(必须在主线程调用)。"""
        self._pump.start()

        import webview

        try:
            # private_mode=False + storage_path:让 WebView2 的用户数据
            # 固定落在项目内的 data/webview,而不是 %LOCALAPPDATA%。
            webview.start(private_mode=False, storage_path=str(self._webview_dir))
        except Exception as e:
            log.error("面板启动失败(WebView2 不可用?): %s", e)
        finally:
            self._stop.set()
            self._pump.stop()
