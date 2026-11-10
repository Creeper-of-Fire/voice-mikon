"""语音识别引擎。"""

from .base import ASRBackend, ASRError
from .engine import ASREngine, LocalASRBackend
from .network import DashScopeBackend
from .streaming import (
    FullPassEngine,
    PreviewEngineKind,
    SentencePreviewEngine,
    StreamingEngine,
    make_preview_engine,
)

__all__ = [
    "ASRBackend",
    "ASRError",
    "ASREngine",
    "LocalASRBackend",
    "DashScopeBackend",
    "FullPassEngine",
    "PreviewEngineKind",
    "SentencePreviewEngine",
    "StreamingEngine",
    "make_preview_engine",
]
