"""pywebview 窗口的 WebView2 细节调校:把键盘焦点交给 WebView2。

胶囊窗口带 WS_EX_NOACTIVATE(``focus=False``,不抢用户输入焦点,
设计使然),鼠标点击不会让它成为前台 —— 于是 WebView2 拿不到键盘
焦点,文字条里选字后的 Ctrl+C、右键菜单的复制命令全都使不上劲
(实测踩过)。

解法(用户拍板):文字条是**内容**区,用户按下它就是要选字复制,
此刻主动把前台与焦点送过去是明确意图,不算抢 ——
  1. 前台:调用方用 ``restore_foreground``(自带 AttachThreadInput,
     绕 Windows 前台锁;WS_EX_NOACTIVATE 只挡"鼠标点击自动激活",
     不挡程序化激活);
  2. 控件内部焦点:本模块的 ``focus_webview`` —— 控件方法必须回
     UI 线程(CoreWebView2 是 STA COM 对象),走 BeginInvoke 调度。

⚠ 依赖 pywebview 的内部对象链::

    window.native          # BrowserView(winforms 后端在建窗时挂上)
      .browser             # EdgeChrome
      .webview             # WebView2 WinForms 控件

pywebview 升级若动了这条链,这里**静默降级**(log 一条),不影响
主程序的其他功能。

配套约定(见 capsule.css):内容(识别文字)保持可选,**不想被选的
UI**(胶囊按钮、图标、状态提示)用 ``user-select: none`` 声明。
"""

from __future__ import annotations

from ..logging_setup import get_logger

log = get_logger("ui.webview_tweaks")


def focus_webview(window) -> None:
    """把键盘焦点交给窗口里的 WebView2 控件(UI 线程调度)。"""
    try:
        native = getattr(window, "native", None)
        ctl = native.browser.webview
    except AttributeError as e:
        log.warning("WebView2 聚焦失败(pywebview 内部结构变化?): %s", e)
        return

    def _apply() -> None:
        try:
            ctl.Focus()
        except Exception as e:
            log.debug("WebView2 聚焦失败: %s", e)

    try:
        from System import Action  # pythonnet:winforms 后端必有

        ctl.BeginInvoke(Action(_apply))
    except Exception as e:
        log.warning("调度 WebView2 聚焦失败: %s", e)
