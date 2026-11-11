"""应用入口。

线程分工:
  主线程        调 webview.start() 并等它返回(GUI 消息循环在它自己
                起的 STA 线程上,主线程只是阻塞等待)
  host-hotkey   Win32 热键线程(消息队列是线程局部的)
  host-tray     pystray 自己的消息循环
  model-load    后台加载识别后端
  mic-warmup    后台探测麦克风
  asr-run       识别与注入
  asr-preview   实时预览(喂预览位引擎)
  core-levels   30fps 电平推送

装配链(顺序有讲究,每一步都不能换位置):

  1. DPI 感知      —— 必须在**任何窗口**创建之前,否则高分屏下布局全乱
  2. 剪贴板 owner —— 必须在主线程建,owner 窗口归创建它的线程
  3. 配置 + 日志   —— 之后才有地方写日志
  4. 通知出口      —— notify 的日志出口(拒绝/失败路径都要落到它)
  5. controller    —— 业务核心
  6. presenter     —— 把 core 的状态/通知翻译成胶囊的动作
  7. 胶囊窗口      —— 必须在 webview.start() **之前**建好
  8. 面板窗口      —— 同上(pywebview 的多窗口约束:start 之后不能再建窗)
  9. 托盘 / 热键   —— 独立线程
 10. 引擎启动      —— 后台加载模型,主循环不阻塞

窗口有两个:**胶囊**(host/capsule.py,录音时的浮层)和**设置面板**
(host/panel.py,配置窗口)。面板默认**不常驻** —— 托盘菜单或球菜单
才打开,关掉只是隐藏,不退出程序。

退出收尾顺序:**先停窗口推送,再注销热键**。热键是全局资源,占着
不放的话,用户"退出后马上重启"会因为注册冲突而失败(热键注销要等
进程真正退出);而窗口推送停晚了,退出过程中还会往一个已经没人
看的窗口里推东西。
"""

from __future__ import annotations

import argparse
import os
import sys
import threading
import time
from pathlib import Path
from typing import NamedTuple

from .config import Config, load_config
from .core.controller import CoreController
from .core.state import AppState
from .logging_setup import get_logger, setup
from .notify import Notification, notify
from .paths import PROJECT_DIR, config_path, data_dir, ensure_dirs, webview_dir

# 等引擎就绪的秒数上限(无头模式用)
DEFAULT_READY_TIMEOUT = 300.0


class _Entry(NamedTuple):
    """「当初是怎么被拉起来的」—— 重启必须原样复刻它。

    argv0  入口的 argv[0](已展开成绝对路径):
            - 脚本入口(桌面快捷方式走的就是这条)→ 那个 .pyw 的路径
            - ``-m qwen3_voice``                   → 包里的 __main__.py
    frozen 是否是 PyInstaller 打包出来的 exe
    """

    argv0: str | None
    frozen: bool


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="qwen3-voice",
        description="全局语音输入 —— 按热键(或点悬浮球)说话,文字注入当前光标位置。",
    )
    parser.add_argument(
        "--headless",
        action="store_true",
        help="无头模式:不启动 GUI,只装配 core,等引擎就绪后干净退出",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=DEFAULT_READY_TIMEOUT,
        help="等待引擎加载完成的秒数上限(默认 %(default)s)",
    )
    return parser


def _capture_entry() -> _Entry:
    """在启动早期把入口信息记下来。

    必须趁早取:后面 GUI 库有可能动 sys.argv,那时候再问就不准了。

    相对路径在这里就展开成绝对路径 —— 到退出时工作目录可能已经变了,
    那时再解析相对路径就会指向别的地方。
    """
    argv = getattr(sys, "argv", None) or []
    argv0 = argv[0] if argv else None
    if argv0:
        try:
            argv0 = str(Path(argv0).resolve())
        except (OSError, RuntimeError):
            argv0 = None
    return _Entry(argv0=argv0, frozen=bool(getattr(sys, "frozen", False)))


def _project_root_of(path: Path) -> Path:
    """从入口反推项目根(向上找带 pyproject.toml 的那一级)。

    桌面快捷方式的「起始于」就是项目根,重启时的工作目录要跟它一致。
    找不到标记文件就退回 paths.PROJECT_DIR —— 包自己知道自己在哪。
    """
    for d in (path.parent, *path.parent.parents):
        if (d / "pyproject.toml").is_file():
            return d
    return PROJECT_DIR


def _restart_plan(entry: _Entry) -> tuple[list[str], str, str | None]:
    """算出重启该用的命令行、工作目录,以及要提醒用户的话。

    原则:**忠实复刻当初的启动方式**。四种入口分别对应:
      - 脚本(launch.pyw):[同一个解释器, 同一个脚本的绝对路径]
      - ``-m qwen3_voice``:argv0 是包里的 __main__.py → 保持 ``-m``
      - 冻结 exe(PyInstaller):[exe 自己]
      - 薄壳 exe(runtime\\bin\\Qwen3Voice.exe):[exe 自己,不带任何参数]

    为什么不能一律 ``-m``:解释器可能压根看不见这个包 ——
    桌面快捷方式用的是 base 目录的 pythonw.exe,而 qwen3_voice 只装在
    venv 里(base 下 find_spec 返回 None,实测),拿 base 去 ``-m`` 只会
    ModuleNotFoundError;再叠上 pythonw 没有控制台 → 静默闪退。
    """
    if entry.frozen:
        # 打包成 exe 之后入口就是 exe 自己,再起一个自己就行
        exe = Path(sys.executable)
        return [sys.executable], str(exe.parent), None

    argv0 = entry.argv0
    if argv0:
        script = Path(argv0)
        if script.name == "__main__.py":
            # ``python -m qwen3_voice``:解释器本来就看得见包,保持 -m
            return ([sys.executable, "-m", "qwen3_voice"],
                    str(_project_root_of(script)), None)

        if script.suffix.lower() == ".exe":
            # 薄壳入口(runtime\bin\Qwen3Voice.exe):这个 exe **自己**就是
            # 跑 Python 的进程(CPython 被装进了它),所以重启只要再起一个
            # 自己,而且**不能带参数** ——
            #   Qwen3Voice.exe -m qwen3_voice 是错的:壳根本不转发参数给
            #   Python,那样起来的新实例会当成有参数而行为异常。
            # exe 的父目录(runtime\)里放着 pyvenv.cfg 和 launcher.toml,
            # 新实例照样找得到 venv,工作目录取它的上一级即可。
            exe = Path(sys.executable)
            if exe.suffix.lower() != ".exe":
                # 理论上不会走到这(sys.executable 就是这个壳),兜底用 argv0
                exe = script
            return ([str(exe)], str(exe.parent.parent), None)

        if script.suffix.lower() in (".py", ".pyw"):
            if script.is_file():
                # 脚本入口(桌面快捷方式走的就是这条):同一个解释器
                # + 同一个脚本 —— 脚本自己会挂 venv,等于完整复刻
                return ([sys.executable, str(script)],
                        str(_project_root_of(script)), None)
            return ([sys.executable, "-m", "qwen3_voice"], str(PROJECT_DIR),
                    f"入口脚本 {script} 不存在,退回 -m qwen3_voice")

        return ([sys.executable, "-m", "qwen3_voice"], str(PROJECT_DIR),
                f"入口 {script} 不是 .py/.pyw 脚本,退回 -m qwen3_voice")

    return ([sys.executable, "-m", "qwen3_voice"], str(PROJECT_DIR),
            "认不出当初的启动方式,退回 -m qwen3_voice")


def _spawn_restart(logger, entry: _Entry | None = None) -> None:
    """以分离方式拉起一个新的自身实例(重启的第二步)。

    调用时机有讲究:必须在**全部清理之后**(热键已注销、窗口已销毁、
    模型已卸载)—— 新实例一上来就要注册同一个热键、往同一块显存里
    加载模型,任何一样没让位都会撞车。所以它放在 main() 的最后,
    而不是托盘回调里。

    分离(DETACHED_PROCESS):新进程不继承控制台、不挂在旧进程上 ——
    旧进程退出后它照常活,终端里 Ctrl+C 也只影响旧进程。

    命令行必须**忠实复刻当初的启动方式**(见 _restart_plan):
    换一种方式拉起来,新进程可能根本起不来,而且是静默地起不来。
    """
    import subprocess

    if entry is None:
        entry = _capture_entry()
    cmd, cwd, note = _restart_plan(entry)
    if note:
        logger.warning("重启:%s", note)

    flags = 0
    if sys.platform == "win32":
        flags = (subprocess.DETACHED_PROCESS
                 | subprocess.CREATE_NEW_PROCESS_GROUP)
    try:
        subprocess.Popen(cmd, cwd=cwd, creationflags=flags, close_fds=True)
        logger.info("重启:已拉起新实例(%s)", " ".join(cmd))
    except Exception as e:
        logger.error("重启失败,请手动启动程序: %s", e)


def make_qr_scanner_launcher():
    """构造「拉起二维码扫描附属工具」的回调(球菜单「二维码扫描」用)。

    附属工具住在 companions/qr_scanner,**架构上完全独立**:它不
    import 本包,我们只负责把它作为**独立进程**拉起 —— 不等它、
    不看它的输出、不管它的死活;它崩了不影响语音主程序,依赖缺失时
    它自己会弹窗报告。

    拉起用的是**本环境真正的 python.exe**(_python_exe_for_children):
    拉起附属工具就是拉起一个真正的运行时进程,跟壳无关 —— 壳的职责
    只是"以 Qwen3Voice.exe 的映像名跑主程序",永远不会参与这件事
    (工具以后换成 Node/Bash 也一样,壳不可能也不应该兼容所有运行时)。
    """
    import subprocess

    tool_dir = PROJECT_DIR / "companions" / "qr_scanner"
    logger = get_logger("main")  # main() 里的 logger 是局部变量,这里自己拿

    def launch() -> None:
        if not (tool_dir / "qr_scanner" / "__main__.py").is_file():
            logger.error("二维码扫描工具不见了:%s", tool_dir)
            _warn_dialog(
                "二维码扫描工具缺失:\n{}\n\n可能是不完整的仓库拷贝。".format(tool_dir)
            )
            return
        python_exe = _python_exe_for_children()
        if python_exe is None:
            logger.error(
                "拉不起二维码扫描:认不出本环境的 python.exe(sys.executable=%s)",
                sys.executable,
            )
            _warn_dialog(
                "拉不起二维码扫描:认不出本环境的 python.exe。\n"
                "(sys.executable = {})\n\n"
                "薄壳场景需要 runtime\\launcher.toml 的 venv_site_packages "
                "指向真实 venv,且该 venv 的 Scripts\\python.exe 存在。".format(
                    sys.executable
                )
            )
            return
        try:
            # CREATE_NO_WINDOW:python.exe 是控制台程序,从 GUI 进程
            # 拉起会闪黑框,压掉。cwd 指到工具目录,-m 才找得到包。
            subprocess.Popen(
                [str(python_exe), "-m", "qr_scanner"],
                cwd=str(tool_dir),
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                close_fds=True,
            )
            logger.info("二维码扫描:已拉起附属工具(%s, %s)", python_exe, tool_dir)
        except OSError as e:
            logger.error("拉起二维码扫描工具失败: %s", e)
            _warn_dialog(f"拉起二维码扫描工具失败:\n{e}")

    return launch


def _python_exe_for_children(exe: str | None = None) -> Path | None:
    """返回本环境**真正的解释器**,拉起附属子进程时用;认不出返回 None。

    为什么不能直接拿 sys.executable:薄壳场景(runtime\\bin\\Qwen3Voice.exe)
    它指向**壳自己** —— 嵌入式初始化按进程映像推导,壳不转发参数,
    拉起它只会得到第二个主程序(实测:点「二维码扫描」弹出的是新悬浮球)。

    定位(两条路,认不出就 None):
    - sys.executable 本来就是 python*.exe(脚本 / ``-m`` 入口)→ 直接用。
      注意:脚本入口若用 base 解释器挂 venv,子进程会缺依赖 —— 工具自己
      会弹窗报告,不算静默故障。
    - 薄壳场景:从壳旁的 launcher.toml(venv_site_packages)反推真 venv:
      site-packages 的上两级是 venv 根,python.exe 在 Scripts\\ 下。
      这条配置正是壳挂载主程序依赖用的「真实 venv」,同源,不是猜。

    ``exe`` 参数给测试用:模拟薄壳场景时传壳的路径进来。
    """
    exe_path = Path(sys.executable if exe is None else exe)
    if exe_path.stem.lower() in ("python", "pythonw"):
        return exe_path

    site_pkgs = (
        os.environ.get("QWEN3_VOICE_VENV_SITE", "").strip()
        or _launcher_toml_site_packages(exe_path.parent.parent / "launcher.toml")
    )
    if not site_pkgs:
        return None
    candidate = Path(site_pkgs).parent.parent / "Scripts" / "python.exe"
    return candidate if candidate.is_file() else None


def _launcher_toml_site_packages(toml_path: Path) -> str:
    """读 launcher.toml 的 venv_site_packages(壳同款配置键);读不到返回空串。"""
    import tomllib

    try:
        with open(toml_path, "rb") as f:
            value = tomllib.load(f).get("venv_site_packages", "")
    except (OSError, tomllib.TOMLDecodeError):
        return ""
    return value.strip() if isinstance(value, str) else ""


def _warn_dialog(text: str) -> None:
    """附属工具拉不起来的提示(和启动期致命错误同一个出口,必须让人看见)。"""
    try:
        from .win32.api import show_error_box

        show_error_box(text)
    except Exception:
        pass  # 连弹窗都起不来,日志里那份就是全部了


def _print_state(state: AppState) -> None:
    health = state.backend_health
    detail = f" ({health.reason})" if health.reason else ""
    print(f"[状态] {state.state} | 后端={state.backend} | "
          f"健康={health.status}{detail} | 模型={state.model or '-'}")
    if state.preview_text:
        print(f"[预览] {state.preview_text}")


def _print_notification(note: Notification) -> None:
    print(f"[通知] {note.level} | {note.kind} | {note.message}")


def _open_config_file() -> None:
    """用系统默认程序打开配置文件(V1 托盘的同名功能)。"""
    path = config_path()
    try:
        if Path(path).is_file():
            os.startfile(str(path))
    except Exception as e:
        get_logger("main").error("打开配置文件失败: %s", e)


def _open_log_file(config: Config) -> None:
    """用系统默认程序打开日志文件(V1 托盘的同名功能)。"""
    try:
        path = Path(config.log_file)
        if path.is_file():
            os.startfile(str(path))
    except Exception as e:
        get_logger("main").error("打开日志失败: %s", e)


def run_gui(config: Config, entry: _Entry) -> int:
    """GUI 模式:装配整条链,起 webview 主循环。"""
    log = get_logger("main")

    from .apps.panel.presenter import PanelPresenter
    from .apps.voicebar.presenter import VoiceBarPresenter
    from .host.capsule import VoiceCapsule
    from .host.hotkey import HotkeyError, HotkeyManager
    from .host.panel import VoicePanel
    from .host.tray import Tray, TrayMenuItem
    from .win32.api import get_foreground_window
    from .win32.dpi import set_dpi_awareness

    # 1) DPI 感知 —— 必须在创建任何窗口之前
    dpi_mode = set_dpi_awareness()
    log.info("DPI 感知: %s", dpi_mode)

    # 2) 剪贴板 owner 窗口:**必须在主线程创建**(owner 窗口归创建它的
    # 线程,线程退出窗口就没了,而 Windows 在 owner 窗口销毁时会
    # 连带清空剪贴板 —— 写在录音线程上会把"识别结果留在剪贴板"
    # 这个兜底直接废掉)。这里趁主线程还空闲时建好,活到进程结束。
    from .win32 import clipboard

    clipboard.ensure_owner_window()

    # 3) 通知出口 —— 拒绝/失败路径都落到它(data/notify.log)
    log.info("通知出口: %s", data_dir() / "notify.log")

    # 4) controller + 5) presenter
    controller = CoreController(config, notifier=notify)
    presenter = VoiceBarPresenter(core=controller, config=config)
    presenter.install()

    # 记住启动时的前台窗口:pywebview 建 hidden 窗口时会偷偷 Show 一次,
    # 那一下会抢走用户的键盘焦点(正在打字就会丢字),建完还得还回去。
    startup_focus = get_foreground_window()

    # 6) 胶囊窗口 —— 必须在 webview.start() **之前**建好:
    # pywebview 才会把它纳入多窗口管理,before_show/shown 回调才注册得上。
    capsule = VoiceCapsule(presenter, config=config, focus_restore=startup_focus)
    presenter.attach(capsule)

    hotkey = HotkeyManager(notifier=notify)
    tray = Tray(notifier=notify, title="Qwen3 语音输入")

    # 退出请求:三条路(托盘菜单 / 球菜单 / 面板的「退出」按钮)都汇到这里。
    flags = {"restart": False}

    def request_quit() -> None:
        """请求退出:先停窗口推送,webview 主循环随之返回。"""
        log.info("收到退出请求")
        _shutdown_and_exit(presenter, controller, hotkey, tray, capsule, panel, panel_presenter)

    def request_restart() -> None:
        """请求重启:置位标记后走**同一条**退出路径。

        新实例**不能在这里拉**:此刻热键还没注销、窗口还没销毁 ——
        拉早了新实例注册热键就撞车("快速重启注册失败"就是这么来的)。
        main() 在所有清理做完之后检查标记,由它负责拉起新实例。
        """
        flags["restart"] = True
        log.info("托盘请求重启")
        request_quit()

    def reload_mic() -> None:
        """重新探测麦克风(V1 托盘的同名功能)。"""
        from .audio.devices import choose_device

        index, desc = choose_device(
            config.input_device,
            config.device_hints,
        )
        config.input_device = index
        config.save()
        log.info("重新探测麦克风: %s", desc)

    # 7) 面板窗口 —— 和胶囊一样,必须在 webview.start() **之前**建好:
    # pywebview 的多窗口约束:start 之后不能再 create_window(官方动态
    # 建窗路径会在返回前触发 before_show/shown,拿不到 window 对象)。
    #
    # 面板 presenter 负责翻译 core 的快照/通知;主题那一条由装配层
    # 注入(capsule.set_theme)—— 面板改了主题,胶囊要跟着换。
    # 悬浮球的开关/待机策略同款:注入 voicebar presenter 的
    # apply_ui_mode —— 面板切了界面形态,胶囊那边当场兑现(球收/露)。
    # 退出的收尾路径也在这里注入:面板的「退出」按钮走**同一条**
    # request_quit,和托盘菜单、球菜单一模一样。
    panel_presenter = PanelPresenter(
        core=controller,
        config=config,
        notifier=notify,
        theme_broadcaster=capsule.set_theme,
        ui_mode_broadcaster=presenter.apply_ui_mode,
        quit_handler=request_quit,
    )
    panel_presenter.install()
    panel = VoicePanel(
        presenter=panel_presenter,
        config=config,
        js_api=panel_presenter.api,
        geometry=config.panel_geometry,
        focus_restore=startup_focus,
    )
    panel_presenter.attach(panel)
    # 默认不常驻:面板由托盘菜单/球菜单打开。配置里显式要求常驻
    # 时才启动就显示。
    if config.panel_visible:
        panel.show()

    # 8) 托盘菜单(V1 的项目基本照搬,「显示/隐藏面板」回来了:
    #    面板退役期间这一项去掉了,现在补回来)
    def toggle_panel() -> None:
        """托盘菜单的「设置面板」:显示/隐藏切换。"""
        panel.toggle()

    def open_panel() -> None:
        """球菜单的「设置」:打开设置面板。"""
        panel.show()

    tray.set_menu([
        TrayMenuItem("开始 / 停止录音", key="record",
                     action=lambda: controller.toggle_recording(),
                     default=True),
        TrayMenuItem("设置面板", key="panel", action=toggle_panel),
        TrayMenuItem("重新探测麦克风", key="reload_mic", action=reload_mic),
        TrayMenuItem("打开配置文件", key="open_config",
                     action=_open_config_file),
        TrayMenuItem("查看日志", key="open_log",
                     action=lambda: _open_log_file(config)),
        TrayMenuItem("重启", key="restart", action=request_restart),
        TrayMenuItem("退出", key="quit", action=request_quit),
    ])

    # 球菜单的三个出口:
    #   「设置」—— 打开设置面板(面板退役那阵子这里退到「打开配置文件」)
    #   「二维码扫描」—— 拉起附属工具(独立进程)
    #   「退出」—— 复用托盘同一条清理路径
    capsule.set_panel_opener(open_panel)
    capsule.set_quit_handler(request_quit)
    capsule.set_qr_scanner_launcher(make_qr_scanner_launcher())

    def on_core_state(state: AppState) -> None:
        """托盘图标跟着状态走(V1 的同名逻辑)。"""
        icon_state = {
            "recording": "recording",
            "processing": "warn",
            "starting": "warn",
            "idle": "idle",
        }.get(state.state, "idle")
        tray.set_icon_state(icon_state)

    controller.subscribe(on_core_state)

    # 8) 热键(独立线程)
    def on_hotkey() -> None:
        log.debug("热键触发")
        try:
            controller.toggle_recording()
        except Exception as e:
            log.exception("热键处理异常")
            notify.publish("error", "hotkey_callback_failed",
                           "快捷键触发的动作失败了", reason=str(e))

    hotkey_ok = False
    try:
        hotkey.start()
        hotkey_ok = hotkey.register(config.hotkey, on_hotkey)
    except HotkeyError as e:
        log.error("%s", e)
    if not hotkey_ok:
        log.warning("热键 %s 没注册上,只能用鼠标点球", config.hotkey)

    # 9) 托盘 + 引擎启动
    if not tray.start():
        log.error("托盘启动失败(菜单功能将不可用)")

    controller.start()
    log.info("已就绪。按 %s 开始录音,再按一次结束。", config.hotkey)

    import webview

    try:
        webview.start(func=presenter.start, private_mode=False,
                      storage_path=str(webview_dir()))
    except KeyboardInterrupt:
        log.info("收到 Ctrl+C")
    finally:
        _shutdown_and_exit(presenter, controller, hotkey, tray, capsule, panel, panel_presenter)

    # 托盘的「重启」:走到这里说明所有清理都完成了(热键已注销、
    # 窗口已销毁、模型已卸载),现在拉新实例才不会撞车
    if flags["restart"]:
        _spawn_restart(log, entry)
    return 0


_finalized = {"done": False}


def _shutdown_and_exit(presenter, controller, hotkey, tray, capsule, panel, panel_presenter) -> None:
    """退出收尾(幂等)。所有退出路都走这一条,避免漏步骤。

    顺序:**先停窗口推送,再注销热键**。
    """
    if _finalized["done"]:
        return
    _finalized["done"] = True
    log = get_logger("main")
    t0 = time.monotonic()
    try:
        presenter.shutdown()
    except Exception:
        log.debug("presenter 收尾异常", exc_info=True)
    log.info("退出收尾: presenter %.2fs", time.monotonic() - t0)
    try:
        panel_presenter.shutdown()
    except Exception:
        log.debug("面板 presenter 收尾异常", exc_info=True)
    # 窗口推送先停 —— 推给一个没人看的窗口没有意义,还会拖慢退出。
    t1 = time.monotonic()
    try:
        panel.destroy()
    except Exception:
        log.debug("面板收尾异常", exc_info=True)
    log.info("退出收尾: panel %.2fs", time.monotonic() - t1)
    t1b = time.monotonic()
    try:
        capsule.destroy()
    except Exception:
        log.debug("窗口收尾异常", exc_info=True)
    log.info("退出收尾: capsule %.2fs", time.monotonic() - t1b)
    # 再注销热键 —— 它是全局资源,占着不放的话,"退出后马上重启"
    # 会因为注册冲突而失败。
    t2 = time.monotonic()
    try:
        hotkey.stop()
    except Exception:
        log.debug("热键收尾异常", exc_info=True)
    log.info("退出收尾: hotkey %.2fs", time.monotonic() - t2)
    t3 = time.monotonic()
    try:
        controller.shutdown()
    except Exception:
        log.debug("core 收尾异常", exc_info=True)
    log.info("退出收尾: core %.2fs", time.monotonic() - t3)
    t4 = time.monotonic()
    try:
        tray.stop()
    except Exception:
        log.debug("托盘收尾异常", exc_info=True)
    log.info("退出收尾: tray %.2fs", time.monotonic() - t4)
    t5 = time.monotonic()
    try:
        from .win32 import clipboard

        # 剪贴板 owner 窗口随进程退出被销毁时,Windows 会连带清空
        # 剪贴板 —— 注入失败时留在剪贴板里的识别结果就没了。
        # 退出前先把所有权交还系统(owner 置 NULL),内容留住。
        clipboard.release_ownership()
    except Exception:
        log.debug("剪贴板归还异常", exc_info=True)
    log.info("退出收尾: clipboard %.2fs", time.monotonic() - t5)
    log.info("退出收尾合计 %.2fs", time.monotonic() - t0)
    _flush_logs()


def _flush_logs() -> None:
    import logging

    for handler in logging.getLogger().handlers:
        try:
            handler.flush()
        except Exception:
            pass
    try:
        sys.stdout.flush()
        sys.stderr.flush()
    except Exception:
        pass


def run_headless(config: Config, args) -> int:
    """无头模式:不建任何窗口,只装配 core。

    等引擎就绪(或超时)后**干净退出** —— 这是给自检/冒烟用的,
    不是常驻服务。
    """
    log = get_logger("main")

    from .win32 import clipboard

    clipboard.ensure_owner_window()

    controller = CoreController(config, notifier=notify)
    controller.subscribe(_print_state)
    notify.subscribe(_print_notification)

    controller.start()
    log.info("无头模式已启动(等待引擎就绪,上限 %.0f 秒)", args.timeout)

    try:
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            snapshot = controller.state_snapshot
            health = snapshot.backend_health
            if health.ready:
                print("[无头] 引擎就绪")
                return 0
            if health.failed:
                print(f"[无头] 引擎加载失败: {health.reason}")
                return 2
            time.sleep(0.2)
        print("[无头] 等待引擎就绪超时")
        return 2
    finally:
        controller.shutdown()
        try:
            clipboard.release_ownership()
        except Exception:
            pass


def run_headless_service(config: Config) -> int:
    """无界面模式(ui_mode=headless):常驻服务,不建任何 GUI 窗口。

    与 ``--headless`` 参数的区别:那是**自检**(引擎就绪即退出,冒烟用);
    这是**运行形态** —— 只有托盘和热键,录音/识别/注入照常工作。

    开发者选项(用户拍板):面板上是 disabled 的第四档,只能改 config
    手动开启、重启生效 —— 运行时销毁/重建窗口会踩 pywebview 动态建窗
    的坑(REFACTOR_SPEC 血泪条款 4),不给面板入口。headless 下同样
    **不做任何故障提示 UI**(托盘气泡之类都不做):托盘图标颜色跟着
    状态走是唯一反馈,出问题看日志(data/voice_input.log)。

    托盘保留(它是 headless 下唯一的退出通道 —— GUI 子系统没有控制台,
    Ctrl+C 摸不到)。想切回别的形态:改 config.json 的 ui_mode 再重启。
    """
    log = get_logger("main")

    from .host.hotkey import HotkeyManager
    from .host.tray import Tray, TrayMenuItem
    from .win32 import clipboard

    clipboard.ensure_owner_window()

    controller = CoreController(config, notifier=notify)
    hotkey = HotkeyManager(notifier=notify)
    tray = Tray(notifier=notify, title="Qwen3 语音输入(无界面)")

    def on_core_state(state: AppState) -> None:
        """托盘图标跟着状态走 —— headless 下唯一的状态反馈。"""
        icon_state = {
            "recording": "recording",
            "processing": "warn",
            "starting": "warn",
            "idle": "idle",
        }.get(state.state, "idle")
        tray.set_icon_state(icon_state)

    controller.subscribe(on_core_state)

    stop = threading.Event()

    def request_quit() -> None:
        log.info("收到退出请求")
        stop.set()

    tray.set_menu([
        TrayMenuItem("开始 / 停止录音", key="record",
                     action=lambda: controller.toggle_recording(),
                     default=True),
        TrayMenuItem("打开配置文件", key="open_config",
                     action=_open_config_file),
        TrayMenuItem("查看日志", key="open_log",
                     action=lambda: _open_log_file(config)),
        TrayMenuItem("退出", key="quit", action=request_quit),
    ])

    def on_hotkey() -> None:
        log.debug("热键触发")
        try:
            controller.toggle_recording()
        except Exception as e:
            log.exception("热键处理异常")
            notify.publish("error", "hotkey_callback_failed",
                           "快捷键触发的动作失败了", reason=str(e))

    try:
        hotkey.start()
        if not hotkey.register(config.hotkey, on_hotkey):
            log.warning("热键 %s 没注册上,只能用托盘菜单录音", config.hotkey)
        if not tray.start():
            log.error("托盘启动失败(headless 下没有其它退出通道,只能结束进程)")
        controller.start()
        log.info("无界面模式已启动(ui_mode=headless)。按 %s 开始录音,"
                 "托盘菜单退出。", config.hotkey)
        # GUI 模式由 webview.start() 的主循环挡着;这里没有窗口,
        # 用事件等退出请求(0.5s 一轮,KeyboardInterrupt 也能进来)。
        while not stop.wait(0.5):
            pass
    except KeyboardInterrupt:
        log.info("收到 Ctrl+C")
    finally:
        # 清理顺序与 _shutdown_and_exit 同款:热键是全局资源,先注销。
        try:
            hotkey.stop()
        except Exception:
            log.debug("热键收尾异常", exc_info=True)
        try:
            controller.shutdown()
        except Exception:
            log.debug("core 收尾异常", exc_info=True)
        try:
            tray.stop()
        except Exception:
            log.debug("托盘收尾异常", exc_info=True)
        try:
            clipboard.release_ownership()
        except Exception:
            pass
    return 0


def main(argv: list[str] | None = None) -> int:
    # 趁 sys.argv 还没被动过,把「当初是怎么被拉起来的」记下来 ——
    # 托盘「重启」要靠它原样复刻启动方式(见 _restart_plan)。
    entry = _capture_entry()

    # V1 的命令行**没有参数语义**(main(argv) 只是收下就丢)。所以这里
    # 用 parse_known_args:未知参数照旧忽略、不报错,保住老习惯;
    # 新增的 --headless / --timeout 走正常解析。
    args, unknown = build_parser().parse_known_args(argv)
    if unknown:
        get_logger("main").debug("忽略不认识的命令行参数: %s", " ".join(unknown))

    ensure_dirs()
    config = load_config()
    setup(config.log_file, config.log_level)
    log = get_logger("main")

    log.info("=" * 60)
    log.info("Qwen3 语音输入启动  热键=%s  模型=%s  后端=%s",
             config.hotkey, config.model, config.backend)

    try:
        if config.ui_mode == "headless":
            # 界面形态第四档(开发者选项,config 手动开,重启生效)
            return run_headless_service(config)
        if args.headless:
            return run_headless(config, args)
        return run_gui(config, entry)
    except KeyboardInterrupt:
        print("qwen3-voice: 收到 Ctrl+C,退出。")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
