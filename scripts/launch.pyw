# coding=utf-8
"""无窗口启动器 —— 给桌面快捷方式用。

快捷方式直接指向 **base 安装的 pythonw.exe**(GUI 子系统,零窗口),
用本脚本作参数。本脚本把 venv 的 site-packages 挂进 sys.path 再跑
主程序 —— 这正是 uv venv 里 trampoline(pythonw.exe)一直在做的事:
**进程是 base 的 exe,包是 venv 的**。DLL 全部按 base 目录解析,
和平时完全一样,不会出现 safetensors 之类 DLL 找不到的问题。

为什么不用 venv 自带的 pythonw.exe:uv 生成的那个是 CONSOLE 子系统
的 trampoline,双击快捷方式会弹一个终端窗口(实测)。

用法(快捷方式里):
    目标:   base 目录下的 pythonw.exe + 本脚本的完整路径
    起始于: qwen3-voice 项目根目录
"""

import site
import sys
from pathlib import Path

VENV_SITE = Path(r"D:\Dev\Environments\qwen3-asr\Lib\site-packages")


def main() -> int:
    if not VENV_SITE.is_dir():
        # 无窗口进程报错看不见,写日志目录
        log = Path(__file__).resolve().parent.parent / "data" / "launch_error.log"
        try:
            log.parent.mkdir(parents=True, exist_ok=True)
            log.write_text(f"venv site-packages 不存在: {VENV_SITE}\n",
                           encoding="utf-8")
        except OSError:
            pass
        return 1

    # 用 addsitedir 而不是 sys.path.insert:editable 安装
    # (qwen3_voice 自己)靠 site-packages 里的 .pth 文件生效,
    # 而 .pth 只在 site 初始化时被处理 —— 手动 insert 路径
    # 不会跑 .pth,qwen3_voice 会 ModuleNotFoundError(实测)。
    site.addsitedir(str(VENV_SITE))

    from qwen3_voice.__main__ import main as app_main

    return app_main()


if __name__ == "__main__":
    raise SystemExit(main())
