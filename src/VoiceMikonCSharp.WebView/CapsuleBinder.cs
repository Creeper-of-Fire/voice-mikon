using System;
using VoiceMikonCSharp.Core;
using VoiceMikonCSharp.Core.Presentation;

namespace VoiceMikonCSharp.WebView;

/// <summary>
/// UI 绑定层：把 presenter 的显示状态接到胶囊窗口上，把窗口的用户动作接回 presenter。
///
/// <para><b>唯一接口依据是 <c>docs/core-api.md</c></b>：事件名、触发线程、mode 枚举、
/// payload 字段、dwell 表都在那份文档里，这里不自己发明。</para>
///
/// <para>两条线：
/// <list type="bullet">
/// <item><b>core → UI</b>：presenter 的事件 → 窗口方法。
/// 事件在**触发它的那条线程**上发出（模型加载线程 / 识别线程 / 电平泵线程），
/// 所以每个回调都切回 UI 线程（<see cref="UiDispatch.Post"/>）。</item>
/// <item><b>UI → core</b>：窗口的用户动作 → presenter 的 TryXxx / OnDismissed。
/// 这些调用会同步推进状态机，**不能**在事件回调里等控制器。</item>
/// </list></para>
///
/// <para>通知（<c>NotificationHub.NotificationPosted</c>）**不直接绑到窗口**：
/// 显示状态只有一个出口（presenter 翻译后的 mode），UI 不重复解释一遍通知的 kind。</para>
/// </summary>
public sealed class CapsuleBinder : IDisposable
{
    private static readonly Logger _log = Log.GetLogger("webview.binder");

    private readonly VoiceBarPresenter _presenter;
    private readonly CapsuleWindow _window;
    private bool _disposed;

    /// <summary>造一个绑定层并立刻订阅。</summary>
    /// <param name="presenter">显示状态翻译层。</param>
    /// <param name="window">胶囊窗口。</param>
    public CapsuleBinder(VoiceBarPresenter presenter, CapsuleWindow window)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _window = window ?? throw new ArgumentNullException(nameof(window));

        _presenter.DisplayModeChanged += OnDisplayModeChanged;
        _presenter.PreviewTextChanged += OnPreviewTextChanged;
        _presenter.LevelsChanged += OnLevelsChanged;
        _presenter.ThemeChanged += OnThemeChanged;
        _presenter.PreviewEnabledChanged += OnPreviewEnabledChanged;
        _presenter.OrbVisibilityChanged += OnOrbVisibilityChanged;

        _window.StartRecordingRequested += OnStartRecording;
        _window.StopRecordingRequested += OnStopRecording;
        _window.CancelRecordingRequested += OnCancelRecording;
        _window.DismissRequested += OnDismissRequest;
        _window.CopyRequested += OnCopyRequest;
        _window.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _window.QrScanRequested += (_, _) => QrScanRequested?.Invoke(this, EventArgs.Empty);
        _window.QuitRequested += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用户点了菜单里的退出（由宿主接去走退出顺序）。</summary>
    public event EventHandler? QuitRequested;

    /// <summary>用户点了菜单里的「设置」（面板由宿主打开）。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>用户点了菜单里的「二维码扫描」（宿主去拉起附属工具的独立进程）。</summary>
    public event EventHandler? QrScanRequested;

    /// <summary>窗口（不创建 UI 时也能拿到状态机之外的引用）。</summary>
    public CapsuleWindow Window => _window;

    /// <summary>解绑（退订 + 断开事件）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _presenter.DisplayModeChanged -= OnDisplayModeChanged;
        _presenter.PreviewTextChanged -= OnPreviewTextChanged;
        _presenter.LevelsChanged -= OnLevelsChanged;
        _presenter.ThemeChanged -= OnThemeChanged;
        _presenter.PreviewEnabledChanged -= OnPreviewEnabledChanged;
        _presenter.OrbVisibilityChanged -= OnOrbVisibilityChanged;

        _window.StartRecordingRequested -= OnStartRecording;
        _window.StopRecordingRequested -= OnStopRecording;
        _window.CancelRecordingRequested -= OnCancelRecording;
        _window.DismissRequested -= OnDismissRequest;
        _window.CopyRequested -= OnCopyRequest;
    }

    // ==================== core → UI ====================

    private void OnDisplayModeChanged(object? sender, DisplayModeChangedEventArgs e)
    {
        UiDispatch.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _window.SetMode(e.Mode, e.Payload);
        });
    }

    private void OnPreviewTextChanged(object? sender, PreviewTextChangedEventArgs e)
    {
        UiDispatch.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _window.SetPreviewText(e.Text);
        });
    }

    private void OnLevelsChanged(object? sender, LevelsChangedEventArgs e)
    {
        UiDispatch.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _window.PushLevels(e.Levels, e.Elapsed);
        });
    }

    private void OnThemeChanged(object? sender, string theme)
    {
        UiDispatch.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _window.SetTheme(theme);
        });
    }

    private void OnPreviewEnabledChanged(object? sender, bool enabled)
    {
        UiDispatch.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _window.SetPreviewEnabled(enabled);
        });
    }

    private void OnOrbVisibilityChanged(object? sender, bool visible)
    {
        UiDispatch.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _window.SetOrbVisible(visible);
        });
    }

    // ==================== UI → core ====================

    /// <summary>
    /// 点球开始录音。
    ///
    /// <para>⚠ <b>焦点陷阱</b>：鼠标点在 WebView2 的子窗口上会激活整棵窗口树，
    /// 前台被胶囊抢走 —— 那一轮识别完的文字就会贴到胶囊自己身上。
    /// 所以开始录音**之前**必须把前台还给用户原先所在的窗口。</para>
    /// </summary>
    private void OnStartRecording(object? sender, EventArgs e)
    {
        _window.PrepareStartRecording();

        (bool ok, string hint) = _presenter.TryStartRecording();
        if (!ok && hint.Length > 0)
        {
            // 模型还在加载：controller 拒绝了，但"点了没反应"在用户眼里
            // 就是"球坏了"。给一句提示，别让人对着沉默发懵。
            _window.ShowOrbHint(hint);
        }
    }

    private void OnStopRecording(object? sender, EventArgs e)
    {
        _window.GiveBackFocusToTarget();
        _presenter.TryStopRecording();
    }

    private void OnCancelRecording(object? sender, EventArgs e)
    {
        _window.GiveBackFocusToTarget();
        _presenter.TryCancelRecording();
    }

    /// <summary>
    /// 用户主动关掉错误提示（带原文的错误常驻态）。
    ///
    /// <para>返回**是否真的处理了** —— 不在"带原文的错误常驻态"时静默忽略（幂等）。</para>
    /// </summary>
    private bool OnDismissRequest()
    {
        bool handled = _presenter.OnDismissed();
        if (!handled)
        {
            _log.Debug("dismiss 被忽略（当前不是带原文的错误常驻态）");
            return false;
        }

        // 非常驻：窗口藏掉；常驻：只清掉"这一轮的胶囊还该显示"的标记（球还在），
        // 胶囊由前端收回成球。
        _window.HideCapsule();
        return true;
    }

    /// <summary>把识别结果手动写进剪贴板（注入失败时的兜底出口）。</summary>
    /// <param name="text">前端手上的原文。</param>
    /// <returns>是否真的写进去了。</returns>
    private bool OnCopyRequest(string text)
    {
        bool ok = _presenter.TryCopyResult();
        if (!ok)
        {
            // 写不进剪贴板：提示一下，用户可以再试（带原文的错误态不自动收回）。
            _window.ShowOrbHint("复制失败，再试一次");
            return false;
        }

        // 写成功就把胶囊收掉。
        _window.HideCapsule();
        return true;
    }
}
