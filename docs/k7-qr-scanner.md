# K7 —— 二维码扫描（附属工具重写为 C# 独立项目 + 主程序菜单入口）

> 本文是 **K7 阶段的过程记录**，现状以代码和 [`AGENTS.md`](../AGENTS.md) 为准。
>
> **翻译源**：`voice-mikon/companions/qr_scanner/`（Python 版工具本体：约 460 行
> tkinter）、`voice-mikon/src/voice_mikon/__main__.py` 的 `make_qr_scanner_launcher`
> （拉起语义）、`host/static/capsule.html` 的 `#menuQr`（菜单图标 SVG）与
> `host/static/capsule.css` 的 `.orb-menu-btn`（菜单按钮样式）。
> **原则**：交互流程照搬；视觉只有球菜单按钮照搬（工具本身 tkinter 没有 CSS，
> 用 WinForms 默认控件）。

---

## 0. 这一版做了什么

| 项 | 结果 |
|---|---|
| 新增 `companions/QrScanner/` | 独立 WinForms 项目（net10.0-windows / WinExe / ZXing.Net）：截屏 → 框选 → 解码 → 结果窗（在 sln 的 `companions` solution folder 里） |
| 新增 `src/VmNext.Core/Companions.cs` | 附属工具的**路径定位**（纯函数：给 base dir 返回路径 / 文件不存在返回 null） |
| 修改 `src/VmNext.Ui/CapsuleWindow.axaml(.cs)` | 球菜单加回「二维码扫描」按钮（图标取自 capsule.html 的 `#menuQr`）+ `QrScanRequested` 事件 |
| 修改 `src/VmNext.Ui/UiBinder.cs` | 转发 `QrScanRequested` |
| 修改 `src/VmNext.App/Gui/GuiHost.cs` | `LaunchQrScanner()`：`Process.Start` 拉起**独立进程**（不等、不看输出、异常只记日志 + 发通知） |
| 修改 `src/VmNext.App/VmNext.App.csproj` | TFM `net10.0` → `net10.0-windows`；build-only `ProjectReference`（`ReferenceOutputAssembly=false`）+ `CopyCompanionQrScanner` 目标（产物复制到输出目录 / publish 目录的 `companions/QrScanner/`） |
| 修改 `VmNext.sln` | 新增 `companions` solution folder（装 `QrScanner`）+ `tests` folder 里加 `VmNext.Companions.Tests` |
| 新增 `tests/VmNext.Companions.Tests/` | 解码 4 条 + 框选几何 5 条（**共 9 条**） |
| 新增 `tests/VmNext.Core.Tests/CompanionsTests.cs` | 路径定位 4 条 |
| 新增 `tests/VmNext.Ui.Tests/OrbMenuTests.cs` | 球菜单三项齐全且顺序一致 + 二维码图标几何落在视框内 + 点二维码按钮发出请求（**3 条**） |

---

## 1. 附属工具：独立进程

### 目的

悬浮球右键菜单「二维码扫描」拉起一个**独立进程**的小工具。
主程序与工具互不认识：不等它、不看它的输出、不管它的死活。

### 照搬了什么

- **`voice-mikon/src/voice_mikon/__main__.py` L210-264 `make_qr_scanner_launcher` 的语义**：
  只负责"把工具拉起来"，工具崩了不影响主程序；工具不在时**明确报错**（不静默）。
  → `GuiHost.LaunchQrScanner()`：`Process.Start` + `UseShellExecute = false`，
  不 `WaitForExit`、不读 stdout/stderr；`catch` 里只记日志 + 发一条通知。
- **`_warn_dialog`（L310-317）的文案语义**：缺工具时告诉用户"缺了 + 在哪"。
  → 通知 `qr_scanner_missing`：「二维码扫描工具缺失：<路径>」。
- **Python 版工具"依赖缺失时自己弹窗报告，不静默"**（`__main__.py` 的 `_fatal`）。
  → C# 侧：入口 `catch` 住所有异常 → `MessageBox` 报出来（stderr 那份也留）。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| `_python_exe_for_children`（找解释器） | C# 侧没有"解释器"这回事 —— 工具就是个 exe | 直接 `Process.Start(<QrScanner.exe>)` |
| `subprocess.Popen(..., creationflags=CREATE_NO_WINDOW)` | 压黑框是"控制台程序从 GUI 进程拉起"的问题 | 工具是 **WinExe**，本来就没有控制台窗口 |
| `cwd=tool_dir`（`-m` 才找得到包） | exe 不需要工作目录来定位自己 | `WorkingDirectory` 指到工具自己的目录（稳妥） |
| 工具目录由 `PROJECT_DIR / "companions" / "qr_scanner"` 推导 | Python 版主程序和工具在同一个源码树里 | C# 版工具的产物**复制**到主程序输出目录的 `companions/QrScanner/` 下，按 `AppContext.BaseDirectory` 找 |

### sln 归属与构建接线

任务书给了两个选项：**(a) 不进根 `VmNext.sln`**；**(b) 进 sln 但放在 `companions`
solution folder，且不与任何 `VmNext.*` 项目建立 `ProjectReference`**。

实测下来，两条都有坑：

| 试过的做法 | 结果 |
|---|---|
| (a) 不进 sln | sln 构建**拿不到配置映射** → `dotnet build VmNext.sln -c Release` 把工具按 **Debug** 构建，Release 目录不存在 → 复制被跳过 → **发布产物里没有工具** |
| `SkipGetTargetFrameworkProperties="true"`（绕开 TFM 检查） | 这个开关会让 MSBuild **跳过引用项目的构建** —— 顺序没了，复制拿不到当次产物 |
| (b) 进 sln + build-only `ProjectReference` | ✅ 配置正确（sln 的映射生效）、顺序正确（引用关系保证工具先构建）、**产物被复制到输出目录与 publish 目录** |

**最终做法**：工具进 sln 的 `companions` solution folder；`VmNext.App` 用
`ReferenceOutputAssembly="false"` 的 `ProjectReference`（**只定构建顺序，不引用程序集**）。
零 import 的**实质**仍然满足 —— 工具的类型不参与主程序的编译，工具的依赖
（ZXing.Net）也不进主程序的依赖图；"复制"不是"引用"。

顺带的两个连带改动：

- **`VmNext.App` 的 TFM `net10.0` → `net10.0-windows`**：本程序本来就是 Windows-only
  （Win32 P/Invoke / 托盘 / 剪贴板 / Avalonia 桌面）；带上 `-windows` 之后，
  `net10.0-windows` 的工具可以被正常 `ProjectReference`，不用绕 TFM 检查。
  被它引用的 `Core / Audio / Engines / Shell / Ui` 仍是 `net10.0`（**没动**）。
- 工具**不跟 RID**（`UndefineProperties="RuntimeIdentifier"`）→ 它的输出目录名固定，
  复制路径可以写死（`bin\$(Configuration)\net10.0-windows\`）。

### 测试结果

- `CompanionsTests` 4 条全过：期望路径拼得对、文件存在时定位到它、
  文件不存在返回 null、空目录参数返回 null。
- **不写"真的拉起进程"的测试** —— 那属于碰桌面。

---

## 2. 工具本体：截屏 → 框选 → 解码 → 结果窗

### 目的

和 Python 版一模一样的交互：启动 → 全屏半透明遮罩（覆盖所有显示器）→ 拖拽框选 →
松手后对选区解码 → 成功弹结果窗并自动复制，失败提示"未识别到二维码"；
Esc / 右键取消退出。

### 照搬了什么

- **`capture.py`（mss）**：截**整个虚拟桌面**（所有显示器拼起来的那一整块），
  原点平移到虚拟桌面左上角 —— 选区窗也从那儿铺满，两套坐标天然一致。
  → `ScreenCapture.GrabVirtualScreen()`：`Graphics.CopyFromScreen` +
  `SystemInformation.VirtualScreen`。
- **`selector.py` 的观感**：铺的是截屏那一帧的**变暗版**（与全黑混合 45%），
  拖出的矩形内实时贴回**原图**（框内亮、框外暗），选区边框 + 尺寸标签是琥珀色
  （`#f59e0b`，与主程序警告色同族），顶部一行操作提示。
  → `SelectorForm.OnPaint` 全部照搬；变暗用逐像素乘法（不走 GDI 颜色矩阵，结果确定）。
- **`selector.py` 的选区算法**：起点 / 终点 → 矩形；反向拖拽框出同一个矩形；
  拖太小（< 10px）当误触取消。
  → `SelectionGeometry`（**纯函数**）：`Normalize` / `IsTooSmall` / `ToImage`。
- **`decode.py` 的补救顺序**：原图 → 反色 → 放大一倍，逐级试，某一遍有命中就停。
  → `QrDecoder.Decode`：同样的三档候选。
- **`result.py` 的结果窗**：有命中就展示类型与内容，并把全部内容拼接后**自动复制**；
  没命中就展示引导文案；**识别完不自动关窗**（读完自己关）。
  → `ResultForm` 一模一样的结构（标题 / 正文框 / 状态行 / 复制 + 关闭）。
- **坐标系**：全程物理像素。进程在**任何截屏 / 建窗之前**声明 per-monitor v2
  （`Application.SetHighDpiMode`），`VirtualScreen` / `CopyFromScreen` / 鼠标事件
  是同一套坐标。
- **跨显示器**：副屏挂在主屏左边时虚拟桌面原点是负的 → 鼠标坐标先经
  `SelectionGeometry.ToImage` 减去虚拟桌面原点，再拿去裁图。

### 没搬什么及替代写法

| Python 的做法 | 为什么不搬 | 这里的写法 |
|---|---|---|
| mss | 第三方库 | WinForms 自带的 `Graphics.CopyFromScreen`（多显示器就是 `VirtualScreen` 那一块） |
| pyzbar / zbar | 需要装原生库（Windows 上是 DLL） | **ZXing.Net**（NuGet，纯托管，多格式对等；对 net6.0+ 无传递依赖） |
| tkinter 全屏窗（`overrideredirect` + `focus_force`） | tkinter 的无边框 / 抢焦点那套 | WinForms：`FormBorderStyle.None` + `TopMost` + `ShowInTaskbar=false` + `Bounds=VirtualScreen` |
| PIL `ImageOps.invert` / `resize(LANCZOS)` | PIL | `ImageOps.Inverted`（逐像素取反）/ `ImageOps.Scale2x`（`HighQualityBicubic` 插值） |
| win32 直写剪贴板（`CF_UNICODETEXT` + `GlobalAlloc`，手写 argtypes） | 那是"tk 的延迟提交在进程退出后会丢内容"的绕法；C# 侧没这个问题 | `Clipboard.SetText`（内部有占用重试；外面再包一层 5 次重试） |
| 运行时探测 pip 包是否装齐 | Python 的依赖是运行时才有的 | C# 的依赖在**编译期**就定了（NuGet）；运行期只可能缺 DLL，入口 catch 后弹窗 |

### 测试结果

- `QrDecoderTests` 4 条全过：
  - ZXing 生成一张 QR 位图 → 解码 → **文本逐字一致**、类型是 `QR_CODE`；
  - 中文文本（含全角破折号）也解得出来（生成端显式声明 UTF-8 → ZXing 写 ECI 段）；
  - **空白位图 → 返回空结果**（不是抛异常）；
  - 反色的码也解得出来（验证"反色"那一档补救确实在跑）。
- `SelectionGeometryTests` 5 条全过：正向拖拽 / 反向拖拽框出同一个矩形 /
  虚拟桌面原点在负方向时的换算 / 跨屏选区仍落在截图范围内 / 拖太小算误触。
- **真机行为不写自动化测试**：遮罩观感、框选跟手、结果窗、剪贴板 —— 任务书明确
  要求由用户实测（且"不碰桌面"是硬约束：不截图、不弹窗、不写剪贴板）。

---

## 3. 球菜单的入口

### 目的

球右键菜单里把「二维码扫描」加回来（K4b 因为"这轮不做"删掉了），
图标与样式照搬 Python 版。

### 照搬了什么

- **`capsule.html` L130-147 的 `#menuQr` SVG**（24×24 视框，三个定位角方块 +
  若干短线）→ `CapsuleWindow.axaml` 里一个 `Path`（17×17、`Stretch="Uniform"`、
  `StrokeThickness=2`、圆头圆角接），几何**逐段翻译**成 Avalonia 的路径语法
  （SVG 的 `<rect rx=1>` 译成带圆角弧的子路径）。
- **`capsule.css` L744-764 `.orb-menu-btn`**：34×34 的圆、`padding:0`、
  `border:0`、底色 `--state-bg`、hover 换 `--cancel-btn`；退出项 hover 才是警示色。
  → 按钮样式与现有的"设置"按钮完全一致；hover 走 `WireMenuButtonHover`（非退出项）。
- **菜单项顺序**：设置 / 二维码扫描 / 退出（与 Python 版一致）。
- **点击链路**：`CapsuleWindow.QrScanRequested` → `UiBinder.QrScanRequested`
  → `GuiHost.LaunchQrScanner()`。

### 没搬什么及替代写法

- 不用 SVG 的 `<rect>` 元素（Avalonia 的 `Path` 里没有 rect 语义）→ 译成路径子句。
- 不用 SVG 的 `h.01` / `v.01` 紧凑写法 → 改成等价的绝对命令（`H3.01` / `V21.01`），
  免得解析器对"小数点开头"的容错成为隐患。

### 测试结果

- `OrbMenuTests` 3 条全过：菜单打开时三项都在且**顺序一致**；
  二维码图标的**几何**落在 24×24 视框内（(3,3)–(21,21)）—— 路径抄错时会炸；
  点二维码按钮 → 窗口发出 `QrScanRequested`。
- 图标长相、hover 颜色不写测试（观感，不是规范）。

---

## 4. 与 Python 版的差异

| 分类 | 方面 | Python 版 | C# 版 | 为什么 |
|---|---|---|---|---|
| 工具 | UI 技术栈 | tkinter | WinForms | 截屏 + 全屏遮罩 + 框选，WinForms 最直接；不碰 Avalonia |
| 工具 | 解码库 | pyzbar（zbar 原生库） | ZXing.Net（纯托管） | 免安装原生 DLL；多格式对等 |
| 工具 | 截屏 | mss | `Graphics.CopyFromScreen` | WinForms 自带 |
| 工具 | 剪贴板 | win32 直写 | `Clipboard.SetText` | 独立进程，不需要主程序那套"owner 窗口"规矩 |
| 工具 | 依赖缺失 | 运行时探测 pip 包 → 弹窗 | 编译期依赖；运行期缺 DLL → 入口 catch → 弹窗 | C# 的依赖在编译期就定了 |
| 工具 | **一次解几个码** | zbar 一次能解多个（结果窗逐条列） | **只解一个**（ZXing 的 `MultiFormatReader` 是单码 API） | 见 §5 疑问 #1 |
| 入口 | 拉起方式 | `python -m qr_scanner`（要找解释器） | `Process.Start(QrScanner.exe)` | C# 不需要找解释器 |
| 入口 | 工具缺失时 | 弹窗（`_warn_dialog`） | 发一条通知「二维码扫描工具缺失：<路径>」 | C# 侧走通知中心（胶囊 / 面板可见） |
| 入口 | 工具放哪 | 源码树里的 `companions/qr_scanner/`（需要 `-m` 与 cwd） | 主程序输出目录下的 `companions/QrScanner/`（构建时复制） | 开发与发布两种形态都要能找到 |
| 球菜单 | 项 | 三项（设置 / 二维码 / 退出） | 三项（设置 / 二维码 / 退出） | 一致 |

---

## 5. 疑问清单

1. **一次解几个码**：Python 版能一次解多个码（结果窗逐条列出）；C# 版目前用
   `MultiFormatReader.decode`，**只解一个**。ZXing 有 `GenericMultipleBarcodeReader`
   / `BarcodeReaderGeneric.DecodeMultiple` 可以补上，但截图场景里一个框里通常只有一个码，
   多码检测反而容易误报。要不要补？（我倾向先不补，等实际遇到再说。）
2. **发布形态**：现在复制的是 **framework-dependent** 的 `QrScanner.exe` ——
   目标机器得装有 .NET 10 runtime 才能跑起来。要不要改成自包含发布
   （SelfContained）？代价是往 App 输出目录里多塞一份 runtime（几十 MB）。
3. **工具缺失时的提示方式**：现在是主程序发通知（胶囊 / 面板里能看见），
   Python 版是弹窗。通知会不会太轻（用户点完菜单没反应，容易以为程序卡住）？
4. **要不要也加到托盘菜单**：现在「二维码扫描」只在**球菜单**里（第二项，与 Python 版
   一致）。Python 版的托盘菜单里没有这一项，但托盘是更"随手可点"的地方 ——
   要不要在托盘里也加一项？
5. **二维码菜单项的 hover 观感**：现在与"设置"项完全同色（CSS 里就是同一套
   `.orb-menu-btn`）。要不要给它单独的 hover 配色？
