"""识别后端的统一接口(本地 / 网络)。

项目有两种识别后端:

    LocalASRBackend   本地 Qwen3-ASR(transformers,GPU,完全离线)
    MiniMaxBackend    MiniMax 平台网络接口(asr-1.0,multipart 上传音频)

两者实现**同一个抽象** ``ASRBackend``,控制器和流式引擎只认这个接口,
不关心底下跑的是模型还是网络请求:

    transcribe(audio, sample_rate) -> str   识别一段音频(核心方法)
    loaded -> bool                          是否就绪
    model_id -> str                         面板显示用的标识
    load() -> float                         加载,返回耗时秒数
    unload() -> None                        卸载(本地释放显存 / 网络关会话)

**为什么要有 ``unload``**:两种后端是**互斥**的(同一时刻只留一个),
切走后必须真的把资源还回去 —— 本地要 ``del model`` + ``empty_cache()``
把显存还给系统,不然"切到网络省显存"就是空话。

**预览不归后端管**:后端**不声明**"自己支不支持流式"—— 那种能力位
模型会把"引擎种类"和"能不能预览"搅在一起,最后长出"网络引擎不支持
实时预览"这种分支。预览有没有**只由用户开关** ``config.show_preview``
决定:关着就一个预览线程都不建,开着任何后端都建(网络走分句伪流式,
与本地流式平级)。

热词(context biasing)只有本地后端有这条通道:词表有两个来源 ——
config.json 的 ``hotwords`` + ``data/hotwords.txt``(mtime 热加载,改完立即生效),
拼出来的格式是官方那套 ``"Vocabulary: a, b, c."``:
本地走 qwen_asr 的 ``context`` 参数。MiniMax 没有 context biasing 入口,
那一路**不注入**热词(切换成功的通知里会明说,不静默装支持)。
"""

from __future__ import annotations

import abc
import io
import math
import wave
from pathlib import Path

import numpy as np

from ..config import load_hotwords_file
from ..logging_setup import get_logger
from ..paths import hotwords_path

log = get_logger("asr")


class ASRError(Exception):
    """模型加载或推理失败。"""


class ASRBackend(abc.ABC):
    """识别后端的抽象基类。

    子类必须实现 ``transcribe`` / ``load`` / ``unload`` / ``loaded``;
    热词、音频格式转换这些共用的东西放在这里。
    """

    def __init__(self, hotwords: list[str] | None = None,
                 hotwords_file: Path | None = None):
        """
        ``hotwords``      config.json 里配的术语表
        ``hotwords_file`` 术语表文件(默认 ``data/hotwords.txt``)——
                          **每次识别时**检查 mtime,文件变了才重读,
                          所以改完词表立刻生效,不用重启程序。
        """
        self.hotwords = list(hotwords or [])
        self._hotwords_file = Path(hotwords_file) if hotwords_file else hotwords_path()
        self._hw_file_mtime: float | None = None
        self._hw_file_words: list[str] = []

    # ---------- 必须由子类实现 ----------

    @abc.abstractmethod
    def transcribe(self, audio: np.ndarray, sample_rate: int = 16000) -> str:
        """识别音频,返回文本。

        ``sample_rate`` 是**传入音频的实际采样率**(设备原生率,通常是 48 kHz)。
        """

    @abc.abstractmethod
    def load(self) -> float:
        """加载后端,返回耗时秒数。失败抛 ``ASRError``。"""

    @abc.abstractmethod
    def unload(self) -> None:
        """卸载后端:本地释放显存,网络关闭 HTTP 会话。"""

    @property
    @abc.abstractmethod
    def loaded(self) -> bool:
        """后端是否就绪(可以识别了)。"""

    # ---------- 公共 ----------

    @property
    def model_id(self) -> str:
        """面板显示用的标识。"""
        return getattr(self, "model", "") or ""

    # ---------- 热词 ----------

    def _effective_hotwords(self) -> list[str]:
        """合并两个来源的热词:构造时传入的 + 词表文件的。

        词表文件**每次识别时**检查 mtime —— 文件变了就重读,
        所以 ``data/hotwords.txt`` 改完立即生效,不用重启程序。
        文件不存在/读不了是正常情况(只用传入的列表),不报错。
        """
        words = [w.strip() for w in self.hotwords if w and w.strip()]
        try:
            mtime = self._hotwords_file.stat().st_mtime
            if mtime != self._hw_file_mtime:
                self._hw_file_words = load_hotwords_file(self._hotwords_file)
                self._hw_file_mtime = mtime
                if self._hw_file_words:
                    log.info("词表文件已(重)加载:%s(%d 词)",
                             self._hotwords_file, len(self._hw_file_words))
        except OSError:
            # 文件不存在或不可访问 —— 不是错误
            pass
        seen = set(words)
        for w in self._hw_file_words:
            if w not in seen:
                seen.add(w)
                words.append(w)
        return words

    def _build_context(self) -> str:
        """把热词拼成识别提示(context biasing)。

        官方把这个通道叫 **Context Biasing**:可以传任意文本
        (API 上限 10000 token),让模型在**音频模糊时优先选这些词** ——
        公司名、产品代号、医疗术语、领域黑话都靠它校准。

        格式用官方 HF 版的写法 ``"Vocabulary: a, b, c."``:
        模型在 SFT 阶段就是用这种格式训练上下文偏置的,
        格式对上了才吃得到那份偏置,自己编一句提示效果会打折。

        **只有本地后端有这条通道**:交给 qwen_asr 的 ``context`` 参数。
        MiniMax 那边没有 context biasing 入口 —— 它把这个方法覆写成
        空串,热词一律**不注入**(切换成功的通知里会明说这件事,
        不静默装支持)。
        """
        words = self._effective_hotwords()
        if not words:
            return ""
        return "Vocabulary: " + ", ".join(words) + "."

    # ---------- 音频格式 ----------

    @staticmethod
    def _to_float32(audio: np.ndarray) -> np.ndarray:
        """把音频统一成 [-1, 1] 的 float32(单声道)。

        录音器给的是 int16,但 librosa(重采样用)只吃浮点,
        网络后端写 WAV 也要浮点刻度。
        已经在 [-1, 1] 范围的浮点原样保留。

        注意顺序:**先记住原始 dtype**,再混音 ——
        mean 会把 int16 提升成 float64,之后再判断"是不是整数"
        就永远判不到了 —— 那样 int16 的刻度(±32768)会被当成
        已经归一化的浮点,直接 clip 成 ±1(全幅方波)。
        """
        arr = np.asarray(audio)
        was_int = np.issubdtype(arr.dtype, np.integer)
        if arr.ndim > 1:
            arr = arr.mean(axis=-1)
        arr = arr.astype(np.float32)
        if was_int:
            # int16 的刻度是 ±32768,除完正好落在 ±1
            arr = arr / 32768.0
        return np.clip(arr, -1.0, 1.0)


# ---------------- 音频处理(网络后端共用) ----------------
#
# 这两个函数原先住在网络后端那一坨里 —— 后端退役之后它们是**孤儿**,
# 但新后端照样要用(重采样 + 写 WAV),所以上移到基类模块里:
# 放在这儿是因为它们跟 ``_to_float32`` 是同一件事的连体 —— 都是
# "把设备原生的录音变成接口要的格式"。拆到别处只会让调用方多绕一层。

def resample_to(audio: np.ndarray, src_rate: int, dst_rate: int) -> np.ndarray:
    """重采样到目标采样率。

    用 ``scipy.signal.resample_poly``(多相滤波,**带抗混叠低通**)——
    README 里那条坑明说不要用 ``np.interp``:线性插值没有抗混叠滤波,
    8~24 kHz 的成分会折叠回低频,叠在人声基频上(实测失真能量占比 98.71%)。

    ``scipy`` 是延迟导入的:环境里没装 scipy 时,只有真要重采样才报错,
    模块本身照常能 import(本地后端不受影响)。
    """
    src_rate = int(src_rate)
    dst_rate = int(dst_rate)
    if src_rate <= 0 or dst_rate <= 0:
        raise ASRError(f"采样率不合法: {src_rate} → {dst_rate}")
    if src_rate == dst_rate:
        return np.asarray(audio, dtype=np.float32)

    try:
        from scipy.signal import resample_poly
    except ImportError as e:      # pragma: no cover - 环境问题
        raise ASRError("网络后端需要 scipy 做重采样(未安装)") from e

    g = math.gcd(src_rate, dst_rate)
    up = dst_rate // g
    down = src_rate // g
    data = np.asarray(audio, dtype=np.float32)
    return resample_poly(data, up, down).astype(np.float32)


def encode_wav(audio: np.ndarray, sample_rate: int) -> bytes:
    """把浮点音频编码成 WAV 字节流(单声道 / 16-bit PCM)。

    用**标准库** ``wave`` + numpy int16 —— 不引 soundfile
    (那玩意本机没装,而且没必要)。
    """
    pcm = np.clip(np.asarray(audio, dtype=np.float32), -1.0, 1.0)
    pcm = (pcm * 32767.0).astype(np.int16)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(int(sample_rate))
        wf.writeframes(pcm.tobytes())
    return buf.getvalue()
