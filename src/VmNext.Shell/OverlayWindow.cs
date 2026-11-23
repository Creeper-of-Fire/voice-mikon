using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace VmNext.Shell;

/// <summary>
/// 覆盖层窗口的构造参数。
///
/// 这里**不含任何几何参数**：窗口多大由内容决定（SizeToContent），裁剪区域
/// 跟随客户区实际尺寸。球的直径、胶囊的高度与宽度属于 UI 设计，真相源在
/// voice-mikon 的 capsule.css / placement.py，正式版翻译到 XAML 里，
/// 窗口层不设默认值。
/// </summary>
public sealed class OverlayOptions
{
    /// <summary>
    /// 是否允许窗口被激活。
    /// false = 加 WS_EX_NOACTIVATE，点击不会抢走用户当前窗口的焦点（覆盖层默认）。
    /// true  = 不加该样式，窗口可获得焦点。
    ///
    /// 已知约束（archive 验证壳确认）：不激活 ⇔ 收不到键盘事件 ⇔ 文字选不了、复制不了。
    /// voice-mikon 原设计是"按文字条时临时激活"（capsule.py 的 focus_textbar），
    /// 正式版按同样思路用 <see cref="OverlayWindow.SetAllowActivation"/> 在需要时切换，
    /// 而不是一直开着。
    /// </summary>
    public bool AllowActivation { get; init; }

    /// <summary>
    /// 是否加 WS_EX_LAYERED（分层窗口，逐像素 alpha）。
    /// 验证壳确认：Avalonia 走 DirectComposition，加上这个样式不影响渲染。
    /// </summary>
    public bool UseLayeredStyle { get; init; } = true;

    /// <summary>初始裁剪形状。形变时通过 <see cref="OverlayWindow.RegionShape"/> 切换。</summary>
    public OverlayShape Shape { get; init; } = OverlayShape.Circle;
}

/// <summary>
/// 透明覆盖层窗口：真透明 / 不抢焦点 / 置顶 / 点击穿透 / DPI 感知移动，
/// 这几件**平台相关**的事集中在这一个类里。不含任何 UI 内容和设计常量。
///
/// 三条平台约定（archive 验证壳实测确认，见 archive/docs/feasibility.md）：
/// 1. Avalonia 的 ShowActivated 只影响 Show() 那一刻，之后点击仍会激活窗口
///    → 必须自己补 WS_EX_NOACTIVATE。
/// 2. 点击穿透靠 Win32 的 SetWindowRgn：区域之外的像素不属于本窗口。
///    Avalonia 的透明像素只是画出来透明，命中测试仍算命中。
/// 3. 区域坐标是物理像素，布局是逻辑像素，中间必须过 RenderScaling；
///    拖到另一块缩放不同的屏幕后 ScalingChanged 会触发重算。
///
/// 不在这层的事（正式版另有归属）：
/// - 摆位（贴哪个角、位置记忆、工作区夹紧）→ 从 host/placement.py 翻译
/// - 形变动画、展开方向等策略 → 从 capsule.css / capsule.js 翻译
/// - 全局热键 → <see cref="HotKeyWindow"/>（不激活窗口收不到键盘事件，热键是必需品）
/// </summary>
public class OverlayWindow : Window
{
    // 本类管理的扩展样式位。重设样式时先清掉这些位再按当前配置设回去，
    // 这样 AllowActivation / UseLayeredStyle 运行时切换才能真正生效（只 OR 的话关不掉）。
    private const int ManagedExStyle =
        NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST
        | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;

    private IntPtr _handle;
    private bool _stylesApplied;

    /// <summary>是否允许窗口被激活（false = 加 WS_EX_NOACTIVATE，点击不抢用户焦点）。</summary>
    public bool AllowActivation { get; set; }

    /// <summary>是否加 WS_EX_LAYERED。</summary>
    public bool UseLayeredStyle { get; set; } = true;

    /// <summary>当前裁剪形状。改完不会自动重裁，需要时调用 <see cref="ApplyRegion"/>（尺寸变化时会自动调）。</summary>
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
        AllowActivation = options.AllowActivation;
        UseLayeredStyle = options.UseLayeredStyle;
        RegionShape = options.Shape;

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
    /// 窗口打开后：拿 HWND → 设扩展样式 → 裁区域。
    /// 位置由调用方在 Show 之前设好（摆位是 placement 层的事，不在窗口层）。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

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

    /// <summary>尺寸变化（形变动画期间每帧都会触发）后重新裁区域。</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRegion();
    }

    /// <summary>
    /// 设置扩展样式：NOACTIVATE（不抢焦点）/ TOOLWINDOW（不进任务栏）/ TOPMOST（置顶）/ LAYERED（分层透明）。
    /// 可重复调用：先清本类管理的位，再按当前配置设回去。
    /// </summary>
    public void ApplyNativeStyles()
    {
        if (_handle == IntPtr.Zero || _stylesApplied)
        {
            return;
        }

        int style = NativeMethods.GetWindowExStyle(_handle) & ~ManagedExStyle;
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
    /// 运行时切换"是否允许激活"。
    /// 用途：按文字条选文本时临时开（拿键盘焦点才能 Ctrl+C），松手再关回去。
    /// 注意：开了之后窗口仍要靠点击或 SetForegroundWindow 才真正成为前台，
    /// 这个方法只是解除 WS_EX_NOACTIVATE 的限制。
    /// </summary>
    public void SetAllowActivation(bool allow)
    {
        if (AllowActivation == allow)
        {
            return;
        }

        AllowActivation = allow;
        _stylesApplied = false;
        ApplyNativeStyles();
    }

    /// <summary>
    /// 用 SetWindowRgn 把窗口裁成当前形状：区域之外的像素不再属于本窗口，
    /// 鼠标点击会"穿透"到下层窗口 —— 这就是覆盖层要的点击穿透。
    /// 区域按客户区实际尺寸算，所以内容多大裁多大，不需要几何常量。
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
    /// 拖拽会话的"按住 → 拖 → 松手"状态机在 drag 层，这里只提供无副作用的移动。
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
        }
    }

    /// <summary>取当前渲染缩放（DPI / 96）。</summary>
    public double CurrentRenderScaling => RenderScaling > 0 ? RenderScaling : 1.0;
}
