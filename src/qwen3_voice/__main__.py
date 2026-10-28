"""应用入口。

线程分工:
  主线程      tkinter 面板的 mainloop(所有 UI 操作必须在这里)
  hotkey 线程 RegisterHotKey + GetMessage 循环(Win32 消息队列是线程局部的)
  tray 线程   pystray 自己的消息循环
  model/mic   后台初始化
  asr 线程    识别与注入

热键线程触发时不能直接碰 tkinter,所以统一走控制器的订阅机制,
由面板的主线程轮询事件队列。
"""

from __future__ import annotations

import sys
import threading

from .config import load_config
from .core.controller import State, VoiceController
from .logging_setup import get_logger, setup
from .paths import config_path
from .win32.dpi import set_dpi_awareness
from .win32.hotkey import HotkeyError, HotkeyListener


def main(argv: list[str] | None = None) -> int:
    argv = argv if argv is not None else sys.argv[1:]

    # 必须在创建任何窗口之前声明 DPI 感知,否则高分屏下布局全乱
    dpi_mode = set_dpi_awareness()

    config = load_config()
    log = setup(config.log_file, config.log_level)
    logger = get_logger("main")

    logger.info("=" * 60)
    logger.info("Qwen3 语音输入启动  热键=%s  模型=%s  DPI=%s",
                config.hotkey, config.model, dpi_mode)

    controller = VoiceController(config)
    controller.start()

    # ---- 面板(主线程) ----
    from .ui.panel import VoicePanel

    panel = VoicePanel(controller, config.panel_geometry)
    if config.panel_visible:
        panel.show()
    else:
        panel.hide()

    # ---- 托盘(独立线程) ----
    from .ui.tray import TrayIcon

    tray = TrayIcon(controller, panel=panel, config_path=config_path())

    def run_tray():
        try:
            tray.start()
        except Exception as e:
            logger.error("托盘启动失败: %s", e)

    tray_thread = threading.Thread(target=run_tray, name="tray", daemon=True)
    tray_thread.start()

    # ---- 全局热键(独立线程) ----
    def on_hotkey():
        logger.debug("热键触发")
        controller.toggle_recording()

    hotkey = HotkeyListener(config.hotkey, on_hotkey)
    try:
        hotkey.start()
    except HotkeyError as e:
        logger.error("%s", e)
        logger.error("请在配置里换一个不冲突的组合键")
        panel._set_status("热键注册失败", "#e5484d")
        panel.show()

    logger.info("已就绪。按 %s 开始录音,再按一次结束。", config.hotkey)

    try:
        panel.mainloop()
    except KeyboardInterrupt:
        pass
    finally:
        hotkey.stop()
        controller.shutdown()
        panel.destroy()
        logger.info("退出")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
