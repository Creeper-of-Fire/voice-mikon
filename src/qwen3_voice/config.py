"""配置管理。

配置来源优先级(后者覆盖前者):
    1. 内置默认值 DEFAULT_CONFIG
    2. 项目内的 data/config.json(portable,见仓库根目录 AGENTS.md)
    3. 仓库里的 config.example.json(模板)

关于历史状态迁移:代码里**不写**旧位置适配逻辑。
从旧位置(比如 %APPDATA%)搬数据是一次性人工操作 ——
让代码永远背着某个版本的包袱,正是 vibecoding 项目该避免的事。
"""

from __future__ import annotations

import json
import logging
from dataclasses import dataclass, field, asdict, fields
from pathlib import Path
from typing import Any

from .paths import PROJECT_DIR, config_path, ensure_dirs, hotwords_path, log_path

log = logging.getLogger("qwen3_voice.config")

# 仓库内的示例配置(首次运行时复制到 data/)
EXAMPLE_CONFIG = PROJECT_DIR / "config.example.json"


@dataclass
class Config:
    """全部可配置项。

    注意 dataclass 字段顺序决定 to_dict 的输出顺序,写文件时更好读。
    """

    # ---- 热键与模型
    hotkey: str = "ctrl+alt+space"
    model: str = "0.6B"                       # 可选 0.6B / 1.7B
    model_root: str = r"D:\Dev\Environments\models"

    # ---- 音频
    input_device: int | None = None           # None = 自动挑选
    device_name_hints: list[str] = field(default_factory=lambda: [
        "USB PnP Audio", "USB Audio", "Realtek HD Audio Mic",
    ])
    sample_rate: int = 16000                  # ASR 模型要求的采样率;重采样由 qwen_asr 内部完成
    min_duration: float = 0.3
    max_duration: float = 120.0
    silence_rms: float = 0.002                # 低于此电平视为静音

    # ---- 识别
    language: str | None = None               # None = 自动检测语种
    hotwords: list[str] = field(default_factory=list)

    # ---- 文本注入
    restore_clipboard: bool = True
    trailing_space: bool = True

    # ---- 界面
    panel_geometry: str = ""                 # "x,y,w,h",空=用默认位置
    panel_visible: bool = True                # 面板启动时是否显示
    show_tray: bool = True

    # ---- 其它
    log_file: str = ""
    log_level: str = "INFO"

    # ---------- 校验 ----------

    def validate(self) -> list[str]:
        """返回问题列表,空列表表示配置正常。"""
        issues: list[str] = []
        if self.model not in ("0.6B", "1.7B"):
            issues.append(f"model 只能是 0.6B 或 1.7B,当前 {self.model!r}")
        if self.sample_rate <= 0:
            issues.append("sample_rate 必须为正数")
        if self.min_duration < 0:
            issues.append("min_duration 不能为负")
        if self.max_duration <= self.min_duration:
            issues.append("max_duration 必须大于 min_duration")
        if self.silence_rms < 0:
            issues.append("silence_rms 不能为负")
        return issues

    # ---------- 序列化 ----------

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "Config":
        """只接受已知字段,忽略未知键(避免旧配置文件导致崩溃)。"""
        known = {f.name for f in fields(cls)}
        clean = {k: v for k, v in data.items() if k in known}
        return cls(**clean)

    def save(self, path: Path | None = None) -> None:
        """保存配置到 data/config.json(路径见 paths.py)。"""
        target = path or config_path()
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(
            json.dumps(self.to_dict(), indent=2, ensure_ascii=False),
            encoding="utf-8",
        )

    def update(self, **kwargs: Any) -> None:
        """更新字段并忽略未知键。"""
        known = {f.name for f in fields(self)}
        for k, v in kwargs.items():
            if k in known:
                setattr(self, k, v)


def load_hotwords_file(path: Path | None = None) -> list[str]:
    """读术语表文件(一行一词,``#`` 开头是注释,空行忽略)。

    文件不存在是正常情况 —— 没有术语而已,不算错误。
    """
    target = path or hotwords_path()
    if not target.is_file():
        return []
    try:
        text = target.read_text(encoding="utf-8")
    except OSError as e:
        log.warning("术语表 %s 读取失败: %s", target, e)
        return []

    words: list[str] = []
    seen: set[str] = set()
    for line in text.splitlines():
        w = line.strip()
        if not w or w.startswith("#"):
            continue
        if w not in seen:
            seen.add(w)
            words.append(w)
    return words


def load_config(path: Path | None = None) -> Config:
    """加载配置。

    优先级:显式参数 > 环境变量 > data/config.json > config.example.json > 默认值

    术语表文件 ``data/hotwords.txt`` 不在这里读 —— 由
    ``asr/engine.py`` 在每次识别时实时读取(改完立即生效)。
    """
    ensure_dirs()

    candidates: list[Path] = []
    if path is not None:
        candidates.append(path)
    candidates.append(config_path())
    candidates.append(EXAMPLE_CONFIG)

    for cand in candidates:
        if cand.is_file():
            try:
                data = json.loads(cand.read_text(encoding="utf-8"))
                cfg = Config.from_dict(data)
                log.debug("配置来自 %s", cand)
                break
            except (OSError, json.JSONDecodeError) as e:
                log.warning("配置文件 %s 读取失败(%s),回退默认值", cand, e)
                continue
    else:
        cfg = Config()

    # 注意:hotwords.txt 文件**不在这里并入** ——
    # 词表由 asr/engine.py 在每次识别时实时读取(改完立即生效)。
    # 这里只管 config.json 里的静态 hotwords 字段。

    # 补全派生字段
    if not cfg.log_file:
        cfg.log_file = str(log_path())
    return cfg


def current_config_path() -> Path:
    """当前实际使用的配置文件路径(用于「打开设置文件」菜单)。"""
    return config_path()
