# K3 —— 核心层：状态机 + 编排 + presenter + headless 入口（Python → C#）

翻译对象：`voice-mikon/src/voice_mikon/core/controller.py`（1058 行）、
`core/state.py`、`apps/voicebar/protocol.py`、`apps/voicebar/presenter.py`、
`host/hotkey.py`、`__main__.py`（装配部分）
→ `voice-mikon-next/src/VmNext.Core/Controller/`、`src/VmNext.Core/Presentation/`、
`src/VmNext.App/`、`src/VmNext.Audio/RecorderAdapter.cs`。

原则同 K1 / K2：**翻译不是重写**。Python 的逻辑一一对应到 C#，Python 注释里的
"为什么"（踩坑记录）一并带进 C#；不优化、不改进、不凭感觉改行为。
`voice-mikon/AGENTS.md` 的规矩逐条落地并写明理由。

**UI 阶段的接口依据是 [`docs/core-api.md`](./core-api.md)**，本文件是翻译笔记。

---

## 1. 翻译对照表

### 1.1 控制器 —— `src/VmNext.Core/Controller/CoreController.cs`（+ `.Recording.cs` / `.Backend.cs`）

| Python（`core/controller.py`） | C# | 备注 |
|---|---|---|
| `class CoreController` | `VmNext.Core.Controller.CoreController`（partial，三份文件） | |
| `class State`（内部状态机） | `enum ControllerState` | Starting / Idle / Recording / Processing / Error |
| `_APP_STATE_BY_V1` | `SetState()` 里的 switch | ⚠ **ERROR 没有对外状态**，对外仍是 idle |
| `__init__(config, injector, recorder_factory, backend_factory, notifier)` | `CoreController(config, backendFactory, recorderFactory, injector, notifier, recordingSaver, micProbe, clipboard)` | 多两个装配口：`recordingSaver`（录音留存）、`micProbe`（麦克风探测）—— Core 不能引用 Audio 层（见疑问 #3） |
| `subscribe(cb)` | `Subscribe(Action<AppState>)` | 新订阅者**立即**收到当前快照一次 |
| `subscribe_levels(cb)` | `SubscribeLevels(Action<LevelFrame>)` | |
| `state_snapshot` | `StateSnapshot` | |
| `_update_state(**changes)` | `UpdateState(Func<AppState,AppState>)` | 没变化就不推 |
| `_set_state(state)` | `SetState(ControllerState)` | |
| `_notify(level, kind, message, **fields)` | `Notify(level, kind, message, fields)` | |
| `_reject(kind, reason, message)` | `Reject(kind, reason, message)` | |
| `normalize_backend(kind)` | `NormalizeBackend(kind)` | 认不出来 → 本地 + 一条 warning |
| `_default_backend_factory` | `DefaultBackendFactory` | |
| `_current_model_tag()` | `CurrentModelTag()` | 本地给 `config.model`（0.6B/1.7B），网络给远端模型名 |
| `_unload_backend(backend)` | `UnloadBackend(backend)` | |
| `start()` | `Start()` | 两个后台线程：`model-load` / `mic-warmup` |
| `_init_model()` | `InitModel()` | 失败 → `backend_load_failed` + health=failed |
| `_init_mic()` | `InitMic()` | 失败 → `microphone_unavailable`（warning，不阻断） |
| `toggle_recording()` | `ToggleRecording()` | |
| `start_recording()` | `StartRecording()` | |
| `stop_recording()` | `StopRecording()` | |
| `cancel_recording()` | `CancelRecording()` | |
| `_make_chunk_puller(recorder)` | `MakeChunkPuller(recorder)` | 优先增量，退回快照差量 |
| `_make_preview_engine(...)` | `MakePreviewEngine(...)` | 分句 / 整段由 `PreviewEngine` 常量选 |
| `_make_final_engine(...)` | `MakeFinalEngine(...)` | **固定整段引擎** |
| `_make_partial_callback(session)` | `MakePartialCallback(session)` | 必须过 session 检查 |
| `_start_preview` / `_stop_preview` / `_preview_loop` | 同名 | 预览线程最多等 2 秒 |
| `_is_stale(session)` | `IsStale(session)` | `_cancelled` + session 代号 |
| `_process(audio, rate, session)` | `Process(...)` | 识别 → 注入 → 失败文案 |
| `_start_level_pump` / `_stop_level_pump` / `_level_loop` / `_mapped_levels` | `StartLevelPump` / `StopLevelPump` | 映射与 30fps 泵在 K1 的 `LevelPump` 里；控制器只负责**每轮现造泵**并转发 |
| `switch_model(tag)` | `SwitchModel(tag)` | 后台线程 |
| `switch_backend(kind)` / `_switch_backend_job(kind)` | `SwitchBackend` / `SwitchBackendJob` | 先加载新的 → 再卸旧的 |
| `attach_hotkey(listener)` | `AttachHotkey(Action stop)` | |
| `shutdown()` | `Shutdown()` | **热键第一个停** |
| — | `ReloadMic()` | 托盘「重新探测麦克风」那条路（Python 在 `__main__.py` 里，本层收口） |
| — | `StartMaxDurationTimer()` / `StopMaxDurationTimer()` | ⚠ **Python 没有实现**（见疑问 #2） |

### 1.2 显示协议 —— `src/VmNext.Core/Presentation/DisplayProtocol.cs`

| Python（`apps/voicebar/protocol.py`） | C# | 备注 |
|---|---|---|
| `MODE_*`（9 个） | `DisplayModes.*` | 字符串**逐字一致** |
| `MODE_ERROR_WITH_TEXT` | `DisplayModes.ErrorWithText` | `error_terminal+has-text` |
| `DWELL_SECONDS` | `DisplayProtocol.DwellSeconds` | 值逐字一致（含 `None` → `null`） |
| `DWELL_SECONDS.get(key)` | `DisplayProtocol.DwellFor(key)` | |
| payload 的四个字段说明 | `DisplayPayload`（Message / Text / CanCopy / Reason + Warn / Body） | `Warn`/`Body` 是"等松键"瞬态覆盖那一条（Python 那边传的是另一个 dict） |
| `DISPATCH_*` / `CMD_*` | — | Python 侧前端桥接常量，C# 阶段没有前端，**不翻** |

### 1.3 presenter —— `src/VmNext.Core/Presentation/VoiceBarPresenter.cs`

| Python（`apps/voicebar/presenter.py`） | C# | 备注 |
|---|---|---|
| `VoiceBarPresenter.__init__` | `VoiceBarPresenter(core, config, notifier)` | |
| `install()` | `Install()` | |
| `start()` | `Start()` | |
| `shutdown()` | `Shutdown()` / `Dispose()` | |
| `apply_ui_mode(mode)` | `ApplyUiMode(mode)` | |
| `_on_core_state(state)` | `OnCoreState(state)` | |
| `_push_preview()` | `PushPreview()` → `PreviewTextChanged` | |
| `_push_mode()` | `PushMode()` → `DisplayModeChanged` | **显示状态的唯一出口** |
| `_enter_expanding` / `_enter_processing` / `_enter_idle` / `_enter_starting` | 同名（去掉下划线） | |
| `_on_notification(note)` | `OnNotification(note)` | |
| `_enter_error` / `_enter_rejected` / `_enter_backend_failed` | 同名 | |
| `on_dismissed()` | `OnDismissed()` | |
| `_on_levels(levels)` | `OnLevels(LevelFrame)` → `LevelsChanged` | |
| `try_start_recording()` | `TryStartRecording()` → `(bool, string)` | |
| `try_stop_recording()` | `TryStopRecording()` | |
| `try_cancel_recording()` | `TryCancelRecording()` | |
| `_schedule_dwell` / `_cancel_dwell` / `_on_dwell_expired` | `ScheduleDwell` / `CancelDwell` / `OnDwellExpired` | 用 `System.Threading.Timer` 代替 `threading.Timer` |
| `capsule.set_mode(mode, payload)` | **`DisplayModeChanged` 事件** | ⚠ 不引用任何 UI 类型（无 Avalonia），输出 = mode 字符串 + payload |
| `capsule.set_preview(text)` | **`PreviewTextChanged` 事件** | |
| `capsule.push_levels(levels, elapsed)` | **`LevelsChanged` 事件** | |
| `capsule.set_theme` / `set_preview_enabled` / `show_orb` / `hide_orb` | `ThemeChanged` / `PreviewEnabledChanged` / `OrbVisibilityChanged` | |
| — | `TryCopyResult()` | ⚠ **Python 没有**（Python 胶囊自己管剪贴板），见疑问 #4 |

### 1.4 热键 —— `src/VmNext.Core/Controller/HotkeyBinding.cs`

| Python（`host/hotkey.py`） | C# | 备注 |
|---|---|---|
| `NAMED_KEYS` | `HotkeyBinding.NamedKeys` | 19 项逐字一致 |
| `parse_hotkey(spec)` | `HotkeyBinding.Parse(spec)` | 见疑问 #5（单字符要大写） |
| `MOD_NOREPEAT` | `NativeMethods.MOD_NOREPEAT` | K3 补进 P/Invoke 源 |
| `MOD_WIN` | `NativeMethods.MOD_WIN` | K3 补进 P/Invoke 源（之前没有） |
| `HotkeyManager.start()` | `HotkeyBinding.Start()` | 独立线程 + 消息循环 |
| `HotkeyManager.stop()` | `HotkeyBinding.Stop()` | |
| `HotkeyManager.register(combo, cb)` | `HotkeyBinding.Register(combo, cb)` | 注册失败 → `hotkey_failed` 通知 |
| `HotkeyManager.unregister_all()` | `UnregisterAll()` | |
| `HotkeyError` | `HotkeyError` | |

### 1.5 入口 —— `src/VmNext.App/`

| Python（`__main__.py`） | C# | 备注 |
|---|---|---|
| `main(argv)` | `Program.Main(args)` | |
| `build_parser()` | `AppOptions.Parse(args)` | `--headless` / `--timeout` / `--backend` / `--config`（+ 新增 `--mock`） |
| `parse_known_args`（未知参数忽略） | 同样忽略 | |
| `run_gui(config, entry)` 的装配 | `AppHost` 构造函数 + `Start()` | 见 `core-api.md` §1 的装配表 |
| `run_headless(config, args)` | `Program.RunHeadless` | 退出码 0 / 2 |
| `run_headless_service(config)` | `Program.RunConsoleService` | 本阶段没有托盘 |
| `_shutdown_and_exit(...)` | `AppHost.Shutdown()` | 顺序照搬 |
| `_print_state(state)` | `AppHost.PrintState` | `[状态] …` |
| `_print_notification(note)` | `AppHost.PrintNotification` | `[通知] …` |
| `reload_mic()` | `CoreController.ReloadMic()` | 收口到控制器 |
| `on_hotkey()` | `AppHost.OnHotkey` | |
| `_flush_logs()` | `Console.Out.Flush()` | 日志本身 AutoFlush |
| 托盘 / 面板 / 胶囊窗口 | — | **GUI 阶段才有**，本阶段不做 GUI |

### 1.6 录音器适配 —— `src/VmNext.Audio/RecorderAdapter.cs`

| Python | C# | 备注 |
|---|---|---|
| `audio/recorder.py` `Recorder`（鸭子类型被 controller 直接用） | `VmNext.Audio.Recorder` + `RecorderAdapter : IDrainableRecorder` | Core 只认窄接口（疑问 #3） |

---

## 2. 状态机（文字版）

### 2.1 对外状态（`AppState.State`，presenter 消费的那一套）

```
                       ┌─────────────┐
      进程启动 ────────▶│  starting   │  后台加载模型
                       └──────┬──────┘
              模型就绪       │        模型加载失败
                       ┌──────▼──────┐  ┌─────────────┐
                       │    idle     │◀─│ failed(健康)│  ← ⚠ 状态本身仍是 idle
                       └──┬────────┬──┘  └─────────────┘
        start_recording   │        │   cancel
                       ┌──▼────────▼──┐
                       │  recording   │
                       └──┬────────┬──┘
          stop_recording │        │ cancel（丢音频，不识别不注入）
                       ┌──▼────────▼──┐
                       │  processing  │
                       └──┬────────┬──┘
      识别完成/失败/空文本 │        │ cancel（作废结果，识别线程醒来丢弃）
                       ┌──▼────────▼──┐
                       │    idle      │
                       └──────────────┘
```

- **没有对外的 error 态**：出错信息走 `backend_health`（failed + reason）和通知流，
  状态本身回到 `idle`，好让用户能直接再来一轮。
- **被拒不改状态**：`toggle_recording` 在 `processing` / `starting` 下只发一条
  `recording_rejected` 通知（外加日志），状态不动。
- `AppState.State` 只有四个值：`starting` / `idle` / `recording` / `processing`。

### 2.2 显示状态（`VoiceBarPresenter` 的 mode）

```
   orb ──(模型还没好)──▶ starting ──(模型就绪)──▶ orb
   orb ──(开始录音)──▶ recording ──(停止)──▶ processing
                              │                     │
                       (取消) │              (识别完成) │
                              ▼                     ▼
                          cancelled ──1.0s──▶ orb   result ──1.5s──▶ orb
                                                        ▲
                          processing ──(取消)──▶ cancelled
                          processing ──(识别失败)──▶ error ──4.5s──▶ orb
                                       (注入失败带原文) error+has-text ──**不收回**──▶ (用户点 ✕ / 下一轮录音)
   任意态 ──(命令被拒)──▶ rejected ──0.6s──▶ orb
```

`mode` 的完整枚举、payload 字段表、dwell 表原文 → **`docs/core-api.md`**。

---

## 3. 与 Python 不一致处（逐条）

| # | Python | C# | 为什么 |
|---|---|---|---|
| 1 | `core/controller.py` 的 `_process` 在**注入前**取前台窗口（`hwnd = get_foreground_window()`） | **开始录音时**记下前台窗口，注入时贴回那一个 | 任务书明确要求"焦点保护"（录音期间切窗口，注入仍贴回开始时的那个 HWND），并有对应的端到端测试 |
| 2 | `config.max_duration` 是**死配置**（只有校验，没有任何地方用） | 每轮录音起一个定时器，到点自动 `StopRecording()`（发 `recording_timeout` 通知） | 任务书要求"`max_duration` 超时自动 stop"；Python 那边是漏实现 |
| 3 | controller 直接用 `Recorder`（鸭子类型） | Core 只认 `IRecorder` / `IDrainableRecorder` 窄接口，`VmNext.Audio.RecorderAdapter` 适配 | `VmNext.Audio` 引用 `VmNext.Core`（电平泵在 Core），反向引用会成环 |
| 4 | 胶囊自己管剪贴板（复制按钮在 host 层） | presenter 上多一个 `TryCopyResult()` | UI 阶段只跟 core / presenter 打交道，不该让 UI 直接摸剪贴板 |
| 5 | `parse_hotkey` 单字符走 `ord(part.upper())` | 同样取**大写**码位（`char.ToUpperInvariant`） | 键位按大写字母定义（VK_A = 0x41）；第一版照抄成了小写码位，被测试抓出来 |
| 6 | 电平循环读 `controller.recorder`（一个常驻对象） | 每轮录音**现造**一个 `LevelPump`（数据源是那一轮的录音器），停录音就收 | `LevelPump` 的数据源在构造时定死（K1 的设计）；现造 = 每轮一个干净的电平窗口 |
| 7 | `vad.py` 自己发 `vad_fallback` 通知 + "同一原因只通知一次" | K2 把降级交给上层回调（`VadFactory.Make(..., onFallback)`），**去重与通知由控制器做** | K2 疑问 9 已经把这件事留给 K3（引擎层不认识通知中心） |
| 8 | `HotkeyManager` 用 `GetMessageW` 阻塞 | 用 `PeekMessage` + 5ms 轮询 | `NativeMethods` 里只有 `PeekMessage`；轮询对热键延迟的影响可以忽略（≤5ms） |
| 9 | 没有 | `--mock` 命令行开关 | 自检不能加载真模型（用户的 voice-mikon 正占着显存），真模型加载也要几十秒 |
| 10 | — | 输出被重定向时固定 UTF-8 | 交互式控制台保持系统默认（用户看得懂），管道 / 文件固定 UTF-8（脚本与测试读到的字节确定） |
| 11 | `subscribe(cb)` 一个通道 | 保留 `Subscribe` 为主路，另加 `StateChanged` / `BackendHealthChanged` / `NotificationPosted` 三个**事件**包装 | UI 那边更像是"事件"；事件在锁外触发，回调里再调控制器不会自锁 |
| 12 | `engine.model_tag`（本地）/ `engine.model`（网络） | 本地用 `config.model`（0.6B / 1.7B），网络用 `IAsrBackend.ModelId`（asr-1.0） | K2 把本地后端的 `ModelId` 定成**权重路径**（"永远显示本地权重路径"）；控制器对外的 `AppState.Model` 保持 Python 的口径（显示规格，不是路径） |

---

## 4. 测试结果

### 4.1 命令

```powershell
# 每个测试项目目录里各放了一份 global.json（MTP runner），要进项目目录跑
cd tests\VmNext.App.Tests   && dotnet test
cd tests\VmNext.Core.Tests  && dotnet test
cd tests\VmNext.Engines.Tests && dotnet test
cd tests\VmNext.Audio.Tests  && dotnet test
```

### 4.2 汇总

| 测试项目 | 总数 | 通过 | 失败 | 跳过 |
|---|---|---|---|---|
| `VmNext.App.Tests`（**新增**） | 10 | **9** | 0 | **1** |
| `VmNext.Core.Tests`（K1，宿主抽走后复跑） | 23 | 22 | 0 | 1 |
| `VmNext.Engines.Tests`（K2，未动） | 80 | 78 | 0 | 2 |
| `VmNext.Audio.Tests`（K1，未动） | 10 | 10 | 0 | 0 |

跳过的 3 条都是**环境限制**，与移植无关：
- App.Tests / Core.Tests 各 1 条：归还开关打开时本机 `SetClipboardData` 被 **1418** 拒绝
  （K1 疑问 #4 已经记过；Python 版在同样场景下同样写不回去）；
- Engines.Tests 2 条：环境变量 `MINIMAX_API_KEY` 不存在。

### 4.3 `VmNext.App.Tests` 逐条

| 测试 | 结果 | 说明 |
|---|---|---|
| `EndToEndTests.录音到注入_读回逐字相等` | ✅ | 真麦克风录 1.5 秒环境音 → FakeBackend 返回"测试文本" → 注入到自建 EDIT 窗口 → `WM_GETTEXT` 读回 **"测试文本"**（逐字相等） |
| `EndToEndTests.录音期间切窗口_文字仍落在开始时的那个窗口` | ✅ | 录音期间 `SetForegroundWindow` 切到第二个 EDIT 窗口 → 文字落回**第一个**窗口，第二个窗口为空 |
| `EndToEndTests.取消_不识别也不注入` | ✅ | `FakeBackend.TranscribeCount == 0`、目标窗口为空、状态回 idle |
| `EndToEndTests.后端失败_收到错误通知且不注入且状态回idle` | ✅ | 收到 `asr_failed`、未注入、状态回 idle |
| `EndToEndTests.剪贴板留底_归还关时剪贴板里是识别文本` | ✅ | 归还关 → 剪贴板里留着"测试文本" |
| `EndToEndTests.剪贴板归还_归还开时原内容被还回去` | ⏭ skip | 本机 1418 拒绝写回（环境限制） |
| `HotKeyTests.热键消息触发进入录音` | ✅ | `PostMessage(hwnd, WM_HOTKEY, id, 0)` → 控制器进入 recording |
| `HotKeyTests.热键字符串解析成修饰键加主键` | ✅ | `ctrl+alt+space` / `ctrl+shift+F12` / `win+a` |
| `HeadlessCliTests.headless_引擎就绪后退出码0` | ✅ | 子进程 `--headless --mock --timeout 60` → **退出码 0** |
| `HeadlessCliTests.headless_超时参数生效` | ✅ | 子进程 `--headless --mock --timeout 0` → 打印"等待引擎就绪超时" → **退出码非 0** |

### 4.4 `--headless` 实测

```
dotnet run --project src/VmNext.App -- --headless --mock --timeout 60
  → [无头] 引擎就绪
  → 退出码 0

dotnet run --project src/VmNext.App -- --headless --timeout 30      # 不带 --mock
  → [无头] 引擎加载失败: 本地权重缺失:Qwen3-ASR-0.6B 不存在
             本地后端不会联网下载 —— 请二选一:
               1) 下载权重后,在 data/config.json 的 model_root 里配好权重根目录 …
               2) 或把后端切成 minimax 网络接口(不需要本地权重,音频会出本机)
  → 退出码 2
```

> ⚠ **不带 `--mock` 时退出码是 2，不是 0** —— 这是**正确行为**：
> `voice-mikon-next/data/` 里没有 `config.json`，本地后端没有权重根目录，
> 按 AGENTS.md 的"不静默回落"规矩**明确报错指路**，绝不偷偷换成别的后端。
> 本机权重其实在 `D:\Dev\Environments\models\`，但**不加载真模型**
> （用户的 voice-mikon 正占着显存），所以自检走 `--mock`。

### 4.5 端到端实测到的数字

| 项 | 实测值 |
|---|---|
| 设备原生采样率 | **48000 Hz**（NAudio 自动挑的 USB 麦克风） |
| 录 1.5 秒 → 实际时长 | 约 1.4 s |
| 注入读回内容 | **"测试文本"**（逐字相等，`trailing_space=false`） |
| 焦点保护 | 切到第二个窗口后，文字仍落在第一个；第二个窗口读回空串 |
| 识别耗时 | FakeBackend 秒回；真实后端见 K2 |

---

## 5. 疑问清单

> 每条：**原文 / 我的处理 / 为什么**。

### 疑问 #1（最重要）：焦点保护的语义被改了

- **原文**：`core/controller.py` 的 `_process()`：
  ```python
  # 粘贴前捕获焦点窗口 —— 转写这 1 秒用户可能切窗口
  hwnd = get_foreground_window()
  ```
  —— 是在**注入前**才取前台窗口。也就是说：录音期间切走窗口，文字会贴到**新**窗口去。
  （`inject.py` 的 `restore_foreground(hwnd)` 是为"注入期间焦点又变了"准备的，
  不是为"录音期间切窗口"准备的。）
- **我的处理**：**开始录音时**就记下前台窗口（`CoreController._focusAtStart`），
  注入时贴回那一个；`Injector.AttemptInject` 里原有的"焦点变了就还回去"逻辑照旧
  （`RestoreForeground` → `AttachThreadInput` + `SetForegroundWindow`）。
  目标窗口要是已经被关掉了（`IsWindow` 为假），退回当前前台窗口 ——
  宁可贴到"当前看得见的窗口"，也不能把文字丢进虚空。
- **为什么**：任务书明确要求"录音期间用户切窗口，注入仍贴回开始时的那个 HWND"，
  并且要求有一条端到端测试验它。用户按下热键时心里想的就是"说给这个窗口听"，
  中途切走（去看别的消息）不该把话送错地方。
  **代价**：Python 版是"注入时贴当前窗口"，这版是"贴开始时那个窗口" ——
  这是**有意的行为差异**，不是笔误。

### 疑问 #2：`max_duration` 在 Python 里是死配置

- **原文**：`config.py` 有 `max_duration: float = 120.0`，`README.md` 写着"单次录音上限(秒)"，
  但 `grep max_duration` 全仓库**只有定义与校验，没有任何地方读它** —— 录音可以无限长。
- **我的处理**：每轮录音起一个 `System.Threading.Timer`，到点自动 `StopRecording()`，
  并发一条 `recording_timeout` 通知。
- **为什么**：任务书要求"`max_duration` 超时自动 stop"。Python 那边是漏实现。
  补上之后行为比 Python 更"有上限"—— 这是**任务书授权的差异**，写在这里以免
  以后有人当成移植 bug 报回来。

### 疑问 #3：Core 不能引用 Audio（依赖方向）

- **原文**：`core/controller.py` 直接用 `audio/recorder.py` 的 `Recorder`（鸭子类型）。
- **我的处理**：
  - `VmNext.Core` → `VmNext.Engines`（控制器要 `IAsrBackend` 与流式引擎；
    Engines 不引用本仓库任何项目，所以这条边不成环）；
  - `VmNext.Audio` → `VmNext.Core`（K1 已有的方向：电平泵在 Core）；
  - 于是 **Core 不能反向引用 Audio** → Core 只认窄接口 `IRecorder` /
    `IDrainableRecorder`，`VmNext.Audio.RecorderAdapter` 做适配。
- **为什么**：不引入接口的话，Core 引用 Audio、Audio 引用 Core = 项目环，
  MSBuild 直接报错。窄接口还顺带让"假录音器"在测试里可复现。

### 疑问 #4：`TryCopyResult` 是新增的

- **原文**：Python 的胶囊（`host/capsule.py`）自己管"复制"按钮，直接写剪贴板；
  `apps/voicebar/presenter.py` 里没有对应方法。
- **我的处理**：presenter 上加 `TryCopyResult()`（把 `CoreController.LastText` 写进剪贴板）。
- **为什么**：UI 阶段的契约是"UI 只跟 core / presenter 打交道"。
  让 UI 直接摸 `ClipboardService` 就等于把 owner 窗口线程的规矩漏给 UI 层 ——
  那条规矩（owner 窗口必须在主线程）是很容易踩的坑。
  **代价**：这是 Python 版没有的 API，胶囊自己管复制的行为**没有**搬过来。

### 疑问 #5：热键单字符取大写码位

- **原文**：`parse_hotkey` 里的 `elif len(part) == 1: vk = ord(part.upper())`。
- **我的处理**：C# 侧第一版照抄成了 `part[0]`（小写码位），被测试抓出来
  （`win+a` 解析出 97 而不是 65），已改成 `char.ToUpperInvariant(part[0])`。
- **为什么**：键位是按**大写字母**定义的（VK_A = 0x41 = 65）；小写 'a' 那个码位
  根本不是 A 键。这条现在有测试守着（`HotKeyTests.热键字符串解析成修饰键加主键`）。

### 疑问 #6：`Recorder` 的设备索引口径（K1 遗留，影响本层）

- **原文**：Python 的 `config.input_device` 是 PortAudio 的**全局设备索引**（输出设备也占号）。
- **我的处理**：K1 已经记过 —— NAudio 只有采集端点列表，`InputDevice.Index` 是
  **采集端点列表内的序号**。本层的麦克风探测（`DevicePicker.ChooseDevice`）
  直接用 K1 的新口径，**没有**做任何兼容转换。
- **为什么**：转换需要一张"全局索引 ↔ 列表序号"的映射表，而 NAudio 根本不提供
  "全局索引"这个概念（枚举顺序也不保证稳定）。硬凑一张表反而更不可靠。
  **后果**：旧 `data/config.json` 里的 `input_device` 数值不能直接沿用，
  用户需要重选一次设备。

### 疑问 #7：`--mock` 是新增的命令行开关

- **原文**：Python 的 `build_parser()` 只有 `--headless` / `--timeout`。
- **我的处理**：加 `--mock`（本地后端走 mock 模式，不加载真模型）。
- **为什么**：自检/测试不能加载真模型 —— 一来用户的 voice-mikon 正占着显存，
  二来真模型加载要几十秒（自检会超时）。mock 模式只起一个空壳子进程，
  `load` 立刻返回 ready。**没有 mock 的话，`--headless` 在本机永远是退出码 2**
  （本地权重没配 —— 这是正确的"不静默回落"行为，但没法当自检用）。

### 疑问 #8：`config.input_device` 的探测耗时落在后台

- **原文**：Python 的 `_init_mic()` 在 `mic-warmup` 线程里跑 `choose_device()`，
  探测要 2 秒。
- **我的处理**：照搬（`InitMic()` 也在 `mic-warmup` 线程）。
  `ReloadMic()` 是**同步**的 —— 它是用户主动点的托盘菜单项，等 2 秒可以接受。
- **为什么**：探测放在热键路径上会把用户说话的开头吃掉。

### 疑问 #9：测试宿主抽到共享项目，顺手修了一个真 bug

- **原文**：`tests/VmNext.Core.Tests/InjectTarget.cs` 里的 `TestEditHost`
  用**单个静态字段** `s_editHwnd` 记编辑控件句柄（窗口过程是静态委托，拿不到实例）。
- **我的处理**：抽到共享项目 `tests/VmNext.TestSupport/`（两个测试项目共用），
  并把静态字段改成**按窗口**的映射（`Dictionary<IntPtr, IntPtr>`）。
- **为什么**：**两个窗口同时存在时**，第二个宿主会覆盖第一个的映射 ——
  第一个窗口 WM_SETFOCUS 时把焦点设到**第二个**窗口的控件上，焦点被另一个线程抢走，
  实测"目标窗口怎么都拿不回前台焦点"。焦点保护那条测试一开始就是被这个坑挡住的。
  （K1 只有一个窗口，所以一直没暴露。）

---

## 6. 产出文件清单

**新增（Core）**
```
src/VmNext.Core/Controller/IRecorder.cs            录音器窄接口 + 停止结果
src/VmNext.Core/Controller/CoreController.cs        状态机 / 订阅 / 通知 / 后端 / 生命周期
src/VmNext.Core/Controller/CoreController.Recording.cs  录音编排 / 预览 / 电平 / 识别注入
src/VmNext.Core/Controller/CoreController.Backend.cs    切后端 / 切模型
src/VmNext.Core/Controller/HotkeyBinding.cs        热键解析 + 注册 / 注销
src/VmNext.Core/Presentation/DisplayProtocol.cs     mode 枚举 + dwell 表 + payload
src/VmNext.Core/Presentation/VoiceBarPresenter.cs  状态 / 通知 → 显示状态
```

**新增（App / Audio）**
```
src/VmNext.App/VmNext.App.csproj
src/VmNext.App/Program.cs                           入口 / 参数 / headless / 控制台服务
src/VmNext.App/AppOptions.cs                        命令行参数
src/VmNext.App/AppHost.cs                           装配链 + 退出收尾
src/VmNext.Audio/RecorderAdapter.cs                Recorder → IRecorder
```

**追加（K1/K2 已有文件，只补成员不改行为）**
```
src/VmNext.Core/Notifier.cs                         + NotificationPosted 事件
src/VmNext.Shell/HotKeyWindow.cs                    + Handle 属性
src/VmNext.Shell/NativeMethods.cs                   + MOD_WIN 常量
src/VmNext.Core/VmNext.Core.csproj                  + VmNext.Engines 引用
```

**测试**
```
tests/VmNext.TestSupport/                           共享注入验证宿主（从 Core.Tests 抽出）
tests/VmNext.App.Tests/                             端到端测试（FakeBackend / 真链路）
```

**文档**
```
docs/k3-controller.md                               本文件
docs/core-api.md                                    UI 阶段的唯一接口依据
```

**未动**：`voice-mikon/`（只读）、`archive/`（冻结）、`VmNext.sln`（由主代理统一加项目）、
`src/VmNext.Engines/`、`src/VmNext.Audio/`（除新增适配器）。
