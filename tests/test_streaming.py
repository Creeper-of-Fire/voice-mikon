"""实时预览(边说边显示识别文字)测试。

不碰真实模型 / 音频设备 / 剪贴板 —— 用假引擎和假录音器,
验证的是"预览事件怎么发"和"停止时最终结果仍然是整段识别"。

设计前提(和官方 qwen_asr 的 streaming_transcribe 一致):
每次把**累积到目前的全部音频**重新喂给模型,模型每次都重新生成完整文本,
所以预览文字是**整体替换**的,不存在拼接/去重/切句。
"""

import time
from pathlib import Path

import numpy as np
import pytest

from qwen3_voice.audio.recorder import Recorder
from qwen3_voice.config import Config
from qwen3_voice.core.controller import VoiceController


# ---------------- 假实现 ----------------

class FakeEngine:
    """假 ASR 引擎。

    识别文本按**音频长度**编造,这样能区分"预览那一轮"(短音频)和
    "停止时的整段识别"(完整音频)。
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


class FakeRecorder:
    """假录音器。

    ``snapshot()`` 返回的是"到目前为止的音频"(短),
    ``stop()`` 返回整段音频(长)—— 两者故意不一样,用来验证
    最终结果来自整段识别,而不是预览。
    """

    def __init__(self, audio=None, snapshot=None, native_rate=16000):
        self.audio = audio if audio is not None else np.zeros(16000, dtype=np.int16)
        self._snapshot = snapshot if snapshot is not None else self.audio
        self.native_rate = native_rate
        self.started = True
        self.stopped = False

    def start(self):
        return self.started

    def stop(self):
        self.stopped = True
        return self.audio

    def snapshot(self):
        return self._snapshot

    def levels(self):
        return []

    @property
    def elapsed(self):
        return 1.0


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
        snapshot=(np.ones(8000) * 5000).astype(np.int16),  # 快照 0.5 秒
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
    # 预览调度参数调快,测试不用等 1.5 秒
    ctrl.PREVIEW_MIN_INTERVAL = 0.05
    ctrl.PREVIEW_MIN_SECONDS = 0.1
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


# ---------------- Recorder.snapshot ----------------

def test_snapshot_empty_before_any_audio():
    """刚开始录音时快照是空的 —— 调用方必须判空。"""
    rec = Recorder(device=None, target_rate=16000)
    snap = rec.snapshot()
    assert len(snap) == 0
    assert snap.dtype == np.int16


def test_snapshot_matches_stop():
    """快照内容和 stop() 完全一致(同样采样率时逐样本相等)。"""
    rec = _recorder_with_frames(16000)
    snap = rec.snapshot()
    stopped = rec.stop()

    assert snap.dtype == np.int16
    assert len(snap) == 1600
    assert np.array_equal(snap, stopped)


def test_snapshot_does_not_consume_audio():
    """多次取快照不能消费音频 —— stop() 仍然拿到完整数据。"""
    rec = _recorder_with_frames(16000)
    snaps = [rec.snapshot() for _ in range(3)]
    for snap in snaps:
        assert len(snap) == 1600

    stopped = rec.stop()
    assert len(stopped) == 1600
    assert np.array_equal(stopped, snaps[-1])


def test_snapshot_is_native_rate_stop_is_same_rate():
    """快照和 stop() 都是原生采样率 —— 重采样交给 qwen_asr 内部做。"""
    rec = _recorder_with_frames(48000)
    snap = rec.snapshot()
    assert len(snap) == 1600                      # 原生 48 k

    stopped = rec.stop()
    assert len(stopped) == 1600                   # 同样是原生 48 k,没有降采样
    assert np.array_equal(snap, stopped)


# ---------------- 预览线程 ----------------

def test_preview_thread_emits_preview_event(controller):
    """录音过程中会发 kind="preview" 的事件,文字来自引擎。"""
    events = []
    controller.subscribe(events.append)

    assert controller.start_recording() is True
    assert wait_for(lambda: preview_texts(events))

    assert preview_texts(events)[0] == "文本-8000"   # 快照长度 → 假引擎的输出
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
    """引擎抛异常时预览线程不能死 —— 下一轮还要能出文字。"""
    controller.engine = FakeEngine(fail_times=1)
    events = []
    controller.subscribe(events.append)

    controller.start_recording()
    # 第一次推理抛异常,第二次成功
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
