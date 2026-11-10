
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

DWELL_RESULT = 1.5
DWELL_ERROR = 4.5
DWELL_ERROR_COPY = 12.0
DWELL_CANCELLED = 1.0
DWELL_REJECTED = 0.6

TEXT_RESULT = "已输入"
TEXT_CANCELLED = "已取消"
TEXT_PROCESSING = "识别中…"

class VoiceBarPresenter:

    def __init__(self, core: CoreController,
                 config: Any = None,
                 notifier: NotificationHub | None = None) -> None:
        self._core = core
        self._config = config
        self._notifier = notifier if notifier is not None else notify
        self._lock = threading.RLock()

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

        self._core_state: AppState | None = None
        self._capsule: Any = None

        self._round_start = 0.0
        self._round_failed = False
        self._pending_cancel = False

        self._dwell_timer: threading.Timer | None = None
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
            mode = self._mode
            message = self._message
            theme = self._theme
            show_preview = self._show_preview

        capsule = self._capsule
        if capsule is None:
            return
        capsule.set_theme(theme)
        capsule.set_preview_enabled(show_preview)
        if mode == protocol.MODE_ERROR:
            capsule.set_state("idle")
            capsule.set_error(message, "")
        elif mode == protocol.MODE_STARTING:
            capsule.set_state("starting")
        else:
            capsule.set_state("idle")
        if standby == protocol.STANDBY_ON_DEMAND:
            capsule.hide_orb()

    def shutdown(self) -> None:
        with self._lock:
            self._closed = True
            self._cancel_dwell()

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

    def _enter_expanding(self) -> None:
        with self._lock:
            if self._closed:
                return
            if self._mode in (protocol.MODE_EXPANDING,
                              protocol.MODE_RECORDING):
                return
            self._cancel_dwell()
            self._round_start = time.monotonic()
            self._round_failed = False
            self._pending_cancel = False
            self._message = ""
            self._text = ""
            self._can_copy = False
            self._reason = ""
            self._preview_text = ""
            self._mode = protocol.MODE_RECORDING

        capsule = self._capsule
        if capsule is not None:
            capsule.begin_recording()

    def _enter_processing(self) -> None:
        with self._lock:
            if self._closed:
                return
            if self._mode not in (protocol.MODE_EXPANDING,
                                  protocol.MODE_RECORDING):
                return
            self._mode = protocol.MODE_PROCESSING
            self._message = TEXT_PROCESSING
        capsule = self._capsule
        if capsule is not None:
            capsule.set_state("processing")

    def _enter_idle(self) -> None:
        with self._lock:
            if self._closed:
                return
            mode = self._mode
            if mode in (protocol.MODE_EXPANDING, protocol.MODE_RECORDING):
                self._mode = protocol.MODE_CANCELLED
                self._message = TEXT_CANCELLED
                self._text = ""
                self._can_copy = False
                self._schedule_dwell(DWELL_CANCELLED)
                action = "cancelled"
            elif mode == protocol.MODE_PROCESSING:
                if self._pending_cancel:
                    self._mode = protocol.MODE_CANCELLED
                    self._message = TEXT_CANCELLED
                    self._text = ""
                    self._can_copy = False
                    self._schedule_dwell(DWELL_CANCELLED)
                    action = "cancelled"
                elif self._round_failed:
                    return
                else:
                    self._mode = protocol.MODE_RESULT
                    self._message = TEXT_RESULT
                    self._text = getattr(self._core, "last_text", "") or ""
                    self._can_copy = False
                    self._schedule_dwell(DWELL_RESULT)
                    action = "result"
            elif mode == protocol.MODE_STARTING:
                self._mode = protocol.MODE_ORB
                self._message = ""
                action = "idle"
            else:
                action = None

        capsule = self._capsule
        if capsule is None:
            return
        # 状态先回 idle 再发结果:前端 setState('idle') 会停掉波形,
        # 之后的 set_result/set_cancelled 才负责文字条定格与收球
        if action == "cancelled":
            capsule.set_state("idle")
            capsule.set_cancelled()
        elif action == "result":
            capsule.set_state("idle")
            capsule.set_result(self._text)
        elif action == "idle":
            capsule.set_state("idle")

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
                action = "idle"
            else:
                self._mode = protocol.MODE_STARTING
                self._message = ""
                action = "starting"
        capsule = self._capsule
        if capsule is None:
            return
        if action == "idle":
            capsule.set_state("idle")
        else:
            capsule.set_state("starting")

    def _on_notification(self, note: Notification) -> None:
        kind = note.kind
        if kind == "recording_rejected":
            self._enter_rejected(note)
        elif kind == "asr_failed":
            self._enter_error(note, "")
        elif kind == "inject_failed":
            self._enter_error(note, str(note.fields.get("text") or ""))
        elif kind == "backend_load_failed":
            self._enter_backend_failed(note)

    def _enter_error(self, note: Notification, text: str) -> None:
        with self._lock:
            if self._closed:
                return
            self._cancel_dwell()
            self._round_failed = True
            self._mode = protocol.MODE_ERROR
            self._message = note.message
            self._text = text
            self._can_copy = bool(text.strip())
            self._reason = str(note.fields.get("reason") or "")
            message = self._message
        capsule = self._capsule
        if capsule is not None:
            capsule.set_error(message, text)

    def _enter_rejected(self, note: Notification) -> None:
        with self._lock:
            if self._closed:
                return
            self._cancel_dwell()
            self._mode = protocol.MODE_REJECTED
            self._message = note.message
            self._reason = str(note.fields.get("reason") or "")
            self._text = ""
            self._can_copy = False
            message = self._message
        capsule = self._capsule
        if capsule is None:
            return
        capsule.set_rejected(message)

    def _enter_backend_failed(self, note: Notification) -> None:
        with self._lock:
            if self._closed:
                return
            if self._mode in (protocol.MODE_EXPANDING,
                              protocol.MODE_RECORDING,
                              protocol.MODE_PROCESSING):
                return
            self._cancel_dwell()
            self._mode = protocol.MODE_ERROR
            self._message = note.message
            self._text = ""
            self._can_copy = False
            message = self._message
        capsule = self._capsule
        if capsule is not None:
            capsule.set_error(message, "")

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

    def _schedule_dwell(self, seconds: float) -> None:
        self._cancel_dwell()
        timer = threading.Timer(seconds, self._on_dwell_expired)
        timer.daemon = True
        self._dwell_timer = timer
        timer.start()

    def _cancel_dwell(self) -> None:
        timer = self._dwell_timer
        self._dwell_timer = None
        if timer is not None:
            timer.cancel()

    def _on_dwell_expired(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._dwell_timer = None
            self._mode = protocol.MODE_ORB
            self._message = ""
            self._text = ""
            self._can_copy = False
            self._reason = ""
            self._preview_text = ""
            standby = self._standby
        if standby == protocol.STANDBY_ON_DEMAND:
            capsule = self._capsule
            if capsule is not None:
                capsule.hide_orb()
