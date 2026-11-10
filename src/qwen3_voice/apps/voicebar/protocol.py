
from __future__ import annotations

DISPATCH_DISPLAY_STATE = "display_state"

DISPATCH_LEVELS = "levels"

CMD_START_RECORDING = "start_recording"

CMD_STOP_RECORDING = "stop_recording"

CMD_CANCEL_RECORDING = "cancel_recording"

CMD_COPY_RESULT = "copy_result"

CMD_OPEN_PANEL = "open_panel"

CMD_LAUNCH_QR = "launch_qr"

CMD_QUIT = "quit"

MODE_ORB = "orb"

MODE_STARTING = "starting"

MODE_EXPANDING = "expanding"

MODE_RECORDING = "recording"

MODE_PROCESSING = "processing"

MODE_RESULT = "result_terminal"

MODE_ERROR = "error_terminal"

MODE_CANCELLED = "cancelled_terminal"

MODE_REJECTED = "rejected_bounce"

MODES = (
    MODE_ORB,
    MODE_STARTING,
    MODE_EXPANDING,
    MODE_RECORDING,
    MODE_PROCESSING,
    MODE_RESULT,
    MODE_ERROR,
    MODE_CANCELLED,
    MODE_REJECTED,
)

STANDBY_RESIDENT = "resident"

STANDBY_ON_DEMAND = "on_demand"

STANDBY_MODES = (STANDBY_RESIDENT, STANDBY_ON_DEMAND)
