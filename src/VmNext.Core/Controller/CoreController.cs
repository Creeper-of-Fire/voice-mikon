using System;
using System.Collections.Generic;
using System.Threading;
using VmNext.Core.Inject;
using VmNext.Core.Interop;
using VmNext.Core.Levels;
using VmNext.Core.Presentation;
using VmNext.Engines;
using VmNext.Engines.Streaming;
using VmNext.Engines.Vad;

namespace VmNext.Core.Controller;

/// <summary>
/// 控制器内部的应用状态机。
///
/// 翻译自 voice-mikon/src/voice_mikon/core/controller.py 的 <c>State</c> 枚举。
///
/// ⚠ <b>ERROR 没有对外状态</b>：<c>core/state.py</c> 里只有
/// starting / idle / recording / processing 四个（error 态已退役）。
/// 出错信息不靠状态走 —— 走 <c>backend_health</c>（failed + reason）和通知流
/// （backend_load_failed / asr_failed），状态本身回到 idle，好让用户能直接再来一轮。
/// </summary>
public enum ControllerState
{
    /// <summary>启动中（模型还在加载）。</summary>
    Starting,

    /// <summary>待机。</summary>
    Idle,

    /// <summary>录音中。</summary>
    Recording,

    /// <summary>识别中。</summary>
    Processing,

    /// <summary>出错（对外映射成 idle）。</summary>
    Error,
}

/// <summary>
/// 核心控制器：语音输入的业务核心。
///
/// 翻译自 voice-mikon/src/voice_mikon/core/controller.py。
///
/// UI（面板 / 托盘 / 胶囊）和热键都只跟这一层打交道，彼此不直接依赖。
///
/// 状态机：
/// <code>
///     IDLE ──start_recording()──> RECORDING ──stop_recording()──> PROCESSING
///       ^                                                              │
///       └────────────────────── 识别完成/失败 ──────────────────────────┘
/// </code>
///
/// 用户随时可以 <c>CancelRecording()</c> 放弃当前这一轮：录音中就丢掉音频，
/// 识别中就作废结果 —— 两种情况都不识别、不注入，回到 IDLE。
///
/// 录音期间还会跑一个**预览线程**：把录音增量喂给预览位的引擎，
/// 实时刷新"边说边显示"的文字（快照里的 <c>PreviewText</c>）。它只是体验，
/// 最终注入的始终是最终位那次整段识别的结果。
///
/// 两个插槽，同一套接口：
/// <list type="bullet">
/// <item>预览位 —— 要"快"：默认分句预览引擎，切句就出字，跟手</item>
/// <item>最终位 —— 要"准"：固定整段引擎，最终注入的文字只来自它</item>
/// </list>
///
/// 识别后端（本地模型 / 网络 API）是**互斥**的：同一时刻只留一个，
/// 切走后旧后端被 unload（本地释放显存，网络关会话）。
/// 切换在后台线程里做；录音 / 识别中**拒绝切换** ——
/// 这一轮的最终位引擎持有旧后端的引用，中途换掉会出错或丢结果。
/// </summary>
public sealed partial class CoreController : IPanelCore
{
    // ---------- 命令被拒的原因码（presenter 只看 ok，原因码给日志和将来可能的 UI 用） ----------

    public const string ReasonModelNotReady = "model_not_ready";
    public const string ReasonAlreadyRecording = "already_recording";
    public const string ReasonProcessing = "processing";
    public const string ReasonNotRecording = "not_recording";
    public const string ReasonRecorderStartFailed = "recorder_start_failed";

    /// <summary>注入器返回了假值（不是 InjectResult）时的兜底原因码。</summary>
    public const string ReasonInjectFailed = "inject_failed";

    // ---------- 识别后端（互斥，同一时刻只留一个） ----------

    /// <summary>本地 Qwen3-ASR（transformers，GPU）。</summary>
    public const string BackendLocal = "local";

    /// <summary>MiniMax 平台网络接口（asr-1.0）。</summary>
    public const string BackendMinimax = "minimax";

    public static readonly string[] BackendKinds = { BackendLocal, BackendMinimax };

    /// <summary>
    /// 后端的显示名：按后端**查表**，不写死在各个分支里 ——
    /// 加一个后端时，这里加一行就够了，不用满代码库找 "网络" 这两个字。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> BackendLabels =
        new Dictionary<string, string>
        {
            [BackendLocal] = "本地",
            [BackendMinimax] = "MiniMax",
        };

    /// <summary>
    /// 预览位插哪颗引擎 —— **模块级常量**，代码内可切换，不做成用户配置项。
    ///
    /// * Sentence：分句预览（默认）。切一句认一句，跟手；分句的不一致只污染预览层。
    /// * FullPass：旧行为。每轮把累积音频整段重识别。
    ///
    /// 最终位固定整段引擎：最终注入的文字必须永远来自整段识别，
    /// 预览层怎么马虎都不许影响它。
    /// </summary>
    public enum PreviewEngineKind
    {
        Sentence,
        FullPass,
    }

    /// <remarks>
    /// 用 static readonly 而不是 const：const 会被编译器折叠，
    /// 下面那条 FullPass 分支就成了"不可达代码"警告 —— 而这条分支是
    /// **代码内可切换**的开关，留着才能一眼看出"改成 FullPass 会发生什么"。
    /// </remarks>
    public static readonly PreviewEngineKind PreviewEngine = PreviewEngineKind.Sentence;

    /// <summary>VAD 降级通知的 kind（通知中心按 kind 分类）。</summary>
    public const string NotifyKindVadFallback = "vad_fallback";

    private static readonly Logger _log = Log.GetLogger("core");

    private readonly object _lock = new();
    private readonly List<Action<AppState>> _listeners = new();
    private readonly List<Action<LevelFrame>> _levelListeners = new();

    // 后端换装是"整体动作"（建新的 → 加载 → 卸旧的 → 换引用），
    // 拿一把单独的锁串起来：启动加载和切换不会交叉。
    private readonly object _backendLock = new();

    private readonly NotificationHub _notifier;
    private readonly Func<string, IAsrBackend> _backendFactory;
    private readonly Func<int?, IRecorder>? _recorderFactory;
    private readonly Func<string, IntPtr, bool, bool, InjectResult> _injector;
    private readonly Action<short[], int>? _recordingSaver;
    private readonly Func<(int? Index, string Description)>? _micProbe;

    // 同一原因只通知一次（降级是**持续状态**：模型文件缺失就一直缺，
    // 每轮录音都弹一次通知会把用户淹掉）。重复发生只记日志。
    private readonly HashSet<string> _notifiedVadReasons = new(StringComparer.Ordinal);

    /// <summary>对外快照：状态机 + 后端健康 + 模型 + 预览文字。</summary>
    private AppState _state;

    /// <summary>全局热键的停止动作（由装配层挂上来，退出时由 <see cref="Shutdown"/> 第一个停它）。</summary>
    private Action? _hotkeyStop;

    /// <summary>
    /// 造一个控制器。
    /// </summary>
    /// <param name="config">配置。</param>
    /// <param name="backendFactory">后端工厂（可注入，测试用假实现，避免真的加载模型 / 真的联网）。</param>
    /// <param name="recorderFactory">录音器工厂（可注入，测试用假实现，避免真的打开音频设备）。</param>
    /// <param name="injector">注入器（可注入，测试用假实现，避免真的改剪贴板 / 模拟按键）。</param>
    /// <param name="notifier">通知出口（拒绝 / 失败路径都要走它，presenter 靠通知显示错误）。</param>
    /// <param name="recordingSaver">录音留存（存 WAV，调试用）。null = 不留存。</param>
    /// <param name="micProbe">麦克风探测（返回设备号 + 说明）。null = 不探测。</param>
    /// <param name="clipboard">剪贴板服务（owner 窗口在**创建它的线程**）。生产环境在主线程创建。</param>
    public CoreController(
        Config config,
        Func<string, IAsrBackend>? backendFactory = null,
        Func<int?, IRecorder>? recorderFactory = null,
        Func<string, IntPtr, bool, bool, InjectResult>? injector = null,
        NotificationHub? notifier = null,
        Action<short[], int>? recordingSaver = null,
        Func<(int? Index, string Description)>? micProbe = null,
        ClipboardService? clipboard = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));

        _notifier = notifier ?? NotificationHub.Notify;
        _backendFactory = backendFactory ?? DefaultBackendFactory;
        _recorderFactory = recorderFactory;
        _recordingSaver = recordingSaver;
        _micProbe = micProbe;

        // 剪贴板 owner 窗口必须在**主线程**创建（owner 窗口归创建它的线程，
        // 线程退出窗口就没了，而 Windows 在 owner 窗口销毁时连带清空剪贴板）。
        // 控制器在主线程构造 → 这里创建的 ClipboardService 就是 owner 线程。
        Clipboard = clipboard ?? new ClipboardService();
        _injector = injector
            ?? ((text, hwnd, restore, trailingSpace) =>
                new Injector(Clipboard).PasteText(text, hwnd, restore, trailingSpace));

        BackendKind = NormalizeBackend(config.Backend);
        Engine = MakeBackend(BackendKind);

        _state = new AppState
        {
            State = AppStates.Starting,
            Backend = BackendKind,
            BackendHealth = new BackendHealth(HealthStatuses.Loading),
            Model = CurrentModelTag(),
            PreviewText = string.Empty,
        };
    }

    /// <summary>配置（字段名与 Python 版逐字一致）。</summary>
    public Config Config { get; }

    /// <summary>剪贴板服务（退出前要 <see cref="ClipboardService.ReleaseOwnership"/>）。</summary>
    public ClipboardService Clipboard { get; }

    /// <summary>控制器内部状态机。</summary>
    public ControllerState State { get; private set; } = ControllerState.Starting;

    /// <summary>当前生效的后端（本地模型 / 网络 API）。两种后端互斥。</summary>
    public string BackendKind { get; private set; }

    /// <summary>当前生效的后端实例。</summary>
    public IAsrBackend Engine { get; private set; }

    /// <summary>最近一次识别出来的文本（注入失败时也留着，用户能复制走）。</summary>
    public string LastText { get; private set; } = string.Empty;

    /// <summary>最近一次错误文案。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>探测出来的输入设备号（由后台探测填充）。</summary>
    public int? InputDevice { get; private set; }

    /// <summary>当前状态快照（只读）。</summary>
    public AppState StateSnapshot
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// 状态快照变化。
    ///
    /// ⚠ **触发线程**：谁改的状态就在谁的线程上触发（模型加载线程 / 识别线程 /
    /// 调用方线程）。UI 侧要自己 <c>Dispatcher.UIThread.Post</c>。
    /// </summary>
    public event EventHandler<AppState>? StateChanged;

    /// <summary>
    /// 后端健康度变化（loading → ready / failed）。
    ///
    /// ⚠ **触发线程**：同 <see cref="StateChanged"/>。
    /// </summary>
    public event EventHandler<BackendHealth>? BackendHealthChanged;

    // ---------- 后端 ----------

    /// <summary>
    /// 把后端名字规整成合法值（认不出来就当本地）。
    ///
    /// 认不出来时**留一条日志**：静默回退的话，用户点了切后端却什么都没发生，
    /// 只会以为程序坏了。
    /// </summary>
    public static string NormalizeBackend(string? kind)
    {
        string name = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (Array.IndexOf(BackendKinds, name) >= 0)
        {
            return name;
        }

        if (name.Length > 0)
        {
            _log.Warning($"不认识的后端 '{name}',按 {BackendLocal} 处理");
        }

        return BackendLocal;
    }

    /// <summary>默认的后端工厂（本地模型 / 网络 API）。</summary>
    private IAsrBackend DefaultBackendFactory(string kind)
    {
        if (kind == BackendMinimax)
        {
            return new VmNext.Engines.Minimax.MinimaxBackend(
                model: Config.MinimaxModel,
                baseUrl: Config.MinimaxBaseUrl,
                hotwords: Config.Hotwords,
                hotwordsFile: Paths.HotwordsPath);
        }

        return new VmNext.Engines.Local.LocalBackend(
            modelRoot: Config.ModelRoot,
            modelTag: Config.Model,
            language: Config.Language,
            hotwords: Config.Hotwords,
            hotwordsFile: Paths.HotwordsPath);
    }

    /// <summary>造一颗后端（走可注入的工厂）。</summary>
    private IAsrBackend MakeBackend(string kind) => _backendFactory(kind);

    /// <summary>
    /// 当前生效的模型规格（对外快照用）。
    ///
    /// 本地后端给本地模型规格（0.6B / 1.7B）；网络后端给**远端模型名**（asr-1.0）——
    /// 面板上要看得见到底是哪颗远端模型在跑。
    /// </summary>
    private string CurrentModelTag()
    {
        if (BackendKind == BackendMinimax)
        {
            return Engine.ModelId ?? string.Empty;
        }

        return Config.Model ?? string.Empty;
    }

    /// <summary>把一个后端卸掉（本地释放显存 / 网络关会话）。</summary>
    private static void UnloadBackend(IAsrBackend backend)
    {
        try
        {
            backend.Unload();
        }
        catch (Exception ex)
        {
            _log.Debug($"卸载后端失败: {ex.Message}");
        }
    }

    // ---------- 状态订阅 ----------

    /// <summary>
    /// 订阅状态快照。
    ///
    /// 新订阅者**立即**收到当前快照一次 —— 订阅发生在"装配"阶段，
    /// 那时状态可能已经不是初始值了（引擎甚至已经就绪），漏掉这一次的话，
    /// 订阅者得一直等到下一次变化才知道自己身处什么状态。
    /// </summary>
    public void Subscribe(Action<AppState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        AppState snapshot;
        lock (_lock)
        {
            _listeners.Add(callback);
            snapshot = _state;
        }

        CallListener(callback, snapshot);
    }

    /// <summary>
    /// 订阅电平（录音期间 30fps 推一次）。
    ///
    /// 推的是**已映射**电平（<c>LevelPump.DbfsToUnit</c> 的输出，0..1）——
    /// 消费端只负责画，绝不能按窗口内最大值归一化（归一化之后电平条永远是满的，
    /// 用户就没法从条的高低看出"我说话声音大不大"，那条条就只剩装饰作用）。
    /// </summary>
    public void SubscribeLevels(Action<LevelFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_lock)
        {
            _levelListeners.Add(callback);
        }
    }

    private static void CallListener(Action<AppState> cb, AppState snapshot)
    {
        try
        {
            cb(snapshot);
        }
        catch (Exception ex)
        {
            _log.Debug($"状态订阅者回调异常: {ex.Message}");
        }
    }

    /// <summary>改快照并推给订阅者（没变化就不推 —— 免得空推刷前端）。</summary>
    private AppState UpdateState(Func<AppState, AppState> change)
    {
        AppState updated;
        AppState old;
        Action<AppState>[] targets;

        lock (_lock)
        {
            old = _state;
            AppState next = change(old);
            if (next == old)
            {
                return old;
            }

            _state = next;
            updated = next;
            targets = _listeners.ToArray();
        }

        foreach (Action<AppState> cb in targets)
        {
            CallListener(cb, updated);
        }

        // 事件在**锁外**触发：回调里再调控制器不会自锁。
        StateChanged?.Invoke(this, updated);

        if (updated.BackendHealth != old.BackendHealth)
        {
            BackendHealthChanged?.Invoke(this, updated.BackendHealth);
        }

        return updated;
    }

    /// <summary>
    /// 内部状态机推进，并把对外快照跟上。
    ///
    /// V1 的 <c>UIEvent(kind="state")</c> 在这里退役 —— 对外只有
    /// <c>AppState</c> 快照这一条路。
    /// </summary>
    private void SetState(ControllerState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;

        string external = state switch
        {
            ControllerState.Starting => AppStates.Starting,
            ControllerState.Idle => AppStates.Idle,
            ControllerState.Recording => AppStates.Recording,
            ControllerState.Processing => AppStates.Processing,

            // ⚠ ERROR **没有**对外状态：状态本身回到 idle，好让用户能直接再来一轮。
            _ => AppStates.Idle,
        };

        UpdateState(s => s with { State = external });
    }

    // ---------- 通知 ----------

    /// <summary>发通知（失败不许拖垮调用方）。</summary>
    private void Notify(string level, string kind, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        try
        {
            _notifier.Publish(level, kind, message, fields);
        }
        catch (Exception ex)
        {
            _log.Debug($"发通知失败(kind={kind}): {ex.Message}");
        }
    }

    /// <summary>
    /// 命令被拒：记日志 + 发通知 + 返回失败结果。
    ///
    /// 拒绝也要让用户看见 —— 只在日志里写一行的话，用户按了热键什么反应都没有，
    /// 只会以为程序死了。
    /// </summary>
    private CommandResult Reject(string kind, string reason, string message)
    {
        _log.Info($"命令被拒({reason}): {message}");
        Notify("error", kind, message, new Dictionary<string, object?> { ["reason"] = reason });
        return new CommandResult(false, reason, message);
    }

    /// <summary>VAD 降级回调（同一原因只通知一次）。</summary>
    private void OnVadFallback(string reason, string message)
    {
        bool first;
        lock (_lock)
        {
            first = _notifiedVadReasons.Add(reason);
        }

        if (first)
        {
            Notify("warning", NotifyKindVadFallback, message, new Dictionary<string, object?> { ["reason"] = reason });
        }
        else
        {
            _log.Warning(message);
        }
    }

    // ---------- 启动 ----------

    /// <summary>后台初始化：加载模型 + 探测麦克风。</summary>
    public void Start()
    {
        var model = new Thread(InitModel)
        {
            IsBackground = true,
            Name = "model-load",
        };
        var mic = new Thread(InitMic)
        {
            IsBackground = true,
            Name = "mic-warmup",
        };

        model.Start();
        mic.Start();
    }

    private void InitModel()
    {
        try
        {
            // 后端换装和启动加载共用一把锁：不会交叉。
            lock (_backendLock)
            {
                Engine.Load();
                _log.Info("模型就绪");
                UpdateState(s => s with
                {
                    BackendHealth = new BackendHealth(HealthStatuses.Ready),
                    Backend = BackendKind,
                    Model = CurrentModelTag(),
                });
            }

            // 模型好了但麦克风可能还在探测，两边都好了才算真正就绪
            if (State == ControllerState.Starting)
            {
                SetState(ControllerState.Idle);
            }
        }
        catch (AsrError ex)
        {
            LastError = ex.Message;
            SetState(ControllerState.Error);
            UpdateState(s => s with { BackendHealth = new BackendHealth(HealthStatuses.Failed, ex.Message) });
            Notify("error", "backend_load_failed", $"识别引擎加载失败:{ex.Message}",
                new Dictionary<string, object?> { ["backend"] = BackendKind });
            _log.Error($"模型加载失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetState(ControllerState.Error);
            UpdateState(s => s with { BackendHealth = new BackendHealth(HealthStatuses.Failed, ex.Message) });
            Notify("error", "backend_load_failed", $"识别引擎加载失败:{ex.Message}",
                new Dictionary<string, object?> { ["backend"] = BackendKind });
            _log.Error($"模型加载异常: {ex}");
        }
    }

    private void InitMic()
    {
        if (_micProbe is null)
        {
            return;
        }

        try
        {
            (int? index, string desc) = _micProbe();
            InputDevice = index;
            _log.Info($"麦克风: {desc}");
            Notify("info", "microphone_ready", $"麦克风 {desc}");
        }
        catch (Exception ex)
        {
            _log.Error($"麦克风探测失败: {ex.Message}");
            Notify("warning", "microphone_unavailable", $"麦克风探测失败:{ex.Message}");
        }
    }

    /// <summary>
    /// 重新探测麦克风（托盘「重新探测麦克风」那条路）。
    ///
    /// 探测要 2 秒，不能放在热键路径上（会把用户说话的开头吃掉）——
    /// 所以这里同步做完再返回。
    /// </summary>
    public void ReloadMic()
    {
        if (_micProbe is null)
        {
            return;
        }

        try
        {
            (int? index, string desc) = _micProbe();
            InputDevice = index;
            Config.InputDevice = index;
            try
            {
                Config.Save();
            }
            catch (Exception ex)
            {
                _log.Debug($"保存麦克风设置失败: {ex.Message}");
            }

            _log.Info($"重新探测麦克风: {desc}");
            Notify("info", "microphone_ready", $"麦克风 {desc}");
        }
        catch (Exception ex)
        {
            _log.Error($"麦克风探测失败: {ex.Message}");
            Notify("warning", "microphone_unavailable", $"麦克风探测失败:{ex.Message}");
        }
    }

    // ---------- 生命周期 ----------

    /// <summary>
    /// 把全局热键的停止动作挂上来 —— 退出时由 <see cref="Shutdown"/> 负责停它。
    ///
    /// 为什么不让入口自己管：退出有三条路（托盘菜单 / 面板按钮 / Ctrl+C），
    /// 每条都得停热键。挂在 controller 上，<see cref="Shutdown"/> 一处收口，就不会漏。
    /// </summary>
    public void AttachHotkey(Action stop)
    {
        _hotkeyStop = stop;
    }

    /// <summary>
    /// 退出收尾。
    ///
    /// 顺序：**热键第一个停** —— 它是全局资源，占着不放的话，用户"退出后马上重启"
    /// 会因为注册冲突而失败（热键注销要等进程真正退出）。也不能排在模型卸载后面
    /// —— 那一步要好几秒。
    /// </summary>
    public void Shutdown()
    {
        Action? hotkey = _hotkeyStop;
        if (hotkey is not null)
        {
            _hotkeyStop = null;
            try
            {
                hotkey();
            }
            catch (Exception)
            {
                // 热键收尾失败不该挡着后面的清理
            }
        }

        StopPreview();
        StopLevelPump();
        StopMaxDurationTimer();

        IRecorder? recorder = _recorder;
        if (recorder is not null)
        {
            _recorder = null;
            try
            {
                recorder.Stop();
            }
            catch (Exception)
            {
                // 停止失败不影响收尾
            }
        }

        lock (_backendLock)
        {
            UnloadBackend(Engine);
        }
    }
}
