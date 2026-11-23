using System;
using VmNext.Shell;
using VmNext.Ui;
using Xunit;

namespace VmNext.Ui.Tests;

/// <summary>
/// 摆位计算的单元测试。
///
/// <para>对着 voice-mikon/src/voice_mikon/host/placement.py 的函数与常量写 ——
/// 用例里的期望值都是**按 Python 的公式手算出来的**，
/// 目的是保证 C# 版与 Python 版的输出**逐位相同**（位置记忆的编码格式要兼容）。</para>
/// </summary>
public class PlacementTests
{
    /// <summary>造一块工作区（左上 + 尺寸）。</summary>
    private static NativeMethods.RECT WorkArea(int left, int top, int width, int height)
        => new() { Left = left, Top = top, Right = left + width, Bottom = top + height };

    // ==================== 常量 ====================

    /// <summary>常量与 Python 版一致（placement.py 的 ORB_SIZE / ORB_INSET_Y / ORB_MARGIN / EDGE_MARGIN）。</summary>
    [Fact]
    public void 常量_与Python版一致()
    {
        Assert.Equal(40, Placement.OrbSize);
        Assert.Equal(10, Placement.OrbInsetY);
        Assert.Equal(16, Placement.OrbMargin);
        Assert.Equal(8, Placement.EdgeMargin);
    }

    // ==================== encode_offset / decode_offset ====================

    /// <summary>
    /// 编码：物理坐标 → 相对工作区的逻辑偏移（<c>"dx,dy"</c>）。
    ///
    /// <para>Python: <c>encode_offset(1000, 500, (0, 0, 1920, 1080), 2.0)</c> → <c>"500,250"</c>。</para>
    /// </summary>
    [Fact]
    public void 编码_相对工作区的逻辑偏移()
    {
        string encoded = Placement.EncodeOffset(1000, 500, WorkArea(0, 0, 1920, 1080), 2.0);
        Assert.Equal("500,250", encoded);
    }

    /// <summary>工作区有偏移时，编码的是**相对工作区**的量（不是相对屏幕原点）。</summary>
    [Fact]
    public void 编码_工作区有偏移时按工作区算()
    {
        // 工作区从 (1920, 0) 开始（第二块屏），球在 (2120, 100)，缩放 2.0 → dx=100, dy=50
        string encoded = Placement.EncodeOffset(2120, 100, WorkArea(1920, 0, 1920, 1080), 2.0);
        Assert.Equal("100,50", encoded);
    }

    /// <summary>解码：<c>"dx,dy"</c> → 一对偏移。</summary>
    [Fact]
    public void 解码_正常格式()
    {
        (int X, int Y)? decoded = Placement.DecodeOffset("500,250");
        Assert.NotNull(decoded);
        Assert.Equal(500, decoded!.Value.X);
        Assert.Equal(250, decoded.Value.Y);
    }

    /// <summary>解码失败一律返回 null（调用方走兜底，不猜）。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("12,34,56")]
    [InlineData("12,x")]
    public void 解码_非法输入返回空(string? geometry)
    {
        Assert.Null(Placement.DecodeOffset(geometry));
    }

    /// <summary>编码 → 解码 往返（缩放 1.0 时是恒等）。</summary>
    [Fact]
    public void 编码解码_往返一致()
    {
        var area = WorkArea(0, 0, 1920, 1080);
        string encoded = Placement.EncodeOffset(123, 456, area, 1.0);
        (int X, int Y)? decoded = Placement.DecodeOffset(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(123, decoded!.Value.X);
        Assert.Equal(456, decoded.Value.Y);
    }

    // ==================== orb_inset_x ====================

    /// <summary>球在窗口里底边居中 → 球左边到窗口左边的偏移（整除）。</summary>
    [Fact]
    public void 球内缩_窗口512宽时是236()
    {
        // Python: orb_inset_x(512, 40) = (512 - 40) // 2 = 236
        Assert.Equal(236, Placement.OrbInsetX(512));
    }

    /// <summary>窗口比球还窄时，偏移不小于 0。</summary>
    [Fact]
    public void 球内缩_窗口太窄时归零()
    {
        Assert.Equal(0, Placement.OrbInsetX(20));
    }

    // ==================== orb_screen_position ====================

    /// <summary>
    /// 窗口左上角 → 球的左上角。
    ///
    /// <para>Python: <c>orb_screen_position(100, 200, 512*2, 60*2, 2.0)</c>
    /// → 客户端宽 = 512，球内缩 236 → x = 100 + 236*2 = 572；
    /// y = 200 + 120 − (10+40)*2 = 220。</para>
    /// </summary>
    [Fact]
    public void 球的屏幕位置_底边居中()
    {
        (int X, int Y) position = Placement.OrbScreenPosition(100, 200, 512 * 2, 60 * 2, 2.0);
        Assert.Equal(572, position.X);
        Assert.Equal(220, position.Y);
    }

    // ==================== default_orb_position ====================

    /// <summary>
    /// 默认摆工作区右下角（留白 16 逻辑像素）。
    ///
    /// <para>Python: <c>default_orb_position((0,0,1920,1080), 1.0)</c> → (1864, 1024)。</para>
    /// </summary>
    [Fact]
    public void 默认位置_工作区右下角()
    {
        (int X, int Y) position = Placement.DefaultOrbPosition(WorkArea(0, 0, 1920, 1080), 1.0);
        Assert.Equal(1920 - 16 - 40, position.X);
        Assert.Equal(1080 - 16 - 40, position.Y);
    }

    /// <summary>缩放 2.0 时留白与球直径都按物理像素翻倍。</summary>
    [Fact]
    public void 默认位置_按缩放换算()
    {
        (int X, int Y) position = Placement.DefaultOrbPosition(WorkArea(0, 0, 1920, 1080), 2.0);
        Assert.Equal(1920 - 32 - 80, position.X);
        Assert.Equal(1080 - 32 - 80, position.Y);
    }

    // ==================== clamp_to_work_area ====================

    /// <summary>窗口左上角被夹进工作区（留 margin 边距）。</summary>
    [Fact]
    public void 夹取_窗口不能跑出工作区()
    {
        (int X, int Y) position = Placement.ClampToWorkArea(
            -500, -500, 512, 60, WorkArea(0, 0, 1920, 1080));

        Assert.Equal(8, position.X);
        Assert.Equal(8, position.Y);
    }

    /// <summary>窗口太大时不会算出负的边界（至少留最小边距）。</summary>
    [Fact]
    public void 夹取_窗口比工作区还大时不越界()
    {
        (int X, int Y) position = Placement.ClampToWorkArea(
            0, 0, 4000, 2000, WorkArea(0, 0, 1920, 1080));

        Assert.Equal(8, position.X);
        Assert.Equal(8, position.Y);
    }

    // ==================== clamp_orb_position ====================

    /// <summary>球被夹回工作区内（留 margin 边距）。</summary>
    [Fact]
    public void 夹取球_横向纵向都在工作区内()
    {
        (int X, int Y) position = Placement.ClampOrbPosition(
            -100, 99999, 512, 60, 1.0, WorkArea(0, 0, 1920, 1080));

        Assert.Equal(16, position.X);
        Assert.Equal(1080 - 16 - 40, position.Y);
    }

    /// <summary>
    /// 纵向比横向严：球的上边要留给展开后的胶囊。
    ///
    /// <para>Python: <c>head = max(0, win_h - (inset_y + orb_size) * scale)</c>。
    /// 窗口高 60、缩放 1.0 → head = 60 − 50 = 10 → 球的上边至少在 y=10。</para>
    /// </summary>
    [Fact]
    public void 夹取球_纵向要给胶囊留位()
    {
        (int X, int Y) position = Placement.ClampOrbPosition(
            100, 0, 512, 60, 1.0, WorkArea(0, 0, 1920, 1080));

        Assert.Equal(10, position.Y);
    }

    // ==================== orb_window_position ====================

    /// <summary>球的位置 → 窗口左上角（反推）。</summary>
    [Fact]
    public void 球位置反推窗口位置()
    {
        (int X, int Y) position = Placement.OrbWindowPosition(
            100, 100, 512, 60, 1.0, WorkArea(0, 0, 1920, 1080));

        // 球内缩 236 → 窗口 x = 100 − 236 = −136
        // 球底到窗口底 10 → 窗口 y = 100 + 50 − 60 = 90
        Assert.Equal(-136, position.X);
        Assert.Equal(90, position.Y);
    }

    // ==================== orb_position_from_geometry ====================

    /// <summary>记忆的偏移 → 窗口位置（跨缩放也能还原到同一个相对位置）。</summary>
    [Fact]
    public void 记忆偏移_还原到窗口位置()
    {
        // "100,50" 缩放 1.0 → 球在 (100, 50)
        (int X, int Y)? position = Placement.OrbPositionFromGeometry(
            "100,50", WorkArea(0, 0, 1920, 1080), 1.0, 512, 60);

        Assert.NotNull(position);
        Assert.Equal(100 - 236, position!.Value.X);
        Assert.Equal(50 + 50 - 60, position.Value.Y);
    }

    /// <summary>记忆无效（空 / 非法）时返回 null → 调用方走默认位置。</summary>
    [Fact]
    public void 记忆偏移_无效时返回空()
    {
        Assert.Null(Placement.OrbPositionFromGeometry(
            string.Empty, WorkArea(0, 0, 1920, 1080), 1.0, 512, 60));

        Assert.Null(Placement.OrbPositionFromGeometry(
            "乱写", WorkArea(0, 0, 1920, 1080), 1.0, 512, 60));
    }

    /// <summary>
    /// 换缩放率：同一个记忆偏移，在 2.0 缩放下落在工作区里**同一个相对位置**。
    ///
    /// <para>这是"存逻辑偏移而不是绝对坐标"的意义（placement.py 的模块说明）。</para>
    /// </summary>
    [Fact]
    public void 记忆偏移_换缩放率相对位置不变()
    {
        (int X, int Y)? at1x = Placement.OrbPositionFromGeometry(
            "100,50", WorkArea(0, 0, 1920, 1080), 1.0, 512, 60);

        (int X, int Y)? at2x = Placement.OrbPositionFromGeometry(
            "100,50", WorkArea(0, 0, 1920, 1080), 2.0, 1024, 120);

        Assert.NotNull(at1x);
        Assert.NotNull(at2x);

        // 反算球在屏幕上的位置（用同一套公式，别手算）。
        (int orbX1, int orbY1) = Placement.OrbScreenPosition(at1x!.Value.X, at1x.Value.Y, 512, 60, 1.0);
        (int orbX2, int orbY2) = Placement.OrbScreenPosition(at2x!.Value.X, at2x.Value.Y, 1024, 120, 2.0);

        // 物理像素当然不同（缩放不同）—— **逻辑**位置相同才是重点：
        // 球在工作区里的相对位置没变。
        Assert.Equal(orbX1, orbX2 / 2);
        Assert.Equal(orbY1, orbY2 / 2);

        // 逻辑上就是那个偏移（100,50）。
        Assert.Equal(100, orbX1);
        Assert.Equal(50, orbY1);
    }
}
