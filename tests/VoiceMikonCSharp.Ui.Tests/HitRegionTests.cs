using System.Collections.Generic;
using Avalonia;
using VoiceMikonCSharp.Shell;
using VoiceMikonCSharp.Ui;
using Xunit;

namespace VoiceMikonCSharp.Ui.Tests;

/// <summary>
/// 命中区换算的单元测试（**纯函数**）。
///
/// <para>对照物：voice-mikon/src/voice_mikon/host/capsule.py 的 <c>update_regions</c>：
/// <list type="bullet">
/// <item>逻辑像素 → 物理像素：<c>expand()</c> 里是 <c>int(round(x * scale))</c>
/// （四舍五入远离零）。</item>
/// <item>隐藏的块不参与并集：<c>if w &lt;= 0 or h &lt;= 0: continue</c>。</item>
/// <item>形状一律是**直角矩形**：真机那边 <c>ellipses</c> 集合恒为空 ——
/// 胶囊与球同样按矩形裁，"跑道形"是画出来的视觉，不是裁出来的形状。
/// 每边外扩 3 逻辑像素由 Shell 的 <c>OverlayWindow</c> 负责。</item>
/// </list></para>
///
/// <para>窗口那一半（各状态下窗口真算出来的区域集合）不测 ——
/// 那测的是 Avalonia 控件树的内部状态，不是规范。</para>
/// </summary>
public class HitRegionTests
{
    /// <summary>逻辑像素 → 物理像素（DPI 换算）。</summary>
    [Fact]
    public void 纯函数_按缩放换算成物理像素()
    {
        // Avalonia 的 Rect 语义是 (x, y, width, height)。
        IReadOnlyList<HitRegion> regions = HitRegionBuilder.Build(
            new[] { new HitRegionSpec(new Rect(10, 20, 50, 60)) },
            2.0);

        HitRegion region = Assert.Single(regions);
        Assert.Equal(20, region.Left);
        Assert.Equal(40, region.Top);
        Assert.Equal(120, region.Right);
        Assert.Equal(160, region.Bottom);
    }

    /// <summary>空矩形（不可见 / 面积为 0）不参与裁剪。</summary>
    [Fact]
    public void 纯函数_空矩形被跳过()
    {
        IReadOnlyList<HitRegion> regions = HitRegionBuilder.Build(
            new[]
            {
                new HitRegionSpec(new Rect(0, 0, 0, 0)),
                new HitRegionSpec(new Rect(10, 10, 0, 40)),
                new HitRegionSpec(new Rect(0, 0, 40, 40)),
            },
            1.0);

        HitRegion region = Assert.Single(regions);
        Assert.Equal(0, region.Left);
        Assert.Equal(40, region.Right);
    }

    /// <summary>每个可见元素各报一块（浮层不能并进球的包围盒）。</summary>
    [Fact]
    public void 纯函数_浮层单独一块()
    {
        IReadOnlyList<HitRegion> regions = HitRegionBuilder.Build(
            new[]
            {
                new HitRegionSpec(new Rect(236, 140, 40, 40)),   // 球
                new HitRegionSpec(new Rect(16, 40, 480, 90)),    // 文字条
            },
            1.0);

        Assert.Equal(2, regions.Count);
        Assert.Equal(40, regions[0].Width);
        Assert.Equal(40, regions[0].Height);
        Assert.Equal(480, regions[1].Width);
        Assert.Equal(90, regions[1].Height);
    }

    /// <summary>形状不带圆角信息：裁剪一律直角矩形。</summary>
    [Fact]
    public void 纯函数_命中区不带圆角()
    {
        IReadOnlyList<HitRegion> regions = HitRegionBuilder.Build(
            new[] { new HitRegionSpec(new Rect(0, 0, 100, 40)) },
            1.75);

        HitRegion region = Assert.Single(regions);

        // 宽度只由矩形本身决定（1.75 倍缩放）。
        Assert.Equal(175, region.Width);
        Assert.Equal(70, region.Height);
    }
}
