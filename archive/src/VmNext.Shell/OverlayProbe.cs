using System;
using System.Globalization;
using System.Threading;

namespace VmNext.Shell;

/// <summary>一项自动化检查的结果。</summary>
/// <param name="Id">编号（A1…A13）。</param>
/// <param name="Name">检查项名称。</param>
/// <param name="Passed">是否通过。</param>
/// <param name="Detail">实测值/详情。</param>
public sealed record ProbeResult(string Id, string Name, bool Passed, string Detail);

/// <summary>
/// 覆盖层的自动化自检（A1–A6 + 透明度合成验证）。
///
/// 硬约束：**绝不注入输入**。没有 SendInput / SetCursorPos / keybd_event，
/// 鼠标相关的验证一律用 PostMessage + 纯查询类 Win32 API 完成，
/// 免得把用户正在用的鼠标/键盘抢走。
/// </summary>
public sealed class OverlayProbe
{
    private readonly OverlayWindow _window;
    private int[]? _baseline;
    private int _captureX;
    private int _captureY;
    private int _captureW;
    private int _captureH;

    /// <summary>创建探针。</summary>
    public OverlayProbe(OverlayWindow window) => _window = window;

    /// <summary>A1：窗口扩展样式必须含 NOACTIVATE / LAYERED / TOOLWINDOW / TOPMOST。</summary>
    public ProbeResult CheckExStyle()
    {
        IntPtr hwnd = _window.Handle;
        int style = NativeMethods.GetWindowExStyle(hwnd);

        bool noActivate = (style & NativeMethods.WS_EX_NOACTIVATE) != 0;
        bool layered = (style & NativeMethods.WS_EX_LAYERED) != 0;
        bool toolWindow = (style & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        bool topmost = (style & NativeMethods.WS_EX_TOPMOST) != 0;

        bool passed = noActivate && layered && toolWindow && topmost;
        string detail = string.Format(
            CultureInfo.InvariantCulture,
            "EXSTYLE=0x{0:X8} NOACTIVATE={1} LAYERED={2} TOOLWINDOW={3} TOPMOST={4}",
            style, noActivate, layered, toolWindow, topmost);

        return new ProbeResult("A1", "窗口扩展样式正确", passed, detail);
    }

    /// <summary>A2：窗口区域已裁剪成球/胶囊形状（不是整块矩形）。</summary>
    public ProbeResult CheckRegion()
    {
        IntPtr hwnd = _window.Handle;
        IntPtr scratch = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (scratch == IntPtr.Zero)
        {
            return new ProbeResult("A2", "窗口区域已裁", false, "CreateRectRgn 失败，无法查询窗口区域");
        }

        try
        {
            int type = NativeMethods.GetWindowRgn(hwnd, scratch);
            NativeMethods.GetWindowRgnBox(hwnd, out var box);
            NativeMethods.GetClientRect(hwnd, out var client);

            bool hasRegion = type != NativeMethods.RGNNULL_ERROR && type != NativeMethods.RGNNULL_NULLREGION;
            bool matchesGeometry = Math.Abs(box.Width - client.Width) <= 1 && Math.Abs(box.Height - client.Height) <= 1;

            string detail = string.Format(
                CultureInfo.InvariantCulture,
                "GetWindowRgn={0}(1=NULLREGION,2=SIMPLE,3=COMPLEX) RgnBox=({1},{2},{3}x{4}) Client={5}x{6} 期望圆直径≈{6}",
                type, box.Left, box.Top, box.Width, box.Height, client.Width, client.Height);

            return new ProbeResult("A2", "窗口区域已裁", hasRegion && matchesGeometry, detail);
        }
        finally
        {
            NativeMethods.DeleteObject(scratch);
        }
    }

    /// <summary>A3：点击穿透 —— 球外 2px 的点命中别的窗口，球心命中我们。</summary>
    public ProbeResult CheckClickThrough()
    {
        IntPtr hwnd = _window.Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return new ProbeResult("A3", "点击穿透", false, "GetWindowRect 失败");
        }

        // 球包围盒左上角外 2px
        var outsideCorner = new NativeMethods.POINT { X = rect.Left - 2, Y = rect.Top - 2 };
        // 球包围盒左边中点外 2px（第二个采样点，避开屏幕边缘等特殊情况）
        var outsideLeft = new NativeMethods.POINT { X = rect.Left - 2, Y = rect.Top + (rect.Height / 2) };
        var center = new NativeMethods.POINT { X = rect.Left + (rect.Width / 2), Y = rect.Top + (rect.Height / 2) };

        IntPtr hitCorner = NativeMethods.WindowFromPoint(outsideCorner);
        IntPtr hitLeft = NativeMethods.WindowFromPoint(outsideLeft);
        IntPtr hitCenter = NativeMethods.WindowFromPoint(center);

        bool outsideOk = hitCorner != hwnd && hitLeft != hwnd;
        bool centerOk = hitCenter == hwnd;

        string detail = string.Format(
            CultureInfo.InvariantCulture,
            "窗口矩形=({0},{1},{2}x{3}) 角外2px命中=0x{4:X} 左中2px命中=0x{5:X} 球心命中=0x{6:X} 本窗口=0x{7:X}",
            rect.Left, rect.Top, rect.Width, rect.Height,
            hitCorner.ToInt64(), hitLeft.ToInt64(), hitCenter.ToInt64(), hwnd.ToInt64());

        return new ProbeResult("A3", "点击穿透", outsideOk && centerOk, detail);
    }

    /// <summary>A4：点击不激活 —— 合成鼠标消息前后前台窗口不变。</summary>
    public ProbeResult CheckNoActivate()
    {
        IntPtr hwnd = _window.Handle;
        if (!NativeMethods.GetClientRect(hwnd, out var client))
        {
            return new ProbeResult("A4", "点击不激活", false, "GetClientRect 失败");
        }

        IntPtr before = NativeMethods.GetForegroundWindow();

        // 询问窗口自己的"鼠标激活策略"：返回 MA_NOACTIVATE(3) 表示点击不激活
        IntPtr activatePolicy = NativeMethods.SendMessage(
            hwnd,
            NativeMethods.WM_MOUSEACTIVATE,
            IntPtr.Zero,
            new IntPtr(unchecked((int)0xFFFF0001))); // MAKELPARAM(HTCAPTION(2), WM_LBUTTONDOWN(0x201))

        int x = client.Width / 2;
        int y = client.Height / 2;
        IntPtr lParam = new IntPtr((y << 16) | (x & 0xFFFF));

        NativeMethods.PostMessage(hwnd, NativeMethods.WM_LBUTTONDOWN, IntPtr.Zero, lParam);
        NativeMethods.PostMessage(hwnd, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, lParam);

        Thread.Sleep(250);
        IntPtr after = NativeMethods.GetForegroundWindow();

        bool passed = before == after;
        string detail = string.Format(
            CultureInfo.InvariantCulture,
            "前台窗口 前=0x{0:X} 后=0x{1:X} 变化={2}；WM_MOUSEACTIVATE 返回={3}(3=MA_NOACTIVATE)",
            before.ToInt64(), after.ToInt64(), before == after ? "无" : "有",
            activatePolicy.ToInt64());

        return new ProbeResult("A4", "点击不激活", passed, detail);
    }

    /// <summary>A5：拖拽几何 —— 移动 +100 逻辑像素，实际位移 = 100 × 当前缩放（±1px）。</summary>
    public ProbeResult CheckMove()
    {
        IntPtr hwnd = _window.Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var before))
        {
            return new ProbeResult("A5", "拖拽几何", false, "GetWindowRect 失败");
        }

        double scale = _window.CurrentRenderScaling;
        _window.MoveByLogicalPixels(100, 0);
        Thread.Sleep(150);
        NativeMethods.GetWindowRect(hwnd, out var after);
        _window.MoveByLogicalPixels(-100, 0);
        Thread.Sleep(150);

        double expected = 100 * scale;
        int actual = after.Left - before.Left;
        bool passed = Math.Abs(actual - expected) <= 1;

        string detail = string.Format(
            CultureInfo.InvariantCulture,
            "渲染缩放={0:F4} 期望位移={1:F2}px 实测位移={2}px 差={3:F2}px",
            scale, expected, actual, Math.Abs(actual - expected));

        return new ProbeResult("A5", "拖拽几何", passed, detail);
    }

    /// <summary>A6：DPI 读数 —— GetDpiForWindow 与 Avalonia RenderScaling 一致。</summary>
    public ProbeResult CheckDpi()
    {
        IntPtr hwnd = _window.Handle;
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        double winScale = dpi / 96.0;
        double avaloniaScale = _window.CurrentRenderScaling;
        bool passed = dpi != 0 && Math.Abs(winScale - avaloniaScale) < 0.01;

        string detail = string.Format(
            CultureInfo.InvariantCulture,
            "GetDpiForWindow={0} → 缩放={1:F4}；Avalonia RenderScaling={2:F4}",
            dpi, winScale, avaloniaScale);

        return new ProbeResult("A6", "DPI 读数一致", passed, detail);
    }

    /// <summary>
    /// 窗口显示**之前**抓一块屏幕区域作为基线（用来证明透明区域真的透出了桌面）。
    /// 抓的区域要盖住窗口最终可能出现的位置。
    /// </summary>
    public void CaptureBaseline(int x, int y, int width, int height)
    {
        _captureX = x;
        _captureY = y;
        _captureW = width;
        _captureH = height;
        _baseline = ScreenCapture.Capture(x, y, width, height);
    }

    /// <summary>
    /// 额外验证（X1）：窗口显示之后再抓同一块区域，比较像素：
    /// · 球心像素应该变了（内容确实画上去了）
    /// · 球的四角（透明处）像素应该**没变**（说明真透明，不是灰底）
    /// </summary>
    public ProbeResult VerifyTransparency()
    {
        if (_baseline is null)
        {
            return new ProbeResult("X1", "真透明（屏幕合成验证）", false, "缺少显示前的基线截图");
        }

        IntPtr hwnd = _window.Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return new ProbeResult("X1", "真透明（屏幕合成验证）", false, "GetWindowRect 失败");
        }

        // 窗口必须完整落在基线区域里，否则没法逐像素对比
        if (rect.Left < _captureX || rect.Top < _captureY ||
            rect.Right > _captureX + _captureW || rect.Bottom > _captureY + _captureH)
        {
            return new ProbeResult(
                "X1",
                "真透明（屏幕合成验证）",
                false,
                $"窗口矩形({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) 超出基线区域({_captureX},{_captureY},{_captureX + _captureW},{_captureY + _captureH})");
        }

        int[]? after = ScreenCapture.Capture(_captureX, _captureY, _captureW, _captureH);
        if (after is null)
        {
            return new ProbeResult("X1", "真透明（屏幕合成验证）", false, "显示后截图失败");
        }

        // 把窗口坐标换算成基线图里的坐标
        int centerX = rect.Left - _captureX + (rect.Width / 2);
        int centerY = rect.Top - _captureY + (rect.Height / 2);

        int centerBefore = ScreenCapture.PixelAt(_baseline, _captureW, centerX, centerY);
        int centerAfter = ScreenCapture.PixelAt(after, _captureW, centerX, centerY);
        int centerDiff = ScreenCapture.ColorDistance(centerBefore, centerAfter);

        // 四个角：窗口矩形角内侧 2px，落在球外（真透明处）
        (int X, int Y)[] cornerOffsets =
        {
            (rect.Left - _captureX + 2, rect.Top - _captureY + 2),
            (rect.Right - _captureX - 3, rect.Top - _captureY + 2),
            (rect.Left - _captureX + 2, rect.Bottom - _captureY - 3),
            (rect.Right - _captureX - 3, rect.Bottom - _captureY - 3),
        };
        int maxCornerDiff = 0;
        foreach (var (cx, cy) in cornerOffsets)
        {
            int b = ScreenCapture.PixelAt(_baseline, _captureW, cx, cy);
            int a = ScreenCapture.PixelAt(after, _captureW, cx, cy);
            int d = ScreenCapture.ColorDistance(b, a);
            if (d > maxCornerDiff)
            {
                maxCornerDiff = d;
            }
        }

        bool contentDrawn = centerDiff > 20;
        bool cornersTransparent = maxCornerDiff <= 12;
        bool passed = contentDrawn && cornersTransparent;

        string detail = string.Format(
            CultureInfo.InvariantCulture,
            "球心像素 前=0x{0:X6} 后=0x{1:X6} 差={2}（>20 说明内容画上去了）；四角最大差={3}（<=12 说明透明处透出桌面）",
            centerBefore, centerAfter, centerDiff, maxCornerDiff);

        return new ProbeResult("X1", "真透明（屏幕合成验证）", passed, detail);
    }
}
