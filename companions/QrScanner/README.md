# 二维码扫描（附属工具）

悬浮球右键菜单「二维码扫描」拉起的小工具：**截屏 → 全屏框选 → 解码 → 结果窗（自动复制）**。

C# 重写版（`voice-mikon-next`）。Python 版在 `voice-mikon/companions/qr_scanner/`（tkinter + mss + pyzbar），
两边各跑各的，**互不认识**。

## 架构边界（为什么独立）

- 与语音主程序 `VmNext.*` **零 import**：双向都不引用对方的程序集。
  - 本项目**不 ProjectReference 任何 `VmNext.*` 项目**；
  - `VmNext.App` 那边是 `ReferenceOutputAssembly="false"` 的 build-only 引用 ——
    **只用来定构建顺序 + 复制产物，不产生任何代码引用**（复制不是引用）。
- 本项目在 `VmNext.sln` 的 `companions` solution folder 里（**不进 `src/`**）。
- 主程序侧只有一根线：菜单点击 → `Process.Start` 拉起本工具（**独立进程**）。
  工具崩溃 / 被强杀不影响语音主程序；工具缺失时主程序发一条通知报路径。
- UI 走 WinForms（选区 + 结果窗），**不碰 Avalonia** —— 不与主程序的透明 / 裁形那套纠缠，
  附属工具没必要陪跑。
- 坐标全程物理像素：进程先声明 per-monitor v2，`SystemInformation.VirtualScreen` /
  `CopyFromScreen` / 鼠标事件坐标一套坐标走到底。

## 单独使用

```powershell
dotnet build companions/QrScanner/QrScanner.csproj
dotnet run --project companions/QrScanner            # 截屏 → 框选 → 识别
dotnet run --project companions/QrScanner -- --image a.png   # 直接识别一张图片
dotnet run --project companions/QrScanner -- --selftest      # 不弹窗：截屏 + 全图解码
```

> `dotnet run` 跑 WinExe 时 PowerShell 不会等它 —— 要看 `--selftest` 的输出请用
> `Start-Process -Wait -PassThru`。

## 依赖

| 依赖 | 是什么 | 为什么 |
|---|---|---|
| `ZXing.Net`（NuGet，纯托管） | 解码 | 对等 Python 版的 pyzbar / zbar，多格式（QR / 条形码 / DataMatrix …）。对 net6.0+ **无传递依赖** |
| WinForms（`net10.0-windows`） | 遮罩 / 框选 / 结果窗 | 截屏 + 全屏遮罩 + 框选，WinForms 最直接 |

## 文件

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口与编排（DPI 声明、`--image` / `--selftest`、异常弹窗） |
| `Core/ScreenCapture.cs` | 截取整个虚拟桌面（`CopyFromScreen`） |
| `Core/SelectionGeometry.cs` | 框选几何（**纯函数**：起点 / 终点 → 矩形） |
| `Core/ImageOps.cs` | 统一 32bpp / 裁剪 / 反色 / 放大 / 取像素 |
| `Core/QrDecoder.cs` | ZXing 解码；原图 → 反色 → 放大一倍逐级补救 |
| `Core/ClipboardWriter.cs` | 剪贴板写入（独立进程，不带 owner 窗口） |
| `Ui/SelectorForm.cs` | 全屏选区窗：变暗底图 + 框内贴回原图 + 尺寸标签 |
| `Ui/ResultForm.cs` | 结果窗 + 自动复制 |

## 与 Python 版的对应关系

| Python 版 | C# 版 | 说明 |
|---|---|---|
| `capture.py`（mss） | `Core/ScreenCapture.cs` | 都是截整个虚拟桌面；mss → `Graphics.CopyFromScreen` |
| `selector.py`（tkinter） | `Ui/SelectorForm.cs` + `Core/SelectionGeometry.cs` | 遮罩观感与选区算法照搬，UI 换成 WinForms |
| `decode.py`（pyzbar） | `Core/QrDecoder.cs` | 同样的「原图 → 反色 → 放大一倍」补救顺序；zbar → ZXing.Net |
| `result.py`（tkinter） | `Ui/ResultForm.cs` + `Core/ClipboardWriter.cs` | 同样"自动复制 + 不自动关窗"；win32 直写 → `Clipboard.SetText` |
| `__main__.py` | `Program.cs` | 同样的三个入口（默认 / `--image` / `--selftest`） |

## 已知取舍

- 解码只做轻补救（反色 / 放大）：截图场景的码清晰正对，一次就中；小码请框紧一点。
- 结果窗不自动关闭：读完自己关（Esc / 关闭按钮）。复制已经落到系统剪贴板，关了也不丢。
- 工具是**独立进程**：主程序不引用它，也不知道它的死活。
