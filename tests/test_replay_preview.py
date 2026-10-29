"""回放工具(``scripts/replay_preview.py``)的冒烟测试。

不加载模型、不占麦克风 —— 合成一小段 WAV,喂**假**识别引擎,
验证脚本的主体逻辑:

- WAV 读回来是不是原样(采样率、样本、多声道混单声道)
- 帧级 VAD 统计对不对(有声帧/静音帧)
- 断句有没有发生、断句原因对不对
- partial 累计文本是不是逐句增长
- 报告文本里关键信息在不在

真机上"预览零出字"的具体原因,要用**真实录音**跑这个脚本才知道
(测试里只能验证工具本身是对的)。
"""

import importlib.util
import sys
import wave
from pathlib import Path

import numpy as np
import pytest

_SCRIPTS_DIR = Path(__file__).resolve().parents[1] / "scripts"


def _load_module():
    """按文件路径加载 scripts/replay_preview.py(脚本目录不是包)。"""
    path = _SCRIPTS_DIR / "replay_preview.py"
    spec = importlib.util.spec_from_file_location("replay_preview", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules["replay_preview"] = module
    spec.loader.exec_module(module)
    return module


replay_preview = _load_module()


# ---------------- 假实现 ----------------

class FakeASR:
    """假识别引擎:每次调用返回固定文本,并记录收到的音频长度。"""

    def __init__(self, text="测试文本", fail=False):
        self.text = text
        self.fail = fail
        self.calls: list[int] = []

    def transcribe(self, audio, sample_rate=16000, language=None):
        self.calls.append(len(audio))
        if self.fail:
            raise RuntimeError("模型坏了")
        return self.text


def tone(seconds: float, rate: int = 16000, amp: int = 5000) -> np.ndarray:
    return (np.ones(int(seconds * rate)) * amp).astype(np.int16)


def quiet(seconds: float, rate: int = 16000) -> np.ndarray:
    return np.zeros(int(seconds * rate), dtype=np.int16)


def speech_pattern(rate: int = 16000, repeats: int = 3) -> np.ndarray:
    """有声 0.4s + 静音 0.4s,重复几次(静音是断句的依据)。"""
    parts = []
    for _ in range(repeats):
        parts.append(tone(0.4, rate))
        parts.append(quiet(0.4, rate))
    return np.concatenate(parts)


def write_test_wav(path: Path, audio: np.ndarray, rate: int = 16000,
                   channels: int = 1) -> Path:
    with wave.open(str(path), "wb") as wf:
        wf.setnchannels(channels)
        wf.setsampwidth(2)
        wf.setframerate(rate)
        if channels == 1:
            wf.writeframes(audio.astype("<i2").tobytes())
        else:
            stereo = np.repeat(audio.reshape(-1, 1), channels, axis=1)
            wf.writeframes(stereo.astype("<i2").tobytes())
    return path


# ---------------- WAV 读取 ----------------

def test_read_wav_roundtrip(tmp_path: Path):
    """读回来的音频和写进去的一模一样。"""
    audio = speech_pattern()
    path = write_test_wav(tmp_path / "a.wav", audio)

    loaded, rate = replay_preview.read_wav(path)

    assert rate == 16000
    assert np.array_equal(loaded, audio)
    assert loaded.dtype == np.int16


def test_read_wav_mixes_stereo_to_mono(tmp_path: Path):
    """多声道混成单声道(不能只取一个声道)。"""
    audio = tone(0.2, 16000)
    path = write_test_wav(tmp_path / "s.wav", audio, channels=2)

    loaded, rate = replay_preview.read_wav(path)

    assert rate == 16000
    assert len(loaded) == len(audio)
    assert np.array_equal(loaded, audio)


def test_read_wav_keeps_native_sample_rate(tmp_path: Path):
    """原生采样率原样读出来(不重采样)。"""
    audio = tone(0.2, 48000)
    path = write_test_wav(tmp_path / "a.wav", audio, rate=48000)

    _, rate = replay_preview.read_wav(path)
    assert rate == 48000


# ---------------- 回放主体 ----------------

def test_replay_counts_frames_and_breaks():
    """有声/静音帧统计和断句都对。"""
    audio = speech_pattern()
    report = replay_preview.replay(audio, 16000, FakeASR())

    assert report.frames, "应该看到帧"
    assert report.voiced_frames > 0
    assert report.silent_frames > 0
    assert report.voiced_frames + report.silent_frames == len(report.frames)
    # 有声 + 静音交替,应该断出句子来
    assert report.speech_segments
    assert report.segments[0].reason == "静音断句"


def test_replay_feeds_recorder_granularity():
    """按录音器的块大小喂(喂入粒度必须和真机一致)。"""
    audio = speech_pattern()
    report = replay_preview.replay(audio, 16000, FakeASR())

    assert report.block_size == 1024
    assert report.blocks == (len(audio) + 1023) // 1024


def test_replay_emits_growing_partial_text():
    """partial 给的是"已识别句子的累计拼接",逐句增长。"""
    audio = speech_pattern()
    report = replay_preview.replay(audio, 16000, FakeASR())

    assert report.partials
    texts = [t for _, t in report.partials]
    assert len(texts[0]) <= len(texts[-1])
    assert texts[-1] == report.final_text


def test_replay_final_text_is_all_sentences():
    """最终文本 = 各句拼接。"""
    audio = speech_pattern()
    report = replay_preview.replay(audio, 16000, FakeASR())

    assert report.sentences
    assert report.final_text == "".join(report.sentences)


def test_replay_records_recognition_failure():
    """识别抛异常时,诊断里要能看到错误(而不是笼统的"识别为空")。"""
    audio = speech_pattern()
    report = replay_preview.replay(audio, 16000, FakeASR(fail=True))

    assert report.speech_segments
    assert all(s.error for s in report.speech_segments)
    assert not report.partials

    hints = replay_preview.diagnose(report)
    assert any("识别全部失败" in h for h in hints)


def test_replay_detects_all_silence():
    """全是静音 → 直接指出"全部判成静音"。"""
    audio = quiet(2.0)
    report = replay_preview.replay(audio, 16000, FakeASR())

    hints = replay_preview.diagnose(report)
    assert any("全部判成静音" in h for h in hints)


def test_replay_detects_no_frames():
    """没有音频 → 指出"帧没进来"。"""
    report = replay_preview.replay(np.zeros((0,), dtype=np.int16), 16000,
                                   FakeASR())

    hints = replay_preview.diagnose(report)
    assert any("没有任何帧进来" in h for h in hints)


def test_healthy_replay_diagnosis_is_positive():
    """一切正常时给出肯定结论(别老报警)。"""
    audio = speech_pattern()
    report = replay_preview.replay(audio, 16000, FakeASR())

    hints = replay_preview.diagnose(report)
    assert any("一切正常" in h for h in hints)


def test_replay_reads_from_file_end_to_end(tmp_path: Path):
    """从 WAV 文件走一遍完整流程(和命令行用法一致)。"""
    audio = speech_pattern()
    path = write_test_wav(tmp_path / "a.wav", audio)

    loaded, rate = replay_preview.read_wav(path)
    report = replay_preview.replay(loaded, rate, FakeASR())

    assert report.speech_segments
    assert report.final_text


# ---------------- 报告输出 ----------------

def test_report_contains_key_sections():
    """报告里要有诊断需要的全部信息。"""
    audio = speech_pattern()
    report = replay_preview.replay(audio, 16000, FakeASR())
    report.path = "a.wav"

    text = replay_preview.format_report(report)

    assert "帧级 VAD" in text
    assert "断句" in text
    assert "partial" in text
    assert "汇总" in text
    assert "诊断" in text
    assert "测试文本" in text          # 识别文本要出现
    assert "a.wav" in text


def test_verbose_prints_every_frame(capsys):
    """--verbose 逐帧打印。"""
    audio = speech_pattern()
    replay_preview.replay(audio, 16000, FakeASR(), verbose=True)

    out = capsys.readouterr().out
    assert "rms=" in out
    assert "有声" in out
    assert "静音" in out


def test_build_asr_engine_uses_config():
    """识别引擎按 config.json 的参数造(和主程序一致)。"""
    from qwen3_voice.config import Config

    cfg = Config(model="1.7B", language="zh", hotwords=["甲", "乙"])
    engine = replay_preview.build_asr_engine(cfg)

    assert engine.model_tag == "1.7B"
    assert engine.language == "zh"
    assert engine.hotwords == ["甲", "乙"]
