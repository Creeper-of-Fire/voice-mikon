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

from ..logging_setup import get_logger
from . import clipboard
from .api import (get_foreground_window, modifiers_held, restore_foreground,
                  send_ctrl_v, get_window_title)

log = get_logger("win32.inject")

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


def paste_text(text: str, hwnd: int = 0, restore: bool = True,
               trailing_space: bool = True) -> bool:
    """把 text 粘贴到 hwnd 窗口的光标处。

    hwnd 为 0 表示不恢复焦点(粘贴到当前前台窗口)。

    临时故障(修饰键未松开/剪贴板被占/事件没投出去)会整体重试
    ``INJECT_ATTEMPTS`` 次;**粘贴已触发就绝不重试** —— 那会造成
    重复文本,比失败更糟。

    返回**是否真的送到了目标窗口**。

    送不到就必须如实说送不到 —— 谎报成功比失败更糟:用户以为文字
    已经进输入框了,不会再去别处找,结果那段话其实哪儿都没有。
    送不到时的兜底是**把识别结果留在剪贴板里**(不归还),用户按一下
    Ctrl+V 就能拿到,胶囊上也有「复制」按钮可以点。
    """
    if not text:
        return False

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

    for attempt in range(1, INJECT_ATTEMPTS + 1):
        delivered, published_seq = _attempt_inject(text, hwnd, attempt)
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
            return True
        if attempt < INJECT_ATTEMPTS:
            log.info("注入未送达(第 %d/%d 次尝试),%.0fms 后重试",
                     attempt, INJECT_ATTEMPTS, INJECT_RETRY_DELAY * 1000)
            time.sleep(INJECT_RETRY_DELAY)

    log.warning("注入重试 %d 次仍未送达,识别结果保留在剪贴板里", INJECT_ATTEMPTS)
    return False


def _attempt_inject(text: str, hwnd: int, attempt: int) -> tuple[bool, int]:
    """单次注入尝试,返回 ``(是否送达, 发布时的剪贴板序号)``。

    任何一步失败都返回 False —— 由上层决定是否重试。
    粘贴已触发(见下方 sent 判定)必须返回 True:重试会造成重复文本。
    """
    # 用户正按着 Ctrl/Alt/Shift 的话,发出去的其实是 Ctrl+Alt+V,
    # 目标应用收到的是别的东西,粘贴根本不会触发。
    # 多数是松热键的 keyup 竞态 —— 上层睡一下再试通常就过了。
    if modifiers_held():
        log.debug("用户正按住修饰键(第 %d 次尝试),放弃本次", attempt)
        return False, 0

    if not clipboard.set_text(text):
        log.debug("写入剪贴板失败(第 %d 次尝试)", attempt)
        return False, 0
    published_seq = clipboard.get_sequence_number()

    # 转写这 1 秒里用户可能切了窗口,把焦点还回去再贴
    if hwnd and get_foreground_window() != hwnd:
        restore_foreground(hwnd)
        time.sleep(0.25)

    sent = send_ctrl_v()
    # SendInput 只插入数组前缀,顺序是 [Ctrl↓, V↓, V↑, Ctrl↑]。
    # **V↓ 送出去 = 目标应用已经收到"粘贴"这个动作了**:
    #   sent >= 2  粘贴已经触发(Ctrl↑ 没送出去只是键没抬起,
    #               api.send_ctrl_v 会补抬,不影响已经发生的粘贴)
    #   sent <= 1  V 根本没送出去,**什么都没发生**(重试安全)
    if sent < 2:
        log.warning("Ctrl+V 一个事件都没送出去(sent=%d,第 %d 次尝试)",
                    sent, attempt)
        return False, published_seq
    if sent < 4:
        # 抬起事件没全部送达,但 V↓ 已经送出去 = 粘贴**已经触发**了。
        # api.send_ctrl_v 会把抬起事件补发掉(补发在微秒级,远快过
        # 按键重复的最小间隔),所以绝不能重贴 —— 那会造成重复文本。
        log.info("Ctrl+V 只投递了 %d/4 个事件,粘贴已触发", sent)
    time.sleep(PASTE_SETTLE)
    return True, published_seq


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
    """同上,但会记录焦点窗口标题,便于排障。"""
    if hwnd:
        title = get_window_title(hwnd)
        if get_foreground_window() != hwnd:
            print(f"[focus] 焦点已变化,恢复到: {title[:40]}")
    return paste_text(text, hwnd=hwnd, restore=restore, trailing_space=False)
