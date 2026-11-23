# voice-mikon-next

[voice-mikon](../voice-mikon/)（Windows 全局语音输入，Python + pywebview）的
**C# / Avalonia 11 / .NET 10 重写**。

按一次全局热键（或点一下悬浮球）开始说话，识别出的文字直接打进当前光标位置 ——
和手机输入法的语音键一样，但识别可以完全不出本机。

## 和 Python 版是什么关系

- **这是重写，不是替代**。`../voice-mikon/` **仍在运行**，仍然是日常在用的那一版；
  两个程序各跑各的（不同进程、不同 `data/`）。
  ⚠ **但默认热键是同一个**（两边都是 `ctrl+alt+space`）—— 同时跑的话后注册的那个
  会注册失败（发一条 `hotkey_failed` 通知）。要同时开，先给其中一个改热键：
  改 `data/config.json` 的 `hotkey` 字段即可。
- **重写的动机**：Python 版常驻 300–500 MB；Avalonia 窗口层（`archive/` 的验证壳）
  NativeAOT 工作集约 **80 MB**（JIT 约 117 MB），并且原生窗口层能做到
  透明 / 不抢焦点 / 点击穿透。实测数字见
  [`archive/docs/feasibility.md`](archive/docs/feasibility.md)。
- **视觉与配置是"照搬"**：`capsule.css` / `panel.css` 里的尺寸、色值、动画参数
  一个数没改；`data/config.json` 的字段名与 Python 版**逐字一致**，旧配置直接能用
  （唯一一处口径不同，见下面的「配置」）。
- **本地推理改成 Python 子进程**：C# 主进程不再背着几百 MB 的 torch，
  只有真正要用本地模型时才起 `python/asr_server/` 那个子进程。

## 目录结构

```
src/VmNext.Shell/       窗口层：Avalonia 窗口子类（透明/不激活/置顶/穿透/DPI）+ 全部 Win32 P/Invoke
src/VmNext.Core/        状态机 / 两个 presenter / 注入 / 剪贴板 / 配置 / 路径 / 通知中心
src/VmNext.Audio/       录音（NAudio WASAPI）/ 设备挑选 / 底噪估计 / 录音留存
src/VmNext.Engines/     识别引擎：本地（Python 子进程 IPC）/ MiniMax 网络 / VAD / DSP
src/VmNext.Ui/          胶囊窗口 + 面板窗口 + 拖拽 / 摆位 / 命中区
src/VmNext.App/         GUI 入口 + 托盘装配 + `--headless` 自检 + `--mock` 假后端
src/demos/Capsule.Demo/ 离线视觉预览（不连 core，热键直接推 mode、波形用模拟电平）
python/asr_server/      本地推理子进程（named pipe IPC，协议定稿见其 README）
tests/                  只留纯计算 / 无头的测试（见「测试」）
docs/                   过程记录（k1–k5）+ presenter 契约（core-api.md）
archive/                2026-11-23 的可行性验证（冻结，不是设计参考）
data/                   运行时数据：配置 / 日志 / 录音留存（portable，不进 git）
```

依赖方向：`Engines` 与 `Shell` 不引用本仓库任何项目；`Core → Shell + Engines`；
`Audio → Core`；`Ui → Core + Shell`；`App` 装配一切。详见 [`AGENTS.md`](AGENTS.md)。

## 运行方式

```
# GUI（双击 src/VmNext.App/bin/Release/net10.0/VmNext.App.exe 也一样）
dotnet run --project src/VmNext.App

# 无头自检：装配 core，等引擎就绪后干净退出
dotnet run --project src/VmNext.App -- --headless --mock --timeout 60

# 覆盖后端 / 配置文件（一次性覆盖，不写回 config.json）
dotnet run --project src/VmNext.App -- --backend minimax
dotnet run --project src/VmNext.App -- --config D:\path\to\config.json
```

| 参数 | 说明 |
|---|---|
| `--headless` | 无头**自检**：不起 GUI，等引擎就绪后退出。退出码 **0** = 就绪，**2** = 引擎加载失败 / 超时 |
| `--timeout <秒>` | 等引擎就绪的上限（默认 300） |
| `--backend local\|minimax` | 覆盖配置里的后端（**不写回配置文件** —— 那是一次性覆盖，不是用户改的设置） |
| `--config <路径>` | 覆盖配置文件路径（优先级：`--config` > `data/config.json` > 内置默认值） |
| `--mock` | 本地后端走 mock 模式：**不加载真模型**，不 import torch / qwen_asr |

- 未知参数**照旧忽略、不报错**（对齐 Python 版的 `parse_known_args`）。
- `--mock` 是这一版**新增**的开关（Python 版没有）：自检不能加载真模型（真模型要几十秒），
  `--mock` 的 `load` 立刻返回 ready。
- GUI 形态是 `WinExe`：双击运行时没有控制台窗口，程序安静地在后台跑。
  从 cmd / PowerShell 启动时输出照常可见（子进程已继承父控制台）；
  用 PowerShell 的 `&` 直接跑 WinExe 时**PowerShell 不会等它** ——
  要等退出码请用 `Start-Process -Wait -PassThru`。
- 配置里的 `ui_mode = "headless"` 也会走控制台路径（那一档在面板上是 disabled，
  只能改 config 手动开启，重启生效）。

## 配置

- 路径：`data/config.json`（`VOICE_MIKON_HOME` / `VOICE_MIKON_CONFIG` 可覆盖）。
- **JSON 字段名与 Python 版逐字一致**（`hotkey` / `model_root` / `save_recordings` …），
  用户现有的 `data/config.json` 直接能用。

### 唯一一处口径不同：`input_device`

| | Python 版 | 本版 |
|---|---|---|
| 记的是什么 | PortAudio 的**全局设备索引**（输出设备也占号） | NAudio 的**采集端点列表内序号** |

NAudio 没有"全局设备索引"这个概念，硬凑一张映射表反而更不可靠（枚举顺序也不保证稳定）。
**后果**：从 Python 版拷过来的 `input_device` 数值不能直接沿用 ——
**首次要重选一次设备，或者先留 `null`（= 自动挑选）**。

### 主要配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `hotkey` | `ctrl+alt+space` | 全局热键 |
| `model` | `0.6B` | 本地模型规格（`0.6B` / `1.7B`） |
| `model_root` | 空 | 本地权重根目录；权重在 `<model_root>/Qwen3-ASR-<tag>` |
| `backend` | `local` | `local` / `minimax` |
| `minimax_model` / `minimax_base_url` | `asr-1.0` / `https://api.minimax.cn` | 网络后端 |
| `input_device` | `null` | 采集端点序号（见上）；`null` = 自动挑选 |
| `device_hints` | `["USB"]` | 自动挑麦克风时优先匹配的名称子串 |
| `sample_rate` | `16000` | 送进模型的采样率（重采样在引擎内部做） |
| `max_duration` | `120.0` | 单次录音上限（秒），到点自动停并发 `recording_timeout` 通知 |
| `save_recordings` | `true` | 每轮录音结束后把原始音频存成 WAV（调试用） |
| `language` | `null` | `null` = 自动检测语种 |
| `hotwords` | `[]` | 词表（与 `data/hotwords.txt` 合并） |
| `preview_vad` | `silero` | 预览断句的判定层（`silero` / `energy`）；**只影响预览** |
| `restore_clipboard` | `true` | 识别后要不要把原剪贴板内容还回去（面板「归还」开关） |
| `trailing_space` | `true` | 注入时补不补末尾空格 |
| `panel_geometry` | 空 | 面板位置记忆（`"x,y,w,h"`，逻辑像素，格式与 Python 版一致） |
| `show_tray` | `true` | 要不要显示托盘图标 |
| `theme` | `auto` | `auto` / `light` / `dark` |
| `ui_mode` | `orb` | `orb` / `resident` / `on_demand` / `headless` |
| `show_preview` | `true` | 录音时显示实时预览文字条 |
| `orb.geometry` / `orb.monitor` | 空 | 悬浮球位置记忆（拖球松手后写，格式与 Python 版一致） |
| `log_level` | `INFO` | 日志级别 |

- **API key 不进配置文件**：`MINIMAX_API_KEY` 环境变量优先，`data/.env` 兜底。
- 未知键 → warning + 忽略；已知键类型对不上 → warning + 用默认值顶上。**不崩**。
- 词表在 `data/hotwords.txt`（一行一词，`#` 开头是注释），实时热加载，改完立即生效。

## 本地推理

- 子进程脚本：`python/asr_server/asr_server.py`（**自包含**，不 import `voice_mikon` 包）。
- **依赖**：系统 python + `torch` + `qwen-asr`（官方包，内含 `Qwen3ASRModel`）+ `numpy`。
  - ⚠ **不要用 `voice-mikon` 的 venv 跑** —— 那是另一个工程的依赖环境。
  - C# 侧默认用 `python`，可用环境变量 `VMNEXT_PYTHON` 覆盖。
- **权重**：`<model_root>/Qwen3-ASR-<tag>`。**只认本地目录，绝不联网下载**；
  缺了就明确报错指路（下载命令，或把后端切成 `minimax`）。
- **IPC**：Windows 命名管道 `\\.\pipe\vmnext-asr-{进程ID}-{8 位随机}`，
  控制消息是一行 UTF-8 JSON，音频是紧随其后的 int16 小端裸帧。
  **协议定稿见 [`python/asr_server/README.md`](python/asr_server/README.md)**。

## 测试

**只留"有对错规范"的测试**：纯计算、协议编解码、无头控件断言。

| 项目 | 内容 | 怎么跑 |
|---|---|---|
| `tests/VmNext.Core.Tests` | 配置读写 / 电平映射 / 面板 presenter | `cd tests/VmNext.Core.Tests; dotnet test` |
| `tests/VmNext.Audio.Tests` | 底噪估计 / WAV 落盘 | `cd tests/VmNext.Audio.Tests; dotnet test` |
| `tests/VmNext.Engines.Tests` | 重采样 / VAD / 分句状态机 / IPC 协议 / 网络后端 | `cd tests/VmNext.Engines.Tests; dotnet test` |
| `tests/VmNext.Ui.Tests` | 胶囊与面板的显示状态 / 摆位 / 拖拽几何 / 命中区 / 动效降级 | `cd tests/VmNext.Ui.Tests; dotnet test` |
| `tests/VmNext.TokenCheck` | `capsule.css` 的色值与 palette 逐个核对（不是测试项目，是个小程序；不一致时退出码非 0） | `dotnet run --project tests/VmNext.TokenCheck` |

> ⚠ **必须进项目目录跑**：MTP（Microsoft.Testing.Platform）项目在 .NET 10 SDK 上
> 不能走 VSTest target，官方 opt-in 是在**项目目录**放 `global.json`
> （`test.runner = Microsoft.Testing.Platform`）。从仓库根跑
> `dotnet test tests\...` 不会命中那份 `global.json`，会报
> "Testing with VSTest target is no longer supported by Microsoft.Testing.Platform"。

### 为什么不写碰桌面的测试

测试**不注入输入 / 不抢焦点 / 不写剪贴板 / 不注册热键 / 不弹窗 / 不占麦克风**。
这些行为**没有对错规范** —— 拖拽跟不跟手、托盘菜单弹出来焦点在不在、退出顺序
对不对，是"像不像"而不是"对不对"。用测试去锚定只会把当下的实现细节固化成标准
（"测试守的不是偶然实现，是用户需求"）。

所以 K1/K3 那些碰桌面的集成测试（真麦克风、真剪贴板、真记事本注入、自建 Win32
编辑控件）**已经删掉了**。**真实使用就是端到端验证** —— 用户是唯一用户，天天在用。

## 与 Python 版的用户可感知差异

| 分类 | 方面 | Python 版 | 本版 | 为什么 |
|---|---|---|---|---|
| 录音 | 麦克风设备号 | PortAudio 全局设备索引 | NAudio 采集端点序号 | NAudio 没有"全局索引"概念；旧 `input_device` 要重选一次 |
| 录音 | `max_duration` | 死配置（录音可以无限长） | 到点自动停 + `recording_timeout` 通知 | Python 那边是漏实现 |
| 识别 | 本地后端 `unload` | 只释放显存，Python 进程留着 | 连子进程一起结束 | 进程外推理下"空壳 Python 进程"没有意义 |
| 识别 | 多声道混音越界 | 回绕（全幅 +32768 变成 -32768） | 饱和（clamp 到 ±32767/32768） | 回绕会产生负尖峰；差异只在"所有样本都是 int16 极值"这一个边界上 |
| 识别 | 词表文件被删后 | 旧词**继续生效**（看着像 bug） | 同 Python 版（未改） | 行为一致优先；要改得先改 Python 版 —— 那是另一个工程 |
| 注入 | 贴哪个窗口 | 注入时贴**当前**前台窗口 | 贴**开始录音时**那个窗口 | 焦点保护：说的这 1 秒里切了窗口，文字也不送错地方 |
| 注入 | 复制按钮 | 胶囊自己写剪贴板 | 走 `presenter.TryCopyResult()` | UI 不直接摸剪贴板（owner 窗口那条规矩不能漏给 UI 层） |
| 界面 | 窗口尺寸 | 固定 512×260 | 宽度固定 512，**高度跟着内容** | 内容驱动的高度不会让上方留白空着 |
| 界面 | 命中区 | 前端上报的**矩形** | **带圆角**的矩形（圆角抄 CSS） | 视觉与命中一致 |
| 界面 | 托盘图标 | PIL 画 | GDI 画（同样 64×64、同样的圆与麦克风） | Python 生态差别，外观一致 |
| 界面 | 球菜单 | 三项：设置 / 二维码扫描 / 退出 | 两项：设置 / 退出 | 二维码是 `companions/` 附属工具，这一轮不做 |
| 界面 | 拖动窗口 | JS 量位移后 IPC 回 Python | 系统原生 `BeginMoveDrag` | 都比"自己算"稳；原生拖动带虚线框 |
| 界面 | 拖球换算 | 前端持续发绝对目标位置 | 按"按下锚点 + 位移"自己算 | 没有前端 ↔ Python 的桥；结果等价且不累积误差 |
| 面板 | 关闭后 | **常驻隐藏**，再开还是同一个前端实例 | **关闭即销毁，再开重建** | Python 版必须常驻只是因为 pywebview 的 `start()` 之后不能建窗 |
| 面板 | 提示语颜色 | `.notice` 只有一个颜色 | **按级别配色**（错误红 / 警告琥珀 / 常规灰） | "切后端失败"和"正在切换"不该同色 |
| 面板 | 默认摆位 | 整块屏幕的右下角 | **工作区**右下角 | 任务栏在下面时面板不会被遮住 |
| 面板 | 窗口高度 | 量内容高度再 resize | 固定 320×340 | 面板内容是固定布局，装得下 |
| 面板 | 选项行空隙 | 平摊到每一条间隔 | 多出来的空隙落在"最后一个选项行 ↔ 页脚"之间 | `StackPanel` 没有 `space-between`；差几像素，肉眼几乎看不出 |

## `archive/` 是什么

`archive/` 是 2026-11-23 的**可行性验证壳**（已冻结）：它证明
"C# + Avalonia + NativeAOT 能同时做到透明 / 不抢焦点 / 点击穿透 / 拖拽 /
右键菜单 / 动画"。

- **不是设计参考**：里面 demo 的尺寸 / 色值 / 动画参数全是验证时的占位
  （球 96px、胶囊 320px、accent `#3B6EF6`），与真实设计无关。
  **视觉参数只认 `capsule.css` / `panel.css`。**
- **可以继承的是结论和"意外发现"**：Avalonia 11 的坑、内存实测、冷启动数据 ——
  见 [`archive/docs/feasibility.md`](archive/docs/feasibility.md)，
  以及本仓库 [`AGENTS.md`](AGENTS.md) 的「Avalonia 11 的坑」一节。
- 复现方法见 [`archive/README.md`](archive/README.md)（在 `archive/` 目录下跑，
  它有独立的 `Feasibility.sln`）。

## 构建

```
dotnet build VmNext.sln -c Release
dotnet publish src/VmNext.App -c Release -r win-x64
```

> **关于 NativeAOT**：csproj 里已经打开了 AOT 友好的编译期绑定
> （`AvaloniaUseCompiledBindingsByDefault`），但**整个 App 的 AOT 发布尚未验证**。
> 上面那组内存数字（AOT ≈ 80 MB / JIT ≈ 117 MB）来自 `archive/` 里
> **只有窗口层**的验证壳，不是完整程序。
