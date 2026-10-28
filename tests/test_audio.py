"""音频模块测试:重采样与电平计算。

不碰真实麦克风 —— 设备探测那部分依赖具体硬件,单独做成手动验证。
"""

import numpy as np
import pytest

from qwen3_voice.audio.recorder import resample, rms_of


def test_resample_same_rate_is_identity():
    audio = np.array([1, 2, 3, 4, 5], dtype=np.int16)
    out = resample(audio, 16000, 16000)
    assert np.array_equal(out, audio)


def test_resample_changes_length_proportionally():
    audio = np.zeros(48000, dtype=np.int16)  # 1 秒 @48k
    out = resample(audio, 48000, 16000)
    assert abs(len(out) - 16000) <= 1


def test_resample_preserves_sine_shape():
    """重采样后的正弦波应该还是正弦波(相位和幅度基本保持)。"""
    sr = 48000
    freq = 440.0
    t = np.arange(sr // 10) / sr
    wave_in = (np.sin(2 * np.pi * freq * t) * 10000).astype(np.int16)

    out = resample(wave_in, sr, 16000).astype(np.float64)
    # 与同样频率的参考正弦比较相关系数
    t_out = np.arange(len(out)) / 16000
    ref = np.sin(2 * np.pi * freq * t_out) * 10000
    corr = np.corrcoef(out, ref)[0, 1]
    assert corr > 0.99, f"相关系数 {corr}"


def test_resample_empty():
    empty = np.zeros((0,), dtype=np.int16)
    assert len(resample(empty, 48000, 16000)) == 0


def test_rms_of_silence():
    assert rms_of(np.zeros(1000, dtype=np.int16)) == 0.0


def test_rms_of_full_scale():
    full = np.full(1000, 32767, dtype=np.int16)
    assert rms_of(full) > 0.99


def test_rms_of_empty():
    assert rms_of(np.zeros((0,), dtype=np.int16)) == 0.0


def test_rms_scales_with_amplitude():
    quiet = (np.ones(1000) * 1000).astype(np.int16)
    loud = (np.ones(1000) * 10000).astype(np.int16)
    assert rms_of(loud) > rms_of(quiet) * 5
