using System;
using VoiceMikonCSharp.Core.Interop;
using VoiceMikonCSharp.Shell;

// 隐式 using 里有 System.Threading，Monitor 与 Core.Interop.Monitor 同名 —— 点名要哪个。
using Monitor = VoiceMikonCSharp.Core.Interop.Monitor;

namespace VoiceMikonCSharp.WebView;

/// <summary>
/// 拖动会话：把"按住 → 拖 → 松手"这一段收成自足单元。
///
/// <para>翻译自 voice-mikon/src/voice_mikon/host/drag.py，流程逐条对应：
/// 前端持续发**绝对目标位置**（不是增量）→ 换算成物理像素 → 夹回允许的范围
/// → 挪窗口；松手时归还焦点，并且**拖过才**记位置（点一下没动就别写配置）。</para>
///
/// <para>摆位规则**可注入**：球有球的那套规则（球在窗口底边居中、纵向要给
/// 展开后的胶囊留位），普通窗口有普通的那套（整个窗口夹进工作区）。
/// 两种窗口形态不该共用一套夹取规则。</para>
/// </summary>
public sealed class DragSession
{
    private readonly Func<IntPtr> _windowHandle;
    private readonly Func<double> _windowScale;
    private readonly Func<(int Width, int Height)?> _windowSize;
    private readonly Action _saveGeometry;
    private readonly Action _restoreFocus;
    private readonly Func<double, double, (double X, double Y)?> _placement;
    private readonly object _lock = new();

    private bool _dragging;
    private (int X, int Y)? _dragStart;

    /// <summary>造一个拖动会话。</summary>
    /// <param name="windowHandle">底层窗口句柄（0 = 还没建好）。</param>
    /// <param name="windowScale">窗口所在显示器的缩放系数。</param>
    /// <param name="windowSize">窗口当前的物理像素尺寸。</param>
    /// <param name="saveGeometry">记住位置。</param>
    /// <param name="restoreFocus">把前台还给用户当前所在的窗口。</param>
    /// <param name="placement">摆位策略：窗口该摆哪由它说了算。</param>
    public DragSession(
        Func<IntPtr> windowHandle,
        Func<double> windowScale,
        Func<(int Width, int Height)?> windowSize,
        Action saveGeometry,
        Action restoreFocus,
        Func<double, double, (double X, double Y)?> placement)
    {
        _windowHandle = windowHandle;
        _windowScale = windowScale;
        _windowSize = windowSize;
        _saveGeometry = saveGeometry;
        _restoreFocus = restoreFocus;
        _placement = placement;
    }

    /// <summary>此刻是不是正在拖（焦点守卫靠它决定要不要抢回焦点）。</summary>
    public bool IsDragging
    {
        get
        {
            lock (_lock)
            {
                return _dragging;
            }
        }
    }

    /// <summary>用户开始拖（按下不放）。</summary>
    public void Begin()
    {
        lock (_lock)
        {
            _dragging = true;
        }

        _dragStart = WindowOrigin();
    }

    /// <summary>
    /// 拖动中：窗口左上角 → (x, y) 逻辑像素。
    ///
    /// <para>摆位策略交回的是**逻辑**像素，DPI 换算和 <c>SetWindowPos</c> 在这里统一做。</para>
    /// </summary>
    /// <param name="x">目标 x（逻辑像素，绝对位置）。</param>
    /// <param name="y">目标 y（逻辑像素，绝对位置）。</param>
    public void Move(double x, double y)
    {
        IntPtr hwnd = _windowHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            double scale = _windowScale();
            (double X, double Y)? placed = _placement(x, y);
            if (placed is null)
            {
                // 策略说这次别挪（连工作区都问不到）：宁可不动，
                // 也不能把窗口甩到屏幕外面去。
                return;
            }

            MoveWindowTo(
                hwnd,
                (int)Math.Round(placed.Value.X * scale, MidpointRounding.ToEven),
                (int)Math.Round(placed.Value.Y * scale, MidpointRounding.ToEven));
        }
        catch (Exception)
        {
            // 挪不动就算了 —— 拖拽不该把调用方带崩。
        }
    }

    /// <summary>用户松手：归还焦点 + 记住位置。</summary>
    public void End()
    {
        (int X, int Y)? start;
        lock (_lock)
        {
            _dragging = false;
            start = _dragStart;
            _dragStart = null;
        }

        _restoreFocus();

        if (Moved(start))
        {
            _saveGeometry();
        }
    }

    /// <summary>这一下拖动有没有真的挪过位置。</summary>
    /// <param name="start">按下时的窗口左上角。</param>
    private bool Moved((int X, int Y)? start)
    {
        if (start is null)
        {
            return true;
        }

        (int X, int Y)? now = WindowOrigin();
        if (now is null)
        {
            return false;
        }

        return now.Value.X != start.Value.X || now.Value.Y != start.Value.Y;
    }

    /// <summary>窗口当前的左上角（物理像素）。</summary>
    private (int X, int Y)? WindowOrigin()
    {
        IntPtr hwnd = _windowHandle();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return null;
        }

        return (rect.Left, rect.Top);
    }

    /// <summary>把窗口左上角移到物理像素 (x, y)（不改大小、不抢焦点、不动 Z 序）。</summary>
    private static void MoveWindowTo(IntPtr hwnd, int x, int y)
        => NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    // ==================== 摆位策略 ====================

    /// <summary>
    /// 球的摆位策略：球在窗口底边居中，纵向要给展开后的胶囊留位。
    ///
    /// <para>逐条对应 Python 的 <c>drag.orb_placement</c>。</para>
    /// </summary>
    /// <param name="windowScale">窗口所在显示器的缩放系数。</param>
    /// <param name="windowSize">窗口当前的物理像素尺寸。</param>
    /// <returns>夹取后的窗口左上角（逻辑像素）；夹不了返回 null。</returns>
    public static Func<double, double, (double X, double Y)?> OrbPlacement(
        Func<double> windowScale,
        Func<(int Width, int Height)?> windowSize)
        => (x, y) =>
        {
            double scale = windowScale();
            (int Width, int Height)? size = windowSize();
            if (size is null)
            {
                return null;
            }

            int winW = size.Value.Width;
            int winH = size.Value.Height;
            int winX = (int)Math.Round(x * scale, MidpointRounding.ToEven);
            int winY = (int)Math.Round(y * scale, MidpointRounding.ToEven);

            (int ox, int oy) = Placement.OrbScreenPosition(winX, winY, winW, winH, scale);

            NativeMethods.RECT area = WorkAreaAt(ox, oy);
            (int nx, int ny) = Placement.OrbWindowPosition(ox, oy, winW, winH, scale, area);

            return (nx / scale, ny / scale);
        };

    /// <summary>
    /// 普通窗口的摆位策略：整个窗口夹进工作区。
    ///
    /// <para>逐条对应 Python 的 <c>drag.window_placement</c>：夹取按**目标点所在的
    /// 显示器**算（按窗口所在屏夹的话，拖到边界就会被原屏边缘挡住，跨屏拖不过去）。</para>
    /// </summary>
    /// <param name="windowScale">窗口所在显示器的缩放系数。</param>
    /// <param name="windowSize">窗口当前的物理像素尺寸。</param>
    /// <param name="margin">窗口离工作区边缘的最小留白（物理像素）。</param>
    /// <returns>夹取后的窗口左上角（逻辑像素）；夹不了返回 null。</returns>
    public static Func<double, double, (double X, double Y)?> WindowPlacement(
        Func<double> windowScale,
        Func<(int Width, int Height)?> windowSize,
        int margin = Placement.EdgeMargin)
        => (x, y) =>
        {
            double scale = windowScale();
            (int Width, int Height)? size = windowSize();
            if (size is null)
            {
                return null;
            }

            int winW = size.Value.Width;
            int winH = size.Value.Height;
            int px = (int)Math.Round(x * scale, MidpointRounding.ToEven);
            int py = (int)Math.Round(y * scale, MidpointRounding.ToEven);

            NativeMethods.RECT area = WorkAreaAt(px, py);
            (int nx, int ny) = Placement.ClampToWorkArea(px, py, winW, winH, area, margin);

            return (nx / scale, ny / scale);
        };

    /// <summary>物理像素点所在显示器的工作区（问不到就退回主屏）。</summary>
    private static NativeMethods.RECT WorkAreaAt(int x, int y)
    {
        Monitor? mon = Monitors.MonitorAtPoint(x, y);
        if (mon is null)
        {
            mon = Monitors.PrimaryMonitor();
        }

        return mon?.WorkArea ?? new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }
}
