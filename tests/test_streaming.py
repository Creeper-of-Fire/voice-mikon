"""实时预览(边说边显示识别文字)测试。

不碰真实模型 / 音频设备 / 剪贴板 —— 用假引擎和假录音器,
验证的是"预览事件怎么发"和"停止时最终结果仍然是整段识别"。

设计前提(重构后):
预览位插一颗**分句预览引擎** —— 它靠能量 VAD 切句,切一句认一句,
回调给的是"已识别句子的拼接"。所以假录音器喂的必须是
"有声 + 静音"交替的音频:没有静音,分句引擎永远断不出句子。

最终位固定整段引擎:最终结果永远来自停止时那次整段识别,
和预览层怎么马虎无关。
"""

import time
from pathlib import Path

import numpy as np
import pytest

from qwen3_voice.asr.streaming import (
    FullPassEngine,
    PreviewEngineKind,
    SentencePreviewEngine,
)
from qwen3_voice.audio.recorder import Recorder
from qwen3_voice.config import Config
from qwen3_voice.core import controller as controller_module
from qwen3_voice.core.controller import VoiceController


# ---------------- 假实现 ----------------

class FakeEngine:
    """假 ASR 引擎。

    识别文本按**音频长度**编造,这样能区分"预览认的那一句"(短)
    和"停止时的整段识别"(完整音频)。
    """

    def __init__(self, fail_times=0, result=None):
        self.fail_times = fail_times
        self.result = result
        self.model_tag = "0.6B"
        self.loaded = True
        self.calls = []

    def load(self, model_tag=None):
        self.model_tag = model_tag or self.model_tag
        return 0.1

    def switch_model(self, model_tag):
        self.model_tag = model_tag
        return 0.1

    def transcribe(self, audio, sample_rate=16000, language=None):
        self.calls.append((len(audio), sample_rate))
        if self.fail_times > 0:
            self.fail_times -= 1
            raise RuntimeError("识别失败")
        if self.result is not None:
            return self.result
        return f"文本-{len(audio)}"


def tone(seconds: float, rate: int = 16000, amp: int = 5000) -> np.ndarray:
    return (np.ones(int(seconds * rate)) * amp).astype(np.int16)


def quiet(seconds: float, rate: int = 16000) -> np.ndarray:
    return np.zeros(int(seconds * rate), dtype=np.int16)


def speech_pattern(rate: int = 16000, repeats: int = 3):
    """默认的录音内容:有声 0.4s + 静音 0.4s,重复几次。

    静音是分句预览引擎的断句依据 —— 没有它就断不出句子。
    """
    chunks = []
    for _ in range(repeats):
        chunks.append(tone(0.4, rate))
        chunks.append(quiet(0.4, rate))
    return chunks


class BaseFakeRecorder:
    """假录音器的公共部分。

    ``stop()`` 返回整段音频(和预览看到的不一样),
    这样能验证"最终结果来自整段识别,而不是预览"。
    """

    def __init__(self, audio=None, chunks=None, native_rate=16000):
        self.audio = audio if audio is not None else np.zeros(16000, dtype=np.int16)
        self.native_rate = native_rate
        self._chunks = list(chunks) if chunks is not None else speech_pattern(native_rate)
        self._pos = 0
        self.started = True
        self.stopped = False

    def start(self):
        return self.started

    def stop(self):
        self.stopped = True
        return self.audio

    def levels(self):
        return []

    @property
    def elapsed(self):
        return 1.0


class FakeRecorder(BaseFakeRecorder):
    """假录音器(有增量接口)。

    ``drain_chunks()`` 每次给一块新音频(增量,原生采样率)——
    这是预览线程真正用的接口。
    """

    def drain_chunks(self):
        if self._pos >= len(self._chunks):
            return np.zeros((0,), dtype=np.int16)
        chunk = self._chunks[self._pos]
        self._pos += 1
        return chunk


class SnapshotOnlyRecorder(BaseFakeRecorder):
    """只有 ``snapshot()`` 的假录音器(没有 ``drain_chunks``)。

    ``snapshot()`` 每次返回"到目前为止录到的全部音频"(会变长)——
    预览线程靠差量拉取从中切出新增部分。
    """

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._acc: list[np.ndarray] = []

    def snapshot(self):
        if self._pos < len(self._chunks):
            self._acc.append(self._chunks[self._pos])
            self._pos += 1
        if not self._acc:
            return np.zeros((0,), dtype=np.int16)
        return np.concatenate(self._acc)


# ---------------- 夹具与工具 ----------------

@pytest.fixture
def controller(tmp_path: Path):
    cfg = Config(model_root=str(tmp_path), silence_rms=0.0001)

    injected: list[str] = []

    def fake_inject(text, hwnd=0, restore=True, trailing_space=True):
        injected.append(text)
        return True

    recorder = FakeRecorder(
        audio=(np.ones(32000) * 5000).astype(np.int16),    # 整段 2 秒
    )

    def fake_recorder_factory(config, device=None):
        return recorder

    ctrl = VoiceController(cfg, injector=fake_inject,
                           recorder_factory=fake_recorder_factory)
    ctrl.engine = FakeEngine()
    ctrl._init_model = lambda: None  # 不要真的加载模型
    ctrl._init_mic = lambda: None
    ctrl.injected = injected
    ctrl.recorder_obj = recorder
    yield ctrl
    ctrl._stop_preview()


def wait_for(predicate, timeout=5.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(0.02)
    return False


def preview_texts(events) -> list[str]:
    """非空的预览文字(空串是"清空"信号,不算预览内容)。"""
    return [e.text for e in events if e.kind == "preview" and e.text]


def preview_clears(events) -> list:
    return [e for e in events if e.kind == "preview" and not e.text]


def _recorder_with_frames(native_rate: int, n_frames: int = 2,
                          frames: int = 800) -> Recorder:
    """造一个装着假数据的录音器(不走采集回调的设备路径)。

    帧的形状是 (frames, channels) —— sounddevice 0.5 的回调就是给
    (帧数, 声道数),单声道就是 (N, 1)。
    """
    rec = Recorder(device=None, target_rate=16000)
    rec.native_rate = native_rate
    for i in range(n_frames):
        block = np.full((frames, 1), 1000 * (i + 1), dtype=np.int16)
        rec._callback(block, frames, None, None)
    return rec


# ---------------- Recorder 快照 / 增量 ----------------
#
# 录音器本身的行为契约(快照 vs stop()、采样率、增量拉取)归
# ``tests/test_audio.py`` 管 —— 那边才是录音器行为的所在地。
# 这里只保留"预览链路怎么用录音器"相关的那几条。

def test_snapshot_empty_before_any_audio():
    """刚开始录音时快照是空的 —— 调用方必须判空。"""
    rec = Recorder(device=None, target_rate=16000)
    snap = rec.snapshot()
    assert len(snap) == 0
    assert snap.dtype == np.int16


def test_drain_chunks_returns_increment():
    """增量接口:每次只给新录到的那部分,不重复。"""
    rec = _recorder_with_frames(16000, n_frames=3, frames=800)
    first = rec.drain_chunks()
    assert len(first) == 2400           # 攒下的三帧一起给

    # 再录一帧,增量接口只给这一帧
    rec._callback(np.full((800, 1), 9, dtype=np.int16), 800, None, None)
    second = rec.drain_chunks()
    assert len(second) == 800
    # 拼起来 == 全量
    assert np.array_equal(np.concatenate([first, second]), rec.snapshot())


def test_drain_chunks_empty_when_nothing_new():
    """没有新音频时给空数组,不能报错。"""
    rec = _recorder_with_frames(16000)
    rec.drain_chunks()
    assert len(rec.drain_chunks()) == 0


def test_drain_chunks_does_not_consume_audio():
    """增量拉取不能消费音频 —— stop() 仍然拿到完整数据。"""
    rec = _recorder_with_frames(16000)
    rec.drain_chunks()
    stopped = rec.stop()
    assert len(stopped) == 1600


# ---------------- 预览线程 ----------------

def test_preview_thread_emits_preview_event(controller):
    """录音过程中会发 kind="preview" 的事件,文字来自预览引擎。"""
    events = []
    controller.subscribe(events.append)

    assert controller.start_recording() is True
    assert wait_for(lambda: preview_texts(events))

    # 分句引擎认的是"那一句"(0.4s 有声),不是整段
    assert preview_texts(events)[0] == "文本-6400"
    # 识别必须走 engine.transcribe,且采样率是模型要的 16 k
    assert controller.engine.calls
    assert controller.engine.calls[0][1] == 16000


def test_preview_start_clears_previous_text(controller):
    """录音开始时要发一次空预览,把上一轮的残留清掉。"""
    events = []
    controller.subscribe(events.append)
    controller.start_recording()
    assert preview_clears(events)


def test_preview_thread_stops_after_stop(controller):
    """停止后预览线程退出,并且不再发预览事件。"""
    events = []
    controller.subscribe(events.append)
    controller.start_recording()
    assert wait_for(lambda: preview_texts(events))

    controller.stop_recording()
    assert controller._preview_thread is None

    count = len(preview_texts(events))
    time.sleep(0.5)
    assert len(preview_texts(events)) == count


def test_preview_thread_survives_engine_error(controller):
    """引擎抛异常时预览线程不能死 —— 下一句还要能出文字。"""
    controller.engine = FakeEngine(fail_times=1)
    events = []
    controller.subscribe(events.append)

    controller.start_recording()
    # 第一句推理抛异常,第二句成功
    assert wait_for(lambda: preview_texts(events))
    assert len(controller.engine.calls) >= 2


def test_result_is_full_recognition_not_preview(controller):
    """最终结果必须来自停止时的整段识别,不能是预览文字。"""
    events = []
    controller.subscribe(events.append)

    controller.start_recording()
    assert wait_for(lambda: preview_texts(events))
    preview = preview_texts(events)[0]

    controller.stop_recording()
    assert wait_for(lambda: any(e.kind == "result" for e in events))

    result = [e for e in events if e.kind == "result"][0]
    assert result.text == "文本-32000"      # 整段音频的长度
    assert result.text != preview

    # 注入的也只能是整段识别结果
    assert wait_for(lambda: controller.injected)
    assert controller.injected == ["文本-32000"]

    # 结果到了之后清空预览
    assert len(preview_clears(events)) >= 2


def test_final_slot_is_full_pass_engine(controller):
    """最终位固定整段引擎 —— 最终结果只能是整段识别那一次。"""
    controller.start_recording()
    assert wait_for(lambda: controller.engine.calls)

    controller.stop_recording()
    assert wait_for(lambda: controller.injected)
    # 整段识别只发生一次(长度 = 整段音频)
    full_calls = [c for c in controller.engine.calls if c[0] == 32000]
    assert len(full_calls) == 1


# ---------------- 插槽与回退 ----------------

def test_default_preview_slot_is_sentence_engine(controller):
    """默认组合:预览位分句引擎,最终位整段引擎。

    用 isinstance 而不是 ``type(...).__name__`` —— 改个类名字符串断言
    就碎,这不是我们要守的东西。
    而且要**两边都断言**:SentencePreviewEngine 和 FullPassEngine
    都是 StreamingEngine 的子类,只断言 isinstance(x, StreamingEngine)
    分不出谁是谁。所以这里明确断言"是谁" + "不是谁"。
    """
    assert controller_module.PREVIEW_ENGINE_KIND == PreviewEngineKind.SENTENCE
    controller.start_recording()

    preview = controller._preview_engine
    final = controller._final_engine

    # 预览位是分句引擎
    assert isinstance(preview, SentencePreviewEngine)
    assert not isinstance(preview, FullPassEngine)
    # 最终位是整段引擎
    assert isinstance(final, FullPassEngine)
    assert not isinstance(final, SentencePreviewEngine)


def test_full_pass_fallback_still_previews(monkeypatch, controller):
    """回退模式:整段引擎插预览位 —— 预览变成整段重识别(旧行为)。"""
    monkeypatch.setattr(controller_module, "PREVIEW_ENGINE_KIND",
                        PreviewEngineKind.FULL_PASS)
    events = []
    controller.subscribe(events.append)

    controller.start_recording()
    assert wait_for(lambda: preview_texts(events))

    # 整段重识别:认的是"累积到目前的全部音频"(0.4s 有声 + 0.4s 静音)
    assert preview_texts(events)[0] == "文本-12800"


def test_snapshot_only_recorder_still_previews(tmp_path):
    """只有 snapshot() 的录音器也能出预览(差量拉取回退路径)。"""
    cfg = Config(model_root=str(tmp_path), silence_rms=0.0001)
    recorder = SnapshotOnlyRecorder(
        audio=(np.ones(32000) * 5000).astype(np.int16))

    ctrl = VoiceController(cfg, injector=lambda *a, **k: True,
                           recorder_factory=lambda config, device=None: recorder)
    ctrl.engine = FakeEngine()
    ctrl._init_model = lambda: None
    ctrl._init_mic = lambda: None

    events = []
    ctrl.subscribe(events.append)
    try:
        ctrl.start_recording()
        assert wait_for(lambda: preview_texts(events))
        assert preview_texts(events)[0] == "文本-6400"
    finally:
        ctrl._stop_preview()


# ---------------- 作废机制 ----------------

def test_stale_partial_is_not_pushed(controller):
    """过期的 partial 绝不推 UI —— session 代号机制照旧生效。"""
    captured: dict = {}
    original = controller._make_preview_engine

    def spy(sample_rate, partial):
        captured["partial"] = partial
        return original(sample_rate, partial)

    controller._make_preview_engine = spy

    events = []
    controller.subscribe(events.append)
    controller.start_recording()
    assert wait_for(lambda: preview_texts(events))

    # 预览线程收工,然后把这一轮作废(模拟取消/开了新一轮)
    controller._stop_preview()
    controller._session += 1

    before = len(preview_texts(events))
    captured["partial"]("作废的文字")
    assert len(preview_texts(events)) == before
    assert "作废的文字" not in [e.text for e in events]


def test_current_partial_is_pushed(controller):
    """没作废的一轮,partial 照常推给界面。"""
    captured: dict = {}
    original = controller._make_preview_engine

    def spy(sample_rate, partial):
        captured["partial"] = partial
        return original(sample_rate, partial)

    controller._make_preview_engine = spy

    events = []
    controller.subscribe(events.append)
    controller.start_recording()
    assert wait_for(lambda: preview_texts(events))
    controller._stop_preview()

    captured["partial"]("这一轮的文字")
    assert "这一轮的文字" in preview_texts(events)


def test_cancel_drops_preview_text(controller):
    """取消:预览文字清掉,之后也不该再有预览事件冒出来。"""
    events = []
    controller.subscribe(events.append)
    controller.start_recording()
    assert wait_for(lambda: preview_texts(events))

    controller.cancel_recording()
    assert controller.state.value == "idle"
    count = len(preview_texts(events))
    time.sleep(0.3)
    assert len(preview_texts(events)) == count
