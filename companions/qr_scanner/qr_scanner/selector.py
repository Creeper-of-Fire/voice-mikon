"""全屏选区窗:冻结画面 + 拖框,tkinter 实现。

工具自己的一条 UI 线,**不碰 pywebview**(主程序那边的透明/
裁形是另一套坑,附属工具没必要陪跑;tkinter 坏也只坏自己)。

观感对齐现代截图工具:窗口里铺的是截屏那一帧的**变暗版**,
拖出的矩形区域内实时贴回**原图** —— 选区内亮、选区外暗,
所见即所得;还天然规避了"截屏期间屏幕还在变"的问题。

坐标约定:窗口铺满虚拟桌面,进程先声明 per-monitor DPI aware,
所以 tkinter/mss/选区坐标全是**物理像素**,一套坐标走到底。
"""

from __future__ import annotations

import ctypes

# 虚拟桌面几何的 GetSystemMetrics 槽位(SM_X/YVIRTUALSCREEN 等)
_SM_XVIRTUALSCREEN = 76
_SM_YVIRTUALSCREEN = 77
_SM_CXVIRTUALSCREEN = 78
_SM_CYVIRTUALSCREEN = 79

_BORDER = "#f59e0b"  # 选区边框/尺寸标签的颜色(琥珀,和主程序警告色同族)
_MASK_ALPHA = 0.45  # 选区外变暗的程度(与全黑混合的比例)
_MIN_PICK = 10  # 拖出的框宽/高小于这个像素当误触,按取消处理

# 进程级的 DPI 声明只做一次的标记
_dpi_done = False


def enable_dpi_awareness() -> None:
    """声明 per-monitor DPI aware —— 物理像素坐标系的前提。

    **必须在任何截屏/建窗之前调用**(进程级设置,晚了没用):
    不声明的话,150% 缩放屏上 GetSystemMetrics 给的是虚拟化后的
    逻辑值,mss 截屏和 tkinter 坐标全都对不上。

    按支持程度从高到低试:Win10 1703 per-monitor v2 → Win8.1
    per-monitor → 最低限度的 system aware。全失败就带着 96dpi
    假设跑(高缩放屏上选区会错位,但至少能用)。
    """
    global _dpi_done
    if _dpi_done:
        return
    _dpi_done = True

    user32 = ctypes.windll.user32
    try:
        # DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
        if user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
            return
    except OSError:
        pass  # 旧系统没有这个导出
    try:
        # TEMP_PER_MONITOR_AWARE = 2
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        return
    except (OSError, AttributeError):
        pass
    try:
        user32.SetProcessDPIAware()
    except OSError:
        pass


def virtual_screen_metrics() -> tuple[int, int, int, int]:
    """虚拟桌面的 (x, y, 宽, 高),物理像素。"""
    user32 = ctypes.windll.user32
    return (
        user32.GetSystemMetrics(_SM_XVIRTUALSCREEN),
        user32.GetSystemMetrics(_SM_YVIRTUALSCREEN),
        user32.GetSystemMetrics(_SM_CXVIRTUALSCREEN),
        user32.GetSystemMetrics(_SM_CYVIRTUALSCREEN),
    )


def select_region(screenshot):
    """弹全屏选区窗,返回用户框出的矩形 ``(l, t, w, h)``。

    坐标是物理像素、相对虚拟桌面原点,与 ``screenshot``(必须来自
    :func:`capture.grab_virtual_screen`)同一坐标系。Esc / 右键 /
    拖太小 = 取消,返回 ``None``。
    """
    from PIL import Image, ImageTk
    import tkinter as tk

    vx, vy, vw, vh = virtual_screen_metrics()
    if screenshot.size != (vw, vh):
        # 多屏热插拔等竞态:截图和当前几何对不上,宁可不干活
        return None

    # 选区外铺的变暗底图(一次合成,拖动期间只动选区那块)
    dark = Image.blend(screenshot, Image.new("RGB", screenshot.size, (0, 0, 0)), _MASK_ALPHA)

    root = tk.Tk()
    root.overrideredirect(True)  # 无边框
    root.attributes("-topmost", True)
    root.geometry(f"{vw}x{vh}+{vx}+{vy}")

    canvas = tk.Canvas(root, cursor="crosshair", highlightthickness=0, bg="#000000")
    canvas.pack(fill="both", expand=True)

    # 显式 master:结果窗会在同一进程再建一个 Tk 实例,
    # 不写 master 的 PhotoImage 会绑错实例
    dark_photo = ImageTk.PhotoImage(dark, master=root)
    canvas.create_image(0, 0, image=dark_photo, anchor="nw")

    state = {
        "start": None,  # 拖拽起点
        "result": None,  # 最终选区
        "frame": 0,  # 边框矩形 item
        "bright": 0,  # 选区内原图 crop 的 image item
        "bright_photo": None,  # 持引用,防 PhotoImage 被 GC 后图消失
        "tag": 0,  # 尺寸标签 item
    }

    def cancel(*_):
        state["result"] = None
        root.destroy()

    def draw(x, y):
        x0, y0 = state["start"]
        l, t = min(x0, x), min(y0, y)
        w, h = abs(x - x0), abs(y - y0)
        # 选区内贴回原图:这就是"框内亮、框外暗"的来源
        if w > 0 and h > 0:
            crop = screenshot.crop((l, t, l + w, t + h))
            state["bright_photo"] = ImageTk.PhotoImage(crop, master=root)
            if state["bright"]:
                canvas.itemconfig(state["bright"], image=state["bright_photo"])
                canvas.coords(state["bright"], l, t)
            else:
                state["bright"] = canvas.create_image(
                    l, t, image=state["bright_photo"], anchor="nw"
                )
        if state["frame"]:
            canvas.coords(state["frame"], l, t, l + w, t + h)
        else:
            state["frame"] = canvas.create_rectangle(
                0, 0, 0, 0, outline=_BORDER, width=2
            )
        # 尺寸标签跟在框右下角,快出屏时往里收
        tx = min(l + w + 10, vw - 90)
        ty = min(t + h + 10, vh - 30)
        text = f"{w} × {h}"
        if state["tag"]:
            canvas.itemconfig(state["tag"], text=text)
            canvas.coords(state["tag"], tx, ty)
        else:
            state["tag"] = canvas.create_text(
                tx, ty, text=text, fill=_BORDER, anchor="nw",
                font=("Segoe UI", 10, "bold"),
            )

    def on_press(e):
        state["start"] = (e.x, e.y)
        canvas.delete(hint)  # 开始拖了,操作提示让位
        draw(e.x, e.y)

    def on_motion(e):
        if state["start"] is None:
            return
        draw(e.x, e.y)

    def on_release(e):
        if state["start"] is None:
            return
        x0, y0 = state["start"]
        l, t = min(x0, e.x), min(y0, e.y)
        w, h = abs(e.x - x0), abs(e.y - y0)
        if w < _MIN_PICK or h < _MIN_PICK:
            cancel()  # 基本没拖:当误触
            return
        state["result"] = (l, t, w, h)
        root.destroy()

    hint = canvas.create_text(
        vw // 2, 36,
        text="拖拽框选二维码,松开即识别 — Esc 或右键取消",
        fill="#ffffff",
        font=("Segoe UI", 12),
    )

    canvas.bind("<Button-1>", on_press)
    canvas.bind("<B1-Motion>", on_motion)
    canvas.bind("<ButtonRelease-1>", on_release)
    canvas.bind("<Button-3>", cancel)
    root.bind("<Escape>", cancel)
    root.focus_force()  # 无边框窗口要自己抢焦点,键盘(Esc)才可靠

    root.mainloop()
    return state["result"]
