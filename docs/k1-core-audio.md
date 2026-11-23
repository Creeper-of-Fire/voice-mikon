# K1 —— 核心层：基础设施 + Win32 互操作 + 音频层（Python → C#）

> 本文是 **K1 阶段的过程记录**，现状以代码和 [`AGENTS.md`](../AGENTS.md) 为准。

翻译对象：`voice-mikon/src/voice_mikon/` 下的 12 个模块 → `voice-mikon-next/src/VmNext.Core/` 与
`src/VmNext.Audio/`。

**这一阶段的目标**：让 C# 版的核心层与音频层**行为与 Python 版一致**，不是重新设计一套。
做法是 Python 的逻辑一一对应到 C#，Python 注释里的"为什么"（踩坑记录）一并带进 C# 注释 ——
那些坑是真机踩出来的，照搬代码时一起照搬能省一轮返工。
凡是不改行为就不动；确实要改的地方下文每条都写了理由。
`voice-mikon/AGENTS.md` 的规矩是用户拍板的，逐条落地并写明理由。

---

## 1. 翻译对照表

### 配置与基础设施

| Python（`voice_mikon.`） | C# | 备注 |
|---|---|---|
| `config.Config` | `VmNext.Core.Config` | JSON 字段名与 Python 逐字一致（`hotkey` / `model_root` / `save_recordings` …） |
| `config.OrbConfig` | `VmNext.Core.OrbConfig` | |
| `config.Config.validate()` | `Config.Validate()` | 返回问题列表 |
| `config.Config.to_dict()` | `Config.ToDictionary()` | |
| `config.Config.from_dict()` | `Config.FromDictionary(IReadOnlyDictionary<string, JsonElement>)` | 未知键告警不崩 |
| `config.Config.save()` | `Config.Save()` | **不把绝对路径烧进 json**（日志路径派生自 `Paths.LogPath`） |
| `config.Config.update()` | `Config.Update()` | 忽略未知键 |
| `config.load_config()` | `Config.Load()` | 显式参数 > `data/config.json` > 默认值 |
| `config.load_hotwords_file()` | `Config.LoadHotwordsFile()` | |
| `config.current_config_path()` | `Config.CurrentConfigPath` | |
| `config.LEGACY_BACKEND_ALIASES` | `Config.LegacyBackendAliases` | `dashscope → local`，回退 + 一条 warning |
| `paths.data_dir()` | `Paths.DataDir` | `VOICE_MIKON_HOME` 覆盖 |
| `paths.config_path()` | `Paths.ConfigPath` | `VOICE_MIKON_CONFIG` 覆盖 |
| `paths.log_path()` | `Paths.LogPath` | `VOICE_MIKON_LOG` 覆盖 |
| `paths.hotwords_path()` | `Paths.HotwordsPath` | |
| `paths.recordings_dir()` | `Paths.RecordingsDir` | |
| `paths.webview_dir()` | `Paths.WebViewDir` | |
| `paths.ensure_dirs()` | `Paths.EnsureDirs()` | |
| `logging_setup.setup()` | `Log.Setup()` | 幂等；文件写不了不拖垮程序 |
| `logging_setup.get_logger()` | `Log.GetLogger()` | |
| `notify.Notification` | `VmNext.Core.Notification` | level / kind / message 三元组是契约 |
| `notify.NotificationHub.subscribe()` | `NotificationHub.Subscribe()` | |
| `notify.NotificationHub.publish()` | `NotificationHub.Publish()` | 出口 `data/notify.log` |
| `notify.notify`（全局单例） | `NotificationHub.Notify` | |

### 状态机

| Python | C# | 备注 |
|---|---|---|
| `core.state.AppState` | `VmNext.Core.AppState` | 不可变 record，`with` 表达式 ≈ `with_changes()` |
| `core.state.BackendHealth` | `VmNext.Core.BackendHealth` | `Ready` / `Failed` / `Loading` |
| `core.state.CommandResult` | `VmNext.Core.CommandResult` | `Rejected` ≈ Python 的 `rejected` |
| `core.state.STATES` / `HEALTH_STATUSES` | `AppStates.All` / `HealthStatuses.All` | |

### Win32 互操作

| Python（`win32.`） | C# | 备注 |
|---|---|---|
| `clipboard.ClipboardResult` | `VmNext.Core.Interop.ClipboardResult` | |
| `clipboard.read_text()` | `ClipboardService.ReadText()` | **(ok, text)** 两段信息：读失败 ≠ 没有文本 |
| `clipboard.get_text()` | `ClipboardService.GetText()` | |
| `clipboard.get_sequence_number()` | `ClipboardService.GetSequenceNumber()` | |
| `clipboard.set_text_verified()` | `ClipboardService.SetTextVerified()` | 写入后**关板再读回**校验 |
| `clipboard.set_text()` | `ClipboardService.SetText()` | |
| `clipboard.ensure_owner_window()` | `ClipboardService.EnsureOwnerWindow()` | owner 窗口**只在 owner 线程创建** |
| `clipboard.release_ownership()` | `ClipboardService.ReleaseOwnership()` | 转交所有权，内容留住 |
| `monitors.Monitor` | `VmNext.Core.Interop.Monitor` | |
| `monitors.list_monitors()` | `Monitors.ListMonitors()` | `EnumDisplayMonitors` 逐个枚举 |
| `monitors.monitor_at_point()` | `Monitors.MonitorAtPoint()` | |
| `monitors.monitor_by_device()` | `Monitors.MonitorByDevice()` | |
| `monitors.primary_monitor()` | `Monitors.PrimaryMonitor()` | |
| `dpi.set_dpi_awareness()` | `Dpi.SetDpiAwareness()` | 返回 `already`/`per-monitor-v2`/`per-monitor`/`system`/`none` |
| `api.send_ctrl_v()` | `Injector.SendCtrlV()` | SendInput，返回**实际投递**事件数 |
| `api.send_v()` | `Injector.SendV()` | |
| `api.held_modifiers()` | `Injector.HeldModifiers()` | |
| `api.key_is_down()` | `Injector.KeyIsDown()` | |
| `api.restore_foreground()` | `Injector.RestoreForeground()` | AttachThreadInput + SetForegroundWindow |
| `api.get_foreground_window()` | `Injector.GetForegroundWindow()` | |
| `api.get_window_title()` | `Injector.GetWindowTitle()` | |

### 注入

| Python（`core.inject`） | C# | 备注 |
|---|---|---|
| `InjectResult` | `VmNext.Core.Inject.InjectResult` | `Ok` / `Reason` / `TextInClipboard` |
| `paste_text()` | `Injector.PasteText()` | 剪贴板 + Ctrl+V；3 次重试；末尾补空格开关 |
| `paste_with_feedback()` | `Injector.PasteWithFeedback()` | |
| `_text_in_clipboard()` | `Injector.TextInClipboard()`（private） | 读回比对，不猜 |
| `_wait_modifiers()` | `Injector.WaitModifiers()`（private） | clear / ctrl / timeout |
| `_attempt_inject()` | `Injector.AttemptInject()`（private） | 粘贴已触发就绝不重试 |
| `_restore()` | `Injector.Restore()`（private） | 序号 + 内容双重归属守卫 |
| （controller.py 的失败文案） | `Injector.BuildFailureNotification()` | 文案逐字取自 `core/controller.py` |

### 电平

| Python | C# | 备注 |
|---|---|---|
| `core.levels.dbfs_to_unit()` | `VmNext.Core.Levels.LevelPump.DbfsToUnit()` | 对数映射 + 底噪门 |
| `core.levels.level_gate()` | `LevelPump.LevelGate()` | 门限 = max(下限, 底噪 × 1.5) |
| `core.controller._level_loop()` | `LevelPump.Start()` / `Stop()` | 30fps（`PushInterval = 1/30`） |
| `core.controller._mapped_levels()` | `LevelPump.Map()`（private） | 数据源由 `ILevelSource` 提供 |
| `core.controller.subscribe_levels()` | `LevelPump.Subscribe()` | |

### 音频

| Python（`audio.`） | C# | 备注 |
|---|---|---|
| `recorder.Recorder` | `VmNext.Audio.Recorder` | NAudio `WasapiCapture` |
| `recorder.Recorder.start()` | `Recorder.Start()` | |
| `recorder.Recorder.stop()` | `Recorder.Stop()` | 返回 `RecordingResult(int16 单声道, 原生采样率)` |
| `recorder.Recorder.snapshot()` | `Recorder.Snapshot()` | **不消费** |
| `recorder.Recorder.drain_chunks()` | `Recorder.DrainChunks()` | 增量游标 |
| `recorder.Recorder.levels()` | `Recorder.Levels` | |
| `recorder.Recorder.noise_floor()` | `Recorder.NoiseFloor` | 样本不足返回 null |
| `recorder.Recorder.elapsed` | `Recorder.Elapsed` | |
| `recorder.frames_to_int16()` | `Int16CaptureSource`（通道混音 + int16 转换） | |
| `recorder.is_recording()` | `Recorder.IsRecording`（static） | 后台探测让路 |
| `recorder.rms_of()` | `DevicePicker.RmsOf()` | |
| `devices.InputDevice` | `VmNext.Audio.InputDevice` | **索引口径不同，见疑问清单 #1** |
| `devices.list_inputs()` | `DevicePicker.ListInputs()` | |
| `devices.supported_rate()` | `DevicePicker.SupportedRate()` | WASAPI 下 = 设备 mix format（原生率） |
| `devices.probe()` | `DevicePicker.Probe()` | 开流看有没有数据 |
| `devices.choose_device()` | `DevicePicker.ChooseDevice()` | 显式 > 名称 hints > 实测最好 |
| `noise.NoiseFloorTracker` | `VmNext.Audio.NoiseFloorTracker` | 滑动窗口 p10 |
| `recording_store.write_wav()` | `RecordingStore.WriteWav()` | int16 单声道、小端 |
| `recording_store.prune_recordings()` | `RecordingStore.PruneRecordings()` | 只留最近 20 个 |
| `recording_store.save_recording()` | `RecordingStore.SaveRecording()` | `data/recordings/` |

### P/Invoke 落点

全部 Win32 声明集中在 `src/VmNext.Shell/NativeMethods.cs`（**唯一 P/Invoke 源**，K1 只追加、不改已有函数）：

- 剪贴板：`OpenClipboard` / `CloseClipboard` / `EmptyClipboard` / `GetClipboardData` / `SetClipboardData` /
  `GetClipboardSequenceNumber` / `GetClipboardOwner` / `GlobalAlloc` / `GlobalLock` / `GlobalUnlock` / `GlobalFree`
- 键盘注入：`SendInput` + `INPUT` / `INPUTUNION` / `KEYBDINPUT` / `MOUSEINPUT` / `HARDWAREINPUT`、
  `GetAsyncKeyState`、`KEYEVENTF_*`、`VK_CONTROL` / `VK_V` / `VK_MENU` / `VK_SHIFT` / `VK_LWIN` / `VK_RWIN`
- 焦点：`SetForegroundWindow` / `AttachThreadInput` / `GetCurrentThreadId` / `IsWindow` / `GetClassNameW`
- 显示器与 DPI：`EnumDisplayMonitors` / `GetMonitorInfoW` / `MONITORINFOEX` / `GetDpiForMonitor` /
  `GetDpiForSystem` / `SetProcessDpiAwarenessContext` / `SetProcessDpiAwareness` / `SetProcessDPIAware`
- 其它：`EnumChildWindows`、`SendMessage(string 缓冲区重载)`、`WM_GETTEXT` / `WM_GETTEXTLENGTH`

> ⚠ `NativeMethods.cs` 文件头原来的约定「绝不注入输入」被 K1 打了**有记录的例外**：
> 产品本身就是"把文字注入当前光标"的工具，必须能注入键盘事件；注入能力集中在
> `VmNext.Core.Inject.Injector` 一处，唯一的调用方式是「剪贴板 + Ctrl+V」且**必须读回校验**。
> 窗口层与验证壳仍然只做查询。

---

## 2. `dotnet test` 结果摘要

> ⚠ **运行方式**：MTP（Microsoft.Testing.Platform）项目在 .NET 10 SDK 上不能走 VSTest target，
> 官方 opt-in 是**在项目目录放 `global.json`**（`test.runner = Microsoft.Testing.Platform`）。
> 两个测试项目各有一份 `global.json`，所以要**进项目目录**跑：
>
> ```
> cd tests/VmNext.Core.Tests && dotnet test        # 或直接跑 bin\Debug\net10.0\VmNext.Core.Tests.exe
> cd tests/VmNext.Audio.Tests && dotnet test
> ```
>
> 从仓库根目录跑 `dotnet test tests\...` 不会命中那份 `global.json`，会报
> "Testing with VSTest target is no longer supported by Microsoft.Testing.Platform"。

> ⚠ **历史记录**：本节（以及 §3 的实测数字）记的是 **K1 当时**的结果。
> 下面提到的 `ClipboardIntegrationTests` / `InjectorIntegrationTests` /
> `RecorderIntegrationTests` 等**碰桌面的集成测试现已删除** —— 它们会写真实剪贴板、
> 占真实麦克风、往屏幕上弹窗，按「不碰桌面」的原则不再保留。
> 现在 `Core.Tests` / `Audio.Tests` 只剩纯计算的那几条。

| 测试项目 | 总数 | 通过 | 失败 | 跳过 | 耗时 |
|---|---|---|---|---|---|
| `VmNext.Core.Tests` | 23 | 22 | 0 | **1** | 15.1 s |
| `VmNext.Audio.Tests` | 10 | 10 | 0 | 0 | 4.2 s |

### 集成测试（`Category = Integration`）逐条状态

| 测试 | 结果 | 说明 |
|---|---|---|
| `ClipboardIntegrationTests.写入后能读回且owner是本进程` | ✅ 通过 | 写入 → 读回一致 → owner PID == 本进程 → `release_ownership` 后内容仍在 |
| `ClipboardIntegrationTests.归还开关关闭时不还而且没有归还那次写入` | ✅ 通过 | `restore=false` → 等 2.5 s，剪贴板仍是识别文本（原内容没被还回来，也没有那次写入） |
| `ClipboardIntegrationTests.归还开关打开时原内容会被还回去` | ⏭ **skip** | 环境限制：本机 `SetClipboardData` 被 **1418** 拒绝（详见疑问清单 #4）。归还**判定**本身已由日志确认（序号一致、内容一致），只是最后那一次写回被系统拒绝 |
| `ClipboardIntegrationTests.连续写入剪贴板都能成功` | ✅ 通过 | 连写 4 次都成功 |
| `ClipboardIntegrationTests.粘贴消费后还能不能写` | ✅ 通过 | 粘贴之后 0.3 s / 3.3 s 各写一次，都成功 |
| `ClipboardIntegrationTests.读不到内容与读失败是两回事` | ✅ 通过 | `(ok, text)` 两段信息 |
| `InjectorIntegrationTests.注入的文本能被记事本读回` | ✅ 通过 | 目标窗口 = 自建 Win32 EDIT 控件（原因见疑问清单 #3），`WM_GETTEXT` 读回 **"测试文本"** |
| `InjectorIntegrationTests.空文本直接判失败不碰剪贴板` | ✅ 通过 | `empty_text` |
| `InjectorIntegrationTests.失败通知文案按实况分类` | ✅ 通过 | warning / error 三档文案逐字对齐 Python |
| `RecorderIntegrationTests.录两秒返回约两秒原生采样率的音频` | ✅ 通过 | 有麦克风 |
| `RecorderIntegrationTests.快照不消费增量游标能取到新增部分` | ✅ 通过 | 快照 25600 样本 = 首次增量 25600 样本；再取增量 = 空 |

**跳过的唯一一条**是"归还开关=打开"档，原因是本机剪贴板状态（不是移植引入的缺陷，Python 版同样受限）。

### 单元测试

`Config`：默认值逐项一致 / 最小片段只覆盖出现的键 / 未知键被忽略不崩 / 类型不对的键用默认值顶上 /
save→load 往返 / 读不到配置文件回退默认值 / 退役后端名回退 local / 术语表读取去重。
`LevelPump`：30fps 节奏（1 s ≈ 30 次）/ 停止后不再推送 / 底噪门判零 / 对数映射区间 / 门限随底噪走。
`NoiseFloor`：样本不足返回 null / p10 收敛 / 非有限值被忽略 / 窗口只留最近样本。
`RecordingStore`：WAV 头逐字段正确（int16 单声道 + 原生采样率）/ 只留最近 20 个 / 空音频不落盘。

---

## 3. 集成测试实测的关键数字

| 项目 | 实测值 |
|---|---|
| **native_rate**（设备原生采样率） | **48000 Hz**（不是硬编码的 16000） |
| 2 秒录音的样本数 | **92160**（期望 2 × 48000 = 96000，达成 96 %） |
| 录音 RMS | **0.004142** |
| 底噪估计（p10） | **0.002064** |
| 电平帧数（窗口 48） | 48 |
| **注入读回内容** | **"测试文本"**（`WM_GETTEXT` 从目标窗口的 EDIT 控件读回，逐字相等） |
| **剪贴板 owner PID** | **40744**，与本进程 PID **40744** 相同 ✓ |
| owner 窗口句柄 | `0x18D1786`（message-only，`HWND_MESSAGE` 父） |
| 本机输入设备（NAudio 枚举） | `[0] 麦克风 (Steam Streaming Microphone)` 44100/虚拟 · `[1] 麦克风 (3- USB PnP Audio Device)` 48000 · `[2] 麦克风阵列 (网易虚拟音频设备)` 48000/虚拟 · `[3] 耳机 (2- EDIFIER W800BT Plus)` 16000 |

> 设备列表里那个 **EDIFIER W800BT Plus 声称 16000 Hz** —— 正是 Python 注释里点名的那只
> "声称支持 16 kHz、WASAPI 共享模式下却悄悄降速"的蓝牙耳机。Recorder 自动挑的是
> USB 麦克风（48000 Hz）。

---

## 4. 疑问清单

每条：**原文 → 我的处理 → 为什么**。

### #1 设备索引口径变了（最要紧的一条）

- **状态**：**仍然成立**。NAudio 只有采集端点列表，`InputDevice.Index` 就是列表内序号；旧 `config.json` 的 `input_device` 数值仍然不能沿用（首次要重选，或留 `null` = 自动挑选）。
- **原文**：`audio/devices.py` 用 `sd.query_devices()` 的**全局设备索引**（输出设备也占号），
  `config.input_device` 存的就是这个号；`choose_device(explicit_index=...)` 也按它比对。
- **我的处理**：NAudio 只有采集端点列表，`InputDevice.Index` = **采集端点列表内的序号**
  （本机 0..3）；`DevicePicker.ChooseDevice(explicitIndex)` 按新口径比对。
- **为什么**：NAudio 没有"全局设备索引"这个概念。**后果是旧 `data/config.json` 里的
  `input_device` 数值不能直接沿用**（Python 的全局号与 C# 的列表序号不是一回事），
  用户需要重选一次设备。字段名保持不变，JSON 仍然兼容。

### #2 `LevelPump` 在 Python 里没有直接对应物

- **状态**：**仍然成立**。`Core` 依然不引用 `Audio`（`ILevelSource` / `IRecorder` 窄接口），依赖方向没变。
- **原文**：`core/levels.py`（68 行）只有 `dbfs_to_unit` / `level_gate` 两个纯函数；
  30 fps 的电平泵在 `core/controller.py`（`_level_loop` / `_mapped_levels`，属 K3 的范围）。
- **我的处理**：`LevelPump` = 两个纯函数 + 30 fps 泵（`PushInterval = 1/30`）+ 计时，
  数据源抽成接口 `ILevelSource`（`Levels` / `NoiseFloor` / `Elapsed`），由 `VmNext.Audio.Recorder` 实现。
- **为什么**：K1 要求 Core 里有"30fps 电平 + 计时的事件推送"，而 **Core 不能引用 Audio**
  （Audio 引用 Core，反过来会成环）。窄接口是唯一不产生依赖环的写法。

### #3 注入验证目标：Windows 11 的新记事本没法自动化

- **状态**：**已解决，不再适用**。K3 改用「自建 Win32 编辑窗口 + 独立线程消息循环」落地了同一件事；相关碰桌面的集成测试（`InjectTarget.cs` 等）已按「不碰桌面」原则删除 —— 本条只留作背景知识。
- **原文**：验收要求"启动 Notepad → 等到窗口 → 设前台 → 注入 → 用 `WM_GETTEXT` 读 Edit 控件"。
- **我的处理**：默认目标是**自建的最小 Win32 编辑窗口**（顶层窗口 + `EDIT` 控件，跑在
  **独立线程 + 消息循环**上）；真记事本保留在 `VMNEXT_TEST_TARGET=notepad` 开关下。
- **为什么**：新记事本是 packaged 单实例应用 —— ① `notepad.exe` 只是转发器，窗口不挂在
  notepad.exe 进程上（实测新进程 `MainWindowHandle` 恒为 0）；② 请求会转给**已经开着**的实例，
  在用户正在编辑的文档里插标签（不能接受）；③ 即使拿到窗口，它的编辑区不是传统
  Edit/RichEdit 控件，`WM_GETTEXT` 读回**空串**。写字板在本机已被弃用（`write.exe` 启动即退出）。
  自建窗口同样是真实 Win32 编辑控件，验证的是同一件事：Ctrl+V 有没有把文字送进目标窗口。
  另外**必须有消息循环**：没有它，SendInput 的按键进了队列没人取，粘贴永远不会发生
  （这是调试过程中撞出来的，已写进注释）。

### #4 归还原内容在本机会被系统拒绝（1418）

- **状态**：**仍然成立**（本机环境限制，不是移植引入的缺陷）。写入重试（3 次）留在 `ClipboardService.SetTextVerified` 里；相关集成测试已删除。
- **原文**：`core/inject.py` 的 `_restore()` → `clipboard.set_text(original)`。
- **我的处理**：**目的是"借板用完还原，原内容一个字不能丢"** —— 照搬了 Python 的归还逻辑
  （先分配、后清板）；另外给 `SetTextVerified` 加了**写失败重试**（3 次，间隔
  0.5 / 1.0 / 1.5 s，因为 1418 常是瞬时的）；本机仍失败 → 集成测试该档 skip 并写明原因。
- **为什么**：本机 `SetClipboardData` 返回 **1418（ERROR_NOT_ENOUGH_QUOTA）** —— 剪贴板被
  "粘贴消费"之后进入 OLE / 延迟渲染状态，普通写入被拒。用 **Python 版做过对照实验**：
  连写 4 次 ✓、读后再写 ✓、后台线程写 ✓ 全部成功；只有"粘贴消费 → 延迟写回"这条路被拒。
  所以这是**平台/环境特性，不是移植引入的缺陷**（Python 版在这台机器上同样写不回去）。

### #5 `NativeMethods.cs` 的"绝不注入输入"约定需要例外

- **状态**：**仍然成立**。注入能力仍集中在 `VmNext.Core.Inject.Injector`，唯一调用方式是「剪贴板 + Ctrl+V」且必须读回校验。
- **原文**：文件头第 1 条："只做查询与窗口样式/区域设置，绝不注入输入。禁止 SendInput …"
- **我的处理**：加了**有记录的例外**并写明理由：注入能力集中在 `VmNext.Core.Inject.Injector`
  一处，唯一调用方式是「剪贴板 + Ctrl+V」且**必须读回校验**；窗口层与验证壳仍然只做查询。
- **为什么**：产品本身就是"把文字注入当前光标"的语音输入工具 —— 没有注入就没有产品。
  那条约定是**重构骨架期**为了"本机验证期间不要劫持用户键鼠"而立的，现在有了明确归属。

### #6 配置值类型不匹配时的行为

- **状态**：**仍然成立**。`Config.FromDictionary` 仍是「warning + 用默认值顶上 + 不崩」。
- **原文**：`Config.from_dict` 不校验类型（`sample_rate` 写成字符串会一路带着，直到用的时候才炸）；
  `OrbConfig.from_dict` 对未知子键会 warning。
- **我的处理**：已知键类型对不上 → **warning + 用默认值顶上 + 不崩**；未知键 → warning + 忽略。
- **为什么**：与"忽略未知键，避免旧配置文件导致崩溃"的既定思路一致；但**不能静默** ——
  warning 里点名是哪个键，用户才知道要改哪里。

### #7 测试框架：xunit v2 → v3

- **状态**：**部分过时**。`Core.Tests` / `Audio.Tests` / `Engines.Tests` 用 xunit v3 + 项目目录里的 `global.json`；但 `tests/VmNext.Ui.Tests` 仍是 **xunit v2**（`Avalonia.Headless.XUnit` 只支持 v2），而且它没有 `global.json`。
- **原文**：任务写的是 `xunit` + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`。
- **我的处理**：用 `xunit.v3` 4.0.3 + `xunit.runner.visualstudio` 4.0.1 + `Microsoft.NET.Test.Sdk` 17.12.0，
  每个测试项目目录放一份 `global.json`（`test.runner = Microsoft.Testing.Platform`）。
- **为什么**：集成测试必须能**运行时** skip（没有麦克风 / 没有目标窗口 / 环境受限）。
  xunit v2 的 `Assert.Skip` 需要 v3 的 runner 基础设施才生效（v2 下动态 skip 不可用）；
  另外 .NET 10 SDK 上 MTP 项目不再支持 VSTest target，`global.json` 是官方 opt-in 方式
  （放在项目目录，避免影响其它 worker 的项目）。

### #8 "主线程"在 C# 里没有对应概念

- **状态**：**仍然成立**。`ClipboardService` 构造时记录 owner 线程，`EnsureOwnerWindow()` 只在 owner 线程建窗口。
- **原文**：`clipboard._is_main_thread()` = `threading.current_thread() is threading.main_thread()`；
  非主线程**不建** owner 窗口，只复用。
- **我的处理**：`ClipboardService` 构造时记录 **owner 线程**（构造它的那个线程）；
  `EnsureOwnerWindow()` 只在 owner 线程创建窗口，其它线程只复用（拿不到就返回 0 → 降级为无 owner，
  功能不受影响，只是第三方剪贴板管理器看不到来源）。
- **为什么**：C# 没有 main_thread 概念，但**规矩本身必须保留**（owner 窗口属于创建它的线程，
  线程退出窗口就没了，而 Windows 在 owner 窗口销毁时连带清空剪贴板）。生产环境在主线程构造
  `ClipboardService` 即可；测试里测试线程扮演主线程。

### #9 WASAPI 共享模式没有"选采样率"这回事

- **状态**：**仍然成立**。`DevicePicker.SupportedRate()` 取设备 mix format，拿不到时兜底 48000。
- **原文**：`devices.supported_rate()` 从高到低试候选（48000 / 44100 / 32000 / 22050 / 16000）。
- **我的处理**：`DevicePicker.SupportedRate()` 直接取设备的 **mix format** 采样率；
  拿不到时兜底 48000（Python 兜底 16000）。
- **为什么**：共享模式下流格式由系统的共享格式决定，取到的**就是** Python 注释里说的
  "设备原生采样率"—— 少一层探测，也少一次失败的可能。

### #10 顺手修了一个真问题：转交所有权时的顺序

- **状态**：**仍然成立（已修）**。多余的那次 `EmptyClipboard()` 已去掉，归还走「先分配、后清板」。
- **原文**：`clipboard._relinquish_ownership()` 先 `EmptyClipboard()`，再 `_write_text_locked(text)`
  （而 `_write_text_locked` 内部**又**会 EmptyClipboard 一次）。
- **我的处理**：去掉多余的那次 `EmptyClipboard()`，直接走"先分配、后清板"的写入。
- **为什么**：先清板再写，一旦写失败（本机就是 1418），**用户剪贴板的内容就永久没了** ——
  "内容留着"的承诺就废了。`_write_text_locked` 内部那套"分配失败时板不动"的顺序本来就是安全的，
  多出来的那次清板是纯风险。

### #11 录音的回调模型（NAudio 与 PortAudio 不同）

- **状态**：**仍然成立**。NAudio 事件驱动 → 混音入队 → 按 1024 样本凑块。
- **原文**：`sounddevice.InputStream(blocksize=1024, callback=...)`，回调里 append 帧。
- **我的处理**：NAudio 的 `WasapiCapture` 是**事件驱动**（`DataAvailable`）→ 事件里把数据混音成
  int16 单声道入队 → 采集线程按 **1024 样本**凑块（块大小对齐 Python 的 1024）。
- **为什么**：**目的是让喂进引擎的音频粒度与 Python 版一致** —— 回放工具要按**同样的粒度**
  切音频喂引擎，否则复现不出真实的喂入节奏。NAudio 没有"指定 blocksize 的流回调"，
  所以在 NAudio 的事件模型里自己凑块。

### #12 其它小差异（不影响行为）

- **状态**：**仍然成立**。三条都是实现细节差异，不改变行为。
- Python `frames_to_int16` 支持 float 输入并按 `*32768` 缩放；C# 侧输入恒为 int16（WASAPI 混音后），
  缩放系数用 NAudio 口径（`*32767`）并 clamp。
- Python 的 `NoiseFloorTracker` 用无锁 `deque`；C# 用锁 —— 行为等价。
- Python 的 `_restore` 用 daemon thread；C# 用 `Task.Run`（线程池）—— 都会在进程退出时被放弃，行为等价。

---

## 5. NAudio 实际版本

| 项 | 值 |
|---|---|
| `NAudio` | **2.2.1**（NuGet，随 `NAudio.Core` / `NAudio.Wasapi` / `NAudio.WinMM` / `NAudio.Asio` / `NAudio.Midi` 拆分包） |
| 只用到 | `NAudio.CoreAudioApi`（`MMDeviceEnumerator` / `MMDevice` / `DataFlow` / `DeviceState`）、`NAudio.Wave`（`WasapiCapture` / `WaveInEventArgs`） |
| `WasapiCapture` 所在命名空间 | `NAudio.CoreAudioApi`（**不是** `NAudio.Wave`）—— 实测踩过 |
| 引用位置 | `src/VmNext.Audio/VmNext.Audio.csproj` |

---

## 6. 产出文件清单

**新增（Core）**
`src/VmNext.Core/`：`VmNext.Core.csproj`、`Paths.cs`、`Log.cs`、`Notifier.cs`、`Config.cs`、`State.cs`、
`Interop/ClipboardService.cs`、`Interop/Monitors.cs`、`Interop/Dpi.cs`、`Inject/Injector.cs`、`Levels/LevelPump.cs`

**新增（Audio）**
`src/VmNext.Audio/`：`VmNext.Audio.csproj`、`Recorder.cs`、`DevicePicker.cs`、`NoiseFloor.cs`、
`RecordingStore.cs`、`Int16CaptureSource.cs`

**追加（Shell，只追加不改已有函数）**
`src/VmNext.Shell/NativeMethods.cs`：剪贴板 / 键盘注入 / 焦点 / 显示器与 DPI / 消息循环 相关声明

**测试**（其中带 `Integration` 的三个文件**现已删除** —— 碰桌面的测试）
`tests/VmNext.Core.Tests/`：`ConfigTests.cs`、`LevelPumpTests.cs`、`ClipboardIntegrationTests.cs`、
`InjectorIntegrationTests.cs`、`InjectTarget.cs`（注入验证目标：真记事本 or 自建 Win32 编辑窗口）
`tests/VmNext.Audio.Tests/`：`NoiseFloorTests.cs`、`RecordingStoreTests.cs`、`RecorderIntegrationTests.cs`

**未动**：`voice-mikon/`（只读）、`archive/`（冻结）、`VmNext.sln`（由主代理统一加项目）

