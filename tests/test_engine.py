"""识别引擎的音频格式契约测试。

守护的是**会真的炸掉用户的**几件事:
  - 传给 qwen_asr 的必须是浮点音频(librosa 只吃浮点,传 int16 直接报
    ParameterError: Audio data must be floating-point)
  - int16 转 float32 后刻度要正确(除 32768,落在 ±1)
  - 空音频不报错
"""

import numpy as np
import pytest

from qwen3_voice.asr.engine import ASREngine


@pytest.fixture
def engine():
    e = ASREngine(model_root="dummy", hotwords=[])
    return e


def test_int16_becomes_float32(engine):
    """int16 必须转成 float32 —— librosa 只接受浮点。"""
    a = np.array([0, 16384, -16384, 32767, -32768], dtype=np.int16)
    out = engine._to_float32(a)
    assert out.dtype == np.float32
    assert np.isfinite(out).all()


def test_int16_scale_is_correct(engine):
    """int16 的刻度: 16384 -> 0.5, -32768 -> -1.0。"""
    a = np.array([0, 16384, -16384, 32767, -32768], dtype=np.int16)
    out = engine._to_float32(a)
    assert out[0] == pytest.approx(0.0, abs=1e-6)
    assert out[1] == pytest.approx(0.5, abs=1e-4)
    assert out[2] == pytest.approx(-0.5, abs=1e-4)
    assert out[3] == pytest.approx(1.0, abs=1e-4)
    assert out[4] == pytest.approx(-1.0, abs=1e-4)


def test_output_within_unit_range(engine):
    """输出必须落在 [-1, 1],否则 librosa 的峰值归一会失真。"""
    a = (np.random.default_rng(0).normal(0, 20000, 1000)).astype(np.int16)
    out = engine._to_float32(a)
    assert out.min() >= -1.0
    assert out.max() <= 1.0


def test_float_input_kept_as_is(engine):
    """已经是 [-1,1] 的浮点不该被二次缩放。"""
    a = np.array([0.0, 0.5, -0.5, 1.0, -1.0], dtype=np.float32)
    out = engine._to_float32(a)
    assert out[1] == pytest.approx(0.5, abs=1e-6)
    assert out[3] == pytest.approx(1.0, abs=1e-6)


def test_stereo_downmixed(engine):
    """多声道要混成单声道(shape (帧数, 声道数) -> 帧数)。"""
    a = np.array([[1000, 3000], [2000, 4000]], dtype=np.int16)
    out = engine._to_float32(a)
    assert out.ndim == 1
    assert out.shape[0] == 2
    assert out[0] == pytest.approx(2000 / 32768.0, abs=1e-4)
    assert out[1] == pytest.approx(3000 / 32768.0, abs=1e-4)


def test_empty_audio(engine):
    """空音频不炸。"""
    out = engine._to_float32(np.zeros((0,), dtype=np.int16))
    assert out.shape == (0,)


def test_transcribe_empty_audio_returns_empty(engine):
    """空音频直接返回空串,不调模型。"""
    assert engine.transcribe(np.zeros((0,), dtype=np.int16), 48000) == ""


def test_transcribe_requires_model(engine):
    """模型没加载就调用要报 ASRError(而不是 AttributeError)。"""
    from qwen3_voice.asr.engine import ASRError
    with pytest.raises(ASRError):
        engine.transcribe(np.zeros(100, dtype=np.int16), 16000)


def test_build_context_empty(engine):
    """没有热词时 context 是空串 —— 不能塞个 "Vocabulary: ." 进去。"""
    assert engine._build_context() == ""


def test_build_context_official_format():
    """热词按官方格式拼: 'Vocabulary: a, b, c.'"""
    e = ASREngine(model_root="dummy", hotwords=["Qwen3-ASR", "PlasmaService"])
    assert e._build_context() == "Vocabulary: Qwen3-ASR, PlasmaService."


def test_build_context_skips_blanks():
    """空词/空白词要跳过。"""
    e = ASREngine(model_root="dummy", hotwords=["", "  ", "A", " B "])
    assert e._build_context() == "Vocabulary: A, B."
