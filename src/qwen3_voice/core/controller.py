"""核心控制器。

UI(面板/托盘)和热键都只跟这一层打交道,彼此不直接依赖。
这样换 UI 实现、换触发方式,都不用动其它层。

状态机:
    IDLE ──start_recording()──> RECORDING ──stop_recording()──> PROCESSING
      ^                                                              │
      └────────────────────── 识别完成/失败 ──────────────────────────┘

录音期间还会跑一个**预览线程**:把累积到的全部音频反复整段重识别,
实时刷新"边说边显示"的文字(事件 kind="preview")。它只是体验,
最终注入的始终是停止时那次整段识别的结果。
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable

import numpy as np

from ..asr.engine import ASREngine, ASRError
from ..audio.devices import choose_device
from ..audio.recorder import Recorder, rms_of
from ..config import Config
from ..logging_setup import get_logger
from ..win32.api import get_foreground_window
from ..win32.inject import paste_text

log = get_logger("core")


class State(str, Enum):
    """对外暴露的应用状态。"""

    STARTING = "starting"     # 启动中
    IDLE = "idle"             # 待机
    RECORDING = "recording"   # 录音中
    PROCESSING = "processing" # 识别中
    ERROR = "error"           # 出错

    @property
    def label(self) -> str:
        return {
            State.STARTING: "启动中",
            State.IDLE: "待机",
            State.RECORDING: "录音中",
            State.PROCESSING: "识别中",
            State.ERROR: "出错",
        }[self]


@dataclass
class UIEvent:
    """控制器发给 UI 的事件。"""

    kind: str                       # state / level / result / error / info
    state: State | None = None
    text: str = ""
    levels: list[float] = field(default_factory=list)
    elapsed: float = 0.0
    model_tag: str = ""


def _default_injector(text: str, hwnd: int, restore: bool, trailing_space: bool) -> bool:
    """默认的文本注入:剪贴板 + 模拟 Ctrl+V。"""
    return paste_text(text, hwnd=hwnd, restore=restore, trailing_space=trailing_space)


def _default_recorder_factory(config: Config, device: int | None = None) -> Recorder:
    """默认的录音器工厂。

    device 优先用探测出来的结果;探测还没完成时为 None,
    此时退回系统默认设备(宁可设备不理想,也不能阻塞用户)。
    """
    return Recorder(device=device, target_rate=config.sample_rate)


class VoiceController:
    """语音输入的核心逻辑。"""

    # ---- 实时预览(边说边显示)的调度参数 ----

    # 最短音频:太短的片段识别不出东西,白跑一次推理
    PREVIEW_MIN_SECONDS = 0.5
    # 两轮之间的最小间隔(秒)
    PREVIEW_MIN_INTERVAL = 1.5
    # 实际间隔 = max(最小间隔, 上次推理耗时 × 系数)
    # 必须自适应:重喂全部音频的开销随音频长度线性增长
    # (transformers 后端没有 vLLM 的 prefix caching),
    # 推理一变慢,固定间隔就会让请求越积越多。
    PREVIEW_INTERVAL_FACTOR = 1.2

    def __init__(self, config: Config, injector=None, recorder_factory=None):
        self.config = config
        self.state = State.STARTING
        self.engine = ASREngine(
            model_root=config.model_root,
            model_tag=config.model,
            language=config.language,
            hotwords=config.hotwords,
        )
        # 注入器可替换 —— 测试用假实现,避免真的改剪贴板/模拟按键
        self._injector = injector or _default_injector
        # 录音器工厂可替换 —— 测试用假实现,避免真的打开音频设备
        self._recorder_factory = recorder_factory or _default_recorder_factory
        self.recorder: Recorder | None = None
        self.input_device: int | None = None  # 由后台探测填充
        self.last_text = ""
        self.last_error = ""

        self._listeners: list[Callable[[UIEvent], None]] = []
        self._lock = threading.RLock()

        # 实时预览线程(录音期间后台跑整段重识别)
        self._preview_stop: threading.Event | None = None
        self._preview_thread: threading.Thread | None = None

    # ---------- 事件订阅 ----------

    def subscribe(self, callback: Callable[[UIEvent], None]) -> None:
        with self._lock:
            self._listeners.append(callback)

    def _emit(self, event: UIEvent) -> None:
        with self._lock:
            listeners = list(self._listeners)
        for cb in listeners:
            try:
                cb(event)
            except Exception as e:
                log.debug("事件回调异常: %s", e)

    def _set_state(self, state: State) -> None:
        if self.state == state:
            return
        self.state = state
        self._emit(UIEvent(kind="state", state=state))

    # ---------- 启动 ----------

    def start(self) -> None:
        """后台初始化:加载模型 + 探测麦克风。"""
        threading.Thread(target=self._init_model, name="model-load", daemon=True).start()
        threading.Thread(target=self._init_mic, name="mic-warmup", daemon=True).start()

    def _init_model(self) -> None:
        try:
            self.engine.load(self.config.model)
            self._emit(UIEvent(kind="info", text="模型就绪"))
            self._emit(UIEvent(kind="model", model_tag=self.engine.model_tag))
            # 模型好了但麦克风可能还在探测,两边都好了才算真正就绪
            if self.state == State.STARTING:
                self._set_state(State.IDLE)
        except ASRError as e:
            self.last_error = str(e)
            self._set_state(State.ERROR)
            self._emit(UIEvent(kind="error", text=str(e)))

    def _init_mic(self) -> None:
        """探测可用麦克风。

        探测结果存到 self.input_device —— 录音时要用它,不能每次再探一遍
        (探测要 2 秒,放在热键路径上会把用户说话的开头吃掉)。
        """
        try:
            index, desc = choose_device(
                self.config.input_device,
                self.config.device_name_hints,
            )
            self.input_device = index
            log.info("麦克风: %s", desc)
            self._emit(UIEvent(kind="info", text=f"麦克风 {desc}"))
        except Exception as e:
            log.error("麦克风探测失败: %s", e)

    # ---------- 录音控制 ----------

    def toggle_recording(self) -> None:
        """热键/按钮的统一入口。"""
        if self.state == State.RECORDING:
            self.stop_recording()
        elif self.state in (State.IDLE, State.ERROR):
            self.start_recording()
        else:
            log.debug("当前状态 %s,忽略切换请求", self.state)

    def start_recording(self) -> bool:
        if self.state == State.RECORDING:
            return False
        if self.state == State.PROCESSING:
            log.debug("识别中,暂不开始新的录音")
            return False

        recorder = self._recorder_factory(self.config, self.input_device)
        if not recorder.start():
            self._emit(UIEvent(kind="error", text="录音启动失败,请检查麦克风"))
            return False

        self.recorder = recorder
        self._set_state(State.RECORDING)
        # 新一轮录音:先把上一轮残留的预览文字清掉
        self._emit(UIEvent(kind="preview", text=""))
        self._start_preview(recorder)
        return True

    def stop_recording(self) -> None:
        if self.state is not State.RECORDING or self.recorder is None:
            return

        # 先停预览线程 —— 它用的是同一个引擎,不停掉会和下面的整段识别抢 GPU
        self._stop_preview()

        # 录音器的原生采样率(音频就是按这个采样率采的)
        native_rate = int(getattr(self.recorder, "native_rate", 0) or 0) or self.config.sample_rate

        audio = self.recorder.stop()
        self.recorder = None
        dur = len(audio) / native_rate
        log.info("停止录音,时长 %.1fs", dur)

        if dur < self.config.min_duration:
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="error", text="录音太短"))
            self._emit(UIEvent(kind="preview", text=""))
            return

        rms = rms_of(audio)
        if rms < self.config.silence_rms:
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="error", text="没有检测到声音"))
            self._emit(UIEvent(kind="preview", text=""))
            return

        self._set_state(State.PROCESSING)
        threading.Thread(target=self._process, args=(audio, native_rate),
                         name="asr-run", daemon=True).start()

    # ---------- 实时预览 ----------

    def _start_preview(self, recorder: Recorder) -> None:
        """启动实时预览线程。

        做法和官方 ``qwen_asr`` 的 ``streaming_transcribe`` 一致:
        每次把**累积到目前的全部音频**重新喂给模型一次,模型每次都
        **重新生成完整文本**,所以结果直接整体替换即可 ——
        不需要拼接、去重、切句。
        """
        self._stop_preview()
        stop = threading.Event()
        thread = threading.Thread(
            target=self._preview_loop, args=(recorder, stop),
            name="asr-preview", daemon=True)
        self._preview_stop = stop
        self._preview_thread = thread
        thread.start()

    def _stop_preview(self) -> None:
        """停掉预览线程。

        最多等 2 秒 —— 线程可能正卡在一次推理里,不能让它把
        ``stop_recording()`` 堵住。超时就放弃,让它自己退出。
        """
        stop, thread = self._preview_stop, self._preview_thread
        self._preview_stop = None
        self._preview_thread = None
        if stop is not None:
            stop.set()
        if thread is not None and thread.is_alive():
            thread.join(timeout=2.0)
            if thread.is_alive():
                log.warning("预览线程未在 2 秒内退出(可能卡在推理中)")

    def _preview_loop(self, recorder: Recorder, stop: threading.Event) -> None:
        """后台线程:反复对"到目前为止的音频"做整段识别,实时刷新预览文字。

        绝不能把这个循环放到录音回调线程或 UI 线程里 ——
        ``engine.transcribe`` 是阻塞的,会卡住录音或界面。
        """
        snapshot = getattr(recorder, "snapshot", None)
        if snapshot is None:
            # 录音器拿不到快照(不是本项目的 Recorder)→ 没有预览可做
            return

        rate = self.config.sample_rate
        # 快照是设备原生采样率,阈值和重采样都要按原生采样率算
        native_rate = int(getattr(recorder, "native_rate", 0) or 0)
        if native_rate <= 0:
            native_rate = rate
        min_samples = int(self.PREVIEW_MIN_SECONDS * native_rate)
        interval = self.PREVIEW_MIN_INTERVAL

        while not stop.is_set():
            try:
                audio = snapshot()
            except Exception as e:
                log.debug("取录音快照失败: %s", e)
                audio = None
            # 太短不值得跑一次识别(刚开始录音时快照是空的)
            if audio is None or len(audio) < min_samples:
                stop.wait(0.2)
                continue

            # 快照是原生采样率,直接交给引擎 —— qwen_asr 内部会用
            # librosa 做高质量重采样(与官方评测路径一致),
            # 不在这里自己降采样(线性插值没有抗混叠滤波,会毁掉高频)。
            t0 = time.monotonic()
            text = ""
            try:
                text = self.engine.transcribe(audio, native_rate)
            except Exception as e:
                # 识别失败(引擎异常 / 切换模型时旧模型被换掉)只跳过这一轮,
                # 线程绝对不能死
                log.debug("预览识别失败: %s", e)
            elapsed = time.monotonic() - t0

            # 间隔自适应:推理越慢,间隔拉得越长,避免越积越多
            interval = max(self.PREVIEW_MIN_INTERVAL,
                           elapsed * self.PREVIEW_INTERVAL_FACTOR)

            if text and text.strip() and not stop.is_set():
                self._emit(UIEvent(kind="preview", text=text))
            stop.wait(interval)

    # ---------- 识别与注入 ----------

    def _process(self, audio: np.ndarray, sample_rate: int) -> None:
        try:
            text = self.engine.transcribe(audio, sample_rate)
        except ASRError as e:
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="error", text=str(e)))
            self._emit(UIEvent(kind="preview", text=""))
            return
        except Exception as e:
            # 引擎抛任何异常都不能让工作线程死掉
            log.exception("识别异常: %s", e)
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="error", text=f"识别失败: {e}"))
            self._emit(UIEvent(kind="preview", text=""))
            return

        self.last_text = text
        if not text.strip():
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="error", text="没听清,请再说一遍"))
            self._emit(UIEvent(kind="preview", text=""))
            return

        # 粘贴前捕获焦点窗口 —— 转写这 1 秒用户可能切窗口
        hwnd = get_foreground_window()
        ok = self._injector(
            text,
            hwnd=hwnd,
            restore=self.config.restore_clipboard,
            trailing_space=self.config.trailing_space,
        )
        self._set_state(State.IDLE)
        if ok:
            self._emit(UIEvent(kind="result", text=text))
        else:
            self._emit(UIEvent(kind="error", text="文本注入失败"))
        # 最终结果到了,预览文字就没用了(最终结果始终来自整段识别,不是预览)
        self._emit(UIEvent(kind="preview", text=""))

    # ---------- 模型切换 ----------

    def switch_model(self, model_tag: str) -> None:
        """后台切换模型规格。"""
        if model_tag == self.engine.model_tag and self.engine.loaded:
            return

        def job():
            self._emit(UIEvent(kind="info", text=f"正在切换到 {model_tag} ..."))
            try:
                self.engine.switch_model(model_tag)
                self.config.model = model_tag
                self.config.save()
                self._emit(UIEvent(kind="model", model_tag=model_tag))
                self._emit(UIEvent(kind="info", text=f"已切换到 {model_tag}"))
            except ASRError as e:
                self._emit(UIEvent(kind="error", text=str(e)))
            except Exception as e:
                log.exception("模型切换异常: %s", e)
                self._emit(UIEvent(kind="error", text=f"模型切换失败: {e}"))

        threading.Thread(target=job, name="model-switch", daemon=True).start()

    # ---------- 生命周期 ----------

    def shutdown(self) -> None:
        self._stop_preview()
        if self.recorder is not None:
            try:
                self.recorder.stop()
            except Exception:
                pass
            self.recorder = None
