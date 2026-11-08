"""屏幕截取:mss。

选 mss 不用 PIL.ImageGrab 的原因:多显示器场景下 mss 的坐标
(虚拟桌面物理像素,前提是进程先声明 DPI aware)和选区窗口的
坐标系严格一致;ImageGrab 在多屏不同缩放率下会有偏移。

坐标系约定(全工具统一):**物理像素,原点在虚拟桌面左上角**。
进程声明 per-monitor DPI aware 之后,GetSystemMetrics / mss /
tkinter 三者说的都是同一套坐标,不再有任何换算。
"""

from __future__ import annotations

from PIL import Image


def grab_virtual_screen() -> Image.Image:
    """截取整个虚拟桌面(所有显示器拼起来的那一整块)。

    返回 RGB 图,原点平移到虚拟桌面左上角(0, 0) —— 选区窗口
    也是从虚拟屏原点铺满的,两套坐标天然一致。
    """
    import mss

    with mss.mss() as sct:
        # monitors[0] 是"全部显示器"的包围盒(monitors[1:] 才是单屏)
        raw = sct.grab(sct.monitors[0])
    # mss 给的是 BGRA,按 BGRX 裸格式转 RGB
    return Image.frombytes("RGB", raw.size, raw.bgra, "raw", "BGRX")
