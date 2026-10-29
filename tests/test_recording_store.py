"""录音留存测试。

不碰真实麦克风和真实模型 —— 用假录音器 + 临时目录,
验证的是"存盘/清理/开关"这三件事:

- 存出来的 WAV 是不是原样(采样率、声道、样本)
- 旧的会不会被清掉(只留最近 20 个)
- 开关关掉时是不是真的不存
- 存盘失败会不会拖垮主流程
"""

import time
import wave
from pathlib import Path

import numpy as np
import pytest

from qwen3_voice.audio.recording_store import (
    KEEP_RECORDINGS,
    prune_recordings,
    save_recording,
    write_wav,
)
from qwen3_voice.config import Config
from qwen3_voice.core import controller as controller_module
from qwen3_voice.core.controller import VoiceController


# ---------------- 假实现 ----------------

class FakeEngine:
    """假 ASR 引擎(只关心文本能不能出来)。"""

    def __init__(self):
        self.calls = []

    def load(self, model_tag=None):
        return 0.1

    def switch_model(self, model_tag):
        return 0.1

    def transcribe(self, audio, sample_rate=16000, language=None):
        self.calls.append(len(audio))
        return "识别文本"


class FakeRecorder:
    """假录音器:stop() 返回固定的一段音频(原生采样率)。"""

    def __init__(self, audio=None, native_rate=16000):
        self.audio = audio if audio is not None else np.zeros(16000, dtype=np.int16)
        self.native_rate = native_rate
        self.started = False
        self.stopped = False

    def start(self):
        self.started = True
        return True

    def stop(self):
        self.stopped = True
        return self.audio

    def levels(self):
        return []

    def drain_chunks(self):
        return np.zeros((0,), dtype=np.int16)

    @property
    def elapsed(self):
        return 1.0


def tone(seconds: float, rate: int = 16000, amp: int = 5000) -> np.ndarray:
    return (np.ones(int(seconds * rate)) * amp).astype(np.int16)


def wait_for(predicate, timeout=5.0) -> bool:
    """等异步识别线程跑完(识别是在后台线程里做的)。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(0.02)
    return False


# ---------------- 存盘 ----------------

def test_save_recording_writes_int16_mono_wav(tmp_path: Path):
    """存出来的是 int16 单声道、原生采样率的 WAV。"""
    audio = tone(1.0, 16000)
    path = save_recording(audio, 16000, directory=tmp_path)

    assert path is not None and path.is_file()
    with wave.open(str(path), "rb") as wf:
        assert wf.getnchannels() == 1
        assert wf.getsampwidth() == 2          # 16 bit = int16
        assert wf.getframerate() == 16000
        assert wf.getnframes() == len(audio)


def test_saved_audio_is_bit_identical(tmp_path: Path):
    """留存必须保真 —— 音频要能直接拿去回放。"""
    audio = tone(0.5, 16000)
    path = save_recording(audio, 16000, directory=tmp_path)

    with wave.open(str(path), "rb") as wf:
        raw = wf.readframes(wf.getnframes())
    assert np.array_equal(np.frombuffer(raw, dtype="<i2"), audio)


def test_filename_contains_timestamp_and_duration(tmp_path: Path):
    """文件名带时间戳和时长:一眼知道是哪一轮、多长。"""
    path = save_recording(tone(31.3, 16000), 16000, directory=tmp_path)
    name = path.name

    assert name.endswith(".wav")
    stem = name[:-4]
    # 形如 20261029-164630_31s
    date_part, _, dur_part = stem.rpartition("_")
    assert len(date_part) == len("20261029-164630")
    assert dur_part.endswith("s")
    assert int(dur_part[:-1]) == 31


def test_native_sample_rate_is_preserved(tmp_path: Path):
    """原生采样率原样落盘(重采样是识别链路的事,留存不插手)。"""
    path = save_recording(tone(0.2, 48000), 48000, directory=tmp_path)
    with wave.open(str(path), "rb") as wf:
        assert wf.getframerate() == 48000


def test_empty_audio_is_not_saved(tmp_path: Path):
    """空音频没有留存的必要。"""
    assert save_recording(np.zeros((0,), dtype=np.int16), 16000,
                          directory=tmp_path) is None
    assert list(tmp_path.glob("*.wav")) == []


def test_same_second_does_not_overwrite(tmp_path: Path):
    """同一秒录两轮也不能互相覆盖。"""
    a = save_recording(tone(0.2, 16000), 16000, directory=tmp_path)
    b = save_recording(tone(0.2, 16000), 16000, directory=tmp_path)
    assert a != b
    assert a.is_file() and b.is_file()


# ---------------- 清理 ----------------

def test_prune_keeps_only_latest(tmp_path: Path):
    """只保留最近 keep 个,旧的删掉。"""
    for i in range(25):
        (tmp_path / f"20261029-1646{i:02d}_1s.wav").write_bytes(b"x")

    removed = prune_recordings(tmp_path, keep=20)

    left = sorted(p.name for p in tmp_path.glob("*.wav"))
    assert len(left) == 20
    assert len(removed) == 5
    # 留下的是最新的 20 个
    assert left[0] == "20261029-164605_1s.wav"
    assert left[-1] == "20261029-164624_1s.wav"


def test_prune_does_nothing_when_under_limit(tmp_path: Path):
    (tmp_path / "a.wav").write_bytes(b"x")
    assert prune_recordings(tmp_path, keep=20) == []
    assert (tmp_path / "a.wav").is_file()


def test_prune_on_missing_directory(tmp_path: Path):
    """目录还没建也不能报错。"""
    assert prune_recordings(tmp_path / "nope", keep=20) == []


def test_save_recording_prunes_old_ones(tmp_path: Path):
    """连着存很多轮,旧的会被自动清掉(只留 20 个)。"""
    for _ in range(KEEP_RECORDINGS + 5):
        save_recording(tone(0.05, 16000), 16000, directory=tmp_path)

    assert len(list(tmp_path.glob("*.wav"))) == KEEP_RECORDINGS


# ---------------- 控制器接线 ----------------

def _make_controller(tmp_path: Path, audio: np.ndarray,
                     native_rate: int = 16000, save: bool = True):
    cfg = Config(model_root=str(tmp_path), silence_rms=0.0001,
                 save_recordings=save)
    recorder = FakeRecorder(audio=audio, native_rate=native_rate)
    injected: list[str] = []
    ctrl = VoiceController(
        cfg,
        injector=lambda text, **kw: (injected.append(text), True)[1],
        recorder_factory=lambda config, device=None: recorder,
    )
    ctrl.engine = FakeEngine()
    ctrl._init_model = lambda: None
    ctrl._init_mic = lambda: None
    ctrl.injected = injected
    return ctrl


def test_controller_saves_recording_after_stop(tmp_path: Path, monkeypatch):
    """一轮录音结束后,原始音频落到 data/recordings/ 下。"""
    # portable 约定:数据只进项目目录内的 data/ —— 这里换成临时目录
    monkeypatch.setenv("QWEN3_VOICE_HOME", str(tmp_path))

    audio = tone(2.0, 16000)
    ctrl = _make_controller(tmp_path, audio)

    ctrl.start_recording()
    ctrl.stop_recording()

    files = list((tmp_path / "recordings").glob("*.wav"))
    assert len(files) == 1
    with wave.open(str(files[0]), "rb") as wf:
        assert wf.getnframes() == len(audio)
        assert wf.getframerate() == 16000


def test_controller_does_not_save_when_switch_off(tmp_path: Path, monkeypatch):
    """开关关掉就完全不存盘。"""
    monkeypatch.setenv("QWEN3_VOICE_HOME", str(tmp_path))

    ctrl = _make_controller(tmp_path, tone(2.0, 16000), save=False)

    ctrl.start_recording()
    ctrl.stop_recording()

    assert not (tmp_path / "recordings").exists() \
        or list((tmp_path / "recordings").glob("*.wav")) == []


def test_save_failure_does_not_break_main_flow(tmp_path: Path, monkeypatch):
    """存盘炸了也不能影响识别和注入 —— 留存只是调试基建。"""
    monkeypatch.setenv("QWEN3_VOICE_HOME", str(tmp_path))

    def boom(*args, **kwargs):
        raise OSError("磁盘满了")

    monkeypatch.setattr(controller_module, "save_recording", boom)

    ctrl = _make_controller(tmp_path, tone(2.0, 16000))
    ctrl.start_recording()
    ctrl.stop_recording()

    # 主流程照常走完:文本照样识别、照样注入
    assert wait_for(lambda: ctrl.injected)
    assert ctrl.injected == ["识别文本"]


def test_write_wav_converts_float_audio(tmp_path: Path):
    """写 WAV 时把浮点音频也归一到 int16(别写坏格式)。"""
    target = tmp_path / "x.wav"
    write_wav(target, np.ones(1000, dtype=np.float32) * 0.5, 16000)

    with wave.open(str(target), "rb") as wf:
        assert wf.getsampwidth() == 2
        assert wf.getnframes() == 1000
