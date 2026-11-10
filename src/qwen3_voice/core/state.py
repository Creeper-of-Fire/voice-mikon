
from __future__ import annotations

from dataclasses import dataclass, replace

STATE_STARTING = "starting"
STATE_IDLE = "idle"
STATE_RECORDING = "recording"
STATE_PROCESSING = "processing"
STATES = (STATE_STARTING, STATE_IDLE, STATE_RECORDING, STATE_PROCESSING)

HEALTH_LOADING = "loading"
HEALTH_READY = "ready"
HEALTH_FAILED = "failed"
HEALTH_STATUSES = (HEALTH_LOADING, HEALTH_READY, HEALTH_FAILED)

@dataclass(frozen=True)
class BackendHealth:

    status: str = HEALTH_LOADING
    reason: str = ""

    @property
    def ready(self) -> bool:
        return self.status == HEALTH_READY

    @property
    def failed(self) -> bool:
        return self.status == HEALTH_FAILED

    @property
    def loading(self) -> bool:
        return self.status == HEALTH_LOADING

@dataclass(frozen=True)
class AppState:

    state: str = STATE_STARTING
    backend: str = "local"
    backend_health: BackendHealth = BackendHealth()
    model: str = ""
    preview_supported: bool = True
    preview_text: str = ""

    def with_changes(self, **changes) -> "AppState":
        return replace(self, **changes)

@dataclass(frozen=True)
class CommandResult:

    ok: bool
    reason: str = ""
    message: str = ""
    text: str = ""

    @property
    def rejected(self) -> bool:
        return not self.ok

    def __bool__(self) -> bool:
        return self.ok
