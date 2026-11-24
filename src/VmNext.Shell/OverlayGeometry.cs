using System;

namespace VmNext.Shell;

/// <summary>
/// 覆盖层几何：逻辑像素 → 物理像素。
///
/// <para>关键点：<see cref="OverlayWindow"/> 用 Win32 的 SetWindowRgn 做"点击穿透"，
/// 而区域坐标是**物理像素**；Avalonia 侧的布局是**逻辑像素**。
/// 两者换算必须经过 RenderScaling，否则在 125%/150% 缩放下区域会与视觉内容错位。</para>
///
/// <para>这里**只有换算**：裁剪形状一律是直角矩形（外扩若干逻辑像素），
/// 球的正圆 / 胶囊的跑道形是**画出来的**视觉，不是裁出来的形状。</para>
/// </summary>
public static class OverlayGeometry
{
    /// <summary>逻辑像素 → 物理像素（四舍五入）。</summary>
    public static int ToPixels(double logical, double renderScaling)
        => (int)Math.Round(logical * renderScaling, MidpointRounding.AwayFromZero);
}
