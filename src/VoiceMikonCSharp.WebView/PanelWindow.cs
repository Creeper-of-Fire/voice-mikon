using System;
using System.ComponentModel;
using System.Drawing;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoiceMikonCSharp.Core;
using VoiceMikonCSharp.Core.Presentation;
using VoiceMikonCSharp.Shell;

// 隐式 using 里有 System.Threading / System.Windows.Forms，两边都有同名类型
// （Timer / Monitor）—— 这里点名要哪一个。
using Monitor = VoiceMikonCSharp.Core.Interop.Monitor;
using Timer = System.Threading.Timer;

namespace VoiceMikonCSharp.WebView;

/// <summary>
/// 设置面板窗口（WinForms 壳 + WebView2 前端）。
///
/// <para>翻译自 voice-mikon/src/voice_mikon/host/panel.py。前端是原版
/// <c>panel.html/css/js</c>（一字未改）。</para>
///
/// <para>与胶囊**相反**的三点：
/// <list type="bullet">
/// <item><b>普通不透明</b>：窗口底色是实色，可以用圆角卡片与阴影。</item>
/// <item><b>可激活</b>：面板是要拿焦点做交互的（点分段、点开关）——
/// 所以**不加** <c>WS_EX_NOACTIVATE</c>，只保留"不进任务栏 / Alt+Tab"。</item>
/// <item><b>按需创建、关闭即销毁</b>：托盘菜单或球菜单才打开。</item>
/// </list></para>
///
/// <para>几何记忆格式与 Python 版逐字兼容：<c>"x,y,w,h"</c>，逻辑像素。</para>
/// </summary>
public sealed class PanelWindow : WebViewForm
{
    private static readonly Logger _log = Log.GetLogger("webview.panel");

    /// <summary>窗口离工作区边缘的最小留白（物理像素）。来源：panel.py 的 <c>EDGE_MARGIN</c>。</summary>
    private const int EdgeMargin = 8;

    /// <summary>提示语的停留时长（秒）。</summary>
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(8);

    /// <summary>页面加载完成前的窗口底色（浅色）。必须与 panel.css 的 <c>--bg</c> 一致。</summary>
    private static readonly Color WindowBgLight = ColorTranslator.FromHtml("#ffffff");

    /// <summary>页面加载完成前的窗口底色（深色）。必须与 panel.css 的 <c>--bg</c> 一致。</summary>
    private static readonly Color WindowBgDark = ColorTranslator.FromHtml("#0f0f11");

    private int? _posX;
    private int? _posY;
    private DragSession _drag = null!;

    /// <summary>上次记住的窗口位置（<c>"x,y,w,h"</c>）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string> GeometryProvider { get; set; } = () => string.Empty;

    /// <summary>记住窗口位置。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<string> GeometrySaver { get; set; } = _ => { };

    /// <summary>识别后端切换（local / minimax）。</summary>
    public event EventHandler<string>? BackendChanged;

    /// <summary>模型规格切换（0.6B / 1.7B）。</summary>
    public event EventHandler<string>? ModelChanged;

    /// <summary>主题档位切换（auto / light / dark）。</summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>界面形态切换（orb / resident / on_demand）。</summary>
    public event EventHandler<string>? UiModeChanged;

    /// <summary>实时预览开关。</summary>
    public event EventHandler<bool>? PreviewChanged;

    /// <summary>粘贴后归还原剪贴板内容的开关。</summary>
    public event EventHandler<bool>? RestoreClipboardChanged;

    /// <summary>关闭面板（**不是**退出程序）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>退出整个程序。</summary>
    public event EventHandler? QuitRequested;

    /// <summary>提示语淡出（8 秒后自己没了）。</summary>
    public event EventHandler? NoticeExpired;

    /// <summary>造一个面板窗口。</summary>
    /// <param name="theme">主题档位（决定页面加载前的窗口底色）。</param>
    public PanelWindow(string theme = "auto")
        : base("panel.html", ThemeBackgroundColor(theme), transparent: false)
    {
        ShowInTaskbar = false;

        _drag = new DragSession(
            windowHandle: () => Handle,
            windowScale: CurrentScale,
            windowSize: WindowSize,
            saveGeometry: SaveGeometry,
            restoreFocus: () => { },
            placement: DragSession.WindowPlacement(CurrentScale, WindowSize, EdgeMargin));

        // 建好句柄（不显示）。
        _ = Handle;

        // 位置记忆：有就精确校准，没有就摆到主屏右下角。
        if (PanelGeometry.TryParse(GeometryProvider(), out int x, out int y))
        {
            _posX = x;
            _posY = y;
        }
    }

    /// <inheritdoc />
    protected override string BridgeTarget => "__panel";

    /// <inheritdoc />
    protected override (int Width, int Height) LogicalSize
        => (PanelGeometry.BaseWidth, PanelGeometry.BaseHeight);

    /// <summary>
    /// 按主题档位给建窗底色。
    ///
    /// <para>底色只是"页面加载前的那一瞬间" —— 后面的配色由 CSS 决定，
    /// 所以这里跟 CSS 的 <c>--bg</c> 保持一致就行。</para>
    /// </summary>
    /// <param name="theme">auto / light / dark。</param>
    public static Color ThemeBackgroundColor(string theme) => theme switch
    {
        "light" => WindowBgLight,
        "dark" => WindowBgDark,
        _ => SystemPrefersDark() ? WindowBgDark : WindowBgLight,
    };

    /// <summary>系统是不是深色主题（读注册表）。</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"读系统主题失败，按浅色处理: {ex.Message}");
        }

        return false;
    }

    /// <inheritdoc />
    protected override void OnFrontendReadyCore()
    {
        ApplyToolStyle();

        // 摆位 + 尺寸：页面加载完之后量得到内容高度。
        ApplyPosition();
        _ = FitAsync();
    }

    // ==================== 快照 → 前端 ====================

    /// <summary>把面板快照铺到前端（面板 UI 的**唯一**数据入口）。</summary>
    /// <param name="snapshot">快照。</param>
    public void ApplySnapshot(PanelSnapshot snapshot)
    {
        Push("setState", snapshot.State);
        Push("setHealth", snapshot.HealthStatus, snapshot.HealthReason);
        Push("setModel", snapshot.Model);
        Push("setBackend", snapshot.Backend);
        Push("setTheme", snapshot.Theme);
        Push("setPreviewEnabled", snapshot.PreviewEnabled);
        Push("setRestoreClipboardEnabled", snapshot.RestoreClipboardEnabled);
        Push("setUiMode", snapshot.UiMode);
        Push("setNotice", snapshot.Notice);
        Push("setHotkey", snapshot.Hotkey);

        ScheduleNoticeExpire(snapshot.Notice);
    }

    /// <summary>
    /// 提示语 8 秒后自己淡掉 —— 事实也归零，免得下一条快照把它顶回来。
    ///
    /// <para>前端那边自己会淡掉视觉，但"提示语还在"这件事得有人管
    /// （否则状态一变，旧提示语又冒出来了）。</para>
    /// </summary>
    /// <param name="notice">当前提示语文本。</param>
    private void ScheduleNoticeExpire(string notice)
    {
        lock (_noticeLock)
        {
            _noticeTimer?.Dispose();
            _noticeTimer = null;

            if (string.IsNullOrEmpty(notice))
            {
                return;
            }

            _noticeTimer = new Timer(
                _ => UiDispatch.Post(() => NoticeExpired?.Invoke(this, EventArgs.Empty)),
                null,
                NoticeLifetime,
                Timeout.InfiniteTimeSpan);
        }
    }

    private readonly object _noticeLock = new();
    private Timer? _noticeTimer;

    // ==================== 前端 → 宿主 ====================

    /// <inheritdoc />
    protected override void OnBridgeMessageCore(BridgeMessage message)
    {
        switch (message.Method)
        {
            case "switch_model":
                ModelChanged?.Invoke(this, message.ArgString(0));
                break;

            case "switch_backend":
                BackendChanged?.Invoke(this, message.ArgString(0));
                break;

            case "set_theme":
                ThemeChanged?.Invoke(this, message.ArgString(0));
                break;

            case "set_preview":
                PreviewChanged?.Invoke(this, message.ArgBool(0));
                break;

            case "set_ui_mode":
                UiModeChanged?.Invoke(this, message.ArgString(0));
                break;

            case "set_restore_clipboard":
                RestoreClipboardChanged?.Invoke(this, message.ArgBool(0));
                break;

            case "close_panel":
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "quit_app":
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "begin_drag":
                _drag.Begin();
                break;

            case "move_window":
                _drag.Move(message.ArgDouble(0), message.ArgDouble(1));
                break;

            case "end_drag":
                _drag.End();
                break;
        }
    }

    // ==================== 尺寸自适应 ====================

    /// <summary>把窗口调整到刚好能容纳内容。</summary>
    private async Task FitAsync()
    {
        try
        {
            int need = PanelGeometry.BaseHeight;

            for (int i = 0; i < 3; i++)
            {
                if (!ResizeTo(PanelGeometry.BaseWidth, PanelGeometry.BaseHeight))
                {
                    return;
                }

                await Task.Delay(250).ConfigureAwait(true);

                need = await ContentHeightAsync().ConfigureAwait(true);
                if (need <= PanelGeometry.BaseHeight)
                {
                    KeepOnScreen(PanelGeometry.BaseHeight);
                    return;
                }

                if (!ResizeTo(PanelGeometry.BaseWidth, need))
                {
                    return;
                }

                await Task.Delay(250).ConfigureAwait(true);
            }

            KeepOnScreen(need);
        }
        catch (Exception ex)
        {
            _log.Debug($"面板 fit 失败: {ex.Message}");
        }
    }

    /// <summary>改窗口尺寸（逻辑像素）。</summary>
    /// <param name="width">宽（逻辑像素）。</param>
    /// <param name="height">高（逻辑像素）。</param>
    /// <returns>成功返回 true。</returns>
    private bool ResizeTo(int width, int height)
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            double scale = CurrentScale();
            return NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                (int)Math.Round(width * scale, MidpointRounding.ToEven),
                (int)Math.Round(height * scale, MidpointRounding.ToEven),
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            _log.Debug($"调整窗口尺寸失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>页面内容实际需要的高度（逻辑像素）。</summary>
    /// <returns>内容高度；量不到返回 0。</returns>
    private async Task<int> ContentHeightAsync()
    {
        WebViewBridge? bridge = Bridge;
        if (bridge is null || !FrontendReady)
        {
            return 0;
        }

        try
        {
            string script = "(function(){return {sh:document.body.scrollHeight,ih:window.innerHeight};})()";
            string json = await bridge.EvaluateAsync(script).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(json))
            {
                return 0;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sh", out JsonElement sh)
                && sh.ValueKind == JsonValueKind.Number)
            {
                return sh.GetInt32();
            }

            return 0;
        }
        catch (Exception ex)
        {
            _log.Debug($"读取内容高度失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>窗口变高后别让它长出屏幕底部。</summary>
    /// <param name="height">当前高度（逻辑像素）。</param>
    private void KeepOnScreen(int height)
    {
        if (_posX is null || _posY is null)
        {
            return;
        }

        try
        {
            double scale = CurrentScale();
            int sw = (int)(NativeMethods.GetSystemMetrics(0) / scale);
            int sh = (int)(NativeMethods.GetSystemMetrics(1) / scale);

            int x = Math.Min(_posX.Value, sw - PanelGeometry.BaseWidth);
            int y = Math.Min(_posY.Value, sh - height);

            if (x != _posX.Value || y != _posY.Value)
            {
                _posX = x;
                _posY = y;
                MoveTo(x, y);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"把窗口拉回屏幕内失败: {ex.Message}");
        }
    }

    // ==================== 位置 ====================

    /// <summary>摆位：有保存位置就精确校准，没有就摆到主屏右下角。</summary>
    private void ApplyPosition()
    {
        if (_posX is null || _posY is null)
        {
            PlaceDefault();
            return;
        }

        double scale = CurrentScale();
        MoveTo(
            (int)Math.Round(_posX.Value * scale, MidpointRounding.ToEven),
            (int)Math.Round(_posY.Value * scale, MidpointRounding.ToEven));
    }

    /// <summary>没有保存过位置时，摆到主屏右下角。</summary>
    private void PlaceDefault()
    {
        double scale = CurrentScale();
        int sw = (int)(NativeMethods.GetSystemMetrics(0) / scale);
        int sh = (int)(NativeMethods.GetSystemMetrics(1) / scale);

        _posX = sw - PanelGeometry.BaseWidth - 30;
        _posY = sh - PanelGeometry.BaseHeight - 60;

        MoveTo(
            (int)Math.Round(_posX.Value * scale, MidpointRounding.ToEven),
            (int)Math.Round(_posY.Value * scale, MidpointRounding.ToEven));
    }

    /// <summary>把窗口左上角移到物理像素 (x, y)。</summary>
    private void MoveTo(int x, int y)
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
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            _log.Debug($"移动面板失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 保存窗口位置（逻辑像素）。
    ///
    /// <para>先用 <c>GetWindowRect</c> 读**真实位置**再存 —— 记账式的
    /// <c>_posX/_posY</c> 可能和真实窗口脱钩（取整、夹取），读为准。</para>
    /// </summary>
    private void SaveGeometry()
    {
        SyncPositionFromWindow();
        if (_posX is null || _posY is null)
        {
            return;
        }

        try
        {
            GeometrySaver(PanelGeometry.Format(_posX.Value, _posY.Value));
        }
        catch (Exception ex)
        {
            _log.Debug($"保存窗口位置失败: {ex.Message}");
        }
    }

    /// <summary>用窗口的**真实物理位置**回填逻辑坐标。</summary>
    private void SyncPositionFromWindow()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return;
        }

        double scale = CurrentScale();
        _posX = (int)Math.Round(rect.Left / scale, MidpointRounding.ToEven);
        _posY = (int)Math.Round(rect.Top / scale, MidpointRounding.ToEven);
    }

    /// <summary>窗口当前的**物理**像素尺寸。</summary>
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

    /// <summary>把窗口改成"工具窗口"，但**保留可激活性**。</summary>
    private void ApplyToolStyle()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int ex = NativeMethods.GetWindowExStyle(hwnd);
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;

        // ⚠ WS_EX_NOACTIVATE **不加** —— 面板要能拿焦点做交互，
        // 这一点和胶囊正好相反（胶囊抢了焦点，识别出来的文字就没有落点了）。
        ex &= ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowExStyle(hwnd, ex);
    }

    /// <summary>面板关闭时把位置记下来（用户可能拖完就直接关）。</summary>
    /// <inheritdoc />
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            SaveGeometry();
        }
        catch (Exception ex)
        {
            _log.Debug($"保存面板位置失败: {ex.Message}");
        }

        base.OnFormClosed(e);
    }
}
