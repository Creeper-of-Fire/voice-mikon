"""Qwen3-ASR 识别引擎封装。

模型常驻显存,按热键时零等待。0.6B / 1.7B 两个尺寸可运行时切换
(切换会重新加载模型,耗时几秒)。
"""

from __future__ import annotations

import threading
import time
from pathlib import Path

import numpy as np

from ..logging_setup import get_logger

log = get_logger("asr")


class ASRError(Exception):
    """模型加载或推理失败。"""


class ASREngine:
    """封装 Qwen3ASRModel,支持热切换模型规格。"""

    def __init__(self, model_root: str | Path, model_tag: str = "0.6B",
                 language: str | None = None,
                 hotwords: list[str] | None = None):
        self.model_root = Path(model_root)
        self.model_tag = model_tag
        self.language = language
        self.hotwords = list(hotwords or [])
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

    # ---------- 推理 ----------

    def _build_context(self) -> str:
        """把热词拼成识别提示。

        qwen_asr 的 ``context`` 会作为 system role 喂给模型 ——
        也就是在**识别阶段**就提示它这些词会出现,而不是识别完再改写。
        这比"识别完用 LLM 润色"稳得多:不会凭空猜错别字,不会改掉原意。
        """
        words = [w.strip() for w in self.hotwords if w and w.strip()]
        if not words:
            return ""
        return "以下是可能出现的专有名词,请准确转写:" + "、".join(words)

    def transcribe(self, audio: np.ndarray, sample_rate: int = 16000,
                   language: str | None = None) -> str:
        """识别音频,返回文本。

        注意 qwen_asr 的音频输入必须是 (np.ndarray, sr) 元组或文件路径,
        传裸 ndarray 会报 Unsupported audio input type。
        """
        if self._model is None:
            raise ASRError("模型尚未加载")
        if len(audio) == 0:
            return ""

        lang = language if language is not None else self.language
        context = self._build_context()
        t0 = time.time()
        try:
            results = self._model.transcribe(audio=(audio, sample_rate),
                                              language=lang, context=context)
        except Exception as e:
            raise ASRError(f"识别失败: {e}") from e

        text = results[0].text if results else ""
        dur = len(audio) / sample_rate
        log.info("识别完成 %.1fs 音频 / %.1fs 耗时: %s", dur, time.time() - t0, text)
        return text

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
