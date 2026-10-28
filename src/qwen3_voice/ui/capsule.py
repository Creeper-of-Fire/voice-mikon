"""录音胶囊悬浮窗(pywebview + WebView2,透明无边框)。

胶囊是录音过程的**唯一**视觉载体:上方一条实时预览文字,下方一颗
胶囊(取消 / 波形+计时 / 结束)。面板不常驻时,用户按热键录音全程
靠它反馈状态 —— 没有它,录音就是"盲录"。

前端是纯静态 HTML/CSS/JS(``static/capsule.{html,css,js}``),
零框架零构建,和面板同一套约定。

Python 与前端之间的桥接契约(完整见同目录 ``BRIDGE.md``):

  Python → JS   window.__capsule.setState / setLevels / setElapsed
                / setPreview / setResult / setError / setCancelled
  JS → Python   window.pywebview.api.ready / cancel_recording / stop_recording
                / update_regions

线程分工:
  主线程      webview.start() 的 GUI 消息循环(和面板共用一次 start)
  推送线程    面板的 _EventPump 把控制器事件转成这里的前端调用
  守卫线程    建窗后的头几秒盯着窗口,别让 pywebview 的建窗副作用抢焦点
  隐藏定时器  threading.Timer,新一轮录音开始时作废旧的

六个坑,都在下面代码里处理了:

1. **显示绝不能抢焦点**。pywebview 的 ``window.show()`` 内部会调
   WinForms 的 ``Activate()``,抢了焦点,后面注入的文字就没有落点了
   (光标会被抢到胶囊上,文字全丢)。所以显示一律走 Win32:
   先加 ``WS_EX_NOACTIVATE``,再 ``ShowWindow(SW_SHOWNOACTIVATE)``。

2. **透明窗口在 pywebview 里自带一个抢焦点的 hack**。它对透明窗口
   会在页面开始导航时执行 ``form.Show() + form.Activate()`` ——
   胶囊会在启动时自己冒出来,还占住前台(``WS_EX_NOACTIVATE`` 拦不住
   ``Activate()``)。定点修法见 ``_on_before_show``。

3. **透明窗口一旦被 resize,透明就失效**。WebView2 会用 Form 的
   背景色(#F0F0F0)把整块客户区填掉。色键对 WebView2 这个子窗口
   无效,resize 后再重设透明又会被 CoreWebView2 的"只能从 UI 线程
   访问"挡下来 —— 所以窗口**固定尺寸**,不 resize。

4. **evaluate_js 在 GUI 还没建好时会阻塞 20 秒才抛异常**。
   所以所有前端调用都过 ``_gui_ready`` 门控,窗口就绪后才推。

5. **透明窗口上不能有 box-shadow / backdrop-filter** ——
   在透明窗口上它们会渲染成一条灰带,很难看。所以文字条和胶囊都
   只用"深底 + 半透明描边"表达层次。

6. **窗口的空白区也属于窗口**。窗口是**固定尺寸**的(见 WIN_W 的
   说明),文字条和胶囊之外的透明空白虽然看不见,却会**吃掉底下应用
   的点击** —— 屏幕底部有一块点不到的地方。靠 ``SetWindowRgn`` 把
   窗口形状裁成"两块内容的并集"解决:前端量出两块内容的矩形上报
   (``update_regions``),这边换算成物理像素后裁窗口。既不 resize,
   也不动透明实现。
"""

from __future__ import annotations

import json
import threading
import time
from pathlib import Path
from typing import Any

from ..core.controller import State, UIEvent, VoiceController
from ..logging_setup import get_logger
from ..win32.api import (
    bring_to_top,
    get_foreground_window,
    get_window_rect,
    get_work_area,
    hide_window,
    is_window_visible,
    move_window_to,
    restore_foreground,
    set_no_activate_tree,
    set_tool_window_style,
    set_window_regions,
    show_window_no_activate,
)

log = get_logger("ui.capsule")

# 前端静态资源目录(和面板同一份目录,不同文件)
STATIC_DIR = Path(__file__).resolve().parent / "static"

# 窗口尺寸(逻辑像素):**固定**,不做高度自适应。
#
# 为什么固定:pywebview 的透明窗口一旦被 resize,WebView2 的透明背景
# 就会失效 —— 实测 resize 前窗口外区域透出桌面,resize 后变成 WinForms
# Form 的默认灰 #F0F0F0。色键(WS_EX_LAYERED)对 WebView2 这个**子窗口**
# 无效,resize 后再重设 DefaultBackgroundColor 又会被 CoreWebView2 的
# "只能从 UI 线程访问"挡下来 —— 所以唯一可靠的做法就是**别 resize**。
#
# 固定尺寸不亏:窗口是透明的,多出来的空白区域本来就看不见。
# 内容用 CSS 贴着窗口底部排(见 capsule.css 的 .stack),
# 文字条自己内部滚动,窗口大小就一直够用。
#
# 宽 512 / 高 260 是**请求值**,不是拿到手的值:pywebview 建窗后
# WinForms 的 AutoScale 会把客户区再缩一次(历史实测:曾请求
# 496×232 只拿到 482×195,缩水率约 0.97/0.84;panel.py 的
# _fit_window 注释记录的是同一个坑)。而窗口又不能事后 resize
# (一 resize 透明就废),所以只能在建窗时把请求值放大,给缩水留余量。
#
# 内容预算(CSS 逻辑像素,贴底排布):
#   底部留白 10 + 胶囊 40 + 间隙 10 + 文字条最大 120(+2 边框)
#   + 顶部留白 ≈ 194;缩水后的客户区只要 ≥ 194 就不会裁内容。
#   请求 260 在 0.84 的缩水率下还有 ~219,余量足够。
WIN_W = 512
WIN_H = 260

# 底边距(逻辑像素):胶囊离屏幕底部留多远
BOTTOM_MARGIN = 20

# 各种结局之后,胶囊停留多久再隐藏(秒)
HIDE_DELAY_OK = 1.5       # 识别成功并已注入
HIDE_DELAY_ERROR = 4.5    # 出错:错误文案要让人看清
HIDE_DELAY_CANCEL = 1.0   # 取消

# 启动守卫:建窗后的这段时间里盯着窗口,别让 pywebview 的建窗副作用
# 把它亮出来或者抢走焦点(见 _on_before_show 的说明)
GUARD_SECONDS = 5.0
GUARD_INTERVAL = 0.08
# 建窗副作用结束后,守卫转入稀疏轮询**常驻**:胶囊可见期间前台
# 一旦被它自己抢走(WebView2 子窗口的点击激活,跨进程拦不掉),
# 就立刻归还。见 _guard_loop 的说明。
GUARD_SPARSE = 0.15

# 控制器状态 -> 前端状态字符串
_STATE_NAMES = {
    State.STARTING: "starting",
    State.IDLE: "idle",
    State.RECORDING: "recording",
    State.PROCESSING: "processing",
    State.ERROR: "error",
}

# 窗口命中区域裁形(SetWindowRgn):见 _on_update_regions 的说明。
#
# 每边外扩的余量(**物理**像素):逻辑坐标换物理像素有取整误差,
# 抗锯齿的边缘还可能差半个像素 —— 不留余量的话,文字条/胶囊的边
# 会被切掉一圈,按钮可能点不中。
REGION_MARGIN = 8

# 前端上报的两块内容(顺序固定,并集的结果与顺序无关,但好读)
_REGION_KEYS = ("textbar", "pill")


def _parse_regions(payload: Any) -> list[tuple[float, float, float, float]] | None:
    """把前端上报的矩形解析成 [(x, y, w, h), ...](**逻辑**像素)。

    payload 形如 ``{"textbar": [x, y, w, h], "pill": [x, y, w, h]}``;
    一块全 0 表示**那块隐藏**(不参与并集)。

    返回 None 表示这次上报没法用,调用方该原样忽略 —— 前端偶尔会
    报个半截数据,宁可这一帧不裁,也不能把窗口裁歪了。
    """
    if isinstance(payload, str):
        # 兜底:万一前端传的是 JSON 字符串而不是对象
        try:
            payload = json.loads(payload)
        except Exception:
            return None
    if not isinstance(payload, dict):
        return None

    rects: list[tuple[float, float, float, float]] = []
    for key in _REGION_KEYS:
        raw = payload.get(key)
        if raw is None:
            continue
        if isinstance(raw, dict):
            raw = [raw.get("x"), raw.get("y"), raw.get("w"), raw.get("h")]
        if not isinstance(raw, (list, tuple)) or len(raw) != 4:
            return None
        try:
            x, y, w, h = (float(v) for v in raw)
        except (TypeError, ValueError):
            return None
        rects.append((x, y, w, h))
    if not rects:
        return None
    return rects


class _CapsuleApi:
    """暴露给胶囊前端 JS 的桥接对象。

    单独一个类:pywebview 的 ``get_functions`` 会遍历公开属性,遇到
    非 callable 的对象还会**递归**下去 —— 直接把 VoiceCapsule 交出去,
    控制器和配置的内部属性都会被暴露到网页里。这里只留方法,
    引用用下划线开头(pywebview 会跳过)。
    """

    def __init__(self, capsule: VoiceCapsule):
        self._capsule = capsule

    def ready(self) -> None:
        """前端 DOM 就绪。"""
        self._capsule._on_frontend_ready()

    def cancel_recording(self) -> None:
        """放弃这一轮:不识别、不注入。"""
        self._capsule._give_back_focus()
        self._capsule.controller.cancel_recording()

    def stop_recording(self) -> None:
        """结束录音,进入识别。"""
        # 点这个按钮的鼠标点击可能把前台激活到了胶囊上(WebView2
        # 子窗口的激活,NOACTIVATE 树挡不住的残余情形)。
        # 注入靠"粘贴到前台窗口",所以先归还前台,再停录音。
        self._capsule._give_back_focus()
        self._capsule.controller.stop_recording()

    def update_regions(self, payload: Any) -> None:
        """前端上报"文字条 + 胶囊"的矩形(CSS 逻辑像素)。

        Python 侧拿它裁窗口的命中区(SetWindowRgn)—— 把大片透明空白
        从窗口里挖掉,空白区就不再挡住底下应用的点击。
        """
        self._capsule._on_update_regions(payload)


class VoiceCapsule:
    """录音胶囊悬浮窗。"""

    # 胶囊要电平(面板不要)
    wants_levels = True

    def __init__(self, controller: VoiceController, focus_restore: int = 0):
        """`focus_restore` 是**启动时**的前台窗口句柄。

        pywebview 建 hidden 窗口时会偷偷 Show 一次(它的透明 hack),
        那一下会把窗口激活 —— 胶囊要是成了前台窗口,用户的键盘输入
        就进了胶囊,后面注入的文字也没落点了。所以窗口建好后要把
        前台还给用户原来那个窗口。
        """
        self.controller = controller
        self._focus_restore = focus_restore

        self._state = State.STARTING
        self._gui_ready = threading.Event()
        self._destroyed = False
        self._last_preview = ""      # 最近一次预览文字(前端晚就绪时补推)
        # 这一轮录音开始时用户所在的前台窗口(= 注入落点)。
        # 点 ✓/✕ 的点击若把前台激活到胶囊上,靠它归还。
        self._target_hwnd = 0
        # 胶囊当前是不是"该给用户看"。pywebview 建窗时会偷偷 Show 一次,
        # 守卫靠这个标志把那次偷偷的显示藏回去。
        self._want_visible = False
        # 启动守卫只起一次
        self._guard_started = False

        # 隐藏定时器:新一轮录音开始时要作废旧的,否则上一轮的
        # 定时器会把刚开始的胶囊又藏掉
        self._hide_timer: threading.Timer | None = None
        self._hide_lock = threading.Lock()

        # fit(量高度 + resize)整套删掉了 —— 透明窗口一旦被 resize,
        # WebView2 的透明背景就失效(见 WIN_W / WIN_H 的说明)

        import webview

        self._api = _CapsuleApi(self)
        html = STATIC_DIR / "capsule.html"
        self._window = webview.create_window(
            "语音输入",
            url=html.as_uri(),
            js_api=self._api,
            width=WIN_W,
            height=WIN_H,
            frameless=True,
            hidden=True,
            transparent=True,
            shadow=False,        # 透明窗口上阴影会渲染成灰边
            on_top=True,
            resizable=False,
            easy_drag=False,     # 胶囊不拖拽、不记位置
            focus=False,         # pywebview 也会加 WS_EX_NOACTIVATE
            background_color="#000000",
        )
        self._window.events.before_show += self._on_before_show
        self._window.events.shown += self._on_window_shown

    # ---------- 推送目标接口 ----------

    def handle_event(self, event: UIEvent) -> None:
        """控制器事件 → 胶囊前端。

        这里同时管**窗口的显隐**:进入录音就亮出来,出了结果/错误/
        取消就定时藏回去。状态事件本身不负责隐藏 ——
        识别成功时控制器先把状态置回 IDLE 再发 result,
        照着状态藏会把"已输入"的提示一起藏掉。
        """
        kind = event.kind
        if kind == "state" and event.state is not None:
            self._state = event.state
            self._call_js("setState", _STATE_NAMES.get(event.state, "idle"))
            if event.state == State.RECORDING:
                # 新一轮录音:作废上一轮的隐藏定时器,把窗口亮起来。
                # 先记下用户此刻所在的前台窗口 —— 它就是这一轮文字的
                # 注入落点。此刻胶囊还没显示,前台必然是用户自己的窗口;
                # 万一后面前台被胶囊抢走(点 ✓/✕ 的点击激活),
                # 靠这个快照把它还回去。
                self._target_hwnd = get_foreground_window()
                self._cancel_hide_timer()
                self._call_js("resetForRecording")
                self._show()
        elif kind == "preview":
            # 实时预览文字(录音过程中已识别出的内容)
            self._last_preview = event.text
            self._call_js("setPreview", event.text)
        elif kind == "result":
            # 识别完成且已注入:文字条定格最终结果
            self._call_js("setResult", event.text)
            self._schedule_hide(HIDE_DELAY_OK)
        elif kind == "error":
            self._call_js("setError", event.text)
            self._schedule_hide(HIDE_DELAY_ERROR)
        elif kind == "cancelled":
            self._call_js("setCancelled")
            self._schedule_hide(HIDE_DELAY_CANCEL)
        elif kind == "info":
            # 待机时的提示(模型就绪、麦克风名)不打扰用户 ——
            # 这时候胶囊本来就是隐藏的
            pass

    def push_levels(self, levels: list[float], elapsed: float) -> None:
        """30fps 电平 + 计时。

        推的是**绝对电平**(dBFS 映射后的 0..1),前端绝不做窗口内
        归一化 —— 归一化之后波形永远看起来是满的,用户就没法从条的高
        低看出"我说话声音大不大"了。
        """
        if self._state != State.RECORDING:
            return
        self._call_js_pair("setLevels", json.dumps(levels),
                           "setElapsed", elapsed)

    # ---------- 前端桥接 ----------

    def _call_js(self, method: str, *args: Any) -> bool:
        """调用前端方法。

        前端可能还没加载完,所以在同一个表达式里先判断方法存在再调用 ——
        这样存在性检查和调用之间不会有竞态,也只花一次往返。
        """
        if not self._gui_ready.is_set():
            return False
        payload = ", ".join(json.dumps(a, ensure_ascii=False) for a in args)
        script = (
            "(function(){try{"
            f"if(window.__capsule&&typeof window.__capsule.{method}==='function'){{"
            f"window.__capsule.{method}({payload});return true;}}"
            "}catch(e){return false;}return false;})()"
        )
        try:
            self._window.evaluate_js(script)
            return True
        except Exception as e:
            log.debug("调用前端 %s 失败: %s", method, e)
            return False

    def _call_js_pair(self, m1: str, a1: Any, m2: str, a2: Any) -> bool:
        """一次往返里调两个前端方法(电平 + 计时一起推,省一半开销)。"""
        if not self._gui_ready.is_set():
            return False
        p1 = json.dumps(a1, ensure_ascii=False)
        p2 = json.dumps(a2, ensure_ascii=False)
        script = (
            "(function(){try{"
            f"if(window.__capsule&&typeof window.__capsule.{m1}==='function'"
            f"&&typeof window.__capsule.{m2}==='function'){{"
            f"window.__capsule.{m1}({p1});window.__capsule.{m2}({p2});return true;}}"
            "}catch(e){return false;}return false;})()"
        )
        try:
            self._window.evaluate_js(script)
            return True
        except Exception as e:
            log.debug("调用前端 %s/%s 失败: %s", m1, m2, e)
            return False

    def _on_frontend_ready(self) -> None:
        """前端就绪:放行推送,补推一次当前状态,并把窗口摆好。

        补推是因为前端就绪可能晚于控制器事件(比如用户一启动就按热键):
        那些事件当时推不出去,现在要补上,不然胶囊会停在空状态。
        """
        self._gui_ready.set()
        self._call_js("setState", _STATE_NAMES.get(self._state, "idle"))
        if self._last_preview:
            self._call_js("setPreview", self._last_preview)
        self._place()

    def _on_before_show(self) -> None:
        """建窗的**最后一步**:掐掉 pywebview 的抢焦点 hack,并先设好样式。

        pywebview 对透明窗口有一个 hack(winforms 后端
        ``EdgeChrome.on_navigation_start``):页面**开始导航**时它会
        ``form.Show()`` + ``form.Activate()``。源码注释里写着
        "no idea why this works" —— 但代价实打实:

          - 胶囊在**启动时**就自己显示出来了(用户还没按热键就能看见)
          - 而且它成了**前台窗口**,用户的键盘输入全进了胶囊,
            后面注入的文字也就没有落点了

        ``form.Activate()`` 走的是 WinForms 的 ``SetForegroundWindow``,
        ``WS_EX_NOACTIVATE`` **拦不住**它 —— 只有"根本不执行"才拦得住。

        定点修法:在 ``before_show`` 事件里把 ``window.transparent`` 置 False。
        这个时机是唯一能同时满足两件事的点:

          - BrowserForm / EdgeChrome 的构造函数**已经**按 transparent=True
            设好了 WebView2 的 ``DefaultBackgroundColor = Transparent``
            —— 透明效果不受影响(实测:胶囊外的桌面照常透出来)
          - 而导航还没开始,``on_navigation_start`` 读到的 transparent
            已经是 False → 那段 hack 不再执行

        ``before_show`` 是同步事件(``Event(self, True)``),所以这里的修改
        一定早于导航开始,不存在竞态。

        顺带把 ``WS_EX_NOACTIVATE`` / ``WS_EX_TOOLWINDOW`` 设上:
        ``shown`` 事件是**异步**触发的(``Event(self)``,pywebview 会另起
        线程),等到那时再建样式就晚了半拍。这里是 pywebview 把
        ``window.native`` 挂上的第一个时机,句柄已经可用。
        """
        self._window.transparent = False
        hwnd = self._native_handle()
        if hwnd:
            set_tool_window_style(hwnd)

    def _on_window_shown(self) -> None:
        """窗口已创建。

        这里**只改样式**:加上 WS_EX_NOACTIVATE(不抢焦点)和
        WS_EX_TOOLWINDOW(不进任务栏)。显示动作由 _show 走
        ShowWindow(SW_SHOWNOACTIVATE),不能在这里调 show() ——
        shown 是 pywebview **异步**触发的事件(它会另起线程),
        在那里再调 show() 是重入,可能把窗口搞挂。

        样式其实在 ``_on_before_show`` 里已经设过一次(那时句柄刚可用),
        这里再设一遍是兜底:万一 pywebview 换了版本、before_show 没触发。
        """
        hwnd = self._native_handle()
        if hwnd:
            set_tool_window_style(hwnd)
        # 窗口建得比事件晚(启动瞬间就按热键)时,这里补一次显示
        if self._state == State.RECORDING:
            self._show()
        # 启动守卫:盯着建窗后的头几秒,别让 pywebview 的建窗副作用
        # 把胶囊亮出来或者把焦点抢走
        self._start_guard()
        # pywebview 建 hidden 窗口时会偷偷 Show 一次,那一下会把窗口激活 ——
        # 把前台还给用户原来的窗口
        self._restore_focus()

    # ---------- 启动守卫 ----------

    def _start_guard(self) -> None:
        """起一个短命的守卫线程,盯住建窗后的副作用。

        定点修复(``_on_before_show`` 里掐掉 transparent hack)已经挡掉了
        "持续抢焦点",但 pywebview 建 hidden 窗口时还有一次
        ``browser.Show()``(Opacity=0)—— 它照样会把窗口激活一下。
        守卫兜住这一下:窗口不该露面就藏回去,焦点被抢就还回去。
        """
        if self._guard_started:
            return
        self._guard_started = True
        threading.Thread(target=self._guard_loop, name="ui-capsule-guard",
                         daemon=True).start()

    def _guard_loop(self) -> None:
        # 建窗后的头几秒用密集间隔(GUARD_INTERVAL)盯建窗副作用,
        # 之后转入稀疏轮询(GUARD_SPARSE)常驻到退出。
        dense_until = time.monotonic() + GUARD_SECONDS
        # 记住用户当前在哪个窗口 —— 万一胶囊抢了前台,还回**它**
        # (动态跟踪:录音中途用户切了窗口,还的就是切换后的那个)
        last_fg = self._focus_restore
        while not self._destroyed:
            hwnd = self._native_handle()
            if hwnd:
                fg = get_foreground_window()
                if fg and fg != hwnd:
                    last_fg = fg
                # 没人要求显示却露着脸 → 藏回去(pywebview 偷偷 Show 的残留)
                if not self._want_visible and is_window_visible(hwnd):
                    hide_window(hwnd)
                elif fg == hwnd and last_fg and last_fg != hwnd:
                    # 前台被胶囊占了 → 还回去。
                    #
                    # 胶囊是"显示器"不是输入目标:WebView2 的内容窗口
                    # 属于 msedgewebview2.exe,跨进程加不了 NOACTIVATE,
                    # 鼠标点上去(哪怕只是点按钮)就会激活整棵窗口树。
                    # 与其禁止,不如抢回来 —— 归还前台**不影响**鼠标
                    # 点击消息的投递(✓/✕ 的 click 照常触发),而注入
                    # 落点由 ✓ 回调里的 _give_back_focus 双保险。
                    restore_foreground(last_fg)
            interval = (GUARD_INTERVAL if time.monotonic() < dense_until
                        else GUARD_SPARSE)
            time.sleep(interval)

    def _restore_focus(self) -> None:
        """把前台窗口还给用户原来那个。

        pywebview 建 hidden 窗口时会偷偷 Show 一次(它的透明 hack),
        shown 之后还会再调一次 ``webview.Focus()`` —— 那一下会把窗口
        变成前台。胶囊要是成了前台,用户的键盘输入就进了胶囊,
        后面注入的文字也没落点了。

        所以要还原两次:shown 回调里同步还原一次(盖住 Show 那一下),
        再延迟还原一次(盖住后面的 Focus)。延迟期间如果用户自己切了
        窗口,前台已经不是胶囊了,就不会乱抢。
        """
        self._restore_focus_once()
        threading.Thread(target=self._restore_focus_later, name="ui-focus",
                         daemon=True).start()

    def _restore_focus_later(self) -> None:
        time.sleep(0.25)
        self._restore_focus_once()

    def _restore_focus_once(self) -> None:
        hwnd = self._native_handle()
        if not hwnd or not self._focus_restore:
            return
        if get_foreground_window() != hwnd:
            return
        restore_foreground(self._focus_restore)

    def _give_back_focus(self) -> None:
        """前台若是胶囊,还给它该在的窗口(这一轮的注入落点)。

        只在前台**确实是胶囊**时动手:用户点了 ✓/✕ 之后立刻切去
        别的窗口的话,前台已经不是胶囊,不能硬拽回录音时的窗口。
        """
        hwnd = self._native_handle()
        if not hwnd or not self._target_hwnd:
            return
        if get_foreground_window() == hwnd:
            restore_foreground(self._target_hwnd)

    # ---------- 显隐 ----------

    def _show(self) -> None:
        """显示胶囊(不抢焦点)。

        顺序很重要:先改扩展样式(去掉 WS_EX_APPWINDOW、加上
        WS_EX_NOACTIVATE),再显示 —— 任务栏只在窗口显示时重新评估
        可见性,样式没改好就会在任务栏上留一个按钮。
        """
        self._want_visible = True
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            set_tool_window_style(hwnd)
            # WebView2 的子窗口没有 NOACTIVATE:鼠标点在子窗口上照样
            # 激活整棵树、抢走前台(点 ✓ 结束录音时文字就会注入给胶囊
            # 自己)。每次显示都重刷:WebView2 重建页面会换掉子窗口。
            set_no_activate_tree(hwnd)
            show_window_no_activate(hwnd)
            # 别的置顶窗口可能正好压在上面,把我们提到置顶组最顶端
            bring_to_top(hwnd)
            # 显示时再摆一次位:pywebview 建窗给的尺寸会被 WinForms 的
            # AutoScale 再缩一次,建窗时算好的位置到这时已经不准了
            self._place()
        except Exception as e:
            log.debug("显示胶囊失败: %s", e)
        # 显示后的焦点不再在这里还:这里只有"启动时"的前台句柄
        # (_focus_restore),用户中途切过窗口就会还错地方。
        # 常驻守卫(_guard_loop)用动态跟踪的用户窗口来还,目标永远对。

    def _hide(self) -> None:
        self._want_visible = False
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            hide_window(hwnd)
        except Exception as e:
            log.debug("隐藏胶囊失败: %s", e)

    def _schedule_hide(self, delay: float) -> None:
        """延时隐藏胶囊(文字条还有话要说,别急着藏)。"""
        self._cancel_hide_timer()
        timer = threading.Timer(delay, self._hide)
        timer.daemon = True
        self._hide_timer = timer
        timer.start()

    def _cancel_hide_timer(self) -> None:
        """作废隐藏定时器 —— 新一轮录音开始时必须调。"""
        with self._hide_lock:
            timer, self._hide_timer = self._hide_timer, None
        if timer is not None:
            timer.cancel()

    # ---------- 窗口命中区域(SetWindowRgn) ----------

    def _on_update_regions(self, payload: Any) -> None:
        """前端上报的两块内容矩形 → 裁掉透明空白区的命中区。

        胶囊是**固定尺寸**的透明窗口(不能 resize,resize 透明就废),
        可见内容只有"文字条 + 胶囊"两块,其余是透明空白 —— 但那片空白
        **属于窗口**:鼠标点上去会被胶囊吃掉,底下的应用就点不到了。
        把窗口形状裁成两块内容的并集,空白区就不再属于窗口,
        点击直接穿透。既不 resize,也不动透明实现。

        前端**第一次**上报之前不裁(保持整窗):那时还没量准,贸然裁
        会把可见内容切掉。

        隐藏/显示不用管:窗口区域是窗口自己的属性,ShowWindow 藏起来
        再亮出来它还在(窗口销毁时才回收)。所以不用每次显示都重设。
        """
        rects = _parse_regions(payload)
        if rects is None:
            return
        hwnd = self._native_handle()
        if not hwnd:
            return

        scale = self._scale()
        physical: list[tuple[int, int, int, int]] = []
        for (x, y, w, h) in rects:
            if w <= 0 or h <= 0:
                continue        # 隐藏的块不参与并集
            physical.append((
                int(round(x * scale)) - REGION_MARGIN,
                int(round(y * scale)) - REGION_MARGIN,
                int(round((x + w) * scale)) + REGION_MARGIN,
                int(round((y + h) * scale)) + REGION_MARGIN,
            ))
        if not physical:
            # 两块都隐藏(正常不会发生):裁成 1×1 的最小区域 ——
            # 宁可"窗口点不到",也不要"整片空白继续挡着底下的应用"
            physical = [(0, 0, 1, 1)]

        log.debug("裁窗口区域: %s", physical)
        try:
            set_window_regions(hwnd, physical)
        except Exception as e:
            log.debug("裁窗口区域失败: %s", e)

    # ---------- 尺寸与位置 ----------

    def _place(self) -> None:
        """摆到工作区底部居中。

        用**工作区**而不是整块屏幕 —— 后者会被任务栏盖住底边。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        area = get_work_area()
        rect = get_window_rect(hwnd)
        if area is None or rect is None:
            return
        left, top, right, bottom = area
        width = rect[2] - rect[0]
        height = rect[3] - rect[1]
        margin = int(round(BOTTOM_MARGIN * self._scale()))
        x = left + ((right - left) - width) // 2
        y = bottom - height - margin
        try:
            move_window_to(hwnd, int(x), int(y))
        except Exception as e:
            log.debug("摆胶囊位置失败: %s", e)

    def _scale(self) -> float:
        """主显示器缩放系数(胶囊固定在主显示器上)。"""
        try:
            import ctypes

            dpi = ctypes.windll.user32.GetDpiForSystem()
            if dpi:
                return max(1.0, dpi / 96.0)
        except Exception:
            pass
        return 1.0

    # ---------- 生命周期 ----------

    def _native_handle(self) -> int:
        """底层 Win32 窗口句柄(0 = 还没建好)。"""
        try:
            native = getattr(self._window, "native", None)
            if native is None:
                return 0
            return int(native.Handle.ToInt64())
        except Exception:
            return 0

    def destroy(self) -> None:
        """销毁胶囊窗口。

        必须一起销毁:pywebview 的主循环要等**所有**窗口都没了才返回,
        只关面板的话程序会变成"托盘没了但还活着"的僵尸。
        """
        if self._destroyed:
            return
        self._destroyed = True
        self._cancel_hide_timer()
        try:
            self._window.destroy()
        except Exception as e:
            log.debug("销毁胶囊窗口失败: %s", e)
