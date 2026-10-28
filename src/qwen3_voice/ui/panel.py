"""常驻控制面板(tkinter)。

设计:
  - 深色**无边框**小窗(自己画标题栏),始终置顶
  - 可拖动,位置持久化到配置
  - 主按钮:开始 / 停止(录音时变红并显示波形)
  - 实时波形 + 计时
  - 模型切换(0.6B / 1.7B)
  - 结果预览区

tkinter 的窗口操作必须在创建它的线程里调用,所以本模块的所有 UI 更新
都通过 `after()` 回到主线程执行;控制器在别的线程发事件,这里用队列接力。
"""

from __future__ import annotations

import queue
import tkinter as tk
from tkinter import font as tkfont
from typing import Any

from ..core.controller import State, UIEvent, VoiceController
from ..logging_setup import get_logger

log = get_logger("ui.panel")

# ---- 配色 ----
BG = "#1b1d26"
BG_CARD = "#252833"
BG_HEAD = "#22242f"
FG = "#e9eaee"
FG_DIM = "#8b8e99"
ACCENT = "#4c7dff"
REC = "#e5484d"
OK = "#3fb950"
WARN = "#d29922"
BORDER = "#343846"

# 设计基准(96 DPI 下的逻辑像素)
BASE_W = 268
BASE_H = 232
BASE_PAD = 12

FONT = "Microsoft YaHei UI"
MONO = "Consolas"


def system_scale() -> float:
    """系统缩放系数(本机 175% -> 1.75)。

    tkinter 在 DPI 感知下会把字体按 scaling 放大,但固定像素(padding、
    控件宽高)不会跟着放大 —— 所以这些常量都要自己乘一遍缩放,
    否则高 DPI 下布局会挤成一团。
    """
    try:
        import ctypes

        dc = ctypes.windll.user32.GetDC(0)
        try:
            dpi = ctypes.windll.gdi32.GetDeviceCaps(dc, 88)  # LOGPIXELSX
        finally:
            ctypes.windll.user32.ReleaseDC(0, dc)
        return max(1.0, dpi / 96.0)
    except Exception:
        return 1.0


class VoicePanel:
    """语音输入控制面板。"""

    def __init__(self, controller: VoiceController, geometry: str = ""):
        # 高分屏下不声明 DPI 感知,控件尺寸和布局都会错
        from ..win32.dpi import set_dpi_awareness

        set_dpi_awareness()

        self.controller = controller
        self._events: queue.Queue[UIEvent] = queue.Queue()
        self._levels: list[float] = []
        self._elapsed = 0.0
        self._state = State.STARTING
        self._last_result = ""
        self._status_text = "启动中..."
        self._drag_offset = (0, 0)
        self._initializing = True
        self._scale = system_scale()
        # 按缩放折算后的尺寸
        self.W = int(BASE_W * self._scale)
        self.H = int(BASE_H * self._scale)
        self.PAD = int(BASE_PAD * self._scale)

        self.root = tk.Tk()
        self.root.title("Qwen3 语音输入")
        self.root.configure(bg=BG)
        self.root.resizable(False, False)
        self.root.attributes("-topmost", True)
        # 无边框:标题栏自己画
        self.root.overrideredirect(True)

        self._build()
        self._apply_geometry(geometry)

        controller.subscribe(self._on_event)
        self.root.after(40, self._poll_events)
        self.root.after(100, self._tick)

    # ---------- 构建界面 ----------

    def _build(self) -> None:
        """构建界面。

        用 pack 而不是 grid:在固定尺寸的无边框窗口里,grid 的行高分配
        经常算不准(控件互相重叠);pack 顺序放置,行为可预测。
        """
        PAD = self.PAD
        s = self._scale

        def px(v: float) -> int:
            """按缩放折算像素值。"""
            return int(round(v * s))

        # ---- 自绘标题栏 ----
        header = tk.Frame(self.root, bg=BG_HEAD, height=px(30))
        header.pack(side="top", fill="x")
        header.pack_propagate(False)

        self.title_label = tk.Label(
            header, text="语音输入", bg=BG_HEAD, fg=FG, anchor="w",
            font=tkfont.Font(family=FONT, size=9, weight="bold"),
        )
        self.title_label.pack(side="left", padx=(PAD, 0), pady=px(6))

        self.state_dot = tk.Label(header, text="●", bg=BG_HEAD, fg=WARN,
                                  font=tkfont.Font(family=FONT, size=10))
        self.state_dot.pack(side="left", padx=(px(6), 0))

        # 关闭(隐藏)按钮
        self.close_btn = tk.Label(header, text="✕", bg=BG_HEAD, fg=FG_DIM,
                                  font=tkfont.Font(family=FONT, size=9),
                                  cursor="hand2")
        self.close_btn.pack(side="right", padx=(0, px(PAD - 4)))
        self.close_btn.bind("<Button-1>", lambda _e: self.root.withdraw())

        # ---- 主按钮 ----
        self.record_btn = tk.Button(
            self.root, text="●  开始录音", command=self._on_record_click,
            bg=ACCENT, fg="white", activebackground="#3d6ae8",
            activeforeground="white", relief="flat", bd=0,
            font=tkfont.Font(family=FONT, size=11, weight="bold"),
            cursor="hand2", pady=px(8),
        )
        self.record_btn.pack(side="top", fill="x", padx=PAD, pady=(px(10), 0))

        # ---- 波形区 ----
        wave_frame = tk.Frame(self.root, bg=BG_CARD, height=px(44),
                              highlightthickness=1, highlightbackground=BORDER)
        wave_frame.pack(side="top", fill="x", padx=PAD, pady=(px(8), 0))
        wave_frame.pack_propagate(False)

        self.wave_canvas = tk.Canvas(wave_frame, bg=BG_CARD, highlightthickness=0)
        self.wave_canvas.pack(fill="both", expand=True)

        # ---- 状态行 ----
        status_row = tk.Frame(self.root, bg=BG)
        status_row.pack(side="top", fill="x", padx=PAD, pady=(px(6), 0))

        self.status_label = tk.Label(
            status_row, text=self._status_text, bg=BG, fg=FG_DIM, anchor="w",
            font=tkfont.Font(family=FONT, size=8),
        )
        self.status_label.pack(side="left")

        self.timer_label = tk.Label(
            status_row, text="0.0s", bg=BG, fg=FG_DIM, anchor="e",
            font=tkfont.Font(family=MONO, size=8),
        )
        self.timer_label.pack(side="right")

        # ---- 模型切换 ----
        model_row = tk.Frame(self.root, bg=BG)
        model_row.pack(side="top", fill="x", padx=PAD, pady=(px(6), 0))

        tk.Label(model_row, text="模型", bg=BG, fg=FG_DIM,
                 font=tkfont.Font(family=FONT, size=8)).pack(side="left")

        self.model_var = tk.StringVar(value=self.controller.config.model)
        # Radiobutton 在创建时会把 variable 设成自己的值,连带触发 command。
        # 用这个标志守卫,避免初始化时就误触发模型切换。
        self._initializing = True
        for tag in ("0.6B", "1.7B"):
            rb = tk.Radiobutton(
                model_row, text=tag, value=tag, variable=self.model_var,
                command=self._on_model_change, bg=BG, fg=FG_DIM,
                selectcolor=BG, activebackground=BG, activeforeground=FG,
                font=tkfont.Font(family=FONT, size=8),
                indicatoron=False, padx=px(3),
            )
            rb.pack(side="left")
        self._initializing = False

        tk.Label(model_row, text=f"热键 {self.controller.config.hotkey}",
                 bg=BG, fg=FG_DIM, font=tkfont.Font(family=FONT, size=8)
                 ).pack(side="right")

        # ---- 分隔线 ----
        tk.Frame(self.root, bg=BORDER, height=1).pack(
            side="top", fill="x", padx=PAD, pady=(px(8), 0))

        # ---- 结果预览 ----
        self.result_label = tk.Label(
            self.root, text="", bg=BG, fg=FG, anchor="nw", justify="left",
            wraplength=self.W - 2 * PAD,
            font=tkfont.Font(family=FONT, size=8),
        )
        self.result_label.pack(side="top", fill="x", padx=PAD, pady=(px(6), PAD))

        # 拖动:标题栏可以拖
        for widget in (header, self.title_label):
            widget.bind("<Button-1>", self._on_drag_start)
            widget.bind("<B1-Motion>", self._on_drag_move)
            widget.bind("<B1-Motion>", self._on_drag_move)

    # ---------- 几何 ----------

    def _apply_geometry(self, geometry: str) -> None:
        """应用窗口位置尺寸。

        保存的几何值是逻辑像素(见 _save_geometry),这里乘回缩放系数。
        如果宽高跟当前缩放对不上(比如换了显示器缩放率),就丢弃旧值、
        用默认位置 —— 宁可重新摆一次,也不要给一个错位的窗口。
        """
        if geometry:
            try:
                parts = [int(x) for x in geometry.split(",")]
                if len(parts) == 4:
                    x, y, w, h = parts
                    phys_w = int(w * self._scale)
                    phys_h = int(h * self._scale)
                    # 宽高偏差在 40% 以内认为是同一缩放下的旧值,可用
                    if (abs(phys_w - self.W) <= self.W * 0.4
                            and abs(phys_h - self.H) <= self.H * 0.4):
                        self.root.geometry(
                            f"{phys_w}x{phys_h}+{int(x * self._scale)}+"
                            f"{int(y * self._scale)}")
                        self.root.update_idletasks()
                        return
                    log.info("保存的窗口尺寸 %dx%d 与当前缩放不匹配(应为 %dx%d),改用默认位置",
                             phys_w, phys_h, self.W, self.H)
            except (ValueError, tk.TclError):
                pass

        # 默认:屏幕右下角
        sw = self.root.winfo_screenwidth()
        sh = self.root.winfo_screenheight()
        margin_x = int(30 * self._scale)
        margin_y = int(60 * self._scale)
        self.root.geometry(
            f"{self.W}x{self.H}+{sw - self.W - margin_x}+{sh - self.H - margin_y}")
        self.root.update_idletasks()

    def _save_geometry(self) -> None:
        """保存窗口位置。

        存的是**逻辑像素**(物理像素除以缩放系数),这样换一台缩放率不同的
        显示器时,窗口大小还能自动适配。
        """
        try:
            s = self._scale
            x = int(self.root.winfo_x() / s)
            y = int(self.root.winfo_y() / s)
            w = int(BASE_W)
            h = int(BASE_H)
            self.controller.config.panel_geometry = f"{x},{y},{w},{h}"
            self.controller.config.save()
        except Exception:
            pass

    def _on_drag_start(self, event) -> None:
        self._drag_offset = (event.x_root - self.root.winfo_x(),
                             event.y_root - self.root.winfo_y())

    def _on_drag_move(self, event) -> None:
        x = event.x_root - self._drag_offset[0]
        y = event.y_root - self._drag_offset[1]
        self.root.geometry(f"+{x}+{y}")

    # ---------- 交互 ----------

    def _on_record_click(self) -> None:
        self.controller.toggle_recording()

    def _on_model_change(self) -> None:
        if self._initializing:
            return
        tag = self.model_var.get()
        if tag != self.controller.engine.model_tag:
            self.controller.switch_model(tag)

    # ---------- 事件与刷新 ----------

    def _on_event(self, event: UIEvent) -> None:
        """控制器线程调用,这里只投递,实际更新在主线程。"""
        self._events.put(event)

    def _poll_events(self) -> None:
        try:
            while True:
                event = self._events.get_nowait()
                self._apply_event(event)
        except queue.Empty:
            pass
        self.root.after(40, self._poll_events)

    def _apply_event(self, event: UIEvent) -> None:
        if event.kind == "state" and event.state is not None:
            self._state = event.state
            self._refresh_button()
        elif event.kind == "result":
            self._last_result = event.text
            self.result_label.config(text=self._last_result, fg=FG)
            self._set_status("已输入", OK)
        elif event.kind == "error":
            self._set_status(event.text, REC)
        elif event.kind == "info":
            self._set_status(event.text, FG_DIM)
        elif event.kind == "model" and event.model_tag:
            self.model_var.set(event.model_tag)

    def _set_status(self, text: str, color: str = FG_DIM) -> None:
        self._status_text = text
        self.status_label.config(text=text, fg=color)

    def _refresh_button(self) -> None:
        if self._state == State.RECORDING:
            self.record_btn.config(text="■  停止录音", bg=REC, activebackground="#c93c41")
            self.state_dot.config(fg=REC)
        elif self._state == State.PROCESSING:
            self.record_btn.config(text="识别中 ...", bg="#3a3d4a", activebackground="#3a3d4a")
            self.state_dot.config(fg=WARN)
        else:
            self.record_btn.config(text="●  开始录音", bg=ACCENT, activebackground="#3d6ae8")
            self.state_dot.config(fg=OK if self._state == State.IDLE else WARN)

    def _tick(self) -> None:
        """每 100ms 刷新波形与计时。"""
        if self._state == State.RECORDING and self.controller.recorder is not None:
            self._levels = self.controller.recorder.levels()
            self._elapsed = self.controller.recorder.elapsed
            self.timer_label.config(text=f"{self._elapsed:.1f}s")
        else:
            if self._levels:
                self._levels = []
            if self._state != State.RECORDING:
                self.timer_label.config(text="0.0s")

        self._draw_wave()
        self.root.after(100, self._tick)

    def _draw_wave(self) -> None:
        canvas = self.wave_canvas
        canvas.delete("all")
        w = canvas.winfo_width() or self.W
        h = canvas.winfo_height() or int(42 * self._scale)
        mid = h / 2

        if not self._levels:
            canvas.create_line(0, mid, w, mid, fill="#3a3d4a", width=1)
            return

        n = len(self._levels)
        step = w / max(n - 1, 1)
        bar_w = max(1.5 * self._scale, step * 0.6)
        peak = max(self._levels) or 1.0
        color = REC if self._state == State.RECORDING else ACCENT
        for i, level in enumerate(self._levels):
            # 开方压缩动态范围,让小声也看得见
            norm = min(1.0, (level / peak) ** 0.5)
            bar_h = max(1.0 * self._scale, norm * (h - 4 * self._scale))
            x = i * step
            canvas.create_rectangle(
                x, mid - bar_h / 2, x + bar_w, mid + bar_h / 2,
                fill=color, outline="",
            )

    # ---------- 生命周期 ----------

    def show(self) -> None:
        self.root.deiconify()
        self.root.lift()

    def hide(self) -> None:
        self.root.withdraw()

    def toggle(self) -> None:
        if self.root.state() == "withdrawn":
            self.show()
        else:
            self.hide()

    def destroy(self) -> None:
        try:
            self._save_geometry()
        except Exception:
            pass
        try:
            self.root.destroy()
        except Exception:
            pass

    def mainloop(self) -> None:
        self.root.mainloop()
