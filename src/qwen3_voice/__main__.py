"""应用入口。

线程分工:
  主线程      调 webview.start() 并等它返回(winforms 后端会把 GUI
              消息循环放到它自己起的 STA 线程上,主线程只是阻塞等待)
  hotkey 线程 RegisterHotKey + GetMessage 循环(Win32 消息队列是线程局部的)
  tray 线程   pystray 自己的消息循环
  model/mic   后台初始化
  asr 线程    识别与注入
  ui-push     面板的推送线程,把控制器事件转成前端调用(面板 + 胶囊)

热键线程触发时不能直接碰 UI,所以统一走控制器的订阅机制,
由面板的推送线程接力。

两个窗口共用**一次** webview.start() —— pywebview 支持多窗口,
但主循环只有一个:
  - 面板(master):配置窗口,常驻与否由配置决定
  - 胶囊(child):录音反馈的悬浮窗,录音时才亮出来
"""

from __future__ import annotations

import sys
import threading

from .config import load_config
from .core.controller import VoiceController
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

    # ---- 窗口(主线程) ----
    # 记住启动时的前台窗口:pywebview 建 hidden 窗口时会偷偷 Show 一次,
    # 那一下会抢走用户的键盘焦点(正在打字就会丢字),建完还得还回去。
    from .win32.api import get_foreground_window

    startup_focus = get_foreground_window()

    # 顺序有讲究:第一个 create_window 的窗口是 pywebview 的 master,
    # 它的 GUI 循环跑在 pywebview 自己起的 STA 线程上;后面的窗口由
    # pywebview 另起一个线程创建。两个窗口都会在 start() 里被创建。
    from .ui.panel import VoicePanel
    from .ui.capsule import VoiceCapsule

    panel = VoicePanel(controller, config.panel_geometry, focus_restore=startup_focus)
    # 胶囊是第二个窗口(录音时才显示),但**必须**在 start() 之前建好,
    # 否则 pywebview 不会把它纳入多窗口管理
    capsule = VoiceCapsule(controller, focus_restore=startup_focus)
    # 推送线程住在面板里(只有一条推送线程,顺序才靠得住),
    # 胶囊作为第二个推送目标挂上去
    panel.add_sink(capsule)

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
        # 面板瘦身后没有状态行了,启动期的致命错误用系统对话框报 ——
        # 托盘图标上的提示太容易被忽略
        from .win32.api import show_error_box

        show_error_box(f"{e}\n\n请在配置里换一个不冲突的组合键。")
        panel.show()

    logger.info("已就绪。按 %s 开始录音,再按一次结束。", config.hotkey)

    try:
        panel.mainloop()
    except KeyboardInterrupt:
        pass
    finally:
        hotkey.stop()
        controller.shutdown()
        # 两个窗口都要销毁:pywebview 的主循环要等所有窗口都没了才返回,
        # 只关一个的话进程会变成"托盘没了但还活着"的僵尸
        panel.destroy()
        capsule.destroy()
        logger.info("退出")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
