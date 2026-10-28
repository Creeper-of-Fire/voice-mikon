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


def paste_text(text: str, hwnd: int = 0, restore: bool = True,
               trailing_space: bool = True) -> bool:
    """把 text 粘贴到 hwnd 窗口的光标处。

    hwnd 为 0 表示不恢复焦点(粘贴到当前前台窗口)。
    返回是否成功写入剪贴板并发送粘贴。
    """
    if not text:
        return False

    if trailing_space and not text.endswith((" ", "\n", "。", "!", "?", "，")):
        text += " "

    # 用户正按着 Ctrl/Alt/Shift 的话,发出去的其实是 Ctrl+Alt+V,
    # 目标应用收到的是别的东西,粘贴根本不会触发。
    # 这种时候宁可什么都不做,也不要往目标窗口塞莫名其妙的东西。
    if modifiers_held():
        log.debug("用户正按住修饰键,放弃注入")
        return False

    # ---- 备份原剪贴板(只备份文本)----
    original = clipboard.get_text() if restore else None

    if not clipboard.set_text(text):
        log.debug("写入剪贴板失败")
        return False
    published_seq = clipboard.get_sequence_number()

    # 转写这 1 秒里用户可能切了窗口,把焦点还回去再贴
    if hwnd and get_foreground_window() != hwnd:
        restore_foreground(hwnd)
        time.sleep(0.25)

    sent = send_ctrl_v()
    if sent < 4:
        # SendInput 只投递了一部分(常见于被 UIPI 拦下:目标程序以管理员
        # 身份运行,我们的进程不是)。此时目标多半没收到粘贴。
        log.warning("Ctrl+V 只投递了 %d/4 个事件,可能没送达", sent)
    time.sleep(PASTE_SETTLE)

    # 恢复原剪贴板放到后台线程:_restore 里要等 RESTORE_SETTLE(约 1 秒)
    # 才动手,同步等它的话 UI 状态会滞后 —— 用户已经看到文字进输入框
    # 了,面板却还转着"识别中"(实测被吐槽过)。归属守卫(序号比对)
    # 在后台做完全等价:比对的是我们发布时的序号,谁先检查都一样。
    threading.Thread(
        target=_restore, args=(original, text, published_seq),
        name="clipboard-restore", daemon=True,
    ).start()
    return True


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
