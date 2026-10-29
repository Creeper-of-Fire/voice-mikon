"""托盘「重启」的单元测试。

重启分两步,分开测:

1. 托盘回调只**置位** ``restart_requested`` 并走和退出一样的清理流程
   —— 它绝不自己拉新实例(那时热键还没注销,拉早了会撞车);
2. 入口的 ``_spawn_restart`` 负责在清理完成后以**分离方式**拉起新实例
   —— 参数必须是 ``python -m qwen3_voice``,Windows 上带 DETACHED 标志。

都是纯逻辑 + 假对象,不真启进程、不碰托盘。
"""

from __future__ import annotations

import sys
import types

import qwen3_voice.__main__ as entry
from qwen3_voice.ui.tray import TrayIcon


# ---------------- 第一步:托盘回调只置位 + 走清理 ----------------

def _tray() -> TrayIcon:
    """一个带假控制器/假面板/假图标的托盘。"""
    t = TrayIcon(types.SimpleNamespace(shutdown=lambda: None),
                 panel=types.SimpleNamespace(destroy=lambda: None))
    t.icon = types.SimpleNamespace(stop=lambda: None)
    return t


def test_restart_requests_and_quits():
    """点「重启」= 置位标志 + 走退出清理,不多不少。"""
    t = _tray()
    calls = []
    t.controller.shutdown = lambda: calls.append("shutdown")
    t.panel.destroy = lambda: calls.append("destroy")
    t.icon.stop = lambda: calls.append("stop")

    t._restart()

    assert t.restart_requested is True
    assert calls == ["shutdown", "destroy", "stop"]


def test_quit_does_not_request_restart():
    """普通退出**不**置位重启标志 —— 退了就是退了。"""
    t = _tray()

    t._quit()

    assert t.restart_requested is False


# ---------------- 第二步:入口在清理后分离拉起新实例 ----------------

def test_spawn_restart_launches_a_detached_self(monkeypatch):
    """重启拉起的必须是 ``python -m qwen3_voice``,且是分离进程。"""
    import subprocess

    launched = {}

    def fake_popen(args, **kwargs):
        launched["args"] = args
        launched["flags"] = kwargs.get("creationflags", 0)

    monkeypatch.setattr(subprocess, "Popen", fake_popen)

    entry._spawn_restart(types.SimpleNamespace(info=lambda *a: None,
                                               error=lambda *a: None))

    assert launched["args"] == [sys.executable, "-m", "qwen3_voice"]
    if sys.platform == "win32":
        assert launched["flags"] & subprocess.DETACHED_PROCESS
    else:
        assert launched["flags"] == 0


def test_spawn_restart_swallows_launch_errors(monkeypatch, caplog):
    """拉不起来(比如解释器路径异常)只记日志,不能把退出流程炸掉。"""
    import subprocess

    def boom(*_a, **_k):
        raise OSError("nope")

    monkeypatch.setattr(subprocess, "Popen", boom)

    # 不抛异常就是通过
    entry._spawn_restart(types.SimpleNamespace(info=lambda *a: None,
                                               error=lambda *a: None))
