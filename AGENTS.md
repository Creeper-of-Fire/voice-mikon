# voice-mikon-next

[voice-mikon](../voice-mikon/)（Python + pywebview）的 **C# / Avalonia 11 / .NET 10 重写**。
Windows 全局语音输入：按热键说话 → 文字注入光标处。

功能已经做完了。本文件是**这个仓库的规矩** —— 每轮 AI 开工前先读它。
要改 UI 先看 [`docs/core-api.md`](docs/core-api.md)（presenter 契约）；
想知道某个设计当初为什么这么定，去 [`docs/`](docs/) 翻对应阶段的过程记录（k1–k5）。

portable 约定（运行时数据只进项目内的 `data/`）见上级目录 `../AGENTS.md`。

## 四条原则

1. **架构保留**：UI 只认 presenter 的快照 / display mode，不碰 controller；
   Win32 P/Invoke 只进 `Shell/NativeMethods.cs`；本地推理走 Python 子进程。
   （为什么：这些边界都是踩坑踩出来的，破坏它们要付的代价比重写还大。
   详见下面的「架构约束」。）
2. **视觉照搬**：UI 的尺寸 / 色值 / 动画参数来自
   `voice-mikon/src/voice_mikon/host/static/capsule.css` 与 `panel.css`，
   **一个数不改**。
   （为什么：那些 CSS 里每个值旁边都注着"为什么"，是用户天天在用的界面；
   自己另定一套就是凭感觉改用户的东西。拿不准的值去查 CSS，不是凭感觉写。）
3. **pywebview 的 workaround 不搬**：Python 版里"因为 pywebview / WebView2 限制才这么做"的，
   在 Avalonia 下用**最直接的方式**达成同样的目的。
   （为什么：那些 workaround 是为 WebView2 服务的，搬过来只会变成没用的复杂度。）
4. **不碰桌面**：测试不注入输入 / 不抢焦点 / 不写剪贴板 / 不注册热键 / 不弹窗 /
   不占麦克风。**只为有对错规范的逻辑写测试，不用测试锚定行为。**
   （为什么：用户是唯一用户，天天在用 —— 真实使用就是端到端验证。）

## 业务规矩

这些是从 `voice-mikon/AGENTS.md` 搬过来的**仍然适用**的部分 —— 每条都是用户拍板的。

- **识别前不加本地预判**：录到的音频**整段**丢给模型，不做静音门、时长门、
  音量门这类本地阈值判定 —— 该不该听、听成什么，由模型说。用户识别错了
  自己会删，不需要程序替他决定"这段算不算说话"。
  (踩过的坑:曾用"整段平均 RMS < silence_rms"判静音报「没有检测到声音」,
  静音占比越高越容易被误杀 —— 说得越久反而越容易中招;同一处还有
  "录音太短"那道门。两道门都已删除,别再加回来。)
- **预览层的 VAD（`VmNext.Engines/Vad/`）只用于断句**，不是判定用 ——
  它只影响实时预览的出字节奏，最终结果永远来自整段识别。它可以留着调。
- **剪贴板写入必须带 owner**（`Core/Interop/ClipboardService.cs`）：
  `OpenClipboard(NULL)` 写入的话，第三方剪贴板管理器(Ditto 一类)查不到
  来源，只能把识别文本标注成"用户正聚焦的那个应用" —— 带上我们自己的
  owner 窗口，来源标注才归位。别改回 `OpenClipboard(NULL)`。
  **owner 窗口必须在主线程创建**：它属于创建它的线程，线程退出窗口就没了，
  而 Windows 在 owner 窗口销毁时**连带清空剪贴板** —— 建在别的线程上会让
  "识别结果留在剪贴板"这个兜底直接失效，还会让后续 `OpenClipboard(死句柄)`
  全线失败。进程退出前要 `ReleaseOwnership()`（把所有权转给 NULL，内容留住）。
  C# 没有 `main_thread` 概念，但**规矩本身必须保留**：`ClipboardService`
  构造时记录 owner 线程，`EnsureOwnerWindow()` 只在 owner 线程建窗口，
  别的线程只复用（拿不到就返回 0 → 降级为无 owner，功能不受影响）。
  **"归还原内容"是有开关的**（面板「归还」/ `config.restore_clipboard`）：
  关掉后识别文本一直留在剪贴板里，而且**不再有还原那一次写入** —— 那次写入
  带着我们的 owner 窗口，剪贴板管理器会把"原内容"这条记录的来源标成我们，
  原本的来源(chrome/word…)就被冲掉了。
- **退出先停推送、再注销热键**：完整顺序见 [`docs/core-api.md`](docs/core-api.md) §1。
  先停"往界面推东西"（推给一个没人看的界面没有意义），再注销热键（全局资源！
  占着不放，"退出后马上重启"会注册失败）。
- **不静默回落**：本地权重缺失就明确报错指路（下载命令或切网络后端），
  不偷偷换后端。未知配置键 → warning + 忽略，不崩；已知键类型对不上 →
  warning + 用默认值顶上。退役的后端名 → 回退 + **一条** warning。
- **API key 不进 config**：`MINIMAX_API_KEY` 环境变量优先，`data/.env` 兜底；
  绝不进 `config.json`、绝不进日志（只报长度）。
- **portable 数据在项目内的 `data/`**：配置、日志、录音留存全在 `data/`
  （`Core/Paths.cs` 的 `Paths.DataDir`）。不要写 `%APPDATA%` /
  `%LOCALAPPDATA%` / 注册表 —— 那些地方删项目删不掉，`git status` 也看不见，
  状态跟代码脱节就复现不了。
  （可用的覆盖环境变量：`VOICE_MIKON_HOME` / `VOICE_MIKON_CONFIG` /
  `VOICE_MIKON_LOG`。）
- **词表以用户在 `data/hotwords.txt` 的编辑为准**，自动打分只是排序建议 ——
  这个分工是用户明确认可的，别改回"自动筛选"。
  词表形态还在演化（用户对多词表等有想法但需求未定），
  动手前先问用户，不要预设方案。
- **注入落点 = 识别完成时的前台窗口**：文字贴到**识别完成那一刻用户看着的窗口**，
  不是"开始录音时"那个窗口 —— 说一两分钟、中途干别的事，用户早就忘了开始时在哪个窗口，
  结束那一刻看着的才是意图所在。
  焦点保护保护的是"取前台 → 粘贴落地"这几百毫秒（这段里焦点又变了就还回去），
  **不是**识别那 1 秒 —— 识别期间切过去的窗口**会**成为落点。
  （这跟"点悬浮球那一下别抢焦点"是两件事：那条规矩在 UI 层，见 `UiBinder` 的 `GiveBackFocus`。）

## Avalonia 11 的坑（都踩过，别重踩）

- **XAML 里写 `Transition.Property="Width"` 会得到 null**，运行时
  `NullReferenceException`。要写全限定名（`Layoutable.Width`）或者干脆在代码里
  设过渡 —— 本项目统一在代码里设。
  （来源：`archive/docs/feasibility.md` 意外发现 #2）
- **自定义类不能叫 `Theme`**：Avalonia 11 给 `StyledElement` 加了同名实例属性
  `Theme`（类型 `ControlTheme`），在窗口子类里 `Theme.Toggle()` 会解析到那个
  属性上。我们的主题类叫 `OverlayTheme` / `CapsuleTheme` / `PanelTheme`。
  （来源：意外发现 #1）
- **`Avalonia.Rect` 的语义是 `(x, y, width, height)`**，不是
  `(left, top, right, bottom)`。按后者拼 `Rect`，球的命中区宽度会算成 276
  而不是 40。
- **`Padding` 的顺序是 左,上,右,下**（`Thickness`），**不能照抄 CSS 的两值简写**：
  CSS 的 `padding: 10px 8px` 是"上下 10、左右 8"，Avalonia 的 `Padding="10,8"`
  是"左 10、上 8"。要写四值 `8,10,8,10` 才对应。
- **`Window.Width` 要显式设**：只让内容撑宽（`SizeToContent.WidthAndHeight`）
  出来的是**物理**像素，175% 缩放下右边被裁掉一大截。宽度要同时设在
  `Window.Width` 和根控件的 `Width` 上，`SizeToContent` 只管高度。
- **验证工具必须声明 DPI 感知**：不声明的进程拿到的是**虚拟化坐标**
  （3840×2160 被当成 2194×1234），`GetClientRect`、屏幕抓取、"窗口位置对不对"
  会一起看错。要信 `PrintWindow`，不要信屏幕抓取。
  （来源：K4a §2 根因 C）
- **不激活的窗口收不到键盘事件**：`WS_EX_NOACTIVATE` 的窗口收不到键盘输入，
  所有按键都走全局热键（`RegisterHotKey`）。而且"不激活"与"文字可选中/可复制"
  互斥 —— 文字条按下时临时 `SetAllowActivation(true)`，松手切回。
  （来源：意外发现 #5、#6）
- **Avalonia 没实现 `WM_MOUSEACTIVATE`**：不抢焦点完全依赖 `WS_EX_NOACTIVATE`。
- **真透明靠自绘**：`TransparencyLevelHint=Transparent` +
  `SystemDecorations=None` + `Background=Transparent`；点击穿透靠 `SetWindowRgn`
  的窗口区域，**不是**靠透明像素。
- **形变每帧都要 resize 窗口 + 重裁区域**，是形变期唯一明显的掉帧来源。
- **内存**：AOT 工作集约 80 MB、JIT 约 117 MB（`archive/docs/feasibility.md`，
  测的是**只有窗口层**的验证壳，不是整个 App）。相对 Python 版的 300–500 MB
  降了 4–6 倍，但离 20–50 MB 的目标还有距离，差距主要来自 Avalonia 的 Skia
  渲染后端。

## 架构约束

### 依赖方向（别改）

```
VmNext.Engines    ← 不引用本仓库任何项目（引擎层的输入就是 short[] + 采样率）
VmNext.Shell      ← 不引用本仓库任何项目（窗口层 + 全部 Win32 P/Invoke）
VmNext.Core       → Shell + Engines
VmNext.Audio      → Core
VmNext.Ui         → Core + Shell
VmNext.App        → Core + Audio + Engines + Shell + Ui   ← 装配都在这一层
```

- **`Core` 只认 `IRecorder` / `IDrainableRecorder` 窄接口，不引用 `Audio`**：
  `Audio` 引用 `Core`（电平泵在 Core），反过来会成环，MSBuild 直接报错。
  适配在 `VmNext.Audio/RecorderAdapter.cs`。
- **UI 层不直接摸 `Config` / `ClipboardService` / `CoreController`**：
  配置读写走 `PanelPresenter` + `IPanelConfig`，剪贴板走
  `presenter.TryCopyResult()`，业务走 presenter 事件。
  （为什么：owner 窗口必须在主线程 —— 让 UI 直接摸剪贴板等于把那条规矩漏给 UI 层。）
- **Win32 P/Invoke 集中在 `src/VmNext.Shell/NativeMethods.cs`**：
  唯一例外是 `src/VmNext.App/Program.cs` 里的 `AttachConsole` / `GetStdHandle`
  （进程入口自己要的）。别的项目里出现 `[DllImport]` 就是走偏了。
- **本地推理走 Python 子进程**（`python/asr_server/`），C# 侧用命名管道通信。
  不要为了"少一个进程"把 torch 拉回主进程 —— 那样几百 MB 常驻内存就回来了。

### presenter 契约（改 UI 先看这个）

**[`docs/core-api.md`](docs/core-api.md) 是 UI 的唯一接口依据。**
不要去翻 `src/VmNext.Core/` 的实现来猜用法 —— 那是"怎么实现"，不是"怎么用"。

要点（细节看 core-api.md）：

- **显示状态的唯一出口是 `VoiceBarPresenter.DisplayModeChanged`**：
  mode 字符串 + `DisplayPayload`。通知**不要**再在 UI 里翻译一遍 kind ——
  同一件事解释两遍迟早对不上（K3 已经把 kind → mode 收进 presenter 了）。
- **dwell 表只有一份**：`DisplayProtocol.DwellSeconds`。终态停多久、到点回哪都查它；
  前端不再有自己的收回定时器。
- **UI 只"演"，不做"决策"**：停多久、到点回哪归 presenter；
  配色 / 尺寸 / 动画时长归 CSS；命中区 / 焦点 / 置顶 / 穿透归 Shell；
  业务（录音 / 识别 / 注入）归 Core。
- **UI 侧所有事件都要 `Dispatcher.UIThread.Post`**：事件在触发它的那条线程上发出。
- **电平绝不能按窗口内最大值归一化**：归一化之后电平条永远是满的，
  用户就没法从条的高低看出"我说话声音大不大"，那条条就只剩装饰作用。

## 文档

| 文件 | 内容 |
|---|---|
| [`docs/core-api.md`](docs/core-api.md) | **presenter 契约**：事件 / mode 枚举 / payload / dwell 表 / 动作 / 线程模型 |
| [`docs/k1-core-audio.md`](docs/k1-core-audio.md) | 核心层：基础设施 + Win32 互操作 + 音频层 |
| [`docs/k2-engines.md`](docs/k2-engines.md) | 识别引擎层 + 本地推理 Python IPC |
| [`docs/k3-controller.md`](docs/k3-controller.md) | 状态机 + 编排 + presenter + headless 入口 |
| [`docs/k4a-ui.md`](docs/k4a-ui.md) | 胶囊 UI 接上真实状态机 + 正式 GUI 入口 |
| [`docs/k4b-shell.md`](docs/k4b-shell.md) | 拖拽 / 摆位 / 位置记忆 / 托盘 / 退出重启 / 右键菜单 |
| [`docs/k5-panel.md`](docs/k5-panel.md) | 设置面板：PanelPresenter + PanelWindow |
| [`python/asr_server/README.md`](python/asr_server/README.md) | 本地推理子进程的 IPC 协议定稿 |
| [`archive/`](archive/) | 2026-11-23 的可行性验证（**冻结**，不是设计参考） |

k1–k5 是**过程记录**，写的是"这一阶段当时怎么做的、为什么" ——
现状以代码和本文件为准。
