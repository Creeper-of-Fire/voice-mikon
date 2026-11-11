"""语音识别引擎。"""

from ._http import HttpxTransport
from .base import ASRBackend, ASRError
from .engine import ASREngine, LocalASRBackend
from .minimax import MiniMaxBackend
from .streaming import (
    FullPassEngine,
    PreviewEngineKind,
    SentencePreviewEngine,
    StreamingEngine,
    make_preview_engine,
)
from .vad import (
    EnergyVadDetector,
    VoiceDetector,
    make_vad_detector,
)

__all__ = [
    "ASRBackend",
    "ASRError",
    "ASREngine",
    "LocalASRBackend",
    "MiniMaxBackend",
    "HttpxTransport",
    "EnergyVadDetector",
    "FullPassEngine",
    "PreviewEngineKind",
    "SentencePreviewEngine",
    "StreamingEngine",
    "VoiceDetector",
    "make_preview_engine",
    "make_vad_detector",
]
