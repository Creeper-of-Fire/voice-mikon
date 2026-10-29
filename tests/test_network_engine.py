"""网络 ASR 后端(DashScope)的契约测试。

**全程不联网**:HTTP 传输层是可注入的,这里塞的是假实现
(以及一个走 ``httpx.MockTransport`` 的真实 httpx 序列化路径)——
既不烧用户配额,也不依赖网络。

守的是这几件事:

  - 请求体长什么样:model / base64 音频 / 热词 system prompt
  - 响应怎么解析(字符串 content、分段 list content)
  - 错误路径:HTTP 错误 → ``ASRError``、超时 → ``ASRError``、
    缺 API Key → ``ASRError``(不静默回落)
  - **重采样必须正确**:48k → 16k 长度比值对、频率不变(不能混叠)
  - WAV 编码:单声道 / 16 kHz / 16-bit PCM
"""

import base64
import io
import os
import wave

import numpy as np
import pytest

from qwen3_voice.asr.base import ASRBackend, ASRError
from qwen3_voice.asr.network import (
    API_KEY_ENV,
    DEFAULT_BASE_URL,
    DEFAULT_MODEL,
    TARGET_RATE,
    DashScopeBackend,
    encode_wav,
    parse_transcription,
    resample_to,
)


# ---------------- 假传输层 ----------------

class FakeResponse:
    """假的 HTTP 响应。"""

    def __init__(self, status_code=200, payload=None, text=""):
        self.status_code = status_code
        self._payload = payload if payload is not None else {}
        self.text = text

    def json(self):
        if isinstance(self._payload, Exception):
            raise self._payload
        return self._payload


class FakeTransport:
    """假的 HTTP 传输层:记录请求,回放预设响应。

    真实实现走 httpx;这里换成字典级的假实现 ——
    请求体不用经过 JSON 序列化就能断言,失败路径也能精确控制。
    """

    def __init__(self, response=None, error=None):
        self.response = response
        self.error = error
        self.calls: list[dict] = []
        self.closed = False

    def post(self, url, *, headers, body, timeout):
        self.calls.append({
            "url": url,
            "headers": dict(headers),
            "body": body,
            "timeout": timeout,
        })
        if self.error is not None:
            raise self.error
        return self.response

    def close(self):
        self.closed = True


def ok_response(text="识别出来的文字"):
    """一个标准的 OpenAI 兼容响应。"""
    return FakeResponse(200, {
        "choices": [{"message": {"role": "assistant", "content": text}}],
    })


@pytest.fixture
def transport():
    return FakeTransport(ok_response())


@pytest.fixture
def backend(transport, tmp_path, monkeypatch):
    """一台网络后端:假传输层 + 固定 API Key + 隔离的词表文件。"""
    monkeypatch.setenv(API_KEY_ENV, "test-key")
    b = DashScopeBackend(hotwords=["Qwen3-ASR"], transport=transport,
                         hotwords_file=tmp_path / "no_hotwords.txt")
    b.load()
    return b


# ---------------- 抽象基类 ----------------

def test_network_backend_implements_the_common_interface(backend):
    """网络后端和本地后端同属一个抽象 —— 控制器才敢 interchangeable。"""
    assert isinstance(backend, ASRBackend)
    assert hasattr(backend, "transcribe")
    assert hasattr(backend, "load")
    assert hasattr(backend, "unload")
    assert hasattr(backend, "loaded")


def test_network_backend_does_not_support_preview(backend):
    """网络端点是整段请求,做不了逐句实时预览。"""
    assert backend.supports_preview is False


# ---------------- 请求体 ----------------

def test_request_body_contains_model_and_base64_audio(backend, transport):
    """请求体:model + base64 音频 + stream=false。"""
    audio = (np.ones(16000) * 5000).astype(np.int16)
    backend.transcribe(audio, 16000)

    assert len(transport.calls) == 1
    call = transport.calls[0]
    assert call["url"] == f"{DEFAULT_BASE_URL}/chat/completions"
    body = call["body"]
    assert body["model"] == DEFAULT_MODEL
    assert body["stream"] is False

    user = body["messages"][-1]
    assert user["role"] == "user"
    part = user["content"][0]
    assert part["type"] == "input_audio"
    data = part["input_audio"]["data"]
    assert data.startswith("data:audio/wav;base64,")


def test_request_body_carries_hotwords_as_system_prompt(backend, transport):
    """热词走 system prompt,格式和本地引擎的 Context Biasing 一致。"""
    backend.transcribe(np.ones(16000, dtype=np.int16), 16000)

    messages = transport.calls[0]["body"]["messages"]
    assert messages[0]["role"] == "system"
    assert messages[0]["content"] == "Vocabulary: Qwen3-ASR."


def test_request_body_has_no_system_message_without_hotwords(transport, tmp_path,
                                                             monkeypatch):
    """没有热词就不塞 system 消息(别发一句空的 "Vocabulary: .")"""
    monkeypatch.setenv(API_KEY_ENV, "test-key")
    b = DashScopeBackend(hotwords=[], transport=transport,
                         hotwords_file=tmp_path / "no_hotwords.txt")
    b.load()
    b.transcribe(np.ones(1600, dtype=np.int16), 16000)

    messages = transport.calls[0]["body"]["messages"]
    assert all(m["role"] != "system" for m in messages)


def test_authorization_header_carries_api_key(backend, transport):
    """Authorization 头带 Bearer key。"""
    backend.transcribe(np.ones(1600, dtype=np.int16), 16000)
    headers = transport.calls[0]["headers"]
    assert headers["Authorization"] == "Bearer test-key"
    assert headers["Content-Type"] == "application/json"


def test_audio_is_encoded_as_16k_mono_16bit_wav(backend, transport):
    """送出去的音频:16 kHz / 单声道 / 16-bit PCM 的 WAV。"""
    backend.transcribe(np.ones(TARGET_RATE, dtype=np.int16), TARGET_RATE)

    data = transport.calls[0]["body"]["messages"][-1]["content"][0][
        "input_audio"]["data"]
    raw = base64.b64decode(data.split(",", 1)[1])
    with wave.open(io.BytesIO(raw), "rb") as wf:
        assert wf.getnchannels() == 1
        assert wf.getsampwidth() == 2
        assert wf.getframerate() == TARGET_RATE
        assert wf.getnframes() == TARGET_RATE


# ---------------- 响应解析 ----------------

def test_response_is_parsed_to_text(backend):
    """标准响应 → 识别文本。"""
    assert backend.transcribe(np.ones(1600, dtype=np.int16), 16000) == "识别出来的文字"


def test_response_content_as_list_is_joined(backend, transport):
    """content 是分段列表时也要拼起来(有些部署这么返)。"""
    transport.response = FakeResponse(200, {
        "choices": [{"message": {"content": [
            {"type": "text", "text": "前半段"},
            {"type": "text", "text": "后半段"},
        ]}}],
    })
    assert backend.transcribe(np.ones(1600, dtype=np.int16), 16000) == "前半段后半段"


def test_empty_audio_does_not_call_network(backend, transport):
    """空音频直接返回空串,不发请求。"""
    assert backend.transcribe(np.zeros((0,), dtype=np.int16), 16000) == ""
    assert transport.calls == []


def test_response_without_choices_is_an_error(backend, transport):
    """响应里没有结果 → ASRError(别静默返回空串)。"""
    transport.response = FakeResponse(200, {"choices": []})
    with pytest.raises(ASRError):
        backend.transcribe(np.ones(1600, dtype=np.int16), 16000)


def test_non_json_response_is_an_error(backend, transport):
    """响应不是合法 JSON → ASRError。"""
    transport.response = FakeResponse(200, payload=ValueError("不是 JSON"),
                                      text="<html>502</html>")
    with pytest.raises(ASRError):
        backend.transcribe(np.ones(1600, dtype=np.int16), 16000)


# ---------------- 错误路径 ----------------

def test_http_error_becomes_asr_error(backend, transport):
    """HTTP 非 200 → ASRError(带状态码和响应片段)。"""
    transport.response = FakeResponse(401, text="InvalidApiKey")
    with pytest.raises(ASRError) as exc:
        backend.transcribe(np.ones(1600, dtype=np.int16), 16000)
    assert "401" in str(exc.value)


def test_timeout_becomes_asr_error(backend, transport):
    """超时 → ASRError(别让线程直接死)。"""
    transport.error = TimeoutError("timed out")
    with pytest.raises(ASRError) as exc:
        backend.transcribe(np.ones(1600, dtype=np.int16), 16000)
    assert "超时" in str(exc.value)


def test_connection_error_becomes_asr_error(backend, transport):
    """连不上 → ASRError。"""
    transport.error = OSError("连接被拒绝")
    with pytest.raises(ASRError):
        backend.transcribe(np.ones(1600, dtype=np.int16), 16000)


def test_missing_api_key_is_an_error(tmp_path, monkeypatch, transport):
    """没配 API Key → 明确报错,**不静默回落到本地**。"""
    monkeypatch.delenv(API_KEY_ENV, raising=False)
    b = DashScopeBackend(transport=transport,
                         hotwords_file=tmp_path / "no_hotwords.txt")
    with pytest.raises(ASRError) as exc:
        b.load()
    assert API_KEY_ENV in str(exc.value)


def test_transcribe_before_load_is_an_error(tmp_path, monkeypatch, transport):
    """没 load 就 transcribe → ASRError。"""
    monkeypatch.setenv(API_KEY_ENV, "test-key")
    b = DashScopeBackend(transport=transport,
                         hotwords_file=tmp_path / "no_hotwords.txt")
    with pytest.raises(ASRError):
        b.transcribe(np.ones(1600, dtype=np.int16), 16000)


def test_unload_closes_the_transport(backend, transport):
    """卸载要真的把 HTTP 会话关掉。"""
    backend.unload()
    assert transport.closed is True
    assert backend.loaded is False


# ---------------- 重采样(必须没有混叠) ----------------

def test_resample_length_ratio_is_correct():
    """48k → 16k:长度必须是三分之一。"""
    audio = np.ones(48000, dtype=np.float32)
    out = resample_to(audio, 48000, 16000)
    assert len(out) == 16000


def test_resample_keeps_the_frequency():
    """重采样后频率不变 —— 别把 1 kHz 折回低频(混叠)。"""
    rate = 48000
    freq = 1000.0
    t = np.arange(rate, dtype=np.float32) / rate
    tone = np.sin(2 * np.pi * freq * t).astype(np.float32)

    out = resample_to(tone, rate, 16000)

    # 用过零率数频率:1 kHz 在 16 kHz 下每 16 个样本一个周期
    crossings = int(np.count_nonzero(np.diff(np.signbit(out))))
    measured = crossings / 2 / (len(out) / TARGET_RATE)
    assert measured == pytest.approx(freq, rel=0.02)


def test_resample_filters_out_content_above_output_nyquist():
    """超出目标奈奎斯特的成分必须被滤掉,不能折叠回低频。

    48 kHz 采到的 12 kHz,降到 16 kHz 时已经超过奈奎斯特(8 kHz)——
    没有抗混叠低通的话它会折到 4 kHz,叠在人声基频上
    (README「不要自己降采样」那条坑:实测失真能量占比 98.71%)。
    """
    rate = 48000
    freq = 12000.0
    t = np.arange(rate, dtype=np.float32) / rate
    tone = np.sin(2 * np.pi * freq * t).astype(np.float32)

    out = resample_to(tone, rate, 16000)

    in_rms = float(np.sqrt(np.mean(tone ** 2)))
    out_rms = float(np.sqrt(np.mean(out ** 2)))
    assert out_rms < in_rms * 0.1, "高频成分没被滤掉,折叠回低频了"


def test_linear_interpolation_would_fold_the_same_signal():
    """对照组:线性插值(np.interp)会把同一个信号折回 4 kHz。

    这条不是测我们的实现,是把 README 那条坑**钉在测试里**:
    将来谁想改成 ``np.interp``,这条会先红。
    """
    rate = 48000
    freq = 12000.0
    t = np.arange(rate, dtype=np.float32) / rate
    tone = np.sin(2 * np.pi * freq * t).astype(np.float32)

    n_out = 16000
    linear = np.interp(np.linspace(0, rate - 1, n_out),
                       np.arange(rate), tone).astype(np.float32)

    in_rms = float(np.sqrt(np.mean(tone ** 2)))
    lin_rms = float(np.sqrt(np.mean(linear ** 2)))
    # 线性插值几乎原样保留能量 —— 12 kHz 被折到了 4 kHz
    assert lin_rms > in_rms * 0.5

    # 而抗混叠的 resample_poly 把这段能量滤掉了
    filtered = resample_to(tone, rate, 16000)
    filt_rms = float(np.sqrt(np.mean(filtered ** 2)))
    assert filt_rms < lin_rms * 0.1


def test_resample_same_rate_is_identity():
    """采样率一样就不重采样。"""
    audio = np.linspace(-1, 1, 100, dtype=np.float32)
    assert resample_to(audio, 16000, 16000) is audio or \
        np.array_equal(resample_to(audio, 16000, 16000), audio)


def test_resample_rejects_bad_rate():
    """采样率不合法 → ASRError。"""
    with pytest.raises(ASRError):
        resample_to(np.ones(10, dtype=np.float32), 0, 16000)


# ---------------- WAV 编码 ----------------

def test_encode_wav_produces_16bit_pcm():
    """编码出来就是标准 WAV:单声道 / 16-bit / 指定采样率。"""
    audio = np.array([0.0, 0.5, -0.5, 1.0], dtype=np.float32)
    raw = encode_wav(audio, 16000)
    with wave.open(io.BytesIO(raw), "rb") as wf:
        assert wf.getnchannels() == 1
        assert wf.getsampwidth() == 2
        assert wf.getframerate() == 16000
        assert wf.getnframes() == 4


def test_encode_wav_clips_out_of_range():
    """超出 [-1, 1] 的样本要削到满刻度,不能回绕成噪声。"""
    raw = encode_wav(np.array([2.0, -2.0], dtype=np.float32), 16000)
    with wave.open(io.BytesIO(raw), "rb") as wf:
        frames = wf.readframes(2)
    values = np.frombuffer(frames, dtype=np.int16)
    assert values[0] == 32767
    assert values[1] == -32767


def test_encode_wav_keeps_amplitude():
    """幅度要按 32767 换算(0.5 → 16383 左右)。"""
    raw = encode_wav(np.array([0.5], dtype=np.float32), 16000)
    with wave.open(io.BytesIO(raw), "rb") as wf:
        value = int(np.frombuffer(wf.readframes(1), dtype=np.int16)[0])
    assert value == pytest.approx(16383, abs=2)


# ---------------- 真实 httpx 序列化路径(仍然不联网) ----------------

def test_httpx_mock_transport_round_trip(tmp_path, monkeypatch):
    """走**真实的 httpx** 请求/响应序列化,但一个包都不往外发。

    用 ``httpx.MockTransport``:httpx 会照常拼 URL、序列化 JSON、
    处理响应头 —— 只有最后一步被换成了本地回放。
    """
    httpx = pytest.importorskip("httpx")
    monkeypatch.setenv(API_KEY_ENV, "test-key")

    seen: dict = {}

    def handler(request):
        seen["url"] = str(request.url)
        seen["auth"] = request.headers.get("authorization")
        seen["body"] = request.content.decode("utf-8")
        return httpx.Response(200, json={
            "choices": [{"message": {"content": "来自 httpx 的结果"}}],
        })

    client = httpx.Client(transport=httpx.MockTransport(handler))
    from qwen3_voice.asr.network import HttpxTransport

    b = DashScopeBackend(transport=HttpxTransport(client=client),
                         hotwords_file=tmp_path / "no_hotwords.txt")
    b.load()
    text = b.transcribe(np.ones(16000, dtype=np.int16), 16000)

    assert text == "来自 httpx 的结果"
    assert seen["url"] == f"{DEFAULT_BASE_URL}/chat/completions"
    assert seen["auth"] == "Bearer test-key"
    assert DEFAULT_MODEL in seen["body"]


# ---------------- 解析函数本身 ----------------

def test_parse_transcription_handles_plain_string():
    resp = FakeResponse(200, {"choices": [{"message": {"content": "你好"}}]})
    assert parse_transcription(resp) == "你好"


def test_parse_transcription_strips_whitespace():
    resp = FakeResponse(200, {"choices": [{"message": {"content": "  你好  "}}]})
    assert parse_transcription(resp) == "你好"


def test_parse_transcription_rejects_bad_payload():
    resp = FakeResponse(200, {"nope": 1})
    with pytest.raises(ASRError):
        parse_transcription(resp)


def test_parse_transcription_rejects_garbage_shaped_choices():
    """choices 里塞的不是对象 → ASRError(别让 AttributeError 逃出去)。"""
    resp = FakeResponse(200, {"choices": ["乱写"]})
    with pytest.raises(ASRError):
        parse_transcription(resp)


# ---------------- 真机用例(默认跳过,需要显式开启) ----------------
#
# 这一条会**真的联网**、真的花配额,所以默认跳过。
# 想跑它:设置 QWEN3_VOICE_REAL_NETWORK=1 并且配好 DASHSCOPE_API_KEY,
# 再用 `-m pytest tests/test_network_engine.py -k real` 跑。
#
# 本次开发**没有**执行过它(没有 key、也不该替用户烧配额)。

@pytest.mark.skipif(
    os.environ.get("QWEN3_VOICE_REAL_NETWORK", "") != "1",
    reason="需要真实网络与 API Key:设 QWEN3_VOICE_REAL_NETWORK=1 再显式开启",
)
def test_real_dashscope_request(tmp_path):
    """真机:合成一段 440 Hz 的音频,发给真实端点,看能不能拿到文本。"""
    if not os.environ.get(API_KEY_ENV):
        pytest.skip(f"没有 {API_KEY_ENV}")
    rate = 48000
    t = np.arange(rate, dtype=np.float32) / rate
    tone = (np.sin(2 * np.pi * 440 * t) * 0.2 * 32767).astype(np.int16)

    b = DashScopeBackend(hotwords_file=tmp_path / "no_hotwords.txt")
    b.load()
    text = b.transcribe(tone, rate)
    assert isinstance(text, str)
    b.unload()
