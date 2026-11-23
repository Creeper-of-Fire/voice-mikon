using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace VmNext.Shell;

/// <summary>覆盖层窗口的构造参数（代码式创建时用；XAML 里则直接设属性）。</summary>
public sealed class OverlayOptions
{
    /// <summary>逻辑直径（球）/ 逻辑高度（胶囊），单位与 Avalonia 逻辑像素一致。</summary>
    public double Size { get; init; } = 96;

    /// <summary>胶囊模式下的逻辑宽度。</summary>
    public double CapsuleWidth { get; init; } = 320;

    /// <summary>
    /// 是否允许窗口被激活。
    /// false = 加 WS_EX_NOACTIVATE，点击球不会抢走用户当前窗口的焦点（覆盖层的默认诉求）。
    /// true  = 不加该样式，窗口可获得焦点（文字选择 / Ctrl+C 这类键盘交互需要）。
    /// </summary>
    public bool AllowActivation { get; init; }

    /// <summary>是否强制加 WS_EX_LAYERED（分层窗口，逐像素 alpha）。</summary>
    public bool UseLayeredStyle { get; init; } = true;

    /// <summary>初始形状。</summary>
    public OverlayShape Shape { get; init; } = OverlayShape.Circle;

    /// <summary>是否贴到主屏右下角（验证时避免挡住用户正在看的东西）。</summary>
    public bool AnchorBottomRight { get; init; } = true;

    /// <summary>贴边留白（物理像素）。</summary>
    public int MarginPx { get; init; } = 48;
}

/// <summary>
/// 透明覆盖层窗口：把"真透明 / 不抢焦点 / 置顶 / 点击穿透 / 可拖动"这几件事集中在一个窗口类里。
///
/// 这是重构骨架里唯一"窗口层"的实现，voice-mikon 重写时直接复用。
///
/// 三条平台相关的关键约定：
/// 1. Avalonia 的 <see cref="Window.ShowActivated"/> 只影响 Show() 那一刻，
///    之后用户点击窗口仍然会激活它 → 必须自己补 WS_EX_NOACTIVATE。
/// 2. 透明 + 点击穿透靠 Win32 的 SetWindowRgn（区域之外的像素不属于窗口，点击落到下层窗口），
///    而不是靠 Avalonia 的透明像素 —— 后者只是画出来透明，命中测试仍算命中。
/// 3. 区域坐标是物理像素，布局是逻辑像素，中间必须过 <see cref="TopLevel.RenderScaling"/>。
/// </summary>
public class OverlayWindow : Window
{
    private IntPtr _handle;
    private bool _stylesApplied;
    private int _lastRightEdgePx;

    /// <summary>球/胶囊的逻辑直径（逻辑像素）。</summary>
    public double BallSize { get; set; } = 96;

    /// <summary>胶囊模式的逻辑宽度。</summary>
    public double CapsuleWidth { get; set; } = 320;

    /// <summary>是否允许窗口被激活（false = 加 WS_EX_NOACTIVATE，点击不抢用户焦点）。</summary>
    public bool AllowActivation { get; set; }

    /// <summary>是否强制加 WS_EX_LAYERED（分层窗口）。</summary>
    public bool UseLayeredStyle { get; set; } = true;

    /// <summary>是否贴到主屏右下角。</summary>
    public bool AnchorBottomRightOnShow { get; set; } = true;

    /// <summary>
    /// 形变（球 → 胶囊）时是否保持窗口右边缘不动。
    /// 覆盖层贴着屏幕右下角，展开时应当向左生长，否则会跑出屏幕。
    /// </summary>
    public bool KeepRightEdgeFixed { get; set; }

    /// <summary>贴边留白（物理像素）。</summary>
    public int AnchorMarginPx { get; set; } = 48;

    /// <summary>当前形状；改变后需要重新裁剪窗口区域。</summary>
    public OverlayShape RegionShape { get; set; } = OverlayShape.Circle;

    /// <summary>窗口原生句柄（Opened 之后有效）。</summary>
    public IntPtr Handle => _handle;

    /// <summary>原生样式与区域都设置完毕、窗口可以交互了。</summary>
    public event EventHandler? NativeReady;

    /// <summary>创建覆盖层窗口（XAML 方式；属性在 XAML 里直接设）。</summary>
    public OverlayWindow()
        : this(new OverlayOptions())
    {
    }

    /// <summary>创建覆盖层窗口（代码方式）。</summary>
    public OverlayWindow(OverlayOptions options)
        : base()
    {
        BallSize = options.Size;
        CapsuleWidth = options.CapsuleWidth;
        AllowActivation = options.AllowActivation;
        UseLayeredStyle = options.UseLayeredStyle;
        RegionShape = options.Shape;
        AnchorBottomRightOnShow = options.AnchorBottomRight;
        AnchorMarginPx = options.MarginPx;

        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;   // → WS_EX_TOOLWINDOW
        Topmost = true;           // → WS_EX_TOPMOST
        ShowActivated = false;    // 只管 Show() 那一刻，后续靠 WS_EX_NOACTIVATE
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>
    /// 把窗口摆到主屏右下角（物理像素），避免挡住用户正在使用的内容。
    /// 可以在显示之前调用（用于显示前抓屏幕基线），OnOpened 时会再算一次。
    /// </summary>
    public void AnchorToBottomRight()
    {
        if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, out var work, 0))
        {
            return;
        }

        double scale = CurrentRenderScaling;
        int width = OverlayGeometry.ToPixels(BallSize, scale);
        int height = OverlayGeometry.ToPixels(BallSize, scale);

        Position = new PixelPoint(
            Math.Max(work.Left, work.Right - width - AnchorMarginPx),
            Math.Max(work.Top, work.Bottom - height - AnchorMarginPx));
    }

    /// <summary>窗口打开后的初始化：拿到 HWND → 设置扩展样式 → 裁剪窗口区域。</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (AnchorBottomRightOnShow)
        {
            AnchorToBottomRight();
        }

        _handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_handle != IntPtr.Zero)
        {
            ApplyNativeStyles();
            ApplyRegion();
        }

        // DPI 变化（拖到另一块缩放不同的屏幕）后区域必须重算，否则会与内容错位
        ScalingChanged += (_, _) => ApplyRegion();

        NativeReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>窗口尺寸变化（形变动画期间每帧都会变）后重新裁剪区域。</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (_handle != IntPtr.Zero)
        {
            // 形变时保持右边缘不动：窗口向左生长，不会跑出屏幕
            if (KeepRightEdgeFixed && _lastRightEdgePx != 0 && NativeMethods.GetClientRect(_handle, out var client))
            {
                int newLeft = _lastRightEdgePx - client.Width;
                if (newLeft != Position.X)
                {
                    Position = new PixelPoint(newLeft, Position.Y);
                }
            }

            if (NativeMethods.GetWindowRect(_handle, out var rect))
            {
                _lastRightEdgePx = rect.Right;
            }
        }

        ApplyRegion();
    }

    /// <summary>
    /// 设置扩展样式：NOACTIVATE（不抢焦点）/ TOOLWINDOW（不进任务栏）/ TOPMOST（置顶）/ LAYERED（分层透明）。
    /// </summary>
    public void ApplyNativeStyles()
    {
        if (_handle == IntPtr.Zero || _stylesApplied)
        {
            return;
        }

        int style = NativeMethods.GetWindowExStyle(_handle);
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        if (!AllowActivation)
        {
            style |= NativeMethods.WS_EX_NOACTIVATE;
        }

        if (UseLayeredStyle)
        {
            style |= NativeMethods.WS_EX_LAYERED;
        }

        NativeMethods.SetWindowExStyle(_handle, style);
        _stylesApplied = true;
    }

    /// <summary>
    /// 用 SetWindowRgn 把窗口裁成球/胶囊形状：区域之外的像素不再属于本窗口，
    /// 鼠标点击会"穿透"到下层窗口 —— 这就是覆盖层要的点击穿透。
    /// </summary>
    public void ApplyRegion()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.GetClientRect(_handle, out var client))
        {
            return;
        }

        int w = client.Width;
        int h = client.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var (ellipseW, ellipseH) = OverlayGeometry.CornerEllipse(RegionShape, w, h);
        IntPtr region = NativeMethods.CreateRoundRectRgn(0, 0, w, h, ellipseW, ellipseH);
        if (region == IntPtr.Zero)
        {
            return;
        }

        // SetWindowRgn 成功后区域的所有权归系统；失败则必须自己释放，否则泄漏 GDI 对象
        if (NativeMethods.SetWindowRgn(_handle, region, true) == 0)
        {
            NativeMethods.DeleteObject(region);
        }
    }

    /// <summary>
    /// 把窗口移动指定的**逻辑像素**偏移（DPI 感知：实际位移 = 逻辑像素 × 当前缩放）。
    /// 使用 SetWindowPos + SWP_NOACTIVATE，移动过程中不会抢用户焦点。
    /// </summary>
    public void MoveByLogicalPixels(double dxLogical, double dyLogical)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        double scale = CurrentRenderScaling;
        int dx = OverlayGeometry.ToPixels(dxLogical, scale);
        int dy = OverlayGeometry.ToPixels(dyLogical, scale);

        if (NativeMethods.GetWindowRect(_handle, out var rect))
        {
            int newLeft = rect.Left + dx;
            int newTop = rect.Top + dy;
            NativeMethods.SetWindowPos(
                _handle,
                IntPtr.Zero,
                newLeft,
                newTop,
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            // 同步 Avalonia 侧的 Position，避免后续布局把窗口"弹"回旧位置
            Position = new PixelPoint(newLeft, newTop);
            _lastRightEdgePx = newLeft + rect.Width;
        }
    }

    /// <summary>取当前渲染缩放（DPI / 96）。</summary>
    public double CurrentRenderScaling => RenderScaling > 0 ? RenderScaling : 1.0;
}
