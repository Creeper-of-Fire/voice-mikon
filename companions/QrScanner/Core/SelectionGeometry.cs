using System.Drawing;

namespace QrScanner.Core;

/// <summary>
/// 框选几何 —— **纯函数**（不碰窗口、不碰桌面）。
///
/// <para>照搬 Python 版 <c>selector.py</c> 的选区算法：
/// 拖拽起点 / 终点 → 一个矩形；反向拖拽（往左上拖）也要框出同一个矩形；
/// 拖得太小当误触（取消）。</para>
///
/// <para>之所以拆成纯函数：这段是全工具里**唯一有对错规范**的部分，
/// 可以离线断言；真机行为（遮罩观感、跟手）由用户实测。</para>
/// </summary>
public static class SelectionGeometry
{
    /// <summary>拖出的框宽 / 高小于这个像素当误触（照搬 Python 的 <c>_MIN_PICK = 10</c>）。</summary>
    public const int MinPickSize = 10;

    /// <summary>拖拽起点 / 终点 → 选区矩形（自动处理反向拖拽）。</summary>
    public static Rectangle Normalize(Point start, Point end)
        => new(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));

    /// <summary>拖得太小 → 当误触（取消）。</summary>
    public static bool IsTooSmall(Rectangle region)
        => region.Width < MinPickSize || region.Height < MinPickSize;

    /// <summary>
    /// 虚拟桌面坐标 → 截图坐标（截图的原点是虚拟桌面左上角）。
    /// </summary>
    /// <para>副屏挂在主屏左边 / 上边时，虚拟桌面原点是负的 —— 这一步不能省，
    /// 否则跨屏框选会整体偏移。</para>
    public static Point ToImage(Point virtualDesktopPoint, Rectangle virtualScreen)
        => new(
            virtualDesktopPoint.X - virtualScreen.Left,
            virtualDesktopPoint.Y - virtualScreen.Top);
}
