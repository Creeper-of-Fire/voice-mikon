using System.Drawing;
using QrScanner.Core;
using Xunit;

namespace VoiceMikonCSharp.Companions.Tests;

/// <summary>
/// 框选几何的单元测试（纯函数）。
///
/// <para>覆盖：正向拖拽 / 反向拖拽 / 跨显示器（虚拟桌面原点在负方向）/
/// 拖太小当误触。</para>
/// </summary>
public class SelectionGeometryTests
{
    [Fact]
    public void 正向拖拽给出选区矩形()
    {
        Rectangle region = SelectionGeometry.Normalize(new Point(100, 200), new Point(300, 500));

        Assert.Equal(new Rectangle(100, 200, 200, 300), region);
    }

    [Fact]
    public void 反向拖拽框出同一个矩形()
    {
        Rectangle forward = SelectionGeometry.Normalize(new Point(100, 200), new Point(300, 500));
        Rectangle backward = SelectionGeometry.Normalize(new Point(300, 500), new Point(100, 200));

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void 跨显示器_虚拟桌面原点在左边时换算正确()
    {
        // 副屏挂在主屏左边：虚拟桌面 = (-1920, 0, 3840, 1080)
        var screen = new Rectangle(-1920, 0, 3840, 1080);

        Point image = SelectionGeometry.ToImage(new Point(-500, 300), screen);

        Assert.Equal(new Point(1420, 300), image);
    }

    [Fact]
    public void 跨显示器_选区仍落在截图范围内()
    {
        var screen = new Rectangle(-1920, 0, 3840, 1080);

        Point start = SelectionGeometry.ToImage(new Point(-1800, 100), screen);
        Point end = SelectionGeometry.ToImage(new Point(-1200, 400), screen);
        Rectangle region = SelectionGeometry.Normalize(start, end);

        Assert.Equal(new Rectangle(120, 100, 600, 300), region);
        Assert.True(region.Left >= 0);
        Assert.True(region.Right <= screen.Width);
    }

    [Fact]
    public void 拖太小算误触()
    {
        Assert.True(SelectionGeometry.IsTooSmall(new Rectangle(0, 0, 9, 50)));
        Assert.True(SelectionGeometry.IsTooSmall(new Rectangle(0, 0, 50, 9)));
        Assert.False(SelectionGeometry.IsTooSmall(new Rectangle(0, 0, 10, 10)));
        Assert.False(SelectionGeometry.IsTooSmall(new Rectangle(0, 0, 120, 80)));
    }
}
