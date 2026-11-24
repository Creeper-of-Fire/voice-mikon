using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace VmNext.Shell;

/// <summary>
/// 覆盖层窗口的构造参数。
///
/// 这里**不含任何几何参数**：窗口多大由 UI 层自己定（覆盖层窗口一律
/// <c>SizeToContent = Manual</c>，尺寸是 UI 设计的一部分），裁剪区域
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
    /// 已知约束：不激活 ⇔ 收不到键盘事件 ⇔ 文字选不了、复制不了。
    /// voice-mikon 原设计是"按文字条时临时激活"（capsule.py 的 focus_textbar），
    /// 正式版按同样思路用 <see cref="OverlayWindow.SetAllowActivation"/> 在需要时切换，
    /// 而不是一直开着。
    /// </summary>
    public bool AllowActivation { get; init; }

    /// <summary>
    /// 是否加 WS_EX_LAYERED（分层窗口）。默认 false。
    ///
    /// <para>Avalonia 11 的真透明走 <c>WS_EX_NOREDIRECTIONBITMAP</c> + DirectComposition
    /// 做逐像素 alpha 合成，不需要分层窗口样式；<c>WS_EX_LAYERED</c> 是
    /// WebView2 / WinForms 时代才需要的 workaround，搬过来只会多一份无用复杂度。</para>
    /// </summary>
    public bool UseLayeredStyle { get; init; }
}

/// <summary>
/// 透明覆盖层窗口：真透明 / 不抢焦点 / 置顶 / 点击穿透 / DPI 感知移动，
/// 这几件**平台相关**的事集中在这一个类里。不含任何 UI 内容和设计常量。
///
/// 三条平台约定：
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
    // WS_EX_LAYERED 仍然在管理范围内：默认不启用（见 OverlayOptions.UseLayeredStyle），
    // 但一旦被打开过，这里能把它清掉。
    private const int ManagedExStyle =
        NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST
        | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;

    /// <summary>
    /// 命中区每边向外扩的余量（逻辑像素）。
    ///
    /// <para>命中区是精确按控件 Bounds 裁的，而控件边缘是**抗锯齿**的 ——
    /// 正好切在抗锯齿像素上，边缘看起来全是锯齿。向外扩一点：扩出去的部分
    /// 落在透明区域里看不见，但抗锯齿的那一圈被完整包含进来。</para>
    ///
    /// <para>对照 Python 版 <c>host/capsule.py</c> 的 <c>REGION_MARGIN = 3</c>
    /// （那边是物理像素，这里按当前缩放换算）。</para>
    /// </summary>
    private const int RegionMarginLogicalPx = 3;

    private IntPtr _handle;

    /// <summary>是否允许窗口被激活（false = 加 WS_EX_NOACTIVATE，点击不抢用户焦点）。</summary>
    public bool AllowActivation { get; set; }

    /// <summary>是否加 WS_EX_LAYERED（默认 false：Avalonia 走 DirectComposition，不需要分层窗口样式）。</summary>
    public bool UseLayeredStyle { get; set; }

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

        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;   // → WS_EX_TOOLWINDOW
        Topmost = true;           // → WS_EX_TOPMOST
        ShowActivated = false;    // 只管 Show() 那一刻，后续靠 WS_EX_NOACTIVATE
        CanResize = false;

        // ⚠ 尺寸**不跟内容**：覆盖层窗口的尺寸是 UI 设计的一部分（胶囊固定 512×190），
        // 由 UI 层直接设 Width / Height。让窗口按内容长大会带来两个麻烦：
        // 形变期间每帧 resize（点击穿透区域跟着每帧重算），以及"内容变了窗口就变高"
        // —— 后者会让球在屏幕上跑位（真机 voice-mikon 用固定 512×260 窗口正是为了这个）。
        SizeToContent = SizeToContent.Manual;
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
    /// 设置扩展样式：NOACTIVATE（不抢焦点）/ TOOLWINDOW（不进任务栏）/ TOPMOST（置顶）。
    /// 可重复调用：先清本类管理的位，再按当前配置设回去（幂等）。
    ///
    /// <para>⚠ 不能只设一次：Avalonia 的 <c>WindowImpl.UpdateWindowProperties</c>
    /// 会在很多时机整体重写 exStyle，把这里加的位抹掉（抹掉之后点击就开始抢焦点）。
    /// 所以 <see cref="ApplyRegion"/> 每次调用都会顺带重设一遍。</para>
    ///
    /// <para><b>但只在真的需要改的时候才写</b>：<c>SetWindowLong</c> 会触发
    /// WM_NCCALCSIZE / 重绘 —— 每次裁区域都无条件重设一遍扩展样式，
    /// 右键球的时候窗口会"闪一下"（多出来的那一帧重绘）。
    /// 现在先读当前值，与目标一致就直接返回。</para>
    /// </summary>
    public void ApplyNativeStyles()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        int current = NativeMethods.GetWindowExStyle(_handle);
        int desired = current & ~ManagedExStyle;
        desired |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        if (!AllowActivation)
        {
            desired |= NativeMethods.WS_EX_NOACTIVATE;
        }

        // 默认不加 WS_EX_LAYERED：Avalonia 走 DirectComposition 做逐像素 alpha，
        // 分层窗口样式是 WebView2 / WinForms 时代的 workaround。
        if (UseLayeredStyle)
        {
            desired |= NativeMethods.WS_EX_LAYERED;
        }

        if (desired != current)
        {
            NativeMethods.SetWindowExStyle(_handle, desired);
        }
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
        ApplyNativeStyles();
    }

    /// <summary>
    /// 用 SetWindowRgn 把窗口裁成当前形状：区域之外的像素不再属于本窗口，
    /// 鼠标点击会"穿透"到下层窗口 —— 这就是覆盖层要的点击穿透。
    /// 区域按客户区实际尺寸算，所以内容多大裁多大，不需要几何常量。
    ///
    /// <para>默认裁整个客户区（一个圆角矩形）。UI 层如果重写了
    /// <see cref="BuildHitRegions"/>（返回"真正可见的那几块"），就改成裁
    /// **那几块的并集** —— 真机（voice-mikon）就是这么做的：Python 侧由前端上报
    /// 文字条 / 胶囊 / 浮层的矩形，再 <c>SetWindowRgn</c> 裁成它们的并集，
    /// 窗口其余部分（大片空白）不归窗口管，点击直接穿透到下面的应用。</para>
    /// </summary>
    public void ApplyRegion()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // Avalonia 会在很多时机整体重写扩展样式，把我们加的位抹掉 ——
        // 每次裁区域顺带重设一遍（幂等、便宜）。
        ApplyNativeStyles();

        // 窗口刚 resize 的那一瞬（SizeChanged），子控件的 Bounds 还是旧的 —— 布局在后面才跑。
        // 按旧 Bounds 裁，会把刚露出来的东西（文字条、菜单、被顶上去的球）裁到区域外，
        // 看起来就是"整个坏掉"。所以裁之前先把挂起的布局同步跑完（UpdateLayout），
        // 读到的一定是当前几何。
        UpdateLayout();

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

        IntPtr region = BuildRegion(w, h);
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
    /// 让 Shell 重新裁一次点击穿透区域。
    ///
    /// <para><b>UI 层唯一的裁剪入口</b>：可见元素的集合变了（形态切换、菜单/气泡
    /// 开关、文字条显隐）就调一次。UI 层只负责"报哪些元素可见"，
    /// 裁不裁、裁成什么样、什么时候裁，全在 Shell 这一侧。</para>
    ///
    /// <para>窗口尺寸 / DPI 变化时 Shell 自己会重裁（见 <see cref="OnSizeChanged"/>
    /// 与 <c>ScalingChanged</c>），UI 层不需要管那两件事。</para>
    /// </summary>
    public void InvalidateRegion() => ApplyRegion();

    /// <summary>
    /// 命中区（客户区坐标，**物理像素**）。
    ///
    /// <para>默认返回空 = 用整个客户区（默认行为）；重写它 = 只裁这几块的并集。
    /// UI 层在布局落定后按"真正可见的元素"算出来 —— 真机是前端上报
    /// （<c>capsule.js</c> 的 <c>update_regions</c>），Avalonia 下没有"上报"这一步：
    /// 控件树的 Bounds 直接可读，UI 层自己算完重写这个方法即可。</para>
    ///
    /// <para>⚠ 这个方法在**每次** <see cref="ApplyRegion"/> 时被调用，形变动画期间
    /// 会每帧调用 —— 所以实现要便宜（别在里面做布局或分配大量对象）。</para>
    /// </summary>
    protected virtual IReadOnlyList<HitRegion> BuildHitRegions() => Array.Empty<HitRegion>();

    /// <summary>
    /// 按 <see cref="BuildHitRegions"/> 造区域；没填就退回"整个客户区"。
    /// </summary>
    private IntPtr BuildRegion(int clientWidth, int clientHeight)
    {
        IReadOnlyList<HitRegion> regions = BuildHitRegions();
        if (regions.Count > 0)
        {
            IntPtr? merged = null;
            foreach (HitRegion r in regions)
            {
                if (r.IsEmpty)
                {
                    continue;
                }

                IntPtr piece = CreatePiece(r, clientWidth, clientHeight);
                if (piece == IntPtr.Zero)
                {
                    continue;
                }

                if (merged is null)
                {
                    merged = piece;
                    continue;
                }

                // ⚠ CombineRgn 的返回值**不是句柄**，是**区域类型码**
                // （0=错误 / 1=空区域 / 2=单矩形 / 3=复杂区域；对照 voice-mikon 的
                //  win32/api.py，那边 restype 声明的是 c_int）。
                // NativeMethods 里它被声明成了 IntPtr —— 所以这里只判断"是不是 0"，
                // **绝不能把返回值当句柄用**：那会把类型码 3 当成 GDI 句柄，
                // SetWindowRgn 拿到无效句柄直接失败 → 窗口区域保持旧值 →
                // 菜单那块被整个裁掉（真机症状："右键球，菜单不出现"）。
                //
                // 合并按 MSDN 的官方用法**就地**做：hDest = 第一个源
                // （三个区域不必互不相同），成功时 dest 的内容已经变成并集、句柄不变。
                int combineStatus = NativeMethods.CombineRgn(merged.Value, merged.Value, piece, NativeMethods.RGN_OR).ToInt32();
                NativeMethods.DeleteObject(piece);
                if (combineStatus == 0)
                {
                    // 合并失败（0 = 错误）：这块没并进去，继续用已有的区域。
                    continue;
                }
            }

            if (merged is not null)
            {
                return merged.Value;
            }
        }

        // 兜底：整个客户区（直角矩形）。
        return NativeMethods.CreateRectRgn(0, 0, clientWidth, clientHeight);
    }

    /// <summary>
    /// 造一块区域：**一律直角矩形**。
    ///
    /// <para>每块向外扩 <see cref="RegionMarginLogicalPx"/>（按当前缩放换算成物理像素），
    /// 再夹回客户区内 —— 抗锯齿的边缘才不会被切出锯齿。</para>
    ///
    /// <para>球 / 胶囊 / 气泡的"跑道形"是**画出来的**视觉，不是裁出来的形状：
    /// 裁剪只回答"这块归不归我管"。（对照真机 voice-mikon 的
    /// <c>host/capsule.py</c>：<c>ellipses</c> 集合恒为空。）</para>
    /// </summary>
    private IntPtr CreatePiece(HitRegion region, int clientWidth, int clientHeight)
    {
        int margin = Math.Max(1, OverlayGeometry.ToPixels(RegionMarginLogicalPx, CurrentRenderScaling));

        // 向四周外扩，再夹在客户区范围内（不能越界）。
        int left = Math.Max(0, region.Left - margin);
        int top = Math.Max(0, region.Top - margin);
        int right = Math.Min(clientWidth, region.Right + margin);
        int bottom = Math.Min(clientHeight, region.Bottom + margin);

        if (right <= left || bottom <= top)
        {
            // 完全在客户区之外（扩完还是空的）→ 这块不参与并集。
            return IntPtr.Zero;
        }

        return NativeMethods.CreateRectRgn(left, top, right, bottom);
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

    /// <summary>
    /// 把窗口移到指定的**物理像素**位置（絕對位置）。
    ///
    /// <para>用 SetWindowPos + SWP_NOACTIVATE：移动过程中不会抢用户焦点。
    /// 同时同步 Avalonia 侧的 <see cref="Window.Position"/>，避免后续布局把窗口"弹"回旧位置。</para>
    /// </summary>
    /// <param name="x">目标 X（物理像素，虚拟桌面坐标）。</param>
    /// <param name="y">目标 Y（物理像素，虚拟桌面坐标）。</param>
    public void MoveToPhysicalPixels(int x, int y)
    {
        if (_handle == IntPtr.Zero)
        {
            Position = new PixelPoint(x, y);
            return;
        }

        NativeMethods.SetWindowPos(
            _handle,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        Position = new PixelPoint(x, y);
    }
}
