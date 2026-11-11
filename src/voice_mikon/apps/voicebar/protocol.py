
from __future__ import annotations

DISPATCH_DISPLAY_STATE = "display_state"

DISPATCH_LEVELS = "levels"

CMD_START_RECORDING = "start_recording"

CMD_STOP_RECORDING = "stop_recording"

CMD_CANCEL_RECORDING = "cancel_recording"

CMD_COPY_RESULT = "copy_result"

CMD_OPEN_PANEL = "open_panel"

CMD_LAUNCH_QR = "launch_qr_scanner"

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

# 带原文的错误态(注入失败)的**标记**。
#
# 为什么单独标一个:MODE_ERROR 只说明"现在是错误态",区分不了
# "错误带原文(常驻,可无限次点复制重试)"和"错误没原文(自动收回)"
# —— 而这两种的**何时收回**正好相反,得让 dwell 表能分开查。
MODE_ERROR_WITH_TEXT = "error_terminal+has-text"

# dwell 转移表:**整个项目只有这一份**。
#
# "何时发生"归 presenter:终态停多久、到点回哪个状态,都查这张表;
# 前端不再有任何收回定时器("如何呈现"归前端,那是动画的事)。
# key = 终态,value = 停留秒数;到点回 orb(常驻模式则收成球)。
DWELL_SECONDS: dict[str, float | None] = {
    MODE_RESULT: 1.5,
    # **无原文**的错误:错误文案要让人看清,停久一点
    MODE_ERROR: 4.5,
    MODE_CANCELLED: 1.0,
    MODE_REJECTED: 0.6,
    # 带原文的错误态:**无自动转移**(值 None = 不排)。
    #
    # 用户可能要反复点「复制」重试(剪贴板被占、目标窗口不给焦点),
    # 自动收回会把重试的口子堵上。只有点 ✕(dismiss)或开始下一轮
    # 录音才收(行为基准见 38b105a)。
    MODE_ERROR_WITH_TEXT: None,
}

# set_mode(mode, payload) 的 payload 字段 —— **按 mode 取用**,
# 用不到的字段一律给空值(前端不按 mode 猜):
#   message  提示文案:错误原因 / 识别中 / 已输入 / 已取消 / 被拒的原因
#   text     识别正文:结果态是识别出来的文字;带原文的错误态是
#            **用户还能复制回来的那段原文**(注入失败时没送出去)
#   can_copy 有没有可复制的原文(bool):带原文的错误态为 true。
#            这类错误**不自动收回**,只有 dismiss 或新一轮录音才收
#   reason   机器可读的原因码(暂不参与渲染,留着以后排查)
