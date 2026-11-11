"""语音/静音判定层(VAD):只回答"这一帧是不是人声",不管切句。

**这个模块的边界很重要**:它只做**判定**,不做**切句**。

切句状态机(连续静音多久断句、单句上限、最短句长)在
``engines/streaming.py``,那里一行都没动 —— 这里换掉的只是
"这一帧是不是人声"的答案从哪来。

两种实现,同一个接口:

    ``EnergyVadDetector``  能量域判定:底噪估计 + 滞回双门(旧实现,原样保留)
    ``SileroVadDetector``  Silero VAD 神经网络判定(见 ``engines/vad_silero.py``)

**为什么要有这一层**:能量法只看 RMS 高低,分不清"有人小声说话"和
"环境噪声大一点" —— 换环境/换麦之后底噪抬升,固定门限就被穿透过,
"连续静音 0.3s"的判定永不触发。Silero 是训练出来的神经网络判定,
分得清人声和噪声。但神经网络会加载失败、会有不支持的采样率,
所以能量法**保留**作为降级路径,而不是删掉。

**帧长由判定层决定**(``frame_samples``):Silero 要求固定的 32ms 窗
(8k→256 / 16k→512 / 32k→1024 / 48k→1536 样本),能量法则继续用
20ms。两者的帧长不同,但切句阈值一律按**样本数**算
(``0.3s × 采样率``),所以切句行为不随判定层变化。
"""

from __future__ import annotations

import abc
import threading
from typing import Callable

import numpy as np

from ..config import PREVIEW_VAD_MODES, load_config
from ..logging_setup import get_logger
from ..notify import notify

log = get_logger("asr.vad")

# ---- 能量法的调参(原样搬自 engines/streaming.py,数值一个没动)----

# VAD 切句门**不再用固定绝对阈值**。
#
# 旧做法:VAD_SILENCE_RMS = 0.0015,按 2026-10-29 那次真机录音标定
# (USB 麦增益偏低,底噪峰 -63~-60 dB)。换环境后底噪抬到 -52.6 dB
# (2026-11-07 实测),底噪 RMS 0.0017~0.0023 **全部高过门限** ——
# "连续静音 0.3s"的判定永不触发,句子只能等 20s 强切,预览基本不出字。
# 固定阈值在环境面前活不过一次换麦/换房间,已退役。
#
# 现做法:门限 = max(VAD_GATE_MIN, 底噪估计 × 系数),底噪估计由
# 调用方注入(``noise_floor`` callable,Recorder 持有那个滑动窗口
# p10 估计器,见 audio/noise.py)。估计还没收敛(None,冷启动头一秒)
# 时门退到 VAD_GATE_MIN —— 宁可把底噪包进句子(识别不在乎句首尾
# 一点底噪),也不能把轻声开头判成静音(旧注释的教训:0.01 的门
# 曾把 94% 的语音帧判成静音,句子碎成渣,预览大量缺字)。
VAD_GATE_OPEN_FACTOR = 2.5   # 开门:超过底噪 2.5 倍才算"说话了"
VAD_GATE_CLOSE_FACTOR = 1.5  # 关门:低于底噪 1.5 倍才算"停了"
VAD_GATE_MIN = 1e-4          # 估计不可用时的兜底门(≈ -80 dBFS)

# 开关门限为什么要不一样(**滞后**):门限附近若只有一个门,RMS 在
# 门附近抖一下,"有声/静音"就翻转一次,silence_run 反复清零,
# 一句话永远攒不出 0.3s 的连续静音 —— 该切的时候切不出来。
# 开门严(2.5×)、关门松(1.5×),中间留一条滞回带,状态才稳。

# VAD 的分析帧长(毫秒):断句必须在**固定粒度**上判断。
# 不这样的话,识别期间攒下的一大块音频会被当成一整段,
# 里面的句边界就全丢了。
VAD_FRAME_MS = 20.0

# 降级通知的 kind(通知中心按 kind 分类,见 notify.py)
NOTIFY_KIND_FALLBACK = "vad_fallback"


def frame_rms(audio: np.ndarray) -> float:
    """归一化 RMS(0..1)。

    用 chunk **自己**算 —— 便宜,也不依赖录音器的电平通道
    (那条通道给界面画波形用,不该被识别链路依赖)。
    """
    if len(audio) == 0:
        return 0.0
    arr = audio.astype(np.float32)
    if np.issubdtype(audio.dtype, np.integer):
        arr = arr / 32768.0
    return float(np.sqrt(np.mean(arr ** 2)))


class VoiceDetector(abc.ABC):
    """语音判定层:把"这一帧是不是人声"和"怎么切句"彻底分开。

    切句状态机只跟这个接口打交道,所以换判定实现(能量 ↔ Silero)
    不需要碰切句逻辑。
    """

    @property
    @abc.abstractmethod
    def frame_samples(self) -> int:
        """本判定器的一帧多少样本。

        帧长是**判定层的属性**,不是切句层的:Silero 要求固定 32ms 窗,
        能量法用 20ms。切句阈值一律按样本数算,两者互不影响。
        """

    @property
    def requires_exact_frames(self) -> bool:
        """是否要求每帧长度**精确**等于 ``frame_samples``。

        True  = 判定器只吃固定长度的窗(Silero),引擎必须跨块拼帧,
                不足一窗的尾巴留到下一块。
        False = 允许短尾帧(能量法),保持旧行为:按块切,块尾剩多少算多少。
        """
        return False

    @abc.abstractmethod
    def reset(self) -> None:
        """每轮录音开始时重置内部状态。

        Silero 背后是 LSTM,带着上一轮的状态开会判歪 —— 必须重置。
        能量法的滞回状态同理(上一轮结束时正在说话,不该影响下一轮)。
        """

    @abc.abstractmethod
    def is_speech(self, frame: np.ndarray) -> bool:
        """这一帧是不是人声。

        判定层内部自己做**滞回**(能量法的双门 / Silero 的双阈值),
        所以切句层拿到的是一个稳定的状态,而不是每帧抖动的原始值。
        """

    @property
    @abc.abstractmethod
    def in_speech(self) -> bool:
        """当前滞回状态(诊断用:回放工具要逐帧打印判定结果)。"""


class EnergyVadDetector(VoiceDetector):
    """能量域判定:底噪估计 + 滞回双门。

    这是**旧实现,原样保留** —— 从 ``engines/streaming.py`` 搬过来,
    阈值、滞回方向、底噪口径一个都没改。它现在的身份是
    **Silero 不可用时的降级路径**(模型加载失败 / 采样率不支持)。
    """

    def __init__(self, sample_rate: int = 16000,
                 noise_floor: Callable[[], float | None] | None = None,
                 frame_ms: float = VAD_FRAME_MS):
        self._sample_rate = int(sample_rate) or 16000
        # 底噪估计的供给口:callable,每帧问一次当前底噪(None=还没收敛)。
        # 不直接依赖 Recorder —— 依赖的是"谁来供数"这个窄接口,测试里
        # 可以给个固定值的 lambda,行为完全可复现。
        self._noise_floor = noise_floor
        self._frame_samples = max(1, int(frame_ms * self._sample_rate / 1000.0))
        self._in_speech = False

    @property
    def frame_samples(self) -> int:
        return self._frame_samples

    def reset(self) -> None:
        self._in_speech = False

    def _gates(self) -> tuple[float, float]:
        """当前(开门, 关门)门限。估计没收敛就双双退到兜底门。"""
        floor = None
        if self._noise_floor is not None:
            try:
                floor = self._noise_floor()
            except Exception:
                floor = None
        if floor is None or floor <= 0:
            return VAD_GATE_MIN, VAD_GATE_MIN
        return (max(VAD_GATE_MIN, floor * VAD_GATE_OPEN_FACTOR),
                max(VAD_GATE_MIN, floor * VAD_GATE_CLOSE_FACTOR))

    def is_speech(self, frame: np.ndarray) -> bool:
        """判定用**滞后双门**(见 VAD_GATE_* 的说明):

          - 不在说话态:frame_rms 越过开门门(2.5×底噪)才进说话态;
          - 说话态中:frame_rms 跌破关门门(1.5×底噪)才算停。
        """
        level = frame_rms(frame)
        gate_open, gate_close = self._gates()
        if self._in_speech:
            if level < gate_close:
                self._in_speech = False
        elif level > gate_open:
            self._in_speech = True
        return self._in_speech

    @property
    def in_speech(self) -> bool:
        return self._in_speech


class SileroUnavailable(RuntimeError):
    """Silero VAD 用不了(模型缺失 / 加载失败 / 采样率不支持)。

    带 ``reason`` 是为了通知里说清楚**为什么**降级 —— 三条路径的
    排查方向完全不同。
    """

    def __init__(self, reason: str, message: str):
        super().__init__(message)
        self.reason = reason


# 同一个原因只通知一次:降级是**持续状态**(模型文件缺失就一直缺),
# 每轮录音都弹一次通知会把用户淹掉。重复发生只记日志。
_notified_reasons: set[str] = set()
_notify_lock = threading.Lock()


def _notify_fallback(reason: str, message: str, **fields) -> None:
    """发布一条降级通知(同一原因只发一次)。"""
    with _notify_lock:
        first = reason not in _notified_reasons
        _notified_reasons.add(reason)
    if first:
        notify.publish("warning", NOTIFY_KIND_FALLBACK, message,
                       reason=reason, **fields)
    else:
        log.warning("%s", message)


def _configured_kind() -> str:
    """读配置里的 ``preview_vad``。读不到就按默认(silero)走。"""
    try:
        return load_config().preview_vad
    except Exception as e:
        log.warning("读 preview_vad 配置失败,按默认 silero 处理: %s", e)
        return "silero"


def make_vad_detector(vad_kind: str | None = None,
                      sample_rate: int = 16000,
                      noise_floor: Callable[[], float | None] | None = None,
                      ) -> VoiceDetector:
    """造一颗判定器。

    ``vad_kind`` 为 None 时读配置 ``preview_vad``。

    **降级策略**(三条路径都会发一条 warning 通知,kind=vad_fallback):

        模型文件缺失   → 静默降能量法
        模型加载失败   → 静默降能量法
        采样率不支持   → 静默降能量法

    显式配 ``energy`` **不通知** —— 那是用户自己主动选的,不是故障。
    降级永远不抛异常:判定层挂了不该让整个录音流程起不来。
    """
    kind = (vad_kind or _configured_kind() or "").strip().lower()

    if kind == "energy":
        # 用户主动选的,不通知。
        return EnergyVadDetector(sample_rate, noise_floor)

    if kind not in PREVIEW_VAD_MODES:
        _notify_fallback(
            "invalid_config",
            f"preview_vad 取值 {kind!r} 不认识,预览断句已降级为能量判定",
            requested=kind)
        return EnergyVadDetector(sample_rate, noise_floor)

    # Silero 的实现放在 engines/vad_silero.py,那里反过来 import 本模块的
    # 基类 —— 放在函数里 import 是为了避开模块级循环导入。
    try:
        from .vad_silero import SileroVadDetector
        return SileroVadDetector(sample_rate)
    except SileroUnavailable as e:
        _notify_fallback(
            e.reason,
            f"Silero VAD 不可用({e}),预览断句已降级为能量判定",
            sample_rate=int(sample_rate))
        return EnergyVadDetector(sample_rate, noise_floor)
