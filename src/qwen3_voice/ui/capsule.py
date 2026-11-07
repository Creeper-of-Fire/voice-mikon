"""录音胶囊悬浮窗(pywebview + WebView2,透明无边框)。

胶囊是录音过程的**唯一**视觉载体:上方一条实时预览文字,下方一颗
胶囊(取消 / 波形+计时 / 结束)。面板不常驻时,用户按热键录音全程
靠它反馈状态 —— 没有它,录音就是"盲录"。

前端是纯静态 HTML/CSS/JS(``static/capsule.{html,css,js}``),
零框架零构建,和面板同一套约定。

Python 与前端之间的桥接契约(完整见同目录 ``BRIDGE.md``):

  Python → JS   window.__capsule.setState / setLevels / setElapsed
                / setPreview / setResult / setError / setCancelled
                / resetForRecording / setTheme
                / setPositionMode / setPreviewEnabled
                / setOrbMode / setHideDelay
  JS → Python   window.pywebview.api.ready / cancel_recording / stop_recording
                / update_regions / move_capsule / begin_drag / end_drag
                / copy_result / start_recording / move_orb

胶囊出现在哪由三档决定(配置项 ``capsule_position``):

  follow  跟着输入光标(默认)—— 按热键那一刻捕获一次定位信号,
          贴着光标出现,像输入法候选框
  bottom  固定在主屏工作区底部居中(原来的行为)
  custom  用户拖到哪就记在哪(相对所在工作区的逻辑偏移,换屏不飞)

常驻悬浮球(配置项 ``floating_orb``,默认关)是这个窗口的**另一种形态**,
不是另一个窗口 —— 而且也不是另一个元素:

  屏幕上常驻一颗小球(麦克风图标)。点它 → 球**原地**展开成现在的
  录音胶囊 → 开始录音;录完胶囊收回成球。拖球可以挪位置,松手记住。
  关掉这个开关时行为和以前**一模一样**(按热键才弹胶囊)。

  那颗球**就是这颗胶囊收窄成的正圆**(前端只有一颗元素,见
  capsule.css 的「常驻悬浮球」一节):球和胶囊一样大(40px =
  胶囊的高度),展开时是同一块矩形横向长开 —— 没有第二个元素、
  没有接缝,也就不会出现"两个形状互相裁切"的问题(真机症状:
  展开瞬间球的上半截被切出一条白线)。

  球和胶囊共用同一个固定尺寸窗口,所以"球在哪"= "窗口摆哪":
  球固定待在窗口的底边居中,胶囊也在底边居中 —— 展开时球"长成"
  胶囊,位置不用挪。球的位置**独立记忆**(orb_geometry / orb_monitor),
  和胶囊自定义档那套(capsule_geometry / capsule_monitor)互不干扰。

  球的位置三档(配置项 ``orb_position``,设计由用户拍板):

    follow  跟随输入光标 —— 光标真的聚焦在输入处才露脸,
            贴着光标出现(像输入法候选框);位置不归用户管
    pinned  记住的位置(可拖、持久化) —— 只在光标聚焦到输入处
            才露脸,显隐和 follow 同一套一致性
    custom  记住的位置(可拖、持久化),常驻露脸

  pinned 和 custom **共用同一份** orb_geometry(位置都是用户拖出来
  记住的),差别只在显隐;和胶囊的 capsule_position 是两套独立设计,
  档位名相同也不代表含义相同。

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
from typing import Any, Callable

from ..config import CAPSULE_POSITION_MODES, ORB_POSITION_MODES, THEME_MODES
from ..core.controller import State, UIEvent, VoiceController
from ..logging_setup import get_logger
from ..win32 import clipboard
from ..win32.api import (
    bring_to_top,
    enable_layered_window,
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
    upload_transparent_surface,
)
from ..win32.caret import capture_anchor, warmup
from ..win32.monitors import (
    monitor_at_point,
    monitor_by_device,
    primary_monitor,
)
from .placement import (
    bottom_position,
    custom_position,
    default_orb_position,
    encode_offset,
    follow_position,
    orb_position_from_geometry,
    orb_screen_position,
    orb_window_position,
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
# 出错而且**带着识别原文**:用户多半要点胶囊上的「复制」把文字取走,
# 4.5 秒不够 —— 留久一点,别让人手忙脚乱。
HIDE_DELAY_ERROR_COPY = 12.0
HIDE_DELAY_CANCEL = 1.0   # 取消

# 写完剪贴板后等一下再读回校验。剪贴板是异步通知的,立刻读有时会
# 读到旧内容 —— 宁可多等几十毫秒,也不能把"没写进去"当成写成功了。
CLIPBOARD_VERIFY_DELAY = 0.05

# 常驻悬浮球:前端就绪后等这么久再让球露脸。
# 窗口是固定尺寸的透明窗,必须等前端把"球"那一小块报上来、
# Python 侧裁好命中区之后才能露脸 —— 否则那 512×260 的透明空白
# 会把底下应用的点击全吃掉(前端的区域上报有 60ms 的尾沿节流)。
ORB_SHOW_DELAY = 0.3

# 跟随输入档的轮询间隔(秒)。
#
# 250ms:够跟手(光标一动,球最多晚四分之一秒跟过去),
# 又不至于太密 —— UIA 那一级是 COM 调用,几十毫秒的开销,
# 每秒跑四十次纯属烧 CPU。
ORB_FOLLOW_INTERVAL = 0.25

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
#
# 余量从 8 收到 3:窗口区域现在**同时**是"视觉裁剪"和"命中区"。
# 透明做成真透明之后,窗口里 WebView2 透明的地方透出的是**桌面**,
# 裁出来多一圈就多一圈"看不见但能点"的边(点空白处会被胶囊吃掉),
# 裁少了则把抗锯齿的边缘切硬。余量 3 物理像素足够盖住 175% 缩放下
# 约 1.75 像素的抗锯齿带,又不至于把一大片空白收回窗口里。
REGION_MARGIN = 3

# 圆角矩形的圆角半径(**逻辑** CSS 像素,取自 capsule.css)。
#
# 裁形要贴合真实外形,不然边缘会被切:胶囊是**跑道形**(radius 999px,
# 两端是半圆),文字条是 14px 圆角,浮层是 12px 圆角。
# 用椭圆裁胶囊会把跑道形的两端切掉一大块(实测误切 17.9%)。
TEXTBAR_RADIUS_PX = 14
OVERLAY_RADIUS_PX = 12

# 命中刷新间隔(秒):见 _guard_loop 里的说明。
#
# 0.5s:实测一次刷新能管 1s 上下(退化快慢与别的窗口有没有重绘有关),
# 留一倍余量。
HIT_REFRESH_INTERVAL = 0.5

# 前端上报的几块内容(顺序固定,并集的结果与顺序无关,但好读)
#
# textbar / pill / overlay:
#   textbar  上方的文字条
#   pill     胶囊(常驻悬浮球模式下,待机时它**就是**那颗球)
#   overlay  球的浮层(右键菜单 / 操作提示)—— 单独一块,不能并进 pill
#
# overlay 必须单独一块:并进 pill 就成了"球到菜单"的大包围盒,那块
# 中间全是空白,空白落在窗口裁形里就会露出 WebView2 客户区的灰底
# (实测:球上方罩着一大片灰)。分开报,裁形才能一块圆一块方。
_REGION_KEYS = ("textbar", "pill", "overlay")


def _parse_regions(payload: Any) -> dict[str, tuple[float, float, float, float]] | None:
    """把前端上报的矩形解析成 ``{块名: (x, y, w, h)}``(**逻辑**像素)。

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

    out: dict[str, tuple[float, float, float, float]] = {}
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
        out[key] = (x, y, w, h)
    if not out:
        return None
    return out


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

    def move_capsule(self, x: float, y: float) -> None:
        """拖动中:窗口左上角移到 (x, y) —— **绝对位置,CSS(逻辑)像素**。

        和面板的 ``move_window`` 同一套约定:前端每次都从 mousedown
        起点算绝对目标,发过来的不是增量 —— 某一次调用丢了或慢了,
        下一次直接覆盖,天然不漂。
        """
        self._capsule._move_capsule_abs(x, y)

    def begin_drag(self) -> None:
        """用户开始拖胶囊(按住不放)。"""
        self._capsule._begin_drag()

    def end_drag(self) -> None:
        """用户松手:归还焦点 + 记住位置。"""
        self._capsule._end_drag()

    def copy_result(self, text: str) -> bool:
        """把识别结果手动写进剪贴板(注入失败时的兜底出口)。

        返回**是否真的写进去了** —— 前端要靠这个结果决定是收胶囊
        (拿到了)还是让用户再试一次(没拿到)。
        """
        return self._capsule._copy_result(text)

    def start_recording(self) -> None:
        """点悬浮球开始录音。

        ⚠ 这一步踩的是**焦点陷阱**:鼠标点在 WebView2 的子窗口上会
        激活整棵窗口树,前台被胶囊抢走 —— 那一轮识别完的文字就会贴到
        胶囊自己身上,而不是用户的输入框。所以开始录音**之前**必须把
        前台还给用户原先所在的窗口(见 ``_start_recording_from_orb``)。
        """
        self._capsule._start_recording_from_orb()

    def move_orb(self, x: float, y: float) -> None:
        """拖动悬浮球:窗口左上角移到 (x, y) —— **绝对位置,CSS(逻辑)像素**。

        和 ``move_capsule`` 同一套约定(发绝对目标,天然不漂),但摆位
        走的是"球"那套规则:球的位置会被夹回允许的范围,窗口跟着反推。
        """
        self._capsule._move_orb_abs(x, y)

    def open_panel(self) -> None:
        """球菜单里的「设置」:打开面板。

        面板的引用由入口在组装时注入(set_panel_opener)—— 胶囊自己
        不知道面板存在,只认回调。没注入(比如面板建不起来)就忽略。
        """
        self._capsule._open_panel()

    def quit_app(self) -> None:
        """球菜单里的「退出」:退出整个程序。

        走的回调由入口注入(set_quit_handler),和托盘的「退出」是
        **同一条清理路径**(热键注销 → 窗口销毁 → 主循环返回)。
        """
        self._capsule._quit_from_menu()

    def selecting_text(self, selecting: bool) -> None:
        """前端上报"用户正在/结束选文本"。

        选文本期间守卫**不抢前台** —— 抢了 WebView2 就失去键盘焦点,
        用户按 Ctrl+C 复制不了(框选了却复制不了,实测踩过)。
        """
        self._capsule._on_selecting_text(selecting)


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
        # 常驻悬浮球模式(配置项 floating_orb):开着时窗口常驻,
        # 胶囊只是这个窗口的另一种形态。真相源是配置项本身。
        # 球是不是已经就位(窗口该露着)。启动时前端还没报区域,
        # 窗口不能先露脸 —— 那片透明空白会吃掉底下的点击。
        self._orb_shown = False
        # 用户当前所在的窗口(守卫动态跟踪)。点悬浮球开始录音时
        # 要靠它把前台还回去 —— 见 _start_recording_from_orb。
        self._last_foreground = 0
        # 启动守卫只起一次
        self._guard_started = False
        # 球菜单的两个出口(打开面板 / 退出程序):回调由入口在组装时
        # 注入(set_panel_opener / set_quit_handler)—— 胶囊不引用面板
        # 和托盘,只认回调。没注入就当没有这个功能。
        self._panel_opener: Callable[[], None] | None = None
        self._quit_handler: Callable[[], None] | None = None

        # 隐藏定时器:新一轮录音开始时要作废旧的,否则上一轮的
        # 定时器会把刚开始的胶囊又藏掉
        self._hide_timer: threading.Timer | None = None
        self._hide_lock = threading.Lock()
        # "稍后让球露脸"的定时器(常驻模式)
        self._orb_timer: threading.Timer | None = None
        # 跟随输入档的轮询线程:盯着真光标,球跟着它走。
        # 只在「常驻球 + 跟随输入/固定底部」时才有 —— 这两档都要
        # 检测"光标聚焦在输入处没",只是 follow 挪位置、bottom 只管显隐。
        self._follow_thread: threading.Thread | None = None
        self._follow_lock = threading.Lock()
        # 轮询线程的"代数":每次起/停线程都 +1,线程醒来发现代数变了
        # 就立刻退出、不再动窗口 —— 切档时旧线程的"最后一次挪动"
        # 不会覆盖新档位的摆位(切档"不生效"的根因就是这个竞态)。
        self._follow_epoch = 0
        # 前端报过命中区没有(SetWindowRgn 裁好窗口形状了)。
        # 没裁好之前不能露脸 —— 那片透明空白会吃掉底下的点击。
        self._orb_regions_ready = False
        # 已经上传过的透明 surface 的窗口尺寸(见 _ensure_transparent_surface)。
        # 尺寸没变就不用重传 —— 重传会吞掉 WebView2 的画面。
        self._surface_size: tuple[int, int] | None = None

        # 跟随输入模式的定位信号点:按热键那一刻捕获一次,**这一轮
        # 录音内不再动**(位置可预期比位置聪明更重要)。
        # 没捕获到(一点信号都拿不到)就是 None —— 走"主屏底部居中"。
        self._anchor: Any = None
        # 用户正拖着胶囊:拖动期间守卫**不**抢回焦点,
        # 松手时统一还一次(抢来抢去会让 WebView2 的鼠标捕获不稳)
        self._dragging = False
        self._drag_lock = threading.Lock()
        # 用户正在文字条里选文本:这段时间守卫不抢前台(抢了 WebView2
        # 就失去键盘焦点,Ctrl+C 复制不了)。前端上报,见 _on_selecting_text
        self._selecting_text = False
        # 按下时的窗口位置:松手时对比一下,没动过就别去写配置
        self._drag_start_rect: tuple[int, int, int, int] | None = None

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
            easy_drag=False,     # 自己实现拖动(custom 模式才允许拖)
            focus=False,         # pywebview 也会加 WS_EX_NOACTIVATE
            background_color="#000000",
        )
        self._window.events.before_show += self._on_before_show
        self._window.events.shown += self._on_window_shown

        # 预热 UIA:第一次调用要几百毫秒(类型库加载 + COM 初始化)。
        # 那笔开销不能落在"用户刚按完热键"那一刻 —— 不然胶囊会迟到半秒。
        threading.Thread(target=warmup, name="ui-caret-warmup",
                         daemon=True).start()

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
                # 注入落点。
                self._snapshot_target_hwnd()
                self._cancel_hide_timer()
                # **显示之前**把定位信号捕获下来:要的是"按热键那一刻"
                # 的光标位置,不是"画完胶囊再问"的稍晚一点的位置。
                self._capture_anchor()
                # 显示前把当前主题再推一遍:面板改主题是**异步**的
                # (走推送线程),用户改完主题马上按热键的话,setTheme
                # 可能还堵在队列里 —— 这里补推一次,保证胶囊亮起来
                # 时配色已经是对的。setTheme 幂等,多推无害。
                # 位置模式、预览开关同理:面板那边改完马上按热键,
                # 事件可能还堵在队列里。
                self._call_js("setTheme", self._theme_mode())
                self._call_js("setPositionMode", self._position_mode())
                self._call_js("setPreviewEnabled", self._preview_enabled())
                self._call_js("resetForRecording")
                self._show()
        elif kind == "theme" and event.text:
            # 面板改了主题档位 → 跟着换配色
            self._call_js("setTheme", event.text)
        elif kind == "capsule_position" and event.text:
            # 面板改了胶囊位置模式
            self._call_js("setPositionMode", event.text)
        elif kind == "preview_enabled":
            # 面板改了预览开关(事件文本是 "1" / "0")
            self._call_js("setPreviewEnabled", event.text not in ("0", ""))
        elif kind == "preview":
            # 实时预览文字(录音过程中已识别出的内容)
            self._last_preview = event.text
            # 预览关着就一个字都不往前端送:文字条根本不出现,
            # 也省掉每句都来一次 IPC。
            if not self._preview_enabled():
                return
            self._call_js("setPreview", event.text)
        elif kind == "result":
            # 识别完成且已注入:文字条定格最终结果
            self._push_hide_delay(HIDE_DELAY_OK)
            self._call_js("setResult", event.text)
            self._schedule_hide(HIDE_DELAY_OK)
        elif kind == "error":
            # 错误**可能带着识别原文**(注入失败时):一起发给前端,
            # 前端据此显示全文,并给出「复制」按钮。
            payload = event.payload or ""
            delay = HIDE_DELAY_ERROR_COPY if payload else HIDE_DELAY_ERROR
            self._push_hide_delay(delay)
            self._call_js("setError", event.text, payload)
            self._schedule_hide(delay)
        elif kind == "cancelled":
            self._push_hide_delay(HIDE_DELAY_CANCEL)
            self._call_js("setCancelled")
            self._schedule_hide(HIDE_DELAY_CANCEL)
        elif kind == "orb_enabled" and event.text:
            # 面板改了「常驻悬浮球」开关:球要么冒出来,要么收回去。
            # 配置项才是真相源(面板是先存配置再推事件),这里只管反应。
            enabled = event.text not in ("0", "")
            self._call_js("setOrbMode", enabled)
            if enabled:
                # 前端还没就绪就先别露脸(命中区没裁好,大片透明空白
                # 会吃掉底下的点击)—— 前端就绪时会自己安排露脸。
                if self._gui_ready.is_set():
                    self._show_orb()
                # 跟随档:起轮询线程(球要跟着光标走)
                self._start_follow_thread()
            else:
                self._hide_orb()
        elif kind == "orb_position" and event.text:
            # 面板改了悬浮球的位置档位(follow / pinned / custom)
            mode = event.text
            if mode != "follow":
                # 先停轮询线程再摆位:线程可能正攥着一个刚捕获的锚点,
                # 不停的话它醒来会把球又挪回光标处(或者藏掉),
                # 新档位的摆位就被覆盖了 —— 那正是"切了档没反应"的根因。
                self._stop_follow_thread()
            # 前端要知道能不能拖:follow 的位置归光标,pinned/custom
            # 的位置归用户(可拖)。
            self._call_js("setOrbPosition", mode)
            if mode == "follow":
                self._start_follow_thread()
            elif self._orb_enabled() and self._gui_ready.is_set():
                # pinned / custom:摆到记忆的位置(没拖过就是右下角),
                # 两档共用一份 orb_geometry。重新露脸 —— pinned 的显隐
                # 接下来由轮询线程接管(没聚焦就收回去),custom 常驻。
                self._show_orb()
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

    # ---------- 主题 ----------

    def _theme_mode(self) -> str:
        """当前主题档位(auto / light / dark)。

        读不到就按 auto —— 兜底"跟随系统"不会翻车。
        """
        try:
            mode = str(getattr(self.controller.config, "theme", "auto")
                       or "auto").strip().lower()
        except Exception:
            return "auto"
        return mode if mode in THEME_MODES else "auto"

    # ---------- 位置模式 / 预览开关 ----------

    def _position_mode(self) -> str:
        """当前胶囊位置模式(follow / bottom / custom)。

        读不到就按 follow(默认档):贴着光标出现是用户最常用的期待。
        """
        try:
            mode = str(getattr(self.controller.config, "capsule_position",
                               "follow") or "follow").strip().lower()
        except Exception:
            return "follow"
        return mode if mode in CAPSULE_POSITION_MODES else "follow"

    def _preview_enabled(self) -> bool:
        """要不要显示实时预览文字条。

        读不到就按开 —— 预览是默认体验,不该因为读不到配置就消失。
        """
        try:
            return bool(getattr(self.controller.config, "show_preview", True))
        except Exception:
            return True

    def _orb_enabled(self) -> bool:
        """常驻悬浮球模式开没开(配置项 ``floating_orb``)。

        默认**关** —— 关着时行为和老版本一模一样(按热键才弹胶囊),
        老用户的体验一点不变。
        """
        try:
            return bool(getattr(self.controller.config, "floating_orb", False))
        except Exception:
            return False

    def _orb_position_mode(self) -> str:
        """当前悬浮球的位置档位(follow / bottom / custom)。

        读不到就按 follow(默认档):球跟着光标走是用户最常用的期待。
        """
        try:
            mode = str(getattr(self.controller.config, "orb_position",
                               "follow") or "follow").strip().lower()
        except Exception:
            return "follow"
        return mode if mode in ORB_POSITION_MODES else "follow"

    def _push_hide_delay(self, seconds: float) -> None:
        """告诉前端"这一轮结束后停多久再收起"(毫秒)。

        常驻球模式下胶囊是**前端自己**收回球的(动效在前端做),
        所以 Python 不能等隐藏定时器响了再回头调一次前端 ——
        那是隐藏定时器线程去 evaluate_js,会和推送线程抢同一个
        WebView2,顺序就乱了(见 BRIDGE.md)。

        非常驻模式下前端用不着这个数,不用推。
        """
        if not self._orb_enabled():
            return
        self._call_js("setHideDelay", int(round(seconds * 1000)))

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
        # 主题也补推一次:前端可能比 Python 晚就绪,那时的档位没推出去
        self._call_js("setTheme", self._theme_mode())
        # 位置模式与预览开关同理(面板那边改的,可能还堵在队列里)
        self._call_js("setPositionMode", self._position_mode())
        self._call_js("setPreviewEnabled", self._preview_enabled())
        # 常驻球模式同理:前端得知道现在是"球形态"还是"胶囊形态"
        self._call_js("setOrbMode", self._orb_enabled())
        # 球的位置档位也得知道:跟随档下前端**不响应拖动**
        # (球的位置由光标决定,拖了会被下一轮轮询拉回去)
        self._call_js("setOrbPosition", self._orb_position_mode())
        if self._last_preview:
            self._call_js("setPreview", self._last_preview)
        if self._orb_enabled():
            # 常驻球:等前端把"球"那一小块报上来再露脸 ——
            # 窗口是固定尺寸的,没裁形状之前那 512×260 的透明空白
            # 会把底下应用的点击全吃掉。
            self._schedule_show_orb()
            # follow/pinned 档:起轮询线程(follow 挪位置,pinned 管显隐)
            self._start_follow_thread()
        # 跟随输入模式**不在这里**摆位:那时候还没有"这一轮的信号点",
        # 摆出来的位置没有意义(等第一次录音时按光标摆)。
        elif self._position_mode() != "follow":
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
            # 加 WS_EX_LAYERED:窗口支持逐像素 alpha,WebView2 的透明
            # 背景才能真的透出桌面(而不是 pywebview 没设 Form.BackColor
            # 留下的那块 WinForms 灰底),命中判定也才能按 alpha 走。
            # 必须在窗口显示**之前**加:换扩展样式会让系统重算窗口样子。
            enable_layered_window(hwnd)

    def _on_window_shown(self) -> None:
        """窗口已创建。

        这里**只改样式**:加上 WS_EX_NOACTIVATE(不抢焦点)和
        WS_EX_TOOLWINDOW(不进任务栏)。显示动作由 _show 走
        ShowWindow(SW_SHOWNOACTIVATE),不能在这里调 show() ——
        shown 是 pywebview **异步**触发的事件(它会另起线程),
        在那里再调 show() 是重入,可能把窗口搞挂。

        样式其实在 ``_on_before_show`` 里已经设过一次(那时句柄刚可用),
        这里再设一遍是兜底:万一 pywebview 换了版本、before_show 没触发。

        这里还是**补挂 WS_EX_LAYERED 并重传 surface** 的唯一时机:
        pywebview 建 hidden 窗口的透明 hack(``Opacity=0 → Show() →
        Hide() → Opacity=1``)跑在 ``before_show`` 之后,会把 layered
        摘掉。不补的话,窗口退回 WinForms 的默认灰底,内容四周套一圈
        灰边(见 ``_ensure_transparent_surface`` 的说明)。
        """
        hwnd = self._native_handle()
        if hwnd:
            set_tool_window_style(hwnd)
            # 补挂 layered + 重传 surface:shown 落在 pywebview 那串
            # 透明 hack 之后,这时候挂才不会被它摘掉
            self._ensure_transparent_surface(hwnd, force=True)
        # 窗口建得比事件晚(启动瞬间就按热键)时,这里补一次显示
        if self._state == State.RECORDING:
            self._show()
        # 常驻球模式:前端就绪后会安排露脸(等命中区裁好),
        # 这里只是兜底 —— 万一窗口建得比前端还晚
        if self._orb_enabled() and self._gui_ready.is_set():
            self._schedule_show_orb()
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
        # 下一次刷新命中图的时间(见下面的说明)
        next_hit_refresh = time.monotonic() + HIT_REFRESH_INTERVAL
        while not self._destroyed:
            hwnd = self._native_handle()
            if hwnd:
                fg = get_foreground_window()
                if fg and fg != hwnd:
                    last_fg = fg
                # 动态跟踪用户当前所在的窗口:点悬浮球开始录音时
                # 要靠它把前台还回去(见 _start_recording_from_orb)
                self._last_foreground = last_fg
                # 没人要求显示却露着脸 → 藏回去(pywebview 偷偷 Show 的残留)
                if (not self._window_should_be_visible()
                        and is_window_visible(hwnd)):
                    hide_window(hwnd)
                elif (fg == hwnd and last_fg and last_fg != hwnd
                      and not self._is_dragging()
                      and not self._is_selecting_text()):
                    # 前台被胶囊占了 → 还回去。
                    #
                    # 胶囊是"显示器"不是输入目标:WebView2 的内容窗口
                    # 属于 msedgewebview2.exe,跨进程加不了 NOACTIVATE,
                    # 鼠标点上去(哪怕只是点按钮)就会激活整棵窗口树。
                    # 与其禁止,不如抢回来 —— 归还前台**不影响**鼠标
                    # 点击消息的投递(✓/✕ 的 click 照常触发),而注入
                    # 落点由 ✓ 回调里的 _give_back_focus 双保险。
                    #
                    # 拖动期间除外:焦点来回跳会拖垮 WebView2 的
                    # 鼠标捕获(拖到一半就断了),松手时统一还一次。
                    #
                    # **用户正在文字条里选文本**时也除外:选文本靠鼠标
                    # 拖,那一下会把 WebView2 激活成前台;守卫要是照抢,
                    # WebView2 立刻失去**键盘焦点** —— 用户再按 Ctrl+C
                    # 就复制不了(按键送给了别的窗口)。用户手动兜底
                    # 拿回识别结果这条路必须通(实测踩到过)。
                    restore_foreground(last_fg)
                # 刷新命中图。
                #
                # 窗口区域裁好之后,Windows 不会立刻按新形状判命中 ——
                # 要等 DWM **重新合成**一次才生效(实测:裁完形状不刷新,
                # 形状里面点不动,球和胶囊都成了摆设)。合成由"把窗口
                # 提到置顶组顶端"触发,所以这里定期刷一次:
                # 形变期间由 _on_update_regions 顺带刷,静止期间靠这个兜底。
                #
                # 代价:胶囊会被反复提到置顶组最顶端。它本来就是常驻置顶
                # 窗口,置顶组内部的顺序用户基本感知不到。
                if time.monotonic() >= next_hit_refresh:
                    next_hit_refresh = time.monotonic() + HIT_REFRESH_INTERVAL
                    if is_window_visible(hwnd):
                        bring_to_top(hwnd)
            interval = (GUARD_INTERVAL if time.monotonic() < dense_until
                        else GUARD_SPARSE)
            time.sleep(interval)

    def _is_dragging(self) -> bool:
        """用户此刻是不是正拖着胶囊。"""
        with self._drag_lock:
            return self._dragging

    def _is_selecting_text(self) -> bool:
        """用户此刻是不是正在文字条里选文本。

        前端在选区非空时置位(见 capsule.js 的 selectionchange 监听)。
        这段时间里守卫**不抢前台** —— 抢了 WebView2 就失去键盘焦点,
        用户再按 Ctrl+C 复制不了(实测:框选了却复制不了)。
        """
        return self._selecting_text

    def _on_selecting_text(self, selecting: bool) -> None:
        """前端上报"用户开始/结束选文本"。"""
        self._selecting_text = bool(selecting)

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

    def _give_back_focus(self, target: int | None = None) -> None:
        """前台若是胶囊,还给它该在的窗口(这一轮的注入落点)。

        只在前台**确实是胶囊**时动手:用户点了 ✓/✕ 之后立刻切去
        别的窗口的话,前台已经不是胶囊,不能硬拽回录音时的窗口。

        ``target`` 省略时用这一轮记下的注入落点;那一轮还没开始
        (比如刚点完悬浮球)就退到守卫动态跟踪的用户窗口。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        if target is None:
            target = self._target_hwnd
        if not target or target == hwnd:
            target = self._user_foreground()
        if not target or target == hwnd:
            return
        if get_foreground_window() == hwnd:
            restore_foreground(target)

    def _user_foreground(self) -> int:
        """用户当前所在的窗口(0 = 不知道)。

        守卫在动态跟踪它(录音中途用户切了窗口,跟的就是切换后的那个)。
        守卫还没跑过的时候退回"问一次",但前提是前台**不是胶囊自己**。
        """
        hwnd = self._native_handle()
        last = getattr(self, "_last_foreground", 0)
        if last and last != hwnd:
            return last
        try:
            fg = get_foreground_window()
        except Exception:
            return 0
        if fg and fg != hwnd:
            return fg
        return 0

    def _snapshot_target_hwnd(self) -> None:
        """快照这一轮的注入落点(用户此刻所在的前台窗口)。

        前台**已经是胶囊自己**时**不覆盖**:那说明这一轮是从胶囊上
        开始的(点悬浮球 / 点 ✓),鼠标点击把 WebView2 的子窗口激活了。
        这时候落点应该用之前记好的那个,覆盖成胶囊就等于让这一轮的
        文字贴到胶囊自己身上。
        """
        hwnd = self._native_handle()
        fg = get_foreground_window()
        if fg and fg != hwnd:
            self._target_hwnd = fg

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
            # 透明底子必须在显示前就位:分层窗口没有 surface 时是
            # 一块黑,不是"还没画完"。
            self._ensure_transparent_surface(hwnd)
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
        if self._orb_enabled():
            # 常驻模式:窗口**不藏**(球还在那儿等着下一轮),
            # 胶囊由前端自己收回球(动效在前端做)。
            return
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

    # ---------- 手动复制(注入失败时的兜底出口) ----------

    def _copy_result(self, text: str) -> bool:
        """把 text 写进剪贴板,写成功就把胶囊收掉。

        用户在错误态点了「复制」—— 他要的是**确认**:确认文字真的进了
        剪贴板,而不是我们"试了一下"。所以写完要读回来核对:读回来
        不是同一份,就当没成功,胶囊留在原地让用户再试。

        写成功就收胶囊:用户拿到了想要的东西,胶囊继续杵在屏幕上
        就是干扰。
        """
        if not text or not text.strip():
            return False
        try:
            ok = clipboard.set_text(text)
        except Exception as e:
            log.debug("写剪贴板失败: %s", e)
            return False
        if not ok:
            log.warning("手动复制:写入剪贴板失败")
            return False

        time.sleep(CLIPBOARD_VERIFY_DELAY)
        try:
            current = clipboard.get_text()
        except Exception as e:
            log.debug("读回剪贴板失败: %s", e)
            current = None
        if current != text:
            # 读回来不是我们写的那份:要么写失败了,要么用户在这几十
            # 毫秒里自己复制了别的东西。后者不算我们的错,但也不能
            # 谎报成功 —— 让用户看到"没复制成功"更诚实。
            log.warning("手动复制:读回校验不一致,当作失败")
            return False

        log.info("手动复制成功,收胶囊")
        self._cancel_hide_timer()
        self._hide()
        return True

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
        # 窗口形状裁好了 → 球可以露脸了(之前那片透明空白会吃掉
        # 底下应用的点击)
        self._orb_regions_ready = True
        # 透明底子也在这时上传:窗口尺寸到这时候已经定了(前端量过了),
        # 上早了会拿到建窗时的尺寸。
        self._ensure_transparent_surface(hwnd)

        scale = self._scale()
        # 裁形:
        #   胶囊/球 -> 矩形
        #   文字条  -> 14px 圆角矩形
        #   浮层    -> 12px 圆角矩形
        #
        # 胶囊和球都按**矩形**裁,不按"猜出来的形态"分圆/椭圆:
        # 猜形态靠的是控制器状态(录音中=胶囊,其余=球),而状态切换
        # 和形状变化不是同一拍 —— 收回动画期间状态先切、形状后变,
        # 那一拍就会把长条胶囊裁成一个奇怪的椭圆(实测见过)。
        # 矩形不会切出这种怪形状;四角多出来的那点边是"看不见但
        # 能点"的区域,有鼠标穿透兜着,不影响观感。
        #
        # 形状那一项**必须就是**列表里的那项(set_window_regions 按
        # 坐标相等来认),不能另算一份 —— 两份坐标对不上就静默回落成
        # 矩形(实测踩过)。
        ellipses: set[tuple[int, int, int, int]] = set()
        round_rects: dict[tuple[int, int, int, int], int] = {}

        def expand(rect, margin):
            x, y, w, h = rect
            return (
                int(round(x * scale)) - margin,
                int(round(y * scale)) - margin,
                int(round((x + w) * scale)) + margin,
                int(round((y + h) * scale)) + margin,
            )

        physical: list[tuple[int, int, int, int]] = []
        for key in _REGION_KEYS:
            rect = rects.get(key)
            if rect is None:
                continue
            x, y, w, h = rect
            if w <= 0 or h <= 0:
                continue        # 隐藏的块不参与并集
            if key == "pill":
                # 胶囊与球统一按矩形裁(见上面的说明)
                piece = expand(rect, REGION_MARGIN)
            elif key == "overlay":
                # 浮层(菜单/提示):12px 圆角矩形
                piece = expand(rect, REGION_MARGIN)
                round_rects[piece] = int(round(OVERLAY_RADIUS_PX * scale))
            else:
                # 文字条:14px 圆角矩形
                piece = expand(rect, REGION_MARGIN)
                round_rects[piece] = int(round(TEXTBAR_RADIUS_PX * scale))
            physical.append(piece)

        if not physical:
            # 都隐藏(正常不会发生):裁成 1×1 的最小区域 ——
            # 宁可"窗口点不到",也不要"整片空白继续挡着底下的应用"
            physical = [(0, 0, 1, 1)]

        log.debug("裁窗口区域: %s (椭圆=%s 圆角=%s)",
                  physical, ellipses, round_rects)
        try:
            set_window_regions(hwnd, physical, ellipses=ellipses,
                               round_rects=round_rects)
        except Exception as e:
            log.debug("裁窗口区域失败: %s", e)

        # 换了窗口区域之后,Windows 的**命中图**不会自动跟着更新:
        # 实测改完 region,球和胶囊都点不动(空白区倒是正常穿透了),
        # 只有让 DWM 重新合成一次(把窗口提到置顶组顶端)命中才生效。
        # 这一步不能省 —— 少了它,胶囊就成了看得见点不着的摆设。
        bring_to_top(hwnd)

    def _ensure_transparent_surface(self, hwnd: int, force: bool = False) -> None:
        """给分层窗口上传"几乎全透明"的 surface —— 真透明的底子。

        没有它,窗口虽然已经是 layered(WebView2 的透明背景能透出桌面),
        但它自己的**命中判定**仍然是"整窗都算本窗口":点空白处会被
        胶囊吃掉。surface 的 alpha 决定命中(逐像素),窗口区域
        (SetWindowRgn)决定形状,两者合起来才是"空白穿透、球可点"。

        只在窗口尺寸变化时重传:重传会吞掉 WebView2 的画面(DWM 要按
        新的 surface 重新裁剪子窗口,而 WebView2 不会主动重绘),
        而胶囊是固定尺寸的,正常一辈子只传一次。

        **两步的顺序不能反**:必须先把 WS_EX_LAYERED 挂上,再传 surface。
        UpdateLayeredWindow 要求目标窗口是 layered 的,不然直接失败 ——
        窗口就退回 WinForms 自己画的默认灰底(#F0F0F0),内容四周套着
        一圈灰边(实测:球 60px,灰边 1~2px)。

        之所以还要再挂一次 layered:pywebview 建 hidden 窗口时有自己
        的一串透明 hack(winforms 后端 EdgeChrome.on_navigation_start:
        ``Opacity=0 → Show() → Hide() → Opacity=1``),它会**摘掉**
        WS_EX_LAYERED。那串 hack 跑在 ``before_show`` **之后**,所以
        ``_on_before_show`` 里挂的 layered 会被它清掉 —— 只能在
        ``shown`` 事件里补挂(见 ``_on_window_shown``)。

        force=True 时无视尺寸缓存强行重传:补挂 layered 之后必须重传
        一次,否则窗口虽然又是 layered,却还挂着摘钩前那张旧 surface。
        只在 ``shown`` 那个时机用 —— 那时 WebView2 还没画东西,重传
        吞不掉画面。
        """
        rect = get_window_rect(hwnd)
        if not rect:
            return
        left, top, right, bottom = rect
        size = (right - left, bottom - top)
        if size[0] <= 0 or size[1] <= 0:
            return
        if self._surface_size == size and not force:
            return
        try:
            # 先挂 layered 再传 surface:顺序反了 surface 一定传不上去
            enable_layered_window(hwnd)
            if upload_transparent_surface(hwnd):
                self._surface_size = size
                log.info("已上传透明 surface: %sx%s", size[0], size[1])
        except Exception as e:
            log.info("上传透明 surface 失败: %s", e)

    # ---------- 尺寸与位置 ----------

    def _capture_anchor(self) -> None:
        """捕获这一轮的定位信号点(只在录音开始时调一次)。

        捕获的是**按热键那一刻**的光标位置。之后这一轮录音里
        胶囊不再移动 —— 位置可预期比"位置聪明"更重要:
        用户按完热键看到胶囊,就该知道它不会乱跳。
        """
        hwnd = self._native_handle()
        try:
            self._anchor = capture_anchor(
                foreground_hwnd=get_foreground_window(),
                exclude_hwnd=hwnd or 0,
            )
        except Exception as e:
            log.debug("捕获光标位置失败: %s", e)
            self._anchor = None
        if self._anchor is not None:
            log.debug("定位信号: %s (%d, %d)", self._anchor.kind,
                      self._anchor.x, self._anchor.y)

    def _place(self) -> None:
        """按当前模式摆胶囊。"""
        if self._orb_enabled():
            # 常驻模式:位置归**悬浮球**,胶囊在球的位置原地展开。
            # 胶囊不跟着光标跑 —— 常驻的东西不该乱跑。
            return
        mode = self._position_mode()
        if mode == "follow":
            self._place_follow()
        elif mode == "custom":
            self._place_custom()
        else:
            self._place_bottom()

    def _place_follow(self, anchor: Any = None) -> bool:
        """贴着输入光标摆(像输入法候选框)。返回**摆成功没有**。

        ``anchor`` 给了就用给定的 —— 跟随档的轮询每轮自己取好锚点,
        直接拿来摆。不给就用这一轮录音开始时捕获的那个:录音途中
        胶囊不再乱跳(位置可预期比"位置聪明"更重要)。

        实在一点信号都拿不到,退回"主屏底部居中"(胶囊的老兜底)。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return False
        if anchor is None:
            if self._anchor is None:
                self._capture_anchor()
            anchor = self._anchor
        if anchor is None:
            self._place_bottom()
            return False

        size = self._window_size()
        if size is None:
            return False
        win_w, win_h = size

        mon = monitor_at_point(anchor.x, anchor.y)
        if mon is not None:
            area, scale = mon.work_area, mon.scale
        else:
            area, scale = get_work_area(), self._scale()
        if area is None:
            return False

        x, y = follow_position(anchor.x, anchor.y, win_w, win_h, area)
        log.debug("跟随输入: 锚点(%d, %d) → 窗口(%d, %d)",
                  anchor.x, anchor.y, x, y)
        try:
            move_window_to(hwnd, int(x), int(y))
            return True
        except Exception as e:
            log.debug("摆胶囊位置失败: %s", e)
            return False

    def _place_bottom(self) -> None:
        """摆到工作区底部居中(原来的行为)。

        用**工作区**而不是整块屏幕 —— 后者会被任务栏盖住底边。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        area = get_work_area()
        size = self._window_size()
        if area is None or size is None:
            return
        win_w, win_h = size
        margin = int(round(BOTTOM_MARGIN * self._scale()))
        x, y = bottom_position(win_w, win_h, area, margin)
        try:
            move_window_to(hwnd, int(x), int(y))
        except Exception as e:
            log.debug("摆胶囊位置失败: %s", e)

    def _place_custom(self) -> None:
        """摆到用户拖出来的位置。

        位置记的是"相对所在工作区左上角的逻辑偏移",还原时要先
        找到当时那块屏(设备名);那块屏不在了就退回当前屏,再不行
        就退回主屏底部 —— 宁可位置不理想,也不能把胶囊摆到屏幕外。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        size = self._window_size()
        if size is None:
            return
        win_w, win_h = size

        mon = self._saved_monitor()
        if mon is None:
            mon = self._monitor_at_window()
        if mon is None:
            mon = primary_monitor()
        if mon is None:
            self._place_bottom()
            return

        geometry = self._saved_geometry()
        position = custom_position(geometry, mon.work_area, mon.scale,
                                   win_w, win_h)
        if position is None:
            # 还没拖过(custom 模式第一次用):给个合理的起点
            self._place_bottom()
            return
        try:
            move_window_to(hwnd, int(position[0]), int(position[1]))
        except Exception as e:
            log.debug("摆胶囊位置失败: %s", e)

    # ---------- 常驻悬浮球 ----------

    def _window_should_be_visible(self) -> bool:
        """窗口此刻该不该露着。

        三种"该露着":常驻球模式(球已经露脸了)、这一轮的胶囊正在显示,
        或者跟随档下这一刻真的找到了真光标。

        非常驻模式还是老规矩:_want_visible 说了算。
        """
        if self._orb_enabled():
            return bool(getattr(self, "_orb_shown", False)) or self._want_visible
        return self._want_visible

    def _schedule_show_orb(self) -> None:
        """等前端把"球"那一小块报上来再露脸。

        窗口是**固定尺寸**的透明窗,没裁形状之前那 512×260 的透明
        空白会把底下应用的点击全吃掉 —— 球露脸之前必须先裁好。

        已经排上队就别再排:跟随档的轮询每 250ms 就会来问一次,
        每次都"取消旧的、排个新的"的话,这个 0.3s 的定时器永远等不到
        (250 < 300),球就永远露不了脸。
        """
        with self._hide_lock:
            if getattr(self, "_orb_timer", None) is not None:
                return      # 已经排上了,别反复重启
            timer = threading.Timer(ORB_SHOW_DELAY, self._show_orb)
            timer.daemon = True
            self._orb_timer = timer
        timer.start()

    def _cancel_orb_timer(self) -> None:
        """作废"稍后露脸"的定时器。"""
        with self._hide_lock:
            timer, self._orb_timer = getattr(self, "_orb_timer", None), None
        if timer is not None:
            timer.cancel()

    def _show_orb(self, anchor: Any = None) -> None:
        """把球亮出来(常驻模式):摆好位置 + 显示窗口(不抢焦点)。

        ``anchor`` 是跟随档已经取好的真光标;不给就现取一个。
        跟随档取不到光标 → **不露脸**(球只在光标真的聚焦着时才有意义)。
        """
        self._cancel_orb_timer()
        if not self._place_orb(anchor):
            # 跟随档:此刻没有真光标 → 球先收着
            self._hide_orb()
            return
        # 先记"该露着":守卫在同一瞬间看到"没人要求显示"会把球藏回去
        self._orb_shown = True
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            set_tool_window_style(hwnd)
            set_no_activate_tree(hwnd)
            show_window_no_activate(hwnd)
            bring_to_top(hwnd)
        except Exception as e:
            log.debug("显示悬浮球失败: %s", e)

    def _hide_orb(self) -> None:
        """把球收回去(关掉常驻模式,或跟随档下光标不见了)。"""
        self._cancel_orb_timer()
        self._orb_shown = False
        if self._want_visible:
            # 正在录音:这一轮的胶囊还得留着,不能凭空消失
            return
        self._cancel_hide_timer()
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            hide_window(hwnd)
        except Exception as e:
            log.debug("隐藏悬浮球失败: %s", e)

    def _place_orb(self, anchor: Any = None) -> bool:
        """把窗口摆到球该在的位置,返回**摆成功没有**。

        两路(三档共用):

        - follow:位置由**光标**决定(取不到光标就摆不了,返回 False)
        - pinned / custom:摆到**记忆的位置**(没拖过就是工作区右下角),
          两档共用同一份 orb_geometry —— 差别只在显隐(pinned 只在
          光标聚焦到输入处露脸,custom 常驻),摆位完全一样

        球固定待在窗口的**底边居中**处,所以"摆窗口"就是"摆球":
        先算出球该在的屏幕坐标,再反推窗口左上角。
        """
        mode = self._orb_position_mode()
        if mode == "follow":
            if anchor is None:
                anchor = self._capture_follow_anchor()
            if anchor is None:
                return False
            return self._place_follow(anchor)

        hwnd = self._native_handle()
        if not hwnd:
            return False

        size = self._window_size()
        if size is None:
            return False
        win_w, win_h = size

        mon = self._saved_orb_monitor()
        if mon is None:
            mon = primary_monitor()
        if mon is None:
            return False
        area = mon.work_area
        scale = mon.scale

        position = orb_position_from_geometry(
            self._saved_orb_geometry(), area, scale, win_w, win_h)
        if position is None:
            # 还没拖过:给个合理的默认值(工作区右下角)
            ox, oy = default_orb_position(area, scale)
            position = orb_window_position(ox, oy, win_w, win_h, scale, area)
        if position is None:
            return False
        try:
            move_window_to(hwnd, int(position[0]), int(position[1]))
            return True
        except Exception as e:
            log.debug("摆悬浮球位置失败: %s", e)
            return False

    # ---------- 跟随输入档(球跟着光标走) ----------

    def _start_follow_thread(self) -> None:
        """起位置轮询线程(幂等)。

        「常驻球 + follow/pinned」才有意义:这两档都要检测
        "光标聚焦在输入处没"来决定显隐,follow 还要多挪位置。
        custom 常驻,没什么可轮询的 —— 切档时由 ``orb_position``
        事件负责起停。
        """
        if not self._orb_enabled():
            return
        if self._orb_position_mode() not in ("follow", "pinned"):
            return
        # 锁/线程属性可能还没建(测试里绕过 __init__ 造对象)——
        # 拿不到就地补一个,别让这一步把整个前端就绪流程带崩。
        lock = getattr(self, "_follow_lock", None)
        if lock is None:
            lock = threading.Lock()
            self._follow_lock = lock
        with lock:
            thread = getattr(self, "_follow_thread", None)
            epoch_now = getattr(self, "_follow_epoch", 0)
            if thread is not None and thread.is_alive():
                if getattr(thread, "_orb_epoch", -1) == epoch_now:
                    return      # 就是当前档位的线程,复用
                # 换代残留的旧线程:它醒来发现代数变了就退,等它让位
                thread.join(timeout=1.0)
                if thread.is_alive():
                    return      # 还没退干净,这轮放弃(下次事件会再试)
            self._follow_epoch = epoch_now + 1
            epoch = self._follow_epoch
            thread = threading.Thread(target=self._follow_loop,
                                      args=(epoch,),
                                      name="ui-orb-follow", daemon=True)
            thread._orb_epoch = epoch
            self._follow_thread = thread
            thread.start()

    def _stop_follow_thread(self) -> None:
        """停掉位置轮询线程(切到 custom 档时)。

        线程可能正攥着一个刚捕获的锚点(UIA 那一问要几十到几百毫秒):
        直接改配置的话,它醒来会把球又挪回光标处、或者把球藏掉,
        把新档位的摆位覆盖掉 —— 那就是用户看到的"切了档没反应"。

        所以这里把代数 +1(线程醒来即退),再 join 一小会儿:等到线程
        真正退出后,调用方紧接着的 ``_show_orb`` 摆位就是最终位置。
        """
        lock = getattr(self, "_follow_lock", None)
        if lock is None:
            return
        with lock:
            self._follow_epoch = getattr(self, "_follow_epoch", 0) + 1
            thread = getattr(self, "_follow_thread", None)
        if thread is not None and thread.is_alive():
            thread.join(timeout=1.0)

    def _follow_loop(self, epoch: int) -> None:
        """位置轮询循环。``epoch`` 是起线程时领到的代数。

        生命周期:代数变了(被新一代线程顶替)、档位不属于
        follow/pinned、或者常驻球关了,就退出 ——
        下一轮 ``_start_follow_thread`` 会重新起一个。
        """
        while not self._destroyed:
            if epoch != self._follow_epoch:
                break
            try:
                if (not self._orb_enabled()
                        or self._orb_position_mode() not in ("follow",
                                                             "pinned")):
                    break
                self._follow_tick(epoch)
            except Exception as e:
                log.debug("跟随轮询出错: %s", e)
            time.sleep(ORB_FOLLOW_INTERVAL)

    def _follow_tick(self, epoch: int) -> None:
        """一轮轮询:找到真光标就按档位反应,找不到就收起球。

        ``epoch`` 是本线程的代数:UIA 捕获是一笔慢调用,醒来后
        代数变了就立刻收手 —— 这次的结果作废,绝不再动窗口
        (不然会盖掉切档后的摆位)。

        两条边界的按档分派:

        - follow:找到光标 → 挪过去;找不到 → 收球
        - pinned:找到光标 → 露脸(位置在记忆处,不挪);找不到 → 收球

        共同的三条边界:

        1. **录音中不藏** —— 胶囊正在录,窗口必须留着;
        2. **录音中不挪** —— 展开的胶囊在原地待着就够了,
           位置可预期比"位置聪明"更重要;
        3. 第一次露脸要走"等命中区裁好"那条路(``_schedule_show_orb``),
           否则那 512×260 的透明空白会吃掉底下应用的点击。
        """
        if epoch != self._follow_epoch:
            return
        if not self._orb_enabled():
            return
        if self._want_visible:
            # 正在录音:胶囊留着,也不挪
            return

        anchor = self._capture_follow_anchor()
        if epoch != self._follow_epoch:
            # 捕获的这几百毫秒里切了档:这次的结果是旧档位的,
            # 用它动窗口会跟新档位的摆位打架
            return
        if anchor is None:
            # 没有真光标 → 球收起来(不在输入框里就别杵在那儿)
            self._hide_orb()
            return

        if self._orb_position_mode() == "pinned":
            # 记住的位置:显隐归光标管,位置不挪。
            # 摆位在 _show_orb → _place_orb 里做(记忆的位置)。
            if self._orb_shown:
                return
        elif self._orb_shown:
            # 跟随档已经露着脸了 → 只挪位置
            self._place_follow(anchor)
            return

        if self._orb_regions_ready:
            # 命中区裁好了 → 可以直接露脸
            self._show_orb(anchor)
            return

        # 还没裁好 → 排一次"稍后露脸"(别反复重启,见 _schedule_show_orb)
        self._schedule_show_orb()

    def _capture_follow_anchor(self) -> Any:
        """位置轮询档(follow / pinned)取**真光标**(uia / caret / edit 三级)。

        后两级(鼠标位置、前台窗口中心)说的不是光标 —— 拿它们冒充,
        球就会在"用户只是把鼠标挪过来"的时候也冒出来。pinned 档也要
        这个判定:有真光标才露脸,和 follow 档同一套一致性。

        ``edit`` 这一级是"焦点在可编辑输入框里,但没有字符可定位"
        (空输入框)—— 位置退化成输入框的左下角,见 caret.py。

        前台是胶囊自己时直接放弃:这时候问出来的"光标"是胶囊自己的
        (WebView2 里也有文本),拿来摆位只会把自己算到眼前。
        """
        hwnd = self._native_handle()
        try:
            fg = get_foreground_window()
        except Exception:
            fg = 0
        if hwnd and fg == hwnd:
            return None
        try:
            anchor = capture_anchor(foreground_hwnd=fg or 0,
                                    exclude_hwnd=hwnd or 0,
                                    require_caret=True)
        except Exception as e:
            log.debug("跟随档取光标失败: %s", e)
            return None
        if anchor is None or anchor.kind not in ("uia", "caret", "edit"):
            return None
        return anchor

    def _move_orb_abs(self, x: float, y: float) -> None:
        """拖动悬浮球:窗口左上角 → (x, y) 逻辑像素。

        前端每次都从 mousedown 起点算绝对目标发过来(不发增量)——
        某一次调用丢了或慢了,下一次直接覆盖,天然不漂。

        摆位走"球"那套规则:球的位置会被夹回允许的范围(纵向尤其严 ——
        球的上方要留给展开后的胶囊),窗口跟着反推。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            scale = self._current_scale()
            size = self._window_size()
            if size is None:
                return
            win_w, win_h = size
            win_x = int(round(float(x) * scale))
            win_y = int(round(float(y) * scale))
            # 球在窗口的底边居中处:先算球的屏幕坐标,再夹范围
            ox, oy = orb_screen_position(win_x, win_y, win_w, win_h, scale)
            mon = monitor_at_point(ox, oy)
            area = mon.work_area if mon is not None else get_work_area()
            if area is None:
                return
            nx, ny = orb_window_position(ox, oy, win_w, win_h, scale, area)
            move_window_to(hwnd, int(nx), int(ny))
        except Exception as e:
            log.debug("移动悬浮球失败: %s", e)

    def _save_orb_position(self) -> None:
        """记住悬浮球的位置(相对所在工作区的逻辑偏移)。

        存的是**球**的偏移,不是窗口的:球是用户真正盯着的那一小块。
        存相对量而不是绝对坐标:换缩放率、换显示器都不飞。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        rect = get_window_rect(hwnd)
        if rect is None:
            return
        mon = self._monitor_at_window()
        if mon is None:
            return
        ox, oy = orb_screen_position(rect[0], rect[1], rect[2] - rect[0],
                                      rect[3] - rect[1], mon.scale)
        geometry = encode_offset(ox, oy, mon.work_area, mon.scale)
        try:
            config = self.controller.config
            config.orb_geometry = geometry
            config.orb_monitor = mon.device
            config.save()
        except Exception as e:
            log.debug("保存悬浮球位置失败: %s", e)
            return
        log.debug("记住悬浮球位置: %s @ %s", geometry, mon.device)

    def _saved_orb_geometry(self) -> str:
        """上次记忆的悬浮球位置(``"dx,dy"``)。"""
        try:
            return str(getattr(self.controller.config, "orb_geometry", "") or "")
        except Exception:
            return ""

    def _saved_orb_monitor(self) -> Any:
        """上次记忆位置时所在的那块显示器(设备名对不上就返回 None)。"""
        try:
            device = str(getattr(self.controller.config, "orb_monitor", "") or "")
        except Exception:
            return None
        if not device:
            return None
        return monitor_by_device(device)

    def _start_recording_from_orb(self) -> None:
        """点悬浮球开始录音。

        ⚠ **焦点陷阱**:鼠标点在 WebView2 的子窗口上会激活整棵窗口树,
        前台被胶囊抢走。注入靠"粘贴到前台窗口",所以**先把前台还给
        用户**,再开始录音 —— 否则这一轮识别完的文字会贴到胶囊自己
        身上,而不是用户的输入框里。那是功能级的致命 bug,不是观感问题。
        """
        target = self._user_foreground()
        if target:
            self._give_back_focus(target)
            # 这一轮的注入落点:先记下来,别等开始录音时再猜
            self._target_hwnd = target
        try:
            ok = self.controller.start_recording()
        except Exception as e:
            log.debug("点悬浮球开始录音失败: %s", e)
            return
        if not ok and self.controller.state == State.STARTING:
            # 模型还在加载:controller 拒绝了,但"点了没反应"在用户眼里
            # 就是"球坏了"。给一句提示,别让人对着沉默发懵。
            self._call_js("setOrbHint", "模型加载中…")

    # ---------- 球菜单(右键通用功能入口) ----------

    def set_panel_opener(self, fn: Callable[[], None] | None) -> None:
        """注入「打开面板」的回调(入口组装时调)。"""
        self._panel_opener = fn

    def set_quit_handler(self, fn: Callable[[], None] | None) -> None:
        """注入「退出程序」的回调(入口组装时调)。"""
        self._quit_handler = fn

    def _open_panel(self) -> None:
        if self._panel_opener is None:
            return
        try:
            self._panel_opener()
        except Exception as e:
            log.debug("球菜单打开面板失败: %s", e)

    def _quit_from_menu(self) -> None:
        if self._quit_handler is None:
            return
        try:
            self._quit_handler()
        except Exception as e:
            log.debug("球菜单退出失败: %s", e)

    def _window_size(self) -> tuple[int, int] | None:
        """窗口当前的**物理**像素尺寸。

        用真实尺寸而不是建窗时请求的尺寸:WinForms 的 AutoScale 会
        把客户区再缩一次(见 WIN_W 的说明),请求值和实际值对不上。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return None
        rect = get_window_rect(hwnd)
        if rect is None:
            return None
        width = rect[2] - rect[0]
        height = rect[3] - rect[1]
        if width <= 0 or height <= 0:
            return None
        return width, height

    def _current_scale(self) -> float:
        """窗口所在显示器的缩放系数。

        用 ``GetDpiForWindow``:胶囊可能被拖到另一块缩放率不同的
        屏上,按主屏的缩放换算位置会算飞。
        """
        try:
            import ctypes

            hwnd = self._native_handle()
            if hwnd:
                dpi = ctypes.windll.user32.GetDpiForWindow(ctypes.c_void_p(hwnd))
                if dpi:
                    return max(1.0, dpi / 96.0)
        except Exception:
            pass
        return self._scale()

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

    # ---------- 拖动(自定义位置) ----------

    def _move_capsule_abs(self, x: float, y: float) -> None:
        """窗口左上角 → (x, y) 逻辑像素。

        前端拖动时每次都从 mousedown 起点算绝对目标发过来(不发增量):
        某一次调用丢了或慢了,下一次直接覆盖,天然不漂。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        try:
            scale = self._current_scale()
            move_window_to(hwnd, int(round(float(x) * scale)),
                           int(round(float(y) * scale)))
        except Exception as e:
            log.debug("移动胶囊失败: %s", e)

    def _begin_drag(self) -> None:
        """用户开始拖胶囊/悬浮球。

        拖动期间守卫**不**抢回焦点:鼠标点在 WebView2 子窗口上会把
        前台激活到胶囊,守卫每 150ms 还一次的话,焦点来回跳会拖垮
        WebView2 的鼠标捕获(拖到一半就断了)。松手时统一还一次。
        """
        with self._drag_lock:
            self._dragging = True
        # 记下起手位置:松手时对比一下,没挪过就别去写配置
        try:
            self._drag_start_rect = get_window_rect(self._native_handle())
        except Exception:
            self._drag_start_rect = None

    def _end_drag(self) -> None:
        """用户松手:归还焦点 + 记住位置。"""
        with self._drag_lock:
            self._dragging = False
        start_rect = getattr(self, "_drag_start_rect", None)
        self._drag_start_rect = None
        if self._orb_enabled():
            # 悬浮球:这一轮还没开始,没有"注入落点"可言 ——
            # 该还给的是用户**此刻**所在的窗口(守卫动态跟踪的),
            # 不能用上一轮记下的那个(用户早就切到别的窗口去了)。
            self._give_back_focus(self._user_foreground())
            # 拖过才记位置(点一下没动就别去写配置)。
            #
            # ⚠ pinned / custom **记**(两档共用一份 orb_geometry,
            # 位置归用户管);follow **不记** —— 它的位置由光标决定,
            # 轮询线程一直在挪窗口,这里的"挪过"是程序挪的,不是用户
            # 拖的 —— 写进配置只会把一个莫名其妙的位置存成
            # "用户自定义的位置"。
            if (self._orb_position_mode() in ("pinned", "custom")
                    and self._orb_moved(start_rect)):
                self._save_orb_position()
            return
        # 前台若被这次点击激活到了胶囊上,还回这一轮录音的落点 ——
        # 不还的话,后面注入的文字会发给胶囊自己。
        self._give_back_focus()
        if self._position_mode() == "custom":
            self._save_capsule_position()

    def _orb_moved(self, start_rect: Any) -> bool:
        """这一下拖动有没有真的挪过位置。

        点悬浮球(想开始录音)和拖悬浮球(想挪位置)走的是同一套按下/
        松手流程 —— 靠"有没有真的动过"区分,免得点一下也写一次配置。
        """
        if start_rect is None:
            return True     # 没记录(理论上不该发生)→ 当作拖过
        rect = get_window_rect(self._native_handle())
        if rect is None:
            return False
        return rect[:2] != start_rect[:2]

    def _save_capsule_position(self) -> None:
        """记住当前胶囊位置(相对所在工作区的逻辑偏移)。

        存相对量而不是绝对坐标:换缩放率、换显示器都不飞。
        """
        hwnd = self._native_handle()
        if not hwnd:
            return
        rect = get_window_rect(hwnd)
        if rect is None:
            return
        mon = self._monitor_at_window()
        if mon is None:
            return
        geometry = encode_offset(rect[0], rect[1], mon.work_area, mon.scale)
        try:
            config = self.controller.config
            config.capsule_geometry = geometry
            config.capsule_monitor = mon.device
            config.save()
        except Exception as e:
            log.debug("保存胶囊位置失败: %s", e)
            return
        log.debug("记住胶囊位置: %s @ %s", geometry, mon.device)

    def _monitor_at_window(self) -> Any:
        """胶囊当前所在的那块显示器(拿窗口中心点去问)。"""
        rect = get_window_rect(self._native_handle())
        if rect is None:
            return None
        cx = (rect[0] + rect[2]) // 2
        cy = (rect[1] + rect[3]) // 2
        mon = monitor_at_point(cx, cy)
        if mon is None:
            mon = monitor_at_point(rect[0], rect[1])
        return mon

    def _saved_monitor(self) -> Any:
        """上次记忆位置时所在的那块显示器(设备名对不上就返回 None)。"""
        try:
            device = str(getattr(self.controller.config, "capsule_monitor", "")
                         or "")
        except Exception:
            return None
        return monitor_by_device(device)

    def _saved_geometry(self) -> str:
        """上次记忆的位置(``"dx,dy"``)。"""
        try:
            return str(getattr(self.controller.config, "capsule_geometry", "")
                       or "")
        except Exception:
            return ""

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
        self._cancel_orb_timer()
        try:
            self._window.destroy()
        except Exception as e:
            log.debug("销毁胶囊窗口失败: %s", e)
