"""流式识别引擎的接口契约与 VAD 切句测试。

不碰真实模型 —— 用假引擎,验证的是引擎这一层的行为:

  - 统一接口的时序:``feed(chunk)`` → ``partial(text)`` → ``close()``
  - 能量 VAD 的断句 / 强切 / 丢弃逻辑(合成的能量序列)
  - 整段引擎的"只攒 chunk、最后一次性识别"语义

这些逻辑不依赖 GPU 也不依赖麦克风,但错了会直接毁掉用户的预览体验,
所以单独守住。
"""

import numpy as np
import pytest

from qwen3_voice.asr.streaming import (
    FullPassEngine,
    PreviewEngineKind,
    SentencePreviewEngine,
    StreamingEngine,
    make_preview_engine,
)

RATE = 16000


# ---------------- 假实现与工具 ----------------

class FakeEngine:
    """假的整段识别器:按音频长度编造文本,方便区分"认了哪一句"。"""

    def __init__(self, fail_times=0, result=None):
        self.fail_times = fail_times
        self.result = result
        self.calls = []

    def transcribe(self, audio, sample_rate=16000, language=None):
        self.calls.append((len(audio), sample_rate))
        if self.fail_times > 0:
            self.fail_times -= 1
            raise RuntimeError("识别失败")
        if self.result is not None:
            return self.result
        return f"文本-{len(audio)}"


def tone(seconds: float, rate: int = RATE, amp: int = 5000) -> np.ndarray:
    """有声片段。"""
    return (np.ones(int(seconds * rate)) * amp).astype(np.int16)


def quiet(seconds: float, rate: int = RATE) -> np.ndarray:
    """静音片段。"""
    return np.zeros(int(seconds * rate), dtype=np.int16)


class Recorder:
    """收集 partial 回调的假引擎外壳。"""

    def __init__(self, engine: FakeEngine, **kwargs):
        self.partials: list[str] = []
        self.engine = engine
        self._engine = make_preview_engine(
            kwargs.pop("kind", PreviewEngineKind.SENTENCE),
            engine, kwargs.pop("sample_rate", RATE),
            partial=self.partials.append)

    def feed(self, chunk):
        self._engine.feed(chunk)

    def close(self):
        return self._engine.close()


def sentence_engine(engine: FakeEngine, **kwargs) -> SentencePreviewEngine:
    """造一个分句引擎,partial 回调收集到列表里。"""
    partials: list[str] = []
    eng = SentencePreviewEngine(engine, kwargs.pop("sample_rate", RATE),
                                 partial=partials.append, **kwargs)
    eng.partials = partials   # 测试用:看一眼回调收到了什么
    return eng


# ---------------- 统一接口 ----------------

def test_both_engines_implement_same_interface():
    """两个引擎是同一个接口的两颗实现 —— 插槽可以互换。"""
    fake = FakeEngine()
    for eng in (FullPassEngine(fake, RATE),
                SentencePreviewEngine(fake, RATE)):
        assert isinstance(eng, StreamingEngine)
        assert hasattr(eng, "feed") and hasattr(eng, "close")


def test_make_preview_engine_picks_implementation():
    fake = FakeEngine()
    sentence = make_preview_engine(PreviewEngineKind.SENTENCE, fake, RATE)
    full = make_preview_engine(PreviewEngineKind.FULL_PASS, fake, RATE)
    assert isinstance(sentence, SentencePreviewEngine)
    assert isinstance(full, FullPassEngine)


# ---------------- 整段引擎 ----------------

def test_full_pass_accumulates_until_close():
    """整段引擎:feed 只攒,不识别;close 才一次性识别。"""
    fake = FakeEngine()
    eng = FullPassEngine(fake, RATE)
    eng.feed(tone(0.5))
    eng.feed(tone(0.5))
    assert fake.calls == []          # 还没识别

    text = eng.close()
    assert len(fake.calls) == 1
    assert fake.calls[0] == (16000, RATE)   # 两块拼起来的长度
    assert text == "文本-16000"


def test_full_pass_close_is_idempotent():
    """close 只能识别一次 —— 重复调用不能再跑一遍推理。"""
    fake = FakeEngine()
    eng = FullPassEngine(fake, RATE)
    eng.feed(tone(0.5))
    first = eng.close()
    second = eng.close()
    assert first == second
    assert len(fake.calls) == 1


def test_full_pass_feed_after_close_ignored():
    fake = FakeEngine()
    eng = FullPassEngine(fake, RATE)
    eng.close()
    eng.feed(tone(1.0))
    assert len(fake.calls) == 0


def test_full_pass_without_partial_never_refreshes():
    """最终位(不给 partial 回调):只在 close 时识别一次。"""
    fake = FakeEngine()
    eng = FullPassEngine(fake, RATE)
    for _ in range(10):
        eng.feed(tone(0.2))
    assert fake.calls == []


def test_full_pass_with_partial_refreshes_old_behaviour():
    """预览位(给 partial 回调):整段重识别并回调 —— 旧行为。"""
    fake = FakeEngine()
    partials: list[str] = []
    eng = FullPassEngine(fake, RATE, partial=partials.append)
    eng.feed(tone(0.6))
    assert len(fake.calls) == 1        # 音频够了就重识别一次
    assert partials == ["文本-9600"]


# ---------------- 分句引擎:VAD 切句 ----------------

def test_silence_cuts_sentence():
    """约 300ms 静音 → 断句 → 立刻识别这一句。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(tone(0.4))
    assert fake.calls == []            # 还没断句
    eng.feed(quiet(0.4))

    assert len(fake.calls) == 1
    # 句尾的静音被裁掉,认的是那 0.4s 的有声部分
    assert fake.calls[0] == (6400, RATE)
    assert eng.partials == ["文本-6400"]


def test_no_cut_without_silence():
    """一直说话不断句 —— 预览要等静音(或强切)才出字。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(tone(1.0))
    assert fake.calls == []
    assert eng.partials == []


def test_force_cut_on_long_sentence():
    """一直说不停也要切(上限 20s),否则预览一直干等。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(tone(25.0))
    assert len(fake.calls) == 1
    assert fake.calls[0][0] == int(20.0 * RATE)


def test_silence_only_is_discarded():
    """全是静音不是一句话 —— 丢掉,别识别出幻觉文字。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(quiet(2.0))
    assert fake.calls == []
    assert eng.partials == []


def test_short_sentence_is_dropped():
    """太短的句子识别不出东西 —— 丢掉。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(tone(0.1))
    eng.feed(quiet(0.4))
    assert fake.calls == []
    assert eng.partials == []


def test_partial_receives_joined_sentences():
    """partial 回调给的是"已识别句子的拼接",调用方整体替换即可。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(tone(0.4))
    eng.feed(quiet(0.4))
    eng.feed(tone(0.6))
    eng.feed(quiet(0.4))

    assert len(fake.calls) == 2
    assert eng.partials == ["文本-6400", "文本-6400文本-9600"]


def test_close_flushes_pending_sentence():
    """close() 把最后一句也认掉 —— close 的语义就是"给我最终文本"。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(tone(0.4))
    assert fake.calls == []

    text = eng.close()
    assert len(fake.calls) == 1
    assert text == "文本-6400"


def test_close_does_not_notify_partial():
    """close 不再推 partial —— 停止录音时预览条马上要被最终结果接管。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(tone(0.4))
    eng.close()
    assert eng.partials == []


def test_close_is_idempotent():
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(tone(0.4))
    eng.feed(quiet(0.4))
    first = eng.close()
    second = eng.close()
    assert first == second
    assert len(fake.calls) == 1


def test_feed_after_close_ignored():
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.close()
    eng.feed(tone(0.4))
    eng.feed(quiet(0.4))
    assert fake.calls == []


def test_big_chunk_is_split_into_frames():
    """一次喂进来的大块也要能断句 —— VAD 按固定帧长判断,不能被整块抹平。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(np.concatenate([tone(0.4), quiet(0.4), tone(0.4)]))
    assert len(fake.calls) == 1
    assert fake.calls[0] == (6400, RATE)


def test_recognition_failure_does_not_kill_engine():
    """某一句识别失败只丢这一句,引擎继续干活。"""
    fake = FakeEngine(fail_times=1)
    eng = sentence_engine(fake)
    eng.feed(tone(0.4))
    eng.feed(quiet(0.4))
    assert eng.partials == []          # 第一句失败了

    eng.feed(tone(0.4))
    eng.feed(quiet(0.4))
    assert eng.partials == ["文本-6400"]


def test_partial_callback_exception_does_not_kill_engine():
    """回调抛异常不该把引擎带崩(调用方的问题不该反噬识别链路)。"""
    fake = FakeEngine()

    def boom(text):
        raise RuntimeError("回调炸了")

    eng = SentencePreviewEngine(fake, RATE, partial=boom)
    eng.feed(tone(0.4))
    eng.feed(quiet(0.4))
    assert len(fake.calls) == 1


def test_leading_silence_is_not_polluting_next_sentence():
    """长时间静音之后再说话,那一句不能前面挂一大段静音。"""
    fake = FakeEngine()
    eng = sentence_engine(fake)
    eng.feed(quiet(3.0))
    eng.feed(tone(0.4))
    eng.feed(quiet(0.4))
    assert len(fake.calls) == 1
    assert fake.calls[0] == (6400, RATE)   # 只有那 0.4s 的有声


def test_custom_thresholds_are_respected():
    """阈值可调(测试里把静音时间调短,不用真的等 300ms)。"""
    fake = FakeEngine()
    eng = sentence_engine(fake, silence_seconds=0.05)
    eng.feed(tone(0.3))
    eng.feed(quiet(0.1))
    assert len(fake.calls) == 1
