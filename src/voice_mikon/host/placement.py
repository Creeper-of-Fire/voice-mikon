"""胶囊摆位计算(纯逻辑,不碰 Win32 窗口句柄)。

坐标约定:

- **物理像素**:Windows 的窗口坐标就是物理像素,Win32 给的全是这个。
- **逻辑像素**:CSS 像素(用户看到的尺寸)= 物理 ÷ 缩放。

悬浮球和胶囊**共用同一个窗口**(窗口是固定尺寸的,resize 透明就废),
所以"球在哪"其实是"窗口摆哪"的问题:球固定待在窗口的**底边居中**,
胶囊也在底边居中 —— 两者横坐标一致,展开时球"长成"胶囊,位置不用挪。

位置记忆的存法:存**相对工作区左上角的逻辑像素偏移**,另存显示器
设备名,换缩放率/换显示器都不飞。
"""

from __future__ import annotations

# 胶囊与工作区边缘的最小留白(物理像素)
EDGE_MARGIN = 8

# 球的直径(逻辑像素)。
#
# ⚠ **必须等于胶囊的高度**(capsule.css 里 .recording-pill 的 height:40px):
# 球不是另一个元素 —— 它就是这颗胶囊收窄成的正圆。不等大的话,
# 球横向长开时走的轨迹和胶囊对不上,窗口的命中区(SetWindowRgn,
# 按上报矩形裁)就会切到球上(真机症状:球的上半截多一条白线)。
ORB_SIZE = 40
# 球的下边到窗口下边(逻辑像素)。和 CSS 的 bottom 保持一致。
ORB_INSET_Y = 10
# 球与工作区边缘的最小留白(逻辑像素)
ORB_MARGIN = 16


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


def encode_offset(x_phys: int, y_phys: int,
                  work_area: tuple[int, int, int, int],
                  scale: float) -> str:
    """把窗口左上角的物理坐标存成"相对工作区的逻辑偏移"。

    存逻辑偏移而不是绝对坐标:换一台缩放率不同的显示器时,胶囊
    相对工作区的位置不变(不会飞到屏幕外或挤到边角上)。

    存相对工作区而不是绝对屏幕坐标:多屏场景下,副屏的绝对坐标里
    含有该屏的物理原点,换机器接的屏不一样就全错。

    格式 ``"dx,dy"``。
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


def orb_inset_x(client_w: int, orb_size: int = ORB_SIZE) -> int:
    """球在窗口里底边居中 → 球左边到窗口左边的偏移(逻辑像素)。

    球和胶囊共用底边居中线,横坐标就对齐了 —— 展开时不用挪位置。
    """
    return max(0, (client_w - orb_size) // 2)


def orb_screen_position(win_x: int, win_y: int, win_w: int, win_h: int,
                        scale: float, orb_size: int = ORB_SIZE,
                        inset_y: int = ORB_INSET_Y) -> tuple[int, int]:
    """窗口左上角(物理像素) → 球的左上角(物理像素)。

    球是底边居中的:横坐标 = 窗口左边 + (窗口宽 − 球宽) / 2,
    纵坐标 = 窗口底边 − (下边距 + 球高)。
    """
    client_w = int(round(win_w / scale))
    x = win_x + int(round(orb_inset_x(client_w, orb_size) * scale))
    y = win_y + win_h - int(round((inset_y + orb_size) * scale))
    return x, y


def clamp_orb_position(orb_x: int, orb_y: int, win_w: int, win_h: int,
                       scale: float,
                       work_area: tuple[int, int, int, int],
                       orb_size: int = ORB_SIZE, inset_y: int = ORB_INSET_Y,
                       margin: int = ORB_MARGIN) -> tuple[int, int]:
    """把球夹回"允许待着"的范围内(物理像素)。

    纵向比横向严:窗口是**固定尺寸**的,胶囊(文字条 + 胶囊)在球的
    **上方**,球拖得太靠上,展开后文字条会出屏。所以球的上边至少要
    留出"窗口高 − (下边距 + 球高)"的高度。
    """
    left, top, right, bottom = work_area
    size_phys = int(round(orb_size * scale))
    edge = int(round(margin * scale))
    # 球上方要留给胶囊(文字条 + 胶囊)的高度
    head = max(0, win_h - int(round((inset_y + orb_size) * scale)))

    min_x = left + edge
    max_x = max(min_x, right - edge - size_phys)
    min_y = top + head
    max_y = max(min_y, bottom - edge - size_phys)

    return min(max(orb_x, min_x), max_x), min(max(orb_y, min_y), max_y)


def orb_window_position(orb_x: int, orb_y: int, win_w: int, win_h: int,
                        scale: float,
                        work_area: tuple[int, int, int, int],
                        orb_size: int = ORB_SIZE, inset_y: int = ORB_INSET_Y,
                        margin: int = ORB_MARGIN) -> tuple[int, int]:
    """球的位置(物理像素) → 窗口左上角(物理像素)。

    球的位置先夹回允许的范围,再反推窗口该摆哪 —— 拖到屏幕边上
    也不会把窗口甩到屏幕外。
    """
    ox, oy = clamp_orb_position(orb_x, orb_y, win_w, win_h, scale,
                                work_area, orb_size, inset_y, margin)
    client_w = int(round(win_w / scale))
    x = ox - int(round(orb_inset_x(client_w, orb_size) * scale))
    y = oy + int(round((inset_y + orb_size) * scale)) - win_h
    return x, y


def default_orb_position(work_area: tuple[int, int, int, int], scale: float,
                         orb_size: int = ORB_SIZE,
                         margin: int = ORB_MARGIN) -> tuple[int, int]:
    """默认摆在工作区**右下角**(物理像素)。

    右下角是这类悬浮球的惯例位置:不挡中间的编辑区,也不挡任务栏。
    """
    _left, _top, right, bottom = work_area
    edge = int(round(margin * scale))
    size_phys = int(round(orb_size * scale))
    return right - edge - size_phys, bottom - edge - size_phys


def orb_position_from_geometry(geometry: str,
                                work_area: tuple[int, int, int, int],
                                scale: float, win_w: int, win_h: int,
                                orb_size: int = ORB_SIZE,
                                inset_y: int = ORB_INSET_Y,
                                margin: int = ORB_MARGIN) -> tuple[int, int] | None:
    """记忆的偏移 → 窗口左上角(物理像素);记忆无效时返回 None。

    存的是**球**的偏移(不是窗口的):球是用户真正盯着的那一小块,
    记住它在哪,换屏之后它还在同一个相对位置。
    """
    offset = decode_offset(geometry)
    if offset is None:
        return None
    dx, dy = offset
    left, top, _right, _bottom = work_area
    orb_x = left + int(round(dx * scale))
    orb_y = top + int(round(dy * scale))
    return orb_window_position(orb_x, orb_y, win_w, win_h, scale, work_area,
                               orb_size, inset_y, margin)
