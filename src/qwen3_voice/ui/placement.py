"""胶囊摆位计算(纯逻辑,不碰 Win32 窗口句柄)。

拆成独立模块是为了能单测:算位置这件事本身不涉及窗口句柄 ——
"锚点在哪、窗口多大、工作区多大、缩放多少"都是输入,输出是一个
坐标。

坐标约定:

- **物理像素**:Windows 的窗口坐标就是物理像素,Win32 给的全是这个。
- **逻辑像素**:CSS 像素(用户看到的尺寸)= 物理 ÷ 缩放。

摆放规则(三种模式):

- ``follow`` 贴着输入光标:优先放在锚点**下方**(像输入法候选框
  "贴着但不挡字"),下方放不下就翻到**上方**,最后一定夹回工作区
  (防出屏)。
- ``bottom`` 现状:工作区底部居中。
- ``custom`` 记忆位置:存的是**相对所在工作区左上角的逻辑偏移**,
  换缩放率、换显示器都不飞。
"""

from __future__ import annotations

from ..config import CAPSULE_POSITION_MODES

# 胶囊位置三档(真相源在 config.py —— 配置校验和这里用的是同一份)
POSITION_MODES = CAPSULE_POSITION_MODES

# 锚点与胶囊之间的间隙(物理像素)
ANCHOR_GAP = 8

# 胶囊与工作区边缘的最小留白(物理像素)
EDGE_MARGIN = 8


def clamp_to_work_area(x: int, y: int, win_w: int, win_h: int,
                       work_area: tuple[int, int, int, int],
                       margin: int = EDGE_MARGIN) -> tuple[int, int]:
    """把窗口左上角夹进工作区(留 margin 的边距)。

    窗口比工作区还大时(极小分辨率 + 高缩放)不能算出负的边界 ——
    ``max(...)`` 保证左/上界不小于最小留白,至少不会跑到屏幕外。
    """
    left, top, right, bottom = work_area
    min_x = left + margin
    min_y = top + margin
    max_x = max(min_x, right - win_w - margin)
    max_y = max(min_y, bottom - win_h - margin)
    return min(max(x, min_x), max_x), min(max(y, min_y), max_y)


def follow_position(anchor_x: int, anchor_y: int,
                    win_w: int, win_h: int,
                    work_area: tuple[int, int, int, int],
                    gap: int = ANCHOR_GAP,
                    margin: int = EDGE_MARGIN) -> tuple[int, int]:
    """贴着锚点摆胶囊。

    水平居中于锚点;垂直方向优先放**下方**(锚点 + gap),
    下方空间不够(会顶出工作区下边)就改放**上方**(锚点 - gap - 高度)。
    两种都不行就由夹取兜底(总有一条边贴得住)。
    """
    left, top, right, bottom = work_area

    x = anchor_x - win_w // 2
    y = anchor_y + gap

    if y + win_h > bottom - margin:
        # 下方放不下 → 翻到上方
        y = anchor_y - gap - win_h

    return clamp_to_work_area(x, y, win_w, win_h, work_area, margin)


def bottom_position(win_w: int, win_h: int,
                    work_area: tuple[int, int, int, int],
                    bottom_margin: int) -> tuple[int, int]:
    """工作区底部居中(现状行为)。"""
    left, _top, right, bottom = work_area
    x = left + ((right - left) - win_w) // 2
    y = bottom - win_h - bottom_margin
    return x, y


# ---------------- 自定义位置的记忆与还原 ----------------

def encode_offset(x_phys: int, y_phys: int,
                  work_area: tuple[int, int, int, int],
                  scale: float) -> str:
    """把窗口左上角的物理坐标存成"相对工作区的逻辑偏移"。

    存逻辑偏移而不是绝对坐标:换一台缩放率不同的显示器时,胶囊
    相对工作区的位置不变(不会飞到屏幕外或挤到边角上)。

    存相对工作区而不是绝对屏幕坐标:多屏场景下,副屏的绝对坐标里
    含有该屏的物理原点,换机器接的屏不一样就全错。

    格式 ``"dx,dy"``,和面板的 ``panel_geometry`` 一个路子。
    """
    left, top, _right, _bottom = work_area
    dx = int(round((x_phys - left) / scale))
    dy = int(round((y_phys - top) / scale))
    return f"{dx},{dy}"


def decode_offset(geometry: str) -> tuple[int, int] | None:
    """解析 ``"dx,dy"``;解析不了返回 None(调用方走兜底)。"""
    if not geometry:
        return None
    try:
        parts = [int(v) for v in str(geometry).split(",")]
    except (TypeError, ValueError):
        return None
    if len(parts) != 2:
        return None
    return parts[0], parts[1]


def custom_position(geometry: str,
                    work_area: tuple[int, int, int, int],
                    scale: float,
                    win_w: int, win_h: int,
                    margin: int = EDGE_MARGIN) -> tuple[int, int] | None:
    """按记忆的偏移还原物理坐标;记忆无效时返回 None。

    ``scale`` 是**目标显示器**的缩放:偏移是逻辑像素,乘回去才是
    物理像素。
    """
    offset = decode_offset(geometry)
    if offset is None:
        return None
    dx, dy = offset
    left, top, _right, _bottom = work_area
    x = int(round(left + dx * scale))
    y = int(round(top + dy * scale))
    return clamp_to_work_area(x, y, win_w, win_h, work_area, margin)
