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

# 主题档位:
#   auto  跟随系统(Windows 的"应用模式",WebView2 直接跟着走)
#   light 强制白天
#   dark  强制黑夜
# 面板和胶囊共用这一套 —— 面板改了,胶囊跟着换。
THEME_MODES = ("auto", "light", "dark")

# 胶囊位置三档:
#   follow  跟随输入光标(像输入法候选框,贴着光标出现)
#   bottom  固定在主屏工作区底部居中(原来的行为)
#   custom  用户拖到哪就记在哪(存相对工作区的逻辑偏移)
CAPSULE_POSITION_MODES = ("follow", "bottom", "custom")

# 常驻悬浮球的位置三档(设计由用户拍板):
#   follow  跟随输入光标 —— 光标真的聚焦在输入处才露脸,
#           贴着光标出现(像输入法候选框);位置不归用户管,不可拖
#   pinned  记住的位置 —— 可拖、持久化(orb_geometry),和 custom 共用
#           同一份位置;同样只在光标聚焦到输入处才露脸
#   custom  记住的位置 —— 可拖、持久化,常驻露脸(球是录音入口,随时在)
#
# ⚠ 和胶囊的 CAPSULE_POSITION_MODES(follow/bottom/custom)是**两套
# 独立设计**:档位名相同不代表含义相同(球的 follow 显隐同胶囊 follow,
# 但球的档里没有 bottom,却有 pinned)。两边各自演化,不许互相参照。
ORB_POSITION_MODES = ("follow", "pinned", "custom")

# 悬浮球的待机策略两态(悬球窗口的"待机期"怎么过):
#   resident   窗口常驻,球形态待机(热键反馈即时)
#   on_demand  待机时整个窗口销毁,零 WebView2/GPU 占用
#               (热键时再建窗、恢复位置、展开)
# 与上面的 ORB_POSITION_MODES(位置档)是两回事:一个管"待不待机",
# 一个管"球在哪儿"。
ORB_STANDBY_MODES = ("resident", "on_demand")

# 识别后端(互斥,同一时刻只留一个):
#   local      本地 Qwen3-ASR 模型(transformers,GPU,完全离线)
#   dashscope  阿里云百炼 DashScope 网络接口(OpenAI 兼容 HTTP)
# API Key **不进配置文件** —— 只从环境变量 DASHSCOPE_API_KEY 读。
ASR_BACKENDS = ("local", "dashscope")


@dataclass
class OrbConfig:
    r"""悬浮球的配置(v2 schema 里的嵌套 orb 段)。

    单独一个 dataclass,和 v2 的写法保持一致 —— 球的几何位置、
    所在显示器、待不待机都收在这一段里,不再散成平铺字段。

    字段:
        enabled   悬浮球开不开
        standby   待机策略:resident(窗口常驻) / on_demand(待机时销毁窗口)
        geometry  球的位置记忆:"dx,dy",相对所在工作区左上角的逻辑像素偏移
        monitor   球的位置记忆的显示器设备名(如 \\.\DISPLAY1):
                  还原时先找那块屏,找不到就退回当前屏
    """

    enabled: bool = True
    standby: str = "resident"
    geometry: str = ""
    monitor: str = ""

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "OrbConfig":
        """只接受已知子键,忽略未知子键(避免旧配置文件导致崩溃)。"""
        known = {f.name for f in fields(cls)}
        unknown = sorted(k for k in data if k not in known)
        if unknown:
            log.warning("配置键 'orb' 含未知子键(已忽略):%s", ", ".join(unknown))
        return cls(**{k: v for k, v in data.items() if k in known})


@dataclass
class Config:
    """全部可配置项。

    注意 dataclass 字段顺序决定 to_dict 的输出顺序,写文件时更好读。
    """

    # ---- 热键与模型
    hotkey: str = "ctrl+alt+space"
    model: str = "0.6B"                       # 可选 0.6B / 1.7B
    model_root: str = r"D:\Dev\Environments\models"

    # ---- 识别后端(本地 / 网络,互斥)
    # local      = 本地 Qwen3-ASR 模型常驻显存,离线、零等待
    # dashscope  = 阿里云百炼网络接口,音频出本机,有网络延迟
    asr_backend: str = "local"
    # 网络后端的模型名与端点前缀(新加坡地域改 base_url 即可)。
    # **API Key 不在这里** —— 只从环境变量 DASHSCOPE_API_KEY 读。
    dashscope_model: str = "qwen3-asr-flash"
    dashscope_base_url: str = "https://dashscope.aliyuncs.com/compatible-mode/v1"

    # ---- 音频
    input_device: int | None = None           # None = 自动挑选
    device_name_hints: list[str] = field(default_factory=lambda: [
        "USB PnP Audio", "USB Audio", "Realtek HD Audio Mic",
    ])
    sample_rate: int = 16000                  # ASR 模型要求的采样率;重采样由 qwen_asr 内部完成
    max_duration: float = 120.0
    # 每轮录音结束后把原始音频存成 WAV(调试用)。
    # 默认开:出问题时需要**同一段真实录音**反复回放定位,
    # 光看日志不够。不想留就把这个开关关掉。
    save_recordings: bool = True

    # ---- 识别
    language: str | None = None               # None = 自动检测语种
    hotwords: list[str] = field(default_factory=list)

    # ---- 文本注入
    restore_clipboard: bool = True
    trailing_space: bool = True

    # ---- 界面
    panel_geometry: str = ""                 # "x,y,w,h",空=用默认位置
    panel_visible: bool = False              # 面板启动时是否显示(默认不常驻,托盘打开)
    show_tray: bool = True
    theme: str = "auto"                      # auto(跟随系统) / light(白天) / dark(黑夜)
    # 胶囊出现在哪:
    #   follow  跟着输入光标(默认)
    #   bottom  固定在主屏工作区底部居中
    #   custom  用户拖出来的位置(记在 capsule_geometry 里)
    capsule_position: str = "follow"
    # 录音时要不要显示实时预览文字条。关掉之后胶囊只剩波形+计时+按钮,
    # "已输入"/错误提示改在胶囊内的 label 位显示。
    show_preview: bool = True
    # 自定义模式下记忆的胶囊位置:"dx,dy",相对**所在工作区左上角**的
    # 逻辑像素偏移(存相对量,换缩放率/换显示器都不飞)。
    capsule_geometry: str = ""
    # 自定义模式下记忆的显示器设备名(如 \\.\DISPLAY1):还原时先找到
    # 那块屏,找不到就退回当前屏。
    capsule_monitor: str = ""
    # 常驻悬浮球:屏幕上常驻一颗小球 —— 点它开始录音(展开成胶囊),
    # 拖它换位置(松手就记住)。默认**关**:关着时行为和以前一模一样
    # (按热键才弹胶囊),老用户的体验一点不变。
    floating_orb: bool = False
    # 悬浮球出现在哪:
    #   follow  跟随输入光标(默认)—— 光标聚焦在输入框里才露脸,
    #           贴着光标出现
    #   custom  用户拖出来的位置(记在 orb_geometry 里)
    orb_position: str = "follow"
    # 悬浮球位置记忆:"dx,dy",相对**所在工作区左上角**的逻辑像素偏移。
    # 和 capsule_geometry 一个路子,但两套位置**互不干扰**:
    # 球的位置归球,胶囊的位置归胶囊。
    orb_geometry: str = ""
    # 悬浮球位置记忆的显示器设备名(如 \\.\DISPLAY1):还原时先找那块屏,
    # 找不到就退回当前屏。
    orb_monitor: str = ""

    # ---- 其它
    log_file: str = ""
    log_level: str = "INFO"

    # ---- v2 schema 新增字段(expand 阶段:与上面的旧字段并存)
    #
    # 下面的 backend / device_hints / orb 是 v2 写法,目前**还没有消费方** ——
    # 引擎和音频仍读旧的 asr_backend / device_name_hints / floating_orb 等。
    # 等 host 窗口层落地、contract 阶段把旧字段退役时,再统一切到这组。
    #
    # ⚠ backend 与旧的 asr_backend、device_hints 与旧的 device_name_hints
    #   语义相同(只是 v2 改了名),并存期间**不要**两边都当真值用。
    #   orb 与旧的 floating_orb / orb_position / orb_geometry / orb_monitor
    #   同理:v2 把球的几何/显示器/待机策略收进了 orb 这一段。
    backend: str = "local"                     # local / dashscope(v2 改名)
    device_hints: list[str] = field(default_factory=lambda: ["USB"])
    orb: OrbConfig = field(default_factory=OrbConfig)

    # ---------- 校验 ----------

    def validate(self) -> list[str]:
        """返回问题列表,空列表表示配置正常。"""
        issues: list[str] = []
        if self.model not in ("0.6B", "1.7B"):
            issues.append(f"model 只能是 0.6B 或 1.7B,当前 {self.model!r}")
        if self.asr_backend not in ASR_BACKENDS:
            issues.append(
                f"asr_backend 只能是 {' / '.join(ASR_BACKENDS)},"
                f"当前 {self.asr_backend!r}")
        if self.sample_rate <= 0:
            issues.append("sample_rate 必须为正数")
        if self.max_duration <= 0:
            issues.append("max_duration 必须为正数")
        if self.theme not in THEME_MODES:
            issues.append(
                f"theme 只能是 {' / '.join(THEME_MODES)},当前 {self.theme!r}")
        if self.capsule_position not in CAPSULE_POSITION_MODES:
            issues.append(
                "capsule_position 只能是 "
                f"{' / '.join(CAPSULE_POSITION_MODES)},"
                f"当前 {self.capsule_position!r}")
        if self.orb_position not in ORB_POSITION_MODES:
            issues.append(
                "orb_position 只能是 "
                f"{' / '.join(ORB_POSITION_MODES)},"
                f"当前 {self.orb_position!r}")

        # ---- v2 schema 新增字段的校验 ----
        # 类型检查照 v2 写法(backend 枚举 / device_hints 字符串列表 /
        # orb.enabled 布尔 / orb.standby 枚举 / orb.geometry、orb.monitor
        # 字符串),但按本文件的风格统一汇进 issues 列表。
        if self.backend not in ASR_BACKENDS:
            issues.append(
                f"backend 只能是 {' / '.join(ASR_BACKENDS)},"
                f"当前 {self.backend!r}")
        if not isinstance(self.device_hints, list) or not all(
                isinstance(h, str) for h in self.device_hints):
            issues.append("device_hints 必须是字符串列表")
        if not isinstance(self.orb, OrbConfig):
            issues.append(f"orb 必须是对象,当前 {type(self.orb).__name__}")
        else:
            if not isinstance(self.orb.enabled, bool):
                issues.append("orb.enabled 必须是布尔值")
            if self.orb.standby not in ORB_STANDBY_MODES:
                issues.append(
                    "orb.standby 只能是 "
                    f"{' / '.join(ORB_STANDBY_MODES)},"
                    f"当前 {self.orb.standby!r}")
            if self.orb.geometry is not None and not isinstance(
                    self.orb.geometry, str):
                issues.append("orb.geometry 必须是字符串")
            if self.orb.monitor is not None and not isinstance(
                    self.orb.monitor, str):
                issues.append("orb.monitor 必须是字符串")
        return issues

    # ---------- 序列化 ----------

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "Config":
        """只接受已知字段,忽略未知键(避免旧配置文件导致崩溃)。"""
        known = {f.name for f in fields(cls)}
        clean = {k: v for k, v in data.items() if k in known}
        # orb 是嵌套对象:配置文件里是一段 dict,这里转成 OrbConfig。
        # 不是 dict 就原样留着,交给 validate() 报错。
        if isinstance(clean.get("orb"), dict):
            clean["orb"] = OrbConfig.from_dict(clean["orb"])
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
                # orb 是嵌套对象:传 dict 就转成 OrbConfig,别让类型漂了。
                if k == "orb" and isinstance(v, dict):
                    v = OrbConfig.from_dict(v)
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
    ``engines/engine.py`` 在每次识别时实时读取(改完立即生效)。
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
    # 词表由 engines/engine.py 在每次识别时实时读取(改完立即生效)。
    # 这里只管 config.json 里的静态 hotwords 字段。

    # 补全派生字段
    if not cfg.log_file:
        cfg.log_file = str(log_path())
    return cfg


def current_config_path() -> Path:
    """当前实际使用的配置文件路径(用于「打开设置文件」菜单)。"""
    return config_path()
