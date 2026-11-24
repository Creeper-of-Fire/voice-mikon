using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VoiceMikonCSharp.Core.Interop;
using VoiceMikonCSharp.Core.Presentation;
using VoiceMikonCSharp.Shell;

// 别名：避免与 System.Threading.Monitor / System.IO.Path 撞名。
using IconPath = Avalonia.Controls.Shapes.Path;
using MonitorScreen = VoiceMikonCSharp.Core.Interop.Monitor;

namespace VoiceMikonCSharp.Ui;

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
/// <para><b>本类只"演"</b>：什么时候发生、停多久、到点回哪，全归 presenter 的 dwell 表。
/// 入口是 <see cref="SetMode"/>（对应 capsule.js 的 <c>applyMode</c>）。</para>
///
/// <para><b>分层</b>：
/// <list type="bullet">
/// <item>窗口的尺寸 / 位置 / 置顶 / 穿透 / 裁剪时机 → <b>Shell</b>（<see cref="OverlayWindow"/>）。</item>
/// <item>胶囊的几何（球 / 跑道 / 波形 / 图标 / 计时 / 状态文字）→ <b>自绘控件</b>
/// <see cref="PillShape"/>。</item>
/// <item>文本预览（框选 / 复制）→ <b>Avalonia 文本引擎</b>（<c>SelectableTextBlock</c>）。</item>
/// <item>菜单 / 气泡 → <b>普通控件</b>，静态叠放（不回流）。</item>
/// </list></para>
///
/// <para><b>窗口尺寸固定 512×190</b>：球固定待在窗口的**底边居中**，
/// 宽度一变球就横向跑位、高度一变球就在屏幕上跑位。
/// 窗口尺寸**不参与视觉**（内容自己在窗口里排），窗口只负责"往哪摆"和"裁哪块"。</para>
/// </summary>
public partial class CapsuleWindow : OverlayWindow
{
    // ==================== 几何常量（全部来自 CSS / Python）====================

    /// <summary>
    /// 球的直径 = 胶囊高度。来源：capsule.css <c>height: 40px</c> + placement.py <c>ORB_SIZE = 40</c>。
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
    /// 窗口宽度（**固定**，逻辑像素）。来源：voice-mikon 的 host/capsule.py <c>WIN_W = 512</c>。
    ///
    /// <para>为什么必须固定：球固定待在窗口的**底边居中**（placement.py 的模块说明），
    /// 宽度一变球就会横向跑位 —— 真机正是靠"窗口定宽 + 内容居中"保证
    /// 球↔胶囊形变时轨迹严丝合缝、命中区与视觉形状始终对得上。</para>
    /// </summary>
    public const double WindowWidth = 512;

    /// <summary>文字条的最大高度（逻辑像素）。来源：capsule.css <c>.textbar { max-height: 120px }</c>。</summary>
    public const double TextBarMaxHeight = 120;

    /// <summary>上下留白（逻辑像素）。来源：capsule.css <c>.stack { padding: 10px 8px }</c>。</summary>
    public const double StackPaddingY = 10;

    /// <summary>文字条与胶囊之间的间隙（逻辑像素）。来源：capsule.css <c>.textbar { margin: 0 auto 10px }</c>。</summary>
    public const double StackGap = 10;

    /// <summary>胶囊高度（逻辑像素）。来源：capsule.css <c>.recording-pill { height: 40px }</c>。</summary>
    public const double PillHeight = 40;

    /// <summary>
    /// 窗口高度（**固定**，逻辑像素）。
    ///
    /// <para>= stack 上内边距 10 + 文字条 max-height 120 + 间隙 10 + 胶囊 40 + stack 下内边距 10 = 190。</para>
    /// </summary>
    public const double WindowHeight =
        StackPaddingY + TextBarMaxHeight + StackGap + PillHeight + StackPaddingY;

    /// <summary>胶囊展开态的最大宽度。来源：capsule.css <c>max-width: 480px</c>。</summary>
    public const double PillExpandedMaxWidth = 480;

    /// <summary>球形态的宽度（= 胶囊高度 = 球直径）。来源：capsule.css <c>body.orb-mode.orb-idle .recording-pill { max-width: 40px }</c>。</summary>
    public const double PillCollapsedMaxWidth = 40;

    /// <summary>
    /// 球形态的拖动位移判据（**逻辑**像素）。来源：capsule.js 的 <c>ORB_DRAG_THRESHOLD = 5</c>。
    /// 用来区分"点球开始录音"和"拖球挪位置"（手抖一下不算拖）。
    /// 展开态是 0（按下即拖，见 capsule.js 的 <c>CAPSULE_DRAG_OPTS</c>）。
    /// </summary>
    private const double OrbDragThresholdLogical = 5;

    /// <summary>收起态颜色的延迟（ms）。来源：capsule.css <c>transition-delay: 0s,0s,0s,.58s,.58s,.58s</c>。</summary>
    private const int CollapsedColorDelayMs = 580;

    /// <summary>文字条空态占位文案。来源：capsule.js <c>PLACEHOLDER = '正在聆听…'</c>。</summary>
    private const string Placeholder = "正在聆听…";

    /// <summary>球上提示气泡的存活时长（ms）。来源：capsule.css 的 <c>.orb-hint</c> 段落（JS 负责定时收尾）。</summary>
    private const int OrbHintDurationMs = 2200;

    /// <summary>右键菜单的淡入淡出时长（ms）。来源：capsule.css 的 <c>.orb-menu { transition: opacity .12s ease }</c>。</summary>
    private const int MenuFadeDurationMs = 120;

    /// <summary>提示气泡的淡入淡出时长（ms）。来源：capsule.css 的 <c>.orb-hint { transition: opacity .18s ease }</c>。</summary>
    private const int HintFadeDurationMs = 180;

    /// <summary>元素淡入淡出时长（ms）。来源：capsule.css <c>transition: opacity .2s ease</c>。</summary>
    private const int FadeDurationMs = 200;

    // ==================== 运行状态 ====================

    private DispatcherTimer? _colorTimer;
    private DispatcherTimer? _menuFadeTimer;
    private DispatcherTimer? _hintTimer;

    /// <summary>
    /// 命中区的节流重裁（30fps）。
    ///
    /// <para>胶囊的宽度是 800ms 的过渡，过渡期间每帧宽度都在变 ——
    /// 命中区必须跟着变（不然展开后的胶囊点不到）。但每帧都
    /// <c>SetWindowRgn</c> 太重，所以这里节流到 30fps
    /// （对齐真机 <c>host/capsule.py</c> 的 <c>HIT_REFRESH_INTERVAL = 0.5</c> 的思路）。</para>
    /// </summary>
    private readonly DispatcherTimer _regionTimer;

    /// <summary>命中区脏了（可见元素集合或几何变了）。</summary>
    private bool _regionDirty;

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

    /// <summary>
    /// 胶囊控件的**实际**宽度（逻辑像素）。过渡结束后应当等于 <see cref="PillExpandedWidth"/>。
    /// 自检用。
    /// </summary>
    public double PillWidth => Pill.Width;

    /// <summary>
    /// 胶囊展开态的**手算**宽度（按 CSS 盒模型算出来的目标宽度，逻辑像素）。自检用。
    /// </summary>
    public double PillExpandedWidth => Pill.ExpandedWidth;

    /// <summary>
    /// 胶囊里最近一帧画出来的**图标中心**（控件本地坐标）。自检用。
    /// </summary>
    public Point PillIconCenter => Pill.IconCenter;

    /// <summary>
    /// 胶囊控件的几何中心（控件本地坐标）。自检用：应当与 <see cref="PillIconCenter"/> 一致。
    /// </summary>
    public Point PillCenter => new(Pill.Bounds.Width / 2, Pill.Bounds.Height / 2);

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

    /// <summary>
    /// 这一轮的位移判据（逻辑像素）：球形态 5（手抖不算拖），展开态 0（按下即拖）。
    /// 对齐 capsule.js 的 <c>ORB_DRAG_OPTS.threshold</c> / <c>CAPSULE_DRAG_OPTS.threshold</c>。
    /// </summary>
    private double _dragThresholdLogical;

    /// <summary>
    /// 所有过渡的"默认时长"（动效降级时置 0，恢复时还原）。
    /// </summary>
    private readonly List<(Avalonia.Animation.ITransition Transition, TimeSpan Duration)> _transitionDurationsList = new();

    /// <summary>
    /// 动效降级（CSS 的 <c>@media (prefers-reduced-motion: reduce)</c>）。
    /// true = 所有过渡时长归零。
    /// </summary>
    private bool _reducedMotion;

    /// <summary>创建窗口。</summary>
    public CapsuleWindow()
    {
        InitializeComponent();

        // 窗口尺寸：**固定 512×190**（逻辑像素），SizeToContent = Manual（OverlayWindow 已设）。
        //
        // ⚠ 不能用 SizeToContent：窗口高度一变（文字条出现/变长、菜单弹出），
        // 球在屏幕上就会跟着往下挪 —— 位置归摆位层管，窗口不参与。
        Width = WindowWidth;
        Height = WindowHeight;
        CanResize = false;

        ConfigureTransitions();
        CollectTransitionDurations();
        WireInteractions();

        // 命中区的节流重裁（30fps）。
        _regionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 30.0),
        };
        _regionTimer.Tick += (_, _) =>
        {
            if (!_regionDirty)
            {
                return;
            }

            _regionDirty = false;
            InvalidateRegion();
        };
        _regionTimer.Start();

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

        ApplyForm(CapsuleForm.OrbIdle);
        ApplyPhase(CapsulePhase.Recording);
        WriteText(string.Empty, CapsuleTextKind.Normal);

        // 摆位：等布局落定再摆。
        // 摆哪儿由"上次记住的位置"决定；没记住就用默认位置（工作区右下角）。
        Dispatcher.UIThread.Post(PlaceAtRememberedPosition, DispatcherPriority.Loaded);

        // DPI 变化（拖到另一块缩放不同的屏）→ 按记忆里的逻辑偏移重新摆位。
        // 位置记忆存的是**逻辑**偏移，换缩放时球在屏幕上的相对位置不变。
        ScalingChanged += (_, _) => Dispatcher.UIThread.Post(PlaceAtRememberedPosition, DispatcherPriority.Loaded);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _colorTimer?.Stop();
        _menuFadeTimer?.Stop();
        _hintTimer?.Stop();
        _regionTimer.Stop();

        CapsuleTheme.Applied -= OnThemeApplied;
        CapsuleTheme.DetachPlatformWatcher();

        base.OnClosed(e);
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

                // 自绘控件要知道"有没有可复制的原文"（CSS 的 .can-copy：出现复制钮）。
                Pill.CanCopy = _copyPayload.Length > 0;

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
            Pill.Level = levels[levels.Count - 1];
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
    /// <para>胶囊自身的过渡（宽度形变、内容淡入淡出、球图标淡入淡出）在
    /// <see cref="PillShape"/> 内部 —— 它自己管自己的动画。
    /// 这里只管**普通控件**（文字条 / 菜单 / 气泡）的透明度过渡。</para>
    /// </summary>
    private void ConfigureTransitions()
    {
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

        // .orb-menu { transition: opacity .12s ease }
        OrbMenu.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(MenuFadeDurationMs),
                Easing = CapsuleTheme.CssEase,
            },
        };

        // .orb-hint { transition: opacity .18s ease }
        OrbHint.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(HintFadeDurationMs),
                Easing = CapsuleTheme.CssEase,
            },
        };
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
        Pill.OrbLoading = loading;
    }

    /// <summary>
    /// 切形态。对应 CSS 的 <c>body.orb-mode.orb-idle</c> / <c>body.orb-mode.orb-expanded</c> 两组规则。
    ///
    /// <para>形状过渡、内容淡入淡出、宽度计算都在 <see cref="PillShape"/> 内部 ——
    /// 它只在形态**真的变了**的时候才做一次（重复设成同一个形态不会重新淡入）。</para>
    /// </summary>
    private void ApplyForm(CapsuleForm form)
    {
        _form = form;
        bool orb = form == CapsuleForm.OrbIdle;

        // 光标：胶囊本体是拖拽把手（CSS: .can-drag .recording-pill { cursor: grab }）。
        // 球形态与展开态都是 Hand（拖动中换 grabbing，见 OnPillPointerPressed）。
        Pill.Cursor = new Cursor(StandardCursorType.Hand);

        // 形状过渡**不延迟**；只有颜色在收起态延迟 .58s
        // （CSS: body.orb-mode.orb-idle .recording-pill { transition-delay: 0s,0s,0s,.58s,.58s,.58s }）。
        _colorDelayMs = orb ? CollapsedColorDelayMs : 0;

        // 形态 → 自绘控件（宽度过渡 + 内容淡入淡出都在它内部）。
        Pill.Form = form;

        // 文字条：球形态整块淡出（只淡入淡出，不位移）。
        TextBar.Opacity = orb ? 0 : 1;

        // 球形态下文字条**不占布局**：它整块淡出（CSS: opacity:0 + pointer-events:none）。
        TextBar.IsVisible = !orb;

        RefreshDerivedColors();

        // 可见元素的集合变了（文字条显隐）→ 让 Shell 重裁一次命中区。
        // 形态变了 → 胶囊宽度变了 → 命中区跟着变（节流重裁）。
        _regionDirty = true;
    }

    /// <summary>
    /// 切阶段。对应 capsule.js 的 <c>setPhase(phase, labelText)</c>：
    /// 阶段类名挂在胶囊上，CSS 靠它决定显示波形还是状态文字、右侧是结束钮还是状态图标。
    /// </summary>
    private void ApplyPhase(CapsulePhase phase, string? labelText = null)
    {
        _phase = phase;

        // 阶段 → 自绘控件（波形 / 状态文字 / 结束钮 / 状态图标 / 复制钮都在它内部）。
        Pill.Phase = phase;
        Pill.LabelText = labelText;

        RefreshDerivedColors();

        // 阶段变了 → 胶囊的内容变了 → 宽度变了 → 命中区跟着变。
        _regionDirty = true;
    }

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

        // 文字变了 → 文字条的矩形变了 → 命中区跟着变。
        _regionDirty = true;
    }

    // ==================== 派生色 ====================

    /// <summary>
    /// 重算并补间所有派生色（胶囊底/描边/前景、文字条底/描边/前景、状态文字、状态图标）。
    ///
    /// <para>派生色不是 CSS 令牌：它们的值由「形态 + 阶段 + 主题」三者共同决定
    /// （例如胶囊底色在球形态是录音红、在错误态是 err-bg、在展开态是 deep）。</para>
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

    /// <summary>计时显示。CSS: formatTime → m:ss。</summary>
    private void SetElapsed(double seconds)
    {
        _elapsed = Math.Max(0, seconds);
        Pill.Elapsed = _elapsed;
    }

    /// <summary>清掉上一轮错误态留下的东西（原文、复制按钮、✕ 的语义）。</summary>
    private void ResetErrorFlags()
    {
        _copyPayload = string.Empty;
        _errorDismissable = false;
        Pill.CanCopy = false;
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

        // 胶囊里的按钮（自绘控件自己命中判定，按下时抛事件过来）。
        Pill.CancelClicked += (_, _) => OnCancelClick();
        Pill.StopClicked += (_, _) => StopRecordingRequested?.Invoke(this, EventArgs.Empty);
        Pill.CopyClicked += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);

        // 胶囊宽度变了 → 命中区跟着变（节流重裁）。
        Pill.PropertyChanged += OnPillPropertyChanged;

        // 文字条按下 → 临时激活（要键盘焦点才能选文本 / Ctrl+C）。
        // 对应 capsule.py 的 focus_textbar。
        TextBodyScroll.PointerPressed += OnTextBarPointerPressed;
        TextBodyScroll.PointerReleased += (_, _) => RestoreTextBarFocus();
    }

    /// <summary>胶囊的几何变了（宽度过渡）→ 命中区脏了。</summary>
    private void OnPillPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Layoutable.WidthProperty)
        {
            _regionDirty = true;
        }
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

        bool orb = _form == CapsuleForm.OrbIdle;

        // 展开态：点在 ✕ / ✓ / 复制钮上**不启动拖动**
        // （CSS: makeDraggable 的 filter: noButtons —— 按钮自己有点击语义）。
        // 这里不设 e.Handled：让事件继续走到 PillShape，由它按自己的命中判定触发点击。
        if (!orb && Pill.HitSubRegion(e.GetPosition(Pill)) != PillSubRegion.None)
        {
            return;
        }

        e.Handled = true;

        // 按下 = 可能是一次拖拽的开始（也可能只是一次点击）。
        // 到底是"点"还是"拖"，等松手时看有没有真的挪过
        // （与 Python 的 DragSession._moved 同款）。
        _dragging = true;
        _dragMoved = false;

        // 位移判据：球形态 5 逻辑像素（区分"点球"与"拖球"），展开态 0（按下即拖）。
        // 对齐 capsule.js 的 ORB_DRAG_OPTS / CAPSULE_DRAG_OPTS。
        _dragThresholdLogical = orb ? OrbDragThresholdLogical : 0;

        _dragWindowStart = Position;
        _dragMouseStart = this.PointToScreen(e.GetPosition(this));
        _dragScaleAtPress = CurrentRenderScaling;

        // 捕获指针：鼠标拖到控件外面也要继续收到移动/松开事件。
        e.Pointer.Capture(Pill);

        // 拖动中换 grabbing 光标（CSS: body.orb-dragging .recording-pill { cursor: grabbing }）。
        Pill.Cursor = new Cursor(StandardCursorType.SizeAll);
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

        // 可见元素的集合变了（菜单出来了）→ 命中区要跟着变（看得见就得点得到）。
        _regionDirty = true;
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
            _regionDirty = true;
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

        // 气泡出来了 → 命中区要跟着变。
        _regionDirty = true;

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
            _regionDirty = true;
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
                Dispatcher.UIThread.Post(() => _regionDirty = true, DispatcherPriority.Loaded);
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
    /// 命中区：**只裁"真正可见的那几块"**的并集。
    /// <para>对齐 Python 的 <c>update_regions</c>：前端把可见元素的矩形报上来，
    /// Python 侧 <c>SetWindowRgn</c> 裁成它们的并集 —— 窗口其余部分（大片空白）
    /// 不归窗口管，点击直接穿透到下面的应用。</para>
    ///
    /// <para><b>不搬"上报"这个动作</b>：Avalonia 下窗口就是控件树的根，
    /// 每个元素的 <c>Bounds</c> 直接可读，这里自己算。
    /// "量矩形"这一步抽成了纯函数 <see cref="HitRegionBuilder"/>（可单元测试）。</para>
    ///
    /// <para>⚠ 真机的规矩：浮层（菜单 / 气泡）**单独一块**，不能并进球的包围盒 ——
    /// 并进去中间全是空白。这里每个可见元素各报一块，Shell 侧再 <c>CombineRgn</c> 取并集。</para>
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

            specs.Add(new HitRegionSpec(new Rect(origin.X, origin.Y, bounds.Width, bounds.Height)));
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
        int reserve = ReserveHeightPhysical(scale);

        (int X, int Y)? position = null;
        if (hint is not null && !string.IsNullOrEmpty(hint.Geometry))
        {
            position = Placement.OrbPositionFromGeometry(
                hint.Geometry, monitor.WorkArea, scale, winW, winH, reserveHeight: reserve);
        }

        if (position is null)
        {
            // 还没拖过：给个合理的默认值（工作区右下角）。
            (int orbX, int orbY) = Placement.DefaultOrbPosition(monitor.WorkArea, scale);
            position = Placement.OrbWindowPosition(
                orbX, orbY, winW, winH, scale, monitor.WorkArea, reserveHeight: reserve);
        }

        MoveToPhysicalPixels(position.Value.X, position.Value.Y);
    }

    /// <summary>
    /// 球上方要预留的窗口高度（物理像素）。
    ///
    /// <para>窗口高度固定 → 预留量就是窗口本身的高度（<see cref="WindowHeight"/>）：
    /// 球上方要放得下"文字条 + 胶囊"。球拖得太靠上时，展开后的文字条会顶出屏幕，
    /// 所以纵向夹取要按这个高度把球往下推（球在屏幕上不会跳）。</para>
    /// </summary>
    private static int ReserveHeightPhysical(double scale)
        => (int)Math.Round(WindowHeight * scale, MidpointRounding.ToEven);

    /// <summary>
    /// 窗口的**物理**尺寸（按目标显示器的缩放换算）。
    ///
    /// <para>Avalonia 的 <c>ClientSize</c> 是逻辑像素；Win32 的窗口坐标是物理像素。
    /// 摆位算的是物理坐标，所以要过一遍目标显示器的缩放 ——
    /// 摆到缩放不同的屏上时，窗口会被框架按新 DPI 重新缩放，位置也必须按新缩放算。</para>
    ///
    /// <para>⚠ 尺寸是**常量**（512×190），不取 <c>ClientSize</c> ——
    /// 窗口不随内容变，内容再长也不会把窗口撑高。</para>
    /// </summary>
    private static (int Width, int Height) WindowSizePhysical(double scale)
        => (
            (int)Math.Round(WindowWidth * scale, MidpointRounding.ToEven),
            (int)Math.Round(WindowHeight * scale, MidpointRounding.ToEven));

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

        // 位移判据（逻辑像素 → 物理像素）：球形态 5px（手抖不算拖），展开态 0（按下即拖）。
        if (!_dragMoved)
        {
            double threshold = _dragThresholdLogical * _dragScaleAtPress;
            if (threshold > 0 && Math.Sqrt((dx * dx) + (dy * dy)) <= threshold)
            {
                return;
            }

            _dragMoved = true;
        }

        PixelPoint target = DragGeometry.WindowPosition(
            _dragWindowStart, dx, dy, _dragScaleAtPress, CurrentRenderScaling);

        target = ClampDragTarget(target);
        MoveToPhysicalPixels(target.X, target.Y);
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
        Pill.Cursor = new Cursor(StandardCursorType.Hand);

        if (_form == CapsuleForm.OrbIdle && !_dragMoved)
        {
            // 点球（想开始录音）。展开态松手不触发任何点击动作
            // （CSS: CAPSULE_DRAG_OPTS 没有 onClick）。
            StartRecordingRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_dragMoved)
        {
            SaveOrbPosition();
        }
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
        int reserve = ReserveHeightPhysical(scale);
        (int cx, int cy) = Placement.ClampOrbPosition(
            orbX, orbY, winW, winH, scale, area.Value, reserveHeight: reserve);
        (int wx, int wy) = Placement.OrbWindowPosition(
            cx, cy, winW, winH, scale, area.Value, reserveHeight: reserve);
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
        double scale = CurrentRenderScaling;
        (int winW, int winH) = WindowSizePhysical(scale);
        if (winW <= 0 || winH <= 0)
        {
            return;
        }

        (int orbX, int orbY) = Placement.OrbScreenPosition(Position.X, Position.Y, winW, winH, scale);

        if (PositionSaver is null)
        {
            return;
        }

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

        foreach ((Avalonia.Animation.ITransition transition, TimeSpan duration) in _transitionDurationsList)
        {
            SetTransitionDuration(transition, disabled ? TimeSpan.Zero : duration);
        }

        // 胶囊自己的过渡（宽度形变、内容淡入淡出、球图标呼吸）在 PillShape 内部。
        Pill.ApplyReducedMotion(disabled);
    }

    /// <summary>把各个过渡的默认时长记下来（动效降级时置 0，恢复时还原）。</summary>
    private void CollectTransitionDurations()
    {
        _transitionDurationsList.Clear();

        foreach (Control control in new Control[] { TextBar, OrbMenu, OrbHint })
        {
            Avalonia.Animation.Transitions? transitions = control.Transitions;
            if (transitions is null)
            {
                continue;
            }

            foreach (Avalonia.Animation.ITransition transition in transitions)
            {
                _transitionDurationsList.Add((transition, GetTransitionDuration(transition)));
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
        _ => TimeSpan.Zero,
    };

    /// <summary>设置过渡的时长（理由同 <see cref="GetTransitionDuration"/>）。</summary>
    private static void SetTransitionDuration(Avalonia.Animation.ITransition transition, TimeSpan duration)
    {
        if (transition is DoubleTransition d)
        {
            d.Duration = duration;
        }
    }
}
