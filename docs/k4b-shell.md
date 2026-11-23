# K4b —— 胶囊的拖拽 / 摆位 / 位置记忆 / 托盘 / 退出重启 / 右键菜单

> **接口依据**：[`docs/core-api.md`](./core-api.md)（事件名 / mode 枚举 / 退出顺序）。
> **翻译源**：`voice-mikon/src/voice_mikon/host/placement.py`（几何）、
> `host/drag.py`（拖拽会话）、`host/tray.py` + `__main__.py`（托盘）、
> `host/static/capsule.css`（`.orb-menu` / `.orb-hint` / `prefers-reduced-motion`）。
> **原则**：翻译不是设计 —— 尺寸、色值、动画参数、编码格式全部来自上面这几份。

---

## 0. 这一版做了什么

| 项 | 结果 |
|---|---|
| 新增 `src/VmNext.Ui/Placement.cs` | `placement.py` 的 C# 翻译（**纯函数**）：常量 / 夹取 / 偏移编解码 / 摆位 |
| 新增 `src/VmNext.Ui/DragGeometry.cs` | 拖拽几何（**纯函数**）：鼠标位移 → 窗口位移；"动过没有"的判定 |
| 新增 `src/VmNext.Ui/HitRegionBuilder.cs` | 命中区计算（**纯函数**）：可见元素 → 命中区矩形列表 |
| 新增 `src/VmNext.Ui/OrbPlacementHint.cs` | 位置记忆的载体（`geometry` + `monitor`），UI 层不直接碰 `Config` |
| 新增 `src/VmNext.Shell/TrayIcon.cs` | 托盘图标 + 右键菜单（Win32 `Shell_NotifyIcon` + `TrackPopupMenu`） |
| 新增 `src/VmNext.Shell/HitRegion.cs` | 命中区的数据结构（物理像素 + 圆角） |
| 新增 `src/VmNext.Shell/SystemAnimation.cs` | 系统动画开关（`prefers-reduced-motion` 的 Windows 对应物），**可注入** |
| 修改 `src/VmNext.Shell/NativeMethods.cs` | 补托盘 / 菜单 / GDI 绘图 / 图标 / 动画开关的 P/Invoke |
| 修改 `src/VmNext.Shell/OverlayWindow.cs` | `HitRegions`（外部填矩形）→ `BuildHitRegions()`（子类算）；加 `MoveToPhysicalPixels` |
| 修改 `src/VmNext.Ui/CapsuleWindow.axaml(.cs)` | 拖拽 / 摆位 / 位置记忆 / 命中区 / 动效降级 / 球菜单（删二维码） |
| 修改 `src/VmNext.Ui/UiBinder.cs` | 转发 `SettingsRequested` |
| 修改 `src/VmNext.App/Gui/GuiHost.cs` | 托盘装配 + 菜单动作 + **退出 / 重启** + 位置记忆接线 |
| 修改 `src/VmNext.App/Gui/App.axaml.cs` | 接线；进程退出时补删托盘图标 |
| 修改 `src/VmNext.App/AppOptions.cs` | 加 `RawArgs`（重启时"带着同样的参数再起一个"） |
| 新增 `tests/VmNext.Ui.Tests/` 4 个测试文件 | 摆位 24 条 / 拖拽 6 条 / 命中区 8 条 / 降级 3 条（**共 41 条**） |

---

## 1. 拖拽

### 目的

按住球拖，窗口跟手；松手记住位置；拖动全程不抢用户当前窗口的焦点；拖到另一块屏，球大小随 DPI 变。

### 照搬了什么

- **`host/drag.py` L107 `DragSession` 的语义**：按住 → 拖 → 松手这一段收成自足单元；
  "**点球**（想开始录音）"与"**拖球**（想挪位置）"走同一套按下/松手流程，
  靠"有没有真的动过"区分（`drag.py` L223 `_moved`）。
  → `CapsuleWindow.OnPillPointerPressed` 记起点、`OnWindowPointerReleased` 判"动过没有"。
- **`host/drag.py` L41 `orb_placement` 的换算链**：逻辑像素 → 物理像素用的是**当前**缩放
  （`move_window_to(hwnd, round(nx * scale), round(ny * scale))`）。
  → `DragGeometry.WindowPosition`：物理位移 →（÷ 按下时的缩放）→ 逻辑位移 →（× 当前缩放）→ 物理位移。
- **`placement.py` L106 `clamp_orb_position`**：拖动中球要夹回"允许待着"的范围内，
  纵向比横向严（球的上方要留给展开后的胶囊）。
  → `CapsuleWindow.ClampDragTarget` 每次移动后夹一次。
- **焦点不抢**：`drag.py` 的"焦点冻结"是靠 `restore_focus` 回调实现的（见 §2 的"没搬什么"）。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| `begin_drag` / `move_orb` / `move_capsule` / `end_drag` 四个桥接方法 | 那是 JS → Python 的桥；Avalonia 没有桥，控件直接给事件 | `PointerPressed` / `PointerMoved` / `PointerReleased` 直接挂在 `CapsuleWindow` 上（几十行，全在窗口类里） |
| `DragSession` 的"焦点冻结"（`restore_focus` 回调） | 冻结是因为 pywebview 的窗口点击**可能激活**，得在拖动期间压住守卫 | 我们的窗口带 `WS_EX_NOACTIVATE`，本来就不激活；移动用 `SetWindowPos + SWP_NOACTIVATE`（`OverlayWindow.MoveToPhysicalPixels`），全程不碰前台 |
| `window_scale` / `window_size` 那套"用窗口尺寸 + 常量反推球位置" | 那套是为"窗口固定 512×260、球在里面"设计的 | 我们的窗口宽仍是 512（K4a 定值），球在底边居中，偏移是常量；直接用鼠标位移，不做反推 |
| 前端持续发**绝对**目标位置 | 那是前端 ↔ Python 的协议 | Avalonia 侧自己按"按下时的锚点 + 当前位移"算，天然不漂（丢一次事件也不会累积误差） |

**指针捕获**：按下时 `e.Pointer.Capture(Pill)` —— 鼠标拖到控件外面也要继续收到移动/松开事件
（不捕获的话，拖出球就断了）。

### 测试结果

- `DragGeometryTests` 6 条全过：单屏 1:1 跟手、同缩放位移还原、跨屏按当前缩放换算、
  缩放无效按 1:1 处理、动过/没动过的判定。
- **真机行为（拖拽手感、跨屏 DPI）不写自动化测试** —— 任务书明确要求由用户真机验证。

---

## 2. 摆位

### 目的

启动时球出现在上次的位置；首次启动在默认位置（工作区右下角）；窗口不被摆到屏幕外。

### 照搬了什么

- **`host/placement.py` 的全部函数与常量**（**纯数学，没有 pywebview 特定的东西**）：
  - `ORB_SIZE = 40`（L29）、`ORB_INSET_Y = 10`（L31）、`ORB_MARGIN = 16`（L33）、`EDGE_MARGIN = 8`（L21）
  - `clamp_to_work_area`（L36）、`orb_inset_x`（L84）、`orb_screen_position`（L92）、
    `clamp_orb_position`（L106）、`orb_window_position`（L131）、
    `default_orb_position`（L149）、`orb_position_from_geometry`（L162）
  - `encode_offset`（L52）/ `decode_offset`（L71）
- **位置记忆的编码格式**：`"dx,dy"` —— 球相对所在工作区左上角的**逻辑**像素偏移，
  另存显示器设备名。**必须与 Python 版兼容**：用户拿旧的 `config.json` 能直接用。
- **四舍五入规则**：Python 的 `round()` 是银行家舍入，C# 的 `Math.Round(double)` 默认也是
  `MidpointRounding.ToEven` —— 两边逐位一致（这里**刻意不用** `OverlayGeometry.ToPixels`，
  那个是"远离零"舍入）。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| `orb_window_position` 里"从球位置反推 512×260 窗口左上角"的那一步 | Python 的窗口高固定 260；我们的高度**跟着内容**走 | 反推照做，但用**实际**窗口尺寸（`winW` / `winH` 是参数，不是常量） |
| `orb.follow` / `orb.pinned` / `orb.custom` 三档位置模式 | **Python 版已经退役了**：`config.py` L31-32 明确写"胶囊位置三档、悬浮球位置三档(follow/pinned/custom)…均已退役"；`capsule.css` L663-668 的 `.orb-follow` 只是遗留样式 | 只做两档：**默认右下角** + **用户拖动记忆**（见 §8 疑问 #1） |
| `Monitors`（Python `win32/monitors.py`） | 那是 Win32 互操作 | 复用 **Core 已有的** `VmNext.Core.Interop.Monitors`（K1 就有：`ListMonitors` / `MonitorAtPoint` / `MonitorByDevice` / `PrimaryMonitor`），不重复造 |

### 测试结果

- `PlacementTests` 24 条全过：常量、编解码（含"工作区有偏移""非法输入""往返"）、
  球内缩、球的屏幕位置、默认位置（含缩放）、夹窗口、夹球（含"纵向要给胶囊留位"）、
  球→窗口反推、记忆还原、**换缩放率相对位置不变**。
- 对照用例的期望值都是**按 Python 的公式手算**的（例如
  `default_orb_position((0,0,1920,1080), 1.0)` → `(1864, 1024)`）。
- 多屏 / 不同缩放 / 工作区非全屏：用合成的工作区矩形覆盖（`WorkArea(1920, 0, 1920, 1080)`
  这类第二块屏的形状）。

---

## 3. 命中区

### 目的

球 / 胶囊 / 文字条 / 菜单 / 气泡之外的透明区域，点击穿到下层窗口。

### 照搬了什么

- **真机 `capsule.js` L284-300 的规矩**：浮层（菜单 / 气泡）**单独一块**，
  **不能**并进球的包围盒 —— 并进去中间全是空白，空白在裁形里会露出底色。
  → `CapsuleWindow.BuildHitRegions` 里球 / 文字条 / 菜单 / 气泡**各报一块**。
- **`capsule.css` 的圆角**：文字条 14px、`.orb-menu` 12px（CSS L724）、
  `.orb-hint` 999px（L796）、胶囊 999px（L303）。
  → 直接读 `Border.CornerRadius`（布局用的就是那个值），999px 会被夹到短边的一半
  （Win32 的圆角矩形不允许超过一半）。
- **Python 侧 `SetWindowRgn` 裁成并集**的语义 → `OverlayWindow.BuildRegion` 里 `CombineRgn(RGN_OR)`。

### 没搬什么及替代写法

- **不搬"上报"这个动作**：真机是前端 `getBoundingClientRect` 量矩形 → `update_regions` 上报 →
  Python 裁，三步。Avalonia 下窗口就是控件树的根，每个元素的 `Bounds` 直接可读，
  **"量"这一步由 UI 层自己做**，不需要中间人。

**选型：a（带 b 的通用机制骨架）**

| 任务书给的选项 | 这里的落地 |
|---|---|
| (a) 逻辑全在 `CapsuleWindow`，删掉 `OverlayWindow.HitRegions` | ✅ 采纳：**删掉了** `OverlayWindow.HitRegions` 这个"外部填矩形"的公共属性；命中区的**算法**全在 `CapsuleWindow.BuildHitRegions` |
| (b) Shell 保留通用机制但不要求外部填矩形 | ✅ 也采纳：Shell 提供 `protected virtual IReadOnlyList<HitRegion> BuildHitRegions()`，默认返回空 → 退回"整个客户区 + 圆角"（`OverlayShape`），别的覆盖层窗口也能用 |

**为什么不是纯 (b)（attached property + 遍历可视树）**：Avalonia 里找"哪些元素参与命中"有两种做法
（显式列出来 / 打标记后遍历可视树）。显式列出来的好处是**哪些块参与命中是看得见的代码**
（球、文字条、菜单、气泡 —— 与 CSS 的选择器一一对应），而不是靠遍历时才知道；
代价是新增可见元素时要记得加一行（K4a 的 `UpdateHitRegion` 本来就是这么列的，本版延续）。
遍历可视树的写法在本项目里没有额外收益：窗口里就这几个元素。

**触发时机**：布局落定后、尺寸变化后、菜单弹出/收起后重算（`Dispatcher.UIThread.Post(..., Loaded)`），
**不是每帧重算** —— 形变动画期间 `OnSizeChanged` 会连着触发，但那时布局已落定，读到的 `Bounds` 是最新的。

### 测试结果

- `HitRegionTests` 8 条全过：
  - 纯函数 4 条：DPI 换算、空矩形被跳过、圆角按缩放换算、超大圆角夹到短边一半；
  - 无头窗口 4 条：**球形态只有球一块**、录音态胶囊 + 文字条两块、菜单开多一块、气泡开多一块。
- ⚠ 写测试时抓到一个**真 bug**：`Avalonia.Rect` 的语义是 `(x, y, width, height)`，
  不是 `(left, top, right, bottom)` —— 第一版按后者拼 `Rect`，球的命中区宽度算成了 276 而不是 40。
  已修（`new Rect(origin.X, origin.Y, bounds.Width, bounds.Height)`）。

---

## 4. 托盘

### 目的

托盘图标三色随 `AppState`；右键菜单：开始/停止录音 / 设置 / 重新探测麦克风 / 打开配置文件 /
查看日志 / 重启 / 退出；退出时必须 `NIM_DELETE`（否则托盘留死图标）。

### 照搬了什么

- **`host/tray.py` L13 `STATE_COLORS`**：灰 = 待机 `(70,70,70,255)`、红 = 录音 `(200,40,40,255)`、
  黄 = 识别中/警告 `(210,160,30,255)`、红 = 错误 `(200,40,40,255)`。
- **`host/tray.py` L39 `make_icon` 的画法**（64×64）：
  ```
  d.ellipse([2, 2, size - 2, size - 2], fill=color)     # 状态色的圆
  d.rounded_rectangle([28, 12, 36, 34], radius=4, fill=FG) # 麦克风头
  d.arc([21, 24, 43, 46], start=0, end=180, fill=FG, width=3)  # U 形支架
  d.line([32, 42, 32, 52], fill=FG, width=3)             # 支架立杆
  ```
  → `TrayIcon.CreateTrayIcon` 用 GDI 逐笔画出来（见下）。
- **`__main__.py` L478-490 的菜单项文案与顺序**：
  `开始 / 停止录音`（默认项）、`设置面板`、`重新探测麦克风`、`打开配置文件`、`查看日志`、`重启`、`退出`。
- **`__main__.py` L500-508 `on_core_state` 的状态→颜色映射**：
  `recording→recording`、`processing→warn`、`starting→warn`、`idle→idle`、其余→`idle`。
- **`__main__.py` L333 `_open_config_file` / L343 `_open_log_file`**：用系统默认程序打开
  （Python 的 `os.startfile` ↔ C# 的 `Process.Start(UseShellExecute = true)`）。
- **`__main__.py` L424 `reload_mic`**：重新探测麦克风并保存配置
  （C# 侧 `CoreController.ReloadMic()` 自己会更新 `config.input_device` 并 `Save()`）。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| pystray（库） | 库 | 直接 Win32：`Shell_NotifyIconW` + `TrackPopupMenu`（托盘场景最自然） |
| pystray 自己起线程跑消息循环 | 库的内部结构 | `TrayIcon` 自己一条线程 + 一个 message-only 窗口 + 一个消息循环（和 `HotKeyWindow` 同类的东西放在 Shell） |
| `tray.py` L146 `_build_menu` 里"吞掉所有位置参数"的 lambda + 二次异常保护 | 那是 pystray 的调用约定（会把 Icon 对象当位置参数塞进来） | `TrackPopupMenu` 用 `TPM_RETURNCMD` 直接返回命令 id，没有这种坑；异常由宿主统一兜 |
| `tray.py` L54 `Tray` 的 `set_menu` / `set_icon_state` 两套 API | 库的封装 | `TrayIcon.SetMenu` / `SetState`（可从任意线程调用，内部投递消息到托盘线程） |
| PIL 画图标 | Python 生态 | **GDI 画**（`CreateDIBSection` 32bpp + `Ellipse` / `RoundRect` / `PolyBezier` / `LineTo`），不引入新依赖 |

**两处实现差异（写在代码注释里）**：

1. **U 形支架用两段三次 Bezier 近似，不用 GDI 的 `Arc`** ——
   `Arc` 的起止点是"角度约定"（且 y 轴向下的 DC 里方向容易搞反），
   Bezier 的控制点是自己算的，画出来一定是从右经下到左的**下半弧**（= PIL 的 `arc(0,180)`）。
2. **背景是透明的** —— GDI 位图没有 alpha 通道，所以画完补一遍（有颜色的像素 alpha 置 255）。

**线程**：托盘菜单的回调在**托盘线程**（Win32 消息线程），不是 UI 线程 ——
`GuiHost` 里每个回调都 `Dispatcher.UIThread.Post` 再干活（core-api.md §2/§3 的规矩）。

### 测试结果

- **不写自动化测试** —— 任务书明确要求（托盘涉及 Win32 与桌面）。
- 编译通过；`Shell_NotifyIcon` / `TrackPopupMenu` / GDI 的 P/Invoke 都集中在
  `NativeMethods.cs`（本仓库的规矩：Win32 互操作只进这一个文件）。

---

## 5. 退出 / 重启

### 目的

三条退出路径（托盘菜单 / 球菜单 / 面板——面板是 K5，留回调位）汇到同一个 `RequestQuit`；
顺序：**停 UI 推送 → 销毁窗口 → 删托盘图标 → 注销热键**。
重启 = 置标记走同一条退出路径，**全部清理完成后**再拉新实例。

### 照搬了什么

- **退出顺序**（`docs/core-api.md` §1 + `__main__.py` L558 `_shutdown_and_exit` 的**理由**）：
  ```
  停 UI 推送（UiBinder 解绑）      ← 推给一个没人看的界面没有意义
  → 销毁窗口（capsule.destroy）
  → 删托盘图标（NIM_DELETE）      ← 不删 = 托盘里留一枚点不动的死图标
  → 注销热键（hotkey.stop）        ← 全局资源！占着不放，"退出后马上重启"会注册失败
  → core 收尾（controller.shutdown）
  → 归还剪贴板所有权
  ```
  → `GuiHost.RequestQuit` 逐步执行，前四步是本层做的，后两步是 `AppHost.Shutdown`（K3 已有）。
- **重启的时机**：`__main__.py` L548-551 的注释 —— "走到这里说明所有清理都完成了…现在拉新实例才不会撞车"。
  → `GuiHost.StartRestart` 在 `RequestQuit` 的**最后**才 `Process.Start`。
- **`Process.Start` + `UseShellExecute = true`**（脱离父进程）。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| `_restart_plan` 的"忠实复刻启动方式" | Python 有四种入口（脚本 / `-m` / frozen / 薄壳），C# 只有一个 exe | `Environment.ProcessPath` + 同样的 args（`AppOptions.RawArgs` 留了一份原样） |
| `_capture_entry` | 那是 Python 入口捕获的机制 | 不需要 |
| 面板按钮的退出 | 面板是 K5 | 留了回调位：`GuiHost.RequestQuit` 就是那条路，K5 接到即可 |

**`dotnet run` 场景的兜底**：`Environment.ProcessPath` 在 `dotnet run` 下是 `dotnet.exe`，
所以额外判断文件名是 `dotnet` 时把入口 dll 带上（`Environment.GetCommandLineArgs()[0]`），
否则重启会拉起一个空的 dotnet。真机验证用发布版 exe，不受影响。

### 测试结果

- **不写自动化测试** —— 任务书明确要求（退出顺序、重启、托盘、拖拽真机行为都由用户真机验证）。
- 编译通过；顺序写在 `GuiHost.RequestQuit` 的注释里（步骤号一一对应）。

---

## 6. 右键菜单与气泡

### 目的

球上右键弹一排圆按钮（设置 / 退出）；"点球时模型还在加载"之类的提示用气泡，几秒后自己淡掉。

### 照搬了什么

- **`capsule.css` L706-764 `.orb-menu`**：深底 `--deep` + 1px 描边 `--stroke`、12px 圆角、
  `padding: 8px`、按钮 34×34 圆（`border-radius: 50%`）、`gap: 8px`、
  hover 底色 `--cancel-btn`（L758）、退出项 hover 警示色 `--err-bg` / `--err-fg`（L761-764）。
  → K4a 的 XAML 已经把菜单的框做出来了；本轮补上 **hover 色**（含退出项的警示色，
  前景色落到 `Path.Stroke` 上，因为按钮里的图标写死了 `Stroke`）。
- **`capsule.css` L785-810 `.orb-hint`**：999px 圆角气泡、`padding: 6px 12px`、12px 字、
  `opacity .18s ease` 淡入淡出。→ K4a 已有；本轮补了"气泡/菜单变可见时重算命中区"。
- **`capsule.js` 的显隐逻辑**：
  - `closeOrbMenu`（L793）：淡出 140ms 后才彻底隐藏（K4a 已有）；
  - `openOrbMenu`（L817）：**先 `hideOrbHint()`** —— 提示气泡和菜单不同时出现（K4a 已有）；
  - `showOrbHint`（L855）：显示后 2200ms 自动隐藏（K4a 已有）；
  - `onDocMouseDownForMenu`（L845）：**点菜单以外的地方（含左键点球）收菜单**；
    点菜单内部不算 → 本轮补上（`OnWindowPointerPressedForMenu`）。

### 没搬什么及替代写法

- **不搬 `capsule.js` L710-713 "居中不能用 transform" 那条规矩** ——
  那条是因为 Python 版靠 `offset*` 量矩形上报，用了 `transform` 量到的位置会偏半个身位，
  裁出来的窗口形状就把菜单切掉一半。**Avalonia 没有上报**，`HorizontalAlignment=Center` 即可。
- **不搬二维码扫描按钮**：Python 版的球菜单有三项（设置 / 二维码 / 退出），
  二维码是 `companions/` 附属工具 —— 任务书明确"这次不做、菜单里不列"，所以从 XAML 里删掉了。

### 测试结果

- 菜单项与气泡的**显隐逻辑**（K4a 已有的 12 条测试）仍全过；
  本轮新增的 hover 色与"点外面收菜单"没有自动化测试（纯视觉 / 交互行为）。

---

## 7. 其它

### 7.1 动效降级（`prefers-reduced-motion`）

- **照搬**：`capsule.css` L674-684 —— 系统关动画时
  `.recording-pill` / `.textbar` / `.bar-orb` 的 `transition: none !important`，
  `.bar-orb svg` 的 `animation: none !important`。
  → `CapsuleWindow.ApplyReducedMotion(true)` 把所有 `Transitions` 的 `Duration` 归零
  （含收起时的 `Delay` —— CSS 的 `transition: none` 连 delay 一起作废）。
- **不搬**：CSS 的媒体查询本身（那是浏览器的机制）。
  → Windows 的对应开关是 `SPI_GETCLIENTAREAANIMATION`（"轻松使用 → 视觉效果 → 在显示中显示动画"），
  放在 `Shell.SystemAnimation`，**可注入**（`AnimationEnabledProvider`）——
  无头环境读不到真实系统设置，断言就没法做。
- **测试**：`ReducedMotionTests` 3 条全过（开启后全为零、系统关动画时建窗即降级、关闭后时长恢复）。

### 7.2 `ui_mode` 四档

- **K4a 已做**，本轮核对了一遍 `host/capsule.py` L786-819 `_window_should_be_visible` / `_hide`：
  - `orb` 档：球常驻 → 窗口一直可见（Python 的 `_hide` 在 orb 档**不藏窗口**，L809-812）；
  - `resident` / `on_demand` 档：无球 → 待机时整块藏起来（录音/识别时才露脸）。
  → 与 K4a 的 `ApplyWindowVisibility`（`shouldShow = _orbVisible || mode != orb`）一致，**未改**。

### 7.3 文字条按下临时激活

- K4a 已做（按下 → `SetAllowActivation(true)`，松手 → 切回）。
- **本轮补**："选文本期间（`PointerReleased` 之前）不切回不激活" ——
  把"松手切回不激活"从**只挂在文字条上**改成**挂在窗口上**：
  选区可以拖到文字条外面，那时的 `PointerReleased` 不经过文字条，
  只挂文字条会让窗口一直"可激活"，用户正在打的字就被抢走了。

---

## 8. 与 Python 版的行为差异（只列用户能感知的）

| # | Python（voice-mikon） | 本版 | 为什么 |
|---|---|---|---|
| 1 | 球菜单三项：设置 / 二维码扫描 / 退出 | 两项：设置 / 退出 | 二维码是 `companions/` 附属工具，本次不做（任务书要求菜单里不列） |
| 2 | 托盘菜单第二项是"设置面板"（打开面板） | "设置" —— 面板是 K5，点了发一条"面板尚未实现"的通知 + 球上冒一句提示 | 面板还没做，不能开一个不存在的窗口 |
| 3 | 窗口固定 512×260 | 宽度固定 512，**高度跟着内容** | K4a 已有的差异（真机 260 高是为了 WebView2 提交不闪，Avalonia 没这个约束） |
| 4 | 命中区是**矩形**（前端上报 CSS 盒） | **带圆角**的矩形（圆角抄 CSS 的值） | 视觉与命中一致；真机在 512×260 的窗口里裁矩形并集时中间有空白 |
| 5 | 托盘图标用 PIL 画 | 用 GDI 画（同样的 64×64、同样的圆与麦克风） | Python 生态的差别，外观一致 |
| 6 | 拖动时前端持续发**绝对**目标位置 | 窗口自己按"按下锚点 + 位移"算 | 没有前端 ↔ Python 的桥；结果等价且不累积误差 |
| 7 | 位置记忆存在 `config.orb.geometry` / `config.orb.monitor` | **完全一样**（同一份 `config.json` 两边通用） | 这是持久化格式，必须兼容 |

---

## 9. 疑问清单

> 每条：**目的 / 我的处理 / 为什么**。

### 疑问 #1：任务书说的"三种位置模式（跟随光标 / 固定默认位 / 用户自定义）"在 Python 版里已经不存在了

- **原文**：任务书 §2 写"三种位置模式（跟随光标 / 固定默认位 / 用户自定义 —— 见 `capsule.css` L663-668 的
  `.orb-follow` 注释和 `host/placement.py`）按 `config` 选"。
- **查证**：`voice-mikon/src/voice_mikon/config.py` L31-32 明确写
  "胶囊位置三档、悬浮球位置三档(follow/pinned/custom)、待机两态(resident/on_demand)**均已退役**"；
  `config.py` 里只有 `ui_mode`（四档）+ `orb`（位置记忆）两个相关字段；
  `capsule.css` L663-668 的 `.orb-follow` 只是**遗留样式**（正文里已经没有对应的 class 切换）。
- **我的处理**：只实现两档 —— **默认右下角**（`default_orb_position`）+ **用户拖动记忆**
  （`orb.geometry`）。**没有做"跟随光标"**：Python 版没有实现、没有 config 字段、没有可照搬的语义，
  自己发明一套等于凭空加功能。
- **为什么**：原则 2 是"照搬"，不是"按任务书描述重新设计"。如果用户确实想要"跟随光标"，
  那是一个新功能，应该单独一轮做（需要新增 config 字段 + 定时跟随光标 + 与拖拽互斥）。

### 疑问 #2：位置记忆存"球"的位置还是"窗口"的位置？

- **原文**：`placement.py` L162 `orb_position_from_geometry` 的注释说
  "存的是**球**的偏移（不是窗口的）：球是用户真正盯着的那一小块"；
  但 `encode_offset`（L52）的入参是"窗口左上角"。
- **我的处理**：**存球的偏移**（`orb.geometry` = 球相对工作区的逻辑偏移），
  还原时用 `orb_position_from_geometry` 反推窗口位置 —— 与 Python 版**逐字相同**。
  之所以两条注释看着矛盾：Python 的窗口固定 512×260 且球底边居中，
  球的偏移能唯一反推窗口位置；而 `encode_offset` 传窗口左上角是因为 Python 那边
  `_save_orb_position` 传的是窗口位置（同一套偏移，因为球与窗口的相对位置是常量）。
- **为什么**：**持久化格式必须兼容** —— 用户拿旧 `config.json` 要能用。
  我们的窗口宽度仍是 512（K4a 定值），球仍在底边居中，反推公式与 Python 一致，
  所以同一个 `orb.geometry` 在两边都能还原到同一个相对位置。

### 疑问 #3：拖拽的位移换算，Python 依赖前端发"逻辑像素绝对位置"，我们怎么算？

- **原文**：`drag.py` L181 `DragSession.move` —— "前端每次都从 mousedown 起点算绝对目标发过来"，
  Python 侧 `move_window_to(hwnd, round(nx * scale), round(ny * scale))`，
  其中 `scale` 是**当前**窗口所在显示器的缩放。
- **我的处理**：`DragGeometry.WindowPosition` —— 物理位移 →（÷ 按下时的缩放）→ 逻辑位移
  →（× 当前缩放）→ 物理位移。
- **为什么**：没有前端，就没有"逻辑像素绝对位置"可收；只能用屏幕坐标。
  绕一圈之后：单屏（缩放不变）= 物理 1:1，跟手；跨屏 = 按**新**屏的缩放换算，
  与 Python 的 `scale` 取当前值同款。差异只在"跨屏那一瞬间的缩放切换"上：
  Python 那边受 WebView2 坐标系影响会有漂移，这边是确定的。

### 疑问 #4：命中区为什么用"显式列出可见元素"而不是"打标记后遍历可视树"？

- **原文**：任务书给了 (a) / (b) 两个方案，(b) 是
  "`OverlayWindow` 提供 attached property `ParticipatesInHitRegion`，`ApplyRegion` 时遍历可视树找标记的元素"。
- **我的处理**：**a 的主体 + b 的骨架** —— 删掉 `OverlayWindow.HitRegions`（外部填矩形），
  命中区算法全在 `CapsuleWindow.BuildHitRegions`；Shell 侧保留
  `protected virtual BuildHitRegions()` 这个通用挂点（默认空 → 整窗圆角）。
- **为什么**：本窗口里参与命中的元素就四个（球 / 文字条 / 菜单 / 气泡），
  显式列出来**看得见**、与 CSS 的选择器一一对应；打标记 + 遍历可视树要多写一套
  attached property 机制，收益只是"新增元素时少写一行"。另外显式列出还能顺手拿到
  每个元素的圆角（`Border.CornerRadius`），这是 b 方案要多花一步才拿得到的东西。

### 疑问 #5：托盘菜单多了"开始 / 停止录音"，"设置面板"改成了"设置"

- **原文**：任务书 §4 的菜单清单是"打开配置文件 / 打开日志 / 重新探测麦克风 / 设置（K5 未做）/ 重启 / 退出"，
  没有"开始 / 停止录音"；但同一节又说"照搬 `tray.py` 的……菜单项文案与顺序"，
  而 Python 的菜单（`__main__.py` L478-490）第一项就是"开始 / 停止录音"（默认项）。
- **我的处理**：**保留**"开始 / 停止录音"（照搬 Python 的文案与顺序，也是双击托盘图标的默认动作）；
  "设置面板"改成"设置"（指向的面板还不存在，点了会发"面板尚未实现"的通知）。
- **为什么**：托盘是"程序还活着吗"的入口，没有录音开关会很奇怪；照搬优先于任务书的简写。

### 疑问 #6：`prefers-reduced-motion` 的 Windows 对应物不是精确等价

- **原文**：CSS L675 `@media (prefers-reduced-motion: reduce)`；
  任务书指定用 Windows 的 `SPI_GETCLIENTAREAANIMATION`。
- **我的处理**：用 `SPI_GETCLIENTAREAANIMATION`（"在显示中显示动画"）；
  读不到时按"有动画"处理。
- **为什么**：Windows 没有 per-element 的动效降级，只有一个全局开关；
  CSS 那边降级的是三条 transition + 一条 animation，我们对应的是
  **所有** `Transitions` 的时长归零（含收起时的 delay）——
  粒度比 CSS 粗一档，但比"什么都不做"强。读不到时宁可多播动画，也不要在用户没要求时把界面搞死板。

---

## 10. 测试结果

| 项目 | 总数 | 通过 | 失败 | 跳过 | 说明 |
|---|---|---|---|---|---|
| `VmNext.Ui.Tests`（K4a 的 12 + 本轮新增 33） | **53** | **53** | 0 | 0 | 新增：摆位 15 / 拖拽 6 / 命中区 9 / 动效降级 3 |
| `VmNext.TokenCheck` | — | ✓ 全部一致 | — | — | 两张 palette 与 `capsule.css` 完全对得上（本轮没动 palette） |
| `VmNext.Core.Tests` | 14 | 14 | 0 | 0 | 未动 |
| `VmNext.Audio.Tests` | 8 | 8 | 0 | 0 | 未动 |
| `VmNext.Engines.Tests` | 80 | 78 | 0 | 2 | 未动；2 条跳过是环境限制 |
| `dotnet build src/VmNext.App` | — | ✓ 0 警告 0 错误 | — | — | |
| `--headless --mock --timeout 60` | — | ✓ 引擎就绪，退出码 0 | — | — | K3 的自检路径仍通 |

**故意没跑的**：`tests/VmNext.App.Tests`（含"起 GUI 子进程 + `PrintWindow`"那条 ——
会往用户屏幕上弹窗口）；以及一切涉及 Win32 桌面行为的自动化（托盘 / 退出顺序 / 重启 /
拖拽真机行为），按任务书由用户真机验证。

**验证手段只有三种**：纯函数单元测试、`Avalonia.Headless` 无头控件测试、`dotnet build`。
**测试代码**里没有调用过 `SendInput` / `keybd_event` / `mouse_event` / `SetCursorPos` /
`SetForegroundWindow` / `AttachThreadInput` / `OpenClipboard` / `SetClipboardData` /
`RegisterHotKey` / `Shell_NotifyIcon` / `WasapiCapture`，也没有启动过 GUI 子进程
（这些 API 只出现在产品代码里：`Shell_NotifyIcon` 在 `TrayIcon.cs`，
`RegisterHotKey` 在 K1 的 `HotKeyBinding`，注入相关的在 K1 的 `Injector`）。
