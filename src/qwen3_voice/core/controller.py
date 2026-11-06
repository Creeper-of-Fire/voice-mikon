"""核心控制器。

UI(面板/托盘)和热键都只跟这一层打交道,彼此不直接依赖。
这样换 UI 实现、换触发方式,都不用动其它层。

状态机:
    IDLE ──start_recording()──> RECORDING ──stop_recording()──> PROCESSING
      ^                                                              │
      └────────────────────── 识别完成/失败 ──────────────────────────┘

用户随时可以 ``cancel_recording()`` 放弃当前这一轮:录音中就丢掉音频,
识别中就作废结果 —— 两种情况都不识别、不注入,回到 IDLE。

录音期间还会跑一个**预览线程**:把录音增量喂给预览位的引擎,
实时刷新"边说边显示"的文字(事件 kind="preview")。它只是体验,
最终注入的始终是最终位那次整段识别的结果。

两个插槽,同一套接口(见 ``asr/streaming.py``):

    预览位 —— 要"快":默认分句预览引擎,切句就出字,跟手
    最终位 —— 要"准":固定整段引擎,最终注入的文字只来自它

引擎接口只有一种形态(``feed(chunk)`` / ``close()``),
能力差异全在实现里 —— 控制器对"预览位上是哪颗引擎"一无所知。

识别后端(本地模型 / 网络 API)是**互斥**的:同一时刻只留一个,
切走后旧后端被 unload(本地释放显存,网络关会话)。
切换在后台线程里做;录音/识别中**拒绝切换** ——
这一轮的最终位引擎持有旧后端的引用,中途换掉会出错或丢结果。
"""

from __future__ import annotations

import threading
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable

import numpy as np

from ..asr.base import ASRBackend, ASRError
from ..asr.engine import LocalASRBackend
from ..asr.network import DashScopeBackend
from ..asr.streaming import (
    FullPassEngine,
    PreviewEngineKind,
    StreamingEngine,
    make_preview_engine,
)
from ..audio.devices import choose_device
from ..audio.recorder import Recorder
from ..audio.recording_store import save_recording
from ..config import Config
from ..logging_setup import get_logger
from ..win32.api import get_foreground_window
from ..win32.inject import paste_text

log = get_logger("core")

# 识别后端(互斥,同一时刻只留一个)
BACKEND_LOCAL = "local"            # 本地 Qwen3-ASR(transformers,GPU)
BACKEND_DASHSCOPE = "dashscope"    # 阿里云百炼 DashScope(OpenAI 兼容 HTTP)
BACKEND_KINDS = (BACKEND_LOCAL, BACKEND_DASHSCOPE)

# 预览位插哪颗引擎 —— **模块级常量**,代码内可切换,不做成用户配置项。
#
#   PreviewEngineKind.SENTENCE   分句预览(默认):切一句认一句,跟手;
#                                分句的不一致只污染预览层
#   PreviewEngineKind.FULL_PASS  旧行为:每轮把累积音频整段重识别
#
# 最终位固定整段引擎(见 ``_make_final_engine``):最终注入的文字
# 必须永远来自整段识别,预览层怎么马虎都不许影响它。
PREVIEW_ENGINE_KIND = PreviewEngineKind.SENTENCE


def normalize_backend(kind: str | None) -> str:
    """把后端名字规整成合法值(认不出来就当本地)。"""
    name = str(kind or "").strip().lower()
    return name if name in BACKEND_KINDS else BACKEND_LOCAL


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

    kind: str                       # state / level / result / error / info / cancelled
    state: State | None = None
    text: str = ""
    levels: list[float] = field(default_factory=list)
    elapsed: float = 0.0
    model_tag: str = ""
    # error 事件**附带的原文**:注入失败时带着识别出来的文本一起发。
    # 光有一句"文本注入失败"是不够的 —— 用户得看得到自己刚说了什么,
    # 胶囊上的「复制」按钮也靠这份文本才能把内容交到用户手上。
    payload: str = ""


def _default_injector(text: str, hwnd: int, restore: bool, trailing_space: bool) -> bool:
    """默认的文本注入:剪贴板 + 模拟 Ctrl+V。"""
    return paste_text(text, hwnd=hwnd, restore=restore, trailing_space=trailing_space)


def _default_recorder_factory(config: Config, device: int | None = None) -> Recorder:
    """默认的录音器工厂。

    device 优先用探测出来的结果;探测还没完成时为 None,
    此时退回系统默认设备(宁可设备不理想,也不能阻塞用户)。
    """
    return Recorder(device=device, target_rate=config.sample_rate)


def _make_chunk_puller(recorder):
    """造一个"取录音增量"的函数,返回 None 表示取不到。

    优先用 ``Recorder.drain_chunks()``:它只给**新增**的音频,
    不用每次都把全部音频重新拼一遍。

    只有 ``snapshot()`` 的录音器(比如测试里的假录音器)才退回差量拉取:
    记住已经喂过多少样本,只把新增的部分切出来。效果一样,
    只是每次仍然要拼一次全量(那是旧录音器的固有成本)。
    """
    drain = getattr(recorder, "drain_chunks", None)
    if callable(drain):
        return drain

    snapshot = getattr(recorder, "snapshot", None)
    if not callable(snapshot):
        return None

    fed = {"samples": 0}

    def pull():
        audio = snapshot()
        if audio is None:
            return None
        total = len(audio)
        if total <= fed["samples"]:
            return None
        out = audio[fed["samples"]:]
        fed["samples"] = total
        return out

    return pull


class VoiceController:
    """语音输入的核心逻辑。"""

    def __init__(self, config: Config, injector=None, recorder_factory=None,
                 backend_factory=None):
        self.config = config
        self.state = State.STARTING
        # 后端工厂可替换 —— 测试用假实现,避免真的加载模型/真的联网
        self._backend_factory = backend_factory or self._default_backend_factory
        # 当前生效的后端(本地模型 / 网络 API)。两种后端互斥。
        self.backend_kind = normalize_backend(
            getattr(config, "asr_backend", BACKEND_LOCAL))
        self.engine: ASRBackend = self._make_backend(self.backend_kind)
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
        # 后端换装是"整体动作"(建新的 → 加载 → 卸旧的 → 换引用),
        # 拿一把单独的锁串起来:启动加载和切换不会交叉。
        self._backend_lock = threading.RLock()

        # 实时预览线程(录音期间后台跑,喂预览位的引擎)
        self._preview_stop: threading.Event | None = None
        self._preview_thread: threading.Thread | None = None
        # 全局热键监听器(由入口挂上来)。热键是**全局**资源:
        # 本进程不注销,别的实例就注册不上同一个组合键 —— 所以
        # shutdown() 里要**第一个**停它,不能排在模型卸载后面。
        self._hotkey = None
        # 两个插槽的引擎实例。预览位的引擎归预览线程所有(它负责 close),
        # 最终位的引擎只在停止录音时被喂完整音频、然后 close 出最终结果。
        self._preview_engine: StreamingEngine | None = None
        self._final_engine: StreamingEngine | None = None

        # 会话代号:每开始一轮录音 +1。
        # 识别线程(阻塞推理,停不下来)靠它判断"我还是当前这一轮吗" ——
        # 取消或开了新一轮都会让它过期,过期线程醒来后直接丢弃结果,
        # 绝不注入文字(用户已经明确说了不要)。
        # 注意:**"绝不注入作废文本"的主防线是这个代号**,
        # `_cancelled` 标志只是补充(_process 里先查代号再注入,
        # 竞态窗口里迟到的取消靠 session 递增兜住)。
        self._session = 0
        self._cancelled = False

    # ---------- 识别后端 ----------

    def _default_backend_factory(self, kind: str, config: Config) -> ASRBackend:
        """按后端名字造一颗后端(默认实现)。

        两种后端实现同一个 ``ASRBackend`` 抽象,控制器只认那个接口。
        """
        if kind == BACKEND_DASHSCOPE:
            return DashScopeBackend(
                model=getattr(config, "dashscope_model", "") or None,
                base_url=getattr(config, "dashscope_base_url", "") or None,
                hotwords=config.hotwords,
            )
        return LocalASRBackend(
            model_root=config.model_root,
            model_tag=config.model,
            language=config.language,
            hotwords=config.hotwords,
        )

    def _make_backend(self, kind: str) -> ASRBackend:
        """造一颗后端(走可注入的工厂)。"""
        return self._backend_factory(kind, self.config)

    def _backend_supports_preview(self) -> bool:
        """当前后端支不支持实时预览。

        网络后端**不支持**(整段请求,做不了逐句流式)。
        用 getattr 兜底 —— 测试里的假引擎没有这个属性时按"支持"处理。
        """
        return bool(getattr(self.engine, "supports_preview", True))

    @staticmethod
    def _unload_backend(backend) -> None:
        """把一个后端卸掉(本地释放显存 / 网络关会话)。"""
        unload = getattr(backend, "unload", None)
        if not callable(unload):
            return
        try:
            unload()
        except Exception as e:
            log.debug("卸载后端失败: %s", e)

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
            # 后端换装和启动加载共用一把锁:不会交叉。
            with self._backend_lock:
                self.engine.load()
                self._emit(UIEvent(kind="info", text="模型就绪"))
                self._emit(UIEvent(kind="backend", text=self.backend_kind))
                tag = getattr(self.engine, "model_tag", "")
                if tag:
                    self._emit(UIEvent(kind="model", model_tag=tag))
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
        if self.state == State.STARTING:
            # 模型还在加载。这一挡是功能级的:放行的话录音能开,
            # 停止时整段识别会直接抛"模型尚未加载"(日志里真实发生过)。
            # 热键路径 toggle_recording 的 else 分支本来就不放行,
            # 这里挡的是**点悬浮球**那条直通路径。
            log.debug("模型还在加载,暂不能开始录音")
            return False

        recorder = self._recorder_factory(self.config, self.input_device)
        if not recorder.start():
            self._emit(UIEvent(kind="error", text="录音启动失败,请检查麦克风"))
            return False

        self.recorder = recorder
        # 新的一轮:会话代号 +1,并清掉上一轮的取消标记。
        # 上一轮遗留的识别线程(如果还在跑)会因此过期,自动丢弃结果。
        self._session += 1
        self._cancelled = False
        self._set_state(State.RECORDING)
        # 新一轮录音:先把上一轮残留的预览文字清掉
        self._emit(UIEvent(kind="preview", text=""))
        # 最终位的引擎:整段引擎,最终结果只认它。
        # 先造好放在那儿,停止录音时把完整音频喂进去、close 出最终文本。
        native_rate = int(getattr(recorder, "native_rate", 0) or 0) or self.config.sample_rate
        self._final_engine = self._make_final_engine(native_rate)
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

        # 录音留存:调试期需要**同一段真实录音**反复回放才能定位
        # "预览零出字"这类问题(日志分不清是没喂进去还是没识别出来)。
        # 存盘失败绝不影响主流程 —— 只是个调试基建,不值得为它中断识别。
        if self.config.save_recordings:
            try:
                save_recording(audio, native_rate)
            except Exception as e:
                log.debug("录音留存失败: %s", e)

        # **不做任何前置判定**(时长 / 音量):录到多少就送多少,整段交给模型。
        # 识别得到就是结果,识别不到就空 —— 该不该听,由模型说,不由本地阈值说。
        #
        # 以前这里有两道门("录音太短"、"没有检测到声音"),初衷是防呆:
        # 怕用户误按热键白跑一次 GPU 推理。实际代价远大于收益:
        #   - 静音门用的是**整段平均 RMS**,静音占比越高越容易被误杀 ——
        #     说得越久、停顿越多,反而越容易被判成"没说话";
        #   - 阈值卡在麦克风增益偏低时的语音主体电平附近,说话轻一点就整段丢。
        # 用户宁可自己删掉一句识别错的,也不要自己的话被本地阈值替它拦下。
        self._set_state(State.PROCESSING)
        threading.Thread(target=self._process, args=(audio, native_rate, self._session),
                         name="asr-run", daemon=True).start()

    def cancel_recording(self) -> None:
        """放弃当前这一轮:不识别、不注入。

        录音中:停掉录音器和预览线程,音频直接丢掉。
        识别中:推理是阻塞的,停不下来 —— 只能**标记**这一轮作废,
        识别线程醒来后发现自己过期,于是丢掉结果、不注入文字。

        两种情况都回到 IDLE(用户可以立刻开始下一轮),并发出
        ``cancelled`` 事件,让胶囊显示「已取消」再淡出。
        """
        if self.state == State.RECORDING:
            # 先把这轮标成作废,再停预览线程:
            # 预览线程可能正卡在一次识别里,它醒来后的 partial 回调
            # 必须已经知道自己过期了 —— 作废的文字一个字都不许推给界面。
            self._cancelled = True
            # 再停预览线程 —— 它用的是同一个引擎,不停掉会跟识别抢 GPU
            self._stop_preview()

            recorder = self.recorder
            self.recorder = None
            if recorder is not None:
                try:
                    recorder.stop()
                except Exception as e:
                    log.debug("取消录音时停止录音器失败: %s", e)

            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="preview", text=""))
            self._emit(UIEvent(kind="cancelled", text="已取消"))
            return

        if self.state == State.PROCESSING:
            # 识别线程还在跑,停不掉;标记作废 + 立刻回 IDLE,
            # 免得用户被卡在"识别中"按不动热键
            self._cancelled = True
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="preview", text=""))
            self._emit(UIEvent(kind="cancelled", text="已取消"))

    # ---------- 实时预览 ----------

    def _make_preview_engine(self, sample_rate: int,
                             partial) -> StreamingEngine:
        """造预览位的引擎。

        默认按模块级常量 ``PREVIEW_ENGINE_KIND`` 选(分句 / 整段回退),
        测试可以覆盖这个方法换成假引擎。
        """
        return make_preview_engine(PREVIEW_ENGINE_KIND, self.engine,
                                   sample_rate, partial=partial)

    def _make_final_engine(self, sample_rate: int) -> StreamingEngine:
        """造最终位的引擎。

        **固定整段引擎** —— 最终结果必须永远来自整段识别。
        分句预览带来的不一致只允许停留在预览层。
        """
        return FullPassEngine(self.engine, sample_rate)

    def _make_partial_callback(self, session: int):
        """预览引擎的 partial 回调(跑在预览线程里)。

        必须过 session 检查:作废的一轮(被取消,或已被新一轮取代)
        的文字**绝不推 UI** —— 和最终结果一个待遇。
        """

        def on_partial(text: str) -> None:
            if self._is_stale(session):
                log.debug("这一轮已作废,丢弃预览文字")
                return
            if text and text.strip():
                self._emit(UIEvent(kind="preview", text=text))

        return on_partial

    def _start_preview(self, recorder: Recorder) -> None:
        """启动实时预览线程。

        预览位插一颗"快"的引擎(默认分句引擎),它吐出来的文字只影响
        界面上的预览条 —— 最终注入的文字永远来自最终位的整段识别。

        线程只做一件事:不停地取录音增量喂给引擎。
        引擎自己决定什么时候出字(分句引擎切句就出,整段引擎定时重识别),
        控制器对"预览位上是哪颗引擎"一无所知。

        **网络后端不跑预览**:兼容端点是整段请求,做不了"边说边出字"。
        硬做(每隔几秒重发全量音频)只会烧配额、延迟越滚越大 —— 不做。
        网络模式下的行为差异在面板上有明确说明,不是静默失效。
        """
        self._stop_preview()
        if not self._backend_supports_preview():
            log.info("当前后端(%s)不支持实时预览,跳过预览线程", self.backend_kind)
            return
        pull = _make_chunk_puller(recorder)
        if pull is None:
            # 录音器拿不到音频(不是本项目的 Recorder)→ 没有预览可做
            return

        # 快照/增量都是设备原生采样率,阈值和重采样都要按原生采样率算
        native_rate = int(getattr(recorder, "native_rate", 0) or 0)
        if native_rate <= 0:
            native_rate = self.config.sample_rate

        engine = self._make_preview_engine(
            native_rate, self._make_partial_callback(self._session))
        stop = threading.Event()
        thread = threading.Thread(
            target=self._preview_loop, args=(stop, engine, pull),
            name="asr-preview", daemon=True)
        self._preview_stop = stop
        self._preview_thread = thread
        self._preview_engine = engine
        thread.start()

    def _stop_preview(self) -> None:
        """停掉预览线程。

        最多等 2 秒 —— 线程可能正卡在一次推理里,不能让它把
        ``stop_recording()`` 堵住。超时就放弃,让它自己退出。

        预览引擎由**预览线程**负责 close(它才是唯一的写者):
        线程退出时会把最后一句认掉,close 出来的 final 只用于收尾,
        最终结果不取它。线程没退出时控制器不抢着 close ——
        那会和正在进行的识别撞车。
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

    def _preview_loop(self, stop: threading.Event, engine: StreamingEngine,
                      pull) -> None:
        """后台线程:把录音增量喂给预览位的引擎。

        绝不能把这个循环放到录音回调线程或 UI 线程里 ——
        识别是阻塞的,会卡住录音或界面。

        切句识别是在 ``feed`` 内部同步做的:断句间隔天然大于 300ms,
        单句识别几百毫秒,不会积压。识别期间攒下的音频会在下一次
        拉取时一次性喂进来(引擎内部按固定帧长切分,句边界不会丢)。
        """
        try:
            while not stop.is_set():
                try:
                    chunk = pull()
                except Exception as e:
                    log.debug("取录音增量失败: %s", e)
                    chunk = None
                if chunk is None or len(chunk) == 0:
                    # 暂时没有新音频(刚开始录音 / 已经停了)
                    stop.wait(0.05)
                    continue
                try:
                    engine.feed(chunk)
                except Exception as e:
                    # 喂音频失败(引擎异常)只跳过这一块,线程绝对不能死
                    log.debug("预览喂音频失败: %s", e)
        finally:
            # 预览引擎归这条线程所有:退出前 close 掉它
            try:
                engine.close()
            except Exception as e:
                log.debug("关闭预览引擎失败: %s", e)

    # ---------- 识别与注入 ----------

    def _is_stale(self, session: int) -> bool:
        """这一轮是不是已经作废了(被取消,或被新的一轮取代)。

        作废的识别结果**绝不注入** —— 用户说了不要,就不能贴出去。
        """
        return self._cancelled or session != self._session

    def _process(self, audio: np.ndarray, sample_rate: int, session: int) -> None:
        try:
            # 最终位:整段引擎。喂完整音频、close 出最终文本 ——
            # 和以前直接调 engine.transcribe(audio, rate) 完全等价。
            final = self._final_engine
            if final is None:
                # 兜底:没走过 start_recording(比如测试直接摆状态)也能跑
                final = self._make_final_engine(sample_rate)
                self._final_engine = final
            final.feed(audio)
            text = final.close()
        except ASRError as e:
            if self._is_stale(session):
                return
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="error", text=str(e)))
            self._emit(UIEvent(kind="preview", text=""))
            return
        except Exception as e:
            # 引擎抛任何异常都不能让工作线程死掉
            log.exception("识别异常: %s", e)
            if self._is_stale(session):
                return
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="error", text=f"识别失败: {e}"))
            self._emit(UIEvent(kind="preview", text=""))
            return

        # 作废的一轮:直接丢弃(不注入、不发结果、不动状态 ——
        # 状态可能已经被新一轮的 RECORDING 接管了)
        if self._is_stale(session):
            log.debug("这一轮已被取消,丢弃识别结果")
            return

        self.last_text = text
        if not text.strip():
            self._set_state(State.IDLE)
            self._emit(UIEvent(kind="error", text="没听清,请再说一遍"))
            self._emit(UIEvent(kind="preview", text=""))
            return

        # 粘贴前捕获焦点窗口 —— 转写这 1 秒用户可能切窗口
        hwnd = get_foreground_window()
        # 最后一道作废检查:上面 text.strip() 到这里只有几毫秒,
        # 但用户的取消就落在这个窗口里的话,文字一样不该出门
        if self._is_stale(session):
            log.debug("注入前发现这一轮已被取消,丢弃")
            return
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
            # 失败也要把原文带出去:只报一句"文本注入失败",用户既看不到
            # 自己说了什么,也没法把它捞回来 —— 那段话就等于不存在了。
            # 胶囊会把它显示出来,并给一个「复制」按钮。
            # 文案要点破**兜底出口**:识别结果此刻就留在剪贴板里(没送达
            # 就不还,是注入层的规矩),用户按 Ctrl+V 立刻能贴出来 ——
            # 不说清楚的话,报错的同时文字明明一按就出来,用户却不知道
            # (实测:三连败后手动 Ctrl+V 贴出去了,看着错误提示一头雾水)。
            self._emit(UIEvent(kind="error", text="注入失败，文字已留在剪贴板",
                               payload=text))
        # 最终结果到了,预览文字就没用了(最终结果始终来自整段识别,不是预览)
        self._emit(UIEvent(kind="preview", text=""))

    # ---------- 模型切换 ----------

    def switch_model(self, model_tag: str) -> None:
        """后台切换模型规格(只对**本地**后端有意义)。"""
        if self.backend_kind != BACKEND_LOCAL:
            self._emit(UIEvent(kind="notice",
                               text="网络后端不加载本地模型,切回本地后端才能换模型"))
            return
        if model_tag == getattr(self.engine, "model_tag", None) and \
                getattr(self.engine, "loaded", False):
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

    # ---------- 后端切换 ----------

    def switch_backend(self, kind: str) -> None:
        """后台切换识别后端(本地 ↔ 网络)。

        **互斥**:同一时刻只留一个后端。切换的动作是:
        造新的 → 加载新的 → 卸掉旧的 → 换引用。

        顺序是有讲究的:**先把新的加载好**,失败了才敢卸旧的 ——
        网络不可用(没配 API Key / 端点不通)时用户还能留在本地后端,
        不至于两头都空。

        旧后端被卸掉时,本地后端会把模型从显存里放掉
        (``del model`` + ``torch.cuda.empty_cache()``)——
        这正是"切到网络省显存"落地的地方。

        **录音/识别中拒绝切换**:这一轮的最终位引擎持有旧后端的引用,
        中途换掉会让识别出错或直接丢结果。拒绝时给用户一句提示。
        """
        kind = normalize_backend(kind)
        if kind == self.backend_kind:
            return
        if self.state in (State.RECORDING, State.PROCESSING):
            self._emit(UIEvent(kind="notice",
                               text="录音中不能切换识别后端,请先结束这一轮"))
            return
        threading.Thread(target=self._switch_backend_job, args=(kind,),
                         name="backend-switch", daemon=True).start()

    def _switch_backend_job(self, kind: str) -> None:
        """切换后端的实际动作(跑在后台线程里,不卡 UI)。"""
        label = "网络" if kind == BACKEND_DASHSCOPE else "本地"
        self._emit(UIEvent(kind="notice", text=f"正在切换到{label}后端 ..."))

        with self._backend_lock:
            old = self.engine
            try:
                new = self._make_backend(kind)
                # 先把新后端加载好 —— 失败就留在旧后端
                new.load()
            except ASRError as e:
                log.warning("切换后端失败: %s", e)
                self._emit(UIEvent(kind="notice", text=f"切换失败:{e}"))
                return
            except Exception as e:
                log.exception("切换后端异常: %s", e)
                self._emit(UIEvent(kind="notice", text=f"切换失败:{e}"))
                return

            # 新后端就绪了,再把旧的卸掉(本地后端在这里释放显存)
            self._unload_backend(old)
            self.engine = new
            self.backend_kind = kind

        try:
            self.config.asr_backend = kind
            self.config.save()
        except Exception as e:
            log.debug("保存后端设置失败: %s", e)

        self._emit(UIEvent(kind="backend", text=kind))
        tag = getattr(self.engine, "model_tag", "")
        if tag:
            self._emit(UIEvent(kind="model", model_tag=tag))
        # 预览开关跟着后端走:网络模式没有实时预览,胶囊的预览文字条
        # 干脆收起来(别留一条空条在那儿)。切回本地时按配置恢复。
        self._emit(UIEvent(kind="preview_enabled",
                           text="0" if kind == BACKEND_DASHSCOPE
                           else ("1" if self.config.show_preview else "0")))
        if kind == BACKEND_DASHSCOPE:
            self._emit(UIEvent(kind="notice",
                               text="已切换到网络后端:不支持实时预览,"
                                    "停止录音后一次性识别"))
        else:
            self._emit(UIEvent(kind="notice", text="已切换到本地后端"))
        if self.state in (State.STARTING, State.ERROR):
            self._set_state(State.IDLE)

    # ---------- 生命周期 ----------

    def attach_hotkey(self, listener) -> None:
        """把全局热键监听器挂上来 —— 退出时由 shutdown() 负责停它。

        为什么不让入口自己管:退出有三条路(托盘菜单 / 面板按钮 /
        Ctrl+C),每条都得停热键。挂在 controller 上,shutdown() 一处
        收口,就不会漏。
        """
        self._hotkey = listener

    def shutdown(self) -> None:
        # 热键**第一个**停:它是全局资源,占着不放的话,用户"退出后
        # 马上重启"会因为注册冲突而失败(热键注销要等进程真正退出)。
        # 也不能排在模型卸载后面 —— 那一步要好几秒。
        hotkey = self._hotkey
        if hotkey is not None:
            self._hotkey = None
            try:
                hotkey.stop()
            except Exception:
                pass
        self._stop_preview()
        if self.recorder is not None:
            try:
                self.recorder.stop()
            except Exception:
                pass
            self.recorder = None
