# voice-mikon-next

[voice-mikon](../voice-mikon/)（Windows 全局语音输入，Python + pywebview）的 Avalonia 重写。
**进行中**：目前只有窗口层骨架，UI 尚未开始。

技术栈已定：C# / Avalonia 11 / .NET 10 / NativeAOT。选型依据是 `archive/` 里的可行性验证。

## 目录

```
src/VmNext.Shell/    窗口层：透明 / 不激活 / 置顶 / 穿透 / DPI 的 Avalonia 窗口类 + Win32 P/Invoke
archive/             2026-11-23 的可行性验证壳（冻结，可复现；UI 不是设计参考）
docs/                设计翻译笔记（待产出）
```

## 设计真相源 —— 翻译，不是设计

正式 UI **不在本仓库里"设计"**，而是从 voice-mikon 的现有前端翻译：

| 真相源 | 内容 |
|---|---|
| `voice-mikon/src/voice_mikon/host/static/capsule.css` | 色值 token、尺寸、动画参数，以及**每个值旁边的"为什么"** |
| `capsule.html` / `capsule.js` | 结构与交互 |
| `voice-mikon/src/voice_mikon/host/placement.py` | 球 / 窗口摆位常量（`ORB_SIZE`、`ORB_INSET_Y` 等） |
| `panel.css` / `panel.html` / `panel.js` | 设置面板 |

翻译规则：

- `:root` 的 token → `ResourceDictionary`；深浅两套值一字不改；`prefers-color-scheme` 对应"跟随系统"档
- `.recording-pill` 的 `transition` → 代码里设 `Transitions`（XAML 里 `Property="Width"` 会解析成 null，见 archive 报告意外发现 #2）
- `cubic-bezier(.25,.85,.3,1)` → `CubicBezier` 的四个数原样填
- CSS 注释里的"为什么"一并带进 XAML / C# 注释——那些是真机换来的，比数字本身值钱
- **不自己定尺寸、色值、动画时长。** 拿不准的值去 `capsule.css` 查，不是凭感觉写

## Shell 当前提供什么

| 文件 | 内容 |
|---|---|
| `NativeMethods.cs` | 全部 Win32 P/Invoke，集中在这一处 |
| `OverlayWindow.cs` | 窗口类：`WS_EX_NOACTIVATE / TOOLWINDOW / TOPMOST / LAYERED`、`SetWindowRgn` 区域跟随客户区、DPI 变化重算、逻辑像素移动、运行时切换是否允许激活。**零几何常量** |
| `HotKeyWindow.cs` | `RegisterHotKey` 全局热键。不激活窗口收不到键盘事件，热键是必需品 |
| `OverlayGeometry.cs` | 逻辑 → 物理像素、圆角区域几何 |
| `Theme.cs` | 主题机制：资源查找 + 就地改 `brush.Color` 的颜色补间。**不含色值、不含档位** |

**不提供**（正式版另有归属）：摆位逻辑（从 `placement.py` 翻译）、任何尺寸 / 色值 / 动画参数（从 `capsule.css` 翻译）、业务。

## 已知约束（来自 archive 验证，与 UI 设计无关）

- **不激活 ⇔ 文字选不了 / 复制不了**。voice-mikon 原设计是按文字条时临时激活（`capsule.py` 的 `focus_textbar`），对应 `OverlayWindow.SetAllowActivation(true)`
- 形变每帧 resize 窗口 + 重裁区域，是形变期唯一掉帧来源（实测 60fps 无掉帧，但是个负担）
- Avalonia 11.x：自定义类不能叫 `Theme`（与 `StyledElement.Theme` 撞名）；XAML 的 `Transition.Property` 要全限定名或代码设置
- 不抢焦点的窗口收不到键盘事件，所有按键都走全局热键
- 内存：AOT 工作集约 80 MB（Skia / HarfBuzz / ANGLE），JIT 约 117 MB；相对原 Python 版的 300–500 MB 降了 4–6 倍

## 构建

```
dotnet build VmNext.sln -c Release
```

archive 的复现方法见 `archive/README.md`。
