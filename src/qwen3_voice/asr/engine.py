"""Qwen3-ASR 识别引擎封装。

模型常驻显存,按热键时零等待。0.6B / 1.7B 两个尺寸可运行时切换
(切换会重新加载模型,耗时几秒)。
"""

from __future__ import annotations

import threading
import time
from pathlib import Path

import numpy as np

from ..config import load_hotwords_file
from ..logging_setup import get_logger
from ..paths import hotwords_path

log = get_logger("asr")


class ASRError(Exception):
    """模型加载或推理失败。"""


class ASREngine:
    """封装 Qwen3ASRModel,支持热切换模型规格。

    热词有**两个来源**,识别时合并(去重):
      1. 构造时传入的列表(来自 config.json 的 ``hotwords`` 字段)
      2. ``data/hotwords.txt`` 文件 —— **每次识别时实时读取**(mtime 缓存),
         所以改完词表文件立刻生效,不用重启。
         产出这个文件用独立程序 ``scripts/hotwords_manager.py``。
    """

    def __init__(self, model_root: str | Path, model_tag: str = "0.6B",
                 language: str | None = None,
                 hotwords: list[str] | None = None,
                 hotwords_file: Path | None = None):
        self.model_root = Path(model_root)
        self.model_tag = model_tag
        self.language = language
        self.hotwords = list(hotwords or [])
        self._model = None
        self._lock = threading.Lock()
        # 词表文件热加载(mtime 缓存,文件变了才重读)
        self._hotwords_file = Path(hotwords_file) if hotwords_file else hotwords_path()
        self._hw_file_mtime: float | None = None
        self._hw_file_words: list[str] = []

    # ---------- 模型 ----------

    @property
    def loaded(self) -> bool:
        return self._model is not None

    @property
    def model_id(self) -> str:
        """当前模型的标识:优先本地权重目录,否则仓库名。"""
        local = self.model_root / f"Qwen3-ASR-{self.model_tag}"
        if local.is_dir():
            return str(local)
        return f"Qwen/Qwen3-ASR-{self.model_tag}"

    def load(self, model_tag: str | None = None) -> float:
        """加载模型(可顺便切换规格),返回耗时秒数。"""
        import torch
        from qwen_asr import Qwen3ASRModel

        tag = model_tag or self.model_tag
        local = self.model_root / f"Qwen3-ASR-{tag}"
        model_id = str(local) if local.is_dir() else f"Qwen/Qwen3-ASR-{tag}"

        log.info("加载模型 %s ...", model_id)
        t0 = time.time()
        try:
            model = Qwen3ASRModel.from_pretrained(
                model_id,
                dtype=torch.bfloat16 if torch.cuda.is_available() else torch.float32,
                device_map="cuda:0" if torch.cuda.is_available() else "cpu",
                max_inference_batch_size=4,
                max_new_tokens=1024,
            )
        except Exception as e:
            raise ASRError(f"模型加载失败: {e}") from e

        with self._lock:
            self._model = model
            self.model_tag = tag
        elapsed = time.time() - t0
        log.info("模型加载完成,耗时 %.1fs", elapsed)
        return elapsed

    def switch_model(self, model_tag: str) -> float:
        """切换模型规格(重新加载)。"""
        if model_tag not in ("0.6B", "1.7B"):
            raise ASRError(f"不支持的模型规格: {model_tag}")
        return self.load(model_tag)

    # ---------- 推理 ----------

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
        """
        words = self._effective_hotwords()
        if not words:
            return ""
        return "Vocabulary: " + ", ".join(words) + "."

    def transcribe(self, audio: np.ndarray, sample_rate: int = 16000,
                   language: str | None = None) -> str:
        """识别音频,返回文本。

        ``sample_rate`` 是**传入音频的实际采样率**(设备原生率,通常是 48 kHz)。
        重采样到 16 kHz 由 ``qwen_asr`` 内部用 librosa 完成 —— 与官方评测
        路径完全一致,不要在外面自己降采样(线性插值没有抗混叠滤波)。

        注意两件事:
        1. qwen_asr 的音频输入必须是 (np.ndarray, sr) 元组或文件路径,
           传裸 ndarray 会报 Unsupported audio input type。
        2. **必须是浮点音频** —— qwen_asr 内部用 librosa 重采样,而 librosa
           只接受浮点。传 int16 会报
           ``ParameterError: Audio data must be floating-point``。
           所以这里把 int16 转成 [-1, 1] 的 float32 再交出去。
        """
        if len(audio) == 0:
            return ""
        if self._model is None:
            raise ASRError("模型尚未加载")

        wav = self._to_float32(audio)
        lang = language if language is not None else self.language
        context = self._build_context()
        t0 = time.time()
        try:
            results = self._model.transcribe(audio=(wav, sample_rate),
                                              language=lang, context=context)
        except Exception as e:
            raise ASRError(f"识别失败: {e}") from e

        text = results[0].text if results else ""
        dur = len(audio) / sample_rate
        log.info("识别完成 %.1fs 音频 / %.1fs 耗时: %s", dur, time.time() - t0, text)
        return text

    @staticmethod
    def _to_float32(audio: np.ndarray) -> np.ndarray:
        """把音频统一成 [-1, 1] 的 float32(单声道)。

        录音器给的是 int16,但 librosa(重采样用)只吃浮点。
        已经在 [-1, 1] 范围的浮点原样保留。
        """
        arr = np.asarray(audio)
        # 注意顺序:**先记住原始 dtype**,再混音。
        # mean 会把 int16 提升成 float64,之后再判断"是不是整数"
        # 就永远判不到了 —— 那样 int16 的刻度(±32768)会被当成
        # 已经归一化的浮点,直接 clip 成 ±1(全幅方波)。
        was_int = np.issubdtype(arr.dtype, np.integer)
        if arr.ndim > 1:
            arr = arr.mean(axis=-1)
        arr = arr.astype(np.float32)
        if was_int:
            # int16 的刻度是 ±32768,除完正好落在 ±1
            arr = arr / 32768.0
        return np.clip(arr, -1.0, 1.0)

    def transcribe_file(self, path: str | Path, language: str | None = None) -> str:
        """识别音频文件。"""
        if self._model is None:
            raise ASRError("模型尚未加载")
        context = self._build_context()
        t0 = time.time()
        results = self._model.transcribe(audio=str(path), language=language,
                                         context=context)
        text = results[0].text if results else ""
        log.info("识别完成 %s / %.1fs 耗时: %s", path, time.time() - t0, text)
        return text
