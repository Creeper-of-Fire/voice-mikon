
from __future__ import annotations

import json
import logging
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from .logging_setup import get_logger
from .paths import data_dir

log = get_logger("notify")

LEVELS = ("error", "warning", "info")

_LEVEL_TO_LOG = {
    "error": logging.ERROR,
    "warning": logging.WARNING,
    "info": logging.INFO,
}

@dataclass(frozen=True)
class Notification:

    level: str
    kind: str
    message: str
    fields: dict[str, Any] = field(default_factory=dict)
    timestamp: float = 0.0

Subscriber = Callable[[Notification], None]

class NotificationHub:

    def __init__(self, log_file: str | Path | None = None) -> None:

        self._lock = threading.RLock()
        self._subscribers: list[Subscriber] = []
        self._log_file = Path(log_file) if log_file is not None else data_dir() / "notify.log"

    def subscribe(self, callback: Subscriber) -> None:
        if not callable(callback):
            raise TypeError(f"订阅回调必须可调用,实际 {_type_name(callback)}")
        with self._lock:
            self._subscribers.append(callback)

    @property
    def subscriber_count(self) -> int:
        with self._lock:
            return len(self._subscribers)

    def publish(self, level: str, kind: str, message: str, **fields: Any) -> None:
        if level not in LEVELS:
            raise ValueError(
                f"通知 level 非法:期望 {' / '.join(LEVELS)} 之一,实际 {level!r}")
        if not isinstance(kind, str) or not kind.strip():
            raise ValueError(f"通知 kind 非法:期望非空字符串,实际 {kind!r}")
        if not isinstance(message, str):
            raise ValueError(
                f"通知 message 非法:期望 str,实际 {_type_name(message)}")

        note = Notification(
            level=level,
            kind=kind,
            message=message,
            fields=dict(fields),
            timestamp=time.time(),
        )

        self._write_log(note)

        with self._lock:
            targets = list(self._subscribers)

        for cb in targets:
            try:
                cb(note)
            except Exception:

                log.exception("通知订阅者回调异常(kind=%s)", note.kind)

    def _write_log(self, note: Notification) -> None:
        stamp = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(note.timestamp))
        line = f"{stamp} | {note.level} | {note.kind} | {note.message}"
        if note.fields:
            line += " | " + json.dumps(note.fields, ensure_ascii=False, default=str)

        try:
            self._log_file.parent.mkdir(parents=True, exist_ok=True)
            with self._log_file.open("a", encoding="utf-8") as f:
                f.write(line + "\n")
        except OSError as e:
            log.warning("通知日志 %s 写入失败: %s", self._log_file, e)

        log.log(_LEVEL_TO_LOG.get(note.level, logging.INFO),
                "notify %s: %s", note.kind, note.message)

def _type_name(value: Any) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "bool"
    if isinstance(value, int):
        return "int"
    if isinstance(value, float):
        return "float"
    if isinstance(value, str):
        return "str"
    if isinstance(value, list):
        return "list"
    if isinstance(value, dict):
        return "object"
    return type(value).__name__

notify = NotificationHub()
