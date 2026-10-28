"""运行时路径 —— 全部 portable,跟着项目目录走。

约定见仓库根目录的 AGENTS.md:配置、日志、缓存一律放在项目目录内部,
不要写到 %APPDATA% / %LOCALAPPDATA% 等系统位置。

原因:这个仓库里的工具迭代很快、配置格式经常变。把配置放到系统目录里,
旧版本留下的文件会污染新版本,而且它不在项目里,git status 看不到,
删掉项目目录它还在 —— 换机器还复现不了。状态跟着代码走才能复现和回滚。

可以用环境变量 QWEN3_VOICE_HOME 覆盖数据目录(便携使用 / 测试)。
"""

from __future__ import annotations

import os
from pathlib import Path

# src/qwen3_voice/paths.py -> src/qwen3_voice -> src -> 项目根
PACKAGE_DIR = Path(__file__).resolve().parent
PROJECT_DIR = PACKAGE_DIR.parent.parent


def data_dir() -> Path:
    """运行时数据目录(配置、日志都在这)。"""
    override = os.environ.get("QWEN3_VOICE_HOME")
    base = Path(override).expanduser() if override else PROJECT_DIR / "data"
    return base


def config_path() -> Path:
    """用户配置文件。"""
    override = os.environ.get("QWEN3_VOICE_CONFIG")
    if override:
        return Path(override).expanduser()
    return data_dir() / "config.json"


def log_path() -> Path:
    """日志文件。"""
    override = os.environ.get("QWEN3_VOICE_LOG")
    if override:
        return Path(override).expanduser()
    return data_dir() / "voice_input.log"


def hotwords_path() -> Path:
    """术语表文件(一行一词,``#`` 开头是注释)。

    独立于 config.json —— 术语表经常要增删,放单独文件方便编辑,
    也不用每次改都去动配置。文件不存在是正常情况(没有术语)。
    """
    return data_dir() / "hotwords.txt"


def webview_dir() -> Path:
    """WebView2 运行时数据目录。

    WebView2 默认把用户数据(Cookies、缓存、LocalStorage)写到
    %LOCALAPPDATA%,那违反 portable 约定 —— 项目目录之外的东西
    git status 看不到、删项目也删不掉。所以显式指到项目内的 data/。
    """
    return data_dir() / "webview"


def ensure_dirs() -> None:
    """确保数据目录存在。"""
    for d in (data_dir(), webview_dir()):
        try:
            d.mkdir(parents=True, exist_ok=True)
        except OSError:
            pass
