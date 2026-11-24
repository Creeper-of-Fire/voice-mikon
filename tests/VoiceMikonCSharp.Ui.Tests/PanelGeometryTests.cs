using Xunit;

namespace VoiceMikonCSharp.Ui.Tests;

/// <summary>
/// 面板位置记忆（<c>config.panel_geometry</c>）的编解码测试。
///
/// <para>翻译源：voice-mikon 的 <c>host/panel.py</c> 的 <c>_parse_geometry</c>（L480）
/// 与 <c>_save_geometry</c>（L500）。</para>
///
/// <para>⚠ 格式必须与 Python 版**逐字兼容**：<c>"x,y,w,h"</c>，逻辑像素。
/// 用户拿旧的 <c>data/config.json</c> 直接能用。</para>
/// </summary>
public class PanelGeometryTests
{
    [Fact]
    public void 编码_是逗号分隔的四个整数()
    {
        Assert.Equal("120,80,320,340", PanelGeometry.Format(120, 80));
    }

    [Fact]
    public void 解码_读出左上角()
    {
        Assert.True(PanelGeometry.TryParse("120,80,320,340", out int x, out int y));
        Assert.Equal(120, x);
        Assert.Equal(80, y);
    }

    [Fact]
    public void 往返_编完再解还是同一个位置()
    {
        string text = PanelGeometry.Format(-37, 1543);
        Assert.True(PanelGeometry.TryParse(text, out int x, out int y));
        Assert.Equal(-37, x);
        Assert.Equal(1543, y);
    }

    [Fact]
    public void 往返_与Python版的字符串逐字相同()
    {
        // Python: f"{self._pos_x},{self._pos_y},{BASE_W},{BASE_H}"
        Assert.Equal("10,20,320,340", PanelGeometry.Format(10, 20));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("120,80")]
    [InlineData("120,80,320,340,1")]
    [InlineData("a,b,320,340")]
    [InlineData("120.5,80,320,340")]
    public void 解码_认不出来就返回false(string? geometry)
    {
        // Python 版：段数不对 / 不是整数 → log.info 一条，然后退回默认摆位。
        Assert.False(PanelGeometry.TryParse(geometry, out _, out _));
    }

    [Fact]
    public void 基准尺寸与Python版一致()
    {
        // host/panel.py: BASE_W = 320 / BASE_H = 340
        Assert.Equal(320, PanelGeometry.BaseWidth);
        Assert.Equal(340, PanelGeometry.BaseHeight);
    }
}
