"""音频设备枚举与自动选择。

为什么要实测而不是看名字:
    蓝牙耳机(EDIFIER W800BT Plus 等)在 WASAPI 共享模式下会被 Steam / 网易云
    等程序占住音频端点。设备声称支持某个采样率,实际打开后却只拿到零星数据
    —— 表现为「录音被截断」或「完全采不到声音」。

    所以策略是:逐个候选实测,选采集速度正常的那个;都不正常就退而求其次。
"""

from __future__ import annotations

import time
from dataclasses import dataclass

import numpy as np

from ..logging_setup import get_logger

log = get_logger("audio.devices")

# 明显是虚拟/回环设备,除非没有别的选择否则跳过
_VIRTUAL_HINTS = (
    "声音映射器", "sound mapper", "streaming", "virtual", "虚拟",
    "回环", "loopback", "cable output", "primary sound capture",
    "立体声混音", "stereo mix", "wave",
)

# 采集速度正常与否的判定阈值(实测 2 秒,得到 >=1.2 秒算正常)
_PROBE_DURATION = 2.0
_PROBE_OK_RATIO = 0.6

# 候选采样率,从高到低,优先选设备真正支持的
_RATE_CANDIDATES = (48000, 44100, 32000, 22050, 16000)


@dataclass
class InputDevice:
    index: int
    name: str
    max_channels: int
    default_rate: float
    is_virtual: bool = False

    @property
    def display(self) -> str:
        return f"[{self.index}] {self.name}"


def list_inputs() -> list[InputDevice]:
    """列出所有可用的输入设备。"""
    import sounddevice as sd

    try:
        devs = sd.query_devices()
    except Exception as e:
        log.error("枚举音频设备失败: %s", e)
        return []

    out: list[InputDevice] = []
    for i, d in enumerate(devs):
        if d.get("max_input_channels", 0) <= 0:
            continue
        name = d.get("name", f"device-{i}")
        out.append(
            InputDevice(
                index=i,
                name=name,
                max_channels=int(d.get("max_input_channels", 1)),
                default_rate=float(d.get("default_samplerate", 48000)),
                is_virtual=_is_virtual(name),
            )
        )
    return out


def _is_virtual(name: str) -> bool:
    low = name.lower()
    return any(h.lower() in low for h in _VIRTUAL_HINTS)


def supported_rate(index: int) -> int:
    """选一个该设备确实支持的采样率(优先高保真)。"""
    import sounddevice as sd

    for rate in _RATE_CANDIDATES:
        try:
            sd.check_input_settings(device=index, channels=1, dtype="int16", samplerate=rate)
            return rate
        except Exception:
            continue
    return 16000


def probe(index: int, duration: float = _PROBE_DURATION) -> tuple[int, float, float]:
    """实测设备:返回 (原生采样率, 实际采集时长, rms)。

    rms 是判断「有没有真的采到声音」的关键指标。
    """
    import sounddevice as sd

    rate = supported_rate(index)
    frames: list[np.ndarray] = []

    try:
        with sd.InputStream(
            device=index, channels=1, samplerate=rate, dtype="int16",
            blocksize=1024,
            callback=lambda ind, fr, t, s: frames.append(ind.copy()),
        ):
            time.sleep(duration)
    except Exception as e:
        log.debug("探测设备 %d 失败: %s", index, e)
        return rate, 0.0, 0.0

    if not frames:
        return rate, 0.0, 0.0

    arr = np.concatenate(frames, axis=0)
    if arr.ndim > 1:
        # mean 会把 int16 提升成 float64 —— 这里只用来算 RMS,
        # 不涉及刻度还原,但保持与 recorder.frames_to_int16 一致的做法。
        arr = arr.mean(axis=1)
    dur = len(arr) / rate
    rms = float(np.sqrt(np.mean((arr.astype(np.float32) / 32768.0) ** 2)))
    return rate, dur, rms


def choose_device(
    explicit_index: int | None = None,
    name_hints: list[str] | None = None,
) -> tuple[int | None, str]:
    """挑选一个可用的输入设备,返回 (设备号, 说明文字)。

    优先级:
        1. 显式指定
        2. 名称关键词匹配(且实测能采到数据)
        3. 所有非虚拟设备里实测最好的
    """
    devices = list_inputs()
    if not devices:
        return None, "没有找到任何输入设备"

    if explicit_index is not None:
        for d in devices:
            if d.index == explicit_index:
                return d.index, f"使用指定设备 {d.display}"
        log.warning("指定的设备 [%d] 不存在,改为自动选择", explicit_index)

    # 名称关键词
    for hint in name_hints or []:
        h = hint.lower()
        for d in devices:
            if h in d.name.lower():
                rate, dur, rms = probe(d.index)
                if dur >= _PROBE_DURATION * _PROBE_OK_RATIO:
                    return d.index, f"按名称匹配 {d.display}(原生 {rate} Hz)"
                log.info("名称匹配 %s 但采集不到数据(可能被其它程序占用),继续找", d.display)

    # 全量实测
    good: list[tuple[float, InputDevice, int, float]] = []
    fallback: list[tuple[float, InputDevice, int, float]] = []

    for d in devices:
        if d.is_virtual:
            continue
        rate, dur, rms = probe(d.index)
        if dur >= _PROBE_DURATION * _PROBE_OK_RATIO:
            good.append((rms, d, rate, dur))
        elif dur > 0:
            fallback.append((rms, d, rate, dur))

    if good:
        good.sort(key=lambda x: -x[0])  # 信号强的优先
        rms, d, rate, dur = good[0]
        return d.index, f"自动选择 {d.display}(原生 {rate} Hz, rms={rms:.4f})"

    if fallback:
        fallback.sort(key=lambda x: -x[0])
        rms, d, rate, dur = fallback[0]
        return d.index, (
            f"仅 {d.display} 有信号但采集偏慢({dur:.1f}s/{_PROBE_DURATION:.0f}s),"
            f"可能有其它程序占用音频设备"
        )

    return None, "找不到可用麦克风"
