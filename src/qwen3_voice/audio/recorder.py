"""录音采集。

两个关键设计(都来自实际踩坑):

1. **用设备原生采样率打开,自己重采样到 16 kHz**
   蓝牙耳机声称支持 16 kHz,但 WASAPI 共享模式下会悄悄降速
   (实测 2 秒只采到 0.38 秒),按 16 kHz 直接开会得到残缺数据。

2. **设备访问互斥**
   探测流和录音流不能同时占用同一个音频端点,否则其中一方只拿到部分数据。
   用一把全局锁串行化,并让后台探测在用户录音时主动让路。
"""

from __future__ import annotations

import threading
import time
from collections import deque

import numpy as np

from ..logging_setup import get_logger
from .devices import supported_rate

log = get_logger("audio.recorder")

# 设备访问锁:探测与录音互斥
_device_lock = threading.Lock()
# 录音进行中的标志,后台探测看到就主动让路
_recording_state = threading.Event()


def resample(audio: np.ndarray, src: int, dst: int) -> np.ndarray:
    """线性插值重采样。语音场景足够,且不引入 scipy 依赖。"""
    if src == dst or len(audio) == 0:
        return audio
    n_out = int(round(len(audio) * dst / src))
    if n_out <= 0:
        return audio
    x_old = np.arange(len(audio), dtype=np.float64)
    x_new = np.linspace(0, len(audio) - 1, n_out, dtype=np.float64)
    return np.interp(x_new, x_old, audio.astype(np.float64)).astype(np.int16)


class Recorder:
    """后台录音采集。"""

    def __init__(self, device: int | None = None, target_rate: int = 16000,
                 level_window: int = 48):
        self.device = device
        self.target_rate = target_rate
        self.native_rate = target_rate
        self._frames: list[np.ndarray] = []
        self._stream = None
        self._lock = threading.Lock()
        self._device_lock_held = False
        self.start_time = 0.0
        # 最近若干帧的 rms,给界面画电平用
        self._levels: deque[float] = deque(maxlen=level_window)

    # ---------- 状态 ----------

    @property
    def recording(self) -> bool:
        return self._stream is not None

    @property
    def elapsed(self) -> float:
        return time.time() - self.start_time if self.start_time else 0.0

    def levels(self) -> list[float]:
        """最近的音量电平序列(用于实时波形)。"""
        with self._lock:
            return list(self._levels)

    # ---------- 控制 ----------

    def start(self) -> bool:
        import sounddevice as sd

        self._frames = []
        self.native_rate = supported_rate(self.device) if self.device is not None else supported_rate(None)
        try:
            self._device_lock_held = _device_lock.acquire(timeout=10)
            self._stream = sd.InputStream(
                device=self.device,
                channels=1,
                samplerate=self.native_rate,
                dtype="int16",
                blocksize=1024,
                callback=self._callback,
            )
            self._stream.start()
        except Exception as e:
            log.error("录音启动失败: %s", e)
            self._stream = None
            self._release_device_lock()
            return False

        self.start_time = time.time()
        _recording_state.set()
        log.info("录音中(设备 %s,原生采样率 %d Hz)", self.device, self.native_rate)
        return True

    def _callback(self, indata, frames, time_info, status) -> None:
        if status:
            log.debug("录音状态: %s", status)
        with self._lock:
            self._frames.append(indata.copy())
            arr = np.asarray(indata, dtype=np.float32).reshape(-1) / 32768.0
            self._levels.append(float(np.sqrt(np.mean(arr**2))))

    def stop(self) -> np.ndarray:
        """停止录音并返回 16 kHz 单声道 int16 音频。"""
        if self._stream is not None:
            try:
                self._stream.stop()
                self._stream.close()
            except Exception:
                pass
            self._stream = None
        self._release_device_lock()
        _recording_state.clear()

        with self._lock:
            frames = self._frames
            self._frames = []

        if not frames:
            return np.zeros((0,), dtype=np.int16)

        arr = np.concatenate(frames, axis=0)
        arr = np.asarray(arr)
        if arr.ndim > 1:
            arr = arr.mean(axis=1)
        if arr.dtype != np.int16:
            if np.issubdtype(arr.dtype, np.floating):
                arr = np.clip(arr * 32768.0, -32768, 32767)
            arr = arr.astype(np.int16)
        arr = arr.reshape(-1)

        return resample(arr, self.native_rate, self.target_rate)

    def _release_device_lock(self) -> None:
        if self._device_lock_held:
            try:
                _device_lock.release()
            except Exception:
                pass
            self._device_lock_held = False


def is_recording() -> bool:
    """当前是否有录音在进行(供后台探测让路判断)。"""
    return _recording_state.is_set()


def rms_of(audio: np.ndarray) -> float:
    """音频的均方根电平,用于静音检测。"""
    if len(audio) == 0:
        return 0.0
    return float(np.sqrt(np.mean((audio.astype(np.float32) / 32768.0) ** 2)))
