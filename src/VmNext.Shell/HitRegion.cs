using System;

namespace VmNext.Shell;

/// <summary>
/// 一块命中区（客户区坐标，**物理像素**）。
///
/// <para>覆盖层窗口用 Win32 的 <c>SetWindowRgn</c> 做"点击穿透"：
/// 只有落在区域里的像素才算这个窗口的，区域之外的点击直接落到下层应用。
/// 这份区域由 UI 层按"真正可见的那几块"算出来（见
/// <see cref="OverlayWindow.BuildHitRegions"/>）。</para>
/// </summary>
/// <param name="Left">左边界（物理像素）。</param>
/// <param name="Top">上边界（物理像素）。</param>
/// <param name="Right">右边界（物理像素，排他）。</param>
/// <param name="Bottom">下边界（物理像素，排他）。</param>
/// <param name="CornerRadius">圆角半径（物理像素）；0 = 直角矩形。</param>
public readonly record struct HitRegion(
    int Left,
    int Top,
    int Right,
    int Bottom,
    int CornerRadius = 0)
{
    /// <summary>宽度（物理像素）。</summary>
    public int Width => Right - Left;

    /// <summary>高度（物理像素）。</summary>
    public int Height => Bottom - Top;

    /// <summary>区域是否为空（空区域没有意义，裁剪时直接跳过）。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>把物理像素的圆角半径夹到"短边的一半"以内（Win32 的圆角矩形不允许超过一半）。</summary>
    public int ClampedRadius
    {
        get
        {
            if (CornerRadius <= 0)
            {
                return 0;
            }

            int limit = Math.Min(Width, Height) / 2;
            return Math.Min(CornerRadius, limit);
        }
    }
}
