"""悬浮球窗口(pywebview + WebView2,透明无边框)。

窗口是**固定尺寸**的透明窗:待机时里面只有一颗球(胶囊收窄成的
正圆),录音时球原地横向长开成胶囊。球、胶囊是同一个元素、同一个
窗口 —— 没有第二个形状,也就没有"两个形状互相裁切"的问题。

前端是纯静态 HTML/CSS/JS(``static/capsule.{html,css,js}``),
零框架零构建。

Python 与前端之间的桥接契约:

  Python → JS   window.__capsule.setMode / setLevels / setElapsed
                / setPreview / setTheme / setPreviewEnabled
                / setOrbMode / setOrbPosition / setOrbHint
  JS → Python   window.pywebview.api.ready / cancel_recording / stop_recording
                / update_regions / copy_result / start_recording / open_panel
                / quit_app / launch_qr_scanner / selecting_text / focus_textbar
                / dismiss

  ⚠ **模块私有**(拖拽 composable 内部使用,业务侧勿直接调用):
                window.pywebview.api.move_orb / move_capsule
                / begin_drag / end_drag
    这四个是"拖拽怎么落到窗口上"的实现细节 —— 消费侧只需要
    makeDraggable(把手, {...}),自己拼 begin/move/end 序列的话,
    焦点冻结和位置记忆这两处很容易跟着错。

  **setMode 是显示状态的唯一入口**:Python(presenter)推
  ``setMode(mode, payload)``,前端只演、不决策 —— 停多久、到点回哪
  全归 presenter 的 dwell 表(见 apps/voicebar/protocol.py)。
  setOrbHint 是**纯视觉**的一键提示气泡(球上方冒一句),不是状态机
  命令。

业务动作(开始/停止/取消录音、复制、菜单出口)由 presenter 通过
回调注入(set_*_handler),窗口层只管窗口,不认识引擎和注入器。

六个坑,都在下面代码里处理了:

1. **显示绝不能抢焦点**。pywebview 的 ``window.show()`` 内部会调
   WinForms 的 ``Activate()``,抢了焦点,后面注入的文字就没有落点了。
   所以显示一律走 Win32:先加 ``WS_EX_NOACTIVATE``,再
   ``ShowWindow(SW_SHOWNOACTIVATE)``。

2. **透明窗口在 pywebview 里自带一个抢焦点的 hack**。它对透明窗口
   会在页面开始导航时执行 ``form.Show() + form.Activate()`` ——
   胶囊会在启动时自己冒出来,还占住前台。定点修法见 ``_on_before_show``。

3. **透明窗口一旦被 resize,透明就失效**。所以窗口**固定尺寸**,
   不 resize;多余空白靠 ``SetWindowRgn`` 裁掉。

4. **evaluate_js 在 GUI 还没建好时会阻塞 20 秒才抛异常**。
   所以所有前端调用都过 ``_gui_ready`` 门控,窗口就绪后才推。

5. **透明窗口上不能有 box-shadow / backdrop-filter** ——
   在透明窗口上它们会渲染成一条灰带。

6. **窗口的空白区也属于窗口**。靠 ``SetWindowRgn`` 把窗口形状裁成
   "可见内容的并集"解决:前端量出内容矩形上报(``update_regions``),
   这边换算成物理像素后裁窗口。
"""

from __future__ import annotations

import json
import queue
import threading
import time
from pathlib import Path
from typing import Any, Callable

from ..logging_setup import get_logger
from ..win32 import clipboard
from ..win32.api import (
    bring_to_top,
    enable_layered_window,
    get_foreground_window,
    get_window_rect,
    hide_window,
    is_window_visible,
    move_window_to,
    restore_foreground,
    set_no_activate_tree,
    set_tool_window_style,
    set_window_regions,
    show_window_no_activate,
    upload_transparent_surface,
)
from ..win32.monitors import (
    monitor_at_point,
    monitor_by_device,
    primary_monitor,
)
from .drag import DragSession, orb_placement
from .placement import (
    default_orb_position,
    encode_offset,
    orb_position_from_geometry,
    orb_screen_position,
    orb_window_position,
)
from .webview_tweaks import focus_webview

log = get_logger("host.capsule")

# 前端静态资源目录
STATIC_DIR = Path(__file__).resolve().parent / "static"

# 窗口尺寸(逻辑像素):**固定**,不做高度自适应。
#
# 为什么固定:pywebview 的透明窗口一旦被 resize,WebView2 的透明背景
# 就会失效。宽 512 / 高 260 是**请求值**,不是拿到手的值:pywebview 建窗
# 后 WinForms 的 AutoScale 会把客户区再缩一次。内容用 CSS 贴着窗口
# 底部排,文字条自己内部滚动,窗口大小就一直够用。
WIN_W = 512
WIN_H = 260

# 各种结局之后胶囊停留多久,**不在这一层**:那是显示状态机的事,
# 由 presenter 的 dwell 表统一管(见 apps/voicebar/protocol.py)。
# 窗口层只管显隐,不管"什么时候收回"。

# 写完剪贴板后等一下再读回校验。剪贴板是异步通知的,立刻读有时会
# 读到旧内容。
CLIPBOARD_VERIFY_DELAY = 0.05

# 常驻悬浮球:前端就绪后等这么久再让球露脸。
# 必须等前端把"球"那一小块报上来、Python 侧裁好命中区之后才能露脸
# —— 否则那 512×260 的透明空白会把底下应用的点击全吃掉。
ORB_SHOW_DELAY = 0.3

# 启动守卫:建窗后的这段时间里盯着窗口,别让 pywebview 的建窗副作用
# 把它亮出来或者抢走焦点(见 _on_before_show 的说明)
GUARD_SECONDS = 5.0
GUARD_INTERVAL = 0.08
# 建窗副作用结束后,守卫转入稀疏轮询**常驻**到退出。
GUARD_SPARSE = 0.15

# 窗口命中区域裁形(SetWindowRgn):见 _on_update_regions 的说明。
#
# 每边外扩的余量(**物理**像素):逻辑坐标换物理像素有取整误差,
# 抗锯齿的边缘还可能差半个像素。
REGION_MARGIN = 3

# 圆角矩形的圆角半径(**逻辑** CSS 像素,取自 capsule.css)。
TEXTBAR_RADIUS_PX = 14
OVERLAY_RADIUS_PX = 12

# 命中刷新间隔(秒):见 _guard_loop 里的说明。
HIT_REFRESH_INTERVAL = 0.5

# 前端上报的几块内容(顺序固定,并集的结果与顺序无关,但好读)
#
# textbar / pill / overlay:
#   textbar  上方的文字条
#   pill     胶囊(待机时它**就是**那颗球)
#   overlay  球的浮层(右键菜单 / 操作提示)—— 单独一块,不能并进 pill
_REGION_KEYS = ("textbar", "pill", "overlay")


def _parse_regions(payload: Any) -> dict[str, tuple[float, float, float, float]] | None:
    """把前端上报的矩形解析成 ``{块名: (x, y, w, h)}``(**逻辑**像素)。

    payload 形如 ``{"textbar": [x, y, w, h], "pill": [x, y, w, h]}``;
    一块全 0 表示**那块隐藏**(不参与并集)。

    返回 None 表示这次上报没法用,调用方该原样忽略。
    """
    if isinstance(payload, str):
        try:
            payload = json.loads(payload)
        except Exception:
            return None
    if not isinstance(payload, dict):
        return None

    out: dict[str, tuple[float, float, float, float]] = {}
    for key in _REGION_KEYS:
        raw = payload.get(key)
        if raw is None:
            continue
        if isinstance(raw, dict):
            raw = [raw.get("x"), raw.get("y"), raw.get("w"), raw.get("h")]
        if not isinstance(raw, (list, tuple)) or len(raw) != 4:
            return None
        try:
            x, y, w, h = (float(v) for v in raw)
        except (TypeError, ValueError):
            return None
        out[key] = (x, y, w, h)
    if not out:
        return None
    return out


class _CapsuleApi:
    """暴露给前端 JS 的桥接对象。

    单独一个类:pywebview 的 ``get_functions`` 会遍历公开属性,遇到
    非 callable 的对象还会**递归**下去 —— 直接把 VoiceCapsule 交出去,
    内部属性都会被暴露到网页里。这里只留方法,
    引用用下划线开头(pywebview 会跳过)。
    """

    def __init__(self, capsule: VoiceCapsule):
        self._capsule = capsule

    def ready(self) -> None:
        """前端 DOM 就绪。"""
        self._capsule._on_frontend_ready()

    def cancel_recording(self) -> None:
        """放弃这一轮:不识别、不注入。"""
        self._capsule._give_back_focus()
        self._capsule._presenter.try_cancel_recording()

    def stop_recording(self) -> None:
        """结束录音,进入识别。"""
        self._capsule._give_back_focus()
        self._capsule._presenter.try_stop_recording()

    def update_regions(self, payload: Any) -> None:
        """前端上报可见内容的矩形(CSS 逻辑像素)。"""
        self._capsule._on_update_regions(payload)

    def begin_drag(self) -> None:
        """拖拽 composable 内部使用(**勿直接调用**)。

        用户开始拖(按住不放)。这一下会冻结焦点守卫。
        """
        self._capsule._drag.begin()

    def end_drag(self) -> None:
        """拖拽 composable 内部使用(**勿直接调用**)。

        用户松手:归还焦点 + 记住位置。
        """
        self._capsule._drag.end()

    def copy_result(self, text: str) -> bool:
        """把识别结果手动写进剪贴板(注入失败时的兜底出口)。

        返回**是否真的写进去了** —— 前端要靠这个结果决定是收胶囊
        还是让用户再试一次。
        """
        return self._capsule._copy_result(text)

    def dismiss(self) -> bool:
        """用户主动关掉错误提示(带原文的错误常驻态)。

        返回**是否真的关掉了** —— 不在"带原文的错误常驻态"时
        静默忽略(幂等):前端多发一次、或者别的状态误触发,都不会
        把正在录的一轮收掉。
        """
        return self._capsule._dismiss_error()

    def start_recording(self) -> None:
        """点悬浮球开始录音。

        ⚠ 这一步踩的是**焦点陷阱**:鼠标点在 WebView2 的子窗口上会
        激活整棵窗口树,前台被胶囊抢走 —— 那一轮识别完的文字就会贴到
        胶囊自己身上。所以开始录音**之前**必须把前台还给用户原先所在
        的窗口。
        """
        self._capsule._start_recording_from_orb()

    def move_orb(self, x: float, y: float) -> None:
        """拖拽 composable 内部使用(**勿直接调用**)。

        拖动悬浮球:窗口左上角移到 (x, y) —— **绝对位置,CSS(逻辑)像素**。

        前端每次都从 mousedown 起点算绝对目标,发过来的不是增量 ——
        某一次调用丢了或慢了,下一次直接覆盖,天然不漂。
        """
        self._capsule._drag.move(x, y)

    def move_capsule(self, x: float, y: float) -> None:
        """拖拽 composable 内部使用(**勿直接调用**)。

        拖动胶囊:窗口左上角移到 (x, y) —— **绝对位置,CSS(逻辑)像素**。

        和 ``move_orb`` **同一条路径**:球和胶囊是同一个窗口、共用同一份
        位置记忆(``config.orb.geometry``),拖胶囊就是挪这个窗口 ——
        摆位规则(DPI 换算、夹回工作区)自然也一致,不存在第二套实现。

        松手时走的 ``end_drag`` 和拖球完全相同:只存 orb.geometry /
        orb.monitor 这一份。
        """
        self._capsule._drag.move(x, y)

    def open_panel(self) -> None:
        """球菜单里的「设置」:打开面板(回调由入口注入)。"""
        self._capsule._open_panel()

    def quit_app(self) -> None:
        """球菜单里的「退出」:退出整个程序(回调由入口注入)。"""
        self._capsule._quit_from_menu()

    def launch_qr_scanner(self) -> None:
        """球菜单里的「二维码扫描」:拉起附属工具(回调由入口注入)。"""
        self._capsule._launch_qr_from_menu()

    def selecting_text(self, selecting: bool) -> None:
        """前端上报"用户正在/结束选文本"。

        选文本期间守卫**不抢前台** —— 抢了 WebView2 就失去键盘焦点,
        用户按 Ctrl+C 复制不了。
        """
        self._capsule._on_selecting_text(selecting)

    def focus_textbar(self) -> None:
        """前端上报"用户按下了文字条":胶囊临时成为前台。

        窗口是 WS_EX_NOACTIVATE,鼠标点击不会激活它 —— WebView2 拿
        不到键盘焦点,选字后的 Ctrl+C、右键菜单的复制全都使不上劲。
        """
        self._capsule._focus_textbar()


class VoiceCapsule:
    """悬浮球窗口。"""

    def __init__(self, presenter: Any, config: Any = None,
                 focus_restore: int = 0):
        """``presenter`` 提供业务回调(try_start/stop/cancel_recording
        /try_copy_result);``config`` 提供主题/预览/球开关/球位置记忆;
        ``focus_restore`` 是**启动时**的前台窗口句柄。

        pywebview 建 hidden 窗口时会偷偷 Show 一次(它的透明 hack),
        那一下会把窗口激活 —— 胶囊要是成了前台窗口,用户的键盘输入
        就进了胶囊。所以窗口建好后要把前台还给用户原来那个窗口。
        """
        self._presenter = presenter
        self._config = config
        self._focus_restore = focus_restore

        # 当前显示状态(presenter 推过来的 mode)+ 它的 payload:
        # 前端就绪后要补推一次(见 _on_frontend_ready)
        self._state = "orb"
        self._last_payload: dict[str, Any] = {}
        self._gui_ready = threading.Event()
        self._destroyed = False
        self._last_preview = ""
        # 这一轮录音开始时用户所在的前台窗口(= 注入落点)。
        self._target_hwnd = 0
        # 窗口当前是不是"该给用户看"。
        self._want_visible = False
        # 球是不是已经就位(窗口该露着)。
        self._orb_shown = False
        # 用户当前所在的窗口(守卫动态跟踪)。
        self._last_foreground = 0
        # 启动守卫只起一次
        self._guard_started = False
        # 菜单的三个出口:回调由入口在组装时注入。
        self._panel_opener: Callable[[], None] | None = None
        self._quit_handler: Callable[[], None] | None = None
        self._qr_launcher: Callable[[], None] | None = None

        # "稍后让球露脸"的定时器
        self._orb_timer: threading.Timer | None = None
        self._orb_lock = threading.Lock()
        # 前端报过命中区没有(SetWindowRgn 裁好窗口形状了)。
        self._orb_regions_ready = False
        # 已经上传过的透明 surface 的窗口尺寸。
        self._surface_size: tuple[int, int] | None = None
        # 用户正在文字条里选文本:这段时间守卫不抢前台
        self._selecting_text = False

        import webview

        # 推送队列:所有 Python → JS 的调用串行过这一条线程。
        # WebView2 是 STA COM 对象,多线程并发 evaluate_js 会互相踩
        # (推送丢失、状态错乱、COM 对象分离)—— 推送必须串行。
        self._push_queue: "queue.Queue[str | None]" = queue.Queue()
        self._push_thread = threading.Thread(
            target=self._push_loop, name="ui-capsule-push", daemon=True)
        self._push_thread.start()

        # 拖动会话:按住 → 拖 → 松手的状态、几何换算、焦点冻结都在
        # DragSession 里(见 host/drag.py)。窗口层只提供它需要的几样
        # 东西:窗口句柄、缩放、尺寸、保存位置、归还焦点。
        #
        # 摆位策略也注入:球有球的那套规则(球在窗口底边居中、纵向要给
        # 展开后的胶囊留位),别的窗口形态有别的规则 —— 规则归策略,
        # 会话只管流程。
        self._drag = DragSession(
            window_handle=self._native_handle,
            window_scale=self._current_scale,
            window_size=self._window_size,
            save_geometry=self._save_orb_position,
            restore_focus=lambda: self._give_back_focus(self._user_foreground()),
            placement=orb_placement(
                window_handle=self._native_handle,
                window_scale=self._current_scale,
                window_size=self._window_size),
        )

        self._api = _CapsuleApi(self)
        html = STATIC_DIR / "capsule.html"
        self._window = webview.create_window(
            "voicebar",
            url=html.as_uri(),
            js_api=self._api,
            width=WIN_W,
            height=WIN_H,
            frameless=True,
            hidden=True,
            transparent=True,
            shadow=False,        # 透明窗口上阴影会渲染成灰边
            on_top=True,
            resizable=False,
            easy_drag=False,     # 自己实现拖动
            focus=False,         # pywebview 也会加 WS_EX_NOACTIVATE
            background_color="#000000",
        )
        self._window.events.before_show += self._on_before_show
        self._window.events.shown += self._on_window_shown

    # ---------- 业务动作(presenter 调用) ----------

    def set_mode(self, mode: str, payload: dict | None = None) -> None:
        """推显示状态 —— Python → JS 的**唯一**显示状态入口。

        ``payload`` 序列化过去(和 ``push_levels`` 的电平数组同一个
        套路:json.dumps 过去,前端自己 JSON.parse)。
        """
        data = payload if isinstance(payload, dict) else {}
        if mode == "orb":
            # 回到 orb = 这一轮结束了:窗口不再"因为这一轮"留着。
            # 非常驻模式整个藏掉;常驻模式留着球(前端把胶囊收回球形态)。
            self._hide()
        elif mode == "recording":
            # 新一轮录音:先记下这一轮的注入落点,再亮窗口(不抢焦点)。
            self._snapshot_target_hwnd()
            self._show()
            # 主题/预览开关再推一遍:改完马上按热键时,推送可能还堵在
            # 路上。这些调用幂等,多推无害。
            self._call_js("setTheme", self._theme_mode())
            self._call_js("setPreviewEnabled", self._preview_enabled())
        self._state = mode
        self._last_payload = dict(data)
        self._call_js("setMode", mode, json.dumps(data, ensure_ascii=False))

    def set_theme(self, mode: str) -> None:
        self._call_js("setTheme", mode)

    def set_preview_enabled(self, enabled: bool) -> None:
        self._call_js("setPreviewEnabled", bool(enabled))

    def set_preview(self, text: str) -> None:
        """实时预览文字(录音过程中已识别出的内容)。"""
        self._last_preview = text
        if not self._preview_enabled():
            return
        self._call_js("setPreview", text)

    def push_levels(self, levels: list[float], elapsed: float) -> None:
        """30fps 电平 + 计时。

        推的是**绝对电平**(dBFS 映射后的 0..1),前端绝不做窗口内
        归一化。
        """
        if self._state != "recording":
            return
        self._call_js_pair("setLevels", json.dumps(levels),
                           "setElapsed", elapsed)

    def hide_orb(self) -> None:
        """把球收起来(切到无球形态时窗口整个隐藏)。"""
        self._hide_orb()

    def show_orb(self) -> None:
        """让球露脸(切回悬浮球形态)。"""
        self._schedule_show_orb()

    def destroy(self) -> None:
        """销毁窗口(退出时调)。"""
        if self._destroyed:
            return
        self._destroyed = True
        self._cancel_orb_timer()
        self._push_queue.put(None)
        try:
            self._window.destroy()
        except Exception as e:
            log.debug("销毁窗口失败: %s", e)

    # ---------- 菜单出口(入口组装时注入) ----------

    def set_panel_opener(self, fn: Callable[[], None] | None) -> None:
        self._panel_opener = fn

    def set_quit_handler(self, fn: Callable[[], None] | None) -> None:
        self._quit_handler = fn

    def set_qr_scanner_launcher(self, fn: Callable[[], None] | None) -> None:
        self._qr_launcher = fn

    def _open_panel(self) -> None:
        if self._panel_opener is None:
            return
        try:
            self._panel_opener()
        except Exception as e:
            log.debug("球菜单打开面板失败: %s", e)

    def _launch_qr_from_menu(self) -> None:
        log.info("球菜单「二维码扫描」点击进入(回调%s)",
                 "已注入" if self._qr_launcher else "**未注入**")
        if self._qr_launcher is None:
            return
        try:
            self._qr_launcher()
        except Exception as e:
            log.error("球菜单拉起二维码扫描失败: %s", e)

    def _quit_from_menu(self) -> None:
        if self._quit_handler is None:
            return
        try:
            self._quit_handler()
        except Exception as e:
            log.debug("球菜单退出失败: %s", e)

    # ---------- 配置读值 ----------

    def _theme_mode(self) -> str:
        """当前主题档位(auto / light / dark),读不到按 auto。"""
        try:
            mode = str(getattr(self._config, "theme", "auto")
                       or "auto").strip().lower()
        except Exception:
            return "auto"
        return mode if mode in ("auto", "light", "dark") else "auto"

    def _preview_enabled(self) -> bool:
        """要不要显示实时预览文字条,读不到按开。"""
        try:
            return bool(getattr(self._config, "show_preview", True))
        except Exception:
            return True

    def _orb_enabled(self) -> bool:
        """悬浮球形态开没开(配置项 ``ui_mode == "orb"``),读不到按开。

        这是 host 层对"球开不开"的**唯一**判断:初始露脸、终态停留后
        窗口藏不藏(_hide),全看它。界面形态四档里只有 orb 有球;
        resident / on_demand 都是无球形态。
        """
        try:
            mode = str(getattr(self._config, "ui_mode", "orb")
                       or "orb").strip().lower()
        except Exception:
            return True
        return mode == "orb"

    # ---------- 前端桥接 ----------

    def _push_loop(self) -> None:
        """串行推送循环:唯一碰 evaluate_js 的地方。"""
        while True:
            script = self._push_queue.get()
            if script is None:
                return
            try:
                self._window.evaluate_js(script)
            except Exception as e:
                log.debug("推送失败: %s", e)

    def _call_js(self, method: str, *args: Any) -> bool:
        """调用前端方法(前端可能还没加载完,同一表达式里判断再调)。"""
        if not self._gui_ready.is_set():
            return False
        payload = ", ".join(json.dumps(a, ensure_ascii=False) for a in args)
        script = (
            "(function(){try{"
            f"if(window.__capsule&&typeof window.__capsule.{method}==='function'){{"
            f"window.__capsule.{method}({payload});return true;}}"
            "}catch(e){return false;}return false;})()"
        )
        self._push_queue.put(script)
        return True

    def _call_js_pair(self, m1: str, a1: Any, m2: str, a2: Any) -> bool:
        """一次往返里调两个前端方法(电平 + 计时一起推,省一半开销)。"""
        if not self._gui_ready.is_set():
            return False
        p1 = json.dumps(a1, ensure_ascii=False)
        p2 = json.dumps(a2, ensure_ascii=False)
        script = (
            "(function(){try{"
            f"if(window.__capsule&&typeof window.__capsule.{m1}==='function'"
            f"&&typeof window.__capsule.{m2}==='function'){{"
            f"window.__capsule.{m1}({p1});window.__capsule.{m2}({p2});return true;}}"
            "}catch(e){return false;}return false;})()"
        )
        self._push_queue.put(script)
        return True

    def _on_frontend_ready(self) -> None:
        """前端就绪:放行推送,补推一次当前状态,并把窗口摆好。"""
        self._gui_ready.set()
        # 补推当前显示状态:前端就绪之前推的都被丢掉了(见 _call_js)
        self._call_js("setMode", self._state,
                      json.dumps(self._last_payload, ensure_ascii=False))
        self._call_js("setTheme", self._theme_mode())
        self._call_js("setPreviewEnabled", self._preview_enabled())
        # 前端得知道现在是"球形态"还是"胶囊形态"
        self._call_js("setOrbMode", self._orb_enabled())
        # 球的位置档位:V2 只有"记住的位置"这一档(可拖、持久化)
        self._call_js("setOrbPosition", "custom")
        if self._last_preview:
            self._call_js("setPreview", self._last_preview)
        if self._orb_enabled():
            # 常驻球:等前端把"球"那一小块报上来再露脸
            self._schedule_show_orb()

    def _on_before_show(self) -> None:
        """建窗的**最后一步**:掐掉 pywebview 的抢焦点 hack,并先设好样式。

        pywebview 对透明窗口有一个 hack(winforms 后端
        ``EdgeChrome.on_navigation_start``):页面**开始导航**时它会
        ``form.Show()`` + ``form.Activate()``。``form.Activate()`` 走
        WinForms 的 ``SetForegroundWindow``,``WS_EX_NOACTIVATE`` 拦不住
        —— 只有"根本不执行"才拦得住。

        定点修法:在 ``before_show`` 事件里把 ``window.transparent`` 置
        False。这个时机 BrowserForm/EdgeChrome 的构造函数**已经**按
        transparent=True 设好了 WebView2 的透明背景,而导航还没开始,
        on_navigation_start 读到的 transparent 已经是 False → 那段
        hack 不再执行。

        顺带把 ``WS_EX_NOACTIVATE`` / ``WS_EX_TOOLWINDOW`` 设上:
        ``shown`` 事件是异步触发的,等到那时再建样式就晚了半拍。
        这里是 pywebview 把 ``window.native`` 挂上的第一个时机。
        """
        self._window.transparent = False
        hwnd = self._native_handle()
        if hwnd:
            set_tool_window_style(hwnd)
            # 加 WS_EX_LAYERED:窗口支持逐像素 alpha,WebView2 的透明
            # 背景才能真的透出桌面。必须在窗口显示**之前**加。
            enable_layered_window(hwnd)

    def _on_window_shown(self) -> None:
        """窗口已创建:补挂 layered + 重传 surface,起守卫,还前台。

        这里还是**补挂 WS_EX_LAYERED 并重传 surface** 的唯一时机:
        pywebview 建 hidden 窗口的透明 hack(``Opacity=0 → Show() →
        Hide() → Opacity=1``)跑在 ``before_show`` 之后,会把 layered
        摘掉。不补的话,窗口退回 WinForms 的默认灰底。
        """
        hwnd = self._native_handle()
        if hwnd:
            set_tool_window_style(hwnd)
            self._ensure_transparent_surface(hwnd, force=True)
        if self._state == "recording":
            self._show()
        if self._orb_enabled() and self._gui_ready.is_set():
            self._schedule_show_orb()
        self._start_guard()
        self._restore_focus()

    # ---------- 启动守卫 ----------

    def _start_guard(self) -> None:
        """起守卫线程,盯住建窗后的副作用。"""
        if self._guard_started:
            return
        self._guard_started = True
        threading.Thread(target=self._guard_loop, name="ui-capsule-guard",
                         daemon=True).start()

    def _guard_loop(self) -> None:
        # 建窗后的头几秒用密集间隔盯建窗副作用,之后转入稀疏轮询
        # 常驻到退出。
        dense_until = time.monotonic() + GUARD_SECONDS
        last_fg = self._focus_restore
        next_hit_refresh = time.monotonic() + HIT_REFRESH_INTERVAL
        while not self._destroyed:
            hwnd = self._native_handle()
            if hwnd:
                fg = get_foreground_window()
                if fg and fg != hwnd:
                    last_fg = fg
                self._last_foreground = last_fg
                # 没人要求显示却露着脸 → 藏回去(pywebview 偷偷 Show 的残留)
                if (not self._window_should_be_visible()
                        and is_window_visible(hwnd)):
                    hide_window(hwnd)
                elif (fg == hwnd and last_fg and last_fg != hwnd
                      and not self._drag.is_dragging()
                      and not self._is_selecting_text()):
                    # 前台被胶囊占了 → 还回去。
                    #
                    # WebView2 的内容窗口属于 msedgewebview2.exe,跨进程
                    # 加不了 NOACTIVATE,鼠标点上去就会激活整棵窗口树。
                    # 归还前台不影响鼠标点击消息的投递。拖动期间除外:
                    # 焦点来回跳会拖垮 WebView2 的鼠标捕获;用户正在
                    # 文字条里选文本时也除外(抢了就复制不了)。
                    restore_foreground(last_fg)
                # 刷新命中图:窗口区域裁好之后,Windows 要等 DWM 重新
                # 合成一次才按新形状判命中。合成由"把窗口提到置顶组
                # 顶端"触发,所以定期刷一次。
                if time.monotonic() >= next_hit_refresh:
                    next_hit_refresh = time.monotonic() + HIT_REFRESH_INTERVAL
                    if is_window_visible(hwnd):
                        bring_to_top(hwnd)
            interval = (GUARD_INTERVAL if time.monotonic() < dense_until
                        else GUARD_SPARSE)
            time.sleep(interval)

    def _is_selecting_text(self) -> bool:
        return self._selecting_text

    def _on_selecting_text(self, selecting: bool) -> None:
        self._selecting_text = bool(selecting)

    def _restore_focus(self) -> None:
        """把前台窗口还给用户原来那个(还原两次,盖住 pywebview 的两下)。"""
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

    def _give_back_focus(self, target: int | None = None) -> None:
        """前台若是胶囊,还给它该在的窗口(这一轮的注入落点)。"""
        hwnd = self._native_handle()
        if not hwnd:
            return
        if target is None:
            target = self._target_hwnd
        if not target or target == hwnd:
            target = self._user_foreground()
        if not target or target == hwnd:
            return
        if get_foreground_window() == hwnd:
            restore_foreground(target)

    def _user_foreground(self) -> int:
        """用户当前所在的窗口(0 = 不知道)。"""
        hwnd = self._native_handle()
        last = getattr(self, "_last_foreground", 0)
        if last and last != hwnd:
            return last
        try:
            fg = get_foreground_window()
        except Exception:
            return 0
        if fg and fg != hwnd:
            return fg
        return 0

    def _snapshot_target_hwnd(self) -> None:
        """快照这一轮的注入落点(用户此刻所在的前台窗口)。

        前台**已经是胶囊自己**时**不覆盖**:那说明这一轮是从胶囊上
        开始的(点球 / 点 ✓),覆盖成胶囊就等于让文字贴到胶囊自己身上。
        """
        hwnd = self._native_handle()
        fg = get_foreground_window()
        if fg and fg != hwnd:
            self._target_hwnd = fg

    # ---------- 显隐 ----------

    def _window_should_be_visible(self) -> bool:
        """窗口此刻该不该露着(球已经露脸,或这一轮的胶囊正在显示)。"""
        return bool(getattr(self, "_orb_shown", False)) or self._want_visible

    def _show(self) -> None:
        """显示窗口(不抢焦点)。"""
        self._want_visible = True
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            set_tool_window_style(hwnd)
            # 透明底子必须在显示前就位:分层窗口没有 surface 时是一块黑
            self._ensure_transparent_surface(hwnd)
            # WebView2 的子窗口没有 NOACTIVATE:每次显示都重刷
            set_no_activate_tree(hwnd)
            show_window_no_activate(hwnd)
            bring_to_top(hwnd)
        except Exception as e:
            log.debug("显示窗口失败: %s", e)

    def _hide(self) -> None:
        self._want_visible = False
        if self._orb_enabled():
            # 常驻模式:窗口**不藏**(球还在那儿等着下一轮),
            # 胶囊由前端自己收回球。
            return
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            hide_window(hwnd)
        except Exception as e:
            log.debug("隐藏窗口失败: %s", e)

    # ---------- 手动复制(注入失败时的兜底出口) ----------

    def _copy_result(self, text: str) -> bool:
        """把 text 写进剪贴板,写成功就把胶囊收掉。

        写完要读回来核对:读回来不是同一份,就当没成功。
        """
        if not text or not text.strip():
            return False
        try:
            ok = clipboard.set_text(text)
        except Exception as e:
            log.debug("写剪贴板失败: %s", e)
            return False
        if not ok:
            log.warning("手动复制:写入剪贴板失败")
            return False

        time.sleep(CLIPBOARD_VERIFY_DELAY)
        try:
            current = clipboard.get_text()
        except Exception as e:
            log.debug("读回剪贴板失败: %s", e)
            current = None
        if current != text:
            log.warning("手动复制:读回校验不一致,当作失败")
            return False

        log.info("手动复制成功,收胶囊")
        self._hide()
        return True

    # ---------- 用户主动关掉错误提示 ----------

    def _dismiss_error(self) -> bool:
        """用户点了错误提示上的 ✕:收回胶囊并复位状态。

        返回**是否真的处理了** —— 不在"带原文的错误常驻态"时静默
        忽略(幂等):前端多发一次、或者别的状态误触发,都不会把
        正在录的一轮收掉。

        收回分两种模式:
        - 常驻球:窗口留着(球还在那儿等下一轮),胶囊收回成球形态
          的动效在前端做(见 capsule.js 的 onDismissClick);
        - 非常驻:窗口整个藏掉,回原样。
        """
        try:
            handled = bool(self._presenter.on_dismissed())
        except Exception as e:
            log.debug("关闭错误提示失败: %s", e)
            return False
        if not handled:
            return False
        # 非常驻:_hide() 藏掉窗口;常驻:只清掉"这一轮的胶囊还该
        # 显示"的标记(球还在),胶囊由前端收回成球。
        self._hide()
        return True

    # ---------- 窗口命中区域(SetWindowRgn) ----------

    def _on_update_regions(self, payload: Any) -> None:
        """前端上报的可见内容矩形 → 裁掉透明空白区的命中区。

        前端**第一次**上报之前不裁(保持整窗):那时还没量准,贸然裁
        会把可见内容切掉。
        """
        rects = _parse_regions(payload)
        if rects is None:
            return
        hwnd = self._native_handle()
        if not hwnd:
            return
        # 窗口形状裁好了 → 球可以露脸了
        self._orb_regions_ready = True
        # 透明底子也在这时上传:窗口尺寸到这时候已经定了
        self._ensure_transparent_surface(hwnd)

        scale = self._scale()
        # 裁形:
        #   胶囊/球 -> 矩形
        #   文字条  -> 14px 圆角矩形
        #   浮层    -> 12px 圆角矩形
        #
        # 胶囊和球都按**矩形**裁,不按"猜出来的形态"分圆/椭圆:
        # 状态切换和形状变化不是同一拍 —— 收回动画期间状态先切、
        # 形状后变,矩形不会切出怪形状。
        #
        # 形状那一项**必须就是**列表里的那项(set_window_regions 按
        # 坐标相等来认),不能另算一份。
        ellipses: set[tuple[int, int, int, int]] = set()
        round_rects: dict[tuple[int, int, int, int], int] = {}

        def expand(rect, margin):
            x, y, w, h = rect
            return (
                int(round(x * scale)) - margin,
                int(round(y * scale)) - margin,
                int(round((x + w) * scale)) + margin,
                int(round((y + h) * scale)) + margin,
            )

        physical: list[tuple[int, int, int, int]] = []
        for key in _REGION_KEYS:
            rect = rects.get(key)
            if rect is None:
                continue
            x, y, w, h = rect
            if w <= 0 or h <= 0:
                continue        # 隐藏的块不参与并集
            piece = expand(rect, REGION_MARGIN)
            if key == "overlay":
                round_rects[piece] = int(round(OVERLAY_RADIUS_PX * scale))
            elif key == "textbar":
                round_rects[piece] = int(round(TEXTBAR_RADIUS_PX * scale))
            physical.append(piece)

        if not physical:
            # 都隐藏(正常不会发生):裁成 1×1 的最小区域
            physical = [(0, 0, 1, 1)]

        log.debug("裁窗口区域: %s (圆角=%s)", physical, round_rects)
        try:
            set_window_regions(hwnd, physical, ellipses=ellipses,
                               round_rects=round_rects)
        except Exception as e:
            log.debug("裁窗口区域失败: %s", e)

        # 换了窗口区域之后,Windows 的命中图不会自动跟着更新:
        # 只有让 DWM 重新合成一次(把窗口提到置顶组顶端)命中才生效。
        bring_to_top(hwnd)

    def _ensure_transparent_surface(self, hwnd: int, force: bool = False) -> None:
        """给分层窗口上传"几乎全透明"的 surface —— 真透明的底子。

        只在窗口尺寸变化时重传:重传会吞掉 WebView2 的画面。

        **两步的顺序不能反**:必须先把 WS_EX_LAYERED 挂上,再传 surface。
        """
        rect = get_window_rect(hwnd)
        if not rect:
            return
        left, top, right, bottom = rect
        size = (right - left, bottom - top)
        if size[0] <= 0 or size[1] <= 0:
            return
        if self._surface_size == size and not force:
            return
        try:
            enable_layered_window(hwnd)
            if upload_transparent_surface(hwnd):
                self._surface_size = size
                log.info("已上传透明 surface: %sx%s", size[0], size[1])
        except Exception as e:
            log.info("上传透明 surface 失败: %s", e)

    # ---------- 尺寸与位置 ----------

    def _window_size(self) -> tuple[int, int] | None:
        """窗口当前的**物理**像素尺寸。

        用真实尺寸而不是建窗时请求的尺寸:WinForms 的 AutoScale 会
        把客户区再缩一次,请求值和实际值对不上。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return None
        rect = get_window_rect(hwnd)
        if rect is None:
            return None
        width = rect[2] - rect[0]
        height = rect[3] - rect[1]
        if width <= 0 or height <= 0:
            return None
        return width, height

    def _current_scale(self) -> float:
        """窗口所在显示器的缩放系数。

        用 ``GetDpiForWindow``:球可能被拖到另一块缩放率不同的屏上,
        按主屏的缩放换算位置会算飞。
        """
        try:
            import ctypes

            hwnd = self._native_handle()
            if hwnd:
                dpi = ctypes.windll.user32.GetDpiForWindow(ctypes.c_void_p(hwnd))
                if dpi:
                    return max(1.0, dpi / 96.0)
        except Exception:
            pass
        return self._scale()

    def _scale(self) -> float:
        """主显示器缩放系数。"""
        try:
            import ctypes

            dpi = ctypes.windll.user32.GetDpiForSystem()
            if dpi:
                return max(1.0, dpi / 96.0)
        except Exception:
            pass
        return 1.0

    # ---------- 常驻悬浮球 ----------

    def _schedule_show_orb(self) -> None:
        """等前端把"球"那一小块报上来再露脸。

        已经排上队就别再排:每次都"取消旧的、排个新的"的话,
        这个 0.3s 的定时器永远等不到。
        """
        with self._orb_lock:
            if getattr(self, "_orb_timer", None) is not None:
                return
            timer = threading.Timer(ORB_SHOW_DELAY, self._show_orb)
            timer.daemon = True
            self._orb_timer = timer
        timer.start()

    def _cancel_orb_timer(self) -> None:
        """作废"稍后露脸"的定时器。"""
        with self._orb_lock:
            timer, self._orb_timer = getattr(self, "_orb_timer", None), None
        if timer is not None:
            timer.cancel()

    def _show_orb(self) -> None:
        """把球亮出来:摆好位置 + 显示窗口(不抢焦点)。"""
        self._cancel_orb_timer()
        if not self._place_orb():
            return
        # 先记"该露着":守卫在同一瞬间看到"没人要求显示"会把球藏回去
        self._orb_shown = True
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            set_tool_window_style(hwnd)
            set_no_activate_tree(hwnd)
            show_window_no_activate(hwnd)
            bring_to_top(hwnd)
        except Exception as e:
            log.debug("显示悬浮球失败: %s", e)

    def _hide_orb(self) -> None:
        """把球收回去。"""
        self._cancel_orb_timer()
        self._orb_shown = False
        if self._want_visible:
            # 正在录音:这一轮的胶囊还得留着
            return
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            hide_window(hwnd)
        except Exception as e:
            log.debug("隐藏悬浮球失败: %s", e)

    def _place_orb(self) -> bool:
        """把窗口摆到球该在的位置,返回**摆成功没有**。

        摆到**记忆的位置**(没拖过就是工作区右下角)。球固定待在窗口
        的**底边居中**处,所以"摆窗口"就是"摆球":先算出球该在的
        屏幕坐标,再反推窗口左上角。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return False

        size = self._window_size()
        if size is None:
            return False
        win_w, win_h = size

        mon = self._saved_orb_monitor()
        if mon is None:
            mon = primary_monitor()
        if mon is None:
            return False
        area = mon.work_area
        scale = mon.scale

        position = orb_position_from_geometry(
            self._saved_orb_geometry(), area, scale, win_w, win_h)
        if position is None:
            # 还没拖过:给个合理的默认值(工作区右下角)
            ox, oy = default_orb_position(area, scale)
            position = orb_window_position(ox, oy, win_w, win_h, scale, area)
        if position is None:
            return False
        try:
            move_window_to(hwnd, int(position[0]), int(position[1]))
            return True
        except Exception as e:
            log.debug("摆悬浮球位置失败: %s", e)
            return False

    # ---------- 拖动 ----------

    def set_draggable(self, enabled: bool) -> None:
        """开/关本窗口的拖拽能力。

        **新窗口接入拖拽的公开入口**:调 ``set_draggable(True)`` 之后,
        前端发来的拖拽指令就都走统一的拖拽会话(焦点冻结 + 松手记
        位置都在 ``DragSession`` 里),窗口自己不用再实现一遍。

        当前这个窗口恒可拖(位置档位已退役),所以没人调它 —— 形状
        留着,是为了下一个窗口不用再往 ``VoiceCapsule`` 里加一套。
        """
        self._drag.set_enabled(bool(enabled))

    def _save_orb_position(self) -> None:
        """记住悬浮球的位置(相对所在工作区的逻辑偏移)。

        存的是**球**的偏移,不是窗口的:球是用户真正盯着的那一小块。
        存相对量而不是绝对坐标:换缩放率、换显示器都不飞。

        这是拖拽会话的"保存位置"回调 —— 什么时候存由 DragSession 决定
        (拖过才存),这里只管怎么存。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        rect = get_window_rect(hwnd)
        if rect is None:
            return
        mon = self._monitor_at_window()
        if mon is None:
            return
        ox, oy = orb_screen_position(rect[0], rect[1], rect[2] - rect[0],
                                     rect[3] - rect[1], mon.scale)
        geometry = encode_offset(ox, oy, mon.work_area, mon.scale)
        try:
            self._config.orb.geometry = geometry
            self._config.orb.monitor = mon.device
            self._config.save()
        except Exception as e:
            log.debug("保存悬浮球位置失败: %s", e)
            return
        log.debug("记住悬浮球位置: %s @ %s", geometry, mon.device)

    def _start_recording_from_orb(self) -> None:
        """点悬浮球开始录音。

        ⚠ **焦点陷阱**:鼠标点在 WebView2 的子窗口上会激活整棵窗口树,
        前台被胶囊抢走。注入靠"粘贴到前台窗口",所以**先把前台还给
        用户**,再开始录音 —— 否则这一轮识别完的文字会贴到胶囊自己
        身上,而不是用户的输入框里。那是功能级的致命 bug,不是观感问题。
        """
        target = self._user_foreground()
        if target:
            self._give_back_focus(target)
            # 这一轮的注入落点:先记下来,别等开始录音时再猜
            self._target_hwnd = target
        try:
            ok, hint = self._presenter.try_start_recording()
        except Exception as e:
            log.debug("点悬浮球开始录音失败: %s", e)
            return
        if not ok and hint:
            # 模型还在加载:controller 拒绝了,但"点了没反应"在用户眼里
            # 就是"球坏了"。给一句提示,别让人对着沉默发懵。
            self._call_js("setOrbHint", hint)

    def _saved_orb_geometry(self) -> str:
        """上次记忆的悬浮球位置(``"dx,dy"``)。"""
        try:
            return str(getattr(self._config.orb, "geometry", "") or "")
        except Exception:
            return ""

    def _saved_orb_monitor(self) -> Any:
        """上次记忆位置时所在的那块显示器(设备名对不上就返回 None)。"""
        try:
            device = str(getattr(self._config.orb, "monitor", "") or "")
        except Exception:
            return None
        if not device:
            return None
        return monitor_by_device(device)

    def _monitor_at_window(self) -> Any:
        """窗口当前所在的那块显示器(拿窗口中心点去问)。"""
        rect = get_window_rect(self._native_handle())
        if rect is None:
            return None
        cx = (rect[0] + rect[2]) // 2
        cy = (rect[1] + rect[3]) // 2
        mon = monitor_at_point(cx, cy)
        if mon is None:
            mon = monitor_at_point(rect[0], rect[1])
        return mon

    def _focus_textbar(self) -> None:
        """把前台与键盘焦点送给文字条(文字条被按下时)。"""
        hwnd = self._native_handle()
        if hwnd:
            restore_foreground(hwnd)
        focus_webview(self._window)

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
