"""文本注入:把识别结果送进当前光标位置。

策略:剪贴板 + 模拟 Ctrl+V。
  - 不依赖目标应用的输入法状态,中文绝不出错
  - 一次粘贴,不存在逐字输入的丢字/乱序问题
代价:会占用剪贴板。所以粘贴完成后把原内容恢复回去。
"""

from __future__ import annotations

import time

from . import clipboard
from .api import get_foreground_window, get_window_title, restore_foreground, send_ctrl_v


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

    original = clipboard.get_text() if restore else None

    if not clipboard.set_text(text):
        return False

    # 转写这 1 秒里用户可能切了窗口,把焦点还回去再贴
    if hwnd and get_foreground_window() != hwnd:
        restore_foreground(hwnd)
        time.sleep(0.25)

    send_ctrl_v()
    time.sleep(0.15)

    if original is not None:
        time.sleep(0.4)
        clipboard.set_text(original)

    return True


def paste_with_feedback(text: str, hwnd: int = 0, restore: bool = True) -> bool:
    """同上,但会记录焦点窗口标题,便于排障。"""
    if hwnd:
        title = get_window_title(hwnd)
        if get_foreground_window() != hwnd:
            print(f"[focus] 焦点已变化,恢复到: {title[:40]}")
    return paste_text(text, hwnd=hwnd, restore=restore, trailing_space=False)
