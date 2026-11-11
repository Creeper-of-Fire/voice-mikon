"""噪声底跟踪:用滑动窗口的低分位数估计环境底噪。

为什么需要它:环境底噪是"测出来的",不是"定出来的"。

旧做法是给三处(电平显示门 / VAD 切句门 / 前端死区)各配一个
按**某次录音**标定的绝对 RMS 常量。2026-10-29 按当时的 USB 麦
标定(底噪 -63~-60 dBFS),换环境后底噪抬到 -52.6 dBFS,三处
阈值同一天全部失效:电平条安静时 65% 满格,切句静音判定永不触发
(底噪 0.0017~0.0023 全部高过门限 0.0015)。固定值在环境面前
活不过一次换麦/换房间。

怎么做:滚动窗口(默认 3 秒)收最近一串 RMS,取**低分位数 p10**。

  - 为什么是 p10 而不是 min:min 会被偶发脉冲压低(一次 USB 咔哒
    的静音帧把估计拖得过低,门就漏底噪);p10 意味着"窗口里最安静
    的那 10% 帧",对孤立脉冲免疫,底噪一变 3 秒内就跟上。
  - 为什么窗口 3 秒:人说话总有换气停顿,3 秒里几乎必然混着纯底噪
    帧;再长对"刚换环境"的响应就慢了。
  - 样本不足(min_samples 之前)返回 None:调用方自行兜底 —— 语义是
    "还不知道环境有多吵",而不是"环境很安静"。冷启动那不到一秒
    宁可把底噪包进句子(识别不在乎句首尾一点底噪),也不能把轻声
    开头判成静音。

线程模型:``update`` 由采集回调线程调,``floor`` 由消费线程调 ——
``deque.append`` 在 CPython 里是原子的,读之前浅拷贝一份再算,
跟 Recorder._levels 同一套模式,不加锁。

这个类是**纯估计**,不含任何门系数:开门/关门用几倍底噪,是
消费者(显示/切句)各自的策略,不在这里定。
"""

from __future__ import annotations

from collections import deque

import numpy as np


class NoiseFloorTracker:
    """滑动窗口 RMS 低分位数 → 环境底噪估计。

    ``update(rms)`` 逐帧喂(帧长不限,窗口按**帧数**算 —— 调用方
    自己换算:20ms 帧给 150,64ms 帧给 48,都是约 3 秒)。

    ``floor()`` 窗口攒够 ``min_samples`` 帧前返回 None;之后返回
    p10(线性 RMS,0..1 量纲,与喂进去的一致)。
    """

    def __init__(self, window: int = 150, min_samples: int = 50):
        self._samples: deque[float] = deque(maxlen=max(2, int(window)))
        self._min_samples = max(1, min(int(min_samples), self._samples.maxlen))

    def update(self, rms: float) -> None:
        """喂一帧 RMS。只收有限值 —— NaN/inf 会把 percentile 毒死。"""
        r = float(rms)
        if np.isfinite(r):
            self._samples.append(r)

    def floor(self) -> float | None:
        """当前底噪估计;样本不足返回 None(不是 0 —— 0 是"很安静",
        None 是"还不知道",两者对调用方的意义完全不同)。"""
        if len(self._samples) < self._min_samples:
            return None
        arr = np.fromiter(self._samples, dtype=np.float32,
                          count=len(self._samples))
        return float(np.percentile(arr, 10))
