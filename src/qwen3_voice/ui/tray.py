"""系统托盘图标。

托盘与面板共享同一个控制器:
  - 托盘图标点击 -> 显示/隐藏面板
  - 托盘菜单   -> 显示面板 / 重启 / 退出 / 打开配置
"""

from __future__ import annotations

from pathlib import Path

from ..core.controller import State, VoiceController
from ..logging_setup import get_logger

log = get_logger("ui.tray")

BG_IDLE = (70, 70, 70, 255)
BG_REC = (200, 40, 40, 255)
BG_WARN = (210, 160, 30, 255)
FG = (255, 255, 255, 255)


def make_icon(recording: bool = False, ready: bool = True):
    """生成托盘图标。"""
    from PIL import Image, ImageDraw

    size = 64
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    if recording:
        d.ellipse([2, 2, size - 2, size - 2], fill=BG_REC)
    elif ready:
        d.ellipse([2, 2, size - 2, size - 2], fill=BG_IDLE)
    else:
        d.ellipse([2, 2, size - 2, size - 2], fill=BG_WARN)

    # 麦克风
    d.rounded_rectangle([28, 12, 36, 34], radius=4, fill=FG)
    d.arc([21, 24, 43, 46], start=0, end=180, fill=FG, width=3)
    d.line([32, 42, 32, 52], fill=FG, width=3)
    return img


class TrayIcon:
    """托盘图标封装。"""

    def __init__(self, controller: VoiceController, panel=None,
                 config_path: Path | None = None):
        self.controller = controller
        self.panel = panel
        self.config_path = config_path
        self.icon = None
        # 用户点了「重启」:main() 在全部清理完成后检查它,
        # 由入口负责拉起新实例(见 __main__.py 的 _spawn_restart)
        self.restart_requested = False

    def start(self) -> None:
        import pystray

        menu = pystray.Menu(
            pystray.MenuItem(
                "显示 / 隐藏面板", self._toggle_panel, default=True),
            pystray.MenuItem("开始 / 停止录音",
                             lambda: self.controller.toggle_recording()),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("重新探测麦克风", self._reload_mic),
            pystray.MenuItem("打开配置文件", self._open_config),
            pystray.MenuItem("查看日志", self._open_log),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("重启", self._restart),
            pystray.MenuItem("退出", self._quit),
        )
        self.icon = pystray.Icon("qwen3_voice", make_icon(), "Qwen3 语音输入", menu)
        self.controller.subscribe(self._on_event)
        self.icon.run()

    def _on_event(self, event) -> None:
        if self.icon is None:
            return
        try:
            if event.kind == "state":
                recording = event.state == State.RECORDING
                ready = event.state == State.IDLE
                self.icon.icon = make_icon(recording, ready)
                self.icon.title = {
                    State.RECORDING: "Qwen3 语音输入 - 正在录音",
                    State.PROCESSING: "Qwen3 语音输入 - 识别中",
                    State.IDLE: "Qwen3 语音输入 - 待机",
                    State.STARTING: "Qwen3 语音输入 - 启动中",
                    State.ERROR: "Qwen3 语音输入 - 出错",
                }.get(event.state, "Qwen3 语音输入")
        except Exception:
            pass

    def _toggle_panel(self, *_args) -> None:
        if self.panel is not None:
            self.panel.toggle()

    def _reload_mic(self, *_args) -> None:
        from ..audio.devices import choose_device

        index, desc = choose_device(
            self.controller.config.input_device,
            self.controller.config.device_name_hints,
        )
        self.controller.config.input_device = index
        self.controller.config.save()
        log.info("重新探测麦克风: %s", desc)

    def _open_config(self, *_args) -> None:
        import os

        if self.config_path and Path(self.config_path).is_file():
            os.startfile(str(self.config_path))

    def _open_log(self, *_args) -> None:
        import os

        p = Path(self.controller.config.log_file)
        if p.is_file():
            os.startfile(str(p))

    def _restart(self, *_args) -> None:
        """重启:置位请求,然后走和退出**完全一样**的清理流程。

        新实例**不能在这里拉**:此刻热键还没注销、窗口还没销毁 ——
        拉早了新实例注册热键就撞车("快速重启注册失败"就是这么来的)。
        main() 在所有清理(热键注销 → 窗口销毁 → 主循环返回)做完之后
        检查 ``restart_requested``,由它负责拉起新实例。
        """
        self.restart_requested = True
        log.info("托盘请求重启")
        self._quit()

    def quit(self, *_args) -> None:
        """公开的退出入口:悬浮球右键菜单的「退出」也走这条清理路径
        (和托盘「退出」完全一样:热键注销 → 窗口销毁 → 主循环返回)。"""
        self._quit()

    def _quit(self, *_args) -> None:
        try:
            self.controller.shutdown()
        finally:
            # 必须销毁窗口:webview.start() 是主线程阻塞点,窗口不关
            # 主循环就不返回,进程会变成"托盘没了但还活着"的僵尸。
            if self.panel is not None:
                try:
                    self.panel.destroy()
                except Exception:
                    pass
            if self.icon is not None:
                self.icon.stop()
