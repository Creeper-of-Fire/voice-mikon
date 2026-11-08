"""识别结果展示:置顶小窗 + 真剪贴板复制。

剪贴板走 win32 直写(ctypes),不走 tkinter 的 clipboard_append:
tk 那套是"延迟提交"—— tk 进程活着才有人应答剪贴板读取,
进程一退内容就丢。直写系统剪贴板之后,窗子什么时候关都无所谓。

窗子本身也是同一条原则:识别完不自动退出,用户读完自己关
(Esc / 关闭按钮 / 右上角 ×),看多久都行。
"""

from __future__ import annotations

import ctypes

_WIN32_CF_UNICODETEXT = 13
_GMEM_MOVEABLE = 0x0002


def copy_text_to_clipboard(text: str) -> bool:
    """把文本真写进系统剪贴板。成功返回 True。

    CF_UNICODETEXT + GlobalAlloc 一条龙;SetClipboardData 成功后
    handle 归系统管,失败才需要我们自己 GlobalFree。

    所有句柄类 API 都显式声明 argtypes/restypes:64 位 Python 上
    ctypes 默认把返回值截成 32 位 int,GlobalAlloc 的 HGLOBAL 高位
    一丢就是无效句柄(实测踩过,GlobalLock 恒 NULL)。
    """
    import time

    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32

    kernel32.GlobalAlloc.restype = ctypes.c_void_p
    kernel32.GlobalAlloc.argtypes = (ctypes.c_uint, ctypes.c_size_t)
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalLock.argtypes = (ctypes.c_void_p,)
    kernel32.GlobalUnlock.argtypes = (ctypes.c_void_p,)
    kernel32.GlobalFree.argtypes = (ctypes.c_void_p,)
    user32.SetClipboardData.restype = ctypes.c_void_p
    user32.SetClipboardData.argtypes = (ctypes.c_uint, ctypes.c_void_p)

    # 剪贴板可能正被别的程序占着(剪贴板管理器之类):短重试几次
    opened = False
    for _ in range(5):
        if user32.OpenClipboard(0):
            opened = True
            break
        time.sleep(0.05)
    if not opened:
        return False
    try:
        user32.EmptyClipboard()
        size = (len(text) + 1) * 2  # UTF-16 字节数,含结尾 NUL
        handle = kernel32.GlobalAlloc(_GMEM_MOVEABLE, size)
        if not handle:
            return False
        target = kernel32.GlobalLock(handle)
        if not target:
            kernel32.GlobalFree(handle)
            return False
        ctypes.memmove(target, ctypes.create_unicode_buffer(text), size)
        kernel32.GlobalUnlock(handle)
        if user32.SetClipboardData(_WIN32_CF_UNICODETEXT, handle):
            return True
        kernel32.GlobalFree(handle)
        return False
    finally:
        user32.CloseClipboard()


def show(hits):
    """弹结果窗并阻塞到用户关闭它。

    ``hits`` 为空时展示"没识别到"的引导文案(框紧一点、对准一点);
    有命中则逐条展示类型与内容,并把全部内容拼接后自动复制。
    """
    import tkinter as tk

    copied_all = False
    if hits:
        copied_all = copy_text_to_clipboard("\n".join(h.text for h in hits))

    root = tk.Tk()
    root.title("二维码扫描")
    root.attributes("-topmost", True)
    root.resizable(False, True)
    root.configure(padx=14, pady=12)

    body = tk.Frame(root)
    body.pack(fill="both", expand=True)

    if hits:
        head = f"识别到 {len(hits)} 个码" if len(hits) > 1 else "识别到内容"
        tk.Label(
            body, text=head,
            font=("Segoe UI", 11, "bold"), anchor="w",
        ).pack(fill="x")
        for i, hit in enumerate(hits, 1):
            if len(hits) > 1:
                tk.Label(
                    body, text=f"#{i} · {hit.kind}",
                    fg="#777777", font=("Segoe UI", 9), anchor="w",
                ).pack(fill="x", pady=(8, 0))
            text = tk.Text(
                body, height=_text_height(hit.text), wrap="char",
                font=("Consolas", 10), relief="flat",
                background="#f4f4f4", padx=8, pady=6,
            )
            text.insert("1.0", hit.text)
            text.configure(state="normal")  # 保持可选中,方便只复制一段
            text.pack(fill="x", pady=(2 if len(hits) > 1 else 6, 0))
        note = (
            "已复制到剪贴板" if copied_all else "复制失败,请在上面手动选中复制"
        )
    else:
        tk.Label(
            body, text="框里没找到二维码 / 条形码",
            font=("Segoe UI", 11, "bold"), anchor="w",
        ).pack(fill="x")
        tk.Label(
            body,
            text="试把框画得紧一点(只框住码本身),或者离码近一点再截。",
            fg="#777777", wraplength=380, justify="left", anchor="w",
        ).pack(fill="x", pady=(6, 0))
        note = ""

    if note:
        tk.Label(
            body, text=note, fg="#2e7d32" if copied_all else "#b00020",
            font=("Segoe UI", 9), anchor="w",
        ).pack(fill="x", pady=(8, 0))

    buttons = tk.Frame(root)
    buttons.pack(fill="x", pady=(12, 0))
    if hits and not copied_all:
        tk.Button(
            buttons, text="复制", width=8,
            command=lambda: _retry_copy(root, body, hits),
        ).pack(side="left")
    tk.Button(buttons, text="关闭", width=8, command=root.destroy).pack(side="right")

    root.bind("<Escape>", lambda e: root.destroy())
    root.update_idletasks()
    _center(root)
    root.mainloop()


def _text_height(text: str) -> int:
    """Text 部件的高度行数:内容自适应,长 URL 折行也放得下。"""
    lines = max(1, len(text.splitlines()))
    return min(6, lines + 1)


def _retry_copy(root, body, hits) -> None:
    if copy_text_to_clipboard("\n".join(h.text for h in hits)):
        root.title("二维码扫描 — 已复制")


def _center(window) -> None:
    """窗口挪到主屏中间偏上(跟截图时手的位置接近)。"""
    window.update_idletasks()
    w, h = window.winfo_width(), window.winfo_height()
    screen_w = window.winfo_screenwidth()
    screen_h = window.winfo_screenheight()
    window.geometry(f"+{(screen_w - w) // 2}+{(screen_h - h) // 3}")
