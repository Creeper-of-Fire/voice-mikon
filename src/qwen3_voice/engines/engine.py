"""Qwen3-ASR 识别引擎封装(**本地后端**)。

模型常驻显存,按热键时零等待。0.6B / 1.7B 两个尺寸可运行时切换
(切换会重新加载模型,耗时几秒)。

这是 ``ASRBackend`` 的**本地实现**(见 ``engines/base.py``)——
和网络后端 ``MiniMaxBackend`` 可互换,控制器只认那个抽象接口。
热词(context biasing)的逻辑在基类里,两个后端共用。
"""

from __future__ import annotations

import threading
import time
from pathlib import Path

import numpy as np

from ..logging_setup import get_logger
from .base import ASRBackend, ASRError

log = get_logger("asr")

__all__ = ["ASREngine", "LocalASRBackend", "ASRError"]


class ASREngine(ASRBackend):
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
        super().__init__(hotwords=hotwords, hotwords_file=hotwords_file)
        self.model_root = Path(model_root)
        self.model_tag = model_tag
        self.language = language
        self._model = None
        self._lock = threading.Lock()

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

    def unload(self) -> None:
        """卸载模型,**把显存还给系统**。

        切到网络后端时走这条路 —— 本地模型常驻显存是几 GB,
        不卸掉的话"切到网络省显存"就是空话。

        ``del self._model`` 只是让 Python 的引用计数归零;
        ``torch.cuda.empty_cache()`` 才是真的把缓存块还给驱动
        (否则显存占用数字不会立刻降下来)。
        """
        with self._lock:
            model = self._model
            self._model = None
        del model
        try:
            import torch

            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except Exception as e:
            # 释放缓存失败不影响功能(模型引用已经断了)
            log.debug("释放显存缓存失败: %s", e)

    # ---------- 推理 ----------

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

    # ``_to_float32`` 在基类 ``ASRBackend`` 里(网络后端也要用同一套
    # int16 → [-1,1] float32 的换算,踩坑点也就守在一处)。

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


# 本地后端的"说明性"别名:和 ``MiniMaxBackend`` 并列时读起来更清楚。
# 类本身还是 ``ASREngine``(名字沿用旧代码,少改一圈引用)。
LocalASRBackend = ASREngine
