using System;
using System.Collections.Generic;
using Avalonia;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.Ui;

/// <summary>
/// 一块参与命中区的可见元素（**客户区逻辑像素**）。
///
/// <para>⚠ 这里只有矩形，没有圆角：裁剪一律用直角矩形 + 每边外扩 3 逻辑像素。
/// 球 / 胶囊 / 气泡的"跑道形"是**画出来的**视觉，不是裁出来的形状
/// （对照真机 voice-mikon 的 <c>host/capsule.py</c>：<c>ellipses</c> 集合恒为空）。</para>
/// </summary>
/// <param name="Bounds">
/// 元素在客户区里的矩形（逻辑像素）。
/// ⚠ 用的是 Avalonia 的 <c>Rect</c>，语义是 <c>(x, y, width, height)</c> ——
/// 右/下边界由 <c>Rect.Right</c> / <c>Rect.Bottom</c> 算，不要自己拼。
/// </param>
public readonly record struct HitRegionSpec(Rect Bounds);

/// <summary>
/// 命中区计算（**纯函数**）：可见元素 → 命中区矩形列表。
///
/// <para>翻译自真机的 <c>capsule.js</c> 的 <c>measureRegions</c> + Python 侧的
/// <c>SetWindowRgn</c> 合并。真机是"前端量矩形 → 上报 → Python 裁"三步，
/// Avalonia 下没有"上报"这一步：控件树的 <c>Bounds</c> 直接可读，
/// 这里把"量"这一步抽成纯函数，"裁"由 <see cref="OverlayWindow"/> 做。</para>
///
/// <para>⚠ 真机的规矩：**浮层（菜单 / 气泡）要单独一块，不能并进球的包围盒** ——
/// 并进去中间全是空白，空白在裁形里会露出客户区的底色。
/// 这里的做法与之一致：每个可见元素各报一块，Shell 侧再 <c>CombineRgn</c> 取并集。</para>
/// </summary>
public static class HitRegionBuilder
{
    /// <summary>
    /// 把可见元素列表算成命中区列表（**物理像素**，客户区坐标）。
    ///
    /// <para>空矩形（不可见 / 面积为 0）直接跳过 —— 它们不参与裁剪。</para>
    /// </summary>
    /// <param name="specs">可见元素（逻辑像素）。</param>
    /// <param name="renderScaling">渲染缩放（DPI / 96）。</param>
    public static IReadOnlyList<HitRegion> Build(IReadOnlyList<HitRegionSpec> specs, double renderScaling)
    {
        ArgumentNullException.ThrowIfNull(specs);

        double scale = renderScaling > 0 ? renderScaling : 1.0;
        var result = new List<HitRegion>(specs.Count);

        foreach (HitRegionSpec spec in specs)
        {
            Rect bounds = spec.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            int left = OverlayGeometry.ToPixels(bounds.Left, scale);
            int top = OverlayGeometry.ToPixels(bounds.Top, scale);
            int right = OverlayGeometry.ToPixels(bounds.Right, scale);
            int bottom = OverlayGeometry.ToPixels(bounds.Bottom, scale);

            if (right <= left || bottom <= top)
            {
                continue;
            }

            result.Add(new HitRegion(left, top, right, bottom));
        }

        return result;
    }
}
