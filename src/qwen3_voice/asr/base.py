"""识别后端的统一接口(本地 / 网络)。

项目有两种识别后端:

    LocalASRBackend   本地 Qwen3-ASR(transformers,GPU,完全离线)
    DashScopeBackend  阿里云百炼 DashScope(OpenAI 兼容 HTTP 接口)

两者实现**同一个抽象** ``ASRBackend``,控制器和流式引擎只认这个接口,
不关心底下跑的是模型还是网络请求:

    transcribe(audio, sample_rate) -> str   识别一段音频(核心方法)
    loaded -> bool                          是否就绪
    model_id -> str                         面板显示用的标识
    load() -> float                         加载,返回耗时秒数
    unload() -> None                        卸载(本地释放显存 / 网络关会话)
    supports_preview -> bool                是否支持实时预览

**为什么要有 ``unload``**:两种后端是**互斥**的(同一时刻只留一个),
切走后必须真的把资源还回去 —— 本地要 ``del model`` + ``empty_cache()``
把显存还给系统,不然"切到网络省显存"就是空话。

**为什么要有 ``supports_preview``**:网络端点是整段请求,做不了
"边说边出字"的逐句预览(硬做只能反复重发全量音频,烧配额且延迟越来越大)。
所以网络后端返回 False,预览位在网络模式下直接不干活 —— 而且必须
在界面上明说,不能静默失效。

热词(context biasing)两个后端共用同一套逻辑:词表有两个来源 ——
config.json 的 ``hotwords`` + ``data/hotwords.txt``(mtime 热加载,改完立即生效),
拼出来的格式是官方那套 ``"Vocabulary: a, b, c."``:
本地走 qwen_asr 的 ``context`` 参数,网络走 system prompt。
"""

from __future__ import annotations

import abc
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

    @property
    def supports_preview(self) -> bool:
        """是否支持实时预览(边说边出字)。

        默认支持;网络后端返回 False —— 兼容端点是整段请求,
        做不了逐句流式预览。
        """
        return True

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

        本地后端把它交给 qwen_asr 的 ``context`` 参数,
        网络后端把它放进 system prompt —— **两边格式一致**,效果可比。
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
