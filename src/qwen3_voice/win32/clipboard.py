"""剪贴板读写(Win32 API,不走 pyperclip)。"""

from __future__ import annotations

import ctypes
import threading
import time
from dataclasses import dataclass

from ..logging_setup import get_logger
from .api import kernel32, user32

log = get_logger("win32.clipboard")

CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002

# 写入结果的原因码 —— 让调用方能分清"卡在哪一步",而不是只拿到一个
# 说不清缘由的 False。
WRITE_REASON_OK = ""                 # 成功
WRITE_REASON_OPEN_FAILED = "open_failed"      # 剪贴板被别人占着,开不了
WRITE_REASON_WRITE_FAILED = "write_failed"    # SetClipboardData 没成功
WRITE_REASON_VERIFY_FAILED = "verify_failed"  # 写完读回来对不上


@dataclass(frozen=True)
class ClipboardResult:
    """一次剪贴板写入的结果。

    ok     真的写进去了吗(带读回校验,见 :func:`set_text_verified`)
    reason 失败的原因码(成功时是空串)
    """

    ok: bool
    reason: str = ""

    def __bool__(self) -> bool:
        return self.ok


# ---- 打开剪贴板的重试窗口 ----
#
# 剪贴板是全局资源,别的进程可能正占着它(OpenClipboard 会失败)。
# 读不出来时必须重试 —— 而且**读失败要和"里面没有文本"分开**:
# 把"读失败"当成"没有文本"是个危险的误判:注入逻辑会据此决定
# "没有原内容需要归还",用户剪贴板里的东西就丢了。
# 所以下面有 :func:`read_text` 返回 (ok, text) 两段信息。
#
# 重试窗口原来只有 4 次 × 20ms ≈ 60ms —— 第三方剪贴板管理器
# (Ditto 一类)稍微多占用一会儿,写入就直接失败;而失败之后**拿不出
# 任何证据**说明"到底写没写进去"(见 set_text_verified),用户看到的
# 只有一句"复制失败",剪贴板里其实什么都没有。
#
# 提到 10 次 × 50ms ≈ 450ms:够第三方管理器把板子还回来,又不会把注入
# 路径卡得太久(最坏 450ms 打开 + 注入自己的 3 次重试,仍在可接受范围)。
#
# ⚠ 重试窗口只影响"打不开板"这一件事;**"读失败"与"里面没有文本"的
# 区分语义一点没动**(见 :func:`read_text`)。
#
# 节奏是"前密后疏"两段式(实测口径,用户拍板):
#   - 前段(20~60ms 间隔,~150ms 内 5 次):占用方很快就放板的常见情形,
#     手动点「复制」按钮时几乎无感;
#   - 后段(100~200ms 间隔,拖到 ~2s):第三方剪贴板管理器(Ditto 一类)
#     正在做整轮扫描/写入,持有剪贴板可到秒级 —— 等它做完再抢,
#     比 500ms 就放弃诚实得多(用户实测:间隔太短时"占着板的功能
#     还没彻底关闭",重试全落空)。
_OPEN_RETRY_DELAYS = (
    0.02, 0.02, 0.04, 0.04, 0.06,          # 前段:快速抢
    0.10, 0.10, 0.15, 0.15,                 # 过渡
    0.20, 0.20, 0.20, 0.20, 0.20, 0.20,     # 后段:耐心等,总窗口 ~1.9s
)

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
    """把剪贴板所有权转给 NULL,但保住内容(见模块头的 Windows 规矩)。

    转交完再**读回核对一次**:这里承诺的是"内容留着",那就得确认它
    真的留着 —— 空口无凭的承诺不算数(和 :func:`set_text_verified` 同一
    道理)。核对失败只记日志:这条路径跑在退出前,那时已经没有补救余地。

    核对走的是 ``OpenClipboard(NULL)``,**不碰 owner 窗口** —— 本函数
    可能正在 owner 窗口的 WM_DESTROY 里跑,那时去重建 owner 窗口等于
    在窗口销毁回调里造新窗口,自己给自己挖坑。
    """
    text: str | None = None
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
        return

    try:
        if not user32.OpenClipboard(None):
            log.warning("剪贴板转交后核对失败:剪贴板打不开")
            return
        try:
            handle = user32.GetClipboardData(CF_UNICODETEXT)
            current: str | None = None
            if handle:
                ptr = kernel32.GlobalLock(handle)
                if ptr:
                    try:
                        current = ctypes.c_wchar_p(ptr).value
                    finally:
                        kernel32.GlobalUnlock(handle)
        finally:
            user32.CloseClipboard()
        if current != text:
            log.warning("剪贴板转交后读回不一致,内容可能没留住")
    except Exception:
        pass


def _open_clipboard() -> bool:
    """打开剪贴板,被占用时按节奏表重试(见 _OPEN_RETRY_DELAYS)。带 owner。"""
    owner = _ensure_owner_window()
    for delay in _OPEN_RETRY_DELAYS:
        if user32.OpenClipboard(owner or None):
            return True
        time.sleep(delay)
    # 节奏表走完还打不开才是真失败(最后一次 sleep 后再试一下,
    # 保证"尝试次数 = 间隔数 + 1")
    return bool(user32.OpenClipboard(owner or None))


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
    """剪贴板**已打开**的前提下写入文本(set_text 的内部步骤)。

    顺序是硬要求:**先分配,后清板**。

    为什么不能反:``EmptyClipboard`` 一执行,用户原来那份内容就**没了**
    (不可撤销)。而分配内存是会失败的 —— 内存紧张时 ``GlobalAlloc``
    直接返回 0。原来"先清板再分配"的写法在这个失败分支上就是
    "把用户剪贴板清空了,新内容却没写进去":用户按 Ctrl+V 贴出空,
    而且**再也找不回来**。所以分配链(GlobalAlloc → GlobalLock →
    memmove)必须整条走通、确认内容已经在自己手里了,才允许动板。

    ``SetClipboardData`` 本身**不**清掉板上的其它格式(只替换同格式),
    所以清板这一步不能省;但它必须发生在写入**之前**,否则新内容
    会被随后的 EmptyClipboard 一起清掉。

    这样丢数据的窗口就从"分配失败(现实可能发生)"压缩到
    "清板之后 SetClipboardData 罕见失败(极小概率)"。
    """
    data = text + "\0"
    size = len(data) * ctypes.sizeof(ctypes.c_wchar)
    handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, size)
    if not handle:
        # 分配失败:**板还没动**,用户原来的内容完好无损
        return False
    ptr = kernel32.GlobalLock(handle)
    if not ptr:
        kernel32.GlobalFree(handle)
        return False
    try:
        ctypes.memmove(ptr, ctypes.create_unicode_buffer(data), size)
    finally:
        kernel32.GlobalUnlock(handle)

    # 内容已经在自己手里了,这才可以动板
    user32.EmptyClipboard()
    if not user32.SetClipboardData(CF_UNICODETEXT, handle):
        kernel32.GlobalFree(handle)
        return False
    # 所有权已交给系统,不能再 free
    return True


def set_text_verified(text: str) -> ClipboardResult:
    """把文本写入剪贴板,并**读回校验**,返回带原因码的结果。

    为什么必须读回:``SetClipboardData`` 返回非零只说明"系统收下了这块
    内存",**不代表剪贴板里真的有这份内容** —— 剪贴板被别人占着、
    写完立刻被覆盖、owner 窗口已经失效(见模块头)都会让"写入成功"
    变成一句空话。实测症状:程序报"已复制成功",剪贴板里**什么都没有**,
    用户按 Ctrl+V 贴出的是上一次的旧内容,还以为程序在乱写。

    所以写完关板、重新打开、读回来比对:
    - 读回来的文本 == 写入的文本 -> 真的写进去了
    - 读不回来(打不开板 / 锁内存失败)-> **也算失败**。读不回来就没有
      "写成功了"的证据,不能拿"可能成功了"去糊弄用户。

    读回用的是 ``GetClipboardData``,**不会**递增剪贴板变更序号 ——
    注入层"这份剪贴板还是我写的那份吗"的序号判定不受影响。
    """
    try:
        if not _open_clipboard():
            log.warning("剪贴板打不开(可能被别的程序占着),写入失败")
            return ClipboardResult(False, WRITE_REASON_OPEN_FAILED)
        try:
            written = _write_text_locked(text)
        finally:
            user32.CloseClipboard()
        if not written:
            log.warning("写剪贴板失败(SetClipboardData 没成功)")
            return ClipboardResult(False, WRITE_REASON_WRITE_FAILED)

        # 读回校验:必须**关板再读** —— 开着板读回来的是自己刚写进内存的
        # 那一份,验的是内存不是剪贴板。
        ok, current = read_text()
        if not ok:
            log.warning("剪贴板写入后读回失败,当作没写进去")
            return ClipboardResult(False, WRITE_REASON_VERIFY_FAILED)
        if current != text:
            log.warning("剪贴板写入后读回不一致(写入 %d 字,读回 %d 字),当作没写进去",
                        len(text), len(current or ""))
            return ClipboardResult(False, WRITE_REASON_VERIFY_FAILED)
        return ClipboardResult(True, WRITE_REASON_OK)
    except Exception as e:
        log.debug("写剪贴板异常: %s", e)
        return ClipboardResult(False, WRITE_REASON_WRITE_FAILED)


def set_text(text: str) -> bool:
    """把文本写入剪贴板,成功返回 True。

    带读回校验(见 :func:`set_text_verified`):写进去的内容能被读回来
    才算成功。
    """
    return set_text_verified(text).ok
