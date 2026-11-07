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
from .noise import NoiseFloorTracker

log = get_logger("audio.recorder")

# 采集块大小(每次回调的样本数)。
# 回放工具要按**同样的粒度**切音频喂引擎,否则复现不出真实的喂入节奏
# —— 粒度不同,分句引擎内部按帧切分的边界也跟着变。
RECORDER_BLOCK_SIZE = 1024

# 设备访问锁:探测与录音互斥
_device_lock = threading.Lock()
# 录音进行中的标志,后台探测看到就主动让路
_recording_state = threading.Event()


def frames_to_int16(frames: list[np.ndarray]) -> np.ndarray:
    """把采集到的帧拼成 int16 单声道数组。

    ``stop()`` 和 ``snapshot()`` 共用这一段转换 —— 两者必须给出
    **完全一致**的内容,否则"预览 = 停止后的最终音频"这个前提就不成立。
    """
    if not frames:
        return np.zeros((0,), dtype=np.int16)

    arr = np.concatenate(frames, axis=0)
    arr = np.asarray(arr)
    # 多声道混成单声道。mean 会把 int16 提升成 float64,
    # 所以这里**先记住原始整数刻度**,混完再还原 —— 不能对
    # 已经变成浮点的均值再乘 32768(那是给 [-1,1] 浮点音频用的缩放)。
    if arr.ndim > 1:
        if arr.dtype == np.int16:
            arr = arr.mean(axis=1).astype(np.int16)
        else:
            arr = arr.mean(axis=1)
    if arr.dtype != np.int16:
        if np.issubdtype(arr.dtype, np.floating):
            arr = np.clip(arr * 32768.0, -32768, 32767)
        arr = arr.astype(np.int16)
    return arr.reshape(-1)


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
        # 已经取走到第几帧了(drain_chunks 的游标)
        self._chunk_cursor = 0
        # 环境底噪估计(滑动窗口 p10):显示门和 VAD 切句门的共同数据源。
        # 块长 1024/原生采样率 ≈ 64ms(16k),48 帧 ≈ 3 秒窗口,
        # 攒够 16 帧(约 1 秒)才开始给估计 —— 之前返回 None,调用方兜底。
        #
        # ⚠️ **跨轮保留**:故意不在 start()/stop() 里重置。底噪是房间
        # 和设备的属性,不会一轮一变;留着上一轮的窗口,第二轮录音
        # 一开始就有先验,不用再等一秒冷启动。换设备才需要重新收敛,
        # 而 Recorder 本身就是换设备就重建的。
        self._noise = NoiseFloorTracker(
            window=level_window, min_samples=min(16, level_window))

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

    def noise_floor(self) -> float | None:
        """环境底噪估计(线性 RMS);样本不足返回 None。

        显示门(电平条)和切句门(VAD)都从这里取 —— 底噪是房间和
        设备的属性,只能有一个真相。冷启动(窗口没攒够)返回 None,
        调用方自行兜底,别把 None 当 0 用。
        """
        return self._noise.floor()

    # ---------- 控制 ----------

    def start(self) -> bool:
        import sounddevice as sd

        self._frames = []
        self._chunk_cursor = 0
        self.native_rate = supported_rate(self.device) if self.device is not None else supported_rate(None)
        try:
            self._device_lock_held = _device_lock.acquire(timeout=10)
            self._stream = sd.InputStream(
                device=self.device,
                channels=1,
                samplerate=self.native_rate,
                dtype="int16",
                blocksize=RECORDER_BLOCK_SIZE,
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
            rms = float(np.sqrt(np.mean(arr**2)))
            self._levels.append(rms)
            # 同一个 RMS 顺手喂给底噪估计器 —— 它和电平条看的是
            # 同一串帧,不该有第二份数据源
            self._noise.update(rms)

    def stop(self) -> np.ndarray:
        """停止录音并返回**原生采样率**的单声道 int16 音频。

        这里**不做**重采样 —— 由 ``qwen_asr`` 内部完成(见下方说明)。

        为什么不在这里降采样:
        设备原生采样率通常是 48 kHz,降到 16 kHz 要做低通+抽取。
        自己用线性插值实现,没有抗混叠滤波,8~24 kHz 的成分会折叠回
        低频,叠在人声基频上(实测失真能量占比高达 98%,其中 88% 落在
        0~4 kHz 语音主频带)。

        而 ``qwen_asr`` 内部本来就用 librosa 做高质量重采样
        (与官方评测路径完全一致),把原生采样率直接给它即可 ——
        既少写一段代码,又和官方用同一条路。
        """
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

        return frames_to_int16(frames)

    def snapshot(self) -> np.ndarray:
        """当前已录到的音频(原生采样率,int16 单声道)。

        给实时预览用:**不消费、不打断录音**,录音继续,``stop()`` 仍然
        能拿到完整音频。

        注意与 ``stop()`` 的差别:**两者都是设备原生采样率**,
        内容完全一致 —— 重采样统一交给 ``qwen_asr`` 内部做
        (见 ``stop()`` 的说明)。调用方要自己重采样的话,
        拿到的采样率就是 ``native_rate``。

        必须**在锁内拷贝帧列表**:采集回调线程随时在往 ``_frames`` 里塞
        数据,直接把列表给出去会被并发修改。
        """
        with self._lock:
            frames = list(self._frames)
        return frames_to_int16(frames)

    def drain_chunks(self) -> np.ndarray:
        """取走"上次取走之后新录到的音频"(原生采样率,int16 单声道)。

        给流式引擎用:预览位只需要**增量**,不必每次都把全部音频重新拼一遍
        —— 那正是旧预览"整段重识别"越说越慢的根源之一。

        与 ``snapshot()`` 的分工:
            ``snapshot()``     给**全量**且不消费(整段引擎 / 调试用)
            ``drain_chunks()`` 给**增量**且推进游标(流式引擎用)

        两者都不打断录音 —— ``stop()`` 仍然拿到完整音频。
        录音停止后(帧列表已清空)再调用只会拿到空数组。

        必须**在锁内取游标**:采集回调线程随时在往 ``_frames`` 里塞数据,
        游标和帧列表要一起取,否则会漏帧或者重复。
        """
        with self._lock:
            frames = self._frames
            start = self._chunk_cursor
            if start >= len(frames):
                return np.zeros((0,), dtype=np.int16)
            self._chunk_cursor = len(frames)
            new_frames = frames[start:]
        return frames_to_int16(new_frames)

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
