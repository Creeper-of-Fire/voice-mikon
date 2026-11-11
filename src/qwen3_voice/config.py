"""配置管理。

配置来源优先级(后者覆盖前者):
    1. 内置默认值(Config dataclass 的字段初始值)
    2. 项目内的 data/config.json(portable,见仓库根目录 AGENTS.md)

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

from .paths import config_path, ensure_dirs, hotwords_path, log_path

log = logging.getLogger("qwen3_voice.config")

# 主题档位:
#   auto  跟随系统(Windows 的"应用模式",WebView2 直接跟着走)
#   light 强制白天
#   dark  强制黑夜
# 面板和胶囊共用这一套 —— 面板改了,胶囊跟着换。
THEME_MODES = ("auto", "light", "dark")

# 胶囊位置三档、悬浮球位置三档(follow/pinned/custom)、待机两态
# (resident/on_demand)均已退役:悬浮球开关 × 待机策略合并成下面这行。

# 界面形态四档(悬浮球开关 × 待机策略合并后的单一维度,用户拍板):
#   orb        悬浮球常驻:屏幕上常驻一颗球,点它开始录音,拖它挪位置
#   resident   无球,窗口常驻:按热键弹胶囊,终态停留后窗口隐藏
#   on_demand  按需:**存意图、不存现实** —— config 保存用户选的原值,
#              运行期按 resident 行为执行(待机销毁/重建窗口会踩
#              pywebview 动态建窗的坑,未实现);面板 hover 会写明
#              「暂未实现,当前行为等同常驻」。实现后存量配置无感升级
#   headless   无界面:不建任何 GUI 窗口,只有托盘和热键的开发者选项,
#              **只能改 config 手动开启**(重启生效)—— 面板上是
#              disabled 的第四档:知道存在、不给误触入口
UI_MODES = ("orb", "resident", "on_demand", "headless")

# 识别后端(互斥,同一时刻只留一个):
#   local    本地 Qwen3-ASR 模型(transformers,GPU,完全离线)
#   minimax  MiniMax 平台网络接口(asr-1.0,multipart 上传音频)
# API Key **不进配置文件** —— 只从环境变量 MINIMAX_API_KEY 读。
ASR_BACKENDS = ("local", "minimax")

# 退役的后端名 → 回退到哪个。
#
# 为什么留这张表而不是"直接崩掉":老配置不该把程序搞死 —— 用户只是
# 升级了程序,配置还是老的。但也**不能静默**:回退时发一条 warning,
# 让用户知道配的后端没了(不然他会以为"切了没反应")。
LEGACY_BACKEND_ALIASES = {"dashscope": "local"}

# 预览断句用哪种"人声判定层"(只影响**预览**的切句,不影响最终识别):
#   silero  Silero VAD 神经网络判定(默认)—— 模型随包安装,离线可用
#   energy  能量域判定(底噪估计 + 滞回门)—— Silero 不可用时的降级路径
#
# 显式配 energy 不发通知(那是用户自己选的);配 silero 但模型缺失/
# 加载失败/采样率不支持时,自动降级并发一条 warning 通知。
PREVIEW_VAD_MODES = ("silero", "energy")


@dataclass
class OrbConfig:
    r"""悬浮球的位置记忆。

    球的**显示形态**归 ``Config.ui_mode`` 管(合并后那是唯一开关);
    这里只剩**位置** —— 拖球松手后记下相对工作区的偏移和所在
    显示器,重启后还原。ui_mode 不是 orb 时这份记忆只是躺着,
    等哪天切回来再用。

    字段:
        geometry  "dx,dy",相对所在工作区左上角的逻辑像素偏移
        monitor   显示器设备名(如 \\.\DISPLAY1):还原时先找那块屏,
                  找不到就退回当前屏
    """

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
    # 本地 Qwen3-ASR 权重所在的根目录(其下应有 Qwen3-ASR-0.6B/ 等子目录)。
    # 默认空 = 不指给任何人本机路径:本地后端必须显式配置,缺权重时
    # 明确报错指路(见 engines/engine.py),**绝不静默联网下载**。
    model_root: str = ""

    # ---- 识别后端(本地 / 网络,互斥)
    # local    = 本地 Qwen3-ASR 模型常驻显存,离线、零等待
    # minimax  = MiniMax 平台网络接口,音频出本机,有网络延迟
    backend: str = "local"
    # 网络后端的模型名与端点前缀。
    # 国内站是 api.minimax.cn;海外站改成 https://api.minimax.io。
    # **API Key 不在这里** —— 只从环境变量 MINIMAX_API_KEY 读。
    minimax_model: str = "asr-1.0"
    minimax_base_url: str = "https://api.minimax.cn"

    # ---- 音频
    input_device: int | None = None           # None = 自动挑选
    # 自动挑选麦克风时优先匹配这些名称子串。
    device_hints: list[str] = field(default_factory=lambda: ["USB"])
    sample_rate: int = 16000                  # ASR 模型要求的采样率;重采样由 qwen_asr 内部完成
    max_duration: float = 120.0
    # 每轮录音结束后把原始音频存成 WAV(调试用)。
    # 默认开:出问题时需要**同一段真实录音**反复回放定位,
    # 光看日志不够。不想留就把这个开关关掉。
    save_recordings: bool = True

    # ---- 识别
    language: str | None = None               # None = 自动检测语种
    hotwords: list[str] = field(default_factory=list)

    # ---- 预览断句
    # 预览层用什么判定"这一帧是不是人声"。**只影响预览**:
    # 最终识别永远是把整段音频直送引擎,不加任何本地预判。
    preview_vad: str = "silero"

    # ---- 文本注入
    restore_clipboard: bool = True
    trailing_space: bool = True

    # ---- 界面
    panel_geometry: str = ""                 # "x,y,w,h",空=用默认位置
    panel_visible: bool = False              # 面板启动时是否显示(默认不常驻,托盘打开)
    show_tray: bool = True
    theme: str = "auto"                      # auto(跟随系统) / light(白天) / dark(黑夜)
    # 界面形态四档(见文件头 UI_MODES):悬浮球 / 常驻 / 按需 / 无界面。
    # 默认 orb(球常驻) —— 合并前 orb.enabled 默认开,行为不变。
    ui_mode: str = "orb"
    # 录音时要不要显示实时预览文字条。关掉之后胶囊只剩波形+计时+按钮,
    # "已输入"/错误提示改在胶囊内的 label 位显示。
    show_preview: bool = True
    # 悬浮球的位置记忆(拖球松手后记住,重启还原;形态归 ui_mode 管)。
    orb: OrbConfig = field(default_factory=OrbConfig)

    # ---- 其它
    log_file: str = ""
    log_level: str = "INFO"

    # ---------- 校验 ----------

    def validate(self) -> list[str]:
        """返回问题列表,空列表表示配置正常。"""
        issues: list[str] = []
        if self.model not in ("0.6B", "1.7B"):
            issues.append(f"model 只能是 0.6B 或 1.7B,当前 {self.model!r}")
        if self.backend not in ASR_BACKENDS:
            issues.append(
                f"backend 只能是 {' / '.join(ASR_BACKENDS)},"
                f"当前 {self.backend!r}")
        if self.sample_rate <= 0:
            issues.append("sample_rate 必须为正数")
        if self.preview_vad not in PREVIEW_VAD_MODES:
            issues.append(
                "preview_vad 只能是 "
                f"{' / '.join(PREVIEW_VAD_MODES)},"
                f"当前 {self.preview_vad!r}")
        if self.max_duration <= 0:
            issues.append("max_duration 必须为正数")
        if self.theme not in THEME_MODES:
            issues.append(
                f"theme 只能是 {' / '.join(THEME_MODES)},当前 {self.theme!r}")
        if self.ui_mode not in UI_MODES:
            issues.append(
                f"ui_mode 只能是 {' / '.join(UI_MODES)},当前 {self.ui_mode!r}")
        if not isinstance(self.device_hints, list) or not all(
                isinstance(h, str) for h in self.device_hints):
            issues.append("device_hints 必须是字符串列表")
        if not isinstance(self.orb, OrbConfig):
            issues.append(f"orb 必须是对象,当前 {type(self.orb).__name__}")
        else:
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

    优先级:显式参数 > 环境变量 > data/config.json > 默认值

    术语表文件 ``data/hotwords.txt`` 不在这里读 —— 由
    ``engines/engine.py`` 在每次识别时实时读取(改完立即生效)。
    """
    ensure_dirs()

    candidates: list[Path] = []
    if path is not None:
        candidates.append(path)
    candidates.append(config_path())

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

    # 退役的后端值:回退 + **一条** warning。
    #
    # 不崩(老配置不该把程序搞死),也不静默(静默的话用户只会觉得
    # "切了没反应")。
    retired_names: list[str] = []
    raw = str(getattr(cfg, "backend", "") or "").strip().lower()
    fallback = LEGACY_BACKEND_ALIASES.get(raw)
    if fallback:
        cfg.backend = fallback
        retired_names.append(raw)
    if retired_names:
        # **把退役的值写进日志** —— 用户得看见自己配的是哪个,
        # 才知道该改哪里。
        log.warning("配置里的后端值已退役(%s):已回退为 local —— "
                    "请把 backend 改成 \"local\" 或 \"minimax\"",
                    " / ".join(sorted(set(retired_names))))

    return cfg


def current_config_path() -> Path:
    """当前实际使用的配置文件路径(用于「打开设置文件」菜单)。"""
    return config_path()
