using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VmNext.Core.Interop;
using VmNext.Core.Presentation;
using VmNext.Shell;

// 别名：避免与 System.Threading.Monitor / System.IO.Path 撞名。
using IconPath = Avalonia.Controls.Shapes.Path;
using MonitorScreen = VmNext.Core.Interop.Monitor;

namespace VmNext.Ui;

/// <summary>胶囊的阶段。对应 capsule.js 的 <c>PHASES</c>（recording / processing / done / error / warning / cancelled）。</summary>
public enum CapsulePhase
{
    /// <summary>录音中（波形 + 计时 + 结束钮）。</summary>
    Recording,

    /// <summary>识别中（转圈 + "识别中…"）。</summary>
    Processing,

    /// <summary>已完成（对勾 + "已输入"）。</summary>
    Done,

    /// <summary>出错（感叹号 + "出错了"，胶囊与文字条都是错误红）。</summary>
    Error,

    /// <summary>警告（琥珀色：内容拿到了，只是没送出去）。</summary>
    Warning,

    /// <summary>已取消（中性灰文字 + 转圈）。</summary>
    Cancelled,
}

/// <summary>胶囊的形态：收起的球 ↔ 展开的胶囊。</summary>
public enum CapsuleForm
{
    /// <summary>收起的悬浮球（CSS: body.orb-mode.orb-idle）。</summary>
    OrbIdle,

    /// <summary>展开的胶囊（CSS: body.orb-mode.orb-expanded / 非常驻模式）。</summary>
    Expanded,
}

/// <summary>文字条的配色档（CSS: writeText 的 kind 参数）。</summary>
public enum CapsuleTextKind
{
    /// <summary>正常。</summary>
    Normal,

    /// <summary>警告（琥珀）。</summary>
    Warning,

    /// <summary>错误（红）。</summary>
    Error,

    /// <summary>取消（中性灰）。</summary>
    Cancelled,
}

/// <summary>
/// 胶囊窗口：voice-mikon 的 <c>capsule.html</c> / <c>capsule.css</c> 的 Avalonia 翻译。
///
/// <para><b>翻译，不是设计。</b>所有数值都来自 capsule.css / capsule.js / placement.py，
/// 每个值旁边都有来源注释。CSS 注释里的"为什么"也一并带了过来。</para>
///
/// <para>形状过渡必须改**布局尺寸**（MaxWidth），不能用 RenderTransform 缩放：
/// 形变期间窗口每帧都要 resize + 重裁 SetWindowRgn 的区域，
/// 视觉形状与点击穿透区域必须始终一致（CSS 注释里"不能有 transform"的理由在 Avalonia 下
/// 不完全成立 —— 这里不需要前端上报命中区 —— 但结论一样：改布局尺寸最稳）。</para>
///
/// <para>本类只**演**：什么时候发生、停多久、到点回哪，全归 presenter 的 dwell 表。
/// 入口是 <see cref="SetMode"/>（对应 capsule.js 的 <c>applyMode</c>）。</para>
/// </summary>
public partial class CapsuleWindow : OverlayWindow
{
    // ==================== 几何常量（全部来自 CSS / Python）====================

    /// <summary>
    /// 球的直径 = 胶囊高度。来源：capsule.css L303 <c>height: 40px</c> + placement.py <c>ORB_SIZE = 40</c>。
    /// （真相源在 <see cref="Placement"/> —— 摆位与几何是同一套数。）
    /// </summary>
    public const double OrbSize = Placement.OrbSize;

    /// <summary>
    /// 球的下边到窗口下边。来源：placement.py <c>ORB_INSET_Y = 10</c>（= CSS <c>.stack</c> 的 padding-bottom 10px）。
    /// </summary>
    public const double OrbInsetY = Placement.OrbInsetY;

    /// <summary>
    /// 球与工作区边缘的最小留白。来源：placement.py <c>ORB_MARGIN = 16</c>。
    /// </summary>
    public const double OrbMargin = Placement.OrbMargin;

    /// <summary>
    /// 窗口宽度（**固定**）。来源：voice-mikon 的 host/capsule.py <c>WIN_W = 512</c>。
    ///
    /// 为什么必须固定：球固定待在窗口的**底边居中**（placement.py 的模块说明），
    /// 宽度一变球就会横向跑位 —— 真机正是靠"窗口定宽 + 内容居中"保证
    /// 球↔胶囊形变时轨迹严丝合缝、命中区与视觉形状始终对得上。
    /// </summary>
    public const double WindowWidth = 512;

    /// <summary>胶囊展开态的最大宽度。来源：capsule.css L299 <c>max-width: 480px</c>。</summary>
    public const double PillExpandedMaxWidth = 480;

    /// <summary>球形态的最大宽度。来源：capsule.css L589 <c>body.orb-mode.orb-idle .recording-pill { max-width: 40px }</c>。</summary>
    public const double PillCollapsedMaxWidth = 40;

    /// <summary>
    /// 胶囊圆角。CSS 展开态写的是 <c>border-radius: 999px</c>，但胶囊只有 40px 高，
    /// 浏览器会把超过一半的圆角按比例缩成 20px（= 40/2），和球形态的 20px 视觉相同。
    /// 这里直接用等效值 20，避免依赖渲染器的 clamp 行为。
    /// </summary>
    private const double PillCornerRadius = 20;

    /// <summary>形变时长（ms）。来源：capsule.css L339 <c>max-width .8s</c>。</summary>
    private const int MorphDurationMs = 800;

    /// <summary>元素淡入淡出时长（ms）。来源：capsule.css L507 <c>transition: opacity .2s ease</c>。</summary>
    private const int FadeDurationMs = 200;

    /// <summary>展开态的内容淡入时长（ms）。来源：capsule.css L630 <c>transition-duration: .1s</c>。</summary>
    private const int FadeInDurationMs = 100;

    /// <summary>收起态的内容淡出时长（ms）。来源：capsule.css L620 <c>transition-duration: .12s</c>。</summary>
    private const int FadeOutDurationMs = 120;

    /// <summary>球图标淡入淡出时长（ms）。来源：capsule.css L561 <c>transition: opacity .07s ease</c>。</summary>
    private const int OrbFadeDurationMs = 70;

    /// <summary>球图标收起时的延迟（ms）。来源：capsule.css L569 <c>transition-delay: .76s</c>。</summary>
    private const int OrbFadeDelayMs = 760;

    /// <summary>收起态颜色的延迟（ms）。来源：capsule.css L647 <c>transition-delay: 0s,0s,0s,.58s,.58s,.58s</c>。</summary>
    private const int CollapsedColorDelayMs = 580;

    /// <summary>展开后多久启动波形条（ms）。来源：capsule.js <c>WAVEFORM_START_DELAY_MS = 620</c>。</summary>
    private const int WaveformStartDelayMs = 620;

    /// <summary>呼吸动画周期（ms）。来源：capsule.css L575 <c>animation: orb-breathe 2.8s ease-in-out infinite</c>。</summary>
    private const int BreathePeriodMs = 2800;

    /// <summary>球上 loader 的旋转周期（ms）。来源：capsule.css L695 <c>animation: bar-spin 1.1s linear infinite</c>。</summary>
    private const int SpinPeriodMs = 1100;

    /// <summary>文字条空态占位文案。来源：capsule.js <c>PLACEHOLDER = '正在聆听…'</c>。</summary>
    private const string Placeholder = "正在聆听…";

    /// <summary>波形区宽度（px）。来源：capsule.css L381 <c>.waveform { width: 75px }</c>。</summary>
    private const double WaveformWidth = 75;

    /// <summary>波形区高度（px）。来源：capsule.css L382 <c>.waveform { height: 24px }</c>。</summary>
    private const double WaveformHeight = 24;

    /// <summary>球上提示气泡的存活时长（ms）。来源：capsule.css 的 <c>.orb-hint</c> 段落（JS 负责定时收尾）。</summary>
    private const int OrbHintDurationMs = 2200;

    // ==================== 运行状态 ====================

    private readonly WaveformData _waveform = new();
    private readonly List<Rectangle> _bars = new();
    private readonly List<Transitions> _contentOpacityTransitions = new();
    private readonly int[] _ages = new int[WaveformData.PoolSize];
    private readonly double[] _heights = new double[WaveformData.PoolSize];

    private DispatcherTimer? _levelTimer;
    private DispatcherTimer? _barTimer;
    private DispatcherTimer? _colorTimer;
    private DispatcherTimer? _waveformStartTimer;
    private DispatcherTimer? _menuFadeTimer;
    private DispatcherTimer? _hintTimer;

    private Transitions? _orbOpacityTransition;
    private double _spinAngle;
    private CapsuleForm _form = CapsuleForm.OrbIdle;
    private CapsulePhase _phase = CapsulePhase.Recording;
    private CapsuleTextKind _textKind = CapsuleTextKind.Normal;
    private bool _textHasContent;
    private bool _textHasReport;
    private int _colorDelayMs;
    private bool _orbLoading;

    /// <summary>预览文字条是否开着（config.show_preview / CSS 的 <c>.no-preview</c>）。</summary>
    private bool _previewEnabled = true;

    /// <summary>界面形态（orb / resident / on_demand）。非 orb 时没有"球形态"。</summary>
    private string _uiMode = "orb";

    /// <summary>球形态是否可见（ui_mode != orb 时收球）。</summary>
    private bool _orbVisible = true;

    /// <summary>带原文的错误态：✕ 的语义是"关提示"而不是"取消录音"。</summary>
    private bool _errorDismissable;

    /// <summary>可复制的原文（带原文的错误态）。</summary>
    private string _copyPayload = string.Empty;

    /// <summary>当前显示状态（mode）—— 窗口显隐要看它。</summary>
    private string _currentMode = DisplayModes.Orb;

    /// <summary>本轮已录音秒数（由电平帧带过来）。</summary>
    private double _elapsed;

    /// <summary>当前形态。</summary>
    public CapsuleForm Form => _form;

    /// <summary>当前阶段。</summary>
    public CapsulePhase Phase => _phase;

    /// <summary>当前文字条配色档。</summary>
    public CapsuleTextKind TextKind => _textKind;

    /// <summary>当前形态下文字条是否可见（球的形态下文字条整块淡出）。</summary>
    public bool TextBarVisible => TextBar.IsVisible;

    /// <summary>复制按钮是否亮着（CSS 的 <c>.can-copy</c>）。</summary>
    public bool CanCopy => _copyPayload.Length > 0;

    /// <summary>用户点了球（想开始录音）。</summary>
    public event EventHandler? StartRecordingRequested;

    /// <summary>用户点了 ✓（想结束录音）。</summary>
    public event EventHandler? StopRecordingRequested;

    /// <summary>用户点了 ✕（想取消这一轮）。</summary>
    public event EventHandler? CancelRecordingRequested;

    /// <summary>用户点了 ✕ 且当前是"带原文的错误常驻态"（✕ 的语义是关掉提示）。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>用户点了复制钮（想捞回原文）。</summary>
    public event EventHandler? CopyRequested;

    /// <summary>用户点了菜单里的退出。</summary>
    public event EventHandler? QuitRequested;

    /// <summary>用户点了菜单里的「设置」（面板是 K5，宿主负责回话）。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>用户点了菜单里的「二维码扫描」（拉起附属工具的独立进程，宿主负责回话）。</summary>
    public event EventHandler? QrScanRequested;

    /// <summary>
    /// 读上次记住的球位置（<c>orb.geometry</c> + <c>orb.monitor</c>）。
    /// 宿主注入；返回 null = 没拖过 → 走默认位置。
    /// </summary>
    public Func<OrbPlacementHint?>? PositionHintProvider { get; set; }

    /// <summary>
    /// 拖完记住球的位置。宿主注入（负责写配置）。
    /// </summary>
    public Action<OrbPlacementHint>? PositionSaver { get; set; }

    /// <summary>
    /// 当前是否处在拖拽中（按下 → 移动 → 松手）。
    /// 暴露出来给宿主判断"这一下到底是点还是拖"。
    /// </summary>
    public bool IsDragging => _dragging;

    // ==================== 拖拽状态 ====================

    /// <summary>是否正在拖（按下之后、还没松手）。</summary>
    private bool _dragging;

    /// <summary>这一下拖动有没有真的挪过位置（区分"点球"与"拖球"）。</summary>
    private bool _dragMoved;

    /// <summary>按下时的窗口位置（物理像素）。</summary>
    private PixelPoint _dragWindowStart;

    /// <summary>按下时的鼠标屏幕坐标（物理像素）。</summary>
    private PixelPoint _dragMouseStart;

    /// <summary>按下时的渲染缩放（折算"逻辑位移"用）。</summary>
    private double _dragScaleAtPress;

    // ==================== 过渡时长（动效降级）====================

    /// <summary>
    /// 所有过渡的"默认时长"（动效降级时置 0，恢复时还原）。
    /// </summary>
    private readonly List<(Avalonia.Animation.ITransition Transition, TimeSpan Duration)> _transitionDurations = new();

    /// <summary>
    /// 动效降级（CSS 的 <c>@media (prefers-reduced-motion: reduce)</c>）。
    /// true = 所有过渡时长归零。
    /// </summary>
    private bool _reducedMotion;

    /// <summary>创建窗口。</summary>
    public CapsuleWindow()
    {
        InitializeComponent();

        // 窗口尺寸：**宽度定值（真机 WIN_W = 512）+ 高度跟着内容**。
        //
        // ⚠ 宽度只能设在 Window 上（不能靠 SizeToContent 把宽度也交给内容）：
        // 实测 175% 缩放下，宽度取自内容时 Win32 后端会按**逻辑**像素建窗，
        // 而内容按 1.75 倍渲染 → 右边被裁掉一截（球跑到窗口角落）。
        // 走 Window.Width 这条路，后端会按缩放换算成物理像素（实测正确）。
        //
        // 宽度固定是"球固定在窗口底边居中"的前提（见 WindowWidth 的注释）。
        Width = WindowWidth;
        SizeToContent = SizeToContent.Height;
        Root.Width = WindowWidth;

        ConfigureTransitions();
        CollectTransitionDurations();
        BuildWaveform();
        WireInteractions();

        // 动效降级：CSS 的 `@media (prefers-reduced-motion: reduce)`（capsule.css L674-684）
        // —— 系统关了"在显示中显示动画"时，所有过渡时长归零。
        ApplyReducedMotion(!SystemAnimation.IsAnimationEnabled());

        // 主题：默认"跟随系统"。**这里定一次就够** —— 之后由 SetTheme（配置里的档位）
        // 或面板的切换来改；OnOpened 不再重置，否则配置里的 light/dark 会被抹掉。
        CapsuleTheme.Apply(CapsuleThemeMode.Auto);

        CapsuleTheme.Applied += OnThemeApplied;
        CapsuleTheme.AttachPlatformWatcher();
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 主题：跟随系统档要跟着系统走；切换时颜色要平滑过渡（就地改 brush.Color）。
        // （档位在构造函数里定过一次，这里不再重置 —— 配置里的 light/dark 要算数。）

        // 球上 loader 的旋转变换（CSS: animation: bar-spin 1.1s linear infinite）。
        OrbLoaderIcon.RenderTransform = new RotateTransform();
        OrbLoaderIcon.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        ApplyForm(CapsuleForm.OrbIdle);
        ApplyPhase(CapsulePhase.Recording);
        WriteText(string.Empty, CapsuleTextKind.Normal);

        // 摆位：等布局落定（SizeToContent 的高度要算完）再摆。
        // 摆哪儿由"上次记住的位置"决定；没记住就用默认位置（工作区右下角）。
        Dispatcher.UIThread.Post(PlaceAtRememberedPosition, DispatcherPriority.Loaded);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _levelTimer?.Stop();
        _barTimer?.Stop();
        _colorTimer?.Stop();
        _waveformStartTimer?.Stop();
        _menuFadeTimer?.Stop();
        _hintTimer?.Stop();

        CapsuleTheme.Applied -= OnThemeApplied;
        CapsuleTheme.DetachPlatformWatcher();

        base.OnClosed(e);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // 尺寸变了 → 命中区要重算（球的矩形变了），位置也要重摆（保持球在原地）。
        Dispatcher.UIThread.Post(RefreshHitRegion, DispatcherPriority.Loaded);
    }

    // ==================== 状态机入口（core-api.md §3 / §4）====================

    /// <summary>
    /// 显示状态变化。**显示状态的唯一入口**（对应 capsule.js 的 <c>applyMode(mode, payload)</c>）。
    ///
    /// <para>mode 与 payload 的字段见 <c>docs/core-api.md</c> §4。这里只"演"：
    /// 收不收、停多久全是 presenter 的 dwell 表管的事。</para>
    /// </summary>
    /// <param name="mode">显示状态（<see cref="DisplayModes"/>）。</param>
    /// <param name="payload">载荷。</param>
    public void SetMode(string mode, DisplayPayload payload)
    {
        _currentMode = mode;

        switch (mode)
        {
            case DisplayModes.Orb:
                // 回到待机：波形停、收回球形态、上一轮的错误残留清干净。
                // ⚠ 只有**有球**的界面形态才收成球（非常驻模式没有 orb-idle 这一档，
                // CSS 里没有 body.orb-mode.*，胶囊一直是展开态）。
                StopWaveform();
                ResetErrorFlags();
                ApplyForm(_orbVisible ? CapsuleForm.OrbIdle : CapsuleForm.Expanded);
                WriteText(string.Empty, CapsuleTextKind.Normal);
                break;

            case DisplayModes.Starting:
                // 模型还没就绪：球上转圈（CSS: body.orb-loading），胶囊形态不动。
                SetOrbLoading(true);
                break;

            case DisplayModes.Recording:
            case DisplayModes.Expanding:
                // 新一轮录音：清场 —— 上一轮的文案、配色、波形、错误残留全清。
                SetOrbLoading(false);
                CloseOrbMenu();
                HideOrbHint();
                WriteText(string.Empty, CapsuleTextKind.Normal);
                ResetWaveform();
                SetElapsed(0);
                ResetErrorFlags();
                ApplyPhase(CapsulePhase.Recording);
                ApplyForm(CapsuleForm.Expanded);
                break;

            case DisplayModes.Processing:
                if (payload.Warn)
                {
                    // 等修饰键的**瞬态警告覆盖**（状态位仍是 processing）：
                    // 正文(body) = 识别出的文字，报告位 = 规则说明，中段短词 = "等你松键"。
                    if (_previewEnabled)
                    {
                        WriteText(payload.Body, CapsuleTextKind.Warning, payload.Message);
                    }

                    ApplyPhase(CapsulePhase.Warning, payload.Text.Length > 0 ? payload.Text : "等你松键");
                }
                else
                {
                    if (_previewEnabled)
                    {
                        WriteText(payload.Message, CapsuleTextKind.Normal);
                    }

                    ApplyPhase(CapsulePhase.Processing, payload.Message.Length > 0 ? payload.Message : null);
                }

                ApplyForm(CapsuleForm.Expanded);
                break;

            case DisplayModes.Result:
                if (_previewEnabled)
                {
                    WriteText(payload.Text, CapsuleTextKind.Normal);
                }

                ApplyPhase(CapsulePhase.Done);
                ApplyForm(CapsuleForm.Expanded);
                break;

            case DisplayModes.Error:
                // 错误**可能带着识别原文**（注入失败时）：带原文是**警告**态
                // （识别是成功的，内容也拿到了，只是没送进目标窗口），没有原文
                // 才是真的失败（错误色）。
                _copyPayload = payload.CanCopy ? payload.Text : string.Empty;
                _errorDismissable = payload.CanCopy;

                if (payload.CanCopy)
                {
                    if (_previewEnabled)
                    {
                        WriteText(payload.Text, CapsuleTextKind.Warning, payload.Message);
                        ApplyPhase(CapsulePhase.Warning);
                    }
                    else
                    {
                        ApplyPhase(CapsulePhase.Warning, payload.Message);
                    }
                }
                else
                {
                    if (_previewEnabled)
                    {
                        WriteText(payload.Message, CapsuleTextKind.Error);
                        ApplyPhase(CapsulePhase.Error);
                    }
                    else
                    {
                        ApplyPhase(CapsulePhase.Error, payload.Message);
                    }
                }

                ApplyForm(CapsuleForm.Expanded);
                break;

            case DisplayModes.Cancelled:
                if (_previewEnabled)
                {
                    WriteText(payload.Message.Length > 0 ? payload.Message : "已取消", CapsuleTextKind.Cancelled);
                }

                ApplyPhase(CapsulePhase.Cancelled);
                ApplyForm(CapsuleForm.Expanded);
                break;

            case DisplayModes.Rejected:
                // 这轮被拒：球上方冒一句提示（气泡的淡出是纯视觉）。
                ShowOrbHint(payload.Message);
                break;

            default:
                // 不认识的 mode：不猜、不改状态（core-api.md 说前端不按 mode 猜字段）。
                break;
        }

        ApplyWindowVisibility();
    }

    /// <summary>预览文字变化（整段替换）。对应 capsule.js 的 <c>writeText</c> 的"正常档"。</summary>
    public void SetPreviewText(string text)
    {
        if (!_previewEnabled)
        {
            return;
        }

        WriteText(text, CapsuleTextKind.Normal);
    }

    /// <summary>
    /// 电平帧（30fps）。对应 capsule.js 的 <c>setLevels</c>：只取最新的一帧喂平滑器。
    /// </summary>
    /// <param name="levels">已映射电平（0..1）。</param>
    /// <param name="elapsed">本轮已录音秒数。</param>
    public void PushLevels(IReadOnlyList<double> levels, double elapsed)
    {
        if (levels.Count > 0)
        {
            _waveform.SetLevel(levels[levels.Count - 1]);
        }

        SetElapsed(elapsed);
    }

    /// <summary>主题档位（auto / light / dark）。对应 capsule.js 的 <c>applyTheme</c>。</summary>
    public void SetTheme(string theme) => CapsuleTheme.Apply(CapsuleTheme.ParseMode(theme));

    /// <summary>预览开关。对应 capsule.js 的 <c>.no-preview</c>（文字条整块消失）。</summary>
    public void SetPreviewEnabled(bool enabled)
    {
        _previewEnabled = enabled;
        if (!enabled)
        {
            WriteText(string.Empty, CapsuleTextKind.Normal);
        }

        ApplyWindowVisibility();
    }

    /// <summary>
    /// 界面形态（orb / resident / on_demand）。
    ///
    /// <para>orb：球常驻，待机时是球形态。非 orb：无球 —— 待机时整块藏起来，
    /// 录音/识别时才露出胶囊（对齐 Python 的 <c>_hide</c>：非球形态时窗口可以藏）。</para>
    /// </summary>
    public void SetUiMode(string uiMode)
    {
        string normalized = (uiMode ?? "orb").Trim().ToLowerInvariant();
        _uiMode = normalized;
        _orbVisible = normalized == "orb";

        if (!_orbVisible)
        {
            // 无球形态：胶囊永远展开（CSS 里没有 orb-mode class）。
            ApplyForm(CapsuleForm.Expanded);
        }

        ApplyWindowVisibility();
    }

    /// <summary>球形态显隐（presenter 的 <c>OrbVisibilityChanged</c>）。</summary>
    public void SetOrbVisible(bool visible)
    {
        _orbVisible = visible;
        ApplyWindowVisibility();
    }

    // ==================== 过渡 ====================

    /// <summary>
    /// 配置过渡。**全部在代码里设** —— XAML 里写 <c>Property="MaxWidth"</c> 会解析成 null
    /// （archive 意外发现 #2）。
    ///
    /// capsule.css 的 <c>.recording-pill</c> 一共 6 条 transition，按顺序是：
    /// <code>
    /// opacity .2s ease,
    /// max-width .8s cubic-bezier(.25,.85,.3,1),
    /// border-radius .8s cubic-bezier(.25,.85,.3,1),
    /// background-color .18s ease-out,
    /// border-color .18s ease-out,
    /// color .18s ease-out
    /// </code>
    /// 前三条（形状 + 整体透明度）用 Avalonia 的 Transition；
    /// 后三条（颜色）走 <see cref="OverlayTheme.TweenBrush"/> 就地改 brush.Color ——
    /// 换 brush 实例会瞬间跳色，就地改才有补间。
    /// </summary>
    private void ConfigureTransitions()
    {
        Pill.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Layoutable.MaxWidthProperty,
                Duration = TimeSpan.FromMilliseconds(MorphDurationMs),
                Easing = CapsuleTheme.MorphEase,
            },
            new CornerRadiusTransition
            {
                Property = Border.CornerRadiusProperty,
                Duration = TimeSpan.FromMilliseconds(MorphDurationMs),
                Easing = CapsuleTheme.MorphEase,
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(FadeDurationMs),
                Easing = CapsuleTheme.CssEase,
            },
        };

        // .textbar { transition: opacity .2s ease } —— 球形态下文字条只淡入淡出，不位移。
        TextBar.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(FadeDurationMs),
                Easing = CapsuleTheme.CssEase,
            },
        };

        // .bar-orb { transition: opacity .07s ease } —— 收起时要等 .76s 才出来。
        _orbOpacityTransition = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(OrbFadeDurationMs),
                Easing = CapsuleTheme.CssEase,
            },
        };
        OrbHost.Transitions = _orbOpacityTransition;

        // .recording-pill > * { transition: opacity .2s ease } —— 胶囊里各个子元素。
        // 每个控件要**自己的** Transitions 实例：Avalonia 的 Transitions 集合只能挂在一个
        // Animatable 上（共享会抛异常）。
        foreach (var element in new Control[]
                 {
                     CancelButton, BarContent, StopButton, StateIcon, CopyButton,
                 })
        {
            var transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(FadeDurationMs),
                    Easing = CapsuleTheme.CssEase,
                },
            };
            element.Transitions = transitions;
            _contentOpacityTransitions.Add(transitions);
        }
    }

    // ==================== 形态与阶段 ====================

    /// <summary>切到收起的悬浮球形态。</summary>
    public void ShowOrbIdle() => ApplyForm(CapsuleForm.OrbIdle);

    /// <summary>切到某个阶段（自动展开成胶囊）。</summary>
    public void ShowPhase(CapsulePhase phase)
    {
        if (_form != CapsuleForm.Expanded)
        {
            ApplyForm(CapsuleForm.Expanded);
        }

        ApplyPhase(phase);
    }

    /// <summary>球上显示 loader（模型加载中，CSS: body.orb-loading）。</summary>
    public void SetOrbLoading(bool loading)
    {
        _orbLoading = loading;
        OrbMicIcon.IsVisible = !loading;
        OrbLoaderIcon.IsVisible = loading;
    }

    /// <summary>
    /// 切形态。对应 CSS 的 <c>body.orb-mode.orb-idle</c> / <c>body.orb-mode.orb-expanded</c> 两组规则。
    /// </summary>
    private void ApplyForm(CapsuleForm form)
    {
        _form = form;
        bool orb = form == CapsuleForm.OrbIdle;

        // 形状：max-width .8s cubic-bezier(.25,.85,.3,1)。
        // 实际宽度 = min(内容宽, MaxWidth) —— CSS 的 fit-content 就是这个意思，
        // 所以视觉上是"一点点长开"，不是整条跳到 480。
        Pill.MaxWidth = orb ? PillCollapsedMaxWidth : PillExpandedMaxWidth;
        Pill.CornerRadius = new CornerRadius(PillCornerRadius);

        // 形状过渡**不延迟**；只有颜色在收起态延迟 .58s
        // （CSS: body.orb-mode.orb-idle .recording-pill { transition-delay: 0s,0s,0s,.58s,.58s,.58s }）。
        _colorDelayMs = orb ? CollapsedColorDelayMs : 0;

        // 内容淡入淡出：展开 .1s / 收起 .12s（CSS L627-631 / L615-621）。
        ApplyContentFadeDuration();

        // 球图标：收起时延迟 .76s 才淡入（CSS L564-570），展开时立刻淡出。
        // 动效降级时延迟也归零（CSS 的 `transition: none` 连 delay 一起作废）。
        if (_orbOpacityTransition is not null)
        {
            TimeSpan delay = _reducedMotion
                ? TimeSpan.Zero
                : (orb ? TimeSpan.FromMilliseconds(OrbFadeDelayMs) : TimeSpan.Zero);

            foreach (var transition in _orbOpacityTransition)
            {
                if (transition is DoubleTransition doubleTransition)
                {
                    doubleTransition.Delay = delay;
                }
            }
        }

        OrbHost.Opacity = orb ? 1 : 0;
        foreach (var element in new Control[] { CancelButton, BarContent, StopButton, StateIcon, CopyButton })
        {
            element.Opacity = orb ? 0 : 1;
        }

        // 文字条：球形态整块淡出（只淡入淡出，不位移）。
        TextBar.Opacity = orb ? 0 : 1;

        // 球形态下文字条**不占布局**：真机的窗口是固定 512×260，文字条占位只是为了
        // "窗口留够空间、内容贴底"；Avalonia 的窗口高度跟着内容走，
        // 文字条占着 96px 的最小宽度会把窗口撑高。
        // 视觉上没有差别（文字条在球形态本来就是 opacity:0）。
        TextBar.IsVisible = !orb;

        // 形状不同 → 窗口的点击穿透区域形状也要跟着换。
        RegionShape = orb ? OverlayShape.Circle : OverlayShape.Capsule;

        // 波形条：收起时立刻停；展开时等形变落定再启动
        // （capsule.js: "条子在动 + 胶囊在变宽，两件事叠在一起容易糊成一片残影"）。
        if (orb)
        {
            StopWaveform();
        }
        else
        {
            _waveformStartTimer?.Stop();
            _waveformStartTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(WaveformStartDelayMs),
            };
            _waveformStartTimer.Tick += (_, _) =>
            {
                _waveformStartTimer!.Stop();
                if (_phase == CapsulePhase.Recording)
                {
                    StartWaveform();
                }
            };
            _waveformStartTimer.Start();
        }

        RefreshDerivedColors();
        Dispatcher.UIThread.Post(RefreshHitRegion, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 切阶段。对应 capsule.js 的 <c>setPhase(phase, labelText)</c>：
    /// 阶段类名挂在胶囊上，CSS 靠它决定显示波形还是状态文字、右侧是结束钮还是状态图标。
    /// </summary>
    private void ApplyPhase(CapsulePhase phase, string? labelText = null)
    {
        _phase = phase;
        bool recording = phase == CapsulePhase.Recording;

        // CSS: .recording-pill.recording .waveform { display:block } 等四条。
        Waveform.IsVisible = recording;
        BarTimer.IsVisible = recording;
        StopButton.IsVisible = recording;
        BarLabel.IsVisible = !recording;
        StateIcon.IsVisible = !recording;

        // CSS: .bar-state svg { display:none } + 各 phase 匹配一个图标。
        // ⚠ warning 态 CSS **没有**匹配任何图标 → 三个都不显示（一个空的圆）。照搬。
        StateLoaderIcon.IsVisible = phase is CapsulePhase.Processing or CapsulePhase.Cancelled;
        StateCheckIcon.IsVisible = phase == CapsulePhase.Done;
        StateInfoIcon.IsVisible = phase == CapsulePhase.Error;

        // CSS: .recording-pill.error.can-copy .bar-copy / .warning.can-copy .bar-copy { display:grid }
        // —— 复制按钮只在"带原文"的错误/警告态出现。
        CopyButton.IsVisible = _copyPayload.Length > 0
            && phase is CapsulePhase.Error or CapsulePhase.Warning;

        // CSS: .recording-pill.error.can-copy .bar-state { display:none }
        //      .recording-pill.warning.can-copy .bar-state { display:none }
        // —— 有复制按钮时状态图标让位（文案已经说明了出错，两个图标并排反而挤）。
        if (CopyButton.IsVisible)
        {
            StateIcon.IsVisible = false;
        }

        BarLabel.Text = labelText ?? PhaseText(phase);

        // 波形只在录音阶段跑（CSS: "滚动电平条只在**录音阶段**跑"）。
        if (recording && _form == CapsuleForm.Expanded)
        {
            if (_barTimer is null || !_barTimer.IsEnabled)
            {
                StartWaveform();
            }
        }
        else
        {
            StopWaveform();
        }

        RefreshDerivedColors();
    }

    /// <summary>各阶段胶囊里的文案。来源：capsule.js 的 <c>PHASE_TEXT</c>。</summary>
    private static string PhaseText(CapsulePhase phase) => phase switch
    {
        CapsulePhase.Recording => string.Empty,
        CapsulePhase.Processing => "识别中…",
        CapsulePhase.Done => "已输入",
        CapsulePhase.Error => "出错了",
        CapsulePhase.Warning => "没送出去",
        CapsulePhase.Cancelled => "已取消",
        _ => string.Empty,
    };

    // ==================== 文字条 ====================

    /// <summary>
    /// 写文字条内容。对应 capsule.js 的 <c>writeText(text, kind, report)</c>。
    /// </summary>
    /// <param name="value">正文（识别文字）。</param>
    /// <param name="kind">配色档。</param>
    /// <param name="report">失败报告（可选，显示在正文**上方**，中间有分隔线）。</param>
    public void WriteText(string value, CapsuleTextKind kind, string? report = null)
    {
        _textKind = kind;
        _textHasContent = !string.IsNullOrWhiteSpace(value);
        _textHasReport = !string.IsNullOrWhiteSpace(report);

        // CSS: .textbar.has-report .textbar-report { padding-bottom:6px; margin-bottom:8px;
        //      border-bottom:1px solid var(--warn-stroke); position:sticky; top:0; }
        TextReport.Text = _textHasReport ? report! : string.Empty;
        TextReportFrame.IsVisible = _textHasReport;

        // CSS: writeText 里 `el.textbarText.textContent = value.trim() ? value : PLACEHOLDER`
        TextBody.Text = _textHasContent ? value : Placeholder;

        RefreshDerivedColors();
    }

    // ==================== 派生色 ====================

    /// <summary>
    /// 重算并补间所有派生色（胶囊底/描边/前景、文字条底/描边/前景、状态文字、状态图标）。
    ///
    /// 派生色不是 CSS 令牌：它们的值由「形态 + 阶段 + 主题」三者共同决定
    /// （例如胶囊底色在球形态是录音红、在错误态是 err-bg、在展开态是 deep）。
    /// </summary>
    private void RefreshDerivedColors()
    {
        if (_colorDelayMs > 0)
        {
            // 收起态的颜色要等 .58s（胶囊快收完了再把底色褪回红）。
            _colorTimer?.Stop();
            _colorTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(_colorDelayMs),
            };
            _colorTimer.Tick += (_, _) =>
            {
                _colorTimer!.Stop();
                ApplyDerivedColors();
            };
            _colorTimer.Start();
            return;
        }

        ApplyDerivedColors();
    }

    private void ApplyDerivedColors()
    {
        var palette = CapsuleTheme.CurrentPalette;
        bool orb = _form == CapsuleForm.OrbIdle;

        // ---- 胶囊底色 / 描边 / 状态文字 / 状态图标 ----
        // 阶段优先；球形态再覆盖（CSS: body.orb-mode.orb-idle .recording-pill 的那组规则）。
        Color pillBackground;
        Color pillBorder;
        Color labelForeground;
        Color stateIconForeground;

        switch (_phase)
        {
            case CapsulePhase.Error:
                // CSS: .recording-pill.error { background:var(--err-bg); border-color:var(--err-stroke); }
                //      .recording-pill.error .bar-label { color:var(--err-fg); }
                //      .recording-pill.error .bar-state { background:var(--state-bg); color:var(--err-icon); }
                pillBackground = palette["--err-bg"];
                pillBorder = palette["--err-stroke"];
                labelForeground = palette["--err-fg"];
                stateIconForeground = palette["--err-icon"];
                break;

            case CapsulePhase.Warning:
                // CSS: .recording-pill.warning { background:var(--warn-bg); border-color:var(--warn-stroke); }
                //      .recording-pill.warning .bar-label { color:var(--warn-fg); }
                //      .recording-pill.warning .bar-state { background:var(--state-bg); color:var(--warn-icon); }
                pillBackground = palette["--warn-bg"];
                pillBorder = palette["--warn-stroke"];
                labelForeground = palette["--warn-fg"];
                stateIconForeground = palette["--warn-icon"];
                break;

            default:
                pillBackground = palette["--deep"];
                pillBorder = palette["--stroke"];
                labelForeground = palette["--fg"];
                stateIconForeground = palette["--wave"];
                break;
        }

        Color pillForeground = palette["--fg"];
        Color stateIconBackground = palette["--state-bg"];

        if (orb)
        {
            // CSS: body.orb-mode.orb-idle .recording-pill {
            //   max-width:40px; border-radius:20px; background-color:var(--wave);
            //   border-color:rgba(255,255,255,.28); color:var(--wave-fg); }
            // 红底上再压一圈半透明白边：把球从背景里"托"出来一点（浅色主题下尤其需要）。
            pillBackground = palette["--wave"];
            pillBorder = Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF); // rgba(255,255,255,0.28)
            pillForeground = palette["--wave-fg"];
            labelForeground = palette["--wave-fg"];
            stateIconBackground = palette["--state-bg"];
            stateIconForeground = palette["--wave"];
        }
        else if (_phase == CapsulePhase.Done)
        {
            // CSS: .recording-pill.done .bar-state { background:var(--wave); color:var(--wave-fg); }
            stateIconBackground = palette["--wave"];
            stateIconForeground = palette["--wave-fg"];
        }

        // ---- 文字条 ----
        // CSS: .textbar { background:var(--deep); border:1px solid var(--stroke); color:var(--fg); }
        //      .textbar.is-error { background:var(--err-bg); border-color:var(--err-stroke); color:var(--err-fg); }
        //      .textbar.is-warning { background:var(--warn-bg); border-color:var(--warn-stroke); color:var(--warn-fg); }
        //      .textbar.is-cancelled { color:var(--dim3); }
        //      .textbar.is-empty .textbar-text { color:var(--dim); }
        Color textBackground = palette["--deep"];
        Color textBorder = palette["--stroke"];
        Color textForeground = palette["--fg"];

        switch (_textKind)
        {
            case CapsuleTextKind.Error:
                textBackground = palette["--err-bg"];
                textBorder = palette["--err-stroke"];
                textForeground = palette["--err-fg"];
                break;

            case CapsuleTextKind.Warning:
                textBackground = palette["--warn-bg"];
                textBorder = palette["--warn-stroke"];
                textForeground = palette["--warn-fg"];
                break;

            case CapsuleTextKind.Cancelled:
                textForeground = palette["--dim3"];
                break;
        }

        if (!_textHasContent && _textKind == CapsuleTextKind.Normal)
        {
            textForeground = palette["--dim"];
        }

        // 补间：颜色过渡 .18s ease-out（capsule.css 的 background-color / border-color / color）。
        var duration = TimeSpan.FromMilliseconds(CapsuleTheme.ColorTransitionMs);
        void Tween(string key, Color target) =>
            OverlayTheme.TweenBrush(key, target, duration, t => CapsuleTheme.CssEaseOut.Ease(t));

        Tween("Capsule.Pill.Background.Brush", pillBackground);
        Tween("Capsule.Pill.Border.Brush", pillBorder);
        Tween("Capsule.Pill.Foreground.Brush", pillForeground);
        Tween("Capsule.Textbar.Background.Brush", textBackground);
        Tween("Capsule.Textbar.Border.Brush", textBorder);
        Tween("Capsule.Textbar.Foreground.Brush", textForeground);
        Tween("Capsule.Label.Foreground.Brush", labelForeground);
        Tween("Capsule.StateIcon.Background.Brush", stateIconBackground);
        Tween("Capsule.StateIcon.Foreground.Brush", stateIconForeground);
    }

    private void OnThemeApplied(object? sender, EventArgs e) => RefreshDerivedColors();

    // ==================== 波形 ====================

    /// <summary>造 14 根条（13 根可见 + 1 根屏外的回收位）。</summary>
    private void BuildWaveform()
    {
        this.TryFindResource("Capsule.WaveDim.Brush", null, out object? waveDimResource);
        IBrush waveDimBrush = waveDimResource as IBrush ?? new SolidColorBrush(Color.Parse("#47F4F4F5"));

        for (int i = 0; i < WaveformData.PoolSize; i++)
        {
            var bar = new Rectangle
            {
                // CSS: .waveform i { width:3px; height:2px; border-radius:3px; }
                // 3px 宽的条上写 3px 圆角 → 浏览器 clamp 成 1.5px（正圆）。
                Width = WaveformData.BarWidth,
                Height = WaveformData.SilentHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = waveDimBrush,
                IsVisible = false,
            };

            // 高度过渡：CSS: .waveform i { transition: transform 140ms linear, height 75ms ease-out }
            bar.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Layoutable.HeightProperty,
                    Duration = TimeSpan.FromMilliseconds(75),
                    Easing = CapsuleTheme.CssEaseOut,
                },
            };

            // 位置：CSS 里条固定在右边缘，用 transform: translateX(-age × 6px) 向左滚。
            var translate = new TranslateTransform();
            translate.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = TimeSpan.FromMilliseconds(140),
                    Easing = new LinearEasing(),
                },
            };

            bar.RenderTransform = translate;
            bar.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

            // CSS: .waveform i { position:absolute; right:0; bottom:0; }
            // 条固定在波形区的**右边缘**，水平位置完全由 translateX(-age × 6px) 决定。
            Canvas.SetLeft(bar, WaveformWidth - WaveformData.BarWidth);
            Canvas.SetBottom(bar, 0);

            WaveCanvas.Children.Add(bar);
            _bars.Add(bar);
        }

        _waveform.Reset();
    }

    /// <summary>启动波形（录音阶段）。</summary>
    private void StartWaveform()
    {
        _waveform.Reset();

        foreach (Rectangle bar in _bars)
        {
            bar.IsVisible = true;
            bar.Height = WaveformData.SilentHeight;
            if (bar.RenderTransform is TranslateTransform translate)
            {
                translate.X = 0;
            }
        }

        _levelTimer ??= new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / WaveformData.LevelFps),
        };
        _levelTimer.Tick -= OnLevelTick;
        _levelTimer.Tick += OnLevelTick;
        _levelTimer.Start();

        _barTimer ??= new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(WaveformData.BarTickMs),
        };
        _barTimer.Tick -= OnBarTick;
        _barTimer.Tick += OnBarTick;
        _barTimer.Start();

        OnLevelTick(this, EventArgs.Empty);
    }

    /// <summary>停波形（收起 / 非录音阶段）。</summary>
    private void StopWaveform()
    {
        _barTimer?.Stop();
        _levelTimer?.Stop();
    }

    /// <summary>清掉上一轮错误态留下的东西（原文、复制按钮、✕ 的语义）。</summary>
    private void ResetErrorFlags()
    {
        _copyPayload = string.Empty;
        _errorDismissable = false;
    }

    /// <summary>波形复位（capsule.js 的 resetLevelBars）。</summary>
    private void ResetWaveform()
    {
        _waveform.Reset();
        foreach (Rectangle bar in _bars)
        {
            bar.IsVisible = false;
            bar.Height = WaveformData.SilentHeight;
        }
    }

    /// <summary>每 1/30 秒更新呼吸动画与球上的 loader。</summary>
    private void OnLevelTick(object? sender, EventArgs e)
    {
        double t = _elapsed;

        // 呼吸：CSS 的 orb-breathe（2.8s ease-in-out infinite，0.72 ↔ 1）。
        if (_form == CapsuleForm.OrbIdle && !_orbLoading)
        {
            double phase = (t * 1000.0) % BreathePeriodMs / BreathePeriodMs;
            double breath = 0.5 + (0.5 * Math.Sin(2 * Math.PI * phase));
            OrbMicIcon.Opacity = 0.72 + (0.28 * breath);
        }

        // 球上的 loader：CSS 的 bar-spin（1.1s linear infinite）。
        if (_orbLoading)
        {
            _spinAngle = (_spinAngle + (360.0 * (1000.0 / WaveformData.LevelFps) / SpinPeriodMs)) % 360.0;
            if (OrbLoaderIcon.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = _spinAngle;
            }
        }
    }

    /// <summary>每 140ms 推进一根条（capsule.js 的 BAR_TICK_MS）。</summary>
    private void OnBarTick(object? sender, EventArgs e)
    {
        _waveform.Advance(_ages, _heights);

        this.TryFindResource("Capsule.Wave.Brush", null, out object? waveResource);
        this.TryFindResource("Capsule.WaveDim.Brush", null, out object? waveDimResource);
        IBrush waveBrush = waveResource as IBrush ?? Brushes.Red;
        IBrush waveDimBrush = waveDimResource as IBrush ?? Brushes.Gray;

        Rectangle? recycled = null;
        double recycledTargetHeight = 0;

        for (int i = 0; i < _bars.Count; i++)
        {
            Rectangle bar = _bars[i];
            double targetX = -_ages[i] * WaveformData.BarStep;

            if (i == 0)
            {
                // 回收的那根要**瞬移**到最右（CSS: recycled.style.transition='none'），
                // 否则它会从屏幕左边一路飞过来。
                recycled = bar;
                recycledTargetHeight = _heights[0];
                if (bar.RenderTransform is TranslateTransform translate)
                {
                    translate.Transitions = null;
                    translate.X = targetX;
                }

                bar.Height = WaveformData.SilentHeight;
            }
            else if (bar.RenderTransform is TranslateTransform translate)
            {
                translate.X = targetX;
            }

            // 静音条画灰色（CSS: .waveform i.is-silent { color:var(--wave-dim) }）。
            bool silent = _heights[i] <= WaveformData.SilentMaxPx;
            bar.Fill = silent ? waveDimBrush : waveBrush;
        }

        // 下一帧恢复过渡并给高度（CSS: requestAnimationFrame 后 transition='' + height=target）。
        if (recycled is not null)
        {
            var pending = recycled;
            Dispatcher.UIThread.Post(() =>
            {
                pending.Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = Layoutable.HeightProperty,
                        Duration = TimeSpan.FromMilliseconds(75),
                        Easing = CapsuleTheme.CssEaseOut,
                    },
                };

                if (pending.RenderTransform is TranslateTransform translate)
                {
                    translate.Transitions = new Transitions
                    {
                        new DoubleTransition
                        {
                            Property = TranslateTransform.XProperty,
                            Duration = TimeSpan.FromMilliseconds(140),
                            Easing = new LinearEasing(),
                        },
                    };
                }

                pending.Height = recycledTargetHeight;
            }, DispatcherPriority.Render);
        }
    }

    /// <summary>计时显示。CSS: formatTime → m:ss。</summary>
    private void SetElapsed(double seconds)
    {
        _elapsed = Math.Max(0, seconds);
        int total = (int)Math.Floor(_elapsed);
        int minutes = total / 60;
        int rest = total % 60;
        BarTimer.Text = $"{minutes}:{(rest < 10 ? "0" : "")}{rest}";
    }

    // ==================== 交互 ====================

    private void WireInteractions()
    {
        // 点球 → 开始录音；右键球 → 通用功能菜单；按住球拖 → 挪位置。
        // （CSS: 球形态下按住球 → 挪位置；按着没动 → 点球开始录音。）
        Pill.AddHandler(PointerPressedEvent, OnPillPointerPressed, RoutingStrategies.Tunnel);

        // 拖拽：按下之后鼠标会移出控件，所以移动/松开挂在窗口上，
        // 并在按下时捕获指针（否则拖到控件外就断了）。
        AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel);

        // 点菜单以外的地方（含左键点球）收菜单（capsule.js 的 onDocMouseDownForMenu）。
        AddHandler(PointerPressedEvent, OnWindowPointerPressedForMenu, RoutingStrategies.Tunnel);

        // 菜单里的设置 / 退出项。
        SettingsMenuButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        QrMenuButton.Click += (_, _) => QrScanRequested?.Invoke(this, EventArgs.Empty);
        QuitMenuButton.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        // 菜单按钮的 hover 色（CSS: .orb-menu-btn:hover / .orb-menu-btn.orb-menu-quit:hover）。
        WireMenuButtonHover(SettingsMenuButton, isQuit: false);
        WireMenuButtonHover(QrMenuButton, isQuit: false);
        WireMenuButtonHover(QuitMenuButton, isQuit: true);

        // 结束钮（✓）→ 停止录音。
        StopButton.Click += (_, _) => StopRecordingRequested?.Invoke(this, EventArgs.Empty);

        // 取消钮（✕）→ 取消这一轮；带原文的错误常驻态里是"关掉提示"。
        CancelButton.Click += (_, _) => OnCancelClick();

        // 复制钮 → 把原文写进剪贴板。
        CopyButton.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);

        // 文字条按下 → 临时激活（要键盘焦点才能选文本 / Ctrl+C）。
        // 对应 capsule.py 的 focus_textbar。
        TextBodyScroll.PointerPressed += OnTextBarPointerPressed;
        TextBodyScroll.PointerReleased += (_, _) => RestoreTextBarFocus();
    }

    /// <summary>
    /// 菜单按钮的 hover 色。
    ///
    /// <para>CSS L744-764：
    /// <code>
    /// .orb-menu-btn:hover { background: var(--cancel-btn); }
    /// .orb-menu-btn.orb-menu-quit:hover { background: var(--err-bg); color: var(--err-fg); }
    /// </code>
    /// 退出是重操作：hover 给警示色，平时和设置长得一样（低调）。</para>
    /// </summary>
    private void WireMenuButtonHover(Button button, bool isQuit)
    {
        // 按钮里的图标是 Path（Stroke 写死了 DynamicResource），所以前景色要落到 Path 上。
        IconPath? icon = button.GetVisualDescendants().OfType<IconPath>().FirstOrDefault();

        button.PointerEntered += (_, _) =>
        {
            if (isQuit)
            {
                button.Background = FindBrush("Capsule.ErrBg.Brush");
                if (icon is not null)
                {
                    icon.Stroke = FindBrush("Capsule.ErrFg.Brush");
                }
            }
            else
            {
                button.Background = FindBrush("Capsule.CancelBtn.Brush");
            }
        };

        button.PointerExited += (_, _) =>
        {
            button.Background = FindBrush("Capsule.StateBg.Brush");
            if (isQuit && icon is not null)
            {
                icon.Stroke = FindBrush("Capsule.Fg.Brush");
            }
        };
    }

    /// <summary>按资源键取 brush（取不到就返回 null，调用方自己兜底）。</summary>
    private IBrush? FindBrush(string key)
        => this.TryFindResource(key, null, out object? resource) ? resource as IBrush : null;

    /// <summary>
    /// 点菜单以外的地方 → 收菜单（capsule.js 的 <c>onDocMouseDownForMenu</c>）。
    /// 点菜单**内部**不算。
    /// </summary>
    private void OnWindowPointerPressedForMenu(object? sender, PointerPressedEventArgs e)
    {
        if (!OrbMenu.IsVisible)
        {
            return;
        }

        if (e.Source is Visual source && OrbMenu.IsVisualAncestorOf(source))
        {
            return;
        }

        CloseOrbMenu();
    }

    private void OnPillPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Pill);

        if (point.Properties.IsRightButtonPressed)
        {
            // CSS: 只在**球形态**响应；展开成胶囊后右键没有菜单（那是录音操作区）。
            if (_form != CapsuleForm.OrbIdle)
            {
                return;
            }

            e.Handled = true;
            ToggleOrbMenu();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 只在**球形态**响应点击（展开态的胶囊上是按钮区，不是"点球录音"）。
        if (_form != CapsuleForm.OrbIdle)
        {
            return;
        }

        e.Handled = true;

        // 按下 = 可能是一次拖拽的开始（也可能只是一次点击）。
        // 到底是"点"还是"拖"，等松手时看有没有真的挪过
        // （与 Python 的 DragSession._moved 同款）。
        _dragging = true;
        _dragMoved = false;
        _dragWindowStart = Position;
        _dragMouseStart = this.PointToScreen(e.GetPosition(this));
        _dragScaleAtPress = CurrentRenderScaling;

        // 捕获指针：鼠标拖到控件外面也要继续收到移动/松开事件。
        e.Pointer.Capture(Pill);
    }

    private void OnCancelClick()
    {
        if (_errorDismissable)
        {
            // 带原文的错误常驻态：这个 ✕ 是"关掉提示"，不是"取消这一轮"
            // —— 那一轮早就结束了，没什么可取消的。
            DismissRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        CancelRecordingRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTextBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 文字条按下 → 临时允许激活 + 把前台抢过来（用户要选文字 / Ctrl+C）。
        // 不激活的窗口收不到键盘事件（archive 意外发现 #5）。
        SetAllowActivation(true);
    }

    private void RestoreTextBarFocus()
    {
        // 松手后切回"不激活"：覆盖层默认不抢用户焦点。
        SetAllowActivation(false);
    }

    /// <summary>显示 / 隐藏球的右键菜单。</summary>
    public void ToggleOrbMenu()
    {
        if (OrbMenu.IsVisible)
        {
            CloseOrbMenu();
            return;
        }

        HideOrbHint();
        OrbMenu.IsVisible = true;
        OrbMenu.Opacity = 0;

        // CSS: requestAnimationFrame 后加 is-open（淡入 .12s）。
        Dispatcher.UIThread.Post(() => OrbMenu.Opacity = 1, DispatcherPriority.Render);

        // 布局变了 → 命中区跟着更新（菜单那块要算进来，不然看得见点不到）。
        Dispatcher.UIThread.Post(RefreshHitRegion, DispatcherPriority.Loaded);
    }

    private void CloseOrbMenu()
    {
        if (!OrbMenu.IsVisible)
        {
            return;
        }

        OrbMenu.Opacity = 0;

        // 淡出过渡(.12s)走完再彻底隐藏（menuFadeTimer 140ms）。
        _menuFadeTimer?.Stop();
        _menuFadeTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _menuFadeTimer.Tick += (_, _) =>
        {
            _menuFadeTimer!.Stop();
            OrbMenu.IsVisible = false;

            // 菜单那块要还给底下的应用（capsule.js: 布局变了 → 命中区跟着收）。
            Dispatcher.UIThread.Post(RefreshHitRegion, DispatcherPriority.Loaded);
        };
        _menuFadeTimer.Start();
    }

    /// <summary>显示球上方的操作提示气泡（CSS: showOrbHint）。</summary>
    public void ShowOrbHint(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            HideOrbHint();
            return;
        }

        CloseOrbMenu();

        OrbHintText.Text = text;
        OrbHint.IsVisible = true;
        OrbHint.Opacity = 0;

        Dispatcher.UIThread.Post(() => OrbHint.Opacity = 1, DispatcherPriority.Render);

        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(OrbHintDurationMs),
        };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer!.Stop();
            HideOrbHint();
        };
        _hintTimer.Start();
    }

    /// <summary>隐藏气泡。</summary>
    public void HideOrbHint()
    {
        _hintTimer?.Stop();

        if (!OrbHint.IsVisible)
        {
            return;
        }

        OrbHint.Opacity = 0;

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(240),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            OrbHint.IsVisible = false;

            // 气泡那块要还给底下的应用。
            Dispatcher.UIThread.Post(RefreshHitRegion, DispatcherPriority.Loaded);
        };
        timer.Start();
    }

    // ==================== 显隐 ====================

    /// <summary>
    /// 窗口此刻该不该露着。
    ///
    /// 对齐 Python 的 <c>_window_should_be_visible</c>：球已经露脸（orb 形态），
    /// 或者这一轮的胶囊正在显示。非球形态下待机时整块藏起来。
    /// </summary>
    private void ApplyWindowVisibility()
    {
        // 有球 → 一直露着（球常驻）；无球 → 只有"不是待机"的时候才露脸。
        bool shouldShow = _orbVisible || _currentMode != DisplayModes.Orb;

        if (shouldShow)
        {
            if (!IsVisible)
            {
                Show();
                Dispatcher.UIThread.Post(RefreshHitRegion, DispatcherPriority.Loaded);
                Dispatcher.UIThread.Post(PlaceAtRememberedPosition, DispatcherPriority.Loaded);
            }
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    // ==================== 命中区 ====================

    /// <summary>
    /// 命中区：**只裁"真正可见的那几块"**的并集。    ///
    /// <para>对齐 Python 的 <c>update_regions</c>：前端把可见元素的矩形报上来，
    /// Python 侧 <c>SetWindowRgn</c> 裁成它们的并集 —— 窗口其余部分（大片空白）
    /// 不归窗口管，点击直接穿透到下面的应用。</para>
    ///
    /// <para><b>不搬"上报"这个动作</b>：Avalonia 下窗口就是控件树的根，
    /// 每个元素的 <c>Bounds</c> 直接可读，这里自己算。
    /// "量矩形"这一步抽成了纯函数 <see cref="HitRegionBuilder"/>（可单元测试）。</para>
    ///
    /// <para>⚠ 真机的规矩：浮层（菜单 / 气泡）**单独一块**，不能并进球的包围盒 ——
    /// 并进去中间全是空白，空白在裁形里会露出底色。这里每个可见元素各报一块，
    /// Shell 侧再 <c>CombineRgn</c> 取并集。</para>
    /// </summary>
    protected override IReadOnlyList<HitRegion> BuildHitRegions()
    {
        var specs = new List<HitRegionSpec>(4);

        void Add(Control? control)
        {
            if (control is null || !control.IsVisible)
            {
                return;
            }

            Rect bounds = control.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            // 控件坐标 → 客户区坐标（逻辑像素）
            Point origin = control.TranslatePoint(new Point(0, 0), this) ?? default;

            // 圆角：抄 CSS（文字条 14px / 浮层 12px / 气泡与胶囊 999px）。
            // Border 的 CornerRadius 一眼就是布局用的那个值，直接读。
            double radius = 0;
            if (control is Border border)
            {
                CornerRadius corners = border.CornerRadius;
                radius = Math.Max(
                    Math.Max(corners.TopLeft, corners.TopRight),
                    Math.Max(corners.BottomLeft, corners.BottomRight));
            }

            specs.Add(new HitRegionSpec(
                new Rect(origin.X, origin.Y, bounds.Width, bounds.Height),
                radius));
        }

        Add(Pill);
        Add(TextBar);
        Add(OrbMenu);
        Add(OrbHint);

        return HitRegionBuilder.Build(specs, CurrentRenderScaling);
    }

    /// <summary>
    /// 当前的命中区（客户区坐标，物理像素）。
    ///
    /// <para>给自检 / 测试用：真机上这块区域会被 <c>SetWindowRgn</c> 裁到窗口上，
    /// 区域之外的点击直接穿透到下面的应用。</para>
    /// </summary>
    public IReadOnlyList<HitRegion> ComputeHitRegions() => BuildHitRegions();

    /// <summary>
    /// 重算点击穿透区域。
    ///
    /// 布局落定后（尺寸变了、菜单弹出/收起）才需要重算 —— 不是每帧都算。
    /// </summary>
    private void RefreshHitRegion()
    {
        ApplyRegion();
    }

    // ==================== 摆位与位置记忆 ====================

    /// <summary>
    /// 摆位：上次拖到哪就摆哪；没拖过就摆工作区**右下角**（默认位置）。
    ///
    /// <para>依据 placement.py：
    /// <list type="bullet">
    /// <item><c>ORB_MARGIN = 16</c> —— 球与工作区边缘的最小留白（逻辑像素）</item>
    /// <item><c>ORB_SIZE = 40</c> —— 球的直径</item>
    /// <item>球固定待在窗口**底边居中**，所以窗口左上角 = 目标球位置 − 球在窗口内的偏移</item>
    /// </list></para>
    ///
    /// <para>记忆里存的是**球**的偏移（不是窗口的）：球是用户真正盯着的那一小块，
    /// 记住它在哪，换屏之后它还在同一个相对位置。</para>
    /// </summary>
    private void PlaceAtRememberedPosition()
    {
        OrbPlacementHint? hint = PositionHintProvider?.Invoke();

        // 上次在那块屏上 → 就摆回那块屏；那块屏没了（拔了显示器 / 换机器）→ 退回主屏。
        MonitorScreen? monitor = null;
        if (hint is not null && !string.IsNullOrEmpty(hint.Monitor))
        {
            monitor = Monitors.MonitorByDevice(hint.Monitor);
        }

        monitor ??= Monitors.PrimaryMonitor();
        if (monitor is null)
        {
            return;
        }

        double scale = monitor.Scale;
        (int winW, int winH) = WindowSizePhysical(scale);

        (int X, int Y)? position = null;
        if (hint is not null && !string.IsNullOrEmpty(hint.Geometry))
        {
            position = Placement.OrbPositionFromGeometry(
                hint.Geometry, monitor.WorkArea, scale, winW, winH);
        }

        if (position is null)
        {
            // 还没拖过：给个合理的默认值（工作区右下角）。
            (int orbX, int orbY) = Placement.DefaultOrbPosition(monitor.WorkArea, scale);
            position = Placement.OrbWindowPosition(orbX, orbY, winW, winH, scale, monitor.WorkArea);
        }

        MoveToPhysicalPixels(position.Value.X, position.Value.Y);
    }

    /// <summary>
    /// 窗口的**物理**尺寸（按目标显示器的缩放换算）。
    ///
    /// <para>Avalonia 的 <c>ClientSize</c> 是逻辑像素；Win32 的窗口坐标是物理像素。
    /// 摆位算的是物理坐标，所以要过一遍目标显示器的缩放 ——
    /// 摆到缩放不同的屏上时，窗口会被框架按新 DPI 重新缩放，位置也必须按新缩放算。</para>
    /// </summary>
    private (int Width, int Height) WindowSizePhysical(double scale)
    {
        double logicalWidth = ClientSize.Width > 0 ? ClientSize.Width : Width;
        double logicalHeight = ClientSize.Height > 0
            ? ClientSize.Height
            : (Placement.OrbInsetY * 2 + Placement.OrbSize);

        return (
            (int)Math.Round(logicalWidth * scale, MidpointRounding.ToEven),
            (int)Math.Round(logicalHeight * scale, MidpointRounding.ToEven));
    }

    // ==================== 拖拽 ====================

    /// <summary>
    /// 拖动中：窗口跟手。
    ///
    /// <para>位移的换算见 <see cref="DragGeometry.WindowPosition"/>：
    /// 鼠标的物理位移 → 逻辑位移（按按下时的缩放）→ 物理位移（按当前缩放）。
    /// 单屏上就是 1:1 跟手。</para>
    ///
    /// <para>拖动全程不抢焦点：<c>MoveToPhysicalPixels</c> 用的是
    /// <c>SetWindowPos + SWP_NOACTIVATE</c>，而且窗口本身带 <c>WS_EX_NOACTIVATE</c>。</para>
    /// </summary>
    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        PixelPoint mouseScreen = this.PointToScreen(e.GetPosition(this));
        double dx = mouseScreen.X - _dragMouseStart.X;
        double dy = mouseScreen.Y - _dragMouseStart.Y;
        PixelPoint target = DragGeometry.WindowPosition(
            _dragWindowStart, dx, dy, _dragScaleAtPress, CurrentRenderScaling);

        target = ClampDragTarget(target);
        MoveToPhysicalPixels(target.X, target.Y);

        _dragMoved = DragGeometry.HasMoved(_dragWindowStart, Position);
    }

    /// <summary>
    /// 松手：没动过 = 点球（开始录音）；动过 = 拖完了（记住位置）。
    ///
    /// <para>点球与拖球走的是同一套按下/松手流程，靠"有没有真的动过"区分
    /// （对齐 Python 的 <c>DragSession._moved</c>）。</para>
    /// </summary>
    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 文字条选文本：松手后切回"不激活"。
        //
        // ⚠ 这一步要挂在**窗口**上而不是只挂在文字条上：选区可以拖到文字条外面，
        // 那时的 PointerReleased 不经过文字条 —— 只挂在文字条上，窗口会一直"可激活"，
        // 用户正在打的字就被抢走了（K4a 疑问 #4 的遗留）。
        RestoreTextBarFocus();

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);

        if (!_dragMoved)
        {
            // 点球（想开始录音）。
            StartRecordingRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        SaveOrbPosition();
    }

    /// <summary>
    /// 把拖动中的窗口夹回"允许待着"的范围内。
    ///
    /// <para>按**球**的位置夹（不是窗口）：球的横向按球宽夹、纵向还要给展开后的胶囊留位
    /// （<see cref="Placement.ClampOrbPosition"/>）。</para>
    /// </summary>
    private PixelPoint ClampDragTarget(PixelPoint target)
    {
        double scale = CurrentRenderScaling;
        (int winW, int winH) = WindowSizePhysical(scale);
        if (winW <= 0 || winH <= 0)
        {
            return target;
        }

        // 按**目标点**所在的显示器算 —— 按窗口当前所在那块屏夹的话，
        // 拖到边界就会被原屏的边缘挡住，跨屏拖不过去。
        MonitorScreen? monitor = Monitors.MonitorAtPoint(target.X + (winW / 2), target.Y + (winH / 2));
        NativeMethods.RECT? area = monitor?.WorkArea;
        if (area is null)
        {
            MonitorScreen? primary = Monitors.PrimaryMonitor();
            if (primary is null)
            {
                return target;
            }

            area = primary.WorkArea;
        }

        (int orbX, int orbY) = Placement.OrbScreenPosition(target.X, target.Y, winW, winH, scale);
        (int cx, int cy) = Placement.ClampOrbPosition(orbX, orbY, winW, winH, scale, area.Value);
        (int wx, int wy) = Placement.OrbWindowPosition(cx, cy, winW, winH, scale, area.Value);
        return new PixelPoint(wx, wy);
    }

    /// <summary>
    /// 记住球的位置（相对所在工作区的逻辑偏移 + 显示器设备名）。
    ///
    /// <para>存的是**球**的偏移，不是窗口的；存相对量而不是绝对坐标 ——
    /// 换缩放率、换显示器都不飞。格式与 Python 版一致（<c>"dx,dy"</c>），
    /// 用户拿旧的 <c>config.json</c> 能直接用。</para>
    /// </summary>
    private void SaveOrbPosition()
    {
        if (PositionSaver is null)
        {
            return;
        }

        double scale = CurrentRenderScaling;
        (int winW, int winH) = WindowSizePhysical(scale);
        if (winW <= 0 || winH <= 0)
        {
            return;
        }

        (int orbX, int orbY) = Placement.OrbScreenPosition(Position.X, Position.Y, winW, winH, scale);

        int orbCenterX = orbX + (int)Math.Round(Placement.OrbSize * scale / 2.0, MidpointRounding.ToEven);
        int orbCenterY = orbY + (int)Math.Round(Placement.OrbSize * scale / 2.0, MidpointRounding.ToEven);
        MonitorScreen? monitor = Monitors.MonitorAtPoint(orbCenterX, orbCenterY);

        NativeMethods.RECT? area = monitor?.WorkArea;
        if (area is null)
        {
            MonitorScreen? primary = Monitors.PrimaryMonitor();
            if (primary is null)
            {
                return;
            }

            area = primary.WorkArea;
            monitor = primary;
        }

        string geometry = Placement.EncodeOffset(orbX, orbY, area.Value, scale);
        PositionSaver(new OrbPlacementHint(geometry, monitor?.Device ?? string.Empty));
    }

    // ==================== 动效降级 ====================

    /// <summary>
    /// 动效降级：CSS 的 <c>@media (prefers-reduced-motion: reduce)</c>
    /// （capsule.css L674-684）。
    ///
    /// <para>系统关了"在显示中显示动画"（<c>SPI_GETCLIENTAREAANIMATION</c>）时，
    /// 所有 <c>Transitions</c> 的 <c>Duration</c> 归零 —— 过渡变成"立刻到位"，
    /// 视觉上就是"没有动画"。</para>
    /// </summary>
    /// <param name="disabled">true = 关掉动画。</param>
    public void ApplyReducedMotion(bool disabled)
    {
        _reducedMotion = disabled;

        foreach ((Avalonia.Animation.ITransition transition, TimeSpan duration) in _transitionDurations)
        {
            SetTransitionDuration(transition, disabled ? TimeSpan.Zero : duration);
        }

        if (!disabled)
        {
            // 内容淡入淡出的时长是**跟着形态变**的（展开 .1s / 收起 .12s），
            // 恢复时按当前形态重新落一次。
            ApplyContentFadeDuration();
        }
    }

    /// <summary>
    /// 内容淡入淡出的时长：展开 .1s / 收起 .12s（CSS L627-631 / L615-621）。
    ///
    /// <para>动效降级（<c>prefers-reduced-motion</c>）时归零 —— CSS 那边是
    /// <c>transition: none !important</c>，效果就是"立刻到位"。</para>
    /// </summary>
    private void ApplyContentFadeDuration()
    {
        TimeSpan fade = TimeSpan.FromMilliseconds(
            _form == CapsuleForm.OrbIdle ? FadeOutDurationMs : FadeInDurationMs);

        if (_reducedMotion)
        {
            fade = TimeSpan.Zero;
        }

        foreach (var transitions in _contentOpacityTransitions)
        {
            foreach (var transition in transitions)
            {
                if (transition is DoubleTransition doubleTransition)
                {
                    doubleTransition.Duration = fade;
                    doubleTransition.Delay = TimeSpan.Zero;
                }
            }
        }
    }

    /// <summary>把各个过渡的默认时长记下来（动效降级时置 0，恢复时还原）。</summary>
    private void CollectTransitionDurations()
    {
        _transitionDurations.Clear();

        foreach (Control control in new Control[]
                 {
                     Pill, TextBar, OrbHost, CancelButton, BarContent, StopButton, StateIcon, CopyButton,
                 })
        {
            Avalonia.Animation.Transitions? transitions = control.Transitions;
            if (transitions is null)
            {
                continue;
            }

            foreach (Avalonia.Animation.ITransition transition in transitions)
            {
                _transitionDurations.Add((transition, GetTransitionDuration(transition)));
            }
        }
    }

    /// <summary>
    /// 取过渡的时长。
    ///
    /// <para>Avalonia 11 的 <c>ITransition</c> 接口**不暴露** <c>Duration</c>
    /// （它在泛型基类 <c>Transition&lt;T&gt;</c> 上），所以按具体类型取。</para>
    /// </summary>
    private static TimeSpan GetTransitionDuration(Avalonia.Animation.ITransition transition) => transition switch
    {
        DoubleTransition d => d.Duration,
        CornerRadiusTransition c => c.Duration,
        _ => TimeSpan.Zero,
    };

    /// <summary>设置过渡的时长（理由同 <see cref="GetTransitionDuration"/>）。</summary>
    private static void SetTransitionDuration(Avalonia.Animation.ITransition transition, TimeSpan duration)
    {
        switch (transition)
        {
            case DoubleTransition d:
                d.Duration = duration;
                break;

            case CornerRadiusTransition c:
                c.Duration = duration;
                break;
        }
    }
}
