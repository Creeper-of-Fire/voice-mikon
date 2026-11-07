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
from qwen3_voice.ui.panel import dbfs_to_unit


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


def test_snapshot_matches_snapshot_content_at_device_rate():
    """16 kHz 设备:快照和 stop() 逐样本一致。

    两者内容必须一样 —— "预览看到的音频"和"停止后交给识别的音频"
    不是同一份的话,预览和最终结果就会对不上。
    """
    rec = _recorder_with_frames(16000)
    snap = rec.snapshot()
    stopped = rec.stop()
    assert snap.dtype == np.int16
    assert len(snap) == 1600
    assert np.array_equal(snap, stopped)


def test_snapshot_is_native_rate_too():
    """快照也是**原生采样率** —— 和 stop() 一样,不做降采样。

    重采样交给 qwen_asr 内部做(高质量 + 抗混叠),
    项目自己降采样会毁掉高频信息。
    """
    rec = _recorder_with_frames(48000)
    snap = rec.snapshot()
    assert len(snap) == 1600                 # 48 k 录到的就是 1600 个样本
    stopped = rec.stop()
    assert len(stopped) == 1600              # 同样是原生 48 k,没有降采样
    assert np.array_equal(snap, stopped)


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


# ---------------- 电平 → 界面映射(dbfs_to_unit) ----------------
#
# 函数本身在 ``ui/panel.py`` 里(UI 层负责把电平画成条),
# 输入却是录音器给的 RMS —— 所以它的测试放在这里(电平计算这一档)。
#
# 它守的是 README 里的两条坑:
#   1. **电平必须推绝对值**:按 dBFS 对数映射,不做窗口内归一化 ——
#      归一化之后波形永远看着是满的,用户就看不出自己声音大不大了。
#   2. **电平条要加噪声门**:安静房间的底噪实测 -62 dBFS,
#      不加门映射出来还有 0.11,超过前端 0.30 的死区 ——
#      电平条一直显示 7px 高度,看着像一直在响。

def test_dbfs_to_unit_endpoints():
    """端点:静音 → 0,满刻度 → 1,超出范围夹住。"""
    assert dbfs_to_unit(0.0) == 0.0
    assert dbfs_to_unit(1e-4) == 0.0        # -80 dBFS,低于下限(-70)
    assert dbfs_to_unit(1.0) == 1.0         # 0 dBFS → 1
    assert dbfs_to_unit(4.0) == 1.0         # 超出上限也夹到 1


def test_dbfs_to_unit_is_linear_in_db():
    """对数域线性插值:-35 dBFS(上下限的正中间)→ 0.5。

    这正是"用人耳的感知"那条:线性映射会把正常说话的音量全挤在 0.05 以下。
    """
    rms = 10 ** (-35 / 20)
    assert dbfs_to_unit(rms) == pytest.approx(0.5, abs=1e-6)


def test_dbfs_to_unit_is_monotonic():
    """电平越大映射值越大 —— 绝对电平,不做窗口归一化。"""
    values = [dbfs_to_unit(10 ** (db / 20)) for db in (-60, -40, -20, -6)]
    assert values == sorted(values)
    assert all(0.0 <= v <= 1.0 for v in values)


def test_dbfs_to_unit_noise_gate_kills_background_noise():
    """噪声门:安静房间底噪(-62 dBFS 实测)必须判 0。

    不加门的话,底噪映射出来是 0.11 —— 会超过前端 0.30 的死区,
    电平条会一直显示 7px 高度,看着像一直在响(README 那条坑)。
    """
    noise_rms = 10 ** (-62 / 20)          # ≈ 7.9e-4
    floor = 0.002                         # config.silence_rms 的默认值

    # 不加门:底噪被映射成 0.11(这就是那条坑)
    assert dbfs_to_unit(noise_rms) == pytest.approx(0.114, abs=0.005)
    # 加了门:直接判 0
    assert dbfs_to_unit(noise_rms, floor_rms=floor) == 0.0


def test_dbfs_to_unit_noise_gate_boundary():
    """门限边界:正好等于门限判 0,刚超过门限就有值。"""
    floor = 0.002
    assert dbfs_to_unit(floor, floor_rms=floor) == 0.0
    assert dbfs_to_unit(floor * 1.01, floor_rms=floor) > 0.0


def test_dbfs_to_unit_tiny_signal_is_zero():
    """极小的信号(1e-6 以下)判 0 —— 免得 log10 出怪数。"""
    assert dbfs_to_unit(1e-7) == 0.0
