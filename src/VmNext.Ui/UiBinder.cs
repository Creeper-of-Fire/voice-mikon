using System;
using System.Collections.Generic;
using Avalonia.Threading;
using VmNext.Core;
using VmNext.Core.Presentation;
using VmNext.Shell;

namespace VmNext.Ui;

/// <summary>
/// UI 绑定层：把 presenter 的显示状态接到窗口上，把窗口的用户动作接回 presenter。
///
/// <para><b>唯一接口依据是 <c>docs/core-api.md</c></b>：事件名、触发线程、mode 枚举、
/// payload 字段、dwell 表都在那份文档里，这里不自己发明。</para>
///
/// <para>两条线：
/// <list type="bullet">
/// <item><b>core → UI</b>：presenter 的事件 → 窗口方法。
/// 事件在**触发它的那条线程**上发出（模型加载线程 / 识别线程 / 电平泵线程），
/// 所以每个回调都 <c>Dispatcher.UIThread.Post</c> 到 UI 线程。</item>
/// <item><b>UI → core</b>：窗口的用户动作 → presenter 的 TryXxx / OnDismissed。
/// 这些调用会同步推进状态机，**不能**在事件回调里等控制器。</item>
/// </list></para>
///
/// <para>通知（<c>NotificationHub.NotificationPosted</c>）**不直接绑到窗口**：
/// core-api.md §3 说显示状态只有一个出口（presenter 翻译后的 mode），
/// 通知由 presenter 翻译成 mode 再推过来，UI 不重复解释一遍通知的 kind。</para>
/// </summary>
public sealed class UiBinder : IDisposable
{
    private static readonly Logger _log = Log.GetLogger("ui.binder");

    private readonly VoiceBarPresenter _presenter;
    private readonly CapsuleWindow _window;
    private bool _disposed;

    /// <summary>造一个绑定层并立刻订阅。</summary>
    /// <param name="presenter">显示状态翻译层。</param>
    /// <param name="window">胶囊窗口。</param>
    public UiBinder(VoiceBarPresenter presenter, CapsuleWindow window)
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
        _window.DismissRequested += OnDismiss;
        _window.CopyRequested += OnCopy;
        _window.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _window.QrScanRequested += (_, _) => QrScanRequested?.Invoke(this, EventArgs.Empty);
        _window.QuitRequested += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用户点了菜单里的退出（由宿主接去走退出顺序）。</summary>
    public event EventHandler? QuitRequested;

    /// <summary>用户点了菜单里的「设置」（面板是 K5，宿主负责回话）。</summary>
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
        _window.DismissRequested -= OnDismiss;
        _window.CopyRequested -= OnCopy;
    }

    // ==================== core → UI ====================

    private void OnDisplayModeChanged(object? sender, DisplayModeChangedEventArgs e)
    {
        // 事件在触发它的那条线程上发出（模型加载线程 / 识别线程 / 调用方线程）。
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
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
    /// ⚠ <b>焦点陷阱</b>（capsule.py 的 <c>_start_recording_from_orb</c>）：
    /// 注入靠"粘贴到前台窗口"，所以开始录音之前必须把前台留给用户原来的窗口 ——
    /// 否则这一轮识别完的文字会贴到胶囊自己身上。
    /// 我们的窗口带 WS_EX_NOACTIVATE，本来就不抢焦点；这里仍然先把前台记下来，
    /// 动作做完再核一遍，焦点跑掉了就还回去。
    /// </summary>
    private void OnStartRecording(object? sender, EventArgs e)
    {
        IntPtr userForeground = NativeMethods.GetForegroundWindow();

        var (ok, hint) = _presenter.TryStartRecording();

        if (!ok && hint.Length > 0)
        {
            // 模型还在加载：controller 拒绝了，但"点了没反应"在用户眼里
            // 就是"球坏了"。给一句提示，别让人对着沉默发懵。
            _window.ShowOrbHint(hint);
            return;
        }

        GiveBackFocus(userForeground);
    }

    private void OnStopRecording(object? sender, EventArgs e) => _presenter.TryStopRecording();

    private void OnCancelRecording(object? sender, EventArgs e) => _presenter.TryCancelRecording();

    private void OnDismiss(object? sender, EventArgs e)
    {
        // 只有"带原文的错误常驻态"才认这个动作；其它情况静默忽略（幂等）。
        if (!_presenter.OnDismissed())
        {
            _log.Debug("dismiss 被忽略（当前不是带原文的错误常驻态）");
        }
    }

    private void OnCopy(object? sender, EventArgs e)
    {
        bool ok = _presenter.TryCopyResult();
        if (!ok)
        {
            // 写不进剪贴板：提示一下，用户可以再试（带原文的错误态不自动收回）。
            _window.ShowOrbHint("复制失败，再试一次");
        }
    }

    /// <summary>把前台还给用户原来的窗口（胶囊自己不占着前台）。</summary>
    private static void GiveBackFocus(IntPtr userForeground)
    {
        if (userForeground == IntPtr.Zero)
        {
            return;
        }

        IntPtr current = NativeMethods.GetForegroundWindow();
        if (current == IntPtr.Zero || current == userForeground)
        {
            return;
        }

        try
        {
            NativeMethods.SetForegroundWindow(userForeground);
        }
        catch (Exception ex)
        {
            _log.Debug($"还前台失败: {ex.Message}");
        }
    }
}
