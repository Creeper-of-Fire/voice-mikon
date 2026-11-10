"""阿里云百炼 DashScope **网络后端**(OpenAI 兼容 HTTP 接口)。

走的是**兼容模式**的 chat/completions 端点:

    POST https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions
    Authorization: Bearer $DASHSCOPE_API_KEY
    {
      "model": "qwen3-asr-flash",
      "messages": [
        {"role": "system", "content": "Vocabulary: 词1, 词2."},     # 热词
        {"role": "user", "content": [
          {"type": "input_audio",
           "input_audio": {"data": "data:audio/wav;base64,<BASE64>"}}
        ]}
      ],
      "stream": false
    }

几个刻意的选择:

- **不引 dashscope SDK**:那套带 websocket/回调,复杂度不值当;
  兼容端点够用,而且 httpx 本来就装在环境里。
- **API Key 只从环境变量 ``DASHSCOPE_API_KEY`` 读**,绝不进 config.json
  —— 配置文件会被用户手工编辑、截图、分享。缺 key 就明确报错,
  **不静默回落到本地**(那会让用户以为在用网络)。
- **热词走 system prompt**,格式和本地引擎的 Context Biasing 完全一致
  (``"Vocabulary: a, b, c."``),两边效果可比。
- **重采样用 ``scipy.signal.resample_poly``**(多相滤波,带抗混叠低通)。
  README 里那条坑说得很清楚:``np.interp`` 线性插值没有抗混叠滤波,
  8~24 kHz 的成分会折叠回低频,叠在人声基频上。
- **HTTP 传输层可注入**:测试用假实现断言请求体/响应/错误路径,
  **全程不联网**(也不烧用户配额)。
"""

from __future__ import annotations

import base64
import io
import math
import os
import time
import wave
from pathlib import Path
from typing import Any, Protocol

import numpy as np

from ..logging_setup import get_logger
from .base import ASRBackend, ASRError

log = get_logger("asr.net")

# API Key 的环境变量名(唯一的来源,不进配置文件)
API_KEY_ENV = "DASHSCOPE_API_KEY"

# 兼容端点的默认前缀(北京地域;新加坡地域是 dashscope-intl.aliyuncs.com)
DEFAULT_BASE_URL = "https://dashscope.aliyuncs.com/compatible-mode/v1"

# 默认模型
DEFAULT_MODEL = "qwen3-asr-flash"

# 网络后端要求(也是最通用的组合):单声道 / 16 kHz / 16-bit PCM WAV
TARGET_RATE = 16000

# 单次请求超时(秒)。音频最长 120s,给得比音频长一点,免得慢网络被误杀。
DEFAULT_TIMEOUT = 60.0


# ---------------- HTTP 传输层(可注入) ----------------

class HttpResponse(Protocol):
    """传输层要返回的最小响应接口。"""

    status_code: int
    text: str

    def json(self) -> Any:
        """解析成 dict。"""


class HttpTransport(Protocol):
    """可注入的 HTTP 传输层。

    抽出来是为了**测试不联网**:测试塞一个假实现进来,
    断言请求体长什么样、响应怎么解析、错误怎么报。
    """

    def post(self, url: str, *, headers: dict[str, str], body: dict[str, Any],
             timeout: float) -> HttpResponse:
        """发一个 POST 请求(JSON body)。"""


class HttpxTransport:
    """默认传输层:用 httpx 发请求。

    httpx 是**延迟导入**的 —— 万一环境里没装 httpx,
    整个模块照样能 import(本地后端不受影响),
    只有真要用网络后端时才会报错。
    """

    def __init__(self, client: Any | None = None):
        """
        ``client`` 可以塞一个自己的 ``httpx.Client`` ——
        测试可以用 ``httpx.MockTransport`` 走**真实的 httpx 请求/响应
        序列化**,但一个包都不往外发。
        """
        self._client = client
        self._owns_client = client is None

    def ensure_client(self) -> Any:
        """建好(或取回)HTTP 客户端。"""
        if self._client is None:
            try:
                import httpx
            except ImportError as e:      # pragma: no cover - 环境问题
                raise ASRError(
                    "网络后端需要 httpx(未安装):pip install httpx") from e
            self._client = httpx.Client(timeout=DEFAULT_TIMEOUT)
        return self._client

    def post(self, url: str, *, headers: dict[str, str], body: dict[str, Any],
             timeout: float) -> HttpResponse:
        client = self.ensure_client()
        return client.post(url, headers=headers, json=body, timeout=timeout)

    def close(self) -> None:
        if self._client is not None and self._owns_client:
            try:
                self._client.close()
            except Exception as e:
                log.debug("关闭 HTTP 会话失败: %s", e)
        self._client = None


# ---------------- 后端 ----------------

class DashScopeBackend(ASRBackend):
    """网络识别后端(阿里云百炼 DashScope)。

    和本地后端可互换 —— 控制器只认 ``ASRBackend`` 那几个方法。
    """

    def __init__(self, model: str = DEFAULT_MODEL,
                 base_url: str = DEFAULT_BASE_URL,
                 hotwords: list[str] | None = None,
                 hotwords_file: Path | None = None,
                 api_key: str | None = None,
                 transport: HttpTransport | None = None,
                 timeout: float = DEFAULT_TIMEOUT):
        super().__init__(hotwords=hotwords, hotwords_file=hotwords_file)
        self.model = model or DEFAULT_MODEL
        self.base_url = (base_url or DEFAULT_BASE_URL).rstrip("/")
        self.timeout = float(timeout)
        # api_key 显式传进来优先;否则在 load() 时读环境变量
        self._api_key = api_key
        self._transport: HttpTransport = transport or HttpxTransport()
        self._ready = False

    # ---------- 生命周期 ----------

    @property
    def loaded(self) -> bool:
        return self._ready

    @property
    def model_id(self) -> str:
        return self.model

    @property
    def supports_preview(self) -> bool:
        """网络后端**不支持**实时预览。

        兼容端点是整段请求,没有"边说边出字"的能力。
        硬做(定期重发全量音频)只会烧配额、延迟越来越大 —— 不做。
        """
        return False

    def load(self) -> float:
        """检查 API Key、建好会话,返回耗时秒数。

        **这一步就会失败**:没配 key 就直接报错,不让用户以为网络后端能用。
        """
        key = self._api_key or os.environ.get(API_KEY_ENV, "")
        key = (key or "").strip()
        if not key:
            raise ASRError(
                f"未配置 {API_KEY_ENV} 环境变量 —— 网络后端不可用。"
                f"设置该环境变量后重试(本地后端不受影响)")
        self._api_key = key

        t0 = time.time()
        # 建 HTTP 会话(不发请求)。HttpxTransport 自己会延迟导入 httpx。
        ensure = getattr(self._transport, "ensure_client", None)
        if callable(ensure):
            ensure()
        self._ready = True
        elapsed = time.time() - t0
        log.info("网络后端就绪: %s (%s)", self.model, self.base_url)
        return elapsed

    def unload(self) -> None:
        """关掉 HTTP 会话(切回本地时走这条路)。"""
        self._ready = False
        close = getattr(self._transport, "close", None)
        if callable(close):
            try:
                close()
            except Exception as e:
                log.debug("关闭网络后端失败: %s", e)

    # ---------- 识别 ----------

    def transcribe(self, audio: np.ndarray, sample_rate: int = 16000) -> str:
        """把音频整段发给网络接口,返回识别文本。"""
        if len(audio) == 0:
            return ""
        if not self._ready:
            raise ASRError("网络后端尚未加载")

        wav = self._to_float32(audio)
        wav16 = resample_to(wav, int(sample_rate or TARGET_RATE), TARGET_RATE)
        payload = encode_wav(wav16, TARGET_RATE)
        data_uri = "data:audio/wav;base64," + base64.b64encode(payload).decode("ascii")

        body = self._build_body(data_uri)
        url = f"{self.base_url}/chat/completions"
        headers = {
            "Authorization": f"Bearer {self._api_key}",
            "Content-Type": "application/json",
        }

        t0 = time.time()
        response = self._post(url, headers=headers, body=body)
        text = parse_transcription(response)
        dur = len(audio) / max(1, int(sample_rate))
        log.info("网络识别完成 %.1fs 音频 / %.1fs 耗时: %s",
                 dur, time.time() - t0, text)
        return text

    def _build_body(self, data_uri: str) -> dict[str, Any]:
        """拼请求体。

        热词放在 system role 里,格式和本地引擎的 Context Biasing 一致:
        ``"Vocabulary: a, b, c."`` —— 本地是交给 qwen_asr 的 context 参数,
        网络是 system prompt,**两边格式一样**,效果可比。
        """
        messages: list[dict[str, Any]] = []
        context = self._build_context()
        if context:
            messages.append({"role": "system", "content": context})
        messages.append({
            "role": "user",
            "content": [
                {"type": "input_audio",
                 "input_audio": {"data": data_uri}},
            ],
        })
        return {
            "model": self.model,
            "messages": messages,
            "stream": False,
        }

    def _post(self, url: str, *, headers: dict[str, str],
              body: dict[str, Any]) -> HttpResponse:
        """发请求,把传输层的异常统一翻译成 ``ASRError``。"""
        try:
            response = self._transport.post(url, headers=headers, body=body,
                                            timeout=self.timeout)
        except TimeoutError as e:
            raise ASRError(f"网络请求超时({self.timeout:.0f}s):{e}") from e
        except ASRError:
            raise
        except Exception as e:
            # httpx 的超时是 httpx.TimeoutException(不是内置 TimeoutError),
            # 这里按名字兜住,免得超时被报成"未知网络错误"。
            if "Timeout" in type(e).__name__:
                raise ASRError(f"网络请求超时({self.timeout:.0f}s):{e}") from e
            raise ASRError(f"网络请求失败:{e}") from e

        status = int(getattr(response, "status_code", 0) or 0)
        if status != 200:
            detail = (getattr(response, "text", "") or "")[:200]
            raise ASRError(f"网络识别失败(HTTP {status}):{detail}")
        return response


# ---------------- 音频处理(独立函数,便于单测) ----------------

def resample_to(audio: np.ndarray, src_rate: int, dst_rate: int) -> np.ndarray:
    """重采样到目标采样率。

    用 ``scipy.signal.resample_poly``(多相滤波,**带抗混叠低通**)——
    README 里那条坑明说不要用 ``np.interp``:线性插值没有抗混叠滤波,
    8~24 kHz 的成分会折叠回低频,叠在人声基频上(实测失真能量占比 98.71%)。

    ``scipy`` 是延迟导入的:环境里没装 scipy 时,只有真要重采样才报错,
    模块本身照常能 import(本地后端不受影响)。
    """
    src_rate = int(src_rate)
    dst_rate = int(dst_rate)
    if src_rate <= 0 or dst_rate <= 0:
        raise ASRError(f"采样率不合法: {src_rate} → {dst_rate}")
    if src_rate == dst_rate:
        return np.asarray(audio, dtype=np.float32)

    try:
        from scipy.signal import resample_poly
    except ImportError as e:      # pragma: no cover - 环境问题
        raise ASRError("网络后端需要 scipy 做重采样(未安装)") from e

    g = math.gcd(src_rate, dst_rate)
    up = dst_rate // g
    down = src_rate // g
    data = np.asarray(audio, dtype=np.float32)
    return resample_poly(data, up, down).astype(np.float32)


def encode_wav(audio: np.ndarray, sample_rate: int) -> bytes:
    """把浮点音频编码成 WAV 字节流(单声道 / 16-bit PCM)。

    用**标准库** ``wave`` + numpy int16 —— 不引 soundfile
    (那玩意本机没装,而且没必要)。
    """
    pcm = np.clip(np.asarray(audio, dtype=np.float32), -1.0, 1.0)
    pcm = (pcm * 32767.0).astype(np.int16)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(int(sample_rate))
        wf.writeframes(pcm.tobytes())
    return buf.getvalue()


def parse_transcription(response: HttpResponse) -> str:
    """从响应里取出识别文本。

    OpenAI 兼容格式:``choices[0].message.content``。
    content 可能是字符串,也可能是 ``[{"type": "text", "text": "..."}]``
    这种分段列表 —— 两种都认。
    """
    try:
        data = response.json()
    except Exception as e:
        raise ASRError(f"网络响应不是合法 JSON:{e}") from e
    if not isinstance(data, dict):
        raise ASRError("网络响应格式不对(不是 JSON 对象)")

    choices = data.get("choices") or []
    if not choices or not isinstance(choices, list) or \
            not isinstance(choices[0], dict):
        raise ASRError(f"网络响应里没有识别结果:{str(data)[:200]}")

    message = choices[0].get("message")
    if not isinstance(message, dict):
        message = {}
    content = message.get("content")

    if isinstance(content, str):
        return content.strip()
    if isinstance(content, list):
        parts = []
        for part in content:
            if isinstance(part, dict):
                parts.append(str(part.get("text") or ""))
        return "".join(parts).strip()

    # 兜底:某些部署会把结果放在 output.text
    output = data.get("output")
    if isinstance(output, dict) and isinstance(output.get("text"), str):
        return output["text"].strip()

    raise ASRError(f"网络响应里没有识别文本:{str(data)[:200]}")
