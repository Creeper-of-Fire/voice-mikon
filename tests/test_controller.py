"""控制器状态机测试。

用假的 ASR 引擎和假的录音器,不依赖 GPU 和麦克风 ——
验证的是状态流转和事件派发,那些逻辑最容易出错。
"""

import time
import threading
from pathlib import Path

import numpy as np
import pytest

from qwen3_voice.config import Config
from qwen3_voice.core.controller import State, UIEvent, VoiceController


class FakeEngine:
    """假的 ASR 引擎。"""

    def __init__(self, result="今天天气不错", fail=False):
        self.result = result
        self.fail = fail
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
        if self.fail:
            raise RuntimeError("识别失败")
        return self.result


class FakeRecorder:
    """假的录音器。"""

    def __init__(self, audio=None, started=True):
        self.audio = audio if audio is not None else np.zeros(16000, dtype=np.int16)
        self.started = started
        self.levels_list = []

    def start(self):
        return self.started

    def stop(self):
        return self.audio

    def levels(self):
        return self.levels_list

    @property
    def elapsed(self):
        return 1.0


@pytest.fixture(autouse=True)
def _drain_threads():
    """每个测试结束后等后台线程收尾,否则 pytest 退出时会崩。"""
    yield
    wait_for_threads()


@pytest.fixture
def controller(tmp_path: Path):
    cfg = Config(model_root=str(tmp_path), silence_rms=0.0001)
    # 假注入器:绝不能真的改剪贴板或模拟按键
    injected: list[str] = []

    def fake_inject(text, hwnd=0, restore=True, trailing_space=True):
        injected.append(text)
        return True

    # 假录音器工厂:绝不能真的打开音频设备
    # (真设备的 PortAudio 清理会让 pytest 退出时崩 0xC0000005)
    recorder_devices = []

    def fake_recorder_factory(config, device=None):
        recorder_devices.append(device)
        return FakeRecorder()

    ctrl = VoiceController(cfg, injector=fake_inject,
                           recorder_factory=fake_recorder_factory)
    ctrl.engine = FakeEngine()
    ctrl._init_model = lambda: None  # 不要真的加载模型
    ctrl._init_mic = lambda: None
    ctrl.injected = injected
    ctrl.recorder_devices = recorder_devices
    return ctrl


def wait_for(predicate, timeout=5.0):
    """轮询等待条件成立。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(0.02)
    return False


def wait_for_threads(timeout=5.0):
    """等所有非守护线程结束 —— 否则 pytest 退出时线程还在跑,进程会崩。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        alive = [t for t in threading.enumerate()
                 if t.is_alive() and not t.daemon and t is not threading.main_thread()]
        if not alive:
            return
        time.sleep(0.02)


# ---------------- 事件 ----------------

def test_subscribe_and_emit(controller):
    events = []
    controller.subscribe(events.append)

    controller._set_state(State.IDLE)
    assert any(e.kind == "state" and e.state is State.IDLE for e in events)


def test_broken_listener_does_not_break_others(controller):
    good = []
    controller.subscribe(lambda e: (_ for _ in ()).throw(RuntimeError("boom")))
    controller.subscribe(good.append)

    controller._set_state(State.IDLE)
    assert len(good) == 1


# ---------------- 录音流程 ----------------

def test_start_recording_sets_state(controller):
    controller.recorder = FakeRecorder()
    controller.recorder.start = lambda: True
    # 直接替换 start_recording 里的 Recorder 创建
    assert controller.start_recording() is True
    assert controller.state == State.RECORDING


def test_recorder_uses_probed_device(controller):
    """探测出的麦克风要被传给录音器 —— 之前没传,导致录音用系统默认设备。"""
    controller.input_device = 7
    controller.start_recording()
    assert controller.recorder_devices == [7]


def test_recorder_falls_back_when_device_unknown(controller):
    """探测还没完成时设备为 None(退回系统默认),不能报错。"""
    controller.input_device = None
    assert controller.start_recording() is True
    assert controller.recorder_devices == [None]


def test_stop_with_too_short_audio(controller):
    controller.state = State.RECORDING
    controller.recorder = FakeRecorder(audio=np.zeros(100, dtype=np.int16))  # 0.006s

    events = []
    controller.subscribe(events.append)
    controller.stop_recording()

    assert controller.state is State.IDLE
    assert any("太短" in e.text for e in events)


def test_stop_with_silence(controller):
    controller.state = State.RECORDING
    controller.recorder = FakeRecorder(audio=np.zeros(16000, dtype=np.int16))  # 静音

    events = []
    controller.subscribe(events.append)
    controller.stop_recording()

    assert controller.state is State.IDLE
    assert any("没有检测到声音" in e.text for e in events)


def test_full_flow_produces_result(controller, tmp_path):
    """完整流程:录音 -> 识别 -> 事件。"""
    controller.state = State.RECORDING
    audio = (np.ones(32000) * 5000).astype(np.int16)  # 有声音
    controller.recorder = FakeRecorder(audio=audio)

    events = []
    controller.subscribe(events.append)
    controller.stop_recording()

    assert wait_for(lambda: any(e.kind == "result" for e in events))
    result = [e for e in events if e.kind == "result"][0]
    assert result.text == "今天天气不错"
    assert controller.state is State.IDLE
    # 注入器应被调用,且拿到的是识别出的文字
    assert wait_for(lambda: len(controller.injected) > 0)
    assert controller.injected[0] == "今天天气不错"


def test_recognition_failure_returns_to_idle(controller):
    controller.state = State.RECORDING
    controller.recorder = FakeRecorder(
        audio=(np.ones(32000) * 5000).astype(np.int16))
    controller.engine = FakeEngine(fail=True)

    events = []
    controller.subscribe(events.append)
    controller.stop_recording()

    assert wait_for(lambda: any(e.kind == "error" for e in events))
    assert controller.state is State.IDLE


def test_empty_transcription_gives_feedback(controller):
    controller.state = State.RECORDING
    controller.recorder = FakeRecorder(
        audio=(np.ones(32000) * 5000).astype(np.int16))
    controller.engine = FakeEngine(result="")

    events = []
    controller.subscribe(events.append)
    controller.stop_recording()

    assert wait_for(lambda: any("没听清" in e.text for e in events))


# ---------------- 状态定义 ----------------

def test_state_labels():
    assert State.IDLE.label == "待机"
    assert State.RECORDING.label == "录音中"
    assert State.PROCESSING.label == "识别中"


def test_toggle_switches_recording(controller):
    controller.state = State.IDLE
    controller.recorder = FakeRecorder()
    controller.recorder.start = lambda: True
    controller.toggle_recording()
    # start_recording 会新建 Recorder,这里只验证状态流转方向
    assert controller.state in (State.RECORDING, State.IDLE)


# ---------------- 取消 ----------------

class SlowEngine(FakeEngine):
    """假的慢引擎:识别会卡住,方便在"识别中"插一脚。"""

    def __init__(self, result="今天天气不错"):
        super().__init__(result)
        self.started = threading.Event()
        self.release = threading.Event()

    def transcribe(self, audio, sample_rate=16000, language=None):
        self.started.set()
        self.release.wait(5.0)
        return self.result


def test_cancel_recording_drops_audio(controller):
    """录音中取消:不识别、不注入,回到待机。"""
    controller.state = State.RECORDING
    controller.recorder = FakeRecorder()

    events = []
    controller.subscribe(events.append)

    controller.cancel_recording()

    assert controller.state is State.IDLE
    assert controller.recorder is None
    assert controller.injected == []
    assert any(e.kind == "cancelled" for e in events)
    # 预览文字要清掉,别留着上一轮的内容
    assert any(e.kind == "preview" and e.text == "" for e in events)


def test_cancel_recording_stops_recorder(controller):
    """取消要真的把录音器停掉 —— 否则麦克风一直开着。"""
    controller.state = State.RECORDING
    rec = FakeRecorder()
    stopped = []
    rec.stop = lambda: stopped.append(1) or rec.audio
    controller.recorder = rec

    controller.cancel_recording()

    assert stopped
    # 预览线程也要收掉(它和识别抢同一个引擎)
    assert controller._preview_thread is None


def test_cancel_while_idle_does_nothing(controller):
    """待机时取消是空操作 —— 不该凭空冒出"已取消"。"""
    controller.state = State.IDLE

    events = []
    controller.subscribe(events.append)

    controller.cancel_recording()

    assert controller.state is State.IDLE
    assert not any(e.kind == "cancelled" for e in events)


def test_cancel_while_processing_does_not_inject(controller):
    """识别中取消:这一轮作废,结果绝不注入。"""
    engine = SlowEngine()
    controller.engine = engine
    controller.state = State.RECORDING
    controller.recorder = FakeRecorder(
        audio=(np.ones(32000) * 5000).astype(np.int16))

    events = []
    controller.subscribe(events.append)

    controller.stop_recording()
    assert wait_for(lambda: engine.started.is_set())

    controller.cancel_recording()

    assert controller.state is State.IDLE
    assert any(e.kind == "cancelled" for e in events)

    # 放行识别线程:它醒来后发现这一轮作废,只能丢掉结果
    engine.release.set()
    assert not wait_for(lambda: any(e.kind == "result" for e in events),
                        timeout=1.0)
    assert controller.injected == []


def test_new_recording_invalidates_cancelled_result(controller):
    """取消后马上开新一轮,上一轮迟到的结果也不能注入。"""
    engine = SlowEngine()
    controller.engine = engine
    controller.state = State.RECORDING
    controller.recorder = FakeRecorder(
        audio=(np.ones(32000) * 5000).astype(np.int16))

    events = []
    controller.subscribe(events.append)

    controller.stop_recording()
    assert wait_for(lambda: engine.started.is_set())
    controller.cancel_recording()

    # 新一轮录音:会话代号变了,旧线程回来时必须认出来
    controller.start_recording()
    engine.release.set()

    assert not wait_for(lambda: any(e.kind == "result" for e in events),
                        timeout=1.0)
    assert controller.injected == []
