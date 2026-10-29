"""录音留存:把每轮录音的原始音频落成 WAV。

为什么要有这个:真机上调"预览零出字"这类问题时,光看日志不够 ——
得能拿**同一段真实录音**反复回放,看分句引擎到底断没断句、
识别出了什么。日志会骗人(没喂进去 / 全判有声 / 断句识别为空
三种情况在日志上都可能长得一样),音频不会。

约束:

- 只用标准库 ``wave``(不新增依赖)
- 落盘在 ``data/recordings/``(portable 约定:数据只进 data/)
- 存盘失败**绝不影响主流程** —— 抛出去由调用方兜住
- int16 单声道、原生采样率原样落盘(重采样是识别链路的事,
  留存要保真)

只留最近 ``KEEP_RECORDINGS`` 个:调试期够用,又不会让 data/ 无限膨胀。
"""

from __future__ import annotations

import time
import wave
from pathlib import Path

import numpy as np

from ..logging_setup import get_logger
from ..paths import recordings_dir

log = get_logger("audio.store")

# 最多保留几个录音文件(超出就把最旧的删掉)
KEEP_RECORDINGS = 20


def _timestamp() -> str:
    """文件名用的时间戳(本地时间,精确到秒)。"""
    return time.strftime("%Y%m%d-%H%M%S")


def _unique_path(directory: Path, stem: str) -> Path:
    """同一秒里录了两轮也不会互相覆盖。"""
    path = directory / f"{stem}.wav"
    idx = 1
    while path.exists():
        path = directory / f"{stem}-{idx}.wav"
        idx += 1
    return path


def write_wav(path: Path | str, audio: np.ndarray, sample_rate: int) -> None:
    """把音频写成 WAV(int16 单声道,标准库 wave)。

    ``"<i2"`` 显式指定小端 —— WAV 格式要求小端,别依赖运行平台的
    字节序(留存的录音将来可能在别的机器上读)。
    """
    arr = np.asarray(audio)
    if arr.ndim > 1:
        arr = arr.mean(axis=-1)
    if arr.dtype != np.int16:
        arr = np.clip(arr, -32768, 32767).astype(np.int16)
    data = arr.astype("<i2").tobytes()

    with wave.open(str(path), "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(int(sample_rate))
        wf.writeframes(data)


def prune_recordings(directory: Path | str | None = None,
                     keep: int = KEEP_RECORDINGS) -> list[Path]:
    """只保留最近 ``keep`` 个录音,其余删掉,返回被删的路径列表。

    这里直接用 ``Path.unlink()`` 删除是安全的:删的是**程序自己刚写的**
    运行时数据(data/ 里的调试留存),不是用户的文件。
    不走回收站 —— 免得回收站里堆几百个 WAV,还得一个个清。

    排序用文件名:文件名以时间戳开头,字典序就是时间序,
    比 mtime 可靠(改文件时间不会把顺序搞乱)。
    """
    target = Path(directory) if directory is not None else recordings_dir()
    if not target.is_dir():
        return []

    files = sorted(target.glob("*.wav"), key=lambda p: p.name)
    removed: list[Path] = []
    # keep <= 0 表示全删
    for old in files[:max(0, len(files) - max(0, keep))]:
        try:
            old.unlink()
            removed.append(old)
        except OSError as e:
            log.debug("删除旧录音 %s 失败: %s", old, e)
    return removed


def save_recording(audio: np.ndarray, sample_rate: int,
                   directory: Path | str | None = None,
                   keep: int = KEEP_RECORDINGS) -> Path | None:
    """存一轮录音,返回落盘路径(音频为空时返回 None)。

    文件名带时间戳和时长(例如 ``20261029-164630_31s.wav``)——
    拿到文件名就知道是哪一轮、多长,不用逐个打开听。

    **失败会抛异常** —— 调用方(控制器)负责兜住,保证不影响主流程。
    """
    if audio is None or len(audio) == 0:
        return None

    target_dir = Path(directory) if directory is not None else recordings_dir()
    target_dir.mkdir(parents=True, exist_ok=True)

    duration = len(audio) / max(1, int(sample_rate))
    stem = f"{_timestamp()}_{int(round(duration))}s"
    path = _unique_path(target_dir, stem)
    write_wav(path, audio, sample_rate)
    log.debug("录音已留存: %s", path)

    try:
        prune_recordings(target_dir, keep=keep)
    except Exception as e:
        # 清理失败不影响这一轮已经落盘
        log.debug("清理旧录音失败: %s", e)
    return path
