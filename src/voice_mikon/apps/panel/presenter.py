"""设置面板的 presenter:把 core 的状态/通知翻译成面板前端的调用。

面板 presenter 只做**翻译**,不做判断:

* 订阅 core 的 ``AppState`` 快照(快照优先 —— 新订阅者立刻收到当前快照),
  把状态、后端健康、模型、后端翻译成前端的具名 setter;
* 订阅通知流(NotificationHub),把**面板发起的操作**的进展/拒绝/失败
  翻译成面板的提示语;
* 承接前端发来的动作(切模型、切后端、改配置、退出)。

"切模型/切后端为什么被拒"这件事有个坑:core 的 ``switch_model`` /
``switch_backend`` **不返回 CommandResult**(现行签名是 ``-> None``,
切换动作在后台线程里做),拒绝与失败只走**通知流**。所以面板想给用户
反馈,必须订阅通知,而不是等一个返回值 —— 详见
``_NOTICE_KINDS``。

配置写回也归这一层:面板改的是配置(config.theme / show_preview /
ui_mode),主题还要**立即推给胶囊** —— 面板和胶囊共用
一套主题,面板改了胶囊不跟着换,用户会觉得"两个窗口不是一套东西"。
界面形态(四档单选)同理:面板把球关掉,胶囊那边要当场把球收起来,
不然用户点了档位什么都没发生(V1 就是即时的)。胶囊那侧的推送由装配层
注入(``theme_broadcaster`` / ``ui_mode_broadcaster``),本层不认识胶囊。
"""

from __future__ import annotations

import threading
from typing import Any, Callable

from ...core.state import AppState
from ...logging_setup import get_logger
from ...notify import Notification, NotificationHub, notify

log = get_logger("apps.panel")

# 主题档位(auto / light / dark)—— 与 config.THEME_MODES 一致。
# 这里抄一份而不是 import config:presenter 只跟"档位名字"打交道,
# 不该把整个配置模块拖进来。
THEME_MODES = ("auto", "light", "dark")

# 界面形态四档(悬浮球/常驻/按需/无界面)—— 与 config.UI_MODES 一致。
# 同 THEME_MODES 的理由:这里抄一份而不是 import config,presenter
# 只跟"档位名字"打交道。headless 档面板上 disabled(只能 config 手动开),
# 但校验仍认它 —— 防 config 里真配了 headless 时读值方法乱归一。
UI_MODES = ("orb", "resident", "on_demand", "headless")

# 这些通知的 kind 会变成面板的提示语。
#
# 只收**面板发起的操作**这一类(切模型/切后端):它们的进展、拒绝、失败
# 都需要用户看见 —— "点了没反应"在用户眼里就是"程序坏了"。
# 别的通知(录音失败、注入失败…)归胶囊的 presenter,面板不重复显示。
_NOTICE_KINDS = frozenset({
    "model_switching",
    "model_switched",
    "model_switch_failed",
    "backend_switching",
    "backend_switched",
    "backend_switch_failed",
})


class _PanelApi:
    """暴露给前端 JS 的桥接对象。

    单独一个类:pywebview 的 ``get_functions`` 会遍历公开属性,遇到
    非 callable 的对象还会**递归**下去 —— 直接把 presenter 交出去,
    内部属性都会被暴露到网页里。这里只留方法,引用用下划线开头
    (pywebview 会跳过)。
    """

    def __init__(self, presenter: "PanelPresenter") -> None:
        self._presenter = presenter

    def ready(self) -> None:
        """前端 DOM 就绪。"""
        self._presenter.on_frontend_ready()

    def switch_model(self, tag: str) -> None:
        """切换模型规格(0.6B / 1.7B)。

        只对**本地**后端有意义;网络后端下会被拒绝,拒绝原因由
        presenter 从通知流里取,显示成面板提示语。
        """
        self._presenter.switch_model(tag)

    def switch_backend(self, kind: str) -> None:
        """切换识别后端(local / minimax)。

        录音/识别中会被拒绝 —— 这一轮的最终位引擎持有旧后端的引用,
        中途换掉会出错或丢结果。拒绝时面板显示那句提示。
        """
        self._presenter.switch_backend(kind)

    def set_theme(self, mode: str) -> None:
        """切换主题档位(auto / light / dark)。"""
        self._presenter.set_theme(mode)

    def set_preview(self, enabled: bool) -> None:
        """开关录音时的实时预览文字条。"""
        self._presenter.set_preview(bool(enabled))

    def set_ui_mode(self, mode: str) -> None:
        """切换界面形态(悬浮球 / 常驻 / 按需;无界面档在前端是 disabled)。"""
        self._presenter.set_ui_mode(mode)

    def close_panel(self) -> None:
        """隐藏面板(不是退出程序)。"""
        self._presenter.close_panel()

    def quit_app(self) -> None:
        """退出整个程序(走与托盘同一条清理路径)。"""
        self._presenter.quit_app()

    def begin_drag(self) -> None:
        """拖拽会话内部使用(**勿直接调用**):用户开始拖。"""
        self._presenter.begin_drag()

    def move_window(self, x: float, y: float) -> None:
        """拖拽会话内部使用(**勿直接调用**)。

        窗口左上角移到 (x, y) —— **绝对位置,CSS(逻辑)像素**。
        前端每次都从 mousedown 起点算绝对目标发过来,不发增量。
        """
        self._presenter.move_window(x, y)

    def end_drag(self) -> None:
        """拖拽会话内部使用(**勿直接调用**):用户松手,收尾(记位置)。"""
        self._presenter.end_drag()


class PanelPresenter:
    """把 core 的状态/通知翻译成面板的呈现。"""

    def __init__(self, core: Any, config: Any = None,
                 notifier: NotificationHub | None = None,
                 theme_broadcaster: Callable[[str], None] | None = None,
                 ui_mode_broadcaster: Callable[[str], None] | None = None,
                 quit_handler: Callable[[], None] | None = None) -> None:
        """``core`` 提供状态快照与命令;``config`` 是配置本体(读写都在这);
        ``notifier`` 是通知流(拒绝/失败的原因从这儿来);
        ``theme_broadcaster`` 把主题推给**另一个窗口**(胶囊),由装配层注入;
        ``ui_mode_broadcaster`` 把界面形态推给同一个窗口,
        由装配层注入(主题一条的同款模式);
        ``quit_handler`` 是"退出程序"的收尾路径(与托盘同一条),由装配层注入。
        """
        self._core = core
        self._config = config
        self._notifier = notifier if notifier is not None else notify
        self._theme_broadcaster = theme_broadcaster
        self._ui_mode_broadcaster = ui_mode_broadcaster
        self._quit_handler = quit_handler
        self._lock = threading.RLock()
        self._closed = False

        # 最近一次已知的 core 快照(订阅时立刻会收到一次)
        self._state: AppState | None = None
        # 已经推给前端的值 —— 只在**变了**的时候再推,别空推刷前端
        self._pushed: dict[str, Any] = {}
        self._panel: Any = None

        self._api = _PanelApi(self)

    # ---------- 装配 ----------

    @property
    def api(self) -> _PanelApi:
        """给 host 层挂到 pywebview 上的桥接对象。"""
        return self._api

    def install(self) -> None:
        """订阅 core 与通知流。

        ``core.subscribe`` 是**快照优先**的:订阅的瞬间就会把当前快照
        推过来 —— 装配期状态可能已经不是初始值了(引擎甚至已经就绪),
        漏掉这一次的话,面板得一直等到下一次变化才知道自己身处什么状态。
        """
        self._core.subscribe(self._on_core_state)
        self._notifier.subscribe(self._on_notification)

    def attach(self, panel: Any) -> None:
        """接上面板窗口(host 层)。接上之后补推一次全量。"""
        with self._lock:
            self._panel = panel
        self.push_full_state()

    def shutdown(self) -> None:
        with self._lock:
            self._closed = True

    # ---------- core 快照 → 前端 ----------

    def _on_core_state(self, state: AppState) -> None:
        """core 的状态快照来了:翻译成前端的具名 setter。"""
        with self._lock:
            if self._closed:
                return
            self._state = state

        self.push_full_state()

    def push_full_state(self) -> None:
        """把当前已知的全部事实推一遍(快照 + 配置)。

        两个时机调它:状态变化时,以及前端刚就绪时(前端就绪之前推的
        都被丢掉了 —— 见 host/panel.py 的 ``_call_js`` 门控)。
        """
        with self._lock:
            if self._closed:
                return
            state = self._state
            panel = self._panel

        if panel is None:
            return

        if state is not None:
            # 这里的名字是 **host 层 VoicePanel 的方法名**(snake_case,
            # getattr(panel, method) 取的是它);host 方法内部再转成前端
            # window.__panel 的驼峰名(_call_js("setState"))。三层名字链:
            # presenter "set_state" → host set_state() → 前端 setState。
            # 别把 presenter 这层改成驼峰 —— getattr 会取不到方法,
            # 异常被 except 吞掉,推送静默丢失(踩过)。
            self._push(panel, "set_state", state.state)
            health = state.backend_health
            self._push(panel, "set_health", health.status, health.reason)
            self._push(panel, "set_model", state.model)
            self._push(panel, "set_backend", state.backend)

        # 配置这几项是**低频**的:只在值真的变了的时候推。
        self._push(panel, "set_theme", self._theme_mode())
        self._push(panel, "set_preview_enabled", self._preview_enabled())
        self._push(panel, "set_ui_mode", self._ui_mode())
        self._push(panel, "set_hotkey", self._hotkey())

    def _push(self, panel: Any, method: str, *args: Any) -> None:
        """推给前端(同名同值不重复推 —— 空推会白刷前端)。"""
        value = tuple(args)
        with self._lock:
            if self._pushed.get(method) == value:
                return
            self._pushed[method] = value
        try:
            getattr(panel, method)(*args)
        except Exception as e:
            log.debug("推面板 %s 失败: %s", method, e)

    # ---------- 通知流 → 面板提示 ----------

    def _on_notification(self, note: Notification) -> None:
        """把"面板发起的操作"的进展/拒绝/失败显示成提示语。"""
        if note.kind not in _NOTICE_KINDS:
            return
        with self._lock:
            if self._closed:
                return
            panel = self._panel
        if panel is None:
            return
        try:
            panel.set_notice(note.message)
        except Exception as e:
            log.debug("推面板提示失败: %s", e)

    # ---------- 前端动作 ----------

    def on_frontend_ready(self) -> None:
        """前端 DOM 就绪:让窗口层放行推送并做首次 fit。"""
        with self._lock:
            panel = self._panel
        if panel is None:
            return
        panel.on_frontend_ready()
        # 就绪前的所有推送都被 _frontend_ready 门控丢弃过 —— 但 _push 的
        # 去重账本在调用时就记了账(它不知道对端丢弃了),不清账的话,
        # 就绪后的全量重推会被去重逻辑当成"已推过"全部跳过,面板永远
        # 停在前端默认值(引擎已就绪却显示"加载中/本地/0.6B")。
        # 所以:就绪 = 账本清零 + 重推全量,被丢弃的推送不算数。
        with self._lock:
            self._pushed.clear()
        self.push_full_state()

    def switch_model(self, tag: str) -> None:
        """切模型。拒绝/失败由通知流反馈(见模块 docstring)。"""
        try:
            self._core.switch_model(str(tag or ""))
        except Exception as e:
            log.debug("切换模型失败: %s", e)

    def switch_backend(self, kind: str) -> None:
        """切后端。录音/识别中会被拒绝,原因由通知流反馈。"""
        try:
            self._core.switch_backend(str(kind or ""))
        except Exception as e:
            log.debug("切换后端失败: %s", e)

    def set_theme(self, mode: str) -> None:
        """换主题:存配置 → 推面板 → 推胶囊。

        面板和胶囊共用一套主题(config.theme),所以面板改了就必须让
        胶囊也跟着换 —— 胶囊那边怎么推由装配层注入,本层不认识它。
        """
        mode = str(mode or "auto").strip().lower()
        if mode not in THEME_MODES:
            log.info("未知主题档位 %r,按 auto 处理", mode)
            mode = "auto"
        with self._lock:
            if self._closed:
                return
        if self._config is not None:
            try:
                self._config.theme = mode
                self._config.save()
            except Exception as e:
                log.debug("保存主题设置失败: %s", e)
        broadcaster = self._theme_broadcaster
        if broadcaster is not None:
            try:
                broadcaster(mode)
            except Exception as e:
                log.debug("推送主题给另一个窗口失败: %s", e)
        self.push_full_state()

    def set_preview(self, enabled: bool) -> None:
        """开关实时预览文字条。"""
        with self._lock:
            if self._closed:
                return
        if self._config is not None:
            try:
                self._config.show_preview = bool(enabled)
                self._config.save()
            except Exception as e:
                log.debug("保存预览开关失败: %s", e)
        self.push_full_state()

    def set_ui_mode(self, mode: str) -> None:
        """切换界面形态:存配置 → 推胶囊(当场生效)→ 推面板。

        胶囊那侧必须当场收到:只写 config 的话,球还挂在屏幕上,
        用户会觉得"切了没反应"。

        on_demand 存的是**意图**(config 保存原值,运行期按无球
        形态执行);headless 到不了这里(无界面档在前端 disabled,
        而真 headless 下面板根本不存在)—— 防御性忽略。
        """
        mode = str(mode or "orb").strip().lower()
        if mode not in UI_MODES or mode == "headless":
            log.info("忽略非法的界面形态 %r", mode)
            return
        with self._lock:
            if self._closed:
                return
        if self._config is not None:
            try:
                self._config.ui_mode = mode
                self._config.save()
            except Exception as e:
                log.debug("保存界面形态失败: %s", e)
        self._broadcast_ui_mode()
        self.push_full_state()

    def _broadcast_ui_mode(self) -> None:
        """把界面形态推给另一个窗口(胶囊),当场生效。

        和主题那条同款模式(``theme_broadcaster``):本层不认识胶囊,
        推给谁、怎么推由装配层决定。
        """
        broadcaster = self._ui_mode_broadcaster
        if broadcaster is None:
            return
        try:
            broadcaster(self._ui_mode())
        except Exception as e:
            log.debug("推送界面形态给另一个窗口失败: %s", e)

    def close_panel(self) -> None:
        """隐藏面板(不退出程序)。"""
        with self._lock:
            panel = self._panel
        if panel is not None:
            panel.hide()

    def quit_app(self) -> None:
        """退出程序:走与托盘**同一条**清理路径(回调由装配层注入)。"""
        handler = self._quit_handler
        if handler is None:
            log.warning("面板要退出,但没有注入退出回调")
            return
        try:
            handler()
        except Exception as e:
            log.debug("面板退出失败: %s", e)

    # ---------- 拖拽(转发给窗口层) ----------

    def begin_drag(self) -> None:
        with self._lock:
            panel = self._panel
        if panel is not None:
            panel.begin_drag()

    def move_window(self, x: float, y: float) -> None:
        with self._lock:
            panel = self._panel
        if panel is not None:
            panel.move_window(x, y)

    def end_drag(self) -> None:
        with self._lock:
            panel = self._panel
        if panel is not None:
            panel.end_drag()

    # ---------- 配置读值 ----------

    def _theme_mode(self) -> str:
        mode = str(getattr(self._config, "theme", "auto") or "auto").strip().lower()
        return mode if mode in THEME_MODES else "auto"

    def _preview_enabled(self) -> bool:
        return bool(getattr(self._config, "show_preview", True))

    def _ui_mode(self) -> str:
        """当前界面形态(四档之一),读不到/不认识按 orb。"""
        mode = str(getattr(self._config, "ui_mode", "orb")
                   or "orb").strip().lower()
        return mode if mode in UI_MODES else "orb"

    def _hotkey(self) -> str:
        return str(getattr(self._config, "hotkey", "") or "")
