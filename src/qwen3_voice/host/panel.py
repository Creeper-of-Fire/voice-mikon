"""设置面板窗口(pywebview + WebView2)。

面板是**配置窗口**:状态、后端健康、模型/后端/主题/悬浮球/预览的开关、
退出。录音过程的反馈(实时预览、电平条、计时)全在胶囊窗口那边
(见 host/capsule.py)—— 面板关不关都不影响录音体验。

前端是纯静态 HTML/CSS/JS(``static/panel.{html,css,js}``),零框架零构建。

面板窗口和胶囊**相反**,有三条不一样的地方,都是功能决定的:

1. **普通不透明**:窗口底色是实色(``background_color``),不是透明窗。
   所以可以用 box-shadow、圆角卡片 —— 胶囊那边不行(透明窗口上阴影
   会渲染成一条灰带,见 capsule.css)。
2. **可激活**:面板是要拿焦点做交互的(点分段、点开关)。胶囊相反,
   它抢了焦点,识别出来的文字就没有落点了(见 capsule.py 顶部"坑 1")。
   所以这里**不**加 ``WS_EX_NOACTIVATE``,只保留"不进任务栏/Alt+Tab"
   的 ``WS_EX_TOOLWINDOW``。
3. **默认不常驻**:托盘菜单或球菜单才打开,关掉只是隐藏,不退出程序。

前端三件套与 fit/拖动/几何记忆的逻辑来自 V1 的 ``ui/panel.py``
(退役前的那一版),但伺候旧世界的部分没搬:位置三档 UI、
``_EventPump``、旧 controller 接口都不在了(见 REFACTOR_SPEC 的白名单)。

Python 与前端之间的桥接契约:

  Python → JS   window.__panel.setState / setHealth / setModel / setBackend
                / setTheme / setPreviewEnabled / setOrbEnabled / setStandby
                / setNotice / setHotkey
  JS → Python   window.pywebview.api.ready / switch_model / switch_backend
                / set_theme / set_preview / set_orb / set_standby
                / close_panel / quit_app
                / begin_drag / move_window / end_drag

  ⚠ 拖动那三个(begin_drag / move_window / end_drag)是"拖拽会话"的实现
    细节,必须成对出现(按下 → 拖 → 松手),业务侧别单独调。

两个容易踩的坑,都在下面代码里处理了:

1. ``evaluate_js`` 在 GUI 还没建好时会阻塞 20 秒才抛异常。
   所以所有前端调用都过 ``_frontend_ready`` 门控,前端就绪后才推;
   推送一律走**串行队列**(WebView2 是 STA COM 对象,多线程并发
   ``evaluate_js`` 会互相踩掉 —— 见 REFACTOR_SPEC 血泪条款 2)。
2. pywebview 建窗时给的初始尺寸会被 WinForms 的 AutoScale 再缩一次,
   内容底部几个像素会被裁掉。``_fit_window`` 自己量一次内容高度再
   resize 到刚好(``resize()`` 走的是另一条路径,尺寸是准的)。
"""

from __future__ import annotations

import json
import queue
import threading
import time
from pathlib import Path
from typing import Any

from ..logging_setup import get_logger
from ..win32.api import (
    GWL_EXSTYLE,
    WS_EX_APPWINDOW,
    WS_EX_TOOLWINDOW,
    get_foreground_window,
    get_window_rect,
    move_window_to,
    resize_window_no_activate,
    restore_foreground,
    user32,
)
from .drag import DragSession, window_placement

log = get_logger("host.panel")

# 前端静态资源目录(和胶囊的静态资源同目录 —— 都是 host 层的窗口资产)
STATIC_DIR = Path(__file__).resolve().parent / "static"

# 窗口尺寸(逻辑像素 = CSS 像素)
#
# 宽度固定 320:配置行的分段控件("本地/网络""0.6B/1.7B""自动/白天/黑夜")
# 加上标签,320 刚好不挤。
#
# 高度是**下限**:真正的尺寸由 _fit_window 量内容决定
# (状态区 + 后端 + 模型 + 主题 + 悬浮球 + 待机 + 预览 + 页脚)。
# 内容比这个高时窗口自己长高,比这个矮时留白 —— 面板.css 里 .panel 用的是
# min-height,窗口短了卡片就撑不满,不好看。
BASE_W = 320
BASE_H = 340

# 页面加载完成前的窗口底色(占位):跟最终会显示的那套主题一致,
# 免得深色主题下闪一下白。真正的配色由 CSS 决定,这里只管"开场"。
# 这两个值必须和 panel.css 里 :root 的 --bg 一致
# (浅色 #fff / 深色 #0f0f11),不然开场那一下会闪出两种底色。
WINDOW_BG_LIGHT = "#ffffff"
WINDOW_BG_DARK = "#0f0f11"

# 窗口离工作区边缘的最小留白(物理像素)。
#
# 面板是**不透明**窗口,不像胶囊那样有"透明空白会吃掉底下点击"的顾虑,
# 但拖到屏幕外还是找不回来 —— 所以仍然夹在屏幕内,只是不用夹得那么死。
EDGE_MARGIN = 8


def system_prefers_dark() -> bool:
    """系统是不是深色主题。

    读注册表 ``HKCU\\...\\Themes\\Personalize`` 的 ``AppsUseLightTheme``:
    0 = 深色,1 = 浅色。

    只在"跟随系统(auto)"这一档用来选页面还没加载时的窗口底色 ——
    深色系统配白底会闪一下白。真正的配色由 CSS 决定:强制档用
    ``data-theme`` 属性,auto 档用 ``prefers-color-scheme`` 媒体查询。
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


def theme_background_color(theme: str) -> str:
    """按主题档位给建窗底色。

    底色只是"页面加载前的那一瞬间" —— 后面的配色由 CSS 决定,
    所以这里跟 CSS 的 --bg 保持一致就行,不用跟着主题变来变去。
    """
    if theme == "light":
        return WINDOW_BG_LIGHT
    if theme == "dark":
        return WINDOW_BG_DARK
    return WINDOW_BG_DARK if system_prefers_dark() else WINDOW_BG_LIGHT


class VoicePanel:
    """设置面板窗口。"""

    def __init__(self, presenter: Any = None, config: Any = None,
                 js_api: Any = None, geometry: str = "",
                 focus_restore: int = 0):
        """``presenter`` 是面板 presenter(翻译 core 的快照/通知成前端调用);
        ``config`` 提供主题/预览/悬浮球/窗口位置记忆;
        ``js_api`` 是暴露给前端的桥接对象(见 apps/panel/presenter.py);
        ``geometry`` 是上次记住的窗口位置("x,y,w,h",逻辑像素)。

        pywebview 建 hidden 窗口时会偷偷 Show 一次(它的透明 hack),
        那一下会把窗口激活 —— 程序启动就抢走用户的键盘焦点,正在打字
        的用户会丢字。所以窗口建好后要把前台还回去。
        """
        self._presenter = presenter
        self._config = config
        self._focus_restore = focus_restore

        # 窗口已创建(可以 show/hide)/ 前端已就绪(可以 evaluate_js)
        self._gui_ready = threading.Event()
        self._frontend_ready = threading.Event()
        self._destroyed = False
        # 期望可见性:GUI 就绪前只记录意图
        self._visible = False
        # fit 是耗时的,别让两个同时跑
        self._fit_lock = threading.Lock()
        # 窗口位置(逻辑像素)。None 表示还没定,窗口就绪后摆到右下角。
        self._pos_x: int | None = None
        self._pos_y: int | None = None

        # 推送队列:所有 Python → JS 的调用串行过这一条线程。
        # WebView2 是 STA COM 对象,多线程并发 evaluate_js 会互相踩
        # (推送丢失、状态错乱、COM 对象分离)—— 推送必须串行。
        self._push_queue: "queue.Queue[str | None]" = queue.Queue()
        self._push_thread = threading.Thread(
            target=self._push_loop, name="ui-panel-push", daemon=True)
        self._push_thread.start()

        # 拖动会话:按住 → 拖 → 松手的状态、焦点冻结、松手记位置都在
        # DragSession 里(见 host/drag.py)。窗口层只提供它需要的几样东西。
        #
        # 摆位策略也在这里注入:面板用**通用**那套(整个窗口夹进工作区),
        # 不是胶囊的"球"那套 —— 球在窗口底边居中、纵向还要给展开后的
        # 胶囊留位,那是固定尺寸透明窗的约束,套到普通面板窗口上会把
        # 窗口横向甩出屏幕(球的横向留白按球宽算,面板窗口比球宽得多)。
        self._drag = DragSession(
            window_handle=self._native_handle,
            window_scale=self._current_scale,
            window_size=self._window_size,
            save_geometry=self._save_geometry,
            restore_focus=self._restore_focus_after_drag,
            placement=window_placement(
                window_handle=self._native_handle,
                window_scale=self._current_scale,
                window_size=self._window_size,
                margin=EDGE_MARGIN),
        )

        import webview

        html = STATIC_DIR / "panel.html"
        saved_x, saved_y = self._parse_geometry(geometry)
        self._window = webview.create_window(
            "Qwen3 语音输入",
            url=html.as_uri(),
            js_api=js_api,
            width=BASE_W,
            height=BASE_H,
            x=saved_x,
            y=saved_y,
            frameless=True,       # 无系统边框:标题栏是前端自绘的
            hidden=True,          # 默认不常驻,托盘/球菜单才打开
            easy_drag=False,      # 自己实现拖动,才能记住位置
            on_top=True,
            resizable=False,
            focus=True,           # 面板要能拿焦点做交互(与胶囊相反)
            background_color=theme_background_color(
                str(getattr(config, "theme", "auto") or "auto")),
        )
        self._window.events.before_show += self._on_before_show
        self._window.events.shown += self._on_window_shown

    # ---------- 前端推送(presenter 调用) ----------

    def set_state(self, state: str) -> None:
        """当前状态(starting / idle / recording / processing)。"""
        self._call_js("setState", str(state))

    def set_health(self, status: str, reason: str = "") -> None:
        """后端健康(loading / ready / failed + 失败原因)。"""
        self._call_js("setHealth", str(status), str(reason or ""))

    def set_model(self, tag: str) -> None:
        """当前模型规格(网络后端没有本地模型 → 空串)。"""
        self._call_js("setModel", str(tag or ""))

    def set_backend(self, kind: str) -> None:
        """当前识别后端(local / minimax)。"""
        self._call_js("setBackend", str(kind or "local"))

    def set_theme(self, mode: str) -> None:
        """主题档位(auto / light / dark)。"""
        self._call_js("setTheme", str(mode or "auto"))

    def set_preview_enabled(self, enabled: bool) -> None:
        """要不要显示实时预览文字条。"""
        self._call_js("setPreviewEnabled", bool(enabled))

    def set_orb_enabled(self, enabled: bool) -> None:
        """常驻悬浮球开不开。"""
        self._call_js("setOrbEnabled", bool(enabled))

    def set_standby(self, mode: str) -> None:
        """悬浮球待机策略(resident / on_demand)。"""
        self._call_js("setStandby", str(mode or "resident"))

    def set_notice(self, text: str) -> None:
        """操作提示(切换的进展/拒绝/失败)。"""
        self._call_js("setNotice", str(text or ""))

    def set_hotkey(self, text: str) -> None:
        """当前热键(页脚里显示)。"""
        self._call_js("setHotkey", str(text or ""))

    # ---------- 前端桥接 ----------

    def _push_loop(self) -> None:
        """串行推送循环:唯一碰 evaluate_js 的地方(推送线程)。"""
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
        if not self._frontend_ready.is_set():
            return False
        payload = ", ".join(json.dumps(a, ensure_ascii=False) for a in args)
        script = (
            "(function(){try{"
            f"if(window.__panel&&typeof window.__panel.{method}==='function'){{"
            f"window.__panel.{method}({payload});return true;}}"
            "}catch(e){return false;}return false;})()"
        )
        self._push_queue.put(script)
        return True

    def on_frontend_ready(self) -> None:
        """前端就绪(presenter 转达的):放行推送,并让 presenter 补推一次全量。

        fit 放这里是因为 DOM ready 之后 evaluate_js 一定能执行
        (GUI 就绪那一刻页面可能还没加载完,那时的 fit 会失败,
        靠这里兜底)。fit 是幂等的:量内容高度、resize 到刚好,
        重复执行结果相同。
        """
        self._frontend_ready.set()
        presenter = self._presenter
        if presenter is not None:
            try:
                presenter.push_full_state()
            except Exception as e:
                log.debug("面板首推失败: %s", e)
        try:
            self._fit_window()
        except Exception as e:
            log.debug("前端 ready 后 fit 失败: %s", e)

    # ---------- 显隐 ----------

    def show(self) -> None:
        """显示面板(不常驻,只在托盘/球菜单打开时调)。"""
        self._visible = True
        self._schedule_apply()

    def hide(self) -> None:
        """隐藏面板(不退出程序)。"""
        self._visible = False
        if self._gui_ready.is_set():
            # 隐藏前把真实位置记下来 —— 用户可能拖完就直接关面板,
            # 之后直接退出程序的话就再没机会读了
            self._save_geometry()
        self._schedule_apply()

    def toggle(self) -> None:
        """显示/隐藏切换(托盘菜单项用)。"""
        if self._visible:
            self.hide()
        else:
            self.show()

    def is_visible(self) -> bool:
        """当前是不是"想让它露着"。"""
        return self._visible

    def _apply_visibility(self) -> None:
        """把期望的可见性/位置/尺寸落到窗口上(后台线程里跑)。

        show()/hide() 只记意图,落地统一在这里 —— 并且全程异常兜底:
        窗口层的异常绝不能把调用方(托盘线程/推送线程)带崩。
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
        # 前端 DOM ready 后还会再 fit 一次
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
                         name="ui-panel-layout", daemon=True).start()

    # ---------- 尺寸自适应 ----------

    def _fit_window(self) -> None:
        """把窗口调整到刚好能容纳内容。

        pywebview 建窗时给的初始尺寸会被 WinForms 的 AutoScale 再缩一次
        (实测:请求 320x340 逻辑像素,实际客户区会小一圈),底部几个
        像素的内容就被裁掉了。但 ``resize()`` 走的是另一条路径,尺寸是准的。

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

    # ---------- 几何记忆 ----------

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

        这是拖拽会话的"保存位置"回调 —— 什么时候存由 DragSession 决定
        (拖过才存),这里只管怎么存。
        """
        self._sync_position_from_window()
        if self._pos_x is None or self._pos_y is None:
            return
        if self._config is None:
            return
        try:
            self._config.panel_geometry = (
                f"{self._pos_x},{self._pos_y},{BASE_W},{BASE_H}")
            self._config.save()
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
        scale = self._current_scale()
        self._pos_x = int(round(rect[0] / scale))
        self._pos_y = int(round(rect[1] / scale))

    # ---------- 拖动 ----------

    def begin_drag(self) -> None:
        """用户开始拖(按住标题栏)。

        这一下会冻结拖拽会话的状态,松手时统一收尾(记位置)。
        """
        self._drag.begin()

    def move_window(self, x: float, y: float) -> None:
        """窗口左上角 → (x, y) 逻辑像素。

        前端每次都从 mousedown 起点算绝对目标发过来,不发增量 ——
        某一次调用丢了或慢了,下一次直接覆盖,天然不漂。

        摆位交给拖拽会话(注入的是**通用**策略:把窗口夹回工作区),
        会话内部用 Win32 SetWindowPos 直接设(物理 = 逻辑 × 当前缩放),
        不走 pywebview 的 move()(内部还有一层换算与消息,更慢)。

        窗口位置的记账由会话在松手时统一做(``_save_geometry`` 会先读
        窗口的真实位置再存)—— 夹取过了还记目标值的话,账本和窗口就
        对不上了。
        """
        self._drag.move(x, y)

    def end_drag(self) -> None:
        """用户松手:收尾(记位置)。

        位置记忆由拖拽会话决定(拖过才存),这里只转发。
        """
        self._drag.end()

    def _restore_focus_after_drag(self) -> None:
        """拖完把焦点还给用户当前所在的窗口。

        胶囊需要这个(它抢了焦点,文字就没落点了),**面板不需要**:
        面板就是要拿焦点做交互的,拖完焦点留在面板上才对。所以这里是
        空实现 —— 拖拽会话统一要求提供这个回调,给了空的就够了。
        """

    # ---------- 窗口尺寸与句柄 ----------

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

        用 ``GetDpiForWindow``:面板可能被拖到另一块缩放率不同的屏上,
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
        return 1.0

    def _native_handle(self) -> int:
        """底层 Win32 窗口句柄(0 = 还没建好)。"""
        try:
            native = getattr(self._window, "native", None)
            if native is None:
                return 0
            return int(native.Handle.ToInt64())
        except Exception:
            return 0

    # ---------- 生命周期 ----------

    def _on_before_show(self) -> None:
        """窗口即将显示:抢先改扩展样式。

        面板**保留可激活性**(不加 WS_EX_NOACTIVATE —— 它就是要拿焦点
        做交互的),只去掉任务栏/Alt+Tab 里的位置(WS_EX_TOOLWINDOW)。
        """
        hwnd = self._native_handle()
        if hwnd:
            self._set_panel_style(hwnd)

    def _on_window_shown(self) -> None:
        """窗口已创建:放行 GUI 就绪,再在后台线程里落地可见性。

        这里**只做样式**,不做显示/摆位/尺寸 —— shown 是 pywebview 在
        UI 线程里同步触发的事件(隐藏窗口也会被透明 Show 一次从而触发),
        在这里再调 window.show() 是 UI 线程重入,可能把窗口搞挂。
        显示/摆位/fit 由 ``_apply_visibility`` 负责(有异常兜底)。
        """
        hwnd = self._native_handle()
        if hwnd:
            self._set_panel_style(hwnd)
        self._gui_ready.set()
        # pywebview 建 hidden 窗口时会偷偷 Show 一次,那一下会把窗口激活 ——
        # 把前台还给用户原来那个窗口
        self._restore_focus()
        # 首次落地(可见性 + 尺寸)扔到后台线程 —— 这里正处在 UI 线程
        # 的事件回调里,再调 window.show() 是重入,可能把窗口搞挂。
        self._schedule_apply()

    @staticmethod
    def _set_panel_style(hwnd: int) -> None:
        """把窗口改成"工具窗口",但**保留可激活**。

        WS_EX_TOOLWINDOW:不在任务栏、不在 Alt+Tab(和 V1 面板一致)。
        WS_EX_APPWINDOW 必须去掉,否则上面那条会被抵消。
        WS_EX_NOACTIVATE **不加** —— 面板要能拿焦点做交互,这一点和
        胶囊正好相反(胶囊抢了焦点,识别出来的文字就没有落点了)。
        """
        if not hwnd:
            return
        try:
            ex = user32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE)
            new_ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW
            user32.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new_ex)
        except Exception as e:
            log.debug("设置面板窗口样式失败: %s", e)

    def _restore_focus(self) -> None:
        """把前台窗口还给用户原来那个(还原两次,盖住 pywebview 的两下)。"""
        self._restore_focus_once()
        threading.Thread(target=self._restore_focus_later, name="ui-panel-focus",
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

    def destroy(self) -> None:
        """销毁窗口(退出时调)。"""
        if self._destroyed:
            return
        self._destroyed = True
        self._push_queue.put(None)
        try:
            self._save_geometry()
        except Exception:
            pass
        try:
            self._window.destroy()
        except Exception as e:
            log.debug("销毁面板窗口失败: %s", e)
