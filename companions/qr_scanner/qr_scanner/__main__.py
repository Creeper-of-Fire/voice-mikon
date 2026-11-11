"""二维码扫描工具 —— 入口。

**架构独立**:与语音主程序 ``voice_mikon`` 零 import。主程序只
往悬浮球右键菜单挂一个「拉起本工具」的入口(subprocess,独立
进程);本工具单独跑也完全成立:

    python -m qr_scanner              截屏 → 框选 → 识别 → 结果窗
    python -m qr_scanner --image X    直接识别一张图片(调试/二次利用)
    python -m qr_scanner --selftest   不弹窗:截屏+全图解码,链路自检

依赖在主项目 pyproject 的可选组里(同一个 venv):
    pip install -e ".[qr]"
"""

from __future__ import annotations

import sys


def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)

    # DPI aware 是全工具坐标系的根,必须先于任何截屏/建窗声明
    from .selector import enable_dpi_awareness

    enable_dpi_awareness()

    missing = _missing_dependencies()
    if missing:
        _fatal(
            "依赖没装齐:缺 {}。\n装法:pip install -e \".[qr]\""
            " 或 pip install {}".format("、".join(missing), " ".join(missing))
        )
        return 1

    # --image X:跳过截屏和框选,直接识别图片(主要给调试用,
    # 顺带让工具能当命令行小解码器使)
    image_path = _flag_value(args, "--image")

    from PIL import Image

    if image_path is not None:
        try:
            with Image.open(image_path) as source:
                target = source.convert("RGB")
        except OSError as e:
            _fatal(f"图片读不了:{e}")
            return 1
    elif "--selftest" in args:
        # 链路自检:截屏 + 全图解码,结果走 stdout,不弹任何窗
        from . import capture, decode

        hits = decode.decode_image(capture.grab_virtual_screen())
        if hits:
            for hit in hits:
                print(f"[{hit.kind}] {hit.text}")
        else:
            print("selftest OK: 截屏/解码链路通,当前屏幕上没找到码")
        return 0
    else:
        from . import capture, selector

        screenshot = capture.grab_virtual_screen()
        region = selector.select_region(screenshot)
        if region is None:
            return 0  # 用户取消(Esc/右键/误触):静默退出,别弹什么窗
        l, t, w, h = region
        target = screenshot.crop((l, t, l + w, t + h))

    from . import decode, result

    hits = decode.decode_image(target)
    result.show(hits)
    return 0


def _flag_value(args: list[str], flag: str) -> str | None:
    """取 ``--flag VALUE`` 的 VALUE;没有这个 flag 返回 None。"""
    if flag in args:
        i = args.index(flag)
        if i + 1 < len(args):
            return args[i + 1]
    return None


def _missing_dependencies() -> list[str]:
    """探测缺哪些第三方包,返回包名清单(pip 装的时候要用)。"""
    checks = {
        "mss": "import mss",
        "pyzbar": "from pyzbar.pyzbar import decode",
        "pillow": "from PIL import Image",
    }
    missing = []
    for package, stmt in checks.items():
        try:
            exec(stmt, {})  # noqa: S102 - 探测依赖,语句是本模块写死的
        except ImportError:
            missing.append(package)
    return missing


def _fatal(message: str) -> None:
    """启动期致命错误:优先弹窗(工具是 GUI 场景,多半没终端看 stderr)。"""
    print(f"qr_scanner: {message}", file=sys.stderr)
    try:
        import tkinter as tk
        from tkinter import messagebox

        root = tk.Tk()
        root.withdraw()
        messagebox.showerror("二维码扫描", message)
        root.destroy()
    except Exception:
        pass  # 连 tkinter 都起不来,stderr 那份就是全部了


if __name__ == "__main__":
    sys.exit(main())
