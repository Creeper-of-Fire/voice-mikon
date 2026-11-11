"""MiniMax **网络后端**(asr-1.0,multipart 上传音频)。

    POST https://api.minimax.cn/v1/speech_to_text
    Authorization: Bearer $MINIMAX_API_KEY
    multipart/form-data:
        model=asr-1.0
        file=<WAV bytes>
        response_format=json
        stream=false

几个刻意的选择:

- **API Key 从环境变量 ``MINIMAX_API_KEY`` 或 ``data/.env`` 文件读**(前者
  临时覆盖,后者持久存储),绝不进 config.json
  —— 配置文件会被用户手工编辑、截图、分享。缺 key 就明确报错,
  **不静默回落到本地**(那会让用户以为在用网络)。
- **key 绝不进日志**:需要提及时只报长度(够判断"配没配上"),
  打码串本身也还是进了日志文件 —— 干脆别提。
- **串行锁**:每后端实例一把锁,所有出网请求排队。MiniMax 的并发
  配额是 CONN=2,而预览位和最终位共用同一颗后端实例,一次只放一个
  请求出去 —— 天然落在配额之内,也不会让"分句预览"的请求和"最终整段"
  的请求互相抢。
- **429 指数退避重试 2 次**(0.5s / 1.5s):限流是暂时的,直接报错
  会让用户白白重说一遍。
- **热词不注入**:MiniMax 没有 context biasing 入口 —— 见
  ``MiniMaxBackend._build_context``。切换成功的通知里会**明说**这件事,
  不静默装支持。
- **重采样用 ``scipy.signal.resample_poly``**(多相滤波,带抗混叠低通),
  理由与本地引擎一致,见 ``base.resample_to``。
"""

from __future__ import annotations

import os
import threading
import time
from pathlib import Path

import numpy as np

from ..logging_setup import get_logger
from ..paths import data_dir
from ._http import DEFAULT_TIMEOUT, HttpResponse, HttpTransport, HttpxTransport
from .base import ASRBackend, ASRError, encode_wav, resample_to

log = get_logger("asr.net")

# API Key 的环境变量名(临时覆盖/测试用;日常持久存储在 data/.env)
API_KEY_ENV = "MINIMAX_API_KEY"

# key 的持久存储文件(相对项目 data/ 目录;已被 .gitignore 的 *.env 规则忽略)。
# 文件格式:每行 KEY=VALUE,# 开头是注释。放文件而不是注册表环境变量:
# 暴露面小(不会被任意进程的用户环境块读到),且跟着项目目录走(portable)。
ENV_FILE_NAME = ".env"


def _load_api_key() -> str:
    """取 MiniMax 的 API Key:进程环境变量优先,data/.env 文件兜底。

    优先级是有意的:环境变量是**临时通道**(测试换 key、CI 注入),
    data/.env 才是**持久真相** —— 用户不用往系统里写任何东西。
    手写的极简解析(键=值、# 注释、容忍引号),不引 python-dotenv。
    """
    key = os.environ.get(API_KEY_ENV, "").strip()
    if key:
        return key
    try:
        env_file = data_dir() / ENV_FILE_NAME
        if env_file.is_file():
            for line in env_file.read_text(encoding="utf-8").splitlines():
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                if line.startswith(f"{API_KEY_ENV}="):
                    value = line[len(API_KEY_ENV) + 1:].strip()
                    return value.strip('"').strip("'")
    except OSError as e:
        log.debug("读取 %s 失败: %s", ENV_FILE_NAME, e)
    return ""

# 端点前缀:国内站是 api.minimax.cn,海外站改成 https://api.minimax.io
DEFAULT_BASE_URL = "https://api.minimax.cn"

# 默认模型(官方目前只对外提供 asr-1.0)
DEFAULT_MODEL = "asr-1.0"

# 网络后端要求(也是最通用的组合):单声道 / 16 kHz / 16-bit PCM WAV
TARGET_RATE = 16000

# 官方限制:单次 ≤500 秒 / ≤50 MB。正常语音输入远达不到,
# 但上限是硬的 —— 本地先说清楚是哪一条超了,比服务端回一句 400 好懂。
MAX_DURATION_SECONDS = 500.0
MAX_BYTES = 50 * 1024 * 1024

# 429 限流的退避节奏(秒):第 1 次等 0.5s,第 2 次等 1.5s
RETRY_DELAYS = (0.5, 1.5)


class MiniMaxBackend(ASRBackend):
    """MiniMax 网络识别后端(asr-1.0)。

    和本地后端可互换 —— 控制器只认 ``ASRBackend`` 那几个方法。
    预览位和最终位共用同一颗实例(整段 / 分句都走这里),
    串行锁保证两次出网请求不会撞车。
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
        # 串行锁:一把锁管住这个实例的全部出网请求(见模块 docstring)
        self._lock = threading.Lock()

    # ---------- 生命周期 ----------

    @property
    def loaded(self) -> bool:
        return self._ready

    def load(self) -> float:
        """检查 API Key、建好会话,返回耗时秒数。

        **这一步就会失败**:没配 key 就直接报错,不让用户以为网络后端能用。
        """
        key = self._api_key or _load_api_key()
        key = (key or "").strip()
        if not key:
            raise ASRError(
                f"未配置 {API_KEY_ENV} —— MiniMax 后端不可用。"
                f"把 key 写进 data/{ENV_FILE_NAME}(MINIMAX_API_KEY=...)"
                f"或设置同名环境变量后重试(本地后端不受影响)")
        self._api_key = key

        t0 = time.time()
        # 建 HTTP 会话(不发请求)。HttpxTransport 自己会延迟导入 httpx。
        ensure = getattr(self._transport, "ensure_client", None)
        if callable(ensure):
            ensure()
        self._ready = True
        elapsed = time.time() - t0
        # **只报 key 的长度** —— key 本身绝不能进日志。
        log.info("MiniMax 后端就绪: %s (%s, key 长度 %d)",
                 self.model, self.base_url, len(self._api_key))
        return elapsed

    def unload(self) -> None:
        """关掉 HTTP 会话(切回本地时走这条路)。"""
        self._ready = False
        close = getattr(self._transport, "close", None)
        if callable(close):
            try:
                close()
            except Exception as e:
                log.debug("关闭 MiniMax 后端失败: %s", e)

    # ---------- 热词 ----------

    def _build_context(self) -> str:
        """MiniMax **没有** context biasing 通道 —— 热词一律不注入。

        覆写成空串而不是"忘了调用":这一路哪天不小心调了
        ``_build_context``,拿到的也是空串,不会把词表悄悄塞进请求。
        用户能不能用热词,在切换成功的通知里**明说**,不静默装支持。
        """
        return ""

    # ---------- 识别 ----------

    def transcribe(self, audio: np.ndarray, sample_rate: int = 16000) -> str:
        """把音频发给 MiniMax,返回识别文本。

        同一段音频既可能来自最终位(整段),也可能来自分句预览(一句一段)
        —— 这个方法对两者一视同仁,消费者只认同一个契约。
        """
        if len(audio) == 0:
            return ""
        if not self._ready:
            raise ASRError("MiniMax 后端尚未加载")

        src_rate = int(sample_rate or TARGET_RATE)
        wav = self._to_float32(audio)
        wav16 = resample_to(wav, src_rate, TARGET_RATE)
        payload = encode_wav(wav16, TARGET_RATE)
        self._check_limits(len(audio) / src_rate, len(payload))

        url = f"{self.base_url}/v1/speech_to_text"
        headers = {"Authorization": f"Bearer {self._api_key}"}
        data = {
            "model": self.model,
            "response_format": "json",
            "stream": "false",
        }
        # 文件名给个正经的:有的服务端会拿扩展名判格式
        files = {"file": ("audio.wav", payload, "audio/wav")}

        t0 = time.time()
        response = self._post(url, headers=headers, data=data, files=files)
        text = self._parse_text(response)
        dur = len(audio) / src_rate
        log.info("MiniMax 识别完成 %.1fs 音频 / %.1fs 耗时: %s",
                 dur, time.time() - t0, text)
        return text

    def _check_limits(self, duration: float, size: int) -> None:
        """对照官方的单次上限(≤500 秒 / ≤50 MB)。"""
        if duration > MAX_DURATION_SECONDS:
            raise ASRError(
                f"音频太长({duration:.0f}s):MiniMax 单次上限 "
                f"{MAX_DURATION_SECONDS:.0f}s")
        if size > MAX_BYTES:
            raise ASRError(
                f"音频太大({size / 1024 / 1024:.1f} MB):MiniMax 单次上限 "
                f"{MAX_BYTES // 1024 // 1024} MB")

    # ---------- HTTP ----------

    def _post(self, url: str, *, headers: dict[str, str],
              data: dict[str, str],
              files: dict[str, tuple[str, bytes, str]]) -> HttpResponse:
        """发请求(含 429 退避重试)。

        整段(含重试)都在串行锁里 —— 重试等待期间别的请求不许插队。
        """
        with self._lock:
            attempt = 0
            while True:
                response = self._request(url, headers=headers, data=data,
                                         files=files)
                status = int(getattr(response, "status_code", 0) or 0)
                if status != 429 or attempt >= len(RETRY_DELAYS):
                    return response
                delay = RETRY_DELAYS[attempt]
                log.warning("MiniMax 限流(HTTP 429),%.1fs 后重试(%d/%d)",
                            delay, attempt + 1, len(RETRY_DELAYS))
                time.sleep(delay)
                attempt += 1

    def _request(self, url: str, *, headers: dict[str, str],
                 data: dict[str, str],
                 files: dict[str, tuple[str, bytes, str]]) -> HttpResponse:
        """发一次请求,把传输层的异常统一翻译成 ``ASRError``。"""
        try:
            return self._transport.post_multipart(
                url, headers=headers, data=data, files=files,
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

    # ---------- 响应解析 ----------

    def _parse_text(self, response: HttpResponse) -> str:
        """从响应里取出识别文本。

        成功:``{"text": str, "duration": float, "trace_id": str}``
        失败:``{"type":"error","error":{"type","message","http_code"}}``
        """
        status = int(getattr(response, "status_code", 0) or 0)
        if status != 200:
            raise self._error_from(response, status)
        try:
            data = response.json()
        except Exception as e:
            raise ASRError(f"MiniMax 响应不是合法 JSON:{e}") from e
        if not isinstance(data, dict):
            raise ASRError("MiniMax 响应格式不对(不是 JSON 对象)")
        text = data.get("text")
        if not isinstance(text, str):
            raise ASRError(f"MiniMax 响应里没有识别文本:{str(data)[:200]}")
        return text.strip()

    def _error_from(self, response: HttpResponse, status: int) -> ASRError:
        """把错误响应翻译成 ``ASRError``(带 http_code 与 message)。

        官方错误信封:
        ``{"type":"error","error":{"type","message","http_code"}}``
        —— HTTP 状态码就是错误码(401 鉴权失败 / 429 限流 / 413 超大 …)。
        """
        etype = ""
        detail = ""
        http_code = status
        try:
            data = response.json()
        except Exception:
            data = None
        if isinstance(data, dict):
            err = data.get("error")
            if isinstance(err, dict):
                etype = str(err.get("type") or "")
                detail = str(err.get("message") or "")
                try:
                    http_code = int(err.get("http_code") or status)
                except (TypeError, ValueError):
                    http_code = status
        if not detail:
            detail = (getattr(response, "text", "") or "")[:200]
        tag = f"http_code={http_code}"
        if etype:
            tag += f" type={etype}"
        if status == 429:
            # 重试过了还是 429 —— 真的被限流了,得说清楚是哪一种
            return ASRError(f"MiniMax 限流({tag}):{detail}")
        return ASRError(f"MiniMax 识别失败({tag}):{detail}")
