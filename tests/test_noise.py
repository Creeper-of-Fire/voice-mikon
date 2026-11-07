"""NoiseFloorTracker 的行为测试。

底噪估计是显示门和 VAD 切句门的共同数据源,算错会同时毒两处 ——
单独守住:低分位数、冷启动 None、脉冲免疫、窗口滚动。
"""

import numpy as np
import pytest

from qwen3_voice.audio.noise import NoiseFloorTracker


def test_cold_start_returns_none():
    """样本没攒够 min_samples:返回 None("还不知道"),不是 0("很安静")。"""
    tr = NoiseFloorTracker(window=150, min_samples=50)
    for _ in range(49):
        tr.update(0.002)
    assert tr.floor() is None
    tr.update(0.002)
    assert tr.floor() is not None


def test_p10_lands_on_quiet_frames():
    """说话帧混着停顿帧:p10 落在停顿帧的水平(底噪),不是平均水平。"""
    tr = NoiseFloorTracker(window=100, min_samples=50)
    rng = np.random.default_rng(7)
    for _ in range(30):
        tr.update(0.002 + rng.uniform(-1e-4, 1e-4))   # 底噪
    for _ in range(70):
        tr.update(0.02 + rng.uniform(-1e-3, 1e-3))    # 说话
    # p10 应该被底噪帧占据(30/100 ≈ p30 以下全是底噪)
    assert tr.floor() == pytest.approx(0.002, abs=5e-4)


def test_track_follows_environment_change():
    """底噪抬高后,旧样本滚出窗口,估计跟上去 —— 这就是"灵活"的来源。"""
    tr = NoiseFloorTracker(window=50, min_samples=30)
    for _ in range(50):
        tr.update(0.001)
    assert tr.floor() == pytest.approx(0.001, abs=1e-5)
    for _ in range(50):
        tr.update(0.003)
    assert tr.floor() == pytest.approx(0.003, abs=1e-4)


def test_single_pulse_does_not_drag_estimate():
    """孤立脉冲(一声咔哒)不会把估计拖低 —— p10 对 min 的优势。"""
    tr = NoiseFloorTracker(window=100, min_samples=50)
    rng = np.random.default_rng(11)
    for _ in range(99):
        tr.update(0.002 + rng.uniform(-1e-4, 1e-4))
    tr.update(0.0)                                    # 一帧纯静音脉冲
    # min 会变 0;p10 仍稳在底噪附近
    assert tr.floor() == pytest.approx(0.002, abs=5e-4)


def test_nonfinite_samples_are_ignored():
    """NaN/inf 不进窗口 —— 一个脏值不该毒死 percentile。"""
    tr = NoiseFloorTracker(window=10, min_samples=5)
    tr.update(float("nan"))
    tr.update(float("inf"))
    for _ in range(5):
        tr.update(0.002)
    assert tr.floor() == pytest.approx(0.002, abs=1e-5)
