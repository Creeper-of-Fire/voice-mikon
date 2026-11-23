using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using VmNext.Core.Controller;
using VmNext.Core.Levels;

namespace VmNext.Core.Presentation;

/// <summary>
/// 把 core 的状态 / 通知翻译成胶囊的显示状态。
///
/// 翻译自 voice-mikon/src/voice_mikon/apps/voicebar/presenter.py。
///
/// 显示状态的**唯一真相**是 <c>_mode</c>：每次状态变化都先改 _mode，
/// 再经 <c>PushMode()</c> 推给前端。前端只演，不决策 ——
/// 什么时候发生（停多久、到点回哪）全归这里的 dwell 表。
///
/// ⚠ **不引用任何 UI 类型**（没有 Avalonia）：输出就是 mode 字符串 + payload 对象。
/// 前端（胶囊）订阅 <see cref="DisplayModeChanged"/> 即可。
///
/// ⚠ **线程**：所有事件都在**触发它的那条线程**上发出（后台线程 / 通知线程），
/// UI 侧要自己 <c>Dispatcher.UIThread.Post</c>。
/// </summary>
public sealed class VoiceBarPresenter : IDisposable
{
    // 胶囊的固定文案（对齐 Python presenter 的三个常量）。
    private const string TextResult = "已输入";
    private const string TextCancelled = "已取消";
    private const string TextProcessing = "识别中…";

    private static readonly Logger _log = Log.GetLogger("apps.voicebar");

    private readonly object _lock = new();
    private readonly CoreController _core;
    private readonly Config? _config;
    private readonly NotificationHub _notifier;

    // 显示状态的唯一真相
    private string _mode = DisplayModes.Orb;
    private string _message = string.Empty;
    private string _text = string.Empty;
    private bool _canCopy;
    private string _reason = string.Empty;
    private string _previewText = string.Empty;

    private readonly string _theme;
    private readonly bool _showPreview;

    /// <summary>界面形态四档（悬浮球 / 常驻 / 按需 / 无界面）。</summary>
    private string _uiMode;

    private AppState? _coreState;

    private double _roundStart;
    private bool _roundFailed;
    private bool _pendingCancel;

    // dwell 定时器 + 它期待的(状态, 表键)：
    // 到点时 _mode 对不上就作废 —— 期间有新事实，这次转移不算数。
    private Timer? _dwellTimer;
    private (string Mode, string Key)? _dwellExpect;
    private bool _closed;

    /// <summary>显示状态变化（mode + payload）。</summary>
    public event EventHandler<DisplayModeChangedEventArgs>? DisplayModeChanged;

    /// <summary>预览文字变化（整段替换）。</summary>
    public event EventHandler<PreviewTextChangedEventArgs>? PreviewTextChanged;

    /// <summary>电平帧（30fps）。</summary>
    public event EventHandler<LevelsChangedEventArgs>? LevelsChanged;

    /// <summary>主题档位变化（"auto" / "light" / "dark"）。</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>预览开关变化。</summary>
    public event EventHandler<bool>? PreviewEnabledChanged;

    /// <summary>悬浮球显隐变化（界面形态不是 orb 时藏球）。</summary>
    public event EventHandler<bool>? OrbVisibilityChanged;

    /// <summary>
    /// 造一个 presenter。
    /// </summary>
    /// <param name="core">核心控制器。</param>
    /// <param name="config">配置（可为 null —— 缺省值按 Python 的 getattr 兜底）。</param>
    /// <param name="notifier">通知中心（默认全局单例）。</param>
    public VoiceBarPresenter(CoreController core, Config? config = null, NotificationHub? notifier = null)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _config = config;
        _notifier = notifier ?? NotificationHub.Notify;

        _theme = config?.Theme is { Length: > 0 } t ? t : "auto";
        _showPreview = config?.ShowPreview ?? true;

        // 界面形态四档（悬浮球 / 常驻 / 按需 / 无界面）。胶囊这边只关心
        // **是不是 orb**：orb 露球，其余形态无球（窗口该藏就藏）。
        // on_demand 存的是意图，运行期按无球形态执行。
        string mode = (config?.UiMode is { Length: > 0 } m ? m : "orb").Trim().ToLowerInvariant();
        _uiMode = Array.IndexOf(Config.UiModes, mode) >= 0 ? mode : "orb";
    }

    /// <summary>当前显示状态（诊断 / 测试用）。</summary>
    public string CurrentMode
    {
        get
        {
            lock (_lock)
            {
                return _mode;
            }
        }
    }

    /// <summary>当前界面形态（orb / resident / on_demand / headless）。</summary>
    public string UiMode
    {
        get
        {
            lock (_lock)
            {
                return _uiMode;
            }
        }
    }

    /// <summary>订阅 core 的状态 / 电平与通知流。</summary>
    public void Install()
    {
        _core.Subscribe(OnCoreState);
        _core.SubscribeLevels(OnLevels);
        _notifier.Subscribe(OnNotification);
    }

    /// <summary>启动态：定下初始显示状态并推出去。</summary>
    public void Start()
    {
        string theme;
        bool showPreview;
        string uiMode;

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            BackendHealth? health = _coreState?.BackendHealth;
            if (health is not null && health.Failed)
            {
                _mode = DisplayModes.Error;
                _message = health.Reason.Length > 0 ? health.Reason : "识别引擎不可用";
            }
            else if (health is not null && health.Loading)
            {
                _mode = DisplayModes.Starting;
            }
            else
            {
                _mode = DisplayModes.Orb;
            }

            theme = _theme;
            showPreview = _showPreview;
            uiMode = _uiMode;
        }

        ThemeChanged?.Invoke(this, theme);
        PreviewEnabledChanged?.Invoke(this, showPreview);

        // 启动态也走同一条出口：显示状态只有一个出口。
        PushMode();

        if (!string.Equals(uiMode, "orb", StringComparison.Ordinal))
        {
            // 无球形态（resident / on_demand）：窗口收着，热键才弹胶囊。
            OrbVisibilityChanged?.Invoke(this, false);
        }

        // 启动时的后端不可用：错误文案按 dwell 表走（和运行中一样）。
        ScheduleDwell(DisplayModes.Error, DisplayModes.Error);
    }

    /// <summary>收尾（幂等）。</summary>
    public void Shutdown()
    {
        lock (_lock)
        {
            _closed = true;
            CancelDwell();
        }
    }

    /// <summary>释放（等价于 Shutdown）。</summary>
    public void Dispose() => Shutdown();

    // ---------- 界面形态（面板推过来） ----------

    /// <summary>
    /// 面板改了界面形态（四档单选）：当场兑现。
    ///
    /// 面板那边改配置是**即时**的，这边不能只写 config —— 球的显隐得当场兑现，
    /// 不然用户点了档位什么都没发生。
    ///
    /// * orb：露球（常驻）。
    /// * resident / on_demand：无球，把球收起来。
    /// * headless：面板在 headless 下根本不存在，这个值到不了这里；防御性收着即可。
    ///
    /// <paramref name="mode"/> 变了才动窗口（值没变就不动作）。
    /// </summary>
    public void ApplyUiMode(string mode)
    {
        string normalized = (mode ?? "orb").Trim().ToLowerInvariant();
        if (Array.IndexOf(Config.UiModes, normalized) < 0)
        {
            normalized = "orb";
        }

        bool changed;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            changed = normalized != _uiMode;
            _uiMode = normalized;
        }

        if (!changed)
        {
            return;
        }

        OrbVisibilityChanged?.Invoke(this, string.Equals(normalized, "orb", StringComparison.Ordinal));
    }

    // ---------- core 状态 ----------

    private void OnCoreState(AppState state)
    {
        bool previewChanged;
        string current;

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _coreState = state;
            string preview = state.PreviewText ?? string.Empty;
            previewChanged = !string.Equals(preview, _previewText, StringComparison.Ordinal);
            _previewText = preview;
            current = state.State;
        }

        if (string.Equals(current, AppStates.Recording, StringComparison.Ordinal))
        {
            EnterExpanding();
        }
        else if (string.Equals(current, AppStates.Processing, StringComparison.Ordinal))
        {
            EnterProcessing();
        }
        else if (string.Equals(current, AppStates.Idle, StringComparison.Ordinal))
        {
            EnterIdle();
        }
        else if (string.Equals(current, AppStates.Starting, StringComparison.Ordinal))
        {
            EnterStarting();
        }

        if (previewChanged)
        {
            PushPreview();
        }
    }

    private void PushPreview()
    {
        string preview;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            preview = _previewText;
        }

        PreviewTextChanged?.Invoke(this, new PreviewTextChangedEventArgs(preview));
    }

    /// <summary>
    /// 把当前显示状态推出去 —— 显示状态的**唯一出口**。
    ///
    /// payload 统一带齐四个字段（见 <see cref="DisplayPayload"/>），由前端按 mode 取用：
    /// 前端不猜"这一态该有什么字段"。
    /// </summary>
    private void PushMode()
    {
        string mode;
        DisplayPayload payload;

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            mode = _mode;
            payload = new DisplayPayload(_message, _text, _canCopy, _reason);
        }

        DisplayModeChanged?.Invoke(this, new DisplayModeChangedEventArgs(mode, payload));
    }

    private void EnterExpanding()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (_mode is DisplayModes.Expanding or DisplayModes.Recording)
            {
                return;
            }

            _roundStart = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            _roundFailed = false;
            _pendingCancel = false;
            _message = string.Empty;
            _text = string.Empty;
            _canCopy = false;
            _reason = string.Empty;
            _previewText = string.Empty;
            _mode = DisplayModes.Recording;
        }

        // 新一轮录音：前端的清场并进 applyMode('recording')，
        // 这里只管"进入录音"这件事本身。
        PushMode();
    }

    private void EnterProcessing()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (_mode is not (DisplayModes.Expanding or DisplayModes.Recording))
            {
                return;
            }

            _mode = DisplayModes.Processing;
            _message = TextProcessing;
        }

        PushMode();
    }

    private void EnterIdle()
    {
        string? dwellKey = null;
        string mode;

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            mode = _mode;
            if (mode is DisplayModes.Expanding or DisplayModes.Recording)
            {
                // 录音中回到 idle = 这一轮被放弃
                _mode = DisplayModes.Cancelled;
                _message = TextCancelled;
                _text = string.Empty;
                _canCopy = false;
                dwellKey = DisplayModes.Cancelled;
            }
            else if (mode == DisplayModes.Processing)
            {
                if (_pendingCancel)
                {
                    _mode = DisplayModes.Cancelled;
                    _message = TextCancelled;
                    _text = string.Empty;
                    _canCopy = false;
                    dwellKey = DisplayModes.Cancelled;
                }
                else if (_roundFailed)
                {
                    // 这一轮已经以失败告终（错误态在等着用户）：
                    // 别再用"结果"把它盖掉。
                    return;
                }
                else
                {
                    _mode = DisplayModes.Result;
                    _message = TextResult;
                    _text = _core.LastText ?? string.Empty;
                    _canCopy = false;
                    dwellKey = DisplayModes.Result;
                }
            }
            else if (mode == DisplayModes.Starting)
            {
                // 模型就绪：回球形态
                _mode = DisplayModes.Orb;
                _message = string.Empty;
            }
            else
            {
                return;
            }

            mode = _mode;
        }

        PushMode();

        // 终态统一查表：停多久、到点回哪，dwell 表说了算。
        ScheduleDwell(mode, dwellKey);
    }

    private void EnterStarting()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (_mode is DisplayModes.Expanding or DisplayModes.Recording or DisplayModes.Processing)
            {
                return;
            }

            BackendHealth? health = _coreState?.BackendHealth;
            if (health is not null && health.Failed)
            {
                return;
            }

            if (health is not null && health.Ready)
            {
                _mode = DisplayModes.Orb;
                _message = string.Empty;
            }
            else
            {
                _mode = DisplayModes.Starting;
                _message = string.Empty;
            }
        }

        PushMode();
    }

    // ---------- 通知 ----------

    private void OnNotification(Notification note)
    {
        switch (note.Kind)
        {
            case "recording_rejected":
                EnterRejected(note);
                break;

            case "asr_failed":
                EnterError(note, string.Empty);
                break;

            case "inject_failed":
                EnterError(note, FieldText(note, "text"));
                break;

            case "modifier_wait":
                // 注入在等用户松开修饰键（识别太快、手指还在修饰键上）。
                // 显示切**警告两层形态**（与注入失败的警告同一家族）：
                // 胶囊中段短词 + 文字条完整说明。状态位仍是 PROCESSING
                // （松开后自然流转 result），这里是**瞬态显示覆盖** ——
                // 不改 _message 等常驻字段，免得 result 流转时还要清理。
                lock (_lock)
                {
                    if (_closed || _mode != DisplayModes.Processing)
                    {
                        return;
                    }
                }

                // 正文用通知带来的**最终识别文本**（等键发生在识别完成后，
                // 它就是即将贴出去的那份）—— 不用预览拼接版：
                // 预览与整段识别可能有差异，显示必须与事实一致。
                DisplayModeChanged?.Invoke(
                    this,
                    new DisplayModeChangedEventArgs(
                        DisplayModes.Processing,
                        new DisplayPayload(
                            Message: note.Message,
                            Text: "等你松键",
                            Warn: true,
                            Body: FieldText(note, "text"))));
                break;

            case "backend_load_failed":
                EnterBackendFailed(note);
                break;

            case "backend_switch_failed":
                // 切换后端失败（比如切 MiniMax 没配 key）—— 这条必须让悬浮窗看见：
                // 面板可能正缩在托盘里，用户点了切换什么反应都没有，
                // 只会以为程序死了。错误态无原文，普通 4.5s 收回。
                EnterBackendFailed(note);
                break;
        }
    }

    private static string FieldText(Notification note, string key)
    {
        if (note.Fields.TryGetValue(key, out object? value) && value is not null)
        {
            return value.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private void EnterError(Notification note, string text)
    {
        bool canCopy;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _roundFailed = true;
            _mode = DisplayModes.Error;
            _message = note.Message;
            _text = text;
            _canCopy = !string.IsNullOrWhiteSpace(text);
            _reason = FieldText(note, "reason");
            canCopy = _canCopy;
        }

        PushMode();

        // 带原文的错误态**无自动转移**（表里的值是 null）：用户要反复点「复制」重试，
        // 自动收回会把重试的口子堵上。
        if (canCopy)
        {
            ScheduleDwell(DisplayModes.Error, DisplayModes.ErrorWithText);
        }
        else
        {
            ScheduleDwell(DisplayModes.Error, DisplayModes.Error);
        }
    }

    /// <summary>
    /// 用户主动关掉了错误提示（带原文的错误常驻态）。
    ///
    /// 返回**是否真的处理了** —— 只有"带原文的错误常驻态"才认这个动作：
    /// 录音中、正常结果态、普通错误态收到 dismiss 一律静默忽略（幂等），
    /// 免得误点把正在录的一轮收掉。
    ///
    /// 带原文的错误态**刻意不排 dwell**：用户可能要反复点「复制」重试，
    /// 自动收回会把重试的出口堵上 —— 收回交给用户点 ✕ 或开始下一轮录音。
    /// </summary>
    public bool OnDismissed()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            if (_mode != DisplayModes.Error || !_canCopy)
            {
                return false;
            }

            _mode = DisplayModes.Orb;
            _message = string.Empty;
            _text = string.Empty;
            _canCopy = false;
            _reason = string.Empty;
        }

        PushMode();
        return true;
    }

    private void EnterRejected(Notification note)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _mode = DisplayModes.Rejected;
            _message = note.Message;
            _reason = FieldText(note, "reason");
            _text = string.Empty;
            _canCopy = false;
        }

        PushMode();

        // 被拒的状态本身只停 0.6s（hint 气泡的淡出是纯视觉，留前端）
        ScheduleDwell(DisplayModes.Rejected, DisplayModes.Rejected);
    }

    private void EnterBackendFailed(Notification note)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (_mode is DisplayModes.Expanding or DisplayModes.Recording or DisplayModes.Processing)
            {
                return;
            }

            _mode = DisplayModes.Error;
            _message = note.Message;
            _text = string.Empty;
            _canCopy = false;
        }

        PushMode();
        ScheduleDwell(DisplayModes.Error, DisplayModes.Error);
    }

    // ---------- 电平 ----------

    private void OnLevels(LevelFrame frame)
    {
        double elapsed;

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (_mode is not (DisplayModes.Expanding or DisplayModes.Recording))
            {
                return;
            }

            elapsed = _roundStart > 0
                ? (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) - _roundStart
                : 0.0;
        }

        LevelsChanged?.Invoke(this, new LevelsChangedEventArgs(frame.Levels, Math.Max(0.0, elapsed)));
    }

    // ---------- 用户动作 ----------

    /// <summary>
    /// 点球开始录音。返回（成功, 提示语）—— 提示语非空时球上冒一句。
    /// </summary>
    public (bool Ok, string Hint) TryStartRecording()
    {
        CommandResult result = _core.StartRecording();
        if (result.Ok)
        {
            return (true, string.Empty);
        }

        bool loading = false;
        try
        {
            BackendHealth health = _core.StateSnapshot.BackendHealth;
            loading = health.Loading;
        }
        catch (Exception)
        {
            loading = false;
        }

        return loading ? (false, "模型加载中…") : (false, string.Empty);
    }

    /// <summary>点球 / 热键停止录音。</summary>
    public void TryStopRecording()
    {
        _core.StopRecording();
    }

    /// <summary>放弃当前这一轮。</summary>
    public void TryCancelRecording()
    {
        lock (_lock)
        {
            _pendingCancel = true;
        }

        CommandResult result = _core.CancelRecording();
        if (!result.Ok)
        {
            lock (_lock)
            {
                _pendingCancel = false;
            }
        }
    }

    /// <summary>
    /// 把最近一次识别结果复制到剪贴板（「复制」按钮那条路）。
    ///
    /// ⚠ 这一条 Python 版**没有**对应实现（那边胶囊自己管剪贴板）。
    /// 这里把它收到 presenter 上，是因为 UI 阶段只跟 core/presenter 打交道，
    /// 不该让 UI 直接摸剪贴板。
    /// </summary>
    public bool TryCopyResult()
    {
        string text = _core.LastText ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            return _core.Clipboard.SetTextVerified(text).Ok;
        }
        catch (Exception ex)
        {
            _log.Debug($"复制结果失败: {ex.Message}");
            return false;
        }
    }

    // ---------- dwell ----------

    /// <summary>
    /// 按 dwell 表排一次自动转移。
    ///
    /// <paramref name="mode"/> 是**排表时**的状态，<paramref name="key"/> 是表里的键：
    /// 带原文的错误态和普通错误态是同一个 mode，查表用的键不一样，所以分开传。
    ///
    /// 排新表会把旧表撤掉（同一状态只该有一张表在飞）。
    /// </summary>
    private void ScheduleDwell(string mode, string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        double? seconds = DisplayProtocol.DwellFor(key);
        if (seconds is null)
        {
            // 无自动转移（带原文的错误态）：不排。
            return;
        }

        Timer timer;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (!string.Equals(_mode, mode, StringComparison.Ordinal))
            {
                return;
            }

            CancelDwell();
            _dwellExpect = (mode, key);
            timer = new Timer(_ => OnDwellExpired(), null, Timeout.Infinite, Timeout.Infinite);
            _dwellTimer = timer;
        }

        timer.Change(TimeSpan.FromSeconds(seconds.Value), Timeout.InfiniteTimeSpan);
    }

    /// <summary>撤掉排着的 dwell 转移（只在新排一张表时用）。</summary>
    private void CancelDwell()
    {
        Timer? timer = _dwellTimer;
        _dwellTimer = null;
        _dwellExpect = null;
        timer?.Dispose();
    }

    private void OnDwellExpired()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            (string Mode, string Key)? expect = _dwellExpect;
            _dwellTimer = null;
            _dwellExpect = null;

            // 到点先比状态：期间有新事实（状态已经变了）→ 本次转移作废。
            // 不做"到处显式取消定时器"那套 —— 比一下就够了。
            if (expect is null || !string.Equals(_mode, expect.Value.Mode, StringComparison.Ordinal))
            {
                return;
            }

            _mode = DisplayModes.Orb;
            _message = string.Empty;
            _text = string.Empty;
            _canCopy = false;
            _reason = string.Empty;
            _previewText = string.Empty;
        }

        PushMode();

        // 回球形态后：orb 档窗口自然留着（前端收回球形态）；
        // 无球档（resident / on_demand）由 host 层在 MODE_ORB 时把窗口藏掉。
    }
}
