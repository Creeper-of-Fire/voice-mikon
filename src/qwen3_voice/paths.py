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


def ensure_dirs() -> None:
    """确保数据目录存在。"""
    try:
        data_dir().mkdir(parents=True, exist_ok=True)
    except OSError:
        pass
