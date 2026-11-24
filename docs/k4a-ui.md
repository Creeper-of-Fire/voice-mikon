# K4a —— 胶囊 UI 接上真实状态机 + 正式 GUI 入口 + 解决渲染问题

> 本文是 **K4a 阶段的过程记录**，现状以代码和 [`AGENTS.md`](../AGENTS.md) 为准。

> **接口依据**：[`docs/core-api.md`](./core-api.md)（事件名 / mode 枚举 / payload 字段 / dwell 表）。
> **翻译源**：`voice-mikon/src/voice_mikon/host/static/capsule.css`（样式与数值）、
> `capsule.js`（状态 class 组合与文案）、`host/capsule.py`（窗口显隐 / 焦点）、
> `host/placement.py`（几何常量）。
> **原则**：翻译不是设计 —— 尺寸、色值、动画参数全部来自上面这几份，每个值旁边注了出处。

---

## 1. 这一版做了什么

| 项 | 结果 |
|---|---|
| 新增 `src/VoiceMikonCSharp.Ui/` | classlib（引用 `VoiceMikonCSharp.Core` + `VoiceMikonCSharp.Shell`）：`CapsuleWindow`、`CapsuleTheme`、`CapsuleTheme.axaml`、`WaveformData`、`UiBinder` |
| `src/demos/Capsule.Demo/` | 改成引用 `VoiceMikonCSharp.Ui`，保留为**离线视觉预览**（热键直接推 mode，波形用模拟电平） |
| `src/VoiceMikonCSharp.App/` | 改成 **WinExe**：默认起 GUI；`--headless` 走 K3 的控制台路径 |
| `src/VoiceMikonCSharp.App/Gui/` | `App.axaml(.cs)`（Avalonia 应用 + palette）、`GuiHost.cs`（窗口 + 绑定层 + 界面形态） |
| `src/VoiceMikonCSharp.Shell/` | **只补不改**：`OverlayWindow.HitRegions`（自定义命中区）、`NativeMethods.CombineRgn`、`NativeMethods.SetForegroundWindow` |
| `tests/VoiceMikonCSharp.Ui.Tests/` | 新增（`Avalonia.Headless.XUnit`）：12 条 mode → 视觉状态断言 |
| `tests/VoiceMikonCSharp.App.Tests/` | K3 的 10 条全过 + 新增 1 条 GUI 子进程渲染测试（**该项目现已删除**） |
| `tests/VoiceMikonCSharp.TokenCheck/` | 改引用 `VoiceMikonCSharp.Ui`（palette 搬家），仍通过 |

---

## 2. 渲染问题：根因与修法

### 2.1 症状

上一个 worker 的记录是"窗口创建成功、位置尺寸都对，但屏幕上没有像素"。

### 2.2 根因（三个，叠在一起）

**根因 A（真 bug）：窗口宽度设错了，球的横向位置被挤到窗口边缘。**

旧 demo 的窗口宽度是 `OrbSize + 16 = 56` 逻辑像素，而**内容的最小组宽度是 112**
（文字条 `min-width:96px` + `.stack` 的左右 `padding:8px` ×2）。Avalonia 把 56 宽的窗口
按 112 宽的内容去排版 → 胶囊被顶到右边、竖着被切掉一截，屏幕上就只剩一小条。
实测（PrintWindow 的 ASCII）：球的红像素落在窗口的 `x=416..480`（物理），
而它应该在 `x=403..473`（居中）—— 偏了 13 物理像素，肉眼就是"球缺了一块"。

**根因 B（真 bug）：CSS 的 padding 简写被当成 Avalonia 的简写。**

`capsule.css` 的 `.stack { padding: 10px 8px }` 是"**上下 10、左右 8**"；
Avalonia 的 `Padding="10,8"` 是"**左 10、上 8、右 10、下 8**"（顺序是 左,上,右,下）。
两个都错。→ 改成 `Padding="8,10,8,10"`。

**根因 C（测量假象，把排查带偏了）：验证工具自己没声明 DPI 感知。**

本机主屏是 **3840×2160 @175%**。不声明 DPI 感知的进程拿到的是**虚拟化坐标**
（3840×2160 被当成 2194×1234）。于是：

* `GetClientRect` 返回的是虚拟化尺寸 → 以为窗口是 512×60，实际是 896×106；
* 屏幕抓取抓的是虚拟化桌面 → **抓的根本不是窗口所在的那块屏幕**；
* 顺带把"窗口位置对不对"也一起看错了。

用一个纯 Win32 的 GDI 窗口做对照实验（同样 60×60、红色刷）：

| 测量方式 | 工具 DPI **无**感知 | 工具 DPI **有**感知 |
|---|---|---|
| `PrintWindow` 中心像素 | 红 ✓ | 红 ✓ |
| 屏幕抓取中心像素 | 深灰（**没抓到窗口**） | 红 ✓ |

**=> 屏幕抓取这条路在本机靠不住；`PrintWindow` 才是可信的判据**（也正是任务书指定的判据）。

### 2.3 修法

| 修法 | 在哪 | 为什么 |
|---|---|---|
| 窗口宽度固定 **512 逻辑像素**（真机 `WIN_W = 512`） | `CapsuleWindow` 构造：`Width = WindowWidth` + `Root.Width = WindowWidth` | 球固定待在窗口**底边居中**（placement.py 的模块说明），宽度一变球就横向跑位 |
| 高度**跟着内容**走 | `SizeToContent = SizeToContent.Height` | 真机是固定 260（上方留白），见 §5 的差异表 |
| `Padding="8,10,8,10"` | `CapsuleWindow.axaml` | Avalonia 的顺序是 左,上,右,下 |
| **声明 DPI 感知**（在创建任何窗口之前） | `Program.Main` → `Dpi.SetDpiAwareness()`；demo 同样 | core-api.md §1 第 1 步就是它 |
| 命中区用"可见元素的矩形并集" | `OverlayWindow.HitRegions`（Shell 新增）+ `NativeMethods.CombineRgn` | 真机由前端上报矩形后 `SetWindowRgn`（capsule.js 的 `update_regions`）；Avalonia 没有"按元素裁窗口"的机制，这里补上等价能力 |

### 2.4 修完之后的实测（DPI 感知的测量工具）

```
--- hwnd=0x16A206E visible=True class='Avalonia-…' title='voice-mikon 胶囊'
    windowRect=(3329,1959)-(4225,2065) client=896x106 dpi=168
    exStyle=0x08280088 NOACTIVATE=True LAYERED=True TOOLWINDOW=True TOPMOST=True
    region = 类型=2（SIMPLEREGION → 命中区已裁）
    PrintWindow(PW_RENDERFULLCONTENT): 不透明像素=94976/94976 最大alpha=255
    红色像素包围盒: x=415..480 y=20..85 个数=3444（窗口 896x106）
```

* 客户区 896×106 物理 = **512×60.57 逻辑** ✓（= 512 宽 + 10 上下留白 + 40 球）
* 红球包围盒 `x=415..480` → 逻辑 `x=237..274`，**中心 256 = 512/2** ✓ 水平居中
* 红球下沿 `y=85` → 逻辑 `y=48.6`，**球底离窗口底 10px** ✓（placement.py 的 `ORB_INSET_Y`）
* 命中区已裁（`GetWindowRgn` 返回 SIMPLEREGION）✓ 点击穿透生效

---

## 3. 结构

### 3.1 `src/VoiceMikonCSharp.Ui/` 的职责

```
VoiceMikonCSharp.Ui/
├── CapsuleWindow.axaml(.cs)   胶囊窗口（继承 Shell 的 OverlayWindow）
├── CapsuleTheme.cs            主题档位 + 两张 palette（色值唯一真相在 CSS）
├── CapsuleTheme.axaml         palette 的 ResourceDictionary（正式版 / demo / 测试引同一份）
├── WaveformData.cs            滚动电平条的算法（capsule.js 的 mapHeight/nextBarHeight/smoothLevel）
└── UiBinder.cs                presenter ↔ 窗口 的双向绑定
```

**边界**：UI 层只"演"，不做"决策"。

* 什么时候收、停多久、到点回哪 —— presenter 的 dwell 表（`docs/core-api.md` §5）。
* 配色、尺寸、动画时长 —— `capsule.css` / `capsule.js`。
* 命中区、焦点、置顶、点击穿透 —— `Shell.OverlayWindow`。
* 业务（录音 / 识别 / 注入） —— `Core`。

### 3.2 事件 → 方法对照表（`UiBinder`，core-api.md §3）

| 来源（presenter） | 触发线程 | 窗口方法 | 备注 |
|---|---|---|---|
| `DisplayModeChanged(mode, payload)` | 状态变化那条线程 | `CapsuleWindow.SetMode(mode, payload)` | **显示状态的唯一出口**；对应 capsule.js 的 `applyMode` |
| `PreviewTextChanged(text)` | 预览线程 | `CapsuleWindow.SetPreviewText(text)` | 整段替换，不是追加 |
| `LevelsChanged(levels, elapsed)` | 电平泵线程（30fps） | `CapsuleWindow.PushLevels(levels, elapsed)` | 取窗口里**最新的一帧**喂平滑器，**不做窗口内归一化** |
| `ThemeChanged(theme)` | `Start()` 那条线程 | `CapsuleWindow.SetTheme(theme)` | auto / light / dark |
| `PreviewEnabledChanged(enabled)` | 同上 | `CapsuleWindow.SetPreviewEnabled(enabled)` | 对应 CSS 的 `.no-preview` |
| `OrbVisibilityChanged(visible)` | 同上 / `ApplyUiMode` | `CapsuleWindow.SetOrbVisible(visible)` | false = 收球 |
| `NotificationPosted` | 发通知那条线程 | **不直接绑** | 见 §7 疑问 6 |

所有回调都 `Dispatcher.UIThread.Post` 到 UI 线程（core-api.md §7）。

### 3.3 窗口的用户动作 → presenter（core-api.md §6）

| 用户动作 | 窗口事件 | presenter 的动作 |
|---|---|---|
| 点球 | `StartRecordingRequested` | `TryStartRecording()` → 失败且有 hint 就冒球上提示 |
| 点 ✓ | `StopRecordingRequested` | `TryStopRecording()` |
| 点 ✕（普通态） | `CancelRecordingRequested` | `TryCancelRecording()` |
| 点 ✕（带原文的错误常驻态） | `DismissRequested` | `OnDismissed()` |
| 点复制钮 | `CopyRequested` | `TryCopyResult()`（失败冒提示） |
| 菜单「退出」 | `QuitRequested` | → `AppHost.RequestQuit()` → 按 §1 的退出顺序收尾 |

---

## 4. mode → class 组合 → XAML 状态

mode 取自 `core-api.md` §4.1；class 组合取自 `capsule.js` 的 `applyMode` + `capsule.css` 的选择器。

| mode | CSS class 组合 | XAML 状态 | 文字条 | 胶囊阶段 |
|---|---|---|---|---|
| `orb` | `body.orb-mode.orb-idle` | `Pill.MaxWidth=40`；底色 `--wave`；子元素 `Opacity=0`；`TextBar.IsVisible=false` | 清空 | — |
| `starting` | `body.orb-loading.orb-idle` | 球上 loader 可见、麦克风隐藏（`SetOrbLoading(true)`） | 不动 | — |
| `expanding` | `body.orb-mode.orb-expanded` + `.recording` | `Pill.MaxWidth=480`；底色 `--deep` | 清空 | `recording` |
| `recording` | 同上 | 波形 + 计时可见、结束钮可见、状态图标隐藏 | 清空 | `recording` |
| `processing` | `.processing` | 状态图标=转圈（`StateLoaderIcon`）；label「识别中…」 | — | `processing` |
| `processing` + `Warn=true` | `.warning`（瞬态覆盖） | 胶囊琥珀；label「等你松键」 | 正文=`Body`（琥珀）+ 报告=`Message` | `warning` |
| `result_terminal` | `.done` | 状态图标=对勾 | 正文=`Text`（normal） | `done` |
| `error_terminal`（无原文） | `.error` | 胶囊底 `--err-bg`；状态图标=感叹号；**复制钮不出现** | 正文=`Message`（error） | `error` |
| `error_terminal` + `CanCopy` | `.error.can-copy` + `.warning` | 胶囊底 `--warn-bg`；**复制钮可见**、状态图标让位 | 正文=`Text`（warning）+ 报告=`Message` | `warning` |
| `cancelled_terminal` | `.cancelled` | 状态图标=转圈 | 正文=`Message`（cancelled，`--dim3`） | `cancelled` |
| `rejected_bounce` | 球上 `.orb-hint` | `OrbHint` 可见，文案=`Message` | 不动 | — |

界面形态（`config.ui_mode`）：

| ui_mode | 行为 |
|---|---|
| `orb` | 球常驻：待机是球形态，录音时展开；窗口一直可见 |
| `resident` | 无球：胶囊永远是展开态（CSS 里没有 `body.orb-mode.*`）；**待机时窗口整块藏起来**（对齐 Python 的 `_hide`） |
| `on_demand` | 运行期等同 `resident`（存意图、不存现实） |
| `headless` | 走控制台路径（`Program.Run` 里分流），不起 GUI |

---

## 5. 与 Python 版的 UI 行为差异

| # | Python（voice-mikon） | 本版 | 为什么 |
|---|---|---|---|
| 1 | 窗口**固定** 512×260（`capsule.py` 的 `WIN_W`/`WIN_H`） | 宽度固定 512，**高度跟着内容** | 内容驱动的高度不会让上方留白空着；摆位是 K4b |
| 2 | 命中区由前端 `update_regions` 上报（文字条/胶囊/浮层三块矩形） | `OverlayWindow.HitRegions`：UI 层把"可见元素"的矩形算出来填进去，Shell 合并成区域 | Avalonia 没有"按元素裁窗口"的机制，Shell 补了等价能力；真机量的是 CSS 盒，本版量的是布局盒，量得准略有差别 |
| 3 | 形状过渡靠 CSS `max-width`（内容 fit-content，宽度是"一点点长开"） | 同样是 `MaxWidth` 过渡（800ms，`cubic-bezier(.25,.85,.3,1)`） | 逐条照搬 CSS 的 transition |
| 4 | 复制：胶囊自己写剪贴板 + **读回校验**，失败留在原地 | 走 `presenter.TryCopyResult()`（K3 加的，疑问 #4） | UI 不直接摸剪贴板（core-api.md §6） |
| 5 | 文字条按下 → `focus_textbar`（送前台 + WebView2 焦点） | `SetAllowActivation(true)`，松手切回 | 同样的"按下临时激活"；不激活的窗口收不到键盘事件（archive 意外发现 #5） |
| 6 | 点球开始录音前把前台还给用户（`_start_recording_from_orb` 的焦点陷阱） | 记住前台，动作做完核一遍，焦点跑掉了就还回去 | 我们的窗口带 `WS_EX_NOACTIVATE` 本来就不抢焦点，但注入靠"贴前台窗口"，保险起见照做 |
| 7 | 主题用 `data-theme` 属性（CSS 媒体查询接管 auto） | 就地改 `brush.Color` + 补间（`OverlayTheme.TweenBrush`） | Avalonia 的 `ThemeDictionaries` 是**换 brush 实例**，切主题会瞬间跳色，而 CSS 要求 `transition: background-color .18s` |
| 8 | 预览文字由 Python 推 `setPreview` | presenter 的 `PreviewTextChanged` → `SetPreviewText` | 同一条线，只是把"推"放在了 presenter 后面 |
| 9 | 电平由 Python 30fps 推一串滑动窗口，前端取最新值 | `LevelsChanged`（30fps）→ `PushLevels` | 一致；两边都**不做窗口内归一化**（core-api.md §3 的警告） |
| 10 | 计时由 Python 推 `setElapsed` | 电平帧里的 `Elapsed` | 同一条数据源，省一路 IPC |
| 11 | 球的右键菜单 / 二维码 / 退出在**球形态**才有 | 同样（`OrbMenu`）；设置与二维码是**占位**（K4b） | CSS 的 `.orb-menu` 就在球上方 8px，本版把它放进纵向流（窗口高度自适应，放得下） |

---

## 6. 测试结果

| 项目 | 总数 | 通过 | 失败 | 跳过 |
|---|---|---|---|---|
| `VoiceMikonCSharp.Ui.Tests`（**新增**） | 12 | **12** | 0 | 0 |
| `VoiceMikonCSharp.App.Tests`（K3 + 新增 1 条 GUI；现已删除） | 11 | **10** | 0 | **1** |
| `VoiceMikonCSharp.Core.Tests`（未动） | 23 | 22 | 0 | 1 |
| `VoiceMikonCSharp.Audio.Tests`（未动） | 10 | 10 | 0 | 0 |
| `VoiceMikonCSharp.Engines.Tests`（未动） | 80 | 78 | 0 | 2 |
| `VoiceMikonCSharp.TokenCheck`（改引用） | — | ✓ 全部一致 | — | — |

跳过的 3 条与 K3 时一样，都是环境限制（剪贴板 1418 / `MINIMAX_API_KEY` 不存在）。

### 6.1 新增的两条关键测试

**`tests/VoiceMikonCSharp.Ui.Tests`**（无头）：按 capsule.css 的状态表逐个推 mode，断言元素属性，
每条断言旁注 CSS 行号。例如：

```csharp
// CSS L588-592: body.orb-mode.orb-idle .recording-pill { max-width:40px; background-color:var(--wave); }
Assert.Equal(40, window.Pill.MaxWidth);
Assert.Equal(CapsuleTheme.DarkPalette["--wave"], BackgroundOf(window.Pill.Background));
```

**`tests/VoiceMikonCSharp.App.Tests/GuiWindowTests`**（真子进程，**该项目现已删除**）：起 GUI → 等窗口 →
`PrintWindow` 统计非透明像素（> 500）与录音红像素（> 200）→ `WM_CLOSE` → 正常退出（退出码 0）。

### 6.2 命令行实测

```
dotnet run --project src/VoiceMikonCSharp.App -- --headless --mock --timeout 60
  → [无头] 引擎就绪
  → 退出码 0
```

GUI 形态：进程起来后窗口在右下角（`(3329,1959)-(4225,2065)`，客户区 896×106 物理），
红球画出来了（见 §2.4）。

---

## 7. 疑问清单

> 每条：**原文 / 我的处理 / 为什么**。

### 疑问 #1：CSS 的 padding 简写 ≠ Avalonia 的简写

- **状态**：**仍然成立**。`CapsuleWindow.axaml` 里写的是 `Padding="8,10,8,10"`（左 8、上 10、右 8、下 10）。
- **原文**：`capsule.css` 的 `.stack { padding: 10px 8px }`；Avalonia 的 `Padding` 是
  `Thickness`，两个值的写法是"左,上"（省略时右=左、下=上）。
- **我的处理**：XAML 里写 `Padding="8,10,8,10"`（左 8、上 10、右 8、下 10）。
- **为什么**：CSS 的两值简写是"上下 | 左右"，Avalonia 的是"左,上"——照抄 CSS 的
  `10,8` 会把上下左右整个转置，球的留白就不对了。

### 疑问 #2：窗口宽度该设在 Window 上还是内容上

- **状态**：**仍然成立**。宽度同时设在 `Window.Width` 与根控件的 `Width`（512 逻辑像素），`SizeToContent` 只管高度。
- **原文**：真机 `capsule.py` 的 `WIN_W = 512` 是**窗口**的宽度，内容在里面居中。
- **我的处理**：宽度同时设在 `Window.Width` 和 `Root.Width` 上（都是 512 逻辑像素），
  `SizeToContent` 只管高度。
- **为什么**：试过"只让内容撑宽"（`SizeToContent.WidthAndHeight` + `Root.Width`）——
  实测原生窗口出来的是 512 **物理**像素，而内容按 175% 渲染，右边被裁掉一大截。
  走 `Window.Width` 这条路，后端会按缩放换算（896 物理）✓。
  两处都设是为了保证"内容画布"和"窗口"一样宽，命中区才对得上。

### 疑问 #3：命中区怎么做到"只有球能点"

- **状态**：**已解决（K4b）**。K4b 删掉了 `OverlayWindow.HitRegions` 这个「外部填矩形」的公共属性，命中区算法全在 `CapsuleWindow.BuildHitRegions`；Shell 侧只保留 `protected virtual BuildHitRegions()` 挂点。
- **原文**：真机由前端 `update_regions` 上报文字条 / 胶囊 / 浮层的矩形，Python 侧
  `SetWindowRgn` 裁成并集；`capsule.js` 的注释特别强调"浮层要单独报，不能并进球的包围盒"。
- **我的处理**：Shell 加了 `OverlayWindow.HitRegions`（客户区坐标，物理像素）；
  UI 层在布局落定后把**可见元素**（胶囊 / 文字条 / 气泡 / 菜单）的矩形算出来填进去，
  Shell 用 `CombineRgn` 合并成一块区域。
- **为什么**：没有这一层，512 宽的窗口整条都能点，会把底下应用的点击全吃掉。
  这不是新设计，是把真机那条链路搬过来。

### 疑问 #4：文字条按下要临时激活，但激活了就抢焦点

- **状态**：**仍然成立**（K4b 又补了一条：选文本期间 `PointerReleased` 之前不切回不激活）。
- **原文**：`capsule.py` 的 `focus_textbar` —— 文字条被按下时把前台与键盘焦点送进去
  （不这样就选不了文本、Ctrl+C 不了）；真机还靠 `webview_tweaks` 额外处理 WebView2 的焦点。
- **我的处理**：文字条按下 → `SetAllowActivation(true)`；松手 → `SetAllowActivation(false)`。
- **为什么**：`WS_EX_NOACTIVATE` 的窗口收不到键盘事件（archive 意外发现 #5），
  不临时放开就复制不了；放开又不立刻关回去，就会把用户正在打的字抢走。

### 疑问 #5：点球开始录音的焦点陷阱

- **状态**：**仍然成立**。点球时记下当前前台 HWND，焦点跑掉了就 `SetForegroundWindow` 还回去。
- **原文**：`capsule.py` 的 `_start_recording_from_orb` —— 鼠标点在 WebView2 子窗口上会激活
  整棵窗口树，前台被胶囊抢走，注入就贴到胶囊自己身上。
- **我的处理**：点球时记下当前前台 HWND，`TryStartRecording()` 之后核一遍，焦点跑掉了就
  `SetForegroundWindow` 还回去。
- **为什么**：我们的窗口带 `WS_EX_NOACTIVATE`，理论上不会抢；但注入是"贴前台窗口"，
  万一焦点被别的原因带走了，这一轮就白录了 —— 照真机做一遍最省心。

### 疑问 #6：通知要不要直接绑到 UI

- **状态**：**仍然成立**。UI 只认 mode，不直接订阅 `NotificationPosted`。
- **原文**：`core-api.md` §3 列了 `NotificationHub.NotificationPosted`，但也说
  "显示状态只有一个出口（presenter 翻译后的 mode）"。
- **我的处理**：**不绑**。通知由 presenter 翻译成 mode（`error_terminal` /
  `rejected_bounce` / …）再推过来，UI 只认 mode。
- **为什么**：同一件事在 UI 里再解释一遍 kind，等于有两套翻译，迟早对不上
  （K3 已经把 kind → mode 的映射收进 presenter 了）。

### 疑问 #7：WinExe 下 `--headless` 的控制台输出

- **状态**：**仍然成立**。`Program.TryAttachParentConsole` 仍是「先看标准输出句柄可用不，可用就不附加」。
- **原文**：任务书要求"WinExe 下用 `AttachConsole(ATTACH_PARENT_PROCESS)` 附加到父控制台"。
- **我的处理**：先看 `GetStdHandle(STD_OUTPUT_HANDLE)` 是不是可用 —— **可用就不附加**，
  只有拿不到句柄（双击启动）才尝试附加。
- **为什么**：从 cmd / PowerShell 启动时子进程**已经继承**了控制台，输出照常可见；
  这时候再 `AttachConsole` 会把输出改道到父控制台，**重定向的管道就空了**
  （K3 的 `HeadlessCliTests` 正是靠管道读"引擎就绪"这几个字 —— 第一次改完就红了）。

### 疑问 #8：面板改主题 / 改界面形态之后，胶囊怎么跟上

- **状态**：**已解决（K5）**。面板已实现（`PanelPresenter` + `PanelWindow`），`ThemeChanged` / `UiModeChanged` 已接到胶囊与 presenter。
- **原文**：Python 的面板改完 `config` 会推给 presenter（`setTheme` / `hide_orb` / `show_orb`）。
- **我的处理**：`presenter.ThemeChanged` → `SetTheme`；`presenter.OrbVisibilityChanged` → `SetOrbVisible`；
  `presenter.ApplyUiMode(mode)` → 球的显隐。面板本身是 K4b。
- **为什么**：接口在 core-api.md 里已经定死了，UI 照着接就行，不需要额外发明通道。

### 疑问 #9（K1 的老问题，没改）：`Dpi.SetDpiAwareness()` 的退化分支

- **状态**：**仍未解决**。退化分支返回的档位字符串可能不准 —— 那只是日志里的字样，不影响实际生效的 DPI 模式。
- **原文**：`Core/Interop/Dpi.cs` 里 `SetProcessDpiAwareness(2) == 0` 被当成"成功"，
  但 MSDN 说它失败时返回的也是 0（`DPI_AWARENESS_INVALID`）。
- **我的处理**：**不改**（K1 的行为，K4a 不动）。主路径
  `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` 成功时不会走到这里；
  走到退化分支时，返回的档位字符串可能不准（只是日志里的字样，不影响实际生效的模式）。
- **为什么**：实际生效的 DPI 模式由 Windows 决定，返回字符串只是诊断信息；
  为了一个日志字样去动 K1 的代码不值当 —— 但记在这里，免得以后看到 "system" 误判。

---

## 8. 未完成项（下一步 K4b）

| 项 | 现在到哪一步 | 预留的接口 |
|---|---|---|
| 拖球挪位置 | **未做** | `OverlayWindow.MoveByLogicalPixels`（Shell 已有）+ `config.orb.geometry`（Config 已有） |
| 摆位档位 / 位置记忆 | 只做了"默认贴右下角" | `placement.py` 的 `default_orb_position` / `clamp_orb_position` / `orb_window_position` 未翻 |
| 托盘 | **未做** | 退出入口已有（菜单 → `AppHost.RequestQuit()`） |
| 面板 | **未做** | `Presenter.ApplyUiMode` / `OnDismissed` 已就绪，面板改配置后推给 presenter 即可 |
| 退出 / 重启 | 只做了"退出"（菜单） | `AppHost` 的退出顺序已按 core-api.md §1 排好 |
| 精确命中区 | 用的是"可见元素的矩形"，真机是前端上报的 CSS 盒 | `OverlayWindow.HitRegions` 可以直接换成前端上报的矩形 |
| 窗口固定高度 260 | 本版高度跟内容走 | `capsule.py` 的 `WIN_H` 未翻 |
| 球形态的 hover 底色 / 拖拽光标 | **未做** | CSS L609-611 / L666-671 |
| `prefers-reduced-motion` | **未做** | CSS L473 / L675-684 |

---

## 9. 给主代理的两条提示

1. **根 `VoiceMikonCSharp.sln` 本次没动**（里面的 `Capsule.Demo` / `VoiceMikonCSharp.TokenCheck` 是上一版加的）：
   需要再补 `src/VoiceMikonCSharp.Ui/VoiceMikonCSharp.Ui.csproj` 与 `tests/VoiceMikonCSharp.Ui.Tests/VoiceMikonCSharp.Ui.Tests.csproj`
   两个项目。不加也能编（`ProjectReference` 按路径解析），只是 `dotnet build VoiceMikonCSharp.sln`
   不会覆盖到它们。
2. **`src/VoiceMikonCSharp.App` 改成 WinExe 之后**，`dotnet run --project src/VoiceMikonCSharp.App -- --headless --mock`
   仍然可用（输出照旧、退出码 0）；从 PowerShell 用 `&` 直接跑 WinExe 时
   **PowerShell 不会等它**（GUI 子系统的老规矩），要等退出码请用
   `Start-Process -Wait -PassThru` 或直接看 K3 那两条子进程测试。
