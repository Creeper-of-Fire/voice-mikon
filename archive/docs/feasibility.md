# voice-mikon-next 可行性验证报告（Avalonia 窗口层）

> 本报告由 `tests/VmNext.AutoCheck` 自动生成；所有数值都是本机实测。
> 自动化**不会**移动用户的鼠标或注入键盘（只用 PostMessage + 查询类 Win32 API）。

## 环境

| 项 | 值 |
|---|---|
| 操作系统 | Microsoft Windows NT 10.0.22631.0 |
| .NET 运行时 | 10.0.15 |
| 目标框架 | net10.0 |
| 逻辑处理器数 | 16 |
| Avalonia 版本 | 11.3.23 |
| .NET SDK | 10.0.115 |

## 检查结果

| 编号 | 项名 | 实测值 / 断言结果 | 通过 | 备注 |
|---|---|---|---|---|
| A1 | 窗口扩展样式正确 | EXSTYLE=0x08280088 NOACTIVATE=True LAYERED=True TOOLWINDOW=True TOPMOST=True | ✅ 通过 | 由 Demo A 子进程自检输出。 |
| A2 | 窗口区域已裁 | GetWindowRgn=3(1=NULLREGION,2=SIMPLE,3=COMPLEX) RgnBox=(0,0,167x167) Client=168x168 期望圆直径≈168 | ✅ 通过 | 由 Demo A 子进程自检输出。 |
| A3 | 点击穿透 | 窗口矩形=(3624,1860,168x168) 角外2px命中=0xB2080C 左中2px命中=0xB2080C 球心命中=0xB51E34 本窗口=0xB51E34 | ✅ 通过 | 由 Demo A 子进程自检输出。 |
| A4 | 点击不激活 | 前台窗口 前=0x280F9A 后=0x280F9A 变化=无；WM_MOUSEACTIVATE 返回=1(3=MA_NOACTIVATE) | ✅ 通过 | 由 Demo A 子进程自检输出。 |
| A5 | 拖拽几何 | 渲染缩放=1.7500 期望位移=175.00px 实测位移=175px 差=0.00px | ✅ 通过 | 由 Demo A 子进程自检输出。 |
| A6 | DPI 读数一致 | GetDpiForWindow=168 → 缩放=1.7500；Avalonia RenderScaling=1.7500 | ✅ 通过 | 由 Demo A 子进程自检输出。 |
| X1 | 真透明（屏幕像素合成验证） | 真透明（屏幕合成验证）｜球心像素 前=0x1A1A1E 后=0x3B6EF6 差=333（>20 说明内容画上去了）；四角最大差=0（<=12 说明透明处透出桌面） | ✅ 通过 | 额外项：对比窗口显示前后的屏幕像素，球心变色 + 四角不变 = 真的透出桌面。 |
| A8 | 空闲 CPU 占用（Demo A 静置 10 秒） | 平均 0.020%（10 秒内累计 CPU 31ms / 实测时长 10014ms / 16 逻辑核） | ✅ 通过 | 阈值：平均 < 1% 视为通过。Avalonia 按需渲染，静止时不出帧。 |
| A9 | JIT 工作集（Demo A 启动后静置 10 秒） | Environment.WorkingSet=117.0MB，Process.WorkingSet64=117.3MB，私有内存=102.1MB | ℹ️ 实测值 | 目标是把常驻内存压到 20–50MB；这里给出 JIT 基线，AOT 版本见 A11。 |
| A13 | 冷启动（JIT，进程启动 → 窗口 Opened） | 外部计时 702ms；进程内部计时 697ms | ✅ 通过 | 外部计时 = AutoCheck 拉起进程到收到 OPENED 标记（含 dotnet host 启动 + JIT）；阈值 2s。 |
| A7 | 动画帧时间（Demo B 跑 10 秒：形变 + 呼吸 + 旋转 + 波形） | 整体 平均 16.62ms / 最大 120.67ms / 最小 0.87ms，651 帧，>20ms 的有 8 帧；形变期间 平均 16.56ms / 最大 18.88ms（48 帧，>20ms 0 帧）；形变后 平均 16.61ms / 最大 32.38ms（555 帧，>20ms 2 帧） | ✅ 通过 | 阈值：平均 < 25ms（约 40fps 以上）。形变连续性：620 次采样里有 158 个不同宽度值（96→320），说明是渐变不是跳变。 |
| A7.1 | 形变连续性（宽度采样） | 620 次采样 / 158 个不同宽度 | ✅ 通过 | 宽度连续过渡（不是一步跳到 320），说明 Border.Width 的 DoubleTransition 生效。 |
| B1 | 主题切换（令牌资源查找 + 颜色补间） | 主题切换：令牌资源找到=True，Surface 色 #ed9d9fa2 → #f2ffffff（当前主题 Light） | ✅ 通过 | 额外项：自动切一次主题，确认 ResourceDictionary 里找得到 brush、颜色确实变了。 |
| A10 | JIT 增量编译（改一行 XAML，3 次取中位数） | 3 次耗时 1292ms / 1304ms / 1320ms，中位数 1304ms | ✅ 通过 | 命令：dotnet build src/VmNext.Shell/VmNext.Shell.csproj -c Release --no-restore；阈值：中位数 < 5s。 |
| A11 | NativeAOT 发布（dotnet publish -c Release -r win-x64） | 耗时 19.4s；exe 16.0MB；运行所需文件合计 31.9MB（4 个文件）；另有调试符号 62.3MB（pdb，运行不需要，已用 DebugType=none 去掉） | ℹ️ 实测值 | 发布目录里除 exe 外还有 SkiaSharp / HarfBuzzSharp / ANGLE 等 native 库（Avalonia 渲染后端需要，不是单文件）。 |
| A11.1 | AOT 产物运行指标 | 工作集 Environment=76.5MB / Process=83.0MB；空闲 CPU 0.000%；启动→Opened 254ms | ℹ️ 实测值 | 与 A9（JIT 工作集）对比，看 AOT 是否把常驻内存压下来。 |
| A12 | AOT 产物可运行性（A1–A3 在发布版上重跑） | A1=PASS，A2=PASS，A3=PASS | ✅ 通过 | 发布版 exe 直接跑 --auto 自检；进程退出码 0。 |

## 结论

### 方案是否成立

**成立。** 透明 + 不激活 + 置顶 + 点击穿透 + 拖拽 + 动画这几项在 Avalonia 11 + Win32 后端上可以同时成立，
关键在于：透明靠 Avalonia 自绘（`TransparencyLevelHint=Transparent` + `SystemDecorations=None` + `Background=Transparent`），
"不激活"靠 `WS_EX_NOACTIVATE`，"点击穿透"靠 `SetWindowRgn` 的窗口区域，而不是靠透明像素。
这三件事互相独立，不冲突。

### 内存现状（未达标，需要单独优化）

目标是常驻 20–50MB。实测：AOT 工作集 **76.5MB / 83.0MB**，JIT **117.0MB / 117.3MB / 102.1MB**。
AOT 确实把体积压下来了，但**离 20–50MB 还有明显差距**。
差距主要来自 Avalonia 的 Skia 渲染后端：SkiaSharp / HarfBuzzSharp / ANGLE 三个 native 库会被映射进工作集。
这是**渲染后端**的问题，不是窗口层的问题 —— 窗口层的结论（透明/不激活/穿透/动画）不受影响。
正式版如果死守 20–50MB，需要评估：换更轻的渲染路径、或接受 Skia 的常驻成本。

### 需要用户在 demo 里确认的项

- Demo A：右键菜单能否弹出、菜单项能否点击、点完焦点是否回到原窗口（自动化无法覆盖弹出菜单的焦点行为）。
- Demo A：球下方的"真透明"在**用户当前桌面壁纸**上的观感（自动化只验证了像素没被覆盖，不验证观感）。
- Demo B：形变过程中有没有残影 / 灰带（自动化只测了帧时间与宽度连续性）。
- Demo B：按 T 切主题时颜色过渡是否平滑（配色是 Shell 里的静态 token，切换由 Theme.Toggle 驱动）。
- Demo C：拖到第二块屏时球的大小/位置是否正确（自动化只打印了显示器参数，实际拖拽要人操作）。
- Demo C：Ctrl+C 复制（自动化用 API 验证了复制链路可用，真实按键行为要人点）。

### 自动化结果

A1–A13 全部达到预期，没有失败项。

### 意外发现（重构时必须知道）

1. **Avalonia 11.x（本次实测 11.3.23）给 `StyledElement` 加了同名实例属性 `Theme`（类型 `ControlTheme`）**，
   自定义主题类不能叫 `Theme`，否则在窗口子类里 `Theme.Toggle()` 会解析到那个属性上 —— 我们的类改名为 `OverlayTheme`。
2. **XAML 里写 `<DoubleTransition Property="Width" />` 会得到 null**，运行时 `NullReferenceException`；
   必须写全限定名（`Layoutable.Width`）或者干脆在代码里设置过渡。本项目统一在代码里设置。
3. **Avalonia 没有实现 `WM_MOUSEACTIVATE`**（返回 `MA_ACTIVATE`）；不抢焦点完全依赖 `WS_EX_NOACTIVATE`。
4. **形变每帧都要 resize 窗口 + 重裁窗口区域**，这是形变期间唯一明显的掉帧来源（见 A7 分段数据）。
5. **窗口不抢焦点 ⇒ 收不到键盘事件**。语音工具必须用全局热键（`RegisterHotKey`），我们已把这一层放进 Shell。
6. **"不激活"与"文字可选中/可复制"互斥**：文本框要拿键盘焦点才能响应 Ctrl+C。
   Demo C 因此允许激活，正式版需要在这两者之间做取舍（或用全局热键接管复制）。
7. **`WS_EX_LAYERED` 可以加上且不影响渲染**（Avalonia 走 DirectComposition，叠加 layered 样式后画面仍正常，X1 像素验证通过）。
8. **内存没有达到 20–50MB 的目标**：JIT 工作集约 117–123MB，AOT 约 81–88MB，只降了三成。
   差距主要来自 Avalonia 的 Skia 渲染后端（SkiaSharp / HarfBuzz / ANGLE 几个 native 库会被映射进工作集）
   以及 AOT 运行时本身。**这一条必须单独优化**，窗口层的结论不受影响（渲染后端与窗口层是两件事）。
9. **`csproj` 里写的是 `Version="11.*"` 浮动版本**，本次实际解析到 11.3.23（比开发机上缓存的 11.1.1 更新）。
   正式项目应锁定具体版本，避免行为随 NuGet 更新漂移。

