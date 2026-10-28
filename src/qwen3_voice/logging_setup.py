"""日志。

同时输出到控制台和文件,文件路径来自配置。
"""

from __future__ import annotations

import logging
import sys
from pathlib import Path

_configured = False


def setup(log_file: str | Path, level: str = "INFO") -> logging.Logger:
    """初始化根 logger,重复调用只会生效一次。"""
    global _configured
    if _configured:
        return logging.getLogger("qwen3_voice")

    root = logging.getLogger("qwen3_voice")
    root.setLevel(getattr(logging, level.upper(), logging.INFO))
    root.propagate = False

    fmt = logging.Formatter(
        fmt="[%(asctime)s] %(levelname)s %(message)s",
        datefmt="%H:%M:%S",
    )

    stream = logging.StreamHandler(sys.stdout)
    stream.setFormatter(fmt)
    root.addHandler(stream)

    try:
        path = Path(log_file)
        path.parent.mkdir(parents=True, exist_ok=True)
        file_handler = logging.FileHandler(path, encoding="utf-8")
        file_handler.setFormatter(fmt)
        root.addHandler(file_handler)
    except OSError:
        pass  # 日志文件写不了不该拖垮程序

    _configured = True
    return root


def get_logger(name: str = "") -> logging.Logger:
    return logging.getLogger(f"qwen3_voice.{name}" if name else "qwen3_voice")
