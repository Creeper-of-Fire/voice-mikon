"""常驻控制面板(pywebview + WebView2)。

面板前端是纯静态 HTML/CSS/JS,放在 ``src/qwen3_voice/ui/static/``,
不引入任何前端框架或构建步骤 —— 项目定位是"绿色软件",
``git clone`` + ``pip install -e .`` 就能跑,不该有 node_modules。

Python 与前端之间是双向桥接,方法名两边必须严格对齐
(完整契约见同目录的 ``BRIDGE.md``):

  Python → JS   window.__qwen.setState / setLevels / setElapsed / setStatus
                        / setResult / setModel / setHotkey
  JS → Python   window.pywebview.api.ready / toggle_recording / switch_model
                        / get_snapshot / move_window / close_panel / quit_app

线程分工:
  主线程      webview.start() 的 GUI 消息循环
  推送线程    把控制器事件 + 30fps 电平转成 evaluate_js 调用
  控制器线程  录音/识别,回调可能在任意线程触发,所以只往队列里投

两个容易踩的坑,都在下面代码里处理了:

1. ``evaluate_js`` 在 GUI 还没建好时会阻塞 20 秒才抛异常。
   所以所有前端调用都过 ``_gui_ready`` 门控,窗口就绪后才推。

2. 电平必须推**绝对值**(dBFS 映射),不能按窗口内最大值归一化。
   归一化之后波形永远看起来是满的,用户就没法从条的高低看出
   "我说话声音大不大" —— 那条电平条就只剩装饰作用了。
"""

from __future__ import annotations

import json
import math
import os
import queue
import threading
import time
from pathlib import Path
from typing import Any

from ..core.controller import State, UIEvent, VoiceController
from ..logging_setup import get_logger
from ..win32.api import make_tool_window, set_tool_window_style

log = get_logger("ui.panel")

# 前端静态资源目录
STATIC_DIR = Path(__file__).resolve().parent / "static"

# 设计基准(逻辑像素 = CSS 像素)
BASE_W = 300
BASE_H = 260

# 推送频率
PUSH_INTERVAL = 1.0 / 30.0

# 电平映射区间:dBFS -> 0..1
#
# 用对数域而不是直接用线性 RMS:MiniMax Code 那边靠浏览器的 AGC
# 把 RMS 稳在 0.15 附近,我们没有 AGC,线性映射会因麦克风增益不同
# 而时高时低。对数映射只关心"比底噪高多少",对增益不敏感。
#
# 下限取 -70:实测这个房间的底噪就有 -62 dBFS,下限卡在 -60 会把底噪
# 和"完全无声"混成同一个 0,电平条看着像死了。实际人声约 -35 ~ -20 dBFS,
# 映射到 0.5 ~ 0.71,和底噪拉得开。
DB_FLOOR = -70.0
DB_CEIL = 0.0

# 控制器状态 -> 前端状态字符串
_STATE_NAMES = {
    State.STARTING: "starting",
    State.IDLE: "idle",
    State.RECORDING: "recording",
    State.PROCESSING: "processing",
    State.ERROR: "error",
}


def system_scale() -> float:
    """主显示器的缩放系数(本机 175% -> 1.75)。

    不能用 ``GetDeviceCaps(LOGPIXELSX)``:进程声明了 Per-Monitor V2
    之后,这个值取决于**调用线程**的 DPI 上下文,不同线程拿到的结果
    不一样(实测主线程拿到 96、探测线程拿到 168),位置就会算飞。

    ``GetDpiForSystem()`` 返回的是主显示器的 DPI,与线程无关,可靠。
    """
    try:
        import ctypes

        dpi = ctypes.windll.user32.GetDpiForSystem()
        if dpi:
            return max(1.0, dpi / 96.0)
    except Exception:
        pass
    try:
        import ctypes

        dc = ctypes.windll.user32.GetDC(0)
        try:
            dpi = ctypes.windll.gdi32.GetDeviceCaps(dc, 88)  # LOGPIXELSX
        finally:
            ctypes.windll.user32.ReleaseDC(0, dc)
        if dpi:
            return max(1.0, dpi / 96.0)
    except Exception:
        pass
    return 1.0


def dbfs_to_unit(rms: float, floor_rms: float = 0.0) -> float:
    """把线性 RMS 映射到 0..1(对数域)。

    DB_FLOOR dBFS 映射到 0,0 dBFS 映射到 1,中间线性插值。
    对数域而不是线性域,是因为人耳对 loudness 的感知接近对数,
    线性映射会让正常说话的音量全部挤在 0.05 以下。

    `floor_rms` 是**噪声门**:低于它的直接判 0。不加这个门的话,
    安静房间的底噪(-62 dBFS 实测)映射出来还有 0.11,会被前端当成
    "有声音",电平条一直显示 7px 高度,看着像一直在响。
    """
    if rms <= floor_rms or rms <= 1e-6:
        return 0.0
    db = 20.0 * math.log10(rms)
    if db <= DB_FLOOR:
        return 0.0
    if db >= DB_CEIL:
        return 1.0
    return (db - DB_FLOOR) / (DB_CEIL - DB_FLOOR)


class _PanelApi:
    """暴露给前端 JS 的桥接对象。

    单独一个类而不是直接用 VoicePanel,是因为 pywebview 的
    ``get_functions`` 会遍历对象的公开属性,遇到非 callable 的对象
    还会**递归**下去 —— 直接把 VoicePanel 交出去,控制器和配置
    的内部属性都会被暴露到网页里。这里只留方法,引用用下划线开头
    (pywebview 会跳过)。
    """

    def __init__(self, panel: VoicePanel):
        self._panel = panel

    def ready(self) -> None:
        """前端 DOM 就绪,请求推一次全量状态。"""
        self._panel._on_frontend_ready()

    def toggle_recording(self) -> None:
        self._panel.controller.toggle_recording()

    def switch_model(self, tag: str) -> None:
        self._panel.controller.switch_model(tag)

    def get_snapshot(self) -> dict:
        return self._panel._snapshot()

    def move_window(self, dx: float, dy: float) -> None:
        """拖动窗口。前端给的是**物理像素**增量。"""
        self._panel._move_window(dx, dy)

    def close_panel(self) -> None:
        """隐藏面板(不是退出程序)。"""
        self._panel.hide()

    def quit_app(self) -> None:
        """退出整个程序。"""
        self._panel._quit()


class VoicePanel:
    """语音输入控制面板。"""

    def __init__(self, controller: VoiceController, geometry: str = ""):
        self.controller = controller

        self._events: queue.Queue[UIEvent] = queue.Queue()
        self._state = State.STARTING
        self._last_result = ""
        self._status_text = "启动中..."
        self._status_tone = "dim"
        self._elapsed = 0.0

        self._scale = system_scale()
        self._visible = False          # 期望可见性(GUI 就绪前只记录)
        self._visibility_applied = False
        self._gui_ready = threading.Event()
        self._stop = threading.Event()
        self._pusher: threading.Thread | None = None

        # 窗口位置(逻辑像素)。None 表示还没定,会在窗口就绪后摆到右下角。
        self._pos_x: int | None = None
        self._pos_y: int | None = None

        # ---- portable:WebView2 的用户数据必须落在项目内 ----
        # pywebview 会在 start() 里显式设置 WebView2 的 UserDataFolder,
        # 光设 WEBVIEW2_USER_DATA_FOLDER 环境变量会被它覆盖,
        # 所以两个都要给。
        from ..paths import webview_dir

        self._webview_dir = webview_dir()
        try:
            self._webview_dir.mkdir(parents=True, exist_ok=True)
            os.environ["WEBVIEW2_USER_DATA_FOLDER"] = str(self._webview_dir)
        except OSError as e:
            log.warning("创建 WebView2 数据目录失败: %s", e)

        import webview

        self._api = _PanelApi(self)
        index = STATIC_DIR / "index.html"
        # 先解析保存过的位置:create_window 的 x/y 是逻辑像素,得在建窗时给
        saved_x, saved_y = self._parse_geometry(geometry)
        self._window = webview.create_window(
            "Qwen3 语音输入",
            url=index.as_uri(),
            js_api=self._api,
            width=BASE_W,
            height=BASE_H,
            x=saved_x,
            y=saved_y,
            frameless=True,
            hidden=True,
            easy_drag=False,      # 自己实现拖动,才能记住位置
            on_top=True,
            resizable=False,
            background_color="#ffffff",
        )
        self._window.events.before_show += self._on_before_show
        self._window.events.shown += self._on_window_shown

        controller.subscribe(self._on_event)

    # ---------- 几何 ----------

    def _parse_geometry(self, geometry: str) -> tuple[int | None, int | None]:
        """解析保存过的窗口位置。

        保存的是**逻辑像素**(物理像素 ÷ 缩放系数),换一台缩放率不同的
        显示器时位置还能落在合理的地方。pywebview 的 create_window
        的 x/y 也是逻辑像素,直接传即可。
        """
        if not geometry:
            return None, None
        try:
            parts = [int(v) for v in geometry.split(",")]
            if len(parts) == 4:
                x, y, _w, _h = parts
                self._pos_x = x
                self._pos_y = y
                return x, y
        except ValueError:
            log.info("保存的窗口位置无法解析: %s", geometry)
        return None, None

    def _save_geometry(self) -> None:
        """保存窗口位置(逻辑像素)。"""
        if self._pos_x is None or self._pos_y is None:
            return
        try:
            self.controller.config.panel_geometry = (
                f"{self._pos_x},{self._pos_y},{BASE_W},{BASE_H}")
            self.controller.config.save()
        except Exception as e:
            log.debug("保存窗口位置失败: %s", e)

    def _place_default(self) -> None:
        """没有保存过位置时,摆到主屏幕右下角。"""
        if self._pos_x is not None:
            return
        scale = self._current_scale()
        try:
            import ctypes

            # DPI aware 进程里 GetSystemMetrics 返回物理像素
            sw = ctypes.windll.user32.GetSystemMetrics(0)
            sh = ctypes.windll.user32.GetSystemMetrics(1)
            x = int(sw / scale) - BASE_W - 30
            y = int(sh / scale) - BASE_H - 60
        except Exception:
            x, y = 60, 60
        self._pos_x = x
        self._pos_y = y
        self._window.move(x, y)

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
            f"if(window.__qwen&&typeof window.__qwen.{method}==='function'){{"
            f"window.__qwen.{method}({payload});return true;}}"
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
            f"if(window.__qwen&&typeof window.__qwen.{m1}==='function'"
            f"&&typeof window.__qwen.{m2}==='function'){{"
            f"window.__qwen.{m1}({p1});window.__qwen.{m2}({p2});return true;}}"
            "}catch(e){return false;}return false;})()"
        )
        try:
            self._window.evaluate_js(script)
            return True
        except Exception as e:
            log.debug("调用前端 %s/%s 失败: %s", m1, m2, e)
            return False

    def _on_frontend_ready(self) -> None:
        """前端就绪,推一次全量状态。"""
        self._call_js("setState", self._state_name())
        self._call_js("setStatus", self._status_text, self._status_tone)
        self._call_js("setResult", self._last_result)
        self._call_js("setModel", self._model_tag())
        self._call_js("setHotkey", self.controller.config.hotkey)
        self._call_js("setElapsed", 0.0)
        self._call_js("setLevels", "[]")

    def _state_name(self) -> str:
        return _STATE_NAMES.get(self._state, "idle")

    def _model_tag(self) -> str:
        try:
            return getattr(self.controller.engine, "model_tag", "") or ""
        except Exception:
            return ""

    def _snapshot(self) -> dict:
        return {
            "state": self._state_name(),
            "elapsed": self._elapsed,
            "model": self._model_tag(),
            "last_result": self._last_result,
            "hotkey": self.controller.config.hotkey,
        }

    # ---------- 事件 ----------

    def _on_event(self, event: UIEvent) -> None:
        """控制器线程调用,这里只投递。"""
        self._events.put(event)

    def _handle_event(self, event: UIEvent) -> None:
        if event.kind == "state" and event.state is not None:
            self._state = event.state
            self._call_js("setState", self._state_name())
            if event.state == State.RECORDING:
                self._status_text, self._status_tone = "录音中", "err"
                self._call_js("setStatus", self._status_text, self._status_tone)
            elif event.state == State.PROCESSING:
                self._status_text, self._status_tone = "识别中", "warn"
                self._call_js("setStatus", self._status_text, self._status_tone)
        elif event.kind == "result":
            self._last_result = event.text
            self._status_text, self._status_tone = "已输入", "ok"
            self._call_js("setResult", event.text)
            self._call_js("setStatus", self._status_text, self._status_tone)
            # 结果文字可能让内容变高,重新量一次窗口(别挡着推送线程)
            threading.Thread(target=self._fit_window, daemon=True).start()
        elif event.kind == "error":
            self._status_text, self._status_tone = event.text, "err"
            self._call_js("setStatus", event.text, "err")
        elif event.kind == "info":
            self._status_text, self._status_tone = event.text, "dim"
            self._call_js("setStatus", event.text, "dim")
        elif event.kind == "model" and event.model_tag:
            self._call_js("setModel", event.model_tag)

    def set_status(self, text: str, tone: str = "dim") -> None:
        """对外的公开接口:设置状态行文字。

        ``__main__.py`` 在热键注册失败时用它报错。
        """
        self._status_text = text
        self._status_tone = tone
        self._call_js("setStatus", text, tone)

    # ---------- 推送线程 ----------

    def _pusher_loop(self) -> None:
        """把控制器事件和 30fps 电平转成 evaluate_js 调用。

        单独一个线程,是因为 evaluate_js 是阻塞的(等 WebView2 执行完),
        放在 GUI 主线程会卡住窗口响应。
        """
        last = 0.0
        while not self._stop.is_set():
            try:
                while True:
                    self._handle_event(self._events.get_nowait())
            except queue.Empty:
                pass

            now = time.monotonic()
            if now - last >= PUSH_INTERVAL:
                last = now
                self._push_levels()
                self._apply_pending_visibility()

            self._stop.wait(0.005)

    def _push_levels(self) -> None:
        """把最近的音量序列推给前端。

        推的是**绝对电平**(dBFS 映射后的 0..1)。前端绝不能按窗口内
        最大值归一化 —— 那会让电平条永远看起来是满的,用户就没法从
        条的高低看出"我说话声音大不大"了。
        """
        if self._state != State.RECORDING:
            return
        recorder = getattr(self.controller, "recorder", None)
        if recorder is None:
            return
        try:
            raw = recorder.levels()
            self._elapsed = recorder.elapsed
        except Exception:
            return
        mapped = [dbfs_to_unit(v, self.controller.config.silence_rms)
                  for v in raw]
        self._call_js_pair("setLevels", json.dumps(mapped),
                           "setElapsed", self._elapsed)

    def _apply_pending_visibility(self) -> None:
        """GUI 就绪后把期望的可见性真正应用上。

        show()/hide() 在 webview.start() 之前被调用时,GUI 还不存在,
        只能先记下来,这里再补一刀。
        """
        if self._visibility_applied or not self._gui_ready.is_set():
            return
        self._visibility_applied = True
        self._place_default()
        try:
            if self._visible:
                self._window.show()
            else:
                self._window.hide()
        except Exception as e:
            log.debug("应用窗口可见性失败: %s", e)
        self._fit_window()

    def _fit_window(self) -> None:
        """把窗口调整到刚好能容纳内容。

        pywebview 建窗时给的初始尺寸会被 WinForms 的 AutoScale 再缩一次
        (请求 260 逻辑像素,实际客户区只有 224),底部几个像素的内容
        就被裁掉了。但 ``resize()`` 走的是另一条路径,尺寸是准的
        (实测 resize(300,320) 得到的正好是 320 逻辑像素)。

        所以这里自己量一次内容高度,再 resize 到刚好合适。
        """
        need = BASE_H
        for _ in range(3):
            try:
                self._window.resize(BASE_W, BASE_H)
            except Exception as e:
                log.debug("调整窗口尺寸失败: %s", e)
                return
            time.sleep(0.25)
            need = self._content_height()
            if need <= BASE_H:
                self._keep_on_screen(BASE_H)
                return
            try:
                self._window.resize(BASE_W, need)
            except Exception as e:
                log.debug("按内容高度调整窗口失败: %s", e)
                return
            time.sleep(0.25)
        self._keep_on_screen(need)

    def _keep_on_screen(self, height: int) -> None:
        """窗口变高后别让它长出屏幕底部。

        resize 只改尺寸不改位置,窗口会往下长。默认摆位在右下角,
        长高一点就出屏了,所以这里把超出屏幕的部分拉回来
        (没超出的不动,免得把用户拖到中间的窗口硬拽回角落)。
        """
        if self._pos_x is None or self._pos_y is None:
            return
        try:
            import ctypes

            sw = ctypes.windll.user32.GetSystemMetrics(0)
            sh = ctypes.windll.user32.GetSystemMetrics(1)
            scale = self._current_scale()
            x = min(self._pos_x, int(sw / scale) - BASE_W)
            y = min(self._pos_y, int(sh / scale) - height)
            if (x, y) != (self._pos_x, self._pos_y):
                self._pos_x, self._pos_y = x, y
                self._window.move(x, y)
        except Exception as e:
            log.debug("把窗口拉回屏幕内失败: %s", e)

    def _content_height(self) -> int:
        """页面内容实际需要的高度(逻辑像素)。"""
        try:
            info = self._window.evaluate_js(
                "(function(){return {sh:document.body.scrollHeight,"
                "ih:window.innerHeight};})()")
            return int(info.get("sh", 0))
        except Exception as e:
            log.debug("读取内容高度失败: %s", e)
            return 0

    # ---------- 拖动 ----------

    def _current_scale(self) -> float:
        """当前缩放系数。

        优先用**窗口所在显示器**的 DPI(GetDpiForWindow,多显示器
        不同缩放时也准),取不到再退回主显示器的 DPI。
        """
        try:
            import ctypes

            native = getattr(self._window, "native", None)
            hwnd = int(native.Handle.ToInt64()) if native is not None else 0
            if hwnd:
                dpi = ctypes.windll.user32.GetDpiForWindow(ctypes.c_void_p(hwnd))
                if dpi:
                    return max(1.0, dpi / 96.0)
        except Exception:
            pass
        return self._scale

    def _move_window(self, dx: float, dy: float) -> None:
        """拖动窗口。

        前端发来的是**物理像素**增量(契约规定),而 pywebview 6.x 的
        ``move()`` 吃的是**逻辑像素**(内部自己乘缩放)。这里换算一次,
        漏掉的话在 175% 屏幕上窗口会比光标快 1.75 倍。
        """
        try:
            scale = self._current_scale()
            lx = int(round(float(dx) / scale))
            ly = int(round(float(dy) / scale))
        except (TypeError, ValueError):
            lx, ly = int(dx), int(dy)
        if self._pos_x is None:
            self._pos_x, self._pos_y = 0, 0
        self._pos_x += lx
        self._pos_y += ly
        try:
            self._window.move(self._pos_x, self._pos_y)
        except Exception as e:
            log.debug("移动窗口失败: %s", e)

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

    def _on_before_show(self) -> None:
        """窗口即将显示:抢先改扩展样式,别让它在任务栏上露脸。"""
        hwnd = self._native_handle()
        if hwnd:
            set_tool_window_style(hwnd)

    def _on_window_shown(self) -> None:
        """窗口已创建:改成纯悬浮窗。

        顺序很重要:
          先隐藏再改样式 —— 任务栏只在窗口显示时重新评估可见性,
          顺序反了会先冒出一个任务栏按钮再消失;
          改完才放行 ``_gui_ready`` —— 免得推送线程抢先 show,
          把窗口显示成还带任务栏按钮的样子。
        """
        hwnd = self._native_handle()
        if hwnd:
            make_tool_window(hwnd)
        self._gui_ready.set()

    def show(self) -> None:
        self._visible = True
        if self._gui_ready.is_set():
            try:
                self._window.show()
            except Exception as e:
                log.debug("显示面板失败: %s", e)

    def hide(self) -> None:
        self._visible = False
        if self._gui_ready.is_set():
            try:
                self._window.hide()
            except Exception as e:
                log.debug("隐藏面板失败: %s", e)

    def toggle(self) -> None:
        if self._visible:
            self.hide()
        else:
            self.show()

    def _quit(self) -> None:
        """退出程序:关掉窗口,webview.start() 就会返回。"""
        try:
            self._window.destroy()
        except Exception as e:
            log.debug("销毁窗口失败: %s", e)

    def destroy(self) -> None:
        self._stop.set()
        try:
            self._save_geometry()
        except Exception:
            pass
        try:
            self._window.destroy()
        except Exception:
            pass

    def mainloop(self) -> None:
        """阻塞运行 GUI 主循环(必须在主线程调用)。"""
        self._pusher = threading.Thread(
            target=self._pusher_loop, name="ui-push", daemon=True)
        self._pusher.start()

        import webview

        try:
            # private_mode=False + storage_path:让 WebView2 的用户数据
            # 固定落在项目内的 data/webview,而不是 %LOCALAPPDATA%。
            webview.start(private_mode=False, storage_path=str(self._webview_dir))
        except Exception as e:
            log.error("面板启动失败(WebView2 不可用?): %s", e)
        finally:
            self._stop.set()
