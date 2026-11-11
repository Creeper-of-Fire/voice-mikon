"""电平映射(dBFS → 0..1)。

从 ``ui/panel.py`` 原样搬过来的 —— 那层本片退役,电平的**量纲规则**
跟着数据源一起落到 core:录制端算好绝对电平,消费端(presenter /
胶囊前端)只负责画,不再各自折算一遍。

规则不许改(见 ``REFACTOR_SPEC.md`` 血泪条款 3):

* 推给前端的必须是 **dbfs_to_unit** 的输出(对数映射 + 底噪门),
  不是线性 RMS;
* **绝不能**按窗口内最大值归一化 —— 归一化之后电平条永远是满的,
  用户就没法从条的高低看出"我说话声音大不大",那条条就只剩装饰作用。

关于"底噪门":门限不是常数,而是**底噪估计 × 系数**(``LEVEL_GATE_FACTOR``)。
固定门限是按"某次录音"标定的,环境一变就被整个穿过(2026-11 实测:
换环境后底噪抬高 9 dB,固定门被穿过,安静时电平条 65% 满格)。
门随底噪走才是对的。
"""

from __future__ import annotations

import math

# 电平映射区间:dBFS -> 0..1
#
# 用对数域而不是直接用线性 RMS:MiniMax Code 那边靠浏览器的 AGC
# 把 RMS 稳在 0.15 附近,我们没有 AGC,线性映射会因麦克风增益不同
# 而时高时低。对数映射只关心"比底噪高多少",对增益不敏感。
#
# 下限取 -70:实测这个房间的底噪就有 -62 dBFS,下限卡在 -60 会把底噪
# 和"完全无声"混成同一个 0,电平条看着像死了。实际人声约 -35 ~ -20 dBFS,
# 映射到 0.5 ~ 0.71,和底噪拉得开。
DB_FLOOR = -70.0
DB_CEIL = 0.0

# 显示门的动态门限 = max(LEVEL_GATE_MIN, 底噪估计 × LEVEL_GATE_FACTOR)。
#
# 系数 1.5:比底噪高 50% 以内都算底噪(门限附近的过渡帧不闪)。
# 之前的门限是固定常数(config.silence_rms=0.002),按 10-29 那次录音
# 标定;换环境后底噪抬高 9 dB,门被整个穿过,安静时电平条 65% 满格
# —— 固定值在环境面前活不过一次换麦/换房间,现在门随底噪走。
#
# 绝对下限 1e-5 只是兜底:防止"底噪估计接近 0"(近哑设备)时门
# 坍缩成 0,让任何数值都算"有声"。真正的静音判零由 dbfs_to_unit
# 自带的 rms<=1e-6 承担,两层各管各的。
LEVEL_GATE_FACTOR = 1.5
LEVEL_GATE_MIN = 1e-5


def dbfs_to_unit(rms: float, floor_rms: float = 0.0) -> float:
    """把线性 RMS 映射到 0..1(对数域)。

    DB_FLOOR dBFS 映射到 0,0 dBFS 映射到 1,中间线性插值。
    对数域而不是线性域,是因为人耳对 loudness 的感知接近对数,
    线性映射会让正常说话的音量全部挤在 0.05 以下。

    `floor_rms` 是**噪声门**:低于它的直接判 0。门限不是常数 ——
    由调用方用噪声底估计动态算(见 ``level_gate``):固定门限是
    按"某次录音"标定的,环境一变就失效(2026-11 实测:换环境后
    底噪抬高 9 dB,固定门被整个穿过,安静时电平条 65% 满格)。
    """
    if rms <= floor_rms or rms <= 1e-6:
        return 0.0
    db = 20.0 * math.log10(rms)
    if db <= DB_FLOOR:
        return 0.0
    if db >= DB_CEIL:
        return 1.0
    return (db - DB_FLOOR) / (DB_CEIL - DB_FLOOR)


def level_gate(floor_rms: float | None) -> float:
    """按底噪估计算出显示门限。

    ``floor_rms`` 是 ``Recorder.noise_floor()`` 的读数(线性 RMS);
    **样本不足时它返回 None**(底噪估计还没收敛),这时退回绝对下限
    ``LEVEL_GATE_MIN`` —— 宁可让头几帧底噪照常显示,也不能把轻声
    开头错杀成 0。

    返回的门限直接喂给 ``dbfs_to_unit`` 当 ``floor_rms`` 用。
    """
    if floor_rms is None:
        return LEVEL_GATE_MIN
    return max(LEVEL_GATE_MIN, floor_rms * LEVEL_GATE_FACTOR)
