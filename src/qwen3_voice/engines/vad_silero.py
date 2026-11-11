"""Silero VAD 判定器:神经网络版的"这一帧是不是人声"。

**模型文件随包走,离线优先**:``silero_vad.jit`` 放在包内
(``engines/silero_vad.jit``),随 pip 安装一起分发,运行时**不联网**
下载。上游仓库 snakers4/silero-vad,MIT 许可,允许再分发。

**为什么是神经网络**:能量法只看 RMS 高低,分不清"人在小声说话"和
"环境突然吵了"。真机实测里换环境后底噪抬升,能量门限整轮失效,
预览基本不出字。Silero 是训练过的模型,能把人声和噪声分开。

**采样率**:官方 README 只写了 8000 Hz 和 16000 Hz,但模型自己的
``_validate_input`` 还接受 16000 的整数倍(内部按 ``step = sr // 16000``
抽取到 16k 再算)。实测能跑的组合:

    8000 Hz  → 256 样本/窗
    16000 Hz → 512 样本/窗
    32000 Hz → 1024 样本/窗
    48000 Hz → 1536 样本/窗

全部是 **32ms** 的窗(256/8000 = 512/16000 = 1024/32000 = 1536/48000)。
44100 Hz 这类"不是 16k 整数倍"的率跑不了(模型直接抛 ValueError),
那种设备自动降级到能量法并发通知。

**本项目按设备原生率采集**(常见 48k),支持的率直接按窗长喂,
不做重采样 —— 重采样会引入混叠失真,而原生率喂进去模型自己会抽。
"""

from __future__ import annotations

import threading
from pathlib import Path

import numpy as np

from ..logging_setup import get_logger
from .vad import SileroUnavailable, VoiceDetector

log = get_logger("asr.vad.silero")

# 模型文件(包内资产,随 pip 安装分发)
MODEL_PATH = Path(__file__).resolve().parent / "silero_vad.jit"

# 采样率 → 每次推理需要的样本数(每档都是 32ms 的窗)
#
# 8k/16k 是模型原生支持的;32k/48k 是 16k 的整数倍,模型内部按
# ``step = sr // 16000`` 抽取到 16k 再算 —— 所以喂进去的窗长要按
# 原生率乘上去(48k → 1536 样本,抽完正好 512)。
#
# 实测:48000/1536、32000/1024、16000/512、8000/256 全部 OK;
# 44100/24000 直接抛 ValueError。
SILERO_WINDOW_SAMPLES: dict[int, int] = {
    8000: 256,
    16000: 512,
    32000: 1024,
    48000: 1536,
}

# 进入语音的阈值:概率 ≥ 这个值算"开始说话"。
#
# 取官方推荐值 0.5("lazy 0.5 is pretty good for most datasets")。
# 项目原则是"宁可把底噪包进句子,不吞轻声开头" —— 偏向召回,
# 所以开门不往上调。
SILERO_SPEECH_ON_THRESHOLD = 0.5

# 退出语音的阈值:概率 < 这个值算"停止说话"。
#
# 官方 get_speech_timestamps 的负阈值默认是 ``threshold - 0.15``
# (= 0.35)。比开门低,中间留一条滞回带 —— 和能量法的双门同构:
# 只有一个门的话,概率在门附近抖一下就会翻转,连续静音永远攒不满,
# 该切的时候切不出来。
SILERO_SPEECH_OFF_THRESHOLD = 0.35


# 已加载的模型模板(进程级缓存)。
#
# **为什么要缓存模板**:``torch.jit.load`` 要 40~50ms,每轮录音都重新
# 加载就是每轮白等 50ms。缓存一份"模板",每轮 deepcopy 一份用
# (实测 deepcopy 只要 8.6ms,比重新加载快 5 倍)。
#
# **为什么必须 deepcopy 而不是直接共享**:TorchScript 模型把 LSTM 的
# ``_state``/``_context`` 存在**模块自身**里,两个判定器共用一个模型
# 对象就会互相串味 —— 实测交替喂"有声/静音"时,静音那路的概率被
# 从 0.006 抬到 0.115,判定直接失真。
#
# 这个串味在真机上会发生:``_stop_preview`` 最多等 2 秒就放弃,
# 而一次分句识别要 2~5 秒 —— 旧预览线程可能还活着,新一轮录音的
# 判定器已经建好了。共享模型 = 两轮判定互相污染。
#
# deepcopy 出来的副本自带独立状态,``reset_states()`` 也就只影响自己。
_model_template: dict[str, object] = {}
_model_lock = threading.Lock()


def _load_model(model_path: Path):
    """加载模型**模板**(进程内缓存)。失败抛 SileroUnavailable。"""
    import warnings

    import torch

    key = str(model_path)
    with _model_lock:
        cached = _model_template.get(key)
        if cached is not None:
            return cached

        if not model_path.is_file():
            raise SileroUnavailable(
                "model_missing",
                f"模型文件不存在: {model_path}")
        try:
            with warnings.catch_warnings():
                # torch 2.15 起 ``torch.jit.load`` 会发 FutureWarning,提示
                # 改用 ``torch.export.load``。但那是给 **ExportedProgram**
                # (.pt2)用的,而这个文件是 **TorchScript 归档**(.jit)——
                # 实测 torch.export.load 直接报"必须以 .pt2 结尾"。
                # 上游 silero-vad 用的也是 torch.jit.load,所以照旧,
                # 只把这条 FutureWarning 摁掉,别刷日志。
                warnings.filterwarnings(
                    "ignore", message=".*torch.jit.load is deprecated.*")
                model = torch.jit.load(str(model_path), map_location="cpu")
            model.eval()
        except Exception as e:
            raise SileroUnavailable(
                "model_load_failed",
                f"模型加载失败({model_path}): {e}") from e

        _model_template[key] = model
        return model


def _new_model(model_path: Path):
    """造一份**独立**的模型副本(自己的 LSTM 状态)。"""
    import copy

    template = _load_model(model_path)
    model = copy.deepcopy(template)
    model.eval()
    return model


class SileroVadDetector(VoiceDetector):
    """Silero VAD 判定器:逐窗推理,带滞回。

    一次 ``model(chunk, sample_rate)`` 是亚毫秒级(实测 0.34ms/窗),
    预览线程里同步跑完全够用,不需要另开线程。
    """

    def __init__(self, sample_rate: int = 16000, model_path: Path | None = None):
        self._sample_rate = int(sample_rate)
        window = SILERO_WINDOW_SAMPLES.get(self._sample_rate)
        if window is None:
            raise SileroUnavailable(
                "unsupported_sample_rate",
                f"采样率 {self._sample_rate} Hz 不受支持"
                f"(支持 {'/'.join(str(r) for r in SILERO_WINDOW_SAMPLES)} Hz)")
        self._window_samples = window
        # 每颗判定器用**自己的一份**模型副本 —— LSTM 状态互不干扰。
        # 模板走进程级缓存,副本是 deepcopy(比重新 jit.load 快 5 倍)。
        self._model = _new_model(model_path or MODEL_PATH)
        self._in_speech = False
        # 模型是 LSTM,带着上一轮的状态会判歪 —— 每轮先重置。
        self.reset()

    @property
    def frame_samples(self) -> int:
        return self._window_samples

    @property
    def requires_exact_frames(self) -> bool:
        # Silero 只吃**精确**窗长(模型内部 assert 死了样本数),
        # 所以引擎必须跨块拼帧,不足一窗的留到下一块。
        return True

    def reset(self) -> None:
        """重置滞回状态 + 模型内部 LSTM 状态。"""
        self._in_speech = False
        try:
            self._model.reset_states()
        except Exception as e:
            # reset_states 是 JIT 模型自带的方法,理论上不会失败;
            # 真失败了也不能让录音流程起不来,只是判得稍差。
            log.warning("Silero 状态重置失败: %s", e)

    def is_speech(self, frame: np.ndarray) -> bool:
        """判定这一帧是不是人声(带滞回)。

        迟滞方向和能量法一致:不在说话态时用开门阈值,说话态中用关门阈值。
        """
        prob = self._probability(frame)
        if self._in_speech:
            if prob < SILERO_SPEECH_OFF_THRESHOLD:
                self._in_speech = False
        elif prob >= SILERO_SPEECH_ON_THRESHOLD:
            self._in_speech = True
        return self._in_speech

    @property
    def in_speech(self) -> bool:
        return self._in_speech

    def _probability(self, frame: np.ndarray) -> float:
        """跑一次推理,返回语音概率。

        归一化到 [-1, 1] 再喂 —— 模型是这么训练的。
        """
        import torch

        arr = frame.astype(np.float32)
        if np.issubdtype(frame.dtype, np.integer):
            arr = arr / 32768.0
        tensor = torch.from_numpy(np.ascontiguousarray(arr))
        with torch.no_grad():
            out = self._model(tensor, self._sample_rate)
        return float(out.item())
