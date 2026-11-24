using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading;
using VoiceMikonCSharp.Core;
using VoiceMikonCSharp.Core.Interop;
using VoiceMikonCSharp.Core.Presentation;
using VoiceMikonCSharp.Shell;

// 隐式 using 里有 System.Threading / System.Windows.Forms，两边都有同名类型
// （Timer / Monitor）—— 这里点名要哪一个。
using Monitor = VoiceMikonCSharp.Core.Interop.Monitor;
using Timer = System.Threading.Timer;

namespace VoiceMikonCSharp.WebView;

/// <summary>
/// 悬浮球 / 胶囊窗口（WinForms 壳 + WebView2 前端）。
///
/// <para>翻译自 voice-mikon/src/voice_mikon/host/capsule.py，行为逐条对应：
/// 固定尺寸的透明无边框窗、**不抢焦点**、命中区按前端上报的矩形裁剪、
/// 拖拽走统一的拖动会话、球的位置记忆存在 <c>config.orb</c>。</para>
///
/// <para><b>前端</b>是原版 <c>capsule.html/css/js</c>（一字未改），
/// 显示状态的唯一入口仍是 <c>window.__capsule.setMode</c>。</para>
///
/// <para><b>尺寸固定 512×260 逻辑像素</b>，不 resize：
/// WebView2 / Chromium 的内容提交滞后，宿主窗口尺寸变了的那一瞬渲染进程
/// 还停在旧几何上（形态切换时表现为内容闪一下 / 被裁掉一半）。
/// 多余空白靠 <c>SetWindowRgn</c> 裁掉。</para>
/// </summary>
public sealed class CapsuleWindow : WebViewForm
{
    private static readonly Logger _log = Log.GetLogger("webview.capsule");

    private static readonly JsonSerializerOptions _json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>窗口宽（逻辑像素）。来源：capsule.py 的 <c>WIN_W = 512</c>。</summary>
    public const int LogicalWidth = 512;

    /// <summary>窗口高（逻辑像素）。来源：capsule.py 的 <c>WIN_H = 260</c>。</summary>
    public const int LogicalHeight = 260;

    /// <summary>命中裁形每边外扩的余量（**物理**像素）。来源：capsule.py 的 <c>REGION_MARGIN</c>。</summary>
    private const int RegionMargin = 3;

    /// <summary>文字条的圆角半径（逻辑 CSS 像素）。来源：capsule.py 的 <c>TEXTBAR_RADIUS_PX</c>。</summary>
    private const int TextbarRadiusPx = 14;

    /// <summary>浮层的圆角半径（逻辑 CSS 像素）。来源：capsule.py 的 <c>OVERLAY_RADIUS_PX</c>。</summary>
    private const int OverlayRadiusPx = 12;

    /// <summary>常驻悬浮球：前端就绪后等这么久再让球露脸。</summary>
    private static readonly TimeSpan OrbShowDelay = TimeSpan.FromSeconds(0.3);

    /// <summary>守卫的轮询间隔（秒）。来源：capsule.py 的 <c>GUARD_SPARSE</c>。</summary>
    private const double GuardIntervalSeconds = 0.15;

    // ---- 状态 ----

    private string _state = DisplayModes.Orb;
    private DisplayPayload _lastPayload = DisplayPayload.Empty;
    private string _lastPreview = string.Empty;
    private bool _wantVisible;
    private bool _orbShown;
    private bool _selectingText;
    private bool _textbarFocused;
    private IntPtr _targetHwnd;
    private IntPtr _lastForeground;
    private readonly IntPtr _focusRestore;

    private Timer? _orbTimer;
    private Timer? _guardTimer;
    private DragSession _drag = null!;

    // ---- 配置读值 / 写回（由宿主注入；UI 层不直接碰 Config）----
    //
    // 这些是宿主注入的委托，不是设计器属性 —— 必须声明成"不参与序列化"，
    // 否则 WinForms 的代码生成器会报 WFO1000。

    /// <summary>当前主题档位（auto / light / dark）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string> ThemeProvider { get; set; } = () => "auto";

    /// <summary>要不要显示实时预览文字条。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<bool> PreviewEnabledProvider { get; set; } = () => true;

    /// <summary>是不是"悬浮球"形态（界面形态四档里只有 orb 有球）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<bool> OrbEnabledProvider { get; set; } = () => true;

    /// <summary>上次记住的球位置（<c>"dx,dy"</c>）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string> OrbGeometryProvider { get; set; } = () => string.Empty;

    /// <summary>上次记住位置时所在的那块显示器（设备名）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string> OrbMonitorProvider { get; set; } = () => string.Empty;

    /// <summary>记住球的位置（几何 + 显示器设备名）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string, string> OrbPositionSaver { get; set; } = (_, _) => { };

    // ---- 事件（宿主 / 绑定层接）----

    /// <summary>点球开始录音。</summary>
    public event EventHandler? StartRecordingRequested;

    /// <summary>结束录音，进入识别。</summary>
    public event EventHandler? StopRecordingRequested;

    /// <summary>放弃这一轮。</summary>
    public event EventHandler? CancelRecordingRequested;

    /// <summary>手动复制识别结果（参数是原文；返回值 = 有没有真写进剪贴板）。</summary>
    public event Func<string, bool>? CopyRequested;

    /// <summary>用户主动关掉错误提示（返回值 = 有没有真的处理掉）。</summary>
    public event Func<bool>? DismissRequested;

    /// <summary>球菜单里的「设置」。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>球菜单里的「二维码扫描」。</summary>
    public event EventHandler? QrScanRequested;

    /// <summary>球菜单里的「退出」。</summary>
    public event EventHandler? QuitRequested;

    /// <summary>造一个胶囊窗口。</summary>
    /// <param name="focusRestore">启动时的前台窗口句柄（建窗副作用要把前台还回去）。</param>
    public CapsuleWindow(IntPtr focusRestore = default)
        : base("capsule.html", Color.Magenta, transparent: true)
    {
        _focusRestore = focusRestore;

        // 工具窗口 + 不进任务栏 + 置顶。NOACTIVATE 由 SetNoActivateTree 补上
        // （WebView2 的内容子窗口不在 Form.CreateParams 的管辖范围内）。
        TopMost = true;

        _drag = new DragSession(
            windowHandle: () => Handle,
            windowScale: CurrentScale,
            windowSize: WindowSize,
            saveGeometry: SaveOrbPosition,
            restoreFocus: () => GiveBackFocus(UserForeground()),
            placement: DragSession.OrbPlacement(CurrentScale, WindowSize));

        // 建好句柄（不显示）—— WebView2 初始化需要它。
        _ = Handle;
    }

    /// <inheritdoc />
    protected override string BridgeTarget => "__capsule";

    /// <inheritdoc />
    protected override (int Width, int Height) LogicalSize => (LogicalWidth, LogicalHeight);

    // ==================== 生命周期 ====================

    /// <inheritdoc />
    protected override void OnFrontendReadyCore()
    {
        // 补推当前状态：前端就绪之前推的都被丢掉了。
        Push("setMode", _state, PayloadJson(_lastPayload));
        Push("setTheme", ThemeProvider());
        Push("setPreviewEnabled", PreviewEnabledProvider());

        // 前端得知道现在是"球形态"还是"胶囊形态"。
        Push("setOrbMode", OrbEnabledProvider());

        // 球的位置档位：只有"记住的位置"这一档（可拖、持久化）。
        Push("setOrbPosition", "custom");

        if (!string.IsNullOrEmpty(_lastPreview))
        {
            Push("setPreview", _lastPreview);
        }

        if (OrbEnabledProvider())
        {
            // 常驻球：等前端把"球"那一小块报上来再露脸。
            ScheduleShowOrb();
        }

        StartGuard();
        _ = LogViewportAsync();
    }

    /// <summary>起守卫线程：盯住"别抢前台"和"没人要求显示却露着脸"。</summary>
    private void StartGuard()
    {
        if (_guardTimer is not null)
        {
            return;
        }

        _guardTimer = new Timer(
            _ => UiDispatch.Post(GuardTick),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(GuardIntervalSeconds));
    }

    /// <summary>守卫的一拍。</summary>
    private void GuardTick()
    {
        try
        {
            IntPtr hwnd = Handle;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd))
            {
                return;
            }

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != hwnd)
            {
                _lastForeground = fg;
            }

            // 文字条临时拿到过焦点：前台已经离开胶囊 → 把 NOACTIVATE 加回去。
            if (_textbarFocused && fg != hwnd)
            {
                _textbarFocused = false;
                NativeMethods.AddWindowExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE);
            }

            // 没人要求显示却露着脸 → 藏回去。
            if (!WindowShouldBeVisible() && NativeMethods.IsWindowVisible(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                return;
            }

            if (fg == hwnd && _lastForeground != IntPtr.Zero && _lastForeground != hwnd
                && !_drag.IsDragging && !_selectingText)
            {
                // 前台被胶囊占了 → 还回去。
                //
                // WebView2 的内容窗口属于 msedgewebview2.exe，跨进程加不了
                // NOACTIVATE，鼠标点上去就会激活整棵窗口树。
                RestoreForeground(_lastForeground);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"守卫异常: {ex.Message}");
        }
    }

    /// <summary>收尾（退出时调）。</summary>
    public void Shutdown()
    {
        _orbTimer?.Dispose();
        _orbTimer = null;
        _guardTimer?.Dispose();
        _guardTimer = null;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 守卫 / 定时器是 System.Threading.Timer —— Form.Dispose 不会停它们。
            Shutdown();
        }

        base.Dispose(disposing);
    }

    // ==================== 显示状态（presenter → 前端）====================

    /// <summary>
    /// 推显示状态 —— 前端显示状态的**唯一**入口。
    /// </summary>
    /// <param name="mode">显示状态（见 <see cref="DisplayModes"/>）。</param>
    /// <param name="payload">载荷。</param>
    public void SetMode(string mode, DisplayPayload payload)
    {
        if (string.Equals(mode, DisplayModes.Orb, StringComparison.Ordinal))
        {
            // 回到 orb = 这一轮结束了：窗口不再"因为这一轮"留着。
            HideCapsule();
        }
        else if (string.Equals(mode, DisplayModes.Recording, StringComparison.Ordinal))
        {
            // 新一轮录音：先记下这一轮的注入落点，再亮窗口（不抢焦点）。
            SnapshotTargetHwnd();
            ShowCapsule();

            // 主题 / 预览开关再推一遍：改完马上按热键时，推送可能还堵在路上。
            // 这些调用幂等，多推无害。
            Push("setTheme", ThemeProvider());
            Push("setPreviewEnabled", PreviewEnabledProvider());
        }

        _state = mode;
        _lastPayload = payload;
        Push("setMode", mode, PayloadJson(payload));
    }

    /// <summary>主题档位变化。</summary>
    /// <param name="mode">auto / light / dark。</param>
    public void SetTheme(string mode) => Push("setTheme", mode);

    /// <summary>预览开关变化。</summary>
    /// <param name="enabled">要不要显示实时预览文字条。</param>
    public void SetPreviewEnabled(bool enabled) => Push("setPreviewEnabled", enabled);

    /// <summary>实时预览文字（录音过程中已识别出的内容）。</summary>
    /// <param name="text">预览文字。</param>
    public void SetPreviewText(string text)
    {
        _lastPreview = text;
        if (!PreviewEnabledProvider())
        {
            return;
        }

        Push("setPreview", text);
    }

    /// <summary>
    /// 30fps 电平 + 计时。
    ///
    /// <para>推的是**绝对电平**（dBFS 映射后的 0..1），前端绝不做窗口内归一化。</para>
    /// </summary>
    /// <param name="levels">电平序列（最后一个是最新值）。</param>
    /// <param name="elapsed">本轮已录音时长（秒）。</param>
    public void PushLevels(IReadOnlyList<double> levels, double elapsed)
    {
        if (!string.Equals(_state, DisplayModes.Recording, StringComparison.Ordinal))
        {
            return;
        }

        Push("setLevels", JsonSerializer.Serialize(levels, _json));
        Push("setElapsed", elapsed);
    }

    /// <summary>球上冒一句纯视觉的提示（不是状态机命令）。</summary>
    /// <param name="text">提示语。</param>
    public void ShowOrbHint(string text) => Push("setOrbHint", text);

    /// <summary>球 / 胶囊的显隐（界面形态切换时用）。</summary>
    /// <param name="visible">true = 露球；false = 收球。</param>
    public void SetOrbVisible(bool visible)
    {
        if (visible)
        {
            ScheduleShowOrb();
        }
        else
        {
            HideOrb();
        }
    }

    /// <summary>
    /// 把 <see cref="DisplayPayload"/> 序列化成前端要的 snake_case JSON **字符串**。
    ///
    /// <para>与 Python 版一致：<c>json.dumps</c> 过去，前端 <c>parsePayload</c> 自己解析
    /// （它兼容字符串与对象两种形态）。</para>
    /// </summary>
    /// <param name="payload">载荷。</param>
    private static string PayloadJson(DisplayPayload payload) => JsonSerializer.Serialize(
        new
        {
            message = payload.Message,
            text = payload.Text,
            can_copy = payload.CanCopy,
            reason = payload.Reason,
            warn = payload.Warn,
            body = payload.Body,
        },
        _json);

    // ==================== 显隐 ====================

    /// <summary>显示窗口（不抢焦点）。</summary>
    public void ShowCapsule()
    {
        _wantVisible = true;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            ApplyToolStyle();

            // WebView2 的子窗口没有 NOACTIVATE：每次显示都重刷一遍。
            SetNoActivateTree();
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            BringToTop();
        }
        catch (Exception ex)
        {
            _log.Debug($"显示窗口失败: {ex.Message}");
        }
    }

    /// <summary>藏起窗口（常驻球形态下**不藏** —— 球还在那儿等着下一轮）。</summary>
    public void HideCapsule()
    {
        _wantVisible = false;

        if (OrbEnabledProvider())
        {
            return;
        }

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }
        catch (Exception ex)
        {
            _log.Debug($"隐藏窗口失败: {ex.Message}");
        }
    }

    /// <summary>窗口此刻该不该露着（球已经露脸，或这一轮的胶囊正在显示）。</summary>
    private bool WindowShouldBeVisible() => _orbShown || _wantVisible;

    /// <summary>把窗口摆到球该在的位置（读记忆的位置；没拖过就是工作区右下角）。</summary>
    private bool PlaceOrb()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        (int Width, int Height)? size = WindowSize();
        if (size is null)
        {
            return false;
        }

        Monitor? mon = Monitors.MonitorByDevice(OrbMonitorProvider()) ?? Monitors.PrimaryMonitor();
        if (mon is null)
        {
            return false;
        }

        NativeMethods.RECT area = mon.WorkArea;
        double scale = mon.Scale;

        (int X, int Y)? position = Placement.OrbPositionFromGeometry(
            OrbGeometryProvider(), area, scale, size.Value.Width, size.Value.Height);

        if (position is null)
        {
            // 还没拖过：给个合理的默认值（工作区右下角）。
            (int ox, int oy) = Placement.DefaultOrbPosition(area, scale);
            position = Placement.OrbWindowPosition(ox, oy, size.Value.Width, size.Value.Height, scale, area);
        }

        try
        {
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                position.Value.X,
                position.Value.Y,
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug($"摆悬浮球位置失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>等前端把"球"那一小块报上来再露脸。</summary>
    private void ScheduleShowOrb()
    {
        lock (_orbTimerLock)
        {
            if (_orbTimer is not null)
            {
                // 已经排上队就别再排：每次都"取消旧的、排个新的"的话，
                // 这个 0.3s 的定时器永远等不到。
                return;
            }

            _orbTimer = new Timer(
                _ => UiDispatch.Post(ShowOrb),
                null,
                OrbShowDelay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private readonly object _orbTimerLock = new();

    /// <summary>作废"稍后露脸"的定时器。</summary>
    private void CancelOrbTimer()
    {
        lock (_orbTimerLock)
        {
            _orbTimer?.Dispose();
            _orbTimer = null;
        }
    }

    /// <summary>把球亮出来：摆好位置 + 显示窗口（不抢焦点）。</summary>
    private void ShowOrb()
    {
        CancelOrbTimer();

        if (!PlaceOrb())
        {
            return;
        }

        // 先记"该露着"：守卫在同一瞬间看到"没人要求显示"会把球藏回去。
        _orbShown = true;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            ApplyToolStyle();
            SetNoActivateTree();
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            BringToTop();
        }
        catch (Exception ex)
        {
            _log.Debug($"显示悬浮球失败: {ex.Message}");
        }
    }

    /// <summary>把球收回去。</summary>
    private void HideOrb()
    {
        CancelOrbTimer();
        _orbShown = false;

        if (_wantVisible)
        {
            // 正在录音：这一轮的胶囊还得留着。
            return;
        }

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }
        catch (Exception ex)
        {
            _log.Debug($"隐藏悬浮球失败: {ex.Message}");
        }
    }

    /// <summary>记住悬浮球的位置（相对所在工作区的逻辑偏移）。</summary>
    private void SaveOrbPosition()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return;
        }

        Monitor? mon = MonitorAtWindow();
        if (mon is null)
        {
            return;
        }

        (int ox, int oy) = Placement.OrbScreenPosition(
            rect.Left, rect.Top, rect.Width, rect.Height, mon.Scale);

        string geometry = Placement.EncodeOffset(ox, oy, mon.WorkArea, mon.Scale);
        try
        {
            OrbPositionSaver(geometry, mon.Device);
        }
        catch (Exception ex)
        {
            _log.Debug($"保存悬浮球位置失败: {ex.Message}");
        }
    }

    /// <summary>窗口当前所在的那块显示器（拿窗口中心点去问）。</summary>
    private Monitor? MonitorAtWindow()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return null;
        }

        int cx = (rect.Left + rect.Right) / 2;
        int cy = (rect.Top + rect.Bottom) / 2;

        Monitor? mon = Monitors.MonitorAtPoint(cx, cy) ?? Monitors.MonitorAtPoint(rect.Left, rect.Top);
        return mon;
    }

    // ==================== 焦点 ====================

    /// <summary>
    /// 开始录音之前把前台还给用户（**焦点陷阱**）。
    ///
    /// <para>鼠标点在 WebView2 的子窗口上会激活整棵窗口树，前台被胶囊抢走 ——
    /// 那一轮识别完的文字就会贴到胶囊自己身上。所以先把前台还给用户，
    /// 并把这一轮的注入落点记下来。</para>
    /// </summary>
    public void PrepareStartRecording()
    {
        IntPtr target = UserForeground();
        if (target != IntPtr.Zero)
        {
            GiveBackFocus(target);
            _targetHwnd = target;
        }
    }

    /// <summary>用户当前所在的窗口（0 = 不知道）。</summary>
    private IntPtr UserForeground()
    {
        IntPtr hwnd = Handle;
        IntPtr last = _lastForeground;
        if (last != IntPtr.Zero && last != hwnd)
        {
            return last;
        }

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != hwnd)
        {
            return fg;
        }

        return IntPtr.Zero;
    }

    /// <summary>把前台还给指定窗口（前台是胶囊时才还）。</summary>
    /// <param name="target">目标窗口。</param>
    public void GiveBackFocus(IntPtr target)
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || target == IntPtr.Zero || target == hwnd)
        {
            return;
        }

        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            RestoreForeground(target);
        }
    }

    /// <summary>
    /// 把前台还给这一轮的注入落点（停止 / 取消录音时用）。
    ///
    /// <para>落点没记住时退回"用户此刻所在的窗口"。</para>
    /// </summary>
    public void GiveBackFocusToTarget()
    {
        IntPtr hwnd = Handle;
        IntPtr target = _targetHwnd;

        if (target == IntPtr.Zero || target == hwnd)
        {
            target = UserForeground();
        }

        if (target == IntPtr.Zero || target == hwnd)
        {
            return;
        }

        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            RestoreForeground(target);
        }
    }

    /// <summary>快照这一轮的注入落点（用户此刻所在的前台窗口）。</summary>
    private void SnapshotTargetHwnd()
    {
        IntPtr hwnd = Handle;
        IntPtr fg = NativeMethods.GetForegroundWindow();

        // 前台**已经是胶囊自己**时不覆盖：那说明这一轮是从胶囊上开始的
        // （点球 / 点 ✓），覆盖成胶囊就等于让文字贴到胶囊自己身上。
        if (fg != IntPtr.Zero && fg != hwnd)
        {
            _targetHwnd = fg;
        }
    }

    /// <summary>把前台窗口还给用户（AttachThreadInput 绕开 Windows 的前台锁）。</summary>
    /// <param name="target">目标窗口。</param>
    private static void RestoreForeground(IntPtr target)
    {
        if (target == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.SetForegroundWindow(target);
        }
        catch (Exception ex)
        {
            _log.Debug($"还前台失败: {ex.Message}");
        }
    }

    /// <summary>文字条被按下：临时把前台与键盘焦点送给文字条。</summary>
    private void FocusTextbar()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // 窗口默认 NOACTIVATE（鼠标点击不会激活它）—— WebView2 拿不到键盘焦点，
            // 选字后的 Ctrl+C、右键菜单的复制全都使不上劲。
            // 临时摘掉 NOACTIVATE，把前台交给它。
            NativeMethods.SetWindowExStyle(
                hwnd,
                NativeMethods.GetWindowExStyle(hwnd) & ~NativeMethods.WS_EX_NOACTIVATE);
            _textbarFocused = true;

            RestoreForeground(hwnd);

            // 让 WebView2 控件拿到键盘焦点（文字条里的内容才选得中）。
            FocusWebView();
        }
        catch (Exception ex)
        {
            _log.Debug($"文字条取焦点失败: {ex.Message}");
        }
    }

    // ==================== 前端消息 ====================

    /// <inheritdoc />
    protected override void OnBridgeMessageCore(BridgeMessage message)
    {
        switch (message.Method)
        {
            case "update_regions":
                ApplyRegions(message.Arg(0));
                break;

            case "begin_drag":
                _drag.Begin();
                break;

            case "end_drag":
                _drag.End();
                break;

            case "move_orb":
            case "move_capsule":
                // 球和胶囊是**同一个窗口**，共用同一份位置记忆 —— 拖哪个都是挪它。
                _drag.Move(message.ArgDouble(0), message.ArgDouble(1));
                break;

            case "start_recording":
                StartRecordingRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "stop_recording":
                StopRecordingRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "cancel_recording":
                CancelRecordingRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "copy_result":
                {
                    bool ok = CopyRequested?.Invoke(message.ArgString(0)) ?? false;
                    Bridge?.Resolve(message.Id, ok);
                    break;
                }

            case "dismiss":
                {
                    bool ok = DismissRequested?.Invoke() ?? false;
                    Bridge?.Resolve(message.Id, ok);
                    break;
                }

            case "open_panel":
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "quit_app":
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "launch_qr_scanner":
                QrScanRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "selecting_text":
                _selectingText = message.ArgBool(0);
                break;

            case "focus_textbar":
                FocusTextbar();
                break;
        }
    }

    // ==================== 命中区裁形（SetWindowRgn）====================

    /// <summary>
    /// 前端上报的可见内容矩形 → 裁掉透明空白区的命中区。
    ///
    /// <para>前端**第一次**上报之前不裁（保持整窗）：那时还没量准，
    /// 贸然裁会把可见内容切掉。</para>
    /// </summary>
    /// <param name="payload">上报的矩形（对象或 JSON 字符串）。</param>
    private void ApplyRegions(JsonElement? payload)
    {
        if (payload is null)
        {
            return;
        }

        IReadOnlyDictionary<string, (double X, double Y, double W, double H)>? rects =
            ParseRegions(payload.Value);
        if (rects is null)
        {
            return;
        }

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        double scale = CurrentScale();

        _log.Debug($"收到区域上报: {string.Join(", ", rects.Select(kv => $"{kv.Key}={kv.Value}"))}");

        // 裁形：
        //   胶囊 / 球 → 矩形
        //   文字条   → 14px 圆角矩形
        //   浮层     → 12px 圆角矩形
        //
        // 胶囊和球都按**矩形**裁：不按"猜出来的形态"分圆/椭圆 ——
        // 状态切换和形状变化不是同一拍，矩形不会切出怪形状。
        var pieces = new List<(int L, int T, int R, int B, int Radius)>();

        foreach (string key in new[] { "textbar", "pill", "overlay" })
        {
            if (!rects.TryGetValue(key, out (double X, double Y, double W, double H) rect))
            {
                continue;
            }

            if (rect.W <= 0 || rect.H <= 0)
            {
                // 隐藏的块不参与并集。
                continue;
            }

            (int L, int T, int R, int B) = Expand(rect, scale);
            int radius = key switch
            {
                "overlay" => (int)Math.Round(OverlayRadiusPx * scale, MidpointRounding.ToEven),
                "textbar" => (int)Math.Round(TextbarRadiusPx * scale, MidpointRounding.ToEven),
                _ => 0,
            };

            pieces.Add((L, T, R, B, radius));
        }

        if (pieces.Count == 0)
        {
            // 都隐藏（正常不会发生）：裁成 1×1 的最小区域。
            pieces.Add((0, 0, 1, 1, 0));
        }

        try
        {
            SetWindowRegions(hwnd, pieces);
        }
        catch (Exception ex)
        {
            _log.Debug($"裁窗口区域失败: {ex.Message}");
        }

        // 换了窗口区域之后，Windows 的命中图不会自动跟着更新：
        // 只有让 DWM 重新合成一次（把窗口提到置顶组顶端）命中才生效。
        BringToTop();
    }

    /// <summary>解析前端上报的矩形（逻辑像素）。</summary>
    /// <param name="payload">上报的矩形。</param>
    /// <returns>各块的矩形；解析不了返回 null。</returns>
    private static IReadOnlyDictionary<string, (double X, double Y, double W, double H)>? ParseRegions(
        JsonElement payload)
    {
        JsonElement root = payload;
        if (root.ValueKind == JsonValueKind.String)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(root.GetString() ?? string.Empty);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, (double, double, double, double)>(StringComparer.Ordinal);

        foreach (string key in new[] { "textbar", "pill", "overlay" })
        {
            if (!root.TryGetProperty(key, out JsonElement raw) || raw.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            double[] values = raw.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0d)
                .ToArray();
            if (values.Length != 4)
            {
                return null;
            }

            result[key] = (values[0], values[1], values[2], values[3]);
        }

        if (result.Count == 0)
        {
            return null;
        }

        return result;
    }

    /// <summary>逻辑矩形 → 物理矩形（外扩 <see cref="RegionMargin"/> 物理像素）。</summary>
    private static (int L, int T, int R, int B) Expand(
        (double X, double Y, double W, double H) rect,
        double scale)
        => (
            (int)Math.Round(rect.X * scale, MidpointRounding.ToEven) - RegionMargin,
            (int)Math.Round(rect.Y * scale, MidpointRounding.ToEven) - RegionMargin,
            (int)Math.Round((rect.X + rect.W) * scale, MidpointRounding.ToEven) + RegionMargin,
            (int)Math.Round((rect.Y + rect.H) * scale, MidpointRounding.ToEven) + RegionMargin);

    /// <summary>
    /// 把窗口的命中区域裁成几块的**并集**（物理像素、窗口坐标）。
    ///
    /// <para>照 Python 的 <c>set_window_regions</c>：区域句柄在
    /// <c>SetWindowRgn</c> 成功之后归系统所有（不能 DeleteObject），
    /// 失败时才由我们自己释放。</para>
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="pieces">各块的矩形与圆角半径（0 = 纯矩形）。</param>
    private static void SetWindowRegions(IntPtr hwnd, IReadOnlyList<(int L, int T, int R, int B, int Radius)> pieces)
    {
        IntPtr region = IntPtr.Zero;

        try
        {
            foreach ((int l, int t, int r, int b, int radius) in pieces)
            {
                IntPtr piece;
                if (radius > 0)
                {
                    int r2 = Math.Max(0, Math.Min(radius, Math.Min(r - l, b - t)));
                    piece = NativeMethods.CreateRoundRectRgn(l, t, r, b, r2, r2);
                }
                else
                {
                    piece = NativeMethods.CreateRectRgn(l, t, r, b);
                }

                if (piece == IntPtr.Zero)
                {
                    continue;
                }

                if (region == IntPtr.Zero)
                {
                    region = piece;
                    continue;
                }

                IntPtr combined = NativeMethods.CombineRgn(region, region, piece, NativeMethods.RGN_OR);
                NativeMethods.DeleteObject(piece);
                if (combined == IntPtr.Zero)
                {
                    // 空区域或出错，这块没并进去（继续用已有的）。
                    continue;
                }
            }

            if (region == IntPtr.Zero)
            {
                return;
            }

            if (NativeMethods.SetWindowRgn(hwnd, region, true) != 0)
            {
                // 成功：区域归系统了，再碰它就是用已释放的句柄。
                region = IntPtr.Zero;
            }
        }
        finally
        {
            if (region != IntPtr.Zero)
            {
                // 只有失败路径会走到这：区域还在我们手里，得自己释放。
                NativeMethods.DeleteObject(region);
            }
        }
    }

    // ==================== 窗口杂项 ====================

    /// <summary>窗口当前的**物理**像素尺寸。</summary>
    /// <returns>宽高；拿不到返回 null。</returns>
    private (int Width, int Height)? WindowSize()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return null;
        }

        int width = rect.Width;
        int height = rect.Height;
        return width <= 0 || height <= 0 ? null : (width, height);
    }

    /// <summary>把窗口改成工具窗口（不进任务栏 / Alt+Tab），并且不抢焦点。</summary>
    private void ApplyToolStyle()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int ex = NativeMethods.GetWindowExStyle(hwnd);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowExStyle(hwnd, ex);
    }

    /// <summary>
    /// 给整棵窗口树补上 <c>WS_EX_NOACTIVATE</c>。
    ///
    /// <para>WebView2 的内容子窗口属于 msedgewebview2.exe，跨进程加不了
    /// NOACTIVATE —— 但它们是本进程的子窗口，枚举一遍就能补上。</para>
    /// </summary>
    private void SetNoActivateTree()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.AddWindowExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE);

        NativeMethods.EnumWindowsProc proc = (child, _) =>
        {
            try
            {
                int ex = NativeMethods.GetWindowExStyle(child);
                if ((ex & NativeMethods.WS_EX_NOACTIVATE) == 0)
                {
                    NativeMethods.SetWindowExStyle(child, ex | NativeMethods.WS_EX_NOACTIVATE);
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            return true;
        };

        NativeMethods.EnumChildWindows(hwnd, proc, IntPtr.Zero);
    }

    /// <summary>把窗口提到**置顶组的最顶端**（不移动、不改大小、不抢焦点）。</summary>
    private void BringToTop()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            _log.Debug($"提到置顶失败: {ex.Message}");
        }
    }
}
