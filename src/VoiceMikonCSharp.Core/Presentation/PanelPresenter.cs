using System;
using System.Collections.Generic;
using VoiceMikonCSharp.Core.Controller;

namespace VoiceMikonCSharp.Core.Presentation;

/// <summary>
/// 面板的完整快照（不可变）。
///
/// <para>翻译自 voice-mikon 的 <c>apps/panel/presenter.py</c> 的 <c>push_full_state</c>：
/// 那边是一串"具名 setter"（<c>set_state</c> / <c>set_health</c> / <c>set_model</c> …），
/// C# 侧收成**一个记录** —— 字段一个不漏，值逐字对应。</para>
///
/// <para>⚠ 纯数据：没有 Avalonia、没有 UI 类型。面板 UI 只认这份快照。</para>
/// </summary>
public sealed record PanelSnapshot
{
    /// <summary>状态机（starting / idle / recording / processing）。</summary>
    public string State { get; init; } = AppStates.Idle;

    /// <summary>识别后端（local / minimax）。</summary>
    public string Backend { get; init; } = CoreController.BackendLocal;

    /// <summary>
    /// 模型规格。本地后端给 <c>0.6B</c> / <c>1.7B</c>，
    /// 网络后端给**远端模型名**（asr-1.0）—— 与 Python 版同一个字段、同一份口径。
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>网络后端下的远端模型名（本地后端为空串）。</summary>
    public string NetworkModel { get; init; } = string.Empty;

    /// <summary>后端健康（loading / ready / failed）。</summary>
    public string HealthStatus { get; init; } = HealthStatuses.Loading;

    /// <summary>失败原因（只有 failed 时有值，常驻显示）。</summary>
    public string HealthReason { get; init; } = string.Empty;

    /// <summary>主题档位（auto / light / dark）。</summary>
    public string Theme { get; init; } = "auto";

    /// <summary>界面形态四档（orb / resident / on_demand / headless）。</summary>
    public string UiMode { get; init; } = "orb";

    /// <summary>录音时要不要显示实时预览文字条。</summary>
    public bool PreviewEnabled { get; init; } = true;

    /// <summary>粘贴后要不要归还原剪贴板内容。</summary>
    public bool RestoreClipboardEnabled { get; init; } = true;

    /// <summary>热键字符串（原始，不含"热键"前缀）。</summary>
    public string Hotkey { get; init; } = string.Empty;

    /// <summary>提示语文本（切模型 / 切后端的进展、拒绝与失败）。</summary>
    public string Notice { get; init; } = string.Empty;

    /// <summary>
    /// 提示语级别（info / warning / error）—— 决定提示语的配色。
    ///
    /// <para>⚠ Python 版**没有**这一项（那边 <c>.notice</c> 只有一个颜色）：
    /// 这是 K5 任务书明确要求的一条（"notice 按 level 配色"），见 docs/k5-panel.md。</para>
    /// </summary>
    public string NoticeLevel { get; init; } = "info";
}

/// <summary>
/// 面板的 presenter：把 core 的状态 / 通知翻译成面板的呈现。
///
/// <para>翻译自 voice-mikon/src/voice_mikon/apps/panel/presenter.py（381 行）。
/// 职责与 Python 版逐条对应：</para>
/// <list type="bullet">
/// <item>订阅 core 的 <see cref="AppState"/> 快照（订阅即投递），
/// 把状态 / 后端健康 / 模型 / 后端翻译成快照；</item>
/// <item>订阅通知流，把**面板发起的操作**（切模型 / 切后端）的进展、拒绝、失败
/// 翻译成面板的提示语；</item>
/// <item>承接面板的动作（切模型、切后端、改配置、退出、关面板）。</item>
/// </list>
///
/// <para>⚠ **不引用任何 UI 类型**（没有 Avalonia）：输出就是一份
/// <see cref="PanelSnapshot"/> 加几个事件。面板 UI 只认这份快照和这几个方法。</para>
///
/// <para>⚠ **线程**：事件在**触发它的那条线程**上发出（模型加载线程 / 识别线程 /
/// 调用方线程），UI 侧要自己 <c>Dispatcher.UIThread.Post</c>。</para>
/// </summary>
public sealed class PanelPresenter : IDisposable
{
    private static readonly Logger _log = Log.GetLogger("apps.panel");

    /// <summary>
    /// 会变成面板提示语的通知 kind。
    ///
    /// <para>只收**面板发起的操作**这一类（切模型 / 切后端）：它们的进展、拒绝、失败
    /// 都需要用户看见 —— "点了没反应"在用户眼里就是"程序坏了"。
    /// 别的通知（录音失败、注入失败…）归胶囊的 presenter，面板不重复显示。</para>
    /// </summary>
    public static readonly string[] NoticeKinds =
    {
        "model_switching",
        "model_switched",
        "model_switch_failed",
        "backend_switching",
        "backend_switched",
        "backend_switch_failed",
    };

    private readonly object _lock = new();
    private readonly IPanelCore _core;
    private readonly IPanelConfig? _config;
    private readonly NotificationHub _notifier;

    /// <summary>最近一次已知的 core 快照（订阅时立刻会收到一次）。</summary>
    private AppState _state = new();

    /// <summary>当前提示语（切模型 / 切后端的进展与失败）。</summary>
    private string _notice = string.Empty;

    /// <summary>当前提示语的级别。</summary>
    private string _noticeLevel = "info";

    /// <summary>已经推出去过的快照（用来去重：同值不重复推）。</summary>
    private PanelSnapshot? _pushed;

    private bool _closed;

    /// <summary>
    /// 造一个面板 presenter。
    /// </summary>
    /// <param name="core">core（窄接口）。</param>
    /// <param name="config">配置（可为 null —— 缺省值按 Python 的 getattr 兜底）。</param>
    /// <param name="notifier">通知中心（默认全局单例）。</param>
    public PanelPresenter(IPanelCore core, IPanelConfig? config = null, NotificationHub? notifier = null)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _config = config;
        _notifier = notifier ?? NotificationHub.Notify;
    }

    /// <summary>快照变化（面板 UI 的**唯一**数据入口）。</summary>
    public event EventHandler<PanelSnapshot>? SnapshotChanged;

    /// <summary>主题档位变化（面板改了主题 —— 胶囊要跟着换）。</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>界面形态变化（面板改了 ui_mode —— 胶囊要当场收球 / 露球）。</summary>
    public event EventHandler<string>? UiModeChanged;

    /// <summary>面板请求退出整个程序（与托盘同一条清理路径）。</summary>
    public event EventHandler? QuitRequested;

    /// <summary>面板请求关闭自己（**不是**退出程序）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>当前快照（没推过就现算一份）。</summary>
    public PanelSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _pushed ?? BuildSnapshot();
            }
        }
    }

    // ---------- 装配 ----------

    /// <summary>
    /// 订阅 core 与通知流。
    ///
    /// <para>core 的订阅是**订阅即投递**的：订阅的瞬间就会把当前快照推过来 ——
    /// 装配期状态可能已经不是初始值了（引擎甚至已经就绪），
    /// 漏掉这一次的话，面板得一直等到下一次变化才知道自己身处什么状态。</para>
    /// </summary>
    public void Install()
    {
        _core.Subscribe(OnCoreState);
        _notifier.Subscribe(OnNotification);
    }

    /// <summary>收尾（幂等）：之后不再往界面推东西。</summary>
    public void Shutdown()
    {
        lock (_lock)
        {
            _closed = true;
        }
    }

    /// <summary>释放（等价于 <see cref="Shutdown"/>）。</summary>
    public void Dispose() => Shutdown();

    // ---------- core 快照 → 快照 ----------

    private void OnCoreState(AppState state)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _state = state;
        }

        PushSnapshot();
    }

    /// <summary>
    /// 把当前已知的全部事实推一遍（core 快照 + 配置 + 提示语）。
    ///
    /// <para>两个时机调它：状态变化时，以及面板刚建好时
    /// （Python 版是"前端就绪"那一刻 —— 那边前端就绪之前的推送都被门控丢掉了，
    /// 这里没有前端，窗口建好时直接读 <see cref="Snapshot"/> 即可）。</para>
    /// </summary>
    private void PushSnapshot()
    {
        PanelSnapshot snapshot;

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            snapshot = BuildSnapshot();

            // 同值不重复推（跟 Python 的 _push 去重账本一个意思：空推会白刷前端）。
            if (_pushed == snapshot)
            {
                return;
            }

            _pushed = snapshot;
        }

        SnapshotChanged?.Invoke(this, snapshot);
    }

    /// <summary>算一份当前快照（调用方必须持有锁）。</summary>
    private PanelSnapshot BuildSnapshot()
    {
        AppState state = _state;
        BackendHealth health = state.BackendHealth;
        bool isNetwork = string.Equals(state.Backend, CoreController.BackendMinimax, StringComparison.Ordinal);

        return new PanelSnapshot
        {
            State = state.State,
            Backend = state.Backend,
            Model = state.Model,
            NetworkModel = isNetwork ? state.Model : string.Empty,
            HealthStatus = health.Status,
            HealthReason = health.Reason,
            Theme = ThemeMode(),
            UiMode = UiModeValue(),
            PreviewEnabled = PreviewEnabled(),
            RestoreClipboardEnabled = RestoreClipboard(),
            Hotkey = Hotkey(),
            Notice = _notice,
            NoticeLevel = _noticeLevel,
        };
    }

    // ---------- 通知流 → 面板提示 ----------

    private void OnNotification(Notification note)
    {
        if (Array.IndexOf(NoticeKinds, note.Kind) < 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _notice = note.Message;
            _noticeLevel = note.Level;
        }

        PushSnapshot();
    }

    /// <summary>
    /// 清掉提示语。
    ///
    /// <para>提示语是**限时**的（面板上 8 秒后自己淡掉）—— 淡掉这件事是"怎么呈现"，
    /// 归 UI 层；但"淡掉之后别再冒出来"得有人管，所以由 UI 层回调这一下，
    /// 让快照里的提示语真正归零。</para>
    /// </summary>
    public void ClearNotice()
    {
        lock (_lock)
        {
            if (_closed || _notice.Length == 0)
            {
                return;
            }

            _notice = string.Empty;
            _noticeLevel = "info";
        }

        PushSnapshot();
    }

    // ---------- 面板动作 ----------

    /// <summary>
    /// 切模型规格（0.6B / 1.7B）。
    ///
    /// <para>只对**本地**后端有意义；网络后端下会被拒绝，
    /// 拒绝原因由通知流反馈（显示成面板提示语）。</para>
    /// </summary>
    public void SwitchModel(string tag)
    {
        try
        {
            _core.SwitchModel(tag ?? string.Empty);
        }
        catch (Exception ex)
        {
            _log.Debug($"切换模型失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 切识别后端（local / minimax）。
    ///
    /// <para>录音 / 识别中会被拒绝 —— 这一轮的最终位引擎持有旧后端的引用，
    /// 中途换掉会出错或丢结果。拒绝时面板显示那句提示。</para>
    /// </summary>
    public void SwitchBackend(string kind)
    {
        try
        {
            _core.SwitchBackend(kind ?? string.Empty);
        }
        catch (Exception ex)
        {
            _log.Debug($"切换后端失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 换主题：存配置 → 推胶囊 → 推面板。
    ///
    /// <para>面板和胶囊共用一套主题（<c>config.theme</c>），所以面板改了就必须让
    /// 胶囊也跟着换 —— 胶囊那边怎么推由装配层接（这里只发事件）。</para>
    /// </summary>
    public void SetTheme(string mode)
    {
        string normalized = (mode ?? "auto").Trim().ToLowerInvariant();
        if (Array.IndexOf(Config.ThemeModes, normalized) < 0)
        {
            _log.Info($"未知主题档位 '{mode}',按 auto 处理");
            normalized = "auto";
        }

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }
        }

        if (_config is not null)
        {
            try
            {
                _config.Theme = normalized;
                _config.Save();
            }
            catch (Exception ex)
            {
                _log.Debug($"保存主题设置失败: {ex.Message}");
            }
        }

        ThemeChanged?.Invoke(this, normalized);
        PushSnapshot();
    }

    /// <summary>开关录音时的实时预览文字条。</summary>
    public void SetPreview(bool enabled)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }
        }

        if (_config is not null)
        {
            try
            {
                _config.ShowPreview = enabled;
                _config.Save();
            }
            catch (Exception ex)
            {
                _log.Debug($"保存预览开关失败: {ex.Message}");
            }
        }

        PushSnapshot();
    }

    /// <summary>
    /// 开关"粘贴后归还原剪贴板内容"。
    ///
    /// <para>关掉之后：识别文本**一直留在剪贴板里**（随时能复制走），而且
    /// 不再有"还原那一次写入" —— 那次写入带着我们的 owner 窗口，
    /// 剪贴板管理器会把"原内容"这条记录的来源标成我们，原本的来源就被冲掉了。
    /// 有剪贴板管理器时，关掉才是顺手的方向。</para>
    /// </summary>
    public void SetRestoreClipboard(bool enabled)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }
        }

        if (_config is not null)
        {
            try
            {
                _config.RestoreClipboard = enabled;
                _config.Save();
            }
            catch (Exception ex)
            {
                _log.Debug($"保存剪贴板归还开关失败: {ex.Message}");
            }
        }

        PushSnapshot();
    }

    /// <summary>
    /// 切换界面形态：存配置 → 推胶囊（当场生效）→ 推面板。
    ///
    /// <para>胶囊那侧必须当场收到：只写 config 的话，球还挂在屏幕上，
    /// 用户会觉得"切了没反应"。</para>
    ///
    /// <para>on_demand 存的是**意图**（config 保存原值，运行期按无球形态执行）；
    /// headless 到不了这里（面板上那一档是 disabled）—— 防御性忽略。</para>
    /// </summary>
    public void SetUiMode(string mode)
    {
        string normalized = (mode ?? "orb").Trim().ToLowerInvariant();
        if (Array.IndexOf(Config.UiModes, normalized) < 0 || normalized == "headless")
        {
            _log.Info($"忽略非法的界面形态 '{mode}'");
            return;
        }

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }
        }

        if (_config is not null)
        {
            try
            {
                _config.UiMode = normalized;
                _config.Save();
            }
            catch (Exception ex)
            {
                _log.Debug($"保存界面形态失败: {ex.Message}");
            }
        }

        UiModeChanged?.Invoke(this, normalized);
        PushSnapshot();
    }

    /// <summary>关闭面板（**不是**退出程序）。</summary>
    public void ClosePanel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>退出整个程序（与托盘**同一条**清理路径，由装配层接）。</summary>
    public void QuitApp() => QuitRequested?.Invoke(this, EventArgs.Empty);

    // ---------- 配置读值 ----------

    /// <summary>当前主题档位（认不出来按 auto）。</summary>
    private string ThemeMode()
    {
        string mode = (_config?.Theme ?? "auto").Trim().ToLowerInvariant();
        return Array.IndexOf(Config.ThemeModes, mode) >= 0 ? mode : "auto";
    }

    /// <summary>实时预览文字条是否开着。</summary>
    private bool PreviewEnabled() => _config?.ShowPreview ?? true;

    /// <summary>要不要在粘贴后归还原剪贴板内容（读不到按"要"处理）。</summary>
    private bool RestoreClipboard() => _config?.RestoreClipboard ?? true;

    /// <summary>当前界面形态（四档之一，读不到 / 不认识按 orb）。</summary>
    private string UiModeValue()
    {
        string mode = (_config?.UiMode ?? "orb").Trim().ToLowerInvariant();
        return Array.IndexOf(Config.UiModes, mode) >= 0 ? mode : "orb";
    }

    /// <summary>当前热键（原始字符串，不含"热键"前缀）。</summary>
    private string Hotkey() => _config?.Hotkey ?? string.Empty;
}
