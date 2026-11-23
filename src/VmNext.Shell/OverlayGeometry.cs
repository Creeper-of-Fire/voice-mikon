using System;

namespace VmNext.Shell;

/// <summary>覆盖层的形状。</summary>
public enum OverlayShape
{
    /// <summary>正圆（球）。窗口是正方形，区域是内切圆。</summary>
    Circle,

    /// <summary>胶囊（圆角矩形）。区域是"高为直径、左右完全圆角"的圆角矩形。</summary>
    Capsule,
}

/// <summary>
/// 覆盖层几何：逻辑尺寸 → 物理像素尺寸 / 区域圆角半径。
///
/// 关键点：<see cref="OverlayWindow"/> 用 Win32 的 SetWindowRgn 做"点击穿透"，
/// 而区域坐标是**物理像素**；Avalonia 侧的布局是**逻辑像素**。
/// 两者换算必须经过 RenderScaling，否则在 125%/150% 缩放下区域会与视觉内容错位。
/// </summary>
public static class OverlayGeometry
{
    /// <summary>逻辑像素 → 物理像素（四舍五入）。</summary>
    public static int ToPixels(double logical, double renderScaling)
        => (int)Math.Round(logical * renderScaling, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 计算圆角区域的圆角椭圆尺寸（CreateRoundRectRgn 的 widthEllipse/heightEllipse）。
    /// </summary>
    /// <param name="shape">形状。</param>
    /// <param name="clientWidthPx">客户区宽（物理像素）。</param>
    /// <param name="clientHeightPx">客户区高（物理像素）。</param>
    public static (int EllipseWidth, int EllipseHeight) CornerEllipse(OverlayShape shape, int clientWidthPx, int clientHeightPx)
    {
        int h = Math.Max(0, clientHeightPx);
        int w = Math.Max(0, clientWidthPx);
        return shape switch
        {
            // 正圆：椭圆宽高等于正方形边长 → 内切圆
            OverlayShape.Circle => (Math.Min(w, h), Math.Min(w, h)),
            // 胶囊：圆角半径 = 高/2 → 椭圆宽高都等于高
            OverlayShape.Capsule => (h, h),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    /// <summary>形状对应的视觉圆角半径（逻辑像素），用于 Border.CornerRadius 与区域保持一致。</summary>
    public static double CornerRadius(OverlayShape shape, double logicalHeight)
        => shape switch
        {
            OverlayShape.Circle => logicalHeight / 2.0,
            OverlayShape.Capsule => logicalHeight / 2.0,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
}
