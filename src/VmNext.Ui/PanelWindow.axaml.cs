using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VmNext.Core;
using VmNext.Core.Controller;
using VmNext.Core.Presentation;
using VmNext.Shell;

namespace VmNext.Ui;

/// <summary>
/// 面板窗口：voice-mikon 的 <c>panel.html</c> / <c>panel.css</c> / <c>panel.js</c> 的 Avalonia 翻译。
///
/// <para><b>翻译，不是设计。</b>所有数值都来自 panel.css / panel.js，
/// 每个值旁边都有来源注释。CSS 注释里的"为什么"也一并带了过来。</para>
///
/// <para>面板是**配置窗口**：状态、后端健康、模型 / 后端 / 主题 / 界面形态 / 预览 / 归还的开关、退出。
/// 录音过程的反馈（实时预览、电平条、计时）全在胶囊窗口那边。</para>
///
/// <para>⚠ 本类**不认识 CoreController / Config**：数据只从 <see cref="PanelSnapshot"/> 来，
/// 用户动作只往外抛事件 —— 翻译层是 <see cref="PanelPresenter"/>。</para>
/// </summary>
public partial class PanelWindow : Window
{
    /// <summary>
    /// 提示语自动消失的时间（毫秒）。来源：panel.js 的 <c>NOTICE_MS = 8000</c>。
    /// </summary>
    private const int NoticeMs = 8000;

    /// <summary>状态文案。来源：panel.js 的 <c>STATE_TEXT</c>。</summary>
    private static readonly Dictionary<string, string> StateTexts = new(StringComparer.Ordinal)
    {
        [AppStates.Idle] = "待机",
        [AppStates.Starting] = "准备中",
        [AppStates.Recording] = "录音中",
        [AppStates.Processing] = "识别中",
    };

    /// <summary>健康徽章文案。来源：panel.js 的 <c>HEALTH_TEXT</c>。</summary>
    private static readonly Dictionary<string, string> HealthTexts = new(StringComparer.Ordinal)
    {
        [HealthStatuses.Loading] = "加载中",
        [HealthStatuses.Ready] = "就绪",
        [HealthStatuses.Failed] = "失败",
    };

    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromMilliseconds(NoticeMs) };

    /// <summary>正在套用快照（套用期间不把控件变化当成用户动作）。</summary>
    private bool _applying;

    /// <summary>上一次套用的提示语（用来判断"是不是新提示"）。</summary>
    private string _appliedNotice = string.Empty;

    /// <summary>上一次套用的提示语级别。</summary>
    private string _appliedNoticeLevel = "info";

    /// <summary>当前快照（主题切换后要按它重画一遍）。</summary>
    private PanelSnapshot? _snapshot;

    /// <summary>创建窗口。</summary>
    public PanelWindow()
    {
        InitializeComponent();

        Width = PanelGeometry.BaseWidth;
        Height = PanelGeometry.BaseHeight;

        _noticeTimer.Tick += OnNoticeTimeout;

        WireUserActions();
        ApplyKnobTransitions();

        // 主题：档位由**装配层**定（GuiHost 按 config.theme 落一次），
        // 本层只负责"跟随系统档要跟着系统走"（CSS 里靠媒体查询，
        // Avalonia 里靠 PlatformSettings.ColorValuesChanged）。
        // 面板的配色跟胶囊是**两套**（panel.css 自己有一套 token），
        // 但档位是同一个（config.theme）—— 面板改了主题，胶囊那边由装配层推。
        PanelTheme.Applied += OnThemeApplied;
        PanelTheme.AttachPlatformWatcher();
    }

    /// <summary>
    /// 圆钮滑动的过渡。
    ///
    /// <para>CSS L471 / L484：<c>transition: background .18s, border-color .18s</c> 与
    /// <c>transition: transform .18s</c>。Avalonia 的 ToggleSwitch 自己挪圆钮
    /// （<c>Canvas.Left</c>），过渡就挂在它那个属性上 —— 所以这里用
    /// <see cref="ToggleSwitch.KnobTransitions"/>。</para>
    /// </summary>
    private void ApplyKnobTransitions()
    {
        var transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Canvas.LeftProperty,
                Duration = TimeSpan.FromMilliseconds(KnobTransitionMs),
            },
        };

        PreviewSwitch.KnobTransitions = transitions;
        RestoreClipboardSwitch.KnobTransitions = transitions;
    }

    /// <summary>圆钮滑动时长（毫秒）。来源：panel.css 的 <c>.switch-thumb { transition: transform 0.18s }</c>。</summary>
    private const int KnobTransitionMs = 180;

    // ==================== 用户动作 → 事件 ====================

    /// <summary>用户选了本地后端（local）。</summary>
    public event EventHandler<string>? BackendChanged;

    /// <summary>用户选了模型规格（0.6B / 1.7B）。</summary>
    public event EventHandler<string>? ModelChanged;

    /// <summary>用户换了主题档位（auto / light / dark）。</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>用户换了界面形态（orb / resident / on_demand）。</summary>
    public event EventHandler<string>? UiModeChanged;

    /// <summary>用户动了预览开关。</summary>
    public event EventHandler<bool>? PreviewChanged;

    /// <summary>用户动了剪贴板归还开关。</summary>
    public event EventHandler<bool>? RestoreClipboardChanged;

    /// <summary>用户点了关闭钮（关掉面板，**不是**退出程序）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>用户点了退出钮（退出整个程序）。</summary>
    public event EventHandler? QuitRequested;

    /// <summary>
    /// 提示语淡掉了（8 秒到了）。
    /// 宿主收到后转给 presenter 的 <c>ClearNotice()</c> —— 提示语的生命周期
    /// 由"怎么呈现"（本层）与"事实归零"（翻译层）各管一半。
    /// </summary>
    public event EventHandler? NoticeExpired;

    // ==================== 位置记忆 ====================

    /// <summary>
    /// 读上次记住的窗口位置（<c>config.panel_geometry</c>，格式 <c>"x,y,w,h"</c>）。
    /// 宿主注入；返回空串 = 没拖过 → 走默认摆位。
    ///
    /// <para>UI 层不直接碰 Config —— "配置怎么存"留在宿主层（与胶囊的位置记忆同款做法）。</para>
    /// </summary>
    public Func<string>? GeometryProvider { get; set; }

    /// <summary>
    /// 记下窗口位置（宿主注入，负责写配置）。
    /// </summary>
    public Action<string>? GeometrySaver { get; set; }

    // ==================== 快照入口 ====================

    /// <summary>
    /// 套用一份快照（面板的**唯一**数据入口）。
    ///
    /// <para>本方法只"演"：把快照翻译成控件状态。它不判断、不决策 ——
    /// 什么时候发生、值是什么，全归 <see cref="PanelPresenter"/>。</para>
    /// </summary>
    public void ApplySnapshot(PanelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _applying = true;
        try
        {
            _snapshot = snapshot;

            // ---- 状态区 ----
            ApplyStateDot(snapshot.State);
            StateText.Text = StateTexts.TryGetValue(snapshot.State, out string? stateText)
                ? stateText
                : snapshot.State;

            HealthBadgeText.Text = HealthTexts.TryGetValue(snapshot.HealthStatus, out string? healthText)
                ? healthText
                : snapshot.HealthStatus;
            ApplyHealthBadge(snapshot.HealthStatus);

            // 失败原因常驻显示：原因才是有用的那半截（CSS L411-424）。
            bool failed = snapshot.HealthStatus == HealthStatuses.Failed;
            HealthReason.Text = failed ? snapshot.HealthReason : string.Empty;
            HealthReason.IsVisible = failed && snapshot.HealthReason.Length > 0;

            // ---- 后端 ----
            bool isNetwork = snapshot.Backend == CoreController.BackendMinimax;
            BackendLocalItem.IsChecked = !isNetwork;
            BackendMinimaxItem.IsChecked = isNetwork;

            // ---- 模型：后端决定这一行是什么 ----
            // 本地后端下是 0.6B / 1.7B 的切换档；网络后端下是**远端模型名**（只读）。
            ModelRow.IsVisible = !isNetwork;
            NetModelText.IsVisible = isNetwork;
            NetModelText.Text = snapshot.NetworkModel;
            Model06Item.IsChecked = snapshot.Model == "0.6B";
            Model17Item.IsChecked = snapshot.Model == "1.7B";

            // ---- 主题 ----
            ThemeAutoItem.IsChecked = snapshot.Theme == "auto";
            ThemeLightItem.IsChecked = snapshot.Theme == "light";
            ThemeDarkItem.IsChecked = snapshot.Theme == "dark";

            // ---- 界面形态 ----
            UiModeOrbItem.IsChecked = snapshot.UiMode == "orb";
            UiModeResidentItem.IsChecked = snapshot.UiMode == "resident";
            UiModeOnDemandItem.IsChecked = snapshot.UiMode == "on_demand";
            UiModeHeadlessItem.IsChecked = snapshot.UiMode == "headless";

            // ---- 两个开关 ----
            PreviewSwitch.IsChecked = snapshot.PreviewEnabled;
            RestoreClipboardSwitch.IsChecked = snapshot.RestoreClipboardEnabled;

            // ---- 热键 ----
            HotkeyLabel.Text = NormalizeHotkey(snapshot.Hotkey);

            // ---- 提示语 ----
            ApplyNotice(snapshot);
        }
        finally
        {
            _applying = false;
        }

        RefreshShadows();
    }

    // ==================== 生命周期 ====================

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        ApplyInitialPosition();

        // 模板要等布局落定才套上（套上之后才拿得到 PART_Root / PART_Thumb）。
        Dispatcher.UIThread.Post(RefreshShadows, DispatcherPriority.Loaded);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _noticeTimer.Stop();
        PanelTheme.Applied -= OnThemeApplied;
        PanelTheme.DetachPlatformWatcher();

        // 隐藏前把真实位置记下来 —— 用户可能拖完就直接关面板。
        SaveGeometry();

        base.OnClosed(e);
    }

    // ==================== 摆位 ====================

    /// <summary>
    /// 摆到上次记住的位置；没记住就摆到主屏工作区右下角。
    ///
    /// <para>⚠ 位置记的是**逻辑像素**（物理像素 ÷ 缩放系数）——
    /// 换一台缩放率不同的显示器时位置还能落在合理的地方（与 Python 版同一口径）。</para>
    /// </summary>
    private void ApplyInitialPosition()
    {
        string geometry = GeometryProvider?.Invoke() ?? string.Empty;
        double scale = RenderScaling > 0 ? RenderScaling : 1.0;

        if (PanelGeometry.TryParse(geometry, out int x, out int y))
        {
            Position = new PixelPoint(
                (int)Math.Round(x * scale, MidpointRounding.AwayFromZero),
                (int)Math.Round(y * scale, MidpointRounding.AwayFromZero));
            return;
        }

        // 默认摆位：主屏工作区右下角（Python 版是屏幕右下角减 30 / 60；
        // 这里用**工作区**，免得面板被任务栏挡住）。
        PixelRect area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        double left = area.Right / scale - PanelGeometry.BaseWidth - 30;
        double top = area.Bottom / scale - PanelGeometry.BaseHeight - 60;
        Position = new PixelPoint(
            (int)Math.Round(left * scale, MidpointRounding.AwayFromZero),
            (int)Math.Round(top * scale, MidpointRounding.AwayFromZero));
    }

    /// <summary>把窗口的真实位置记下来（宿主负责写配置）。</summary>
    private void SaveGeometry()
    {
        Action<string>? saver = GeometrySaver;
        if (saver is null)
        {
            return;
        }

        double scale = RenderScaling > 0 ? RenderScaling : 1.0;
        int x = (int)Math.Round(Position.X / scale, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(Position.Y / scale, MidpointRounding.AwayFromZero);
        saver(PanelGeometry.Format(x, y));
    }

    // ==================== 交互 ====================

    private void WireUserActions()
    {
        WireRadio(BackendLocalItem, () => BackendChanged?.Invoke(this, CoreController.BackendLocal));
        WireRadio(BackendMinimaxItem, () => BackendChanged?.Invoke(this, CoreController.BackendMinimax));

        WireRadio(Model06Item, () => ModelChanged?.Invoke(this, "0.6B"));
        WireRadio(Model17Item, () => ModelChanged?.Invoke(this, "1.7B"));

        WireRadio(ThemeAutoItem, () => ThemeChanged?.Invoke(this, "auto"));
        WireRadio(ThemeLightItem, () => ThemeChanged?.Invoke(this, "light"));
        WireRadio(ThemeDarkItem, () => ThemeChanged?.Invoke(this, "dark"));

        WireRadio(UiModeOrbItem, () => UiModeChanged?.Invoke(this, "orb"));
        WireRadio(UiModeResidentItem, () => UiModeChanged?.Invoke(this, "resident"));
        WireRadio(UiModeOnDemandItem, () => UiModeChanged?.Invoke(this, "on_demand"));

        PreviewSwitch.IsCheckedChanged += (_, _) => RaiseUserAction(() => PreviewChanged?.Invoke(this, PreviewSwitch.IsChecked == true));
        RestoreClipboardSwitch.IsCheckedChanged += (_, _) => RaiseUserAction(() => RestoreClipboardChanged?.Invoke(this, RestoreClipboardSwitch.IsChecked == true));

        CloseButton.Click += (_, _) => RaiseUserAction(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        QuitButton.Click += (_, _) => RaiseUserAction(() => QuitRequested?.Invoke(this, EventArgs.Empty));

        // 标题栏拖动：Avalonia 原生的 BeginMoveDrag。
        // Python 版是 JS 量位移 → move_window 桥接（那是 WebView2 的 workaround，不搬）。
        TitleBar.PointerPressed += OnTitleBarPointerPressed;
    }

    /// <summary>
    /// 接一个分段控件的"被选中"事件。
    ///
    /// <para>用 <c>IsCheckedChanged</c> 而不是 <c>Checked</c>：后者在 Avalonia 11
    /// 已标记过时（"Use IsCheckedChanged instead"）。<c>IsCheckedChanged</c>
    /// 选中与取消都会触发，所以这里自己判一下"是不是被选上了"。</para>
    /// </summary>
    private void WireRadio(RadioButton button, Action action)
    {
        button.IsCheckedChanged += (_, _) =>
        {
            if (button.IsChecked == true)
            {
                RaiseUserAction(action);
            }
        };
    }

    /// <summary>套用快照期间不把控件变化当成用户动作。</summary>
    private void RaiseUserAction(Action action)
    {
        if (_applying)
        {
            return;
        }

        action();
    }

    /// <summary>
    /// 标题栏按下 → 开始拖动窗口。
    ///
    /// <para>点在按钮上时不触发拖动（Python 版：<c>e.target.closest('button')</c>）。</para>
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Button)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    // ==================== 渲染细节 ====================

    /// <summary>状态点（CSS L198-228）：实心点 + 一圈 soft 色光晕。</summary>
    private void ApplyStateDot(string state)
    {
        (string dotKey, string haloKey) = state switch
        {
            AppStates.Starting => ("Panel.Warn.Brush", "Panel.HaloWarn.Brush"),
            AppStates.Recording => ("Panel.Danger.Brush", "Panel.HaloRec.Brush"),
            AppStates.Processing => ("Panel.TransB.Brush", "Panel.HaloProcessing.Brush"),
            "error" => ("Panel.Danger.Brush", "Panel.HaloRec.Brush"),
            _ => ("Panel.AskB.Brush", "Panel.HaloOk.Brush"),
        };

        StateDot.Background = BrushOf(dotKey);
        StateDotHalo.Background = BrushOf(haloKey);
    }

    /// <summary>健康徽章的三态配色（CSS L391-409）。</summary>
    private void ApplyHealthBadge(string status)
    {
        (string fgKey, string bgKey) = status switch
        {
            HealthStatuses.Ready => ("Panel.AskB.Brush", "Panel.Ask.Brush"),
            HealthStatuses.Failed => ("Panel.Danger.Brush", "Panel.DangerSoft.Brush"),
            _ => ("Panel.Text2.Brush", "Panel.Nav.Brush"),
        };

        HealthBadgeText.Foreground = BrushOf(fgKey);
        HealthBadge.Background = BrushOf(bgKey);
    }

    /// <summary>
    /// 提示语（CSS L430-440）：空的时候不占位置；颜色按级别分。
    ///
    /// <para>⚠ Python 版的 <c>.notice</c> 只有一个颜色（--text2）；
    /// 这里按 level 分色是 K5 任务书明确要求的一条（见 docs/k5-panel.md）。</para>
    /// </summary>
    private void ApplyNotice(PanelSnapshot snapshot)
    {
        string text = snapshot.Notice ?? string.Empty;
        string level = snapshot.NoticeLevel ?? "info";

        bool changed = !string.Equals(text, _appliedNotice, StringComparison.Ordinal)
            || !string.Equals(level, _appliedNoticeLevel, StringComparison.Ordinal);

        _appliedNotice = text;
        _appliedNoticeLevel = level;

        if (!changed)
        {
            return;
        }

        Notice.Text = text;
        Notice.IsVisible = text.Length > 0;
        Notice.Foreground = BrushOf(level switch
        {
            "error" => "Panel.Danger.Brush",
            "warning" => "Panel.Warn.Brush",
            _ => "Panel.Text2.Brush",
        });

        _noticeTimer.Stop();
        if (text.Length > 0)
        {
            _noticeTimer.Start();
        }
    }

    /// <summary>提示语到期：自己淡掉，并通知宿主把事实也归零。</summary>
    private void OnNoticeTimeout(object? sender, EventArgs e)
    {
        _noticeTimer.Stop();

        Notice.Text = string.Empty;
        Notice.IsVisible = false;

        NoticeExpired?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重算依赖阴影的元素。
    ///
    /// <para>CSS 的 <c>box-shadow</c> 落在**选中项**与**圆钮**上，
    /// 而 <c>BoxShadows</c> 是结构体（不是 brush），补间不了 ——
    /// 所以颜色变主题时得手动重刷一遍。</para>
    /// </summary>
    private void RefreshShadows()
    {
        BoxShadows shadow = PanelTheme.CurrentShadow;

        foreach (RadioButton item in SegItems)
        {
            Border? root = FindTemplatePart<Border>(item, "PART_Root");
            if (root is null)
            {
                continue;
            }

            root.BoxShadow = item.IsChecked == true ? shadow : default;
        }

        foreach (ToggleSwitch toggle in Switches)
        {
            Border? thumb = FindTemplatePart<Border>(toggle, "PART_Thumb");
            if (thumb is not null)
            {
                thumb.BoxShadow = shadow;
            }
        }

        // 热键键帽：CSS L528 `box-shadow: 0 2px 0 var(--line);`（一条硬边投影）。
        string line = HexOf(BrushOf("Panel.Line.Brush"));
        HotkeyBox.BoxShadow = BoxShadows.Parse($"0 2 0 0 {line}");
    }

    /// <summary>主题切换后重刷阴影（颜色变了，阴影也得跟着换）。</summary>
    private void OnThemeApplied(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshShadows, DispatcherPriority.Loaded);
    }

    /// <summary>所有分段控件的项（用来刷阴影）。</summary>
    private IEnumerable<RadioButton> SegItems =>
    [
        BackendLocalItem,
        BackendMinimaxItem,
        Model06Item,
        Model17Item,
        ThemeAutoItem,
        ThemeLightItem,
        ThemeDarkItem,
        UiModeOrbItem,
        UiModeResidentItem,
        UiModeOnDemandItem,
        UiModeHeadlessItem,
    ];

    /// <summary>所有开关（用来刷阴影）。</summary>
    private IEnumerable<ToggleSwitch> Switches => [PreviewSwitch, RestoreClipboardSwitch];

    /// <summary>在控件的可视树里找模板部件。</summary>
    private static T? FindTemplatePart<T>(Control control, string name)
        where T : Control
    {
        return control.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(x => x.Name == name);
    }

    /// <summary>从应用资源里取一个 brush（取不到就返回 null —— 不硬塞颜色）。</summary>
    private static IBrush? BrushOf(string key) => OverlayTheme.FindBrush(key);

    /// <summary>brush 的颜色转成 <c>#AARRGGBB</c>（Avalonia 的 ARGB 写法）。</summary>
    private static string HexOf(IBrush? brush)
        => brush is SolidColorBrush solid
            ? $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
            : "#00000000";

    /// <summary>
    /// 热键文本：去掉可能存在的"热键 "前缀（Python 版：<c>s.replace(/^热键\s*/, '')</c>），
    /// 空的话显示"未设置"。
    /// </summary>
    private static string NormalizeHotkey(string? hotkey)
    {
        string text = (hotkey ?? string.Empty).Trim();
        if (text.StartsWith("热键", StringComparison.Ordinal))
        {
            text = text["热键".Length..].Trim();
        }

        return text.Length > 0 ? text : "未设置";
    }
}
