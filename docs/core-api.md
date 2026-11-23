# core-api —— UI 阶段的唯一接口依据

> **这份文档是 UI 翻译的契约。** 下一阶段的 worker 只看这份文档绑事件，
> 不要去翻 `src/VmNext.Core/` 的实现（那是"怎么实现"，不是"怎么用"）。
>
> 翻译源：`voice-mikon/src/voice_mikon/apps/voicebar/presenter.py` +
> `apps/voicebar/protocol.py` + `core/controller.py` + `core/state.py`。
> C# 侧实现见 `src/VmNext.Core/Controller/` 与 `src/VmNext.Core/Presentation/`。

---

## 0. 一句话地图

```
        UI（下一阶段）
          │  订阅事件 ▲            ┌──────────────┐
          │         │            │ VoiceBarPresenter │  显示状态（mode + payload）
          │  调动作 ▼            └──────────────┘
   ┌──────────────┐   快照 / 通知   ┌──────────────┐
   │  UI 线程     │◀───────────────▶│ CoreController │  状态机 + 录音编排
   └──────────────┘                 └──────────────┘
```

- **CoreController**：业务核心（状态机 + 录音/识别/注入编排）。
- **VoiceBarPresenter**：把 core 的快照 / 通知翻译成**显示状态**（mode 字符串 + payload）。
  它**不认识任何 UI 类型**（没有 Avalonia），输出就是字符串 + 纯数据。
- **UI**：只做"演"（动画 / 布局），不做"决策"（停多久、到点回哪，全归 dwell 表）。

---

## 1. 装配顺序（每一步都不能换位置）

| # | 做什么 | 为什么必须在这个位置 |
|---|---|---|
| 1 | **DPI 感知**（`Dpi.SetDpiAwareness()`） | 必须在**任何窗口**创建之前，否则高分屏下布局全乱 |
| 2 | **剪贴板 owner 窗口**（`new ClipboardService()` + `EnsureOwnerWindow()`） | owner 窗口属于创建它的线程；线程退出窗口就没了，而 Windows 在 owner 窗口销毁时**连带清空剪贴板** —— "识别结果留在剪贴板"这个兜底会直接废掉 |
| 3 | **配置 + 日志**（`Config.Load` / `Log.Setup`） | 之后才有地方写日志 |
| 4 | **通知出口**（`NotificationHub`） | 拒绝 / 失败路径都要落到它 |
| 5 | **controller**（`new CoreController(...)`） | 业务核心 |
| 6 | **presenter**（`new VoiceBarPresenter(...)` + `Install()`） | 翻译层，装在 controller 之后 |
| 7 | 胶囊窗口 / 面板窗口 | UI 阶段才有（本阶段控制台） |
| 8 | **热键**（`HotkeyBinding`） | 独立线程；注册失败只发 warning，不影响程序 |
| 9 | **引擎启动**（`controller.Start()`） | 后台加载模型，主流程不阻塞 |

参考实现：`src/VmNext.App/AppHost.cs`（控制台版装配链）。

### 退出顺序（同样不能换）

```
presenter.Shutdown()   ← 先停"往界面推东西"（推给一个没人看的界面没有意义）
   ↓
hotkey.Stop()          ← 再注销热键（全局资源！占着不放，"退出后马上重启"会注册失败）
   ↓
controller.Shutdown()  ← 预览线程 / 电平泵 / 录音器 / 后端卸载（里面还会再停一次热键，幂等）
   ↓
clipboard.ReleaseOwnership()  ← 退出前把剪贴板所有权还给系统，内容留住
```

> ⚠ **热键是全局资源**：注销要等进程真正退出才彻底释放，所以**不能**排在
> 模型卸载后面（那一步要好几秒），也不能排在窗口推送前面（那会让退出过程
> 继续往一个已经没人看的窗口里推东西）。

---

## 2. 必须在主线程的东西

| 对象 | 必须在 | 为什么 |
|---|---|---|
| `ClipboardService`（owner 窗口） | **主线程** | owner 窗口属于创建它的线程；线程退出 → 窗口销毁 → 剪贴板被连带清空 |
| `HotKeyWindow` | **热键线程** | `RegisterHotKey` 的 hWnd 必须是接收 `WM_HOTKEY` 的那个窗口，且消息要由它自己线程的循环取走 |
| UI 窗口 / 订阅者回调 | **UI 线程** | 事件在触发它的那条线程上发出（见 §3） |

> C# 没有 `main_thread` 概念，但**规矩本身必须保留**：
> `ClipboardService` 构造时记录"owner 线程"，`EnsureOwnerWindow()` 只在 owner 线程建窗口，
> 别的线程只复用（拿不到就返回 0 → 降级为无 owner，功能不受影响）。

---

## 3. UI 要订阅的事件

| 事件 | 在哪 | 触发线程 | UI 要不要 `Dispatcher.UIThread.Post` | 载荷 |
|---|---|---|---|---|
| `CoreController.StateChanged` | `src/VmNext.Core/Controller/CoreController.cs` | **谁改的状态就在谁的线程**（模型加载线程 / 识别线程 / 调用方线程） | **要** | `AppState`（`State` / `Backend` / `BackendHealth` / `Model` / `PreviewText`） |
| `CoreController.BackendHealthChanged` | 同上 | 同上 | **要** | `BackendHealth`（`Status` = loading/ready/failed，`Reason`） |
| `VoiceBarPresenter.DisplayModeChanged` | `src/VmNext.Core/Presentation/VoiceBarPresenter.cs` | 同上（状态变化那条线程 / 通知那条线程） | **要** | `DisplayModeChangedEventArgs`：`Mode`（字符串）+ `Payload`（见 §4） |
| `VoiceBarPresenter.PreviewTextChanged` | 同上 | 同上 | **要** | `PreviewTextChangedEventArgs.Text`（**整段替换**，不是追加） |
| `VoiceBarPresenter.LevelsChanged` | 同上 | 电平泵线程（30fps） | **要** | `LevelsChangedEventArgs`：`Levels`（已映射 0..1）+ `Elapsed`（本轮秒数） |
| `VoiceBarPresenter.ThemeChanged` | 同上 | `Start()` 调用那条线程 | **要** | `string`（auto/light/dark） |
| `VoiceBarPresenter.PreviewEnabledChanged` | 同上 | 同上 | **要** | `bool` |
| `VoiceBarPresenter.OrbVisibilityChanged` | 同上 | 同上 / `ApplyUiMode` | **要** | `bool`（false = 收球） |
| `NotificationHub.NotificationPosted` | `src/VmNext.Core/Notifier.cs` | 发通知那条线程 | **要** | `Notification`（`Level` / `Kind` / `Message` / `Fields` / `Timestamp`） |
| `CoreController.SubscribeLevels(...)` | `CoreController` | 电平泵线程（30fps） | **要** | `LevelFrame`（`Levels` + `Elapsed`）—— 电平的**正规入口**（见下面的说明） |

> **电平为什么有两个入口**：`LevelPump` 实例由控制器**每轮录音现造**
> （数据源是那一轮的录音器），所以 UI 不该去订阅某个 pump 实例 ——
> 订阅 `CoreController.SubscribeLevels(...)` 才是稳定的入口，控制器会转发给当前这一轮。
> `VoiceBarPresenter.LevelsChanged` 已经把这件事做完了：**UI 只需要订阅 presenter**。
>
> 任务书里写的 `LevelPump.Tick(levels, elapsed)`，在本实现里对应
> `LevelFrame(Levels, Elapsed)` —— `Levels` 是**已映射**的 0..1
> （`LevelPump.DbfsToUnit` 的输出），`Elapsed` 是本轮已录音秒数。
> ⚠ **绝不能按窗口内最大值归一化**：归一化之后电平条永远是满的，
> 用户就没法从条的高低看出"我说话声音大不大"，那条条就只剩装饰作用。

### 通知的 kind（UI 按 kind 分支）

| kind | 级别 | 什么时候 | 带什么字段 |
|---|---|---|---|
| `recording_rejected` | error | 命令被拒（已在录音 / 识别中 / 模型还在加载） | `reason` |
| `recording_failed` | error | 录音器启动失败 | — |
| `recording_timeout` | info | 达到 `max_duration`，自动停止 | — |
| `asr_failed` | error / warning | 识别失败 / 没听清 | — |
| `inject_failed` | warning / error | 注入没送达（文案按实况分档） | `text`（原文）、`reason` |
| `modifier_wait` | warning | 注入在等用户松开修饰键 | `text`（最终识别文本） |
| `backend_load_failed` | error | 后端加载失败（权重缺失 / 没配 key） | `backend` |
| `backend_switching` / `backend_switched` | info | 切后端 | — |
| `backend_switch_failed` | error / warning | 切后端失败 / 录音中拒绝切 | — |
| `model_switching` / `model_switched` | info | 切模型规格 | — |
| `model_switch_failed` | error / warning | 切模型失败 / 网络后端不支持 | — |
| `microphone_ready` / `microphone_unavailable` | info / warning | 麦克风探测结果 | — |
| `vad_fallback` | warning | Silero 不可用，预览断句降级为能量判定（同一原因只通知一次） | `reason` |
| `hotkey_invalid` / `hotkey_failed` | warning | 热键写错 / 没注册上 | `combo`、`reason` |
| `hotkey_callback_failed` | error | 热键触发的动作抛异常 | `reason` |

---

## 4. 显示状态（mode）

### 4.1 完整枚举

| mode 常量 | 字符串 | 含义 |
|---|---|---|
| `DisplayModes.Orb` | `orb` | 常驻球形态（待机） |
| `DisplayModes.Starting` | `starting` | 启动中（模型还在加载） |
| `DisplayModes.Expanding` | `expanding` | 展开中（进入录音的过渡态） |
| `DisplayModes.Recording` | `recording` | 录音中 |
| `DisplayModes.Processing` | `processing` | 识别中 |
| `DisplayModes.Result` | `result_terminal` | 结果终态（已输入） |
| `DisplayModes.Error` | `error_terminal` | 错误终态（**无原文**） |
| `DisplayModes.Cancelled` | `cancelled_terminal` | 取消终态 |
| `DisplayModes.Rejected` | `rejected_bounce` | 被拒（命令被拒绝时的弹跳提示） |
| `DisplayModes.ErrorWithText` | `error_terminal+has-text` | 带原文的错误态**标记**（注入失败，原文还能复制走） |

> `ErrorWithText` 不是独立的 mode，而是 **dwell 表的键**：带原文的错误态
> 与普通错误态**共用** `error_terminal` 这个 mode，但**收回时机相反**
> （一个不收回，一个 4.5 秒后收回），所以查表用的键要分开。

### 4.2 payload 字段表

`DisplayPayload`（不可变记录）—— **字段按 mode 取用，用不到的给空值**（前端不按 mode 猜）：

| 字段 | 类型 | 含义 |
|---|---|---|
| `Message` | `string` | 提示文案：错误原因 / 识别中 / 已输入 / 已取消 / 被拒的原因 |
| `Text` | `string` | 识别正文：结果态是识别出来的文字；带原文的错误态是**用户还能复制回来的那段原文** |
| `CanCopy` | `bool` | 有没有可复制的原文。带原文的错误态为 `true`（这类错误**不自动收回**） |
| `Reason` | `string` | 机器可读的原因码（暂不参与渲染，留着以后排查） |
| `Warn` | `bool` | 只在"等用户松开修饰键"的**瞬态覆盖**里为 `true` |
| `Body` | `string` | 同上：文字条里的完整说明（正文用**最终识别文本**，不是预览拼接版） |

### 4.3 各 mode 的常用文案（presenter 内部写死）

| mode | Message | Text | CanCopy |
|---|---|---|---|
| `recording` | `""` | `""` | `false` |
| `processing` | `识别中…` | — | — |
| `result_terminal` | `已输入` | 识别正文 | `false` |
| `cancelled_terminal` | `已取消` | `""` | `false` |
| `rejected_bounce` | 拒绝原因 | `""` | `false` |
| `error_terminal` | 错误原因 | `""`（无原文时） | `false` |
| `error_terminal+has-text` | 错误原因 | **原文** | `true` |

---

## 5. dwell 表（原文）

**整个项目只有这一份**（`DisplayProtocol.DwellSeconds`）。
"何时发生"归 presenter：终态停多久、到点回哪，都查这张表；
**前端不再有任何收回定时器**（"如何呈现"归前端，那是动画的事）。

```python
DWELL_SECONDS: dict[str, float | None] = {
    MODE_RESULT: 1.5,
    # **无原文**的错误:错误文案要让人看清,停久一点
    MODE_ERROR: 4.5,
    MODE_CANCELLED: 1.0,
    MODE_REJECTED: 0.6,
    # 带原文的错误态:**无自动转移**(值 None = 不排)。
    #
    # 用户可能要反复点「复制」重试(剪贴板被占、目标窗口不给焦点),
    # 自动收回会把重试的口子堵上。只有点 ✕(dismiss)或开始下一轮
    # 录音才收(行为基准见 38b105a)。
    MODE_ERROR_WITH_TEXT: None,
}
```

| 终态 | 停留秒数 | 到点回哪 |
|---|---|---|
| `result_terminal` | **1.5** | `orb` |
| `error_terminal`（无原文） | **4.5** | `orb` |
| `cancelled_terminal` | **1.0** | `orb` |
| `rejected_bounce` | **0.6** | `orb` |
| `error_terminal+has-text` | **无（不排）** | —— 靠用户点 ✕ 或开始下一轮录音才收 |

**到点怎么作废**：比较"排表时的 mode"和"当前的 mode"，对不上就作废。
不做"到处显式取消定时器"那套 —— 比一下就够了，也不会漏。

---

## 6. UI 调回来的动作

| 动作 | 在哪 | 签名 | 说明 |
|---|---|---|---|
| 开始录音 | `VoiceBarPresenter` | `TryStartRecording() → (bool Ok, string Hint)` | `Ok=false` 且 `Hint` 非空时，球上冒一句（如 `模型加载中…`） |
| 停止录音 | `VoiceBarPresenter` | `TryStopRecording()` | 无返回值；后续由状态流推进 |
| 取消本轮 | `VoiceBarPresenter` | `TryCancelRecording()` | 录音中丢音频、识别中作废结果；都**不识别不注入** |
| 复制结果 | `VoiceBarPresenter` | `TryCopyResult() → bool` | 把最近一次识别文本写进剪贴板 |
| 切后端 | `CoreController` | `SwitchBackend(string kind)` | `local` / `minimax`；**录音 / 识别中拒绝**（会发 `backend_switch_failed`） |
| 切模型规格 | `CoreController` | `SwitchModel(string modelTag)` | 只对**本地**后端有意义（`0.6B` / `1.7B`） |
| 重新探测麦克风 | `CoreController` | `ReloadMic()` | 同步做完（探测要 2 秒），失败发 `microphone_unavailable` |
| 退出 | `AppHost` | `RequestQuit()` | 走 §1 的退出顺序；三条退出路（托盘 / 面板按钮 / Ctrl+C）汇到同一条 |
| 用户点 ✕ 关错误提示 | `VoiceBarPresenter` | `OnDismissed() → bool` | **只有"带原文的错误常驻态"才认**；其它情况静默忽略（幂等），返回 `false` |
| 面板改界面形态 | `VoiceBarPresenter` | `ApplyUiMode(string mode)` | `orb` / `resident` / `on_demand` / `headless`；值没变就不动作 |

---

## 7. 线程模型小结

| 活儿 | 在哪条线程 |
|---|---|
| 状态快照推送 | 改状态的那条线程（模型加载 / 识别 / 调用方） |
| 通知推送 | 发通知的那条线程 |
| 电平推送（30fps） | 电平泵线程（每轮录音现造） |
| 预览 partial | 预览线程（每轮录音现造） |
| 最终识别 + 注入 | `asr-run` 线程（每轮一个） |
| 热键回调 | 热键线程 |
| 模型加载 | `model-load` 线程 |

⇒ **UI 侧所有事件都要 `Dispatcher.UIThread.Post`**。

⇒ **不要在事件回调里同步等控制器**：识别是在后台线程跑的，
`StateChanged` 到达时最终结果可能还没注入。

---

## 8. 一句话提醒

- **识别前不加本地预判**：录到的音频**整段**丢给模型，没有静音门 / 时长门 / 音量门。
- **预览层的 VAD 只用于断句**，不影响最终结果；最终结果永远来自整段识别。
- **剪贴板 owner 窗口必须在主线程**；归还开关关掉时**不再有还原那一次写入**。
- **退出顺序：先停窗口推送，再注销热键**。
- **不静默回落**：本地权重缺失就明确报错指路（下载命令或切网络后端），不偷偷换后端。
- **API key 只走环境变量或 `data/.env`**，绝不进 `config.json`、绝不进日志。
