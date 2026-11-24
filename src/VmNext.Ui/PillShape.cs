using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VmNext.Ui;

/// <summary>胶囊内部可点击的子区域。</summary>
public enum PillSubRegion
{
    /// <summary>不在任何按钮上。</summary>
    None,

    /// <summary>取消钮（✕）。</summary>
    Cancel,

    /// <summary>结束钮（✓）。</summary>
    Stop,

    /// <summary>复制钮。</summary>
    Copy,
}

/// <summary>
/// 胶囊本体（球 ↔ 跑道）的**自绘控件**。
///
/// <para><b>翻译，不是设计。</b>所有数值都来自 capsule.css / capsule.js，
/// 每个常量旁边都有来源注释。</para>
///
/// <para><b>为什么自绘</b>：胶囊的形状是"一颗 40px 的正圆" ↔ "一条跑道"，
/// 中间还要按 CSS 的 fit-content 规则算宽度。用 Grid / Canvas / Measure 去"拟合"
/// CSS 的结果一定是拟合不准（浏览器按内容排版，布局引擎按约束排版，两套规则）。
/// 所以这里在 <see cref="Render"/> 里按**自己定义的坐标**直接画：
/// 球是圆、胶囊是圆角矩形、波形是 14 根条、图标是 Lucide path、
/// 计时与状态文字用 <see cref="FormattedText"/>。</para>
///
/// <para><b>输入只有"演"参数</b>：形态、阶段、电平、计时、色刷、是否在加载。
/// 什么时候发生、停多久、到点回哪，全归 presenter 的 dwell 表管。</para>
///
/// <para><b>形变</b>：宽度是布局属性 <see cref="Layoutable.Width"/>，
/// 上面挂着 800ms 的 <c>cubic-bezier(.25,.85,.3,1)</c> 过渡 ——
/// 这就是 CSS 的 <c>max-width .8s</c>。内容按**展开态**的坐标画，
/// 宽度不够时右半截被裁掉（CSS 的 <c>overflow:hidden</c>）。</para>
///
/// <para><b>图标永远画在控件几何中心</b>：球形态下那颗正圆的中心，
/// 就是 40×40 的正方形的中心 —— 不是"某个按钮的中心"。</para>
/// </summary>
public sealed class PillShape : Control
{
    // ==================== 几何常量（全部来自 CSS）====================

    /// <summary>胶囊高度。来源：capsule.css <c>.recording-pill { height: 40px }</c>。</summary>
    private const double PillHeight = 40;

    /// <summary>胶囊描边宽度。来源：capsule.css <c>.recording-pill { border: 1px solid … }</c>。</summary>
    private const double PillBorder = 1;

    /// <summary>胶囊内边距。来源：capsule.css <c>.recording-pill { padding: 4px }</c>。</summary>
    private const double PillPadding = 4;

    /// <summary>胶囊内元素间距。来源：capsule.css <c>.recording-pill { gap: 8px }</c>。</summary>
    private const double PillGap = 8;

    /// <summary>胶囊内按钮直径。来源：capsule.css <c>.recording-pill button { width:30px; height:30px }</c>。</summary>
    private const double ButtonSize = 30;

    /// <summary><c>.bar-content</c> 的左右内边距。来源：capsule.css <c>.bar-content { padding: 0 2px }</c>。</summary>
    private const double BarContentPaddingX = 2;

    /// <summary><c>.bar-label</c> 的左右内边距。来源：capsule.css <c>.bar-label { padding: 0 4px }</c>。</summary>
    private const double BarLabelPaddingX = 4;

    /// <summary><c>.bar-timer</c> 的左右内边距。来源：capsule.css <c>.bar-timer { padding: 0 4px }</c>。</summary>
    private const double BarTimerPaddingX = 4;

    /// <summary>波形区宽度。来源：capsule.css <c>.waveform { width: 75px }</c>。</summary>
    private const double WaveformWidth = 75;

    /// <summary>波形区高度。来源：capsule.css <c>.waveform { height: 24px }</c>。</summary>
    private const double WaveformHeight = 24;

    /// <summary>
    /// 波形左侧渐隐的宽度。来源：capsule.css 的
    /// <c>linear-gradient(90deg, transparent 0%, #000 10px)</c>。
    /// </summary>
    private const double WaveformFadeWidth = 10;

    /// <summary>胶囊展开态的最大宽度。来源：capsule.css <c>.recording-pill { max-width: 480px }</c>。</summary>
    private const double PillExpandedMaxWidth = 480;

    /// <summary>球形态的宽度。来源：capsule.css <c>body.orb-mode.orb-idle .recording-pill { max-width: 40px }</c>。</summary>
    private const double PillCollapsedWidth = 40;

    /// <summary>形变时长（ms）。来源：capsule.css <c>max-width .8s</c>。</summary>
    private const int MorphDurationMs = 800;

    /// <summary>展开态的内容淡入时长（ms）。来源：capsule.css <c>transition-duration: .1s</c>。</summary>
    private const int FadeInDurationMs = 100;

    /// <summary>收起态的内容淡出时长（ms）。来源：capsule.css <c>transition-duration: .12s</c>。</summary>
    private const int FadeOutDurationMs = 120;

    /// <summary>球图标淡入淡出时长（ms）。来源：capsule.css <c>transition: opacity .07s ease</c>。</summary>
    private const int OrbFadeDurationMs = 70;

    /// <summary>球图标收起时的延迟（ms）。来源：capsule.css <c>transition-delay: .76s</c>。</summary>
    private const int OrbFadeDelayMs = 760;

    /// <summary>呼吸动画周期（ms）。来源：capsule.css <c>animation: orb-breathe 2.8s ease-in-out infinite</c>。</summary>
    private const int BreathePeriodMs = 2800;

    /// <summary>loader 旋转周期（ms）。来源：capsule.css <c>animation: bar-spin 1.1s linear infinite</c>。</summary>
    private const int SpinPeriodMs = 1100;

    /// <summary>波形推进周期（ms）。来源：capsule.js <c>BAR_TICK_MS = 140</c>。</summary>
    private const int WaveTickMs = WaveformData.BarTickMs;

    // ==================== 图标（Lucide path，24×24 视口）====================

    /// <summary>麦克风。来源：capsule.html 的 <c>svg.orb-mic</c>（20×20 / viewBox 24 / stroke-width 2）。</summary>
    private const string MicPath = "M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3z M19 10v2a7 7 0 0 1-14 0v-2 M12 19 L12 22";

    /// <summary>转圈。来源：capsule.css 的 <c>svg.orb-loader</c> / <c>.bar-state svg</c>。</summary>
    private const string LoaderPath = "M21 12a9 9 0 1 1-6.219-8.56";

    /// <summary>叉（取消）。来源：capsule.html 的 <c>.bar-cancel</c>（16×16）。</summary>
    private const string CancelPath = "M18 6 6 18 M6 6 18 18";

    /// <summary>对勾（结束）。来源：capsule.html 的 <c>.bar-stop</c>（17×17）。</summary>
    private const string StopPath = "M20 6 9 17 4 12";

    /// <summary>感叹号（出错）。来源：capsule.html 的 <c>.bar-state</c>（16×16）。</summary>
    private const string ErrorPath = "M12 16v-4 M12 8h.01 M12 2a10 10 0 1 0 0 20a10 10 0 0 0 0-20z";

    /// <summary>复制。来源：capsule.html 的 <c>.bar-copy</c>（16×16）。</summary>
    private const string CopyPath = "M11 9h9a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-9a2 2 0 0 1-2-2v-9a2 2 0 0 1 2-2z M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1";

    /// <summary>球上麦克风的显示尺寸。来源：capsule.css <c>svg.orb-mic { width:20px; height:20px }</c>。</summary>
    private const double OrbIconSize = 20;

    /// <summary>取消钮图标的显示尺寸。来源：capsule.html 的 <c>.bar-cancel svg</c>（16×16）。</summary>
    private const double CancelIconSize = 16;

    /// <summary>结束钮图标的显示尺寸。来源：capsule.html 的 <c>.bar-stop svg</c>（17×17）。</summary>
    private const double StopIconSize = 17;

    /// <summary>状态图标的显示尺寸。来源：capsule.html 的 <c>.bar-state svg</c>（16×16）。</summary>
    private const double StateIconSize = 16;

    /// <summary>复制钮图标的显示尺寸。来源：capsule.html 的 <c>.bar-copy svg</c>（16×16）。</summary>
    private const double CopyIconSize = 16;

    /// <summary>Lucide 图标的视口边长（path 坐标是 24×24 的）。</summary>
    private const double IconViewport = 24;

    /// <summary>Lucide 图标的描边宽度（视口坐标）。来源：capsule.html 的 <c>stroke-width="2"</c>。</summary>
    private const double IconStrokeWidth = 2;

    // ==================== 字体 ====================

    /// <summary>状态文字的字体。来源：capsule.css <c>.bar-label { font-size:13px; font-weight:500 }</c>。</summary>
    private static readonly Typeface LabelTypeface =
        new(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyle.Normal, FontWeight.Medium);

    /// <summary>计时的字体。来源：capsule.css <c>.bar-timer { font: 12px mono }</c>。</summary>
    private static readonly Typeface TimerTypeface =
        new(new FontFamily("Cascadia Mono, Consolas"), FontStyle.Normal, FontWeight.Normal);

    /// <summary>状态文字的字号。来源：capsule.css <c>.bar-label { font-size:13px }</c>。</summary>
    private const double LabelFontSize = 13;

    /// <summary>计时的字号。来源：capsule.css <c>.bar-timer { font-size:12px }</c>。</summary>
    private const double TimerFontSize = 12;

    // ==================== 静态资源 ====================

    private static readonly Geometry MicGeometry = Geometry.Parse(MicPath);
    private static readonly Geometry LoaderGeometry = Geometry.Parse(LoaderPath);
    private static readonly Geometry CancelGeometry = Geometry.Parse(CancelPath);
    private static readonly Geometry StopGeometry = Geometry.Parse(StopPath);
    private static readonly Geometry ErrorGeometry = Geometry.Parse(ErrorPath);
    private static readonly Geometry CopyGeometry = Geometry.Parse(CopyPath);

    // ==================== 演参数（StyledProperty）====================

    /// <summary>形态：收起的球 ↔ 展开的胶囊。</summary>
    public static readonly StyledProperty<CapsuleForm> FormProperty =
        AvaloniaProperty.Register<PillShape, CapsuleForm>(nameof(Form), CapsuleForm.OrbIdle);

    /// <summary>阶段（决定显示波形还是状态文字、结束钮还是状态图标）。</summary>
    public static readonly StyledProperty<CapsulePhase> PhaseProperty =
        AvaloniaProperty.Register<PillShape, CapsulePhase>(nameof(Phase), CapsulePhase.Recording);

    /// <summary>当前电平（0..1），喂给波形。</summary>
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<PillShape, double>(nameof(Level));

    /// <summary>本轮已录音秒数（显示成 <c>m:ss</c>）。</summary>
    public static readonly StyledProperty<double> ElapsedProperty =
        AvaloniaProperty.Register<PillShape, double>(nameof(Elapsed));

    /// <summary>球上是否显示 loader（模型加载中）。来源：capsule.css <c>body.orb-loading</c>。</summary>
    public static readonly StyledProperty<bool> OrbLoadingProperty =
        AvaloniaProperty.Register<PillShape, bool>(nameof(OrbLoading));

    /// <summary>是否带可复制的原文（错误 / 警告态）。来源：capsule.css <c>.can-copy</c>。</summary>
    public static readonly StyledProperty<bool> CanCopyProperty =
        AvaloniaProperty.Register<PillShape, bool>(nameof(CanCopy));

    /// <summary>状态文字（presenter 覆盖；null = 用阶段默认文案）。</summary>
    public static readonly StyledProperty<string?> LabelTextProperty =
        AvaloniaProperty.Register<PillShape, string?>(nameof(LabelText));

    /// <summary>胶囊内容的整体不透明度（形变时淡入淡出）。</summary>
    public static readonly StyledProperty<double> ContentOpacityProperty =
        AvaloniaProperty.Register<PillShape, double>(nameof(ContentOpacity), 1.0);

    /// <summary>球图标的不透明度（呼吸 + 淡入淡出）。</summary>
    public static readonly StyledProperty<double> OrbOpacityProperty =
        AvaloniaProperty.Register<PillShape, double>(nameof(OrbOpacity), 1.0);

    /// <summary>胶囊底色。</summary>
    public static readonly StyledProperty<IBrush?> PillBackgroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(PillBackground));

    /// <summary>胶囊描边色。</summary>
    public static readonly StyledProperty<IBrush?> PillBorderBrushProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(PillBorderBrush));

    /// <summary>胶囊前景色（球形态下是图标色）。</summary>
    public static readonly StyledProperty<IBrush?> PillForegroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(PillForeground));

    /// <summary>状态文字色。</summary>
    public static readonly StyledProperty<IBrush?> LabelForegroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(LabelForeground));

    /// <summary>计时文字色。</summary>
    public static readonly StyledProperty<IBrush?> TimerForegroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(TimerForeground));

    /// <summary>波形条色（响亮的那根）。</summary>
    public static readonly StyledProperty<IBrush?> WaveBrushProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(WaveBrush));

    /// <summary>波形条色（静音的那根）。</summary>
    public static readonly StyledProperty<IBrush?> WaveDimBrushProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(WaveDimBrush));

    /// <summary>取消钮底色。</summary>
    public static readonly StyledProperty<IBrush?> CancelButtonBackgroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(CancelButtonBackground));

    /// <summary>取消钮前景色。</summary>
    public static readonly StyledProperty<IBrush?> CancelButtonForegroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(CancelButtonForeground));

    /// <summary>结束钮底色。</summary>
    public static readonly StyledProperty<IBrush?> StopButtonBackgroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(StopButtonBackground));

    /// <summary>结束钮前景色。</summary>
    public static readonly StyledProperty<IBrush?> StopButtonForegroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(StopButtonForeground));

    /// <summary>状态图标圆的底色。</summary>
    public static readonly StyledProperty<IBrush?> StateIconBackgroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(StateIconBackground));

    /// <summary>状态图标的颜色。</summary>
    public static readonly StyledProperty<IBrush?> StateIconForegroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(StateIconForeground));

    /// <summary>复制钮底色。</summary>
    public static readonly StyledProperty<IBrush?> CopyButtonBackgroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(CopyButtonBackground));

    /// <summary>复制钮前景色。</summary>
    public static readonly StyledProperty<IBrush?> CopyButtonForegroundProperty =
        AvaloniaProperty.Register<PillShape, IBrush?>(nameof(CopyButtonForeground));

    /// <summary>需要盯着颜色变化的 brush（补间是就地改 <c>brush.Color</c>，得自己订阅）。</summary>
    private static readonly AvaloniaProperty[] WatchedBrushProperties =
    {
        PillBackgroundProperty,
        PillBorderBrushProperty,
        PillForegroundProperty,
        LabelForegroundProperty,
        TimerForegroundProperty,
        WaveBrushProperty,
        WaveDimBrushProperty,
        CancelButtonBackgroundProperty,
        CancelButtonForegroundProperty,
        StopButtonBackgroundProperty,
        StopButtonForegroundProperty,
        StateIconBackgroundProperty,
        StateIconForegroundProperty,
        CopyButtonBackgroundProperty,
        CopyButtonForegroundProperty,
    };

    // ==================== 对外状态 ====================

    /// <summary>形态：收起的悬浮球 ↔ 展开的胶囊。</summary>
    public CapsuleForm Form
    {
        get => GetValue(FormProperty);
        set => SetValue(FormProperty, value);
    }

    /// <summary>阶段。</summary>
    public CapsulePhase Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    /// <summary>当前电平（0..1）。</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>本轮已录音秒数。</summary>
    public double Elapsed
    {
        get => GetValue(ElapsedProperty);
        set => SetValue(ElapsedProperty, value);
    }

    /// <summary>球上是否显示 loader。</summary>
    public bool OrbLoading
    {
        get => GetValue(OrbLoadingProperty);
        set => SetValue(OrbLoadingProperty, value);
    }

    /// <summary>是否带可复制的原文。</summary>
    public bool CanCopy
    {
        get => GetValue(CanCopyProperty);
        set => SetValue(CanCopyProperty, value);
    }

    /// <summary>状态文字（presenter 覆盖；null = 用阶段默认文案）。</summary>
    public string? LabelText
    {
        get => GetValue(LabelTextProperty);
        set => SetValue(LabelTextProperty, value);
    }

    /// <summary>胶囊内容的整体不透明度。</summary>
    public double ContentOpacity
    {
        get => GetValue(ContentOpacityProperty);
        set => SetValue(ContentOpacityProperty, value);
    }

    /// <summary>球图标的不透明度。</summary>
    public double OrbOpacity
    {
        get => GetValue(OrbOpacityProperty);
        set => SetValue(OrbOpacityProperty, value);
    }

    /// <summary>胶囊底色。</summary>
    public IBrush? PillBackground
    {
        get => GetValue(PillBackgroundProperty);
        set => SetValue(PillBackgroundProperty, value);
    }

    /// <summary>胶囊描边色。</summary>
    public IBrush? PillBorderBrush
    {
        get => GetValue(PillBorderBrushProperty);
        set => SetValue(PillBorderBrushProperty, value);
    }

    /// <summary>胶囊前景色。</summary>
    public IBrush? PillForeground
    {
        get => GetValue(PillForegroundProperty);
        set => SetValue(PillForegroundProperty, value);
    }

    /// <summary>状态文字色。</summary>
    public IBrush? LabelForeground
    {
        get => GetValue(LabelForegroundProperty);
        set => SetValue(LabelForegroundProperty, value);
    }

    /// <summary>计时文字色。</summary>
    public IBrush? TimerForeground
    {
        get => GetValue(TimerForegroundProperty);
        set => SetValue(TimerForegroundProperty, value);
    }

    /// <summary>波形条色（响亮的那根）。</summary>
    public IBrush? WaveBrush
    {
        get => GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    /// <summary>波形条色（静音的那根）。</summary>
    public IBrush? WaveDimBrush
    {
        get => GetValue(WaveDimBrushProperty);
        set => SetValue(WaveDimBrushProperty, value);
    }

    /// <summary>取消钮底色。</summary>
    public IBrush? CancelButtonBackground
    {
        get => GetValue(CancelButtonBackgroundProperty);
        set => SetValue(CancelButtonBackgroundProperty, value);
    }

    /// <summary>取消钮前景色。</summary>
    public IBrush? CancelButtonForeground
    {
        get => GetValue(CancelButtonForegroundProperty);
        set => SetValue(CancelButtonForegroundProperty, value);
    }

    /// <summary>结束钮底色。</summary>
    public IBrush? StopButtonBackground
    {
        get => GetValue(StopButtonBackgroundProperty);
        set => SetValue(StopButtonBackgroundProperty, value);
    }

    /// <summary>结束钮前景色。</summary>
    public IBrush? StopButtonForeground
    {
        get => GetValue(StopButtonForegroundProperty);
        set => SetValue(StopButtonForegroundProperty, value);
    }

    /// <summary>状态图标圆的底色。</summary>
    public IBrush? StateIconBackground
    {
        get => GetValue(StateIconBackgroundProperty);
        set => SetValue(StateIconBackgroundProperty, value);
    }

    /// <summary>状态图标的颜色。</summary>
    public IBrush? StateIconForeground
    {
        get => GetValue(StateIconForegroundProperty);
        set => SetValue(StateIconForegroundProperty, value);
    }

    /// <summary>复制钮底色。</summary>
    public IBrush? CopyButtonBackground
    {
        get => GetValue(CopyButtonBackgroundProperty);
        set => SetValue(CopyButtonBackgroundProperty, value);
    }

    /// <summary>复制钮前景色。</summary>
    public IBrush? CopyButtonForeground
    {
        get => GetValue(CopyButtonForegroundProperty);
        set => SetValue(CopyButtonForegroundProperty, value);
    }

    /// <summary>
    /// 展开态的**目标宽度**（按 CSS 的盒模型手算，单位：逻辑像素）。
    ///
    /// <para>自检用：实际布局宽度（<see cref="Layoutable.Width"/>）在 800ms 过渡结束后
    /// 应当与它一致 —— 不一致就说明手算公式与布局对不上。</para>
    /// </summary>
    public double ExpandedWidth => ComputeExpandedWidth();

    /// <summary>
    /// 最近一帧实际画出来的**图标中心**（控件本地坐标）。
    ///
    /// <para>自检用：球形态的图标必须落在控件的几何正中心
    /// （<c>Bounds.Width / 2, Bounds.Height / 2</c>），偏差必须是 0。</para>
    /// </summary>
    public Point IconCenter { get; private set; }

    // ==================== 事件 ====================

    /// <summary>点了取消钮（✕）。</summary>
    public event EventHandler? CancelClicked;

    /// <summary>点了结束钮（✓）。</summary>
    public event EventHandler? StopClicked;

    /// <summary>点了复制钮。</summary>
    public event EventHandler? CopyClicked;

    // ==================== 运行状态 ====================

    private readonly WaveformData _waveform = new();
    private readonly int[] _ages = new int[WaveformData.PoolSize];
    private readonly double[] _heights = new double[WaveformData.PoolSize];

    private readonly HashSet<INotifyPropertyChanged> _watchedBrushes = new();

    private DispatcherTimer? _animTimer;
    private DispatcherTimer? _waveTimer;

    private double _breathPhase;
    private double _spinAngle;
    private double _breathOpacity = 1.0;

    private DoubleTransition? _contentTransition;
    private DoubleTransition? _orbTransition;
    private bool _reducedMotion;

    /// <summary>创建控件。</summary>
    public PillShape()
    {
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Layoutable.WidthProperty,
                Duration = TimeSpan.FromMilliseconds(MorphDurationMs),
                Easing = CapsuleTheme.MorphEase,
            },
            new DoubleTransition
            {
                Property = ContentOpacityProperty,
                Duration = TimeSpan.FromMilliseconds(FadeInDurationMs),
                Easing = CapsuleTheme.CssEase,
            },
            new DoubleTransition
            {
                Property = OrbOpacityProperty,
                Duration = TimeSpan.FromMilliseconds(OrbFadeDurationMs),
                Easing = CapsuleTheme.CssEase,
            },
        };

        foreach (Avalonia.Animation.ITransition transition in Transitions)
        {
            if (transition is DoubleTransition doubleTransition)
            {
                if (doubleTransition.Property == ContentOpacityProperty)
                {
                    _contentTransition = doubleTransition;
                }
                else if (doubleTransition.Property == OrbOpacityProperty)
                {
                    _orbTransition = doubleTransition;
                }
            }
        }

        Width = PillCollapsedWidth;
        Height = PillHeight;
    }

    /// <inheritdoc />
    static PillShape()
    {
        AffectsRender<PillShape>(
            FormProperty,
            PhaseProperty,
            LevelProperty,
            ElapsedProperty,
            OrbLoadingProperty,
            CanCopyProperty,
            LabelTextProperty,
            ContentOpacityProperty,
            OrbOpacityProperty,
            PillBackgroundProperty,
            PillBorderBrushProperty,
            PillForegroundProperty,
            LabelForegroundProperty,
            TimerForegroundProperty,
            WaveBrushProperty,
            WaveDimBrushProperty,
            CancelButtonBackgroundProperty,
            CancelButtonForegroundProperty,
            StopButtonBackgroundProperty,
            StopButtonForegroundProperty,
            StateIconBackgroundProperty,
            StateIconForegroundProperty,
            CopyButtonBackgroundProperty,
            CopyButtonForegroundProperty);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        WatchBrushes();
        UpdateWidth();
        UpdateTimers();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animTimer?.Stop();
        _waveTimer?.Stop();

        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FormProperty)
        {
            bool orb = Form == CapsuleForm.OrbIdle;

            // 内容淡入淡出：展开 .1s / 收起 .12s（CSS L627-631 / L615-621）。
            if (_contentTransition is not null)
            {
                _contentTransition.Duration = TimeSpan.FromMilliseconds(
                    orb ? FadeOutDurationMs : FadeInDurationMs);
            }

            // 球图标：收起时延迟 .76s 才淡入（CSS L564-570），展开时立刻淡出。
            if (_orbTransition is not null)
            {
                _orbTransition.Delay = _reducedMotion
                    ? TimeSpan.Zero
                    : (orb ? TimeSpan.FromMilliseconds(OrbFadeDelayMs) : TimeSpan.Zero);
            }

            ContentOpacity = orb ? 0 : 1;
            OrbOpacity = orb ? 1 : 0;

            UpdateWidth();
            UpdateTimers();
            return;
        }

        if (change.Property == PhaseProperty
            || change.Property == ElapsedProperty
            || change.Property == LabelTextProperty
            || change.Property == CanCopyProperty)
        {
            UpdateWidth();
            UpdateTimers();
            return;
        }

        if (change.Property == LevelProperty)
        {
            _waveform.SetLevel(Level);
            return;
        }

        foreach (AvaloniaProperty watched in WatchedBrushProperties)
        {
            if (change.Property == watched)
            {
                WatchBrushes();
                return;
            }
        }
    }

    /// <summary>
    /// 订阅色刷的变化。
    ///
    /// <para>主题补间是**就地改 <c>brush.Color</c>**（换实例会瞬间跳色），
    /// 所以控件必须自己盯着 brush 的 <see cref="INotifyPropertyChanged.PropertyChanged"/>，
    /// 否则颜色变了画面不重绘。</para>
    /// </summary>
    private void WatchBrushes()
    {
        foreach (AvaloniaProperty property in WatchedBrushProperties)
        {
            if (GetValue(property) is not INotifyPropertyChanged notify || !_watchedBrushes.Add(notify))
            {
                continue;
            }

            notify.PropertyChanged += OnBrushPropertyChanged;
        }
    }

    private void OnBrushPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => InvalidateVisual();

    /// <summary>
    /// 动效降级（CSS 的 <c>@media (prefers-reduced-motion: reduce)</c>）：
    /// 所有过渡时长归零，旋转与呼吸停掉。
    /// </summary>
    /// <param name="disabled">true = 关掉动画。</param>
    public void ApplyReducedMotion(bool disabled)
    {
        _reducedMotion = disabled;

        foreach (Avalonia.Animation.ITransition transition in Transitions ?? [])
        {
            if (transition is DoubleTransition doubleTransition)
            {
                doubleTransition.Duration = disabled
                    ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(
                        doubleTransition.Property == Layoutable.WidthProperty ? MorphDurationMs
                        : doubleTransition.Property == ContentOpacityProperty
                            ? (Form == CapsuleForm.OrbIdle ? FadeOutDurationMs : FadeInDurationMs)
                            : OrbFadeDurationMs);
                doubleTransition.Delay = TimeSpan.Zero;
            }
        }

        UpdateTimers();
    }

    // ==================== 宽度（按 CSS 的 fit-content 手算）====================

    /// <summary>
    /// 重算目标宽度。
    ///
    /// <para><b>手算，不用布局系统量</b>：CSS 的 <c>width: fit-content</c> 是浏览器
    /// 按内容排版算出来的；Avalonia 里拿布局系统的 Measure 结果去"拟合"永远差一点。
    /// 这里按 CSS 的盒模型逐项加：</para>
    /// <code>
    /// 边框 1×2 + 内边距 4×2 + 取消钮 30 + gap 8 + .bar-content + gap 8 + 右按钮 30
    /// .bar-content = padding 2×2 + (波形 75 + 计时 padding 8 + 计时文字)   ← 录音阶段
    /// .bar-content = padding 2×2 + (状态文字 padding 8 + 状态文字)           ← 其它阶段
    /// </code>
    /// <para>其中两处文字宽度用 <see cref="FormattedText"/> 量（文本宽度本来就得量，
    /// 量的是字形不是布局）。</para>
    /// </summary>
    private void UpdateWidth()
    {
        double target = Form == CapsuleForm.OrbIdle
            ? PillCollapsedWidth
            : ComputeExpandedWidth();

        target = Math.Clamp(target, PillCollapsedWidth, PillExpandedMaxWidth);

        if (Math.Abs(Width - target) > 0.01)
        {
            Width = target;
        }
    }

    /// <summary>展开态的目标宽度（逻辑像素）。</summary>
    private double ComputeExpandedWidth()
    {
        double width = (PillBorder * 2) + (PillPadding * 2);   // 边框 + 内边距
        width += ButtonSize;                                    // .bar-cancel
        width += PillGap;                                       // gap 8
        width += ComputeBarContentWidth();
        width += PillGap;                                       // gap 8
        width += ButtonSize;                                    // .bar-stop / .bar-state / .bar-copy

        return width;
    }

    /// <summary><c>.bar-content</c> 的宽度（逻辑像素）。</summary>
    private double ComputeBarContentWidth()
    {
        double width = BarContentPaddingX * 2;   // .bar-content { padding: 0 2px }

        if (Phase == CapsulePhase.Recording)
        {
            width += WaveformWidth;                                   // .waveform { width: 75px }
            width += (BarTimerPaddingX * 2) + MeasureTimerWidth();    // .bar-timer { padding: 0 4px }
        }
        else
        {
            width += (BarLabelPaddingX * 2) + MeasureLabelWidth();    // .bar-label { padding: 0 4px }
        }

        return width;
    }

    private double MeasureTimerWidth() => MeasureTextWidth(FormatElapsed(Elapsed), TimerTypeface, TimerFontSize);

    private double MeasureLabelWidth() => MeasureTextWidth(ResolveLabelText(), LabelTypeface, LabelFontSize);

    /// <summary>
    /// 量一段文字的宽度。
    ///
    /// <para>没有平台文本引擎时（例如单元测试里直接 new 控件）会抛异常 ——
    /// 那时退化成"每个字符 0.6 em"的估算，不影响运行期。</para>
    /// </summary>
    private static double MeasureTextWidth(string text, Typeface typeface, double emSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                emSize,
                null);

            return formatted.Width;
        }
        catch (Exception)
        {
            return text.Length * emSize * 0.6;
        }
    }

    /// <summary>计时文本（CSS 的 <c>formatTime</c>：<c>m:ss</c>）。</summary>
    private static string FormatElapsed(double seconds)
    {
        int total = (int)Math.Floor(Math.Max(0, seconds));
        int minutes = total / 60;
        int rest = total % 60;
        return $"{minutes}:{(rest < 10 ? "0" : string.Empty)}{rest}";
    }

    /// <summary>各阶段的状态文案。来源：capsule.js 的 <c>PHASE_TEXT</c>。</summary>
    private string ResolveLabelText() => LabelText ?? Phase switch
    {
        CapsulePhase.Processing => "识别中…",
        CapsulePhase.Done => "已输入",
        CapsulePhase.Error => "出错了",
        CapsulePhase.Warning => "没送出去",
        CapsulePhase.Cancelled => "已取消",
        _ => string.Empty,
    };

    // ==================== 布局（绘制与命中判定共用）====================

    /// <summary>展开态的内容布局。</summary>
    private struct ContentLayout
    {
        public double CancelCenterX;
        public double RightCenterX;
        public double CenterY;
        public double BarContentLeft;
        public double WaveformLeft;
        public double WaveformTop;
        public double TimerTextLeft;
        public double LabelTextLeft;
        public bool ShowsStop;
        public bool ShowsState;
        public bool ShowsCopy;
    }

    /// <summary>
    /// 展开态的内容布局。
    ///
    /// <para>坐标系是控件本地坐标：控件高 40、描边 1、内边距 4 →
    /// 内容区是 (5, 5)-(W-5, 35)，高 30。横排按 CSS 的 flex + gap 8。</para>
    /// </summary>
    private ContentLayout ComputeLayout()
    {
        double contentTop = PillBorder + PillPadding;               // 5
        double contentHeight = PillHeight - (PillBorder * 2) - (PillPadding * 2);   // 30
        double centerY = contentTop + (contentHeight / 2);           // 20

        var layout = new ContentLayout
        {
            CancelCenterX = PillBorder + PillPadding + (ButtonSize / 2),   // 20
            CenterY = centerY,
        };

        double x = PillBorder + PillPadding + ButtonSize + PillGap;   // 43（.bar-content 的左边）
        layout.BarContentLeft = x + BarContentPaddingX;               // 45（内容真正从这儿开始）

        bool recording = Phase == CapsulePhase.Recording;
        bool canCopy = CanCopy && Phase is CapsulePhase.Error or CapsulePhase.Warning;

        if (recording)
        {
            layout.WaveformLeft = layout.BarContentLeft;
            layout.WaveformTop = contentTop + ((contentHeight - WaveformHeight) / 2);
            layout.TimerTextLeft = layout.BarContentLeft + WaveformWidth + BarTimerPaddingX;
            layout.ShowsStop = true;
        }
        else
        {
            layout.LabelTextLeft = layout.BarContentLeft + BarLabelPaddingX;
            layout.ShowsState = !canCopy;
            layout.ShowsCopy = canCopy;
        }

        // 右按钮的位置：.bar-content 的右边 + gap 8。
        double barContentRight = recording
            ? layout.TimerTextLeft + MeasureTimerWidth() + BarTimerPaddingX
            : layout.LabelTextLeft + MeasureLabelWidth() + BarLabelPaddingX;
        barContentRight += BarContentPaddingX;

        layout.RightCenterX = barContentRight + PillGap + (ButtonSize / 2);

        return layout;
    }

    /// <summary>
    /// 按下的位置落在哪个子区域（控件本地坐标）。
    ///
    /// <para>CSS 的按钮是 30×30 的**圆形**（<c>border-radius:50%</c>），
    /// 命中判定按圆来 —— 与浏览器一致。</para>
    /// </summary>
    /// <param name="localPoint">控件本地坐标。</param>
    public PillSubRegion HitSubRegion(Point localPoint)
    {
        if (Form != CapsuleForm.Expanded)
        {
            return PillSubRegion.None;
        }

        ContentLayout layout = ComputeLayout();
        double radius = ButtonSize / 2;

        if (InsideCircle(localPoint, layout.CancelCenterX, layout.CenterY, radius))
        {
            return PillSubRegion.Cancel;
        }

        if (layout.ShowsStop && InsideCircle(localPoint, layout.RightCenterX, layout.CenterY, radius))
        {
            return PillSubRegion.Stop;
        }

        if (layout.ShowsCopy && InsideCircle(localPoint, layout.RightCenterX, layout.CenterY, radius))
        {
            return PillSubRegion.Copy;
        }

        return PillSubRegion.None;
    }

    private static bool InsideCircle(Point point, double centerX, double centerY, double radius)
    {
        double dx = point.X - centerX;
        double dy = point.Y - centerY;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    // ==================== 绘制 ====================

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        if (Form == CapsuleForm.OrbIdle)
        {
            DrawOrb(context);
            return;
        }

        DrawPill(context);
    }

    /// <summary>球形态：正圆 + 居中的图标。</summary>
    private void DrawOrb(DrawingContext context)
    {
        var outer = new Rect(0, 0, Bounds.Width, Bounds.Height);

        // 描边画在**内侧**（CSS 的 border 在盒内，Win32/Avalonia 的 pen 是居中的）。
        var inner = outer.Deflate(0.5);
        context.DrawEllipse(PillBackground, null, inner);
        context.DrawEllipse(null, MakePen(PillBorderBrush, 1), inner);

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        // 记下图标实际落点（自检：球形态下必须与控件几何中心重合，偏差 0）。
        IconCenter = center;

        if (OrbLoading)
        {
            // 模型加载中：转圈。
            DrawIcon(context, LoaderGeometry, OrbIconSize, center, _spinAngle, PillForeground);
        }
        else
        {
            // 麦克风：呼吸（CSS: orb-breathe，只动透明度 0.72 ↔ 1）。
            using (context.PushOpacity(OrbOpacity * _breathOpacity))
            {
                DrawIcon(context, MicGeometry, OrbIconSize, center, 0, PillForeground);
            }
        }
    }

    /// <summary>展开态：跑道 + 内容。</summary>
    private void DrawPill(DrawingContext context)
    {
        var outer = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var inner = outer.Deflate(0.5);

        // CSS: .recording-pill { border-radius: 999px } —— 浏览器把超过一半的圆角
        // 按比例缩成 20px（= 40/2），所以两端是半圆（跑道形）。
        double radius = Bounds.Height / 2;

        context.DrawRectangle(PillBackground, null, inner, radius, radius);
        context.DrawRectangle(null, MakePen(PillBorderBrush, 1), inner, radius, radius);

        if (ContentOpacity <= 0.001)
        {
            return;
        }

        // 内容按**展开态**的坐标画；宽度不够时右半截被裁掉（CSS: overflow:hidden）。
        using (context.PushClip(outer))
        using (context.PushOpacity(ContentOpacity))
        {
            DrawContent(context);
        }
    }

    /// <summary>展开态的内容：取消钮 + 内容区 + 右按钮。</summary>
    private void DrawContent(DrawingContext context)
    {
        ContentLayout layout = ComputeLayout();

        // .bar-cancel（CSS: background:var(--css.cancel-btn); color:var(--css.fg)）
        DrawRoundButton(
            context,
            layout.CancelCenterX,
            layout.CenterY,
            CancelButtonBackground,
            CancelButtonForeground,
            CancelGeometry,
            CancelIconSize);

        if (Phase == CapsulePhase.Recording)
        {
            DrawWaveform(context, layout);
            DrawTimer(context, layout);
        }
        else
        {
            DrawLabel(context, layout);
        }

        if (layout.ShowsStop)
        {
            // .bar-stop（CSS: background:var(--css.wave); color:var(--css.wave-fg)）
            DrawRoundButton(
                context,
                layout.RightCenterX,
                layout.CenterY,
                StopButtonBackground,
                StopButtonForeground,
                StopGeometry,
                StopIconSize);
        }
        else if (layout.ShowsState)
        {
            // .bar-state（CSS: background:var(--css.state-bg); color:var(--css.wave)）
            context.DrawEllipse(StateIconBackground, null, new Rect(
                layout.RightCenterX - (ButtonSize / 2),
                layout.CenterY - (ButtonSize / 2),
                ButtonSize,
                ButtonSize));

            // ⚠ warning 态 CSS **没有**匹配任何图标 → 什么都不显示（一个空的圆）。照搬。
            Geometry? icon = Phase switch
            {
                CapsulePhase.Processing or CapsulePhase.Cancelled => LoaderGeometry,
                CapsulePhase.Done => StopGeometry,
                CapsulePhase.Error => ErrorGeometry,
                _ => null,
            };

            if (icon is not null)
            {
                double rotation = Phase is CapsulePhase.Processing or CapsulePhase.Cancelled ? _spinAngle : 0;
                DrawIcon(
                    context,
                    icon,
                    StateIconSize,
                    new Point(layout.RightCenterX, layout.CenterY),
                    rotation,
                    StateIconForeground);
            }
        }
        else if (layout.ShowsCopy)
        {
            // .bar-copy（CSS: background:var(--css.state-bg); color:var(--css.fg)）
            DrawRoundButton(
                context,
                layout.RightCenterX,
                layout.CenterY,
                CopyButtonBackground,
                CopyButtonForeground,
                CopyGeometry,
                CopyIconSize);
        }
    }

    /// <summary>画一个 30×30 的圆形按钮（底色 + 居中的描边图标）。</summary>
    private void DrawRoundButton(
        DrawingContext context,
        double centerX,
        double centerY,
        IBrush? background,
        IBrush? foreground,
        Geometry icon,
        double iconSize)
    {
        context.DrawEllipse(background, null, new Rect(
            centerX - (ButtonSize / 2),
            centerY - (ButtonSize / 2),
            ButtonSize,
            ButtonSize));

        DrawIcon(context, icon, iconSize, new Point(centerX, centerY), 0, foreground);
    }

    /// <summary>
    /// 画一个 Lucide 图标（描边风格），**以 <paramref name="center"/> 为中心**。
    ///
    /// <para>SVG 视口是 24×24，按 <c>size / 24</c> 缩放后把 24×24 的框
    /// 摆到以 center 为中心的位置 —— 与 CSS 的 <c>width/height</c> + viewBox 一致。</para>
    /// </summary>
    /// <param name="rotationDegrees">顺时针旋转角度（loader 用）。</param>
    private void DrawIcon(
        DrawingContext context,
        Geometry geometry,
        double size,
        Point center,
        double rotationDegrees,
        IBrush? stroke)
    {
        double scale = size / IconViewport;
        var translate = Matrix.CreateTranslation(center.X - (size / 2), center.Y - (size / 2));
        var scaling = Matrix.CreateScale(scale, scale);

        // 组合矩阵：先缩放、再平移、（可选）最后绕控件中心旋转。
        //
        // ⚠ Avalonia 的 <c>Matrix.operator*</c> 的语义是"**左边的先作用**"：
        //   <c>CreateTranslation(dx,dy) * CreateScale(s,s)</c> 算出来的是
        //   x' = s·(x+dx) —— 先平移再缩放（平移量也跟着被缩放）。
        //   要"先缩放后平移"，必须写成 <c>CreateScale(s,s) * CreateTranslation(dx,dy)</c>。
        //   写反了图标会整体偏移（结束钮的 ✓ 会跑到按钮外面去）。
        Matrix matrix = rotationDegrees == 0
            ? scaling * translate
            : scaling * translate * Matrix.CreateRotation(rotationDegrees * Math.PI / 180.0, center);

        using (context.PushTransform(matrix))
        {
            context.DrawGeometry(null, MakePen(stroke, IconStrokeWidth), geometry);
        }
    }

    /// <summary>波形（13 根可见条 + 1 根屏外的回收位）。</summary>
    private void DrawWaveform(DrawingContext context, ContentLayout layout)
    {
        for (int i = 0; i < _ages.Length; i++)
        {
            int age = _ages[i];
            if (age >= WaveformData.BarCount)
            {
                // 视口左侧外（回收位）。
                continue;
            }

            double height = Math.Max(WaveformData.SilentHeight, _heights[i]);
            double barRight = layout.WaveformLeft + WaveformWidth - (age * WaveformData.BarStep);
            double barLeft = barRight - WaveformData.BarWidth;
            double barTop = layout.WaveformTop + WaveformHeight - height;

            // CSS: .waveform i { width:3px; border-radius:3px } → 3px 宽上 3px 圆角被 clamp 成 1.5px。
            var rect = new Rect(barLeft, barTop, WaveformData.BarWidth, height);

            // 颜色按**目标**高度定（CSS: .waveform i.is-silent { color:var(--wave-dim) }）。
            bool silent = height <= WaveformData.SilentMaxPx;
            IBrush? brush = silent ? WaveDimBrush : WaveBrush;

            // 左侧 10px 渐隐（CSS: linear-gradient(90deg, transparent 0%, #000 10px)）。
            double center = barLeft + (WaveformData.BarWidth / 2);
            double alpha = Math.Clamp((center - layout.WaveformLeft) / WaveformFadeWidth, 0, 1);

            context.DrawRectangle(WithOpacity(brush, alpha), null, rect, 1.5, 1.5);
        }
    }

    /// <summary>计时（CSS: .bar-timer）。</summary>
    private void DrawTimer(DrawingContext context, ContentLayout layout)
    {
        string text = FormatElapsed(Elapsed);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TimerTypeface,
            TimerFontSize,
            TimerForeground);

        context.DrawText(formatted, new Point(layout.TimerTextLeft, layout.CenterY - (formatted.Height / 2)));
    }

    /// <summary>状态文字（CSS: .bar-label）。</summary>
    private void DrawLabel(DrawingContext context, ContentLayout layout)
    {
        string text = ResolveLabelText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            LabelFontSize,
            LabelForeground);

        context.DrawText(formatted, new Point(layout.LabelTextLeft, layout.CenterY - (formatted.Height / 2)));
    }

    /// <summary>造一支画笔（描边色 + 宽度）。</summary>
    private static Pen MakePen(IBrush? brush, double thickness)
        => brush is null ? new Pen(Brushes.Transparent, thickness) : new Pen(brush, thickness);

    /// <summary>给 brush 叠一层透明度（波形左侧的渐隐用）。</summary>
    private static IBrush? WithOpacity(IBrush? brush, double opacity)
    {
        if (brush is null)
        {
            return null;
        }

        if (opacity >= 0.999)
        {
            return brush;
        }

        if (brush is ISolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color) { Opacity = solid.Opacity * opacity };
        }

        return brush;
    }

    // ==================== 定时器 ====================

    /// <summary>
    /// 按当前状态决定定时器跑不跑：
    /// 球形态待机 → 呼吸；球上 loader 或状态图标 loader → 旋转；录音阶段 → 波形。
    /// </summary>
    private void UpdateTimers()
    {
        bool needSpin = OrbLoading || (Phase is CapsulePhase.Processing or CapsulePhase.Cancelled);
        bool needBreathe = Form == CapsuleForm.OrbIdle && !OrbLoading;
        bool needWave = Form == CapsuleForm.Expanded && Phase == CapsulePhase.Recording;

        UpdateAnimTimer(needSpin || needBreathe);
        UpdateWaveTimer(needWave);
    }

    private void UpdateAnimTimer(bool need)
    {
        if (!need || _reducedMotion)
        {
            _animTimer?.Stop();
            return;
        }

        if (_animTimer is null)
        {
            _animTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / WaveformData.LevelFps),
            };
            _animTimer.Tick += OnAnimationTick;
        }

        if (!_animTimer.IsEnabled)
        {
            _animTimer.Start();
        }
    }

    private void UpdateWaveTimer(bool need)
    {
        if (!need)
        {
            _waveTimer?.Stop();
            return;
        }

        if (_waveTimer is null)
        {
            _waveTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(WaveTickMs),
            };
            _waveTimer.Tick += OnWaveTick;
        }

        if (!_waveTimer.IsEnabled)
        {
            _waveform.Reset();
            for (int i = 0; i < _ages.Length; i++)
            {
                _ages[i] = i;
                _heights[i] = WaveformData.SilentHeight;
            }

            _waveTimer.Start();
        }
    }

    /// <summary>动效 tick（~30fps）：呼吸 + 旋转。</summary>
    private void OnAnimationTick(object? sender, EventArgs e)
    {
        double dt = _animTimer?.Interval.TotalMilliseconds ?? (1000.0 / WaveformData.LevelFps);

        if (Form == CapsuleForm.OrbIdle && !OrbLoading)
        {
            _breathPhase = (_breathPhase + dt) % BreathePeriodMs;
            double phase = _breathPhase / BreathePeriodMs;
            double breath = 0.5 + (0.5 * Math.Sin(2 * Math.PI * phase));
            _breathOpacity = 0.72 + (0.28 * breath);
        }

        if (OrbLoading || Phase is CapsulePhase.Processing or CapsulePhase.Cancelled)
        {
            _spinAngle = (_spinAngle + (360.0 * dt / SpinPeriodMs)) % 360.0;
        }

        InvalidateVisual();
    }

    /// <summary>波形 tick（140ms）：推进一根条。</summary>
    private void OnWaveTick(object? sender, EventArgs e)
    {
        _waveform.Advance(_ages, _heights);
        InvalidateVisual();
    }

    // ==================== 指针 ====================

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        Point local = e.GetPosition(this);

        switch (HitSubRegion(local))
        {
            case PillSubRegion.Cancel:
                e.Handled = true;
                CancelClicked?.Invoke(this, EventArgs.Empty);
                break;

            case PillSubRegion.Stop:
                e.Handled = true;
                StopClicked?.Invoke(this, EventArgs.Empty);
                break;

            case PillSubRegion.Copy:
                e.Handled = true;
                CopyClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
