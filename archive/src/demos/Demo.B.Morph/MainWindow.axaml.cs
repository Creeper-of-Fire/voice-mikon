using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using VmNext.Shell;

namespace DemoB;

/// <summary>
/// Demo B 的窗口：球 ↔ 胶囊形变、呼吸、旋转、波形条、主题切换。
///
/// 动画实现说明（踩坑记录，重构时直接沿用）：
/// · 形变（Width + CornerRadius）用 Transitions：0.8s，CubicEaseOut。
///   形变必须改布局尺寸，不能用 RenderTransform 缩放，否则会和 SetWindowRgn 的窗口区域错位。
/// · 波形条的**高度**用 Transitions（DoubleTransition），每帧只更新目标值，
///   由过渡负责平滑；**位移**直接写 Canvas.Left —— 因为滚动是循环的，
///   用过渡做回卷会看到整条 bar 从右边滑回左边的"跳变"。
/// · 旋转 loader 与呼吸麦克风用 RenderTransform（它们在胶囊内部，不影响窗口几何）。
/// </summary>
public partial class MainWindow : OverlayWindow
{
    private const int HotKeyToggleMorph = 1;
    private const int HotKeyToggleTheme = 2;
    private const int HotKeyQuit = 3;

    private const double BallDiameter = 96;
    private const double CapsuleTargetWidth = 320;
    private const int BarCount = 13;
    private const double BarPitch = 14;
    private const double WaveWidth = 182;

    private readonly HotKeyWindow _hotKeys;
    private readonly DispatcherTimer _timer;
    private readonly FrameTimeRecorder _frames = new();
    private readonly List<Rectangle> _bars = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<double> _widthSamples = new();

    private double _spinnerAngle;
    private bool _expanded;

    /// <summary>创建窗口。</summary>
    public MainWindow()
    {
        InitializeComponent();

        ConfigureMorphTransitions();
        BuildWaveform();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += OnTick;

        // 窗口不抢焦点（NOACTIVATE），键盘交互只能走全局热键
        _hotKeys = new HotKeyWindow();
        _hotKeys.HotKeyPressed += OnHotKeyPressed;
        _hotKeys.Register(HotKeyToggleMorph, NativeMethods.MOD_NOREPEAT, 0x20); // Space
        _hotKeys.Register(HotKeyToggleTheme, NativeMethods.MOD_NOREPEAT, 0x54);  // T
        _hotKeys.Register(HotKeyQuit, NativeMethods.MOD_NOREPEAT, 0x51);         // Q

        PointerPressed += OnPointerPressed;
    }

    /// <summary>
    /// 配置形变过渡。
    ///
    /// 踩坑记录：Avalonia 11.1 里在 XAML 中写 &lt;DoubleTransition Property="Width" /&gt;，
    /// 编译期不报错，但 Property 会被解析成 null，运行时直接 NullReferenceException
    /// （必须写全限定名 "Layoutable.Width" 才找得到静态字段）。
    /// 为了稳定，这里统一在代码里设置过渡。
    /// </summary>
    private void ConfigureMorphTransitions()
    {
        var easing = new CubicEaseOut();
        var duration = TimeSpan.FromMilliseconds(800);

        Capsule.Transitions = new Transitions
        {
            new DoubleTransition { Property = Layoutable.WidthProperty, Duration = duration, Easing = easing },
            new CornerRadiusTransition { Property = Border.CornerRadiusProperty, Duration = duration, Easing = easing },
        };
    }

    /// <summary>造 13 根波形条。</summary>
    private void BuildWaveform()
    {
        this.TryFindResource(OverlayTheme.WaveKey, null, out object? waveResource);
        IBrush waveBrush = waveResource as IBrush ?? new SolidColorBrush(Color.Parse("#7FD1FF"));

        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 6,
                Height = 14,
                RadiusX = 3,
                RadiusY = 3,
                Fill = waveBrush,
                IsVisible = false,
            };

            // 高度用过渡平滑，位移直接写（见类注释）
            bar.Transitions = new Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = HeightProperty,
                    Duration = TimeSpan.FromMilliseconds(140),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            };

            WaveCanvas.Children.Add(bar);
            _bars.Add(bar);
        }
    }

    /// <summary>每帧更新动画。</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds;

        // 波形条：向右到左滚动 + 高度起伏 + 出入边缘淡入淡出（避免回卷时突现）
        for (int i = 0; i < _bars.Count; i++)
        {
            Rectangle bar = _bars[i];
            double x = (i * BarPitch) + WaveWidth - ((t * 62) % (WaveWidth + BarPitch));
            double fade = Math.Clamp((WaveWidth - x) / 12, 0, 1) * Math.Clamp((x + 6) / 12, 0, 1);
            double h = 10 + (38 * (0.5 + (0.5 * Math.Sin((t * 3.4) + (i * 0.55)))));

            bar.SetCurrentValue(Canvas.LeftProperty, x);
            bar.SetCurrentValue(HeightProperty, h);
            bar.Opacity = fade;
        }

        // 旋转 loader
        _spinnerAngle = (_spinnerAngle + 6) % 360;
        if (Spinner.RenderTransform is RotateTransform rotate)
        {
            rotate.Angle = _spinnerAngle;
        }

        // 麦克风呼吸：约 3 秒一个周期
        double breath = 0.5 + (0.5 * Math.Sin(t * (2 * Math.PI / 3)));
        if (MicIcon.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = 0.92 + (0.12 * breath);
            scale.ScaleY = 0.92 + (0.12 * breath);
        }

        MicIcon.Opacity = 0.75 + (0.25 * breath);

        _widthSamples.Add(Capsule.Width);
    }

    private void OnHotKeyPressed(object? sender, int id)
    {
        switch (id)
        {
            case HotKeyToggleMorph:
                ToggleMorph();
                break;
            case HotKeyToggleTheme:
                OverlayTheme.Toggle();
                break;
            case HotKeyQuit:
                Close();
                break;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ToggleMorph();
        }
    }

    /// <summary>球 ↔ 胶囊形变。</summary>
    public void ToggleMorph()
    {
        _expanded = !_expanded;

        Capsule.Width = _expanded ? CapsuleTargetWidth : BallDiameter;
        Capsule.CornerRadius = new CornerRadius(BallDiameter / 2);

        // 区域形状跟着切换：胶囊时用圆角矩形，点击穿透范围才和视觉一致
        RegionShape = _expanded ? OverlayShape.Capsule : OverlayShape.Circle;

        BallIcon.IsVisible = !_expanded;
        CapsuleContent.IsVisible = _expanded;
        BallIcon.Opacity = _expanded ? 0 : 1;
        CapsuleContent.Opacity = _expanded ? 1 : 0;

        foreach (Rectangle bar in _bars)
        {
            bar.IsVisible = _expanded;
        }
    }

    /// <summary>自动化模式：开始采帧。</summary>
    public void StartFrameRecording() => _frames.Start(this);

    /// <summary>自动化模式：取帧统计（时间窗口相对开始记录时刻，毫秒）。</summary>
    public FrameStats GetFrameStats(double? fromMs, double? toMs) => _frames.GetStats(fromMs, toMs);

    /// <summary>自动化模式：距离开始记录过去了多少毫秒。</summary>
    public double RecorderElapsedMs => _frames.NowMs();

    /// <summary>自动化模式：形变过程中采样到的宽度序列（用来判断过渡是否连续）。</summary>
    public IReadOnlyList<double> GetWidthSamples() => _widthSamples;

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // loader 与呼吸麦克风的变换
        Spinner.RenderTransform = new RotateTransform();
        MicIcon.RenderTransform = new ScaleTransform();

        _timer.Start();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _hotKeys.Dispose();
        base.OnClosed(e);
    }
}

