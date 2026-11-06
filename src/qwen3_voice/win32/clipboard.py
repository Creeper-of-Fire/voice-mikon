"""剪贴板读写(Win32 API,不走 pyperclip)。"""

from __future__ import annotations

import ctypes
import threading
import time

from ..logging_setup import get_logger
from .api import kernel32, user32

log = get_logger("win32.clipboard")

CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002

# 剪贴板是全局资源,别的进程可能正占着它(OpenClipboard 会失败)。
# 读不出来时必须重试 —— 而且**读失败要和"里面没有文本"分开**:
# 把"读失败"当成"没有文本"是个危险的误判:注入逻辑会据此决定
# "没有原内容需要归还",用户剪贴板里的东西就丢了。
# 所以下面有 :func:`read_text` 返回 (ok, text) 两段信息。
_OPEN_RETRIES = 4
_OPEN_DELAY = 0.02

# ---- 剪贴板 owner:一块 message-only 窗口,专给写入挂名 ----
#
# OpenClipboard(NULL) 打开的话,写入内容的 owner 是 NULL —— 第三方
# 剪贴板管理器(Ditto 一类)拿 GetClipboardOwner 查不到来源,只能
# fallback 标注成"当前前台窗口",识别文本就被记到了用户正聚焦的
# 那个应用头上。给写入挂一个我们自己的窗口,来源标注才归位。
#
# 窗口是 message-only(HWND_MESSAGE 父),不可见、不进任务栏、不抢
# 任何东西。
#
# ⚠⚠ **owner 窗口必须活在长命线程上** —— 这是踩过的坑:
#
#  1. Windows 规矩:**owner 窗口被销毁时,系统连带清空剪贴板**
#     (内容没了、owner 置 NULL)。
#  2. owner 窗口属于创建它的线程;线程一退出,窗口就被系统销毁。
#  3. 而这里的写入发生在**短命线程**上(每轮录音一个 asr 线程、
#     pywebview 的 js_api 线程池)—— 第一次写入在录音线程上建了
#     owner 窗口,那一轮结束线程退出 → 窗口销毁 → **剪贴板被清空**
#     → 下一轮拿着死句柄去 OpenClipboard → 直接失败。
#     (实测症状:注入三连败 + 「手动复制:写入剪贴板失败」刷屏。)
#
# 所以 owner 窗口固定在**主线程**创建(进程活多久,窗口就活多久),
# 见 ensure_owner_window() 的调用点(__main__.main 启动早期)。
# 别的线程只复用,不重建。
#
# 进程退出时窗口照样会被销毁 → 退出前必须 relinquish():把所有权
# 转给 NULL 并把文本写回(见 _relinquish_ownership),内容才能留住。
_OWNER_CLASS = "Qwen3VoiceClipboardOwner"
WM_DESTROY = 0x0002
HWND_MESSAGE = -3

_owner_hwnd = 0
_owner_lock = threading.Lock()
_owner_wndproc_ref = None   # ctypes 回调必须保活,否则被 GC 就是访问已释放代码

# WNDPROC 签名:LRESULT (WINAPI*)(HWND, UINT, WPARAM, LPARAM)
_WNDPROC = ctypes.WINFUNCTYPE(
    ctypes.c_ssize_t, ctypes.c_void_p,
    ctypes.c_uint, ctypes.c_size_t, ctypes.c_ssize_t)


class _WNDCLASSW(ctypes.Structure):
    """RegisterClassW 的参数结构(WNDPROC 字段必须保活引用)。"""

    _fields_ = [
        ("style", ctypes.c_uint),
        ("lpfnWndProc", _WNDPROC),
        ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int),
        ("hInstance", ctypes.c_void_p),
        ("hIcon", ctypes.c_void_p),
        ("hCursor", ctypes.c_void_p),
        ("hbrBackground", ctypes.c_void_p),
        ("lpszMenuName", ctypes.c_wchar_p),
        ("lpszClassName", ctypes.c_wchar_p),
    ]


def _owner_wndproc(hwnd, msg, wparam, lparam):
    """owner 窗口的消息过程:只拦 WM_DESTROY,其余交给 DefWindowProc。"""
    if msg == WM_DESTROY:
        _relinquish_ownership()
    return user32.DefWindowProcW(hwnd, msg, wparam, lparam)


def _ensure_owner_window() -> int:
    """拿到剪贴板 owner 窗口句柄,失败返回 0(降级为无 owner)。

    **只在主线程创建** —— 见模块头:owner 窗口属于创建它的线程,
    线程退出窗口就没了,而 Windows 会在 owner 窗口销毁时清空
    剪贴板。所以这里做两件事:

    1. 已经有窗口且还活着(``IsWindow``)→ 直接复用;
    2. 还没有 → **只在主线程创建**;别的线程(录音线程、js_api
       线程池)来了就等一下,还不来就降级成无 owner(功能不受影响,
       只是第三方剪贴板看不到来源)。
    """
    global _owner_hwnd, _owner_wndproc_ref
    with _owner_lock:
        if _owner_hwnd:
            # 句柄可能已经随着线程退出而失效 —— 这种死句柄传给
            # OpenClipboard 会直接失败(实测:读都读不到剪贴板)
            if _is_window_alive(_owner_hwnd):
                return _owner_hwnd
            log.debug("剪贴板 owner 窗口已失效,重建")
            _owner_hwnd = 0
        if not _is_main_thread():
            # 非主线程不建:短命线程建的 owner 会在线程退出时带走
            # 剪贴板内容。等主线程建好即可。
            return 0
        try:
            # 64 位进程里句柄是完整指针:不设 restype 的话 ctypes 默认按
            # 32 位 int 截断返回值,窗口句柄直接报废。就地补上(幂等,
            # 不影响 api.py 已设的其他原型)。
            kernel32.GetModuleHandleW.restype = ctypes.c_void_p
            # 不设 argtypes 的话 ctypes 把所有 int 按 32 位打包 ——
            # hInstance 这类完整指针的值直接 OverflowError。全量声明:
            user32.CreateWindowExW.argtypes = [
                ctypes.c_ulong,                    # dwExStyle
                ctypes.c_wchar_p,                  # lpClassName
                ctypes.c_wchar_p,                  # lpWindowName
                ctypes.c_ulong,                    # dwStyle
                ctypes.c_int, ctypes.c_int,        # x, y
                ctypes.c_int, ctypes.c_int,        # nWidth, nHeight
                ctypes.c_void_p,                   # hWndParent(HWND_MESSAGE)
                ctypes.c_void_p,                   # hMenu
                ctypes.c_void_p,                   # hInstance
                ctypes.c_void_p,                   # lpParam
            ]
            user32.CreateWindowExW.restype = ctypes.c_void_p
            # OpenClipboard 的 hwnd 参数和 DefWindowProcW 的句柄/参数在
            # 64 位下都必须走完整指针宽度(不设原型 ctypes 默认按 32 位
            # int 传,句柄高于 4GB 就坏)。幂等设置。
            user32.OpenClipboard.argtypes = [ctypes.c_void_p]
            user32.OpenClipboard.restype = ctypes.c_int
            user32.DefWindowProcW.argtypes = [
                ctypes.c_void_p, ctypes.c_uint, ctypes.c_size_t, ctypes.c_ssize_t]
            user32.DefWindowProcW.restype = ctypes.c_ssize_t
            user32.IsWindow.argtypes = [ctypes.c_void_p]
            user32.IsWindow.restype = ctypes.c_int
            _owner_wndproc_ref = _WNDPROC(_owner_wndproc)
            wc = _WNDCLASSW()
            wc.style = 0
            wc.lpfnWndProc = _owner_wndproc_ref
            wc.hInstance = kernel32.GetModuleHandleW(None)
            wc.lpszClassName = _OWNER_CLASS
            if not user32.RegisterClassW(ctypes.byref(wc)):
                # 已注册(别的线程抢了先)不算失败
                if kernel32.GetLastError() != 1410:   # ERROR_CLASS_ALREADY_EXISTS
                    return 0
            hwnd = user32.CreateWindowExW(
                0, _OWNER_CLASS, None, 0,
                0, 0, 0, 0,
                # HWND_MESSAGE 是负数常量:裸传会被 x64 调用约定清零
                # 高 32 位,必须显式包成 64 位指针
                ctypes.c_void_p(HWND_MESSAGE),
                None, wc.hInstance, None,
            )
            if not hwnd:
                return 0
            _owner_hwnd = hwnd
            log.debug("剪贴板 owner 窗口已创建: 0x%X", hwnd)
            return hwnd
        except Exception as e:
            log.debug("创建剪贴板 owner 窗口失败(降级为无 owner): %s", e)
            return 0


def _is_window_alive(hwnd: int) -> bool:
    """句柄还指着活窗口吗(IsWindow)。"""
    if not hwnd:
        return False
    try:
        user32.IsWindow.argtypes = [ctypes.c_void_p]
        user32.IsWindow.restype = ctypes.c_int
        return bool(user32.IsWindow(ctypes.c_void_p(hwnd)))
    except Exception:
        return False


def _is_main_thread() -> bool:
    """当前是不是主线程(owner 窗口只许在主线程上建)。"""
    return threading.current_thread() is threading.main_thread()


def ensure_owner_window() -> int:
    """公开入口:在**主线程**上把 owner 窗口建好。

    启动早期调一次(``__main__.main``)—— 那时主线程还没被
    ``webview.start()`` 占住,窗口归主线程,活到进程结束。
    """
    if not _is_main_thread():
        log.debug("ensure_owner_window 只能从主线程调用")
        return 0
    return _ensure_owner_window()


def release_ownership() -> None:
    """退出前把剪贴板所有权还给系统(内容留着)。

    owner 窗口随进程退出被销毁时,Windows 会**连带清空剪贴板** ——
    注入失败时"识别结果留在剪贴板"的承诺就废了。所以在退出前
    先转交所有权(owner 置 NULL),内容就不会跟着窗口陪葬。
    """
    _relinquish_ownership()


def _relinquish_ownership() -> None:
    """把剪贴板所有权转给 NULL,但保住内容(见模块头的 Windows 规矩)。"""
    try:
        if not user32.OpenClipboard(None):
            return
        try:
            handle = user32.GetClipboardData(CF_UNICODETEXT)
            if not handle:
                return
            ptr = kernel32.GlobalLock(handle)
            if not ptr:
                return
            try:
                text = ctypes.c_wchar_p(ptr).value
            finally:
                kernel32.GlobalUnlock(handle)
            if text is None:
                return
            # OpenClipboard(NULL) 之后 EmptyClipboard:内容清空、owner 置
            # NULL;再把同样的文本写回去 —— owner 保持 NULL,窗口销毁的
            # 时候系统就不再碰剪贴板了。
            user32.EmptyClipboard()
            _write_text_locked(text)
        finally:
            user32.CloseClipboard()
    except Exception:
        pass


def _open_clipboard() -> bool:
    """打开剪贴板,被占用时短暂重试。带 owner(见模块头)。"""
    owner = _ensure_owner_window()
    for i in range(_OPEN_RETRIES):
        if user32.OpenClipboard(owner or None):
            return True
        if i < _OPEN_RETRIES - 1:
            time.sleep(_OPEN_DELAY)
    return False


def read_text() -> tuple[bool, str | None]:
    """读剪贴板里的 Unicode 文本,返回 ``(ok, text)``。

    **ok 说的是"读到了没有",不是"有没有文本"** —— 这两件事必须分得清:

    - ``(True, None)``  读成功了,剪贴板里**确实没有文本**
      (空的,或者只有图片之类读不出文本的内容)
    - ``(False, None)`` **读失败**:打不开剪贴板(被别人占着)、锁内存失败、
      或者中途抛了异常

    为什么要分:注入逻辑要拿原内容来"用完还回去"。分不清的话,
    "读失败"会被当成"原剪贴板是空的" → 归还那一步被跳过 →
    用户原来的内容被 ``EmptyClipboard`` 清掉,**永远还不回来**。
    那是真丢数据,比"多留一份识别结果在剪贴板"严重得多。

    ``get_text()`` 是这个函数的兼容包装(只给文本,不管 ok),
    读失败时它和"没有文本"一样返回 None —— 调用方要区分就用本函数。
    """
    try:
        if not _open_clipboard():
            return False, None
        try:
            handle = user32.GetClipboardData(CF_UNICODETEXT)
            if not handle:
                # 读到了,只是里面没有 CF_UNICODETEXT 格式
                return True, None
            ptr = kernel32.GlobalLock(handle)
            if not ptr:
                return False, None
            try:
                return True, ctypes.c_wchar_p(ptr).value
            finally:
                kernel32.GlobalUnlock(handle)
        finally:
            user32.CloseClipboard()
    except Exception:
        return False, None


def get_text() -> str | None:
    """读取剪贴板中的 Unicode 文本,没有文本时返回 None。

    注意:读失败也返回 None —— 要区分"读失败"和"没有文本",
    请用 :func:`read_text`。
    """
    ok, text = read_text()
    return text if ok else None


def get_sequence_number() -> int:
    """剪贴板变更序号。

    任何一次剪贴板改动(哪怕内容相同)都会让它递增。用它来判断
    "剪贴板现在还是我写的那份吗" —— 这是避免覆盖用户新复制内容的
    关键:恢复原内容之前先比对序号,用户中途动过就放弃恢复。
    """
    try:
        return int(user32.GetClipboardSequenceNumber())
    except Exception:
        return 0


def _write_text_locked(text: str) -> bool:
    """剪贴板**已打开**的前提下写入文本(set_text 的内部步骤)。"""
    user32.EmptyClipboard()
    data = text + "\0"
    size = len(data) * ctypes.sizeof(ctypes.c_wchar)
    handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, size)
    if not handle:
        return False
    ptr = kernel32.GlobalLock(handle)
    if not ptr:
        kernel32.GlobalFree(handle)
        return False
    try:
        ctypes.memmove(ptr, ctypes.create_unicode_buffer(data), size)
    finally:
        kernel32.GlobalUnlock(handle)
    if not user32.SetClipboardData(CF_UNICODETEXT, handle):
        kernel32.GlobalFree(handle)
        return False
    # 所有权已交给系统,不能再 free
    return True


def set_text(text: str) -> bool:
    """把文本写入剪贴板,成功返回 True。"""
    try:
        if not _open_clipboard():
            return False
        try:
            return _write_text_locked(text)
        finally:
            user32.CloseClipboard()
    except Exception:
        return False
