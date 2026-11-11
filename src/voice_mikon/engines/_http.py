"""可注入的 HTTP 传输层。

抽出这一层的唯一理由:**让后端逻辑能脱离网络被测** —— 测试塞一个
假传输层进来,断言请求体长什么样、响应怎么解析、错误怎么报,
全程不联网(也不烧用户配额)。

httpx 是**延迟导入**的:环境里没装 httpx,模块照样能 import
(本地后端不受影响),只有真要发网络请求时才会报错。
"""

from __future__ import annotations

from typing import Any, Protocol

from ..logging_setup import get_logger
from .base import ASRError

log = get_logger("asr.net")

# 单次请求超时(秒)的默认值。给得比最长的音频还长一点,
# 免得慢网络被误杀。
DEFAULT_TIMEOUT = 60.0


class HttpResponse(Protocol):
    """传输层要返回的最小响应接口。"""

    status_code: int
    text: str

    def json(self) -> Any:
        """解析成 dict。"""


class HttpTransport(Protocol):
    """可注入的 HTTP 传输层。"""

    def post_multipart(self, url: str, *, headers: dict[str, str],
                       data: dict[str, str],
                       files: dict[str, tuple[str, bytes, str]],
                       timeout: float) -> HttpResponse:
        """发一个 multipart/form-data 的 POST 请求。"""

    def close(self) -> None:
        """关掉底层会话(可重复调用)。"""


class HttpxTransport:
    """默认传输层:用 httpx 发请求。

    ``client`` 可以塞一个自己的 ``httpx.Client`` —— 测试可以用
    ``httpx.MockTransport`` 走**真实的 httpx 请求/响应序列化**,
    但一个包都不往外发。
    """

    def __init__(self, client: Any | None = None):
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

    def post_multipart(self, url: str, *, headers: dict[str, str],
                       data: dict[str, str],
                       files: dict[str, tuple[str, bytes, str]],
                       timeout: float) -> HttpResponse:
        client = self.ensure_client()
        # **不手设 Content-Type**:multipart 的 boundary 得由 httpx 生成 ——
        # 手写一个 "multipart/form-data" 会让服务端读不出各个 part。
        return client.post(url, headers=headers, data=data, files=files,
                           timeout=timeout)

    def close(self) -> None:
        if self._client is not None and self._owns_client:
            try:
                self._client.close()
            except Exception as e:
                log.debug("关闭 HTTP 会话失败: %s", e)
        self._client = None
