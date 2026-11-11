"""文本注入:把识别结果送进当前光标位置。

策略:剪贴板 + 模拟 Ctrl+V。
  - 不依赖目标应用的输入法状态,中文绝不出错
  - 一次粘贴,不存在逐字输入的丢字/乱序问题

关于"占用剪贴板"的处理(借鉴 OpenType 的做法):
  剪贴板只有一份,借来用完必须还回去。但**无脑还回去会吃掉用户刚复制
  的新内容** —— 用户在这半秒里复制了别的东西,我们的恢复就把它盖掉了,
  那才是真的丢数据。

  所以恢复前先做归属判定:
    1. 记录写入之后的剪贴板序号
    2. 恢复前比对序号,以及当前内容是不是我们写的那份
    3. 对不上 = 用户动过 -> **放弃恢复**,用户的新内容优先

  这样最坏情况是"用户原内容没还回去",而不是"用户新内容被吞掉"。
  真正的解法是缩短占用时间 —— 这里的占用窗口比 OpenType 的 1.2s 短。

**没送达就不还**(本项目自己的规矩):
OpenType 能放心用完就还,是因为它有 UI Automation 可以读回控件内容
校验,知道有没有部分成功。我们没有这个校验能力,所以只认"事件确实
送出去了"这一个信号:
    - 送出去了 -> 文字已经在目标窗口里,归还原内容,一切照旧
    - 没送出去 -> **不还**,把识别结果留在剪贴板里
      (目标窗口没收到、剪贴板又被抹掉 = 刚说出口的话彻底消失,
       那是最不能接受的结果)
代价是失败时用户原来那份剪贴板内容被顶掉了 —— 宁可丢一份旧的,
也不能丢刚说出口的。胶囊上还有「复制」按钮可以手动取走。

为什么不用 `SendInput` + `KEYEVENTF_UNICODE` 逐字打字:
  那种方式不碰剪贴板,看着更"干净",但 OpenType 只在**读不到目标控件**
  时才用它(游戏、终端)。原因有二:
    1. 打字路径下 `\\r` `\\n` `\\t` 会被 Windows 当成真实按键 ——
       回车在聊天软件里就是"发送消息",太危险
    2. 部分应用(记事本、WordPad)接受 Unicode 键事件但丢弃字形,
       表现为"光标在动但没字"
  OpenType 能放心用,是因为它有 UI Automation 可以**读回控件内容校验**,
  知道有没有部分成功(部分成功时它不重贴,避免重复文本)。
  我们没有这个校验能力,盲目走打字路径,一旦部分失败就无法判断
  该重贴还是不该 —— 所以保持剪贴板为主路径。
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass

from ..logging_setup import get_logger
from ..win32 import clipboard
from ..win32.api import (get_foreground_window, held_modifiers, restore_foreground,
                         send_ctrl_v, send_v, get_window_title)

log = get_logger("core.inject")

# 粘贴后等待目标应用消费剪贴板的时间。
# 太短 -> 目标还没读就被恢复,粘贴出来的是旧内容(实测踩到过);
# 太长 -> 剪贴板被我们占着的时间变长,用户中途复制东西的概率上升。
# OpenType 那边用的是 1.2s,这里取 1.0s。
PASTE_SETTLE = 0.15
RESTORE_SETTLE = 1.0

# 注入整体重试:总尝试次数与间隔。
# 失败大多是**临时故障** —— 修饰键还没完全松开(热键 keyup 竞态)、
# 剪贴板被别的程序占了一下、SendInput 偶发没投出去 —— 睡一小觉
# 再来一次就好了,立刻报失败反而把一次成功的输入变成"注入失败"。
INJECT_ATTEMPTS = 3
INJECT_RETRY_DELAY = 0.2

# 等修饰键松开的耐心上限与采样步长(见 _wait_modifiers)。
# 用户按完热键松手是必然的,等比报失败便宜;2.5s 是"真按住不放"
# (有意长按)与"还没松完"的分界。
MOD_WAIT_TIMEOUT = 2.5
MOD_WAIT_STEP = 0.03

# 失败原因码:把"到底卡在哪一步"说清楚。
#
# 以前所有失败都长成同一个样子(一个 False),上层只好写一句万能文案
# "注入失败,文字已留在剪贴板" —— 复制其实失败的时候,这句话是**假的**
# (剪贴板里什么都没有)。有了原因码 + 剪贴板实况,失败通知才能说实话。
REASON_EMPTY_TEXT = "empty_text"
REASON_CLIPBOARD_OPEN_FAILED = "clipboard_open_failed"
REASON_CLIPBOARD_WRITE_FAILED = "clipboard_write_failed"
REASON_CLIPBOARD_VERIFY_FAILED = "clipboard_verify_failed"
REASON_MODIFIER_TIMEOUT = "modifier_timeout"
REASON_KEYS_NOT_SENT = "keys_not_sent"

# 剪贴板层的原因码 → 注入层的原因码(win32 是纯绑定层,不反过来依赖 core)
_CLIPBOARD_REASON_MAP = {
    clipboard.WRITE_REASON_OPEN_FAILED: REASON_CLIPBOARD_OPEN_FAILED,
    clipboard.WRITE_REASON_WRITE_FAILED: REASON_CLIPBOARD_WRITE_FAILED,
    clipboard.WRITE_REASON_VERIFY_FAILED: REASON_CLIPBOARD_VERIFY_FAILED,
}


@dataclass(frozen=True)
class InjectResult:
    """一次注入的结果。

    ok      文字有没有真的送进目标窗口
    reason  失败原因码(成功时是空串)
    text_in_clipboard  失败时**剪贴板里实际有没有我们的那份文本** ——
             读回比对出来的,不是猜的。用户靠它判断"按 Ctrl+V 能不能拿到"。
    """

    ok: bool
    reason: str = ""
    text_in_clipboard: bool = False

    def __bool__(self) -> bool:
        # 旧调用方拿它当 bool 用(``if not result:``)—— 语义不变。
        return self.ok


def paste_text(text: str, hwnd: int = 0, restore: bool = True,
               trailing_space: bool = True) -> InjectResult:
    """把 text 粘贴到 hwnd 窗口的光标处。

    hwnd 为 0 表示不恢复焦点(粘贴到当前前台窗口)。

    临时故障(修饰键未松开/剪贴板被占/事件没投出去)会整体重试
    ``INJECT_ATTEMPTS`` 次;**粘贴已触发就绝不重试** —— 那会造成
    重复文本,比失败更糟。

    返回 :class:`InjectResult`:除了"是否真的送到了目标窗口",还带上
    失败原因码,以及**剪贴板里实际有没有我们的文本**(读回比对出来的)。

    送不到就必须如实说送不到 —— 谎报成功比失败更糟:用户以为文字
    已经进输入框了,不会再去别处找,结果那段话其实哪儿都没有。
    送不到时的兜底是**把识别结果留在剪贴板里**(不归还),用户按一下
    Ctrl+V 就能拿到,胶囊上也有「复制」按钮可以点。

    ⚠ "留在剪贴板里"这句话是**有条件的**:剪贴板被占导致根本没写进去
    时,里面什么都没有 —— 这时 ``text_in_clipboard`` 是 False,上层
    必须照实说"复制失败",不能说"已留在剪贴板"(原话就是假的)。
    """
    if not text:
        return InjectResult(False, REASON_EMPTY_TEXT)

    if trailing_space and not text.endswith((" ", "\n", "。", "!", "?", "，")):
        text += " "

    # ---- 备份原剪贴板(只备份文本,**只做一次**)----
    # 必须在重试循环外:第一次尝试写过剪贴板之后,里面已经是我们的
    # 文本,循环里再备份会把它误当成"原内容"还回去。
    # "读失败"和"里面没有文本"必须分得清:读失败说明我们**不知道**
    # 原内容是什么,也就没法归还 —— 那就干脆不还,把识别结果留在
    # 剪贴板里(总比"清空了又不还"强)。
    original: str | None = None
    if restore:
        read_ok, original = clipboard.read_text()
        if not read_ok:
            log.warning("读不到原剪贴板内容(可能被别的程序占着),"
                        "本次不归还,识别结果将留在剪贴板")

    reason = REASON_EMPTY_TEXT
    for attempt in range(1, INJECT_ATTEMPTS + 1):
        delivered, published_seq, reason = _attempt_inject(text, hwnd, attempt)
        if delivered:
            # 恢复原剪贴板放到后台线程:_restore 里要等 RESTORE_SETTLE
            # (约 1 秒)才动手,同步等它的话 UI 状态会滞后 —— 用户已经
            # 看到文字进输入框了,胶囊却还转着"识别中"(实测被吐槽过)。
            # 归属守卫(序号比对)在后台做完全等价:比对的是**这次成功
            # 尝试**发布时的序号,谁先检查都一样。
            #
            # original 为 None 有两种情况:配置关了归还,或者原剪贴板里
            # 确实没有文本。两种都不动 —— 识别结果留在剪贴板里,用户
            # 自己还能用。
            if original is not None:
                threading.Thread(
                    target=_restore, args=(original, text, published_seq),
                    name="clipboard-restore", daemon=True,
                ).start()
            return InjectResult(True, "", True)
        if attempt < INJECT_ATTEMPTS:
            log.info("注入未送达(第 %d/%d 次尝试),%.0fms 后重试",
                     attempt, INJECT_ATTEMPTS, INJECT_RETRY_DELAY * 1000)
            time.sleep(INJECT_RETRY_DELAY)

    # 失败时的实况:剪贴板里到底有没有我们的那份文本 —— **读回比对**,
    # 不猜。这一句决定了上层能不能说"按 Ctrl+V 就能拿到"。
    in_clipboard = _text_in_clipboard(text)
    log.warning("注入重试 %d 次仍未送达(%s),识别结果%s",
                INJECT_ATTEMPTS, reason,
                "保留在剪贴板里" if in_clipboard else "没能写进剪贴板")
    return InjectResult(False, reason, in_clipboard)


def _text_in_clipboard(text: str) -> bool:
    """剪贴板里现在是不是我们的那份文本。

    **读回来比对**,不猜:写入其实失败了、或者压根没走到写入那一步
    (修饰键被按住),剪贴板里装的是别的东西 —— 只有真读回来对上了
    才算数。
    """
    try:
        ok, current = clipboard.read_text()
    except Exception as e:
        log.debug("核对剪贴板内容失败: %s", e)
        return False
    return bool(ok) and current == text


def _wait_modifiers(attempt: int) -> str:
    """等修饰键松开,返回 "clear" / "ctrl" / "timeout"。

    - "clear":全松开,正常合成 Ctrl+V;
    - "ctrl":**稳定**只剩 Ctrl 按住(连续两个采样周期都是)—— 判定为
      用户有意按住,可顺势补 V。"稳定"这个条件是在滤掉松键途中
      的一瞬(全松的路径会经过 {ctrl,alt}→{ctrl}→{},那一瞬的
      ctrl-only 说明用户其实在松手,等他松完走正常路径);
    - "timeout":等满上限还按着(Alt/Shift/Win 没松,或 Ctrl 抖动
      一直不稳)—— 由调用方如实报 modifier_timeout。

    网络后端(MiniMax)识别只要零点几秒,注入开始时用户手指还留在
    热键上是常态 —— 等 2.5 秒的成本远低于"报失败 + 用户手动重试"。
    """
    deadline = time.monotonic() + MOD_WAIT_TIMEOUT
    ctrl_stable = 0
    while True:
        mods = held_modifiers()
        if not mods:
            return "clear"
        if mods == {"ctrl"}:
            ctrl_stable += 1
            if ctrl_stable >= 2:
                return "ctrl"
        else:
            ctrl_stable = 0
        if time.monotonic() >= deadline:
            log.debug("等修饰键松开超时(第 %d 次尝试,仍按着:%s)",
                      attempt, sorted(mods))
            return "timeout"
        time.sleep(MOD_WAIT_STEP)


def _attempt_inject(text: str, hwnd: int, attempt: int) -> tuple[bool, int, str]:
    """单次注入尝试,返回 ``(是否送达, 发布时的剪贴板序号, 失败原因)``。

    任何一步失败都返回 False —— 由上层决定是否重试。
    粘贴已触发(见下方 sent 判定)必须返回 True:重试会造成重复文本。

    剪贴板写入失败(含**读回校验**不过)都发生在 ``send_ctrl_v`` **之前**,
    此时粘贴还没触发,重试是安全的 —— 不会贴出重复文字。
    """
    # ---- 等修饰键松开(不再"发现按着就立刻失败") ----
    # 用户结束录音的方式就是再按一次热键(Ctrl+Alt+Space),识别完成时
    # 手指常还留在键上 —— 这不是故障,是必然的时间差。以前把它当
    # "失败"烧掉三次重试(共 ~0.5s),再报一句"复制到剪贴板失败"
    # (其实根本没碰剪贴板,纯误导)。现在改成就地等:
    #   - 全松 → 正常合成 Ctrl+V
    #   - 稳定只剩 Ctrl(用户有意按住) → **顺势只补一个 V**
    #     (用户的物理 Ctrl + 我们的 V = 粘贴,连 Ctrl 都不用合成)
    #   - Alt/Shift/Win 按着 → 发什么都不再是普通粘贴,继续等
    #   - 等满 MOD_WAIT_TIMEOUT 还按着 → 如实报 modifier_timeout
    mod = _wait_modifiers(attempt)
    if mod == "timeout":
        return False, 0, REASON_MODIFIER_TIMEOUT
    ctrl_held_by_user = (mod == "ctrl")

    write = clipboard.set_text_verified(text)
    if not write.ok:
        # 写不进去就别往下走:剪贴板里没有文字,Ctrl+V 贴出来的会是
        # 用户原来那份(或空的)—— 那比不贴更糟。
        log.debug("写入剪贴板失败(第 %d 次尝试):%s", attempt, write.reason)
        return False, 0, _CLIPBOARD_REASON_MAP.get(
            write.reason, REASON_CLIPBOARD_WRITE_FAILED)
    # 写入自带的读回校验用的是 GetClipboardData,**不递增变更序号** ——
    # 这里取到的序号仍然是"我们这份"的序号,归属判定不受影响。
    published_seq = clipboard.get_sequence_number()

    # 转写这 1 秒里用户可能切了窗口,把焦点还回去再贴
    if hwnd and get_foreground_window() != hwnd:
        restore_foreground(hwnd)
        time.sleep(0.25)

    if ctrl_held_by_user:
        # 顺势补 V:Ctrl 是用户手指物理按着的(真实按下状态),我们只投
        # V↓/V↑。绝不合成 Ctrl↓/Ctrl↑ —— 那会跟他真实的手指状态打架。
        sent = send_v()
        v_down_at, total = 1, 2   # [V↓,V↑]:投出 1 个 = V↓ 已送达
    else:
        sent = send_ctrl_v()
        v_down_at, total = 2, 4   # [Ctrl↓,V↓,V↑,Ctrl↑]:前 2 个 = V↓ 已送达
    # **V↓ 送出去 = 目标应用已经收到"粘贴"这个动作了**;
    # 没送出去才是"什么都没发生"(重试安全)。
    if sent < v_down_at:
        log.warning("粘贴按键没送出去(sent=%d/%d,第 %d 次尝试)",
                    sent, total, attempt)
        return False, published_seq, REASON_KEYS_NOT_SENT
    if sent < total:
        # 抬起事件没全送达,但 V↓ 已送 = 粘贴**已经触发**(api 侧会补抬,
        # 微秒级,远快过按键重复间隔)—— 绝不能重贴,那会重复文本。
        log.info("粘贴只投递了 %d/%d 个事件,V↓ 已送达", sent, total)
    time.sleep(PASTE_SETTLE)
    return True, published_seq, ""


def _restore(original: str | None, written: str,
             published_seq: int) -> None:
    """把原剪贴板还回去 —— 但只在它还是"我们写的那份"时才还。

    original 为 None 表示原剪贴板没有文本(或者只有图片之类读不出来的
    内容)。这时**不动**:我们无法完整还原它,硬还原只会把用户的东西
    弄丢,不如把识别结果留在剪贴板里,用户自己还能用。
    """
    if original is None:
        return

    time.sleep(RESTORE_SETTLE)

    current_seq = clipboard.get_sequence_number()
    if current_seq != published_seq:
        # 序号变了 = 有人在这期间动过剪贴板(多半是用户复制了新内容)。
        # 用户的新内容优先,我们的文本让位 —— 但**不覆盖**它。
        log.info("剪贴板在这期间被改动,放弃恢复(用户的新内容优先)")
        return

    try:
        if clipboard.get_text() != written:
            log.info("剪贴板内容已不是我们写入的,放弃恢复")
            return
    except Exception as e:
        log.debug("读取剪贴板失败: %s", e)
        return

    try:
        if not clipboard.set_text(original):
            log.debug("恢复剪贴板失败")
    except Exception as e:
        log.debug("恢复剪贴板失败: %s", e)


def paste_with_feedback(text: str, hwnd: int = 0, restore: bool = True) -> bool:
    """同上,但会记录焦点窗口标题,便于排障。

    返回值保持 bool(不往外抛 InjectResult):这个函数只是排障用的包装。
    """
    if hwnd:
        title = get_window_title(hwnd)
        if get_foreground_window() != hwnd:
            print(f"[focus] 焦点已变化,恢复到: {title[:40]}")
    return bool(paste_text(text, hwnd=hwnd, restore=restore, trailing_space=False))
