# voice-mikon

Windows 全局语音输入法 + 小工具集合。
按一次全局热键(或点一下悬浮球)开始说话,识别出的文字直接打进当前光标位置 ——
类似手机输入法的语音键,但在 Windows 上,识别可以完全不出本机。

> 名字为什么叫 voice-mikon?见文末[附录](#附录为什么叫voicemikon) ——
> 一句话:みこーん!

> **许可证:AGPL-3.0-or-later**。录音胶囊的样式与波形算法移植自
> [OpenType](https://github.com/ct-jyjntc/opentype-public)(同为 AGPL-3.0),
> 见 [`LICENSE`](LICENSE)。

## 这是什么

- **录音胶囊**:按热键后弹出一颗胶囊 —— 上方是实时识别文字,下方是真实电平
  驱动的波形条和计时;说完再按一次,文字自动贴进光标处。左边一颗 ✕,随时反悔,
  不识别、不注入
- **悬浮球**(默认开):屏幕角落常驻一颗小球,点它原地展开成胶囊开始录音,
  拖它挪位置(松手记住);右键菜单带附属小工具(内置:二维码扫描)
- **两种识别后端**(互斥,面板一键切换):
  - `local` 本地 [Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR)
    (0.6B / 1.7B,GPU,音频不出本机,断网可用,模型常驻显存零等待)
  - `minimax` MiniMax 网络接口(不占显存,音频会上传;需 `MINIMAX_API_KEY`)
- **界面形态四档**:`悬浮球 / 常驻 / 按需 / 无界面`(见配置)
- 其它:热词(context biasing)、主题三档、剪贴板借还(绝不吞你刚复制的内容)、
  焦点保护(识别这 1 秒里切了窗口,文字仍贴回原窗口)

## 部署(写给帮用户部署的 AI 助手)

**动手前先问用户一个问题:要不要本地识别?** 这决定走哪条路线。
需要 Windows 10/11 + Python 3.12+;面板基于 WebView2(Win10/11 一般自带)。

### 路线 A:网络后端(用户不想下载模型、或没有 NVIDIA 显卡)

不需要模型权重,不需要 GPU。让 AI 替用户把配置写好:

```powershell
# 1. 项目目录内建环境
uv venv --python 3.12 .venv

# 2. 装依赖
uv pip install --python .venv\Scripts\python.exe -e .

# 3. 替用户写好最小配置 data/config.json(首次运行会自动建 data/ 目录):
#    { "backend": "minimax" }
#    其余键全部走内置默认值,写最小片段即可,不用抄全量模板。

# 4. 替用户写好 key 文件 data/.env(推荐方式,持久、portable、不碰系统):
#    MINIMAX_API_KEY=sk-...
#    key 由用户自己提供(AI 不要替用户编 key)。环境变量 MINIMAX_API_KEY
#    也可以用(优先级更高,适合临时换 key / CI 注入):
[Environment]::SetEnvironmentVariable("MINIMAX_API_KEY", "sk-...", "User")  # 可选覆盖
```

**API key 绝不写进 config.json** —— 配置文件会被手工编辑、截图、分享。
key 只从环境变量或 `data/.env` 读(后者是持久真相,已被 `.gitignore` 的
`*.env` 规则忽略)。缺 key 时切网络后端会明确报错,不会静默回落到本地。

### 路线 B:本地后端(用户要"音频不出本机")

需要 NVIDIA 显卡(8GB 显存足够)+ 模型权重。**`model_root` 是必填项**:
本地后端找不到权重会直接报错并给出下载命令,**不会偷偷联网下载**
(要不要下载、下到哪,由用户决定)。

```powershell
uv venv --python 3.12 .venv
uv pip install --python .venv\Scripts\python.exe -e .

# 下载权重(国内走 ModelScope;AI 助手可以直接帮用户跑):
modelscope download --model Qwen/Qwen3-ASR-0.6B --local_dir D:\models\Qwen3-ASR-0.6B
# 1.7B 可选:更准,更慢
modelscope download --model Qwen/Qwen3-ASR-1.7B --local_dir D:\models\Qwen3-ASR-1.7B
```

然后把权重根目录写进 `data/config.json`(其下应有 `Qwen3-ASR-0.6B/` 子目录):

```json
{ "backend": "local", "model_root": "D:\\models" }
```

### 启动

```powershell
python -m voice_mikon          # 或 venv 里的 voice-mikon 命令
```

也可双击 `runtime\bin\VoiceMikon.exe`(Rust 薄壳:任务管理器和剪贴板管理器里
显示 `VoiceMikon.exe` 而非 pythonw;构建方法见 `launcher/build.ps1`)。

启动后:托盘图标(灰=待机 / 黄=加载 / 红=录音)→ 按 **Ctrl+Alt+Space**
或点悬浮球说话。

### 可选依赖组

| 组 | 装法 | 用途 |
|---|---|---|
| `hotwords` | `pip install -e ".[hotwords]"` | 术语表管理器的分词(jieba) |
| `qr` | `pip install -e ".[qr]"` | 附属工具「二维码扫描」(mss + pyzbar) |

## 配置

配置与数据全在项目内 `data/`(portable,不写系统目录),首次运行自动创建。
`data/config.json` 不存在时用内置默认值,**没有模板文件** —— 配置的唯一真相是
`src/voice_mikon/config.py` 里带注释的字段;JSON 里写最小片段覆盖想要的键即可,
缺省键、未知键都安全(未知键忽略并告警,不会崩)。

| 键 | 默认 | 说明 |
|---|---|---|
| `hotkey` | `ctrl+alt+space` | 全局热键 |
| `backend` | `local` | `local` / `minimax`(互斥) |
| `model` | `0.6B` | 本地模型尺寸(`0.6B` 更快 / `1.7B` 更准) |
| `model_root` | `""` | 本地权重根目录(**本地后端必填**,留空启动即报错指路) |
| `minimax_model` | `asr-1.0` | 网络后端模型名 |
| `minimax_base_url` | `https://api.minimax.cn` | 海外站改 `api.minimax.io` |
| `input_device` | `null` | `null` = 自动挑选(带可用性实测) |
| `device_hints` | `["USB"]` | 自动挑选时优先匹配的名称子串 |
| `language` | `null` | `null` = 自动检测语种 |
| `hotwords` | `[]` | 术语表,识别时作上下文偏置(见下) |
| `theme` | `auto` | `auto` / `light` / `dark`(面板和胶囊一起换) |
| `ui_mode` | `orb` | 界面形态:`orb` 悬浮球 / `resident` 无球窗口常驻 / `on_demand` 按需(**未实现,行为等同 resident**) / `headless` 无界面(**只能改此键开启**,重启生效) |
| `orb.geometry` / `orb.monitor` | `""` | 球的位置记忆(拖球自动保存,换屏按显示器还原) |
| `show_preview` | `true` | 录音时显示实时预览文字条(任何后端都能预览) |
| `save_recordings` | `true` | 每轮录音存 WAV 到 `data/recordings/`(调试用,留最近 20 个) |
| `max_duration` | `120` | 单次录音上限(秒) |
| `restore_clipboard` | `true` | 粘贴后恢复原剪贴板内容 |
| `trailing_space` | `true` | 末尾自动补空格 |

环境变量:`VOICE_MIKON_HOME`(换数据目录) / `VOICE_MIKON_CONFIG`(指定配置路径) /
`VOICE_MIKON_LOG`(日志路径)。API key 的持久配置在 **`data/.env`**
(`MINIMAX_API_KEY=sk-...`);同名环境变量优先(临时覆盖用)。

### 术语表(热词)

Qwen3-ASR 内置 Context Biasing:传入的词会**偏向**出现在识别结果里
(公司名、产品代号、领域黑话靠它校准;Queen→Qwen 这类读音与写法差大的词
收益最大)。配置里 `hotwords` 数组填词;`data/hotwords.txt` 一行一词,
**每次识别实时读取**,改完立即生效。
`python scripts/hotwords_manager.py scan|build|show` 是一套从聊天记录提取
候选词的打分器工作流(全量候选公开,取舍由人定)。

## 给 AI 的扩展指南

**加新的识别后端**:实现 `src/voice_mikon/engines/base.py` 的 `ASRBackend`
契约(本地 `engine.py`、网络 `minimax.py` 是两个现成参考),枚举登记进
`config.py` 的 `ASR_BACKENDS`,面板与切换逻辑自动认得新档位。
规矩:API key 只走环境变量或 `data/.env`,绝不进 config.json;识别失败要有
明确的错误文案,不静默回落。

**写新的小工具**:放 `companions/<工具名>/`,架构**彻底独立** ——
与主包零 import(双向不知道对方存在),主程序只经 subprocess 拉起(崩了不相干),
UI 技术栈自选(二维码扫描用的 tkinter,没碰主程序的 pywebview),
依赖进 pyproject 可选组、共用主程序 venv。
现成参考:`companions/qr_scanner/`(截屏 → 框选 → 解码 → 自动复制)。

## 已知限制

- **`ui_mode: on_demand` 未实现**:选择会被保存(存意图,不偷偷改写),
  运行期按 `resident`(无球)行为执行。待机销毁/重建窗口会踩 pywebview
  动态建窗的坑 —— **不打算实现**,选它等于选 `resident`。
- **`headless` 只能改 config 开启**(面板上是 disabled 的第四档,重启生效)。
  无界面模式没有任何提示 UI(托盘图标颜色是唯一反馈),托盘是唯一退出通道,
  出问题看 `data/voice_input.log`。
- **网络后端不支持热词**(MiniMax 没有 context biasing 入口);切换成功的
  通知里会明说,词表本身不用改,切回本地照旧生效。
- 依赖目前是全量的:走网络后端也会装上 torch 等本地推理栈。而且，如果使用过本地模式，
 torch 会被导入导致高内存占用，不会自动卸载。必须启动时就以网络模式启动才能规避。

## 架构(简)

```
src/voice_mikon/
├── __main__.py       入口:装配 core/host/apps;CLI --headless 是装配自检
├── config.py         配置 schema(唯一真相源,字段带注释)
├── core/             状态机 + 录音编排 + 注入(不知道 GUI 存在)
├── engines/          识别后端:本地 / MiniMax 网络 / 流式预览 / VAD
├── audio/            设备实测选择 / 录音 / 自适应底噪估计
├── host/             窗口脏活:胶囊(透明+裁形+不抢焦点)、面板、托盘、热键
├── apps/             presenter 翻译层(core+host ↔ 前端桥接契约在这)
└── win32/            纯 ctypes 绑定(剪贴板/显示器/DPI/热键)

companions/           附属小工具(零 import,subprocess 拉起)
launcher/             Rust 薄壳(exe 图标+版本资源;build.ps1 构建)
runtime/              exe 的"venv 根"(pyvenv.cfg + launcher.toml)
```

前端是纯静态 HTML/CSS/JS(零 npm、零框架、零 CDN,离线可用);
分层规则、桥接契约与踩坑记录见 [`AGENTS.md`](AGENTS.md)。

## 附录:为什么叫 VoiceMikon

因为小玉藻是小清姬的好伙伴!
而且狐狸也很可爱!而且小玉藻更是可爱!
みこーん!
