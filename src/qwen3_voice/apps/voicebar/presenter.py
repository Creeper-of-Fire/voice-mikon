
from __future__ import annotations

import threading
import time
from typing import TYPE_CHECKING, Any

from ...core.state import (STATE_IDLE, STATE_PROCESSING, STATE_RECORDING,
                            STATE_STARTING, AppState)
from ...logging_setup import get_logger
from ...notify import Notification, NotificationHub, notify
from . import protocol

if TYPE_CHECKING:
    # core 由构造注入,运行期只当鸭子用,模块级不 import controller。
    # controller 改造成 V2 CoreController 之后,这里的类型标注才成立。
    from ...core.controller import CoreController

log = get_logger("apps.voicebar")

# dwell 转移表:**整个项目只有这一份**(protocol.DWELL_SECONDS)。
#
# "何时发生"归 presenter:终态停多久、到点回哪个状态,都查这张表;
# 前端不再有任何收回定时器 —— 那是"如何呈现",归前端自己演。
#
# 到点怎么作废:比较当前的 _mode 和排表时的 mode,对不上就作废。
# 不做"到处显式取消定时器"那套 —— 比一下就够了,也不会漏。
DWELL_SECONDS = protocol.DWELL_SECONDS

TEXT_RESULT = "已输入"
TEXT_CANCELLED = "已取消"
TEXT_PROCESSING = "识别中…"

class VoiceBarPresenter:
    """把 core 的状态/通知翻译成胶囊的显示状态。

    显示状态的**唯一真相**是 ``self._mode``:每次状态变化都先改
    _mode,再经 ``_push_mode()`` 推给前端。前端只演,不决策 ——
    什么时候发生(停多久、到点回哪)全归这里的 dwell 表。
    """

    def __init__(self, core: CoreController,
                 config: Any = None,
                 notifier: NotificationHub | None = None) -> None:
        self._core = core
        self._config = config
        self._notifier = notifier if notifier is not None else notify
        self._lock = threading.RLock()

        # 显示状态的唯一真相
        self._mode = protocol.MODE_ORB
        self._message = ""
        self._text = ""
        self._can_copy = False
        self._reason = ""
        self._preview_text = ""
        self._theme = str(getattr(config, "theme", "auto") or "auto")
        self._show_preview = bool(getattr(config, "show_preview", True))
        self._standby = str(
            getattr(getattr(config, "orb", None), "standby",
                    protocol.STANDBY_RESIDENT) or protocol.STANDBY_RESIDENT)
        # 悬浮球开关:记住上一次的值,才能判断面板这次是不是"变了"
        # (变了才动窗口 —— 值没变就不动作)。
        self._orb_enabled = bool(
            getattr(getattr(config, "orb", None), "enabled", True))

        self._core_state: AppState | None = None
        self._capsule: Any = None

        self._round_start = 0.0
        self._round_failed = False
        self._pending_cancel = False

        # dwell 定时器 + 它期待的(状态, 表键):
        # 到点时 _mode 对不上就作废 —— 期间有新事实,这次转移不算数。
        self._dwell_timer: threading.Timer | None = None
        self._dwell_expect: tuple[str, str] | None = None
        self._closed = False

    def attach(self, capsule: Any) -> None:
        self._capsule = capsule

    def install(self) -> None:
        self._core.subscribe(self._on_core_state)
        self._core.subscribe_levels(self._on_levels)
        self._notifier.subscribe(self._on_notification)

    def start(self) -> None:
        with self._lock:
            if self._closed:
                return
            health = self._core_state.backend_health if self._core_state else None
            if health is not None and health.failed:
                self._mode = protocol.MODE_ERROR
                self._message = health.reason or "识别引擎不可用"
            elif health is not None and health.loading:
                self._mode = protocol.MODE_STARTING
            else:
                self._mode = protocol.MODE_ORB
            standby = self._standby
            theme = self._theme
            show_preview = self._show_preview

        capsule = self._capsule
        if capsule is None:
            return
        capsule.set_theme(theme)
        capsule.set_preview_enabled(show_preview)
        # 启动态也走同一条 set_mode:显示状态只有一个出口。
        self._push_mode()
        if standby == protocol.STANDBY_ON_DEMAND:
            capsule.hide_orb()
        # 启动时的后端不可用:错误文案按 dwell 表走(和运行中一样)。
        self._schedule_dwell(protocol.MODE_ERROR, protocol.MODE_ERROR)

    def shutdown(self) -> None:
        with self._lock:
            self._closed = True
            self._cancel_dwell()

    # ---------- 悬浮球配置(面板推过来) ----------

    def apply_orb_config(self, enabled: bool, standby: str) -> None:
        """面板改了悬浮球的开关/待机策略:当场兑现。

        面板那边改配置是**即时**的(V1 就是这样),这边不能只写 config ——
        悬浮球的显隐得当场兑现,不然用户点了开关什么都没发生。

        ``enabled`` 变了才动窗口(值没变就不动作):

        - 变 False:把球收起来(``capsule.hide_orb``)。正在录音时这一轮
          的胶囊还得留着 —— 那是 capsule 自己的语义,这边不插手。
        - 变 True:恢复球。常驻(resident)露球;按需(on_demand)仍旧收着
          —— 见 ``start``:on_demand 待机时球不露脸,热键时才出来。

        ``standby`` 只影响**后续**的收回:运行期读它的只有
        ``_on_dwell_expired``(终态停完到点回球时收不收)。所以只改
        待机策略时,当场不动窗口。
        """
        standby = str(standby or protocol.STANDBY_RESIDENT).strip().lower()
        if standby not in protocol.STANDBY_MODES:
            standby = protocol.STANDBY_RESIDENT
        with self._lock:
            if self._closed:
                return
            changed = bool(enabled) != self._orb_enabled
            self._orb_enabled = bool(enabled)
            self._standby = standby
            orb_enabled = self._orb_enabled
        if not changed:
            return
        capsule = self._capsule
        if capsule is None:
            return
        if not orb_enabled or standby == protocol.STANDBY_ON_DEMAND:
            capsule.hide_orb()
        else:
            capsule.show_orb()

    def _on_core_state(self, state: AppState) -> None:
        with self._lock:
            if self._closed:
                return
            self._core_state = state
            preview = state.preview_text or ""
            preview_changed = preview != self._preview_text
            self._preview_text = preview
            current = state.state

        if current == STATE_RECORDING:
            self._enter_expanding()
        elif current == STATE_PROCESSING:
            self._enter_processing()
        elif current == STATE_IDLE:
            self._enter_idle()
        elif current == STATE_STARTING:
            self._enter_starting()

        if preview_changed:
            self._push_preview()

    def _push_preview(self) -> None:
        capsule = self._capsule
        if capsule is None:
            return
        with self._lock:
            preview = self._preview_text
        capsule.set_preview(preview)

    def _push_mode(self) -> None:
        """把当前显示状态推给前端 —— 显示状态的**唯一出口**。

        payload 统一带齐四个字段(见 protocol.py 的说明),由前端
        按 mode 取用:前端不猜"这一态该有什么字段"。
        """
        capsule = self._capsule
        if capsule is None:
            return
        with self._lock:
            mode = self._mode
            payload = {
                "message": self._message,
                "text": self._text,
                "can_copy": self._can_copy,
                "reason": self._reason,
            }
        capsule.set_mode(mode, payload)

    def _enter_expanding(self) -> None:
        with self._lock:
            if self._closed:
                return
            if self._mode in (protocol.MODE_EXPANDING,
                              protocol.MODE_RECORDING):
                return
            self._round_start = time.monotonic()
            self._round_failed = False
            self._pending_cancel = False
            self._message = ""
            self._text = ""
            self._can_copy = False
            self._reason = ""
            self._preview_text = ""
            self._mode = protocol.MODE_RECORDING
        # 新一轮录音:前端的清场并进 applyMode('recording')(见 capsule.js),
        # 这里只管"进入录音"这件事本身。
        self._push_mode()

    def _enter_processing(self) -> None:
        with self._lock:
            if self._closed:
                return
            if self._mode not in (protocol.MODE_EXPANDING,
                                  protocol.MODE_RECORDING):
                return
            self._mode = protocol.MODE_PROCESSING
            self._message = TEXT_PROCESSING
        self._push_mode()

    def _enter_idle(self) -> None:
        with self._lock:
            if self._closed:
                return
            mode = self._mode
            dwell_key: str | None = None
            if mode in (protocol.MODE_EXPANDING, protocol.MODE_RECORDING):
                # 录音中回到 idle = 这一轮被放弃
                self._mode = protocol.MODE_CANCELLED
                self._message = TEXT_CANCELLED
                self._text = ""
                self._can_copy = False
                dwell_key = protocol.MODE_CANCELLED
            elif mode == protocol.MODE_PROCESSING:
                if self._pending_cancel:
                    self._mode = protocol.MODE_CANCELLED
                    self._message = TEXT_CANCELLED
                    self._text = ""
                    self._can_copy = False
                    dwell_key = protocol.MODE_CANCELLED
                elif self._round_failed:
                    # 这一轮已经以失败告终(错误态在等着用户):
                    # 别再用"结果"把它盖掉。
                    return
                else:
                    self._mode = protocol.MODE_RESULT
                    self._message = TEXT_RESULT
                    self._text = getattr(self._core, "last_text", "") or ""
                    self._can_copy = False
                    dwell_key = protocol.MODE_RESULT
            elif mode == protocol.MODE_STARTING:
                # 模型就绪:回球形态
                self._mode = protocol.MODE_ORB
                self._message = ""
            else:
                return
        self._push_mode()
        # 终态统一查表:停多久、到点回哪,dwell 表说了算。
        self._schedule_dwell(self._mode, dwell_key)

    def _enter_starting(self) -> None:
        with self._lock:
            if self._closed:
                return
            if self._mode in (protocol.MODE_EXPANDING,
                              protocol.MODE_RECORDING,
                              protocol.MODE_PROCESSING):
                return
            health = self._core_state.backend_health if self._core_state else None
            if health is not None and health.failed:
                return
            if health is not None and health.ready:
                self._mode = protocol.MODE_ORB
                self._message = ""
            else:
                self._mode = protocol.MODE_STARTING
                self._message = ""
        self._push_mode()

    def _on_notification(self, note: Notification) -> None:
        kind = note.kind
        if kind == "recording_rejected":
            self._enter_rejected(note)
        elif kind == "asr_failed":
            self._enter_error(note, "")
        elif kind == "inject_failed":
            self._enter_error(note, str(note.fields.get("text") or ""))
        elif kind == "modifier_wait":
            # 注入在等用户松开修饰键(识别太快、手指还在热键上):
            # 把"识别中…"实时换成提示 —— 用户看到就会松,等待自然缩短。
            # 只在 PROCESSING 态生效:别的状态下这条提示没有落点。
            with self._lock:
                if self._closed or self._mode != protocol.MODE_PROCESSING:
                    return
                self._message = note.message
            self._push_mode()
        elif kind == "backend_load_failed":
            self._enter_backend_failed(note)
        elif kind == "backend_switch_failed":
            # 切换后端失败(比如切 MiniMax 没配 key)—— 这条必须让
            # 悬浮窗看见:面板可能正缩在托盘里,用户点了切换什么反应
            # 都没有,只会以为程序死了。错误态无原文,普通 4.5s 收回。
            self._enter_backend_failed(note)

    def _enter_error(self, note: Notification, text: str) -> None:
        with self._lock:
            if self._closed:
                return
            self._round_failed = True
            self._mode = protocol.MODE_ERROR
            self._message = note.message
            self._text = text
            self._can_copy = bool(text.strip())
            self._reason = str(note.fields.get("reason") or "")
            can_copy = self._can_copy
        self._push_mode()
        # 带原文的错误态**无自动转移**(表里的值是 None):用户要反复点
        # 「复制」重试,自动收回会把重试的口子堵上。
        if can_copy:
            self._schedule_dwell(protocol.MODE_ERROR,
                                 protocol.MODE_ERROR_WITH_TEXT)
        else:
            self._schedule_dwell(protocol.MODE_ERROR, protocol.MODE_ERROR)

    def on_dismissed(self) -> bool:
        """用户主动关掉了错误提示(带原文的错误常驻态)。

        返回**是否真的处理了** —— 只有"带原文的错误常驻态"才认这个
        动作:录音中、正常结果态、普通错误态收到 dismiss 一律静默
        忽略(幂等),免得误点把正在录的一轮收掉。

        带原文的错误态**刻意不排 dwell**:用户可能要反复点「复制」
        重试(剪贴板被占、目标窗口不给焦点),自动收回会把重试的
        出口堵上 —— 收回交给用户点 ✕ 或开始下一轮录音。
        """
        with self._lock:
            if self._closed:
                return False
            if self._mode != protocol.MODE_ERROR or not self._can_copy:
                return False
            self._mode = protocol.MODE_ORB
            self._message = ""
            self._text = ""
            self._can_copy = False
            self._reason = ""
        self._push_mode()
        return True

    def _enter_rejected(self, note: Notification) -> None:
        with self._lock:
            if self._closed:
                return
            self._mode = protocol.MODE_REJECTED
            self._message = note.message
            self._reason = str(note.fields.get("reason") or "")
            self._text = ""
            self._can_copy = False
        self._push_mode()
        # 被拒的状态本身只停 0.6s(hint 气泡的淡出是纯视觉,留前端)
        self._schedule_dwell(protocol.MODE_REJECTED, protocol.MODE_REJECTED)

    def _enter_backend_failed(self, note: Notification) -> None:
        with self._lock:
            if self._closed:
                return
            if self._mode in (protocol.MODE_EXPANDING,
                              protocol.MODE_RECORDING,
                              protocol.MODE_PROCESSING):
                return
            self._mode = protocol.MODE_ERROR
            self._message = note.message
            self._text = ""
            self._can_copy = False
        self._push_mode()
        self._schedule_dwell(protocol.MODE_ERROR, protocol.MODE_ERROR)

    def _on_levels(self, levels: list[float]) -> None:
        with self._lock:
            if self._closed:
                return
            if self._mode not in (protocol.MODE_EXPANDING,
                                  protocol.MODE_RECORDING):
                return
            elapsed = (time.monotonic() - self._round_start
                       if self._round_start else 0.0)
        capsule = self._capsule
        if capsule is not None:
            capsule.push_levels(list(levels), max(0.0, elapsed))

    def try_start_recording(self) -> tuple[bool, str]:
        """点球开始录音。返回 (ok, 提示语) —— 提示语非空时球上冒一句。"""
        result = self._core.start_recording()
        if result.ok:
            return True, ""
        try:
            health = self._core.state_snapshot.backend_health
            loading = bool(health is not None and health.loading)
        except Exception:
            loading = False
        if loading:
            return False, "模型加载中…"
        return False, ""

    def try_stop_recording(self) -> None:
        self._core.stop_recording()

    def try_cancel_recording(self) -> None:
        with self._lock:
            self._pending_cancel = True
        result = self._core.cancel_recording()
        if not result.ok:
            with self._lock:
                self._pending_cancel = False

    def _schedule_dwell(self, mode: str, key: str | None) -> None:
        """按 dwell 表排一次自动转移。

        ``mode`` 是**排表时**的状态,``key`` 是表里的键:带原文的错误态
        和普通错误态是同一个 mode,查表用的键不一样,所以分开传。

        排新表会把旧表撤掉(同一状态只该有一张表在飞)。
        """
        if not key:
            return
        seconds = DWELL_SECONDS.get(key)
        if seconds is None:
            # 无自动转移(带原文的错误态):不排。
            return
        with self._lock:
            if self._closed:
                return
            if self._mode != mode:
                return
            self._cancel_dwell()
            self._dwell_expect = (mode, key)
            timer = threading.Timer(seconds, self._on_dwell_expired)
            timer.daemon = True
            self._dwell_timer = timer
        timer.start()

    def _cancel_dwell(self) -> None:
        """撤掉排着的 dwell 转移(只在新排一张表时用)。"""
        timer = self._dwell_timer
        self._dwell_timer = None
        self._dwell_expect = None
        if timer is not None:
            timer.cancel()

    def _on_dwell_expired(self) -> None:
        with self._lock:
            if self._closed:
                return
            expect = self._dwell_expect
            self._dwell_timer = None
            self._dwell_expect = None
            # 到点先比状态:期间有新事实(状态已经变了)→ 本次转移作废。
            # 不做"到处显式取消定时器"那套 —— 比一下就够了。
            if expect is None or self._mode != expect[0]:
                return
            self._mode = protocol.MODE_ORB
            self._message = ""
            self._text = ""
            self._can_copy = False
            self._reason = ""
            self._preview_text = ""
            standby = self._standby
        self._push_mode()
        if standby == protocol.STANDBY_ON_DEMAND:
            capsule = self._capsule
            if capsule is not None:
                capsule.hide_orb()
