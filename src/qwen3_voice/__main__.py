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

import os
import sys
import threading
from pathlib import Path
from typing import NamedTuple

from .config import load_config
from .core.controller import VoiceController
from .logging_setup import get_logger, setup
from .paths import PROJECT_DIR, config_path
from .win32.dpi import set_dpi_awareness
from .win32.hotkey import HotkeyError, HotkeyListener


class _Entry(NamedTuple):
    """「当初是怎么被拉起来的」—— 重启必须原样复刻它。

    argv0  入口的 argv[0](已展开成绝对路径):
            - 脚本入口(桌面快捷方式走的就是这条)→ 那个 .pyw 的路径
            - ``-m qwen3_voice``                   → 包里的 __main__.py
    frozen 是否是 PyInstaller 打包出来的 exe
    """

    argv0: str | None
    frozen: bool


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
    import 本包,我们也只负责把它作为**独立进程**拉起 —— 不等它、
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


def main(argv: list[str] | None = None) -> int:
    argv = argv if argv is not None else sys.argv[1:]

    # 趁 sys.argv 还没被动过,把「当初是怎么被拉起来的」记下来 ——
    # 托盘「重启」要靠它原样复刻启动方式(见 _restart_plan)。
    entry = _capture_entry()

    # 必须在创建任何窗口之前声明 DPI 感知,否则高分屏下布局全乱
    dpi_mode = set_dpi_awareness()

    # 剪贴板 owner 窗口:**必须在主线程创建**(owner 窗口归创建它的
    # 线程,线程退出窗口就没了,而 Windows 在 owner 窗口销毁时会
    # 连带清空剪贴板 —— 写在录音线程上会把"识别结果留在剪贴板"
    # 这个兜底直接废掉)。这里趁主线程还空闲时建好,活到进程结束。
    from .win32 import clipboard

    clipboard.ensure_owner_window()

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

    # 悬浮球右键菜单的三个出口(通用功能入口,用户拍板的第一批):
    #   「设置」直接显示面板;「二维码扫描」拉起附属工具(独立进程);
    #   「退出」复用托盘同一条清理路径(热键注销 → 窗口销毁 → 主循环
    #   返回),不是简单 kill。
    capsule.set_panel_opener(panel.show)
    capsule.set_quit_handler(tray.quit)
    capsule.set_qr_scanner_launcher(make_qr_scanner_launcher())

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
    # 挂到 controller 上:退出时由 shutdown() 第一个停它(全局资源,
    # 拖到最后会让"退出后马上重启"注册失败)。
    controller.attach_hotkey(hotkey)
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
        # controller.shutdown() 里第一件事就是停热键 —— 它是全局资源,
        # 必须在进程真正退出之前就注销掉,否则"退出后马上重启"会
        # 因为热键还被占着而注册失败。
        controller.shutdown()
        hotkey.stop()   # 幂等兜底(shutdown 里已经停过)
        # 两个窗口都要销毁:pywebview 的主循环要等所有窗口都没了才返回,
        # 只关一个的话进程会变成"托盘没了但还活着"的僵尸
        panel.destroy()
        capsule.destroy()
        # 剪贴板 owner 窗口随进程退出被销毁时,Windows 会连带清空
        # 剪贴板 —— 注入失败时留在剪贴板里的识别结果就没了。
        # 退出前先把所有权交还系统(owner 置 NULL),内容留住。
        clipboard.release_ownership()
        logger.info("退出")

    # 托盘的「重启」:走到这里说明所有清理都完成了(热键已注销、
    # 窗口已销毁、模型已卸载),现在拉新实例才不会撞车
    if getattr(tray, "restart_requested", False):
        _spawn_restart(logger, entry)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
