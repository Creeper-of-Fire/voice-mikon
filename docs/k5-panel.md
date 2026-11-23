# K5 —— 设置面板：PanelPresenter + PanelWindow

> 本文是 **K5 阶段的过程记录**，现状以代码和 [`AGENTS.md`](../AGENTS.md) 为准。

> **接口依据**：[`docs/core-api.md`](./core-api.md)（事件名 / 线程模型 / 退出顺序）。
> **翻译源**：`voice-mikon/src/voice_mikon/apps/panel/presenter.py`（381 行）、
> `host/panel.py`（616 行）、`host/static/panel.{html,css,js}`（110 / 505 / 565 行）。
> **原则**：翻译不是设计 —— 尺寸、色值、间距、圆角、字体，一个数不改。

---

## 0. 这一版做了什么

| 项 | 结果 |
|---|---|
| 新增 `src/VmNext.Core/Presentation/PanelPresenter.cs` | `PanelSnapshot` 记录 + `PanelPresenter`（core 快照 / 通知 → 面板快照；动作方法调 core / config 并 `Save`） |
| 新增 `src/VmNext.Core/Presentation/PanelContracts.cs` | `IPanelCore` / `IPanelConfig` 两个**窄接口**（让 presenter 能被假实现驱动） |
| 修改 `src/VmNext.Core/Config.cs` | 实现 `IPanelConfig`（**显式** `Save()`，因为 `Save(string?)` 带可选参数，签名上不满足无参接口） |
| 修改 `src/VmNext.Core/Controller/CoreController.cs` | 实现 `IPanelCore`（四个成员本来就有，只是补个接口名，**行为不变**） |
| 新增 `src/VmNext.Ui/PanelWindow.axaml(.cs)` | 面板窗口：无边框 + 自定义标题栏 + 原生拖动 + 位置记忆；控件树照搬 `panel.html` |
| 新增 `src/VmNext.Ui/PanelTheme.axaml` / `PanelTheme.cs` | 面板的 palette（21 个 token，两张表）+ 三个控件模板 |
| 新增 `src/VmNext.Ui/PanelGeometry.cs` | 位置记忆的编解码（**纯函数**，格式与 Python 版逐字兼容） |
| 修改 `src/VmNext.App/Gui/GuiHost.cs` | 托盘 / 球菜单的「设置」→ 打开面板；面板的主题 / 界面形态推给胶囊；面板的退出走 K4b 的 `RequestQuit` |
| 修改 `src/VmNext.App/Gui/App.axaml`、`tests/VmNext.Ui.Tests/TestApp.axaml` | 引入 `PanelTheme.axaml` |
| 新增 `tests/VmNext.Core.Tests/PanelPresenterTests.cs` | presenter 单元测试 **19 条** |
| 新增 `tests/VmNext.Ui.Tests/PanelWindowTests.cs` / `PanelGeometryTests.cs` | 面板显示状态 **15 条** + 位置记忆 **12 条** |

---

## 1. 面板 presenter（翻译层）

### 目的

面板是**配置窗口**：只看状态、切模型 / 后端、改主题 / 界面形态 / 预览 / 归还、退出。
录音过程的反馈（实时预览、电平条、计时）全在胶囊那边。

### 照搬了什么

- **`presenter.py` 的全部职责**（逐条对应）：
  - 订阅 core 的 `AppState` 快照（**订阅即投递** —— 装配期状态可能已经不是初始值了，
    漏掉这一次的话面板得一直等到下一次变化才知道自己身处什么状态）；
  - 把状态 / 后端健康 / 模型 / 后端 / 主题 / 预览 / 界面形态 / 热键 / 归还翻译成**一份快照**；
  - 订阅通知流，把**面板发起的操作**（切模型 / 切后端）的进展、拒绝、失败翻译成提示语；
  - 承接前端动作：切模型、切后端、改配置、退出、关面板。
- **`push_full_state` 推的字段一个不漏**：`state` / `health.status` / `health.reason` /
  `model` / `backend` / `theme` / `show_preview` / `ui_mode` / `hotkey` /
  `restore_clipboard_enabled` —— 全部落在 `PanelSnapshot` 上。
  另外补了 `NetworkModel`（网络后端下的远端模型名；本地后端为空串）与
  `NoticeLevel`（提示语级别，见 §7 疑问 #1）。
- **`_NOTICE_KINDS` 的六种通知**（`model_switching` / `model_switched` /
  `model_switch_failed` / `backend_switching` / `backend_switched` /
  `backend_switch_failed`）：别的通知（录音失败、注入失败…）归胶囊的 presenter，
  面板不重复显示。
- **`_push` 的去重账本**：同值不重复推（空推会白刷前端）。
- **动作方法的语义**：`set_theme` 认不出的档位按 `auto` 处理；
  `set_ui_mode` 忽略非法档与 `headless`（面板上那一档是 disabled）；
  切模型 / 切后端**不返回结果** —— 拒绝与失败只走通知流（这是 Python 版模块说明里点名的坑）。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| `_PanelApi` 单独一个类，只留方法、引用用下划线开头 | pywebview 的 `get_functions` 会遍历公开属性、遇到非 callable 的对象还会**递归**下去 —— 直接把 presenter 交出去，内部属性都会被暴露到网页里 | C# 没有那个坑：动作方法直接挂在 `PanelPresenter` 上（`SwitchBackend` / `SwitchModel` / `SetTheme` / `SetUiMode` / `SetPreview` / `SetRestoreClipboard` / `QuitApp` / `ClosePanel`） |
| `core: Any` / `config: Any`（鸭子类型 + `getattr` 兜底） | Python 没有接口，只能这么写 | 两个**窄接口** `IPanelCore` / `IPanelConfig`（仓库已有先例：`IRecorder` / `IDrainableRecorder`）。好处：presenter 能被**假实现**驱动，单元测试不必造控制器、加载模型、碰剪贴板 |
| `theme_broadcaster` / `ui_mode_broadcaster` / `quit_handler` 三个注入的回调 | 那是 Python 的"注入函数"写法 | 三个事件：`ThemeChanged` / `UiModeChanged` / `QuitRequested`（+ `CloseRequested`）。推给谁由装配层决定 —— 本层不认识胶囊、不认识窗口 |
| `begin_drag` / `move_window` / `end_drag` 三个转发方法 | 那是 JS → Python 的拖拽桥接 | 面板窗口用 Avalonia 原生的 `BeginMoveDrag`（见 §2） |

### 测试结果

`PanelPresenterTests` **19 条全过**（`tests/VmNext.Core.Tests/`）：

- 装配：安装即收到当前快照（订阅即投递）；同值不重复推；收尾后不再推。
- 动作：`SwitchBackend` / `SwitchModel` 调到了 core 的正确下游；
  `SetTheme` / `SetPreview` / `SetRestoreClipboard` / `SetUiMode` 改了配置**并且调了 `Save`**；
  未知主题档位按 `auto` 处理；`headless` 档被忽略（配置没动、没落盘、没广播）；
  `QuitApp` / `ClosePanel` 各抛自己的事件。
- 快照：core 快照变化 → 新快照的字段正确；健康状态 + 原因进快照；
  网络后端 → `NetworkModel` = 远端模型名、本地后端 → 空串。
- 提示语：六种 kind 变提示语（带级别）；别的通知不当提示语；`ClearNotice` 归零。

---

## 2. 面板窗口（视觉照搬）

### 目的

把 `panel.html` 的控件树与 `panel.css` 的视觉**逐条**搬到 Avalonia 上：
用户看到的面板跟 Python 版一样。

### 照搬了什么

**控件树**（`panel.html` → XAML，一个不少）：

| Python | Avalonia |
|---|---|
| `.titlebar` / `#stateDot` / `.title` / `#closeBtn` | `TitleBar` / `StateDot` + `StateDotHalo` / 标题 / `CloseButton` |
| `.status-row` / `#stateText` / `#healthBadge` / `#healthReason` | 状态行 / `StateText` / `HealthBadge` + `HealthBadgeText` / `HealthReason` |
| `#backendSeg`（local / minimax） | `BackendSeg` + `BackendLocalItem` / `BackendMinimaxItem` |
| `#modelRow` + `#modelSeg`（0.6B / 1.7B）/ `#netModelText` | `ModelRow` + `ModelSeg`（`Model06Item` / `Model17Item`）/ `NetModelText` |
| `#themeSeg`（自动 / 白天 / 黑夜） | `ThemeSeg` + 三个 `RadioButton` |
| `#uiModeSeg`（悬浮球 / 常驻 / 按需 / 无界面 disabled） | `UiModeSeg` + 四个 `RadioButton`（「无界面」`IsEnabled=False`） |
| `#previewSwitch` / `#restoreClipboardSwitch` | `PreviewSwitch` / `RestoreClipboardSwitch`（`ToggleSwitch`） |
| `#notice` / `#hotkeyLabel` / `#quitBtn` | `Notice` / `HotkeyLabel`（`kbd` 样式）/ `QuitButton` |

**尺寸 / 色值 / 间距 / 圆角 / 字体**（`panel.css` → XAML，逐个注了行号）：

- 窗口 **320×340**（`host/panel.py` 的 `BASE_W` / `BASE_H`）；
- `.panel`：`margin 6px` / `border-radius 16px` / `1px solid var(--line)` / 卡片底色 / 阴影；
- `.titlebar`：`height 32px` / `padding 0 8px 0 14px` / 底部 1px 分隔线 / `gap 8px`；
- `.dot`：8px 正圆 + 3px 光晕圈（用一圈更大的圆底衬出来，与 `box-shadow: 0 0 0 3px` 等价）；
- `.title`：`14px` / `700` / `letter-spacing -0.2px`；
- `.icon-btn`：26×26 / `border-radius 9px` / hover 才出底色；
- `.row-label`：`12px` / `var(--muted)`；
- `.seg`：`padding 3px` / `gap 2px` / `border-radius 11px` / 轨道底 `var(--nav)`；
- `.seg-item`：`min-height 26px` / `padding 0 12px` / `border-radius 9px` / `12px` /
  等宽字体（`.seg.is-text` 那一段用正文字体）；选中项：卡片底 + `600` + 阴影；
- `.health-badge`：`padding 2px 8px` / `border-radius 999px` / `11px`；三态各有底色与字色；
- `.health-reason`：`11px` / 危险色 / 常驻显示；
- `.notice`：`11px` / `line-height 1.4`；空的时候不占位置；
- `.hotkey kbd`：`padding 3px 8px` / `border-radius 7px` / 底部一条同色投影 / `12px` / `500`；
- `.link-btn`：`padding 4px 0` / 无框无底色 / hover 才变深；
- 字体栈：`--font` → `Segoe UI, Microsoft YaHei`；`--font-mono` → `Cascadia Mono, Consolas`。

**交互**：

- 标题栏按下 → 拖动窗口（Avalonia 原生 `BeginMoveDrag`）；点在按钮上不触发拖动。
- 分段控件 = `RadioButton` 组（`GroupName` 互斥）；开关 = `ToggleSwitch`。
- 提示语 8 秒后自己淡掉（`panel.js` 的 `NOTICE_MS = 8000`）。
- 窗口可激活（`SystemDecorations=None` + 不进任务栏）—— 与胶囊**相反**，面板就是要拿焦点做交互的。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| `begin_drag` / `move_window` / `end_drag` 三个桥接方法 + JS 量位移 | 那是 WebView2 的 workaround（JS 量屏幕坐标 → IPC → Python `SetWindowPos`） | Avalonia 原生 `BeginMoveDrag`（系统拖动），一行事件 |
| 面板**必须常驻**（`hidden=True` 建好，托盘才 `show()`） | pywebview 的 `start()` 之后不能建窗 —— 那是库的限制 | **按需创建、关闭即销毁**（见 §6） |
| `_call_js` 的串行推送队列 + `_frontend_ready` 门控 | WebView2 是 STA COM 对象，多线程并发 `evaluate_js` 会互相踩掉 | Avalonia 的事件直接投递；UI 侧 `Dispatcher.UIThread.Post`（core-api.md §3 的规矩） |
| `_fit_window` 量 `scrollHeight` 再 resize | pywebview 建窗尺寸会被 WinForms 的 AutoScale 再缩一次 | 固定 320×340 —— 面板内容是固定布局，装得下 |
| `evaluate_js` 在 GUI 没建好时阻塞 20 秒 | 同上 | 没有 `evaluate_js` 这回事 |
| `set_state` / `set_health` / … 一串具名 setter | 那是三层名字链（presenter → host → 前端 `window.__panel.setState`），中间那层就是"改名不驼峰"的历史包袱 | 一份 `PanelSnapshot` 记录 —— 名字一次到位，没有中间层 |
| HTML 的 `hidden` 属性被作者样式的 `display` 架空 | HTML 的坑（AGENTS.md 里点名过） | Avalonia 的 `IsVisible=false` 直接不占布局 |
| `data-theme` 属性 + `prefers-color-scheme` 媒体查询兜底 | 浏览器机制 | `PanelTheme` 三档 + `PlatformSettings.ColorValuesChanged` 监听（与胶囊那套同款机制） |
| `.content { justify-content: space-between }` | Avalonia 的 `StackPanel` 没有 space-between（只有固定 `Spacing`） | 用 `Grid`（选项区 `*` + 页脚 `Auto`）把页脚压到底部。**后果**：多出来的空隙全落在"最后一个选项行 ↔ 页脚"之间，Python 版是平摊到每一条间隔里（见 §6 差异 #5） |
| 关闭 / 退出走 `_PanelApi` 的桥接 | 那是 JS → Python 的桥 | 窗口抛事件，装配层接回 presenter |

### 测试结果

`PanelWindowTests` **15 条全过**（`tests/VmNext.Ui.Tests/`，`Avalonia.Headless`）：

- 后端分段：本地 → 选中"本地"；网络 → 选中"MiniMax"。
- 模型行：本地 → 显示 0.6B/1.7B 档位、藏起远端模型名；网络 → 反过来，远端模型名 = `asr-1.0`。
- 主题 / 界面形态：选中项正确；「无界面」档是禁用的。
- 两个开关：预览与归还原样跟着快照走。
- 状态点：待机绿 / 录音红 / 准备中黄 / 识别中蓝（点与光晕圈都断言了颜色）。
- 状态文字：待机 / 准备中 / 录音中 / 识别中。
- 健康徽章：三态文案与配色；失败原因常驻显示。
- 提示语：空时不占位置；按级别配色（常规 / 警告 / 错误）。
- 热键：显示按键组合；去掉"热键 "前缀；空的时候显示"未设置"。
- 用户动作：点分段 / 开关 → 抛对应事件；关闭钮 / 退出钮 → 抛事件。
- **套用快照不触发用户动作事件**（否则会形成"推快照 → 用户动作 → 再推"的回环）。

`PanelGeometryTests` **12 条全过**：`"x,y,w,h"` 的编解码、往返、
与 Python 版字符串逐字相同、认不出来就返回 false（空串 / 段数不对 / 不是整数 / 带小数）。

**不写自动化测试**：窗口真机显示、拖拽手感、焦点行为 —— 任务书明确要求由用户真机验证。

---

## 3. 主题与配色

### 目的

面板与胶囊**共用一个主题档位**（`config.theme`），但**各有一套配色**
（`panel.css` 自己有一套 token，与 `capsule.css` 不同源）。

### 照搬了什么

- **`panel.css` 的 21 个颜色 token，一字不改**，两张 palette：
  - 浅色 = `:root`（L40-82）：`--bg #fff` / `--card #fff` / `--nav #f4f4f5` /
    `--hover #ececee` / `--text #111113` / `--text2 #3f3f46` / `--muted #8b8b93` /
    `--line #ececee` / `--accent #111113` / `--danger #e5484d` /
    `--danger-soft #fdeeee` / `--warn #b7791f` / `--warn-soft #fdf5e6` /
    `--k-ask #e8f7ee` / `--k-ask-b #1d7a45` / `--k-trans #eaf0ff` /
    `--k-trans-b #2b4fbf` + 四个光晕色（直接引用 soft 系 token）。
  - 深色 = `:root[data-theme="dark"]`（L120-140），与
    `@media (prefers-color-scheme: dark)` 那一块（L95-117）**逐值一致**
    （CSS 注释明确要求"改一处就要改另一处"）。
- **阴影**：`--shadow` 两层（`0 1px 2px` + `0 6px 24px`），浅深两套。

### 没搬什么及替代写法

- **不走 Avalonia 的 `ThemeDictionaries`**：它是**替换 brush 实例**，切主题会瞬间跳色；
  而 CSS 的 `transition: background .15s` 要求平滑过渡。
  → 与胶囊同款：`OverlayTheme.TweenBrush` 就地改 `brush.Color`（实例不变，动画才有得补）。
- **阴影不走补间**：`BoxShadows` 是结构体不是 brush，补间不了 → 切主题时直接换。
- **`--font` / `--font-mono` 不是颜色**：落到 XAML 的 `FontFamily`
  （`--font` → `Segoe UI, Microsoft YaHei`；`--font-mono` → `Cascadia Mono, Consolas`）。
- **`--shadow` 不是颜色**：单独存成 `PanelTheme.CurrentShadow`（`BoxShadows`）。

### 测试结果

- 面板的配色由 `PanelWindowTests` 的颜色断言覆盖（状态点 / 光晕圈 / 健康徽章 / 提示语），
  断言值直接取自 `PanelTheme.DarkPalette`。
- **色值逐个核对过**：把 `panel.css` 的 `:root` 与 `:root[data-theme="dark"]` 两块解析出来，
  解析 `var()` 引用与 3 位缩写后，与 `PanelTheme.LightPalette` / `DarkPalette`
  **21 个 token 逐个一致**（只有 `--font` / `--font-mono` / `--shadow` 三个非颜色项不在 palette 里）。
  阴影的 alpha 也按 CSS 规则换算核对过：`0.04 → 0x0A`、`0.06 → 0x0F`、
  `0.4 → 0x66`、`0.3 → 0x4D`。
- ⚠ **但这个核对没有变成自动化测试**：`tests/VmNext.TokenCheck` 只核对 `capsule.css`
  的三块 token。面板的 CSS 结构不同（`:root` 是浅色、深色在 `[data-theme]` 与媒体查询两处），
  要复用得先把 TokenCheck 改成"多一份 CSS + 多一张表"的通用形态。
  这是本版的一个**已知缺口**，见 §7 疑问 #3。

---

## 4. 位置记忆

### 目的

面板拖到哪，下次打开还在哪。

### 照搬了什么

- **`config.panel_geometry` 的格式逐字兼容**：`"x,y,w,h"`，四个整数，
  单位是**逻辑像素**（物理像素 ÷ 缩放系数）—— 换一台缩放率不同的显示器时位置还能落在合理的地方。
  用户拿旧的 `data/config.json` 直接能用。
- **读写时机**：关面板 / 关程序时写（用户可能拖完直接关）。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| `DragSession`（按下 → 拖 → 松手的状态机）+ 焦点冻结 | 焦点冻结是 pywebview 窗口**可能激活**的补丁（面板要拿焦点，本来就不冻结） | 系统原生拖动，不需要会话 |
| `_sync_position_from_window`（读 `GetWindowRect` 的真实位置） | 那是 pywebview 内部换算导致记账脱钩的补丁 | Avalonia 的 `Window.Position` 就是真实位置，直接换算成逻辑像素 |
| `_place_default` 用 `GetSystemMetrics`（**整块屏幕**） | 会把面板摆到任务栏底下 | 用**工作区**右下角（`Screens.Primary.WorkingArea`），少 30 / 60 的留白与 Python 一致 |

---

## 5. 接线（`VmNext.App/Gui/GuiHost.cs`）

```
托盘「设置」 / 球菜单「设置」
        └─► GuiHost.OpenPanel()
                ├─ new PanelWindow()                    （按需创建）
                ├─ PanelPresenter.SnapshotChanged ──► window.ApplySnapshot()
                ├─ window.BackendChanged ──► presenter.SwitchBackend()
                ├─ window.ThemeChanged ────► presenter.SetTheme()
                │        └─ presenter.ThemeChanged ──► 胶囊 SetTheme + PanelTheme.Apply
                ├─ window.UiModeChanged ───► presenter.SetUiMode()
                │        └─ presenter.UiModeChanged ──► VoiceBarPresenter.ApplyUiMode（收球 / 露球）
                ├─ window.QuitRequested ───► presenter.QuitApp()
                │        └─ presenter.QuitRequested ──► GuiHost.RequestQuit()（K4b 留的回调位）
                └─ window.CloseRequested ──► presenter.ClosePanel()
                         └─ presenter.CloseRequested ──► GuiHost.ClosePanel()（关窗口，不退程序）
```

- **presenter 常驻，窗口按需**：presenter 在 `GuiHost` 构造时建好并 `Install()`
  （core / 通知的订阅只订一次）；窗口每次打开新建、关闭销毁。
- **主题 / 界面形态要推给胶囊**：Python 版是 `theme_broadcaster=capsule.set_theme` /
  `ui_mode_broadcaster=presenter.apply_ui_mode`，这里对应 `ThemeChanged` / `UiModeChanged`
  两个事件，装配层接到 `CapsuleWindow.SetTheme` 与 `VoiceBarPresenter.ApplyUiMode`。
- **退出的三条路汇到同一条**：托盘 / 球菜单 / 面板按钮都走 `GuiHost.RequestQuit()`
  （K4b 已有的收尾顺序：停推送 → 销毁窗口 → 删托盘图标 → 注销热键）。
- **位置记忆**：`GeometryProvider` / `GeometrySaver` 两个委托 —— UI 层不直接碰 `Config`
  （与胶囊的 `PositionHintProvider` / `PositionSaver` 同款做法）。

---

## 6. 与 Python 版的行为差异（用户可感知的）

1. **面板关闭即销毁，再开重建**（Python 版：常驻隐藏）。
   表现：关掉面板再打开，得到的是一个**全新窗口**（位置、状态都重新从 config / 快照来）；
   Python 版那个前端实例一直在，开面板是把它"显示出来"。
   原因：Python 版必须常驻只是因为 pywebview 的 `start()` 之后不能建窗 —— 那是库的限制，不该搬。
2. **提示语按级别配色**（错误红 / 警告琥珀 / 常规灰）。Python 版的 `.notice` 只有一个颜色。
3. **默认摆位用"工作区"右下角**（Python 版用整块屏幕的右下角）。
   表现：Windows 任务栏在下面时，面板不会被任务栏遮住。
4. **窗口固定 320×340，不会随内容长高**。Python 版会量内容高度再 resize
   （`panel.css` 的 `.panel` 用的是 `min-height`）。面板内容是固定布局，实际装得下。
5. **选项行之间的空隙分布不同**：Python 版的 `.content` 是 `justify-content: space-between`
   （多出来的空隙平摊到每一条间隔里）；Avalonia 的 `StackPanel` 只有固定 `Spacing`，
   这里多出来的空隙落在"最后一个选项行 ↔ 页脚"之间。差几像素，肉眼几乎看不出来。
6. **拖动窗口的手感由系统决定**（原生 `BeginMoveDrag`），Python 版是 JS 量位移后
   IPC 回 Python 再 `SetWindowPos`。两者都比"自己算"稳，但 Windows 的原生拖动带虚线框。

---

## 7. 疑问清单

> 每条：**目的 / 我的处理 / 为什么**。

### 疑问 #1（最重要）：提示语要不要按级别配色

- **状态**：**仍然成立**。`PanelSnapshot.NoticeLevel` 三档（info / warning / error），UI 层只管上色。
- **目的**：任务书明确要求"`notice` 按 level 配色"（测试项里也有）。
- **我的处理**：`PanelSnapshot` 多带一个 `NoticeLevel`（`info` / `warning` / `error`），
  面板按它给提示语上色：error → `--danger`、warning → `--warn`、info → `--text2`。
- **为什么**：Python 版的 `.notice` 只有 `--text2` 一个颜色，但"切后端失败"和"正在切换"
  用同一个颜色，用户分不出轻重。级别是通知本身带的（`Notification.Level`），
  翻译层顺手带过去，UI 层只管上色 —— 跟"显示状态的唯一出口"是同一条原则。
  **代价**：与 Python 版有一处可见差异（见 §6 差异 #2）。

### 疑问 #2：提示语的生命周期归谁管

- **状态**：**仍然成立**。窗口起 8 秒计时器 → 淡掉后抛 `NoticeExpired` → 装配层转给 presenter 的 `ClearNotice()`（事实归零）。
- **目的**：提示语 8 秒后自己淡掉（`panel.js` 的 `NOTICE_MS`）。
- **我的处理**：**两层各管一半** —— 窗口（怎么呈现）起 8 秒计时器，淡掉后抛 `NoticeExpired`；
  装配层转给 presenter 的 `ClearNotice()`（事实归零），快照里的提示语才真正清空。
- **为什么**：如果只在窗口里清，下次 core 推快照时提示语又回来了（快照里还带着它），
  用户会看到"已经消失的提示语又冒出来"。反之如果只在 presenter 里清，
  就没有人管"什么时候淡"——那是呈现的事，不该进翻译层。
  这也是为什么 `ClearNotice()` 是 presenter 上**多出来的一个方法**
  （`_PanelApi` 的动作清单里没有它）。

### 疑问 #3：面板的配色没有自动核对

- **状态**：**仍未解决（已知缺口）**。`tests/VmNext.TokenCheck` 仍只核对 `capsule.css`；面板的 21 个 token 只在 K5 手工核对过一遍，没有自动化测试守着。
- **目的**：`panel.css` 的 21 个 token 抄进 `PanelTheme`，但**没有自动化测试守着**。
- **我的处理**：本版**手工核对过一遍**（把 CSS 两块解析出来、解析 `var()` 引用与 3 位缩写，
  与两张 palette 逐个比对，21 个 token 全一致；阴影 alpha 也按 CSS 规则换算核对过）。
  但核对是**一次性**的，没有变成测试。
- **为什么**：`tests/VmNext.TokenCheck` 是**胶囊专用**的（它硬编码了 `capsule.css` 的
  三块选择器与两张 palette 的键集合）。面板的 CSS 结构不同（`:root` 是浅色、
  深色在 `[data-theme]` 与媒体查询两处），要复用得先把 TokenCheck 改成
  "多一份 CSS + 多张表"的通用形态 —— 那是 K5 之外的工作量。
  **这是已知缺口**：以后有人改 `panel.css` 而忘了改 `PanelTheme`，不会被自动发现。
  （胶囊那边有 TokenCheck 兜着，面板这边没有。）

### 疑问 #4：开关的圆钮位移交给 Avalonia

- **状态**：**仍然成立**。圆钮位移交给 Avalonia 的 `KnobTransitions`，我们只把尺寸摆对。
- **目的**：CSS 的开关是"38×22 的轨道 + 16px 的圆钮，选中后圆钮 `translateX(16px)`"。
- **我的处理**：用 `ToggleSwitch`，但**自己写模板**（Avalonia 的 `ToggleSwitch` 有两个
  **必需**的模板部件：`PART_MovingKnobs` / `PART_SwitchKnob`）。
  圆钮的位移**交给 Avalonia**：它把 `PART_MovingKnobs` 的 `Canvas.Left` 设成
  `PART_SwitchKnob` 的宽度 = 16px —— 正好是 CSS 的 `translateX(16px)`。
  轨道与圆钮之间那 2px 间隙用圆钮自己的 `Margin="2,2,0,0"` 补（CSS 的 `left:2px` 与垂直居中）。
- **为什么**：照搬 CSS 的数值，但**不自己实现**"圆钮怎么滑"——
  Avalonia 已经实现了（还带拖拽跟手），我们只把尺寸摆对。
  代价：过渡挂在 Avalonia 的 `KnobTransitions` 上（`Canvas.Left` 的 180ms 补间），
  与 CSS 的 `transition: transform .18s` 不是同一条机制（值一样，曲线不一定一样）。

### 疑问 #5：`panel_geometry` 的逻辑像素 ↔ Avalonia 的物理像素

- **状态**：**仍然成立**。`PanelGeometry` 只认 `"x,y,w,h"` 四个整数；逻辑像素 ↔ 物理像素的换算留在窗口里。
- **目的**：`config.panel_geometry` 的格式必须与 Python 版**逐字兼容**（旧配置直接能用）。
- **我的处理**：`PanelGeometry` 只认 `"x,y,w,h"` 四个整数（纯函数，可测）；
  逻辑像素 ↔ 物理像素的换算放在窗口里（`RenderScaling`），
  读用 `GeometryProvider`、写用 `GeometrySaver`（UI 层不直接碰 `Config`）。
- **为什么**：Python 版存的是逻辑像素（"换一台缩放率不同的显示器时位置还能落在合理的地方"），
  格式不能改。Avalonia 的 `Window.Position` 是**物理**像素，所以边界上要换算 ——
  换算属于"怎么摆"，留在 UI 层；格式属于"怎么存"，留在纯函数里。

---

## 8. 测试结果汇总

| 测试 | 结果 | 说明 |
|---|---|---|
| `VmNext.Ui.Tests` | **80 / 80 通过** | 原有 53 条（K4b）**全过** + 本轮新增 27 条（面板显示状态 15 + 位置记忆 12） |
| `VmNext.Core.Tests` | **33 / 33 通过** | 原有 14 条**全过** + 本轮新增 19 条（面板 presenter） |
| `VmNext.Audio.Tests` | 8 / 8 通过 | 未动 |
| `VmNext.Engines.Tests` | 78 通过 / 2 跳过 | 未动；跳过 2 条是环境限制（没有 `MINIMAX_API_KEY`） |
| `VmNext.TokenCheck` | ✓ 全部一致 | 未动（只核对 `capsule.css`） |
| `--headless --mock --timeout 60` | 退出码 **0** | 「[无头] 引擎就绪」 |
| `dotnet build VmNext.sln` | 0 警告 / 0 错误 | |

> **不碰桌面**：本轮的测试没有调用 `SendInput` / `SetForegroundWindow` / `OpenClipboard` /
> `RegisterHotKey` / `Shell_NotifyIcon` / `WasapiCapture`，也没有启动 GUI 子进程。
> 验证只用纯函数单元测试 + `Avalonia.Headless` + `dotnet build`。
