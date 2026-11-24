using System;

namespace VoiceMikonCSharp.Shell;

/// <summary>
/// 一块命中区（客户区坐标，**物理像素**）。
///
/// <para>覆盖层窗口用 Win32 的 <c>SetWindowRgn</c> 做"点击穿透"：
/// 只有落在区域里的像素才算这个窗口的，区域之外的点击直接落到下层应用。
/// 这份区域由 UI 层按"真正可见的那几块"算出来（见
/// <see cref="OverlayWindow.BuildHitRegions"/>）。</para>
///
/// <para><b>形状一律是直角矩形</b>：球 / 胶囊 / 气泡这些"跑道形"的视觉是
/// **画出来的**，不是裁出来的 —— 裁剪只负责"这块归不归我管"。
/// （对照真机 voice-mikon 的 <c>host/capsule.py</c>：<c>ellipses</c> 集合恒为空，
/// 胶囊与球同样按矩形裁。）</para>
///
/// <para>抗锯齿边缘由 <see cref="OverlayWindow"/> 统一外扩
/// <c>RegionMarginLogicalPx</c>（3 逻辑像素）解决，不必在这里带圆角信息。</para>
/// </summary>
/// <param name="Left">左边界（物理像素）。</param>
/// <param name="Top">上边界（物理像素）。</param>
/// <param name="Right">右边界（物理像素，排他）。</param>
/// <param name="Bottom">下边界（物理像素，排他）。</param>
public readonly record struct HitRegion(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    /// <summary>宽度（物理像素）。</summary>
    public int Width => Right - Left;

    /// <summary>高度（物理像素）。</summary>
    public int Height => Bottom - Top;

    /// <summary>区域是否为空（空区域没有意义，裁剪时直接跳过）。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
