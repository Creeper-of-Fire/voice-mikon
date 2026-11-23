using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless.XUnit;
using VmNext.Core.Presentation;
using VmNext.Shell;
using VmNext.Ui;
using Xunit;

namespace VmNext.Ui.Tests;

/// <summary>
/// 命中区计算的单元测试。
///
/// <para>两半：<b>纯函数</b>（可见元素 → 矩形列表，可控输入）与
/// <b>无头窗口</b>（各状态下窗口真算出来的区域集合）。</para>
/// </summary>
public class HitRegionTests
{
    // ==================== 纯函数 ====================

    /// <summary>逻辑像素 → 物理像素（DPI 换算）。</summary>
    [Fact]
    public void 纯函数_按缩放换算成物理像素()
    {
        // Avalonia 的 Rect 语义是 (x, y, width, height)。
        IReadOnlyList<HitRegion> regions = HitRegionBuilder.Build(
            new[] { new HitRegionSpec(new Rect(10, 20, 50, 60), 0) },
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
                new HitRegionSpec(new Rect(0, 0, 0, 0), 0),
                new HitRegionSpec(new Rect(10, 10, 0, 40), 0),
                new HitRegionSpec(new Rect(0, 0, 40, 40), 0),
            },
            1.0);

        HitRegion region = Assert.Single(regions);
        Assert.Equal(0, region.Left);
        Assert.Equal(40, region.Right);
    }

    /// <summary>圆角按缩放换算（文字条 14px、浮层 12px）。</summary>
    [Fact]
    public void 纯函数_圆角按缩放换算()
    {
        IReadOnlyList<HitRegion> regions = HitRegionBuilder.Build(
            new[]
            {
                new HitRegionSpec(new Rect(0, 0, 100, 40), 14),
                new HitRegionSpec(new Rect(0, 50, 60, 90), 12),
            },
            1.75);

        Assert.Equal(2, regions.Count);
        Assert.Equal(25, regions[0].CornerRadius);   // round(14 * 1.75) = 25
        Assert.Equal(21, regions[1].CornerRadius);   // round(12 * 1.75) = 21
    }

    /// <summary>999px 的胶囊圆角会被夹到短边的一半（Win32 的圆角矩形不允许超过一半）。</summary>
    [Fact]
    public void 纯函数_超大圆角夹到短边一半()
    {
        IReadOnlyList<HitRegion> regions = HitRegionBuilder.Build(
            new[] { new HitRegionSpec(new Rect(0, 0, 40, 40), 999) },
            1.0);

        HitRegion region = Assert.Single(regions);
        Assert.Equal(20, region.ClampedRadius);
    }

    // ==================== 无头窗口 ====================

    /// <summary>
    /// 球形态：只有球那一块参与命中（文字条不占布局）。
    /// </summary>
    [AvaloniaFact]
    public async Task 球形态_只有球参与命中()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            IReadOnlyList<HitRegion> regions = window.ComputeHitRegions();
            HitRegion region = Assert.Single(regions);

            // 球在窗口里底边居中：宽 40、高 40。
            Assert.Equal(40 * window.CurrentRenderScaling, region.Width, 0);
            Assert.Equal(40 * window.CurrentRenderScaling, region.Height, 0);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>录音态：胶囊 + 文字条两块（真机的规矩：文字条要单独一块）。</summary>
    [AvaloniaFact]
    public async Task 录音态_胶囊与文字条两块()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            IReadOnlyList<HitRegion> regions = window.ComputeHitRegions();
            Assert.Equal(2, regions.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>球的右键菜单打开：多一块菜单（菜单要能点得到）。</summary>
    [AvaloniaFact]
    public async Task 菜单打开_多一块菜单()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            window.ToggleOrbMenu();
            await CapsuleWindowTests.SettleAsync();

            IReadOnlyList<HitRegion> regions = window.ComputeHitRegions();
            Assert.Equal(2, regions.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>气泡：多一块气泡。</summary>
    [AvaloniaFact]
    public async Task 气泡打开_多一块气泡()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            window.ShowOrbHint("模型加载中…");
            await CapsuleWindowTests.SettleAsync();

            IReadOnlyList<HitRegion> regions = window.ComputeHitRegions();
            Assert.Equal(2, regions.Count);
        }
        finally
        {
            window.Close();
        }
    }
}
