using Avalonia;
using VmNext.Ui;
using Xunit;

namespace VmNext.Ui.Tests;

/// <summary>
/// 拖拽几何的单元测试：鼠标位移 → 窗口位移。
///
/// <para>纯计算，不碰任何窗口句柄（真机拖拽行为由用户验证）。</para>
/// </summary>
public class DragGeometryTests
{
    /// <summary>单屏（缩放不变）：窗口与鼠标 1:1 跟手。</summary>
    [Fact]
    public void 单屏_窗口与鼠标1比1()
    {
        PixelPoint result = DragGeometry.WindowPosition(
            new PixelPoint(100, 200), 30, -40, 1.75, 1.75);

        Assert.Equal(130, result.X);
        Assert.Equal(160, result.Y);
    }

    /// <summary>缩放 2.0 时：物理位移 → 逻辑位移（÷2）→ 物理位移（×2）= 原值。</summary>
    [Fact]
    public void 同缩放_位移还原()
    {
        PixelPoint result = DragGeometry.WindowPosition(
            new PixelPoint(0, 0), 100, 200, 2.0, 2.0);

        Assert.Equal(100, result.X);
        Assert.Equal(200, result.Y);
    }

    /// <summary>
    /// 跨屏（缩放变了）：逻辑位移按**当前**缩放折回物理 ——
    /// 与 Python 的 <c>move_window_to(round(nx * scale), round(ny * scale))</c> 一致
    /// （那里的 scale 取的是当前窗口所在显示器的缩放）。
    /// </summary>
    [Fact]
    public void 跨屏_按当前缩放换算()
    {
        // 按下时缩放 1.0，鼠标物理位移 100 → 逻辑位移 100；
        // 现在到了 2.0 的屏 → 物理位移 100 * 2 = 200。
        PixelPoint result = DragGeometry.WindowPosition(
            new PixelPoint(0, 0), 100, 0, 1.0, 2.0);

        Assert.Equal(200, result.X);
    }

    /// <summary>缩放为 0（拿不到）时按 1.0 处理，不至于算出 NaN。</summary>
    [Fact]
    public void 缩放无效_按1比1处理()
    {
        PixelPoint result = DragGeometry.WindowPosition(
            new PixelPoint(10, 20), 5, 6, 0, 0);

        Assert.Equal(15, result.X);
        Assert.Equal(26, result.Y);
    }

    /// <summary>点球 vs 拖球：靠"有没有真的动过"区分。</summary>
    [Fact]
    public void 动过_算拖()
    {
        Assert.True(DragGeometry.HasMoved(new PixelPoint(0, 0), new PixelPoint(1, 0)));
        Assert.True(DragGeometry.HasMoved(new PixelPoint(0, 0), new PixelPoint(0, 1)));
    }

    /// <summary>没动过 = 点（开始录音）。</summary>
    [Fact]
    public void 没动过_算点()
    {
        Assert.False(DragGeometry.HasMoved(new PixelPoint(5, 5), new PixelPoint(5, 5)));
    }
}
