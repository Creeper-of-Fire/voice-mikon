"""音频模块测试:录音采集契约与电平计算。

不碰真实麦克风 —— 设备探测那部分依赖具体硬件,单独做成手动验证。

关于重采样:项目**自己不做降采样**,而是把设备原生采样率的音频
连同样率一起交给 ``qwen_asr``,由它内部用 librosa 做高质量重采样
(与官方评测路径一致)。自己用线性插值降采样没有抗混叠滤波,
8~24 kHz 的成分会折叠回低频,叠在人声基频上。
"""

import numpy as np
import pytest

from qwen3_voice.audio.recorder import Recorder, frames_to_int16, rms_of


def _recorder_with_frames(native_rate: int, n_frames: int = 2,
                          frames: int = 800) -> Recorder:
    """造一个装着假数据的录音器(不走采集回调的设备路径)。"""
    rec = Recorder(device=None, target_rate=16000)
    rec.native_rate = native_rate
    for i in range(n_frames):
        block = np.full((frames, 1), 1000 * (i + 1), dtype=np.int16)
        rec._callback(block, frames, None, None)
    return rec


# ---------------- 采样率契约 ----------------

def test_stop_returns_native_rate_audio():
    """stop() 返回**原生采样率**的音频,不做降采样。

    重采样交给 qwen_asr 内部做(高质量 + 抗混叠),
    项目自己降采样会毁掉高频信息。
    """
    rec = _recorder_with_frames(48000)
    out = rec.stop()
    # 2 帧 × 800 样本 @48k = 1600 样本,降采样到 16k 只会剩 533
    assert len(out) == 1600
    assert out.dtype == np.int16


def test_native_rate_16k_is_unchanged():
    """设备本来就是 16 kHz 时,采样率与长度都不变。"""
    rec = _recorder_with_frames(16000)
    out = rec.stop()
    assert len(out) == 1600


def test_stop_matches_snapshot_content():
    """stop() 与 snapshot() 内容一致(只是采样率可能不同)。"""
    rec = _recorder_with_frames(48000)
    snap = rec.snapshot()
    stopped = rec.stop()
    assert np.array_equal(snap, stopped)


def test_snapshot_does_not_consume_audio():
    """多次取快照不能消费音频 —— stop() 仍然拿到完整数据。"""
    rec = _recorder_with_frames(48000)
    for _ in range(3):
        assert len(rec.snapshot()) == 1600
    assert len(rec.stop()) == 1600


def test_stop_empty():
    rec = Recorder(device=None, target_rate=16000)
    assert len(rec.stop()) == 0


def test_frames_to_int16_downmixes_stereo():
    """多声道帧要混成单声道(mean over channels)。"""
    frames = [np.array([[100, 200], [300, 400]], dtype=np.int16)]
    out = frames_to_int16(frames)
    assert out.tolist() == [150, 350]


def test_frames_to_int16_empty():
    assert len(frames_to_int16([])) == 0


def test_frames_to_int16_keeps_int16_dtype():
    frames = [np.array([1, 2, 3], dtype=np.int16)]
    assert frames_to_int16(frames).dtype == np.int16


# ---------------- 电平 ----------------

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
