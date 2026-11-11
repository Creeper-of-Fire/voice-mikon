"""流式识别引擎:统一接口 + 两个实现。

这个模块是"预览 / 最终双插槽"重构的核心。

**只有一个引擎接口**(只有这一种形态):

    feed(chunk)          持续喂音频 chunk;实现内部可在任意时刻回调 partial(text)
    close() -> final     结束,返回最终文本

预览位和最终位各插一颗引擎,能力差异全在实现里,接口不做任何特殊化:

    预览位 —— 要"快":``SentencePreviewEngine``,切一句认一句,整体替换预览文字
    最终位 —— 要"准":``FullPassEngine``,一次性全量识别

**任何引擎都能插任何位** —— 插槽只决定"这个引擎的输出给谁用",
不决定引擎本身的能力。默认组合是"预览位分句 + 最终位整段",
整段引擎插预览位(旧行为)也照样能跑(见 ``FullPassEngine`` 的刷新策略)。

为什么分句不一致只允许污染预览层:分句识别天然会断在奇怪的地方,
一句话被切成两半、标点乱跳都是常态。但最终注入的文字必须干净 ——
所以最终位固定整段引擎,预览层怎么马虎都不影响它。
"""

from __future__ import annotations

import abc
import threading
import time
from enum import Enum
from typing import Callable, Iterator

import numpy as np

from ..logging_setup import get_logger
from .base import ASRBackend
from .vad import (
    VAD_FRAME_MS,
    EnergyVadDetector,
    VoiceDetector,
    frame_rms,
    make_vad_detector,
)

log = get_logger("asr.stream")

# ---- 调参默认值(都集中在这里,方便按真机效果微调)----

# 注意:底噪门限(``VAD_GATE_*``)和判定帧长(``VAD_FRAME_MS``)已经搬到
# ``engines/vad.py`` —— 它们属于**判定层**(怎么判"这是不是人声"),
# 不属于**切句层**(怎么切句)。切句层的参数一个都没动:
#
#   SENTENCE_SILENCE_SECONDS  连续静音多久断句(秒)
#   SENTENCE_MAX_SECONDS      单句上限(秒)
#   SENTENCE_MIN_SECONDS      单句下限(秒)
#
# 换判定层(能量 ↔ Silero)不影响这三个:它们一律按**样本数**算,
# 与判定帧长无关。

# 连续静音多久算断句(秒)
SENTENCE_SILENCE_SECONDS = 0.3

# 单句上限(秒):一直说不停也得切,否则预览会一直干等
SENTENCE_MAX_SECONDS = 20.0

# 单句下限(秒):太短的片段识别不出东西,丢掉
SENTENCE_MIN_SECONDS = 0.2

# 整段引擎插在预览位时的重识别节奏(秒,墙钟)与自适应系数:
# 实际间隔 = max(节奏, 上次推理耗时 × 系数)——
# 推理越慢间隔越长,避免请求越积越多(重喂全量音频的开销随长度线性涨)。
FULL_PASS_REFRESH_SECONDS = 1.5
FULL_PASS_REFRESH_FACTOR = 1.2
# 预览位整段重识别之前至少要有的音频长度(秒):太短的片段识别不出东西
FULL_PASS_MIN_AUDIO_SECONDS = 0.5


class PreviewEngineKind(str, Enum):
    """预览位用哪颗引擎(代码内可切换,**不做**成用户配置项)。

    ``SENTENCE``   分句预览(默认):切句就出字,跟手
    ``FULL_PASS``  旧行为:每轮把累积音频整段重识别
    """

    SENTENCE = "sentence"
    FULL_PASS = "full_pass"


class StreamingEngine(abc.ABC):
    """流式识别引擎的统一接口。

    子类只需要实现 ``feed`` 和 ``close``;音频格式转换、采样率、
    线程安全这些共用的东西都放在这里。
    """

    def __init__(self, engine: ASRBackend, sample_rate: int = 16000):
        """
        ``engine``   底层的整段识别器(``ASRBackend`` 或任何有
                     ``transcribe(audio, sample_rate)`` 的对象)
        ``sample_rate`` 喂进来的音频的采样率 —— 必须是**音频的实际采样率**
                     (设备原生率),重采样由 qwen_asr 内部做。
        """
        self._engine = engine
        self._sample_rate = int(sample_rate) or 16000
        self._closed = False
        self._final: str | None = None
        # feed 和 close 都要碰内部状态,加锁防止并发调用撞车
        # (比如预览线程还在识别,主线程就 close 了)。
        self._lock = threading.Lock()

    @property
    def sample_rate(self) -> int:
        return self._sample_rate

    @property
    def closed(self) -> bool:
        return self._closed

    @abc.abstractmethod
    def feed(self, chunk: np.ndarray) -> None:
        """喂一段音频进来。

        实现内部可以在任意时刻回调 ``partial(text)`` 报告进展。
        """

    @abc.abstractmethod
    def close(self) -> str:
        """结束,返回最终文本。

        必须**幂等**:重复调用返回同一个结果,不重复识别。
        """

    # ---------- 共用工具 ----------

    @staticmethod
    def _as_audio(chunk) -> np.ndarray | None:
        """把任意形态的音频块统一成单声道 ndarray。"""
        if chunk is None:
            return None
        arr = np.asarray(chunk)
        if arr.ndim > 1:
            # 多声道混成单声道。注意整数刻度要还原:
            # mean 会把 int16 提升成 float64,之后再当成"已归一化的浮点"
            # 就会把 ±32768 的样本当成 ±1 的音频,直接毁掉。
            was_int = np.issubdtype(arr.dtype, np.integer)
            arr = arr.mean(axis=-1)
            if was_int:
                arr = arr.astype(np.int16)
        if arr.size == 0:
            return None
        return arr

    @staticmethod
    def _rms(audio: np.ndarray) -> float:
        """归一化 RMS(0..1)。

        用 chunk **自己**算 —— 便宜,也不依赖录音器的电平通道
        (那条通道给界面画波形用,不该被识别链路依赖)。

        实现搬到判定层了(``vad.frame_rms``),这里留个壳 ——
        回放工具还要用它逐帧打印 RMS。
        """
        return frame_rms(audio)

    @staticmethod
    def _concat(parts: list[np.ndarray]) -> np.ndarray:
        if not parts:
            return np.zeros((0,), dtype=np.int16)
        if len(parts) == 1:
            return parts[0]
        return np.concatenate(parts)


class FullPassEngine(StreamingEngine):
    """整段引擎:feed 只攒 chunk,close() 时一次性全量识别。

    包装现有的 ``ASRBackend.transcribe(audio, rate)``,
    **最终位用的就是它** —— 所以最终注入的文字和以前完全一样,
    一个字都不会因为预览层改了而变。

    插在预览位时(旧行为的回退)会周期性地把累积音频整体重识别一次
    并回调 ``partial``:因为预览位要的是"过程中的文字",
    而整段引擎只会在 close() 时给最终结果 —— 那时预览早就结束了。
    给不给 ``partial`` 回调就是这两种用法的区别:
      - 不给(最终位):只在 close() 识别一次
      - 给(预览位):按节奏重识别并回调
    """

    def __init__(self, engine: ASRBackend, sample_rate: int = 16000,
                 partial: Callable[[str], None] | None = None,
                 refresh_seconds: float = FULL_PASS_REFRESH_SECONDS,
                 refresh_factor: float = FULL_PASS_REFRESH_FACTOR,
                 min_audio_seconds: float = FULL_PASS_MIN_AUDIO_SECONDS):
        super().__init__(engine, sample_rate)
        self._partial = partial
        self._refresh_seconds = float(refresh_seconds)
        self._refresh_factor = float(refresh_factor)
        self._min_samples = max(1, int(min_audio_seconds * self._sample_rate))
        self._parts: list[np.ndarray] = []
        self._total_samples = 0
        self._last_refresh = 0.0
        self._last_cost = 0.0

    def feed(self, chunk: np.ndarray) -> None:
        with self._lock:
            if self._closed:
                return
            data = self._as_audio(chunk)
            if data is None:
                return
            self._parts.append(data)
            self._total_samples += len(data)
            self._refresh_if_due()

    def close(self) -> str:
        with self._lock:
            if self._final is not None:
                return self._final
            self._closed = True
            if not self._parts:
                self._final = ""
                return self._final
            audio = self._concat(self._parts)
            self._parts = []
            try:
                self._final = self._engine.transcribe(audio, self._sample_rate) or ""
            except Exception:
                # 失败也要把状态定死,免得 close() 被重试一次再跑一遍推理
                self._final = ""
                raise
            return self._final

    # ---------- 预览位专用的刷新 ----------

    def _refresh_if_due(self) -> None:
        """把累积音频整体重识别一次,并回调 partial(旧行为)。

        只在给了 ``partial`` 回调时才有意义 —— 最终位不给回调,
        它只在 close() 时识别一次。
        """
        if self._partial is None or self._refresh_seconds <= 0:
            return
        if self._total_samples < self._min_samples:
            # 音频还太短:识别不出东西,白跑一次推理
            return

        now = time.monotonic()
        # 间隔自适应:推理越慢,间隔拉得越长,避免越积越多
        interval = max(self._refresh_seconds, self._last_cost * self._refresh_factor)
        if now - self._last_refresh < interval:
            return

        audio = self._concat(self._parts)
        t0 = time.monotonic()
        try:
            text = self._engine.transcribe(audio, self._sample_rate) or ""
        except Exception as e:
            # 识别失败(引擎异常 / 切换模型时旧模型被换掉)只跳过这一轮。
            # 保持 debug:这是**旧回退路径**(默认走分句引擎),而且每 1.5 秒
            # 就会失败一次 —— 升到 WARNING 只会刷屏,分句路径的日志才有用。
            log.debug("整段预览识别失败: %s", e)
            return
        self._last_cost = time.monotonic() - t0
        self._last_refresh = time.monotonic()
        if text.strip():
            self._notify(text)

    def _notify(self, text: str) -> None:
        if self._partial is None:
            return
        try:
            self._partial(text)
        except Exception as e:
            log.debug("partial 回调异常: %s", e)


class SentencePreviewEngine(StreamingEngine):
    """分句预览引擎:边说边切句,切一句认一句。

    做法:

    1. ``feed`` 收到音频块,自己按固定帧长切帧
    2. 每帧交给**判定层**("这是不是人声")—— 能量法或 Silero VAD,
       由 ``VoiceDetector`` 实现,切句逻辑不关心是哪一个
    3. 连续静音约 300ms → 断句;单句超过 20s → 强切
       (一直说不停也得切,否则预览会一直干等)
    4. 断句后立刻识别这一句,把结果拼到已识别文本后面,
       通过 ``partial`` 回调吐出去 —— 回调给的是**已识别句子的拼接**,
       调用方按"整体替换"处理即可
    5. ``close`` 把最后一句也认掉,返回全部句子的拼接

    拼接就是直接 join,不做任何跨句去重:句子是按静音切出来的,
    本来就不重叠,硬做去重反而会把正常内容吃掉。
    真出现重复了再说。

    **切句状态机与判定层是分开的**:静音时长(0.3s)、强切(20s)、
    最短句长(0.2s)这些参数一个都没改,换的只是"这一帧是不是
    人声"的答案从哪来。
    """

    def __init__(self, engine: ASRBackend, sample_rate: int = 16000,
                 partial: Callable[[str], None] | None = None,
                 noise_floor: Callable[[], float | None] | None = None,
                 silence_seconds: float = SENTENCE_SILENCE_SECONDS,
                 max_seconds: float = SENTENCE_MAX_SECONDS,
                 min_seconds: float = SENTENCE_MIN_SECONDS,
                 frame_ms: float = VAD_FRAME_MS,
                 detector: VoiceDetector | None = None):
        super().__init__(engine, sample_rate)
        self._partial = partial
        # 底噪估计的供给口:callable,每帧问一次当前底噪(None=还没收敛)。
        # 不直接依赖 Recorder —— 依赖的是"谁来供数"这个窄接口,测试里
        # 可以给个固定值的 lambda,行为完全可复现。
        # 只有能量法判定器用它(Silero 不看底噪)。
        self._noise_floor = noise_floor
        self._silence_samples = max(1, int(silence_seconds * self._sample_rate))
        self._max_samples = max(1, int(max_seconds * self._sample_rate))
        self._min_samples = max(1, int(min_seconds * self._sample_rate))

        # 判定层:默认能量法(底噪估计 + 滞回双门)。
        # 传 detector 进来就换成别的实现(比如 Silero VAD)。
        self._detector = detector if detector is not None else EnergyVadDetector(
            self._sample_rate, noise_floor, frame_ms)
        # 判定帧长由判定层决定(Silero 要 32ms 整窗,能量法 20ms)。
        # 切句阈值全按样本数算,所以帧长变了也不影响切句行为。
        self._frame_samples = max(1, int(self._detector.frame_samples))
        # 判定器要求精确帧长时(Silero),跨块拼帧 —— 不足一窗的尾巴
        # 留到下一块再拼。录音器的块(1024 样本)通常切不进一个 32ms 窗。
        self._carry = np.zeros((0,), dtype=np.int16) if self._detector.requires_exact_frames else None

        self._frames: list[np.ndarray] = []   # 当前句的音频帧
        self._frame_count = 0                 # 当前句的样本数
        self._speech_samples = 0              # 当前句里"有声"的样本数
        self._silence_run = 0                 # 连续静音的样本数
        self._trailing_silence = 0            # 句尾连续静音的样本数(切句时裁掉)
        self._sentences: list[str] = []
        # 滞后双门的状态:"现在算说话中吗"。判定层自己维护这个状态
        # (能量法是双门,Silero 是双阈值),这里只留一份镜像给诊断用。
        self._in_speech = False


    # ---------- VAD 门 ----------

    # 注:底噪门限的算法已经搬到判定层(``EnergyVadDetector``),
    # 引擎这边不再自己算门 —— 判定层换实现时引擎不用动。

    # ---------- 接口 ----------

    def feed(self, chunk: np.ndarray) -> None:
        with self._lock:
            if self._closed:
                return
            data = self._as_audio(chunk)
            if data is None:
                return
            for frame in self._split(data):
                self._process_frame(frame)

    def close(self) -> str:
        with self._lock:
            if self._final is not None:
                return self._final
            self._closed = True
            # 判定器要求精确帧长时(Silero),最后可能还剩半窗没判过 ——
            # 补零凑满一窗判掉,否则这截音频永远进不了句子。
            # 补出来的零就是尾部静音,会被 _flush 的句尾裁剪去掉。
            self._flush_carry()
            # 最后一句也要认掉 —— close() 的语义就是"给我最终文本"。
            # 但**不再回调 partial**:停止录音时预览条马上就要被
            # 最终结果接管,再推一次只会闪一下。
            self._flush(notify=False)
            self._final = "".join(self._sentences)
            return self._final

    # ---------- 内部 ----------

    def _split(self, data: np.ndarray) -> Iterator[np.ndarray]:
        """把音频块切成固定长度的分析帧。

        必须切:识别期间攒下的一大块音频,如果不切就整块算 RMS,
        里面的静音/有声边界会被抹平,句子永远断不出来。

        **帧长由判定层决定**(能量法 20ms / Silero 32ms 整窗)。
        切法分两种:

        - 判定器不要求精确帧长(能量法):按老规矩切,块尾不足一帧的
          零头也照样成帧 —— **保持旧行为不变**。
        - 判定器要求精确帧长(Silero):模型只吃固定长度的窗(喂错长度
          直接抛 ValueError),所以跨块攒够一整窗才成帧,零头留到下一块。
        """
        step = self._frame_samples
        if self._carry is None:
            for start in range(0, len(data), step):
                yield data[start:start + step]
            return
        buf = np.concatenate([self._carry, data]) if len(self._carry) else data
        usable = len(buf) - (len(buf) % step)
        self._carry = buf[usable:]
        for start in range(0, usable, step):
            yield buf[start:start + step]

    def _flush_carry(self) -> None:
        """收尾:把攒着的半窗补零凑满,判掉。"""
        if self._carry is None or len(self._carry) == 0:
            return
        carry = self._carry
        self._carry = np.zeros((0,), dtype=carry.dtype)
        pad = self._frame_samples - len(carry)
        if pad > 0:
            carry = np.concatenate([carry, np.zeros(pad, dtype=carry.dtype)])
        self._process_frame(carry)

    def _process_frame(self, frame: np.ndarray) -> None:
        """处理一个分析帧:更新判定状态,必要时断句。

        "这是不是人声"由**判定层**回答(能量法的滞后双门 /
        Silero 的双阈值),这里只把答案拿来喂切句状态机 ——
        状态机本身一行没改。
        """
        speech = self._detector.is_speech(frame)
        self._in_speech = speech
        silent = not speech

        if not silent and self._speech_samples == 0:
            # 有声来了,但这一句前面还挂着静音(断句后残留的那点尾巴)——
            # 把它扔掉:句子从头开始,别让静音混进识别。
            self._reset()
        self._frames.append(frame)
        self._frame_count += len(frame)
        if silent:
            self._silence_run += len(frame)
            self._trailing_silence += len(frame)
        else:
            self._silence_run = 0
            self._trailing_silence = 0
            self._speech_samples += len(frame)

        if self._frame_count >= self._max_samples:
            # 强切:一直说不停也得切
            self._flush()
            return
        if self._silence_run < self._silence_samples:
            return
        if self._speech_samples > 0:
            self._flush()
        else:
            # 全是静音,不是一句话 —— 丢掉。
            # 留着会把后面那句的音频前面塞一大段静音,
            # 识别时容易冒出幻觉文字。
            self._reset()

    def _flush(self, notify: bool = True) -> None:
        """把当前句切出去:识别 + 回调。"""
        frames = self._frames
        total = self._frame_count
        speech = self._speech_samples
        trailing = self._trailing_silence
        self._reset()

        if not frames or total <= 0 or speech <= 0:
            return
        audio = self._concat(frames)
        if trailing > 0:
            # 裁掉句尾的静音:它不属于这句话,留着只会拖慢识别
            audio = audio[:max(0, len(audio) - trailing)]
        if len(audio) < self._min_samples:
            # 太短,识别不出东西 —— 丢掉,别往预览里塞垃圾
            return

        text = self._recognize(audio)
        if not text:
            return
        # INFO 级:真机日志里要能直接看到"分句在出字"。
        # 之前这条链路上只有 debug,一旦预览零出字,日志一片空白,
        # 分不清是"没识别"还是"识别了没显示"——这两种的修法完全不同。
        # 一句一条,不会刷屏。
        log.info("预览句 %.1fs: %s", len(audio) / self._sample_rate, text)
        self._sentences.append(text)
        if notify:
            self._notify("".join(self._sentences))

    def _reset(self) -> None:
        """清空当前句的状态。"""
        self._frames = []
        self._frame_count = 0
        self._speech_samples = 0
        self._silence_run = 0
        self._trailing_silence = 0
    def _recognize(self, audio: np.ndarray) -> str:
        """识别一句。失败只丢这一句,引擎不能死。"""
        try:
            return (self._engine.transcribe(audio, self._sample_rate) or "").strip()
        except Exception as e:
            # 模型没加载 / 切模型时旧模型被换掉 —— 只跳过这一句。
            # WARNING 级(而不是 debug):这正是"预览零出字"的**第一种**
            # 可能原因,必须肉眼可见;而且只在真出错时才出现,不会刷屏。
            log.warning("分句识别失败: %s", e)
            return ""

    def _notify(self, text: str) -> None:
        if self._partial is None or not text:
            return
        try:
            self._partial(text)
        except Exception as e:
            # 认出来了但推不出去 —— "预览零出字"的**第二种**可能原因。
            # 同样提到 WARNING:能区分"没识别"和"没显示",比 debug 有用得多。
            log.warning("partial 回调异常: %s", e)


def make_preview_engine(kind: PreviewEngineKind | str,
                        engine: ASRBackend, sample_rate: int = 16000,
                        partial: Callable[[str], None] | None = None,
                        noise_floor: Callable[[], float | None] | None = None,
                        vad_kind: str | None = None
                        ) -> StreamingEngine:
    """按 kind 造一颗预览引擎。

    kind 是 ``PreviewEngineKind.SENTENCE`` 就分句(默认),
    是 ``FULL_PASS`` 就退回旧行为(整段重识别)。

    ``noise_floor`` 是底噪估计的供给口(仅分句引擎用,整段引擎不切句),
    见 ``SentencePreviewEngine`` 的说明。

    ``vad_kind`` 是判定层的选择(``silero`` / ``energy``),None 时读配置
    ``preview_vad``。**只有分句引擎需要判定层** —— 整段引擎不切句,
    所以压根不造判定器,也就不会因为 Silero 不可用而降级。
    """
    if kind == PreviewEngineKind.FULL_PASS:
        return FullPassEngine(engine, sample_rate, partial=partial)
    detector = make_vad_detector(vad_kind, sample_rate, noise_floor)
    return SentencePreviewEngine(engine, sample_rate, partial=partial,
                                 noise_floor=noise_floor, detector=detector)
