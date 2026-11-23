using System;
using VmNext.Audio;
using Xunit;

namespace VmNext.Audio.Tests;

/// <summary>
/// 底噪估计的单元测试：样本不足返回 null、样本够了才收敛。
/// </summary>
public class NoiseFloorTests
{
    [Fact]
    public void 样本不足时返回null()
    {
        var tracker = new NoiseFloorTracker(window: 150, minSamples: 50);

        Assert.Null(tracker.Floor());

        for (int i = 0; i < 49; i++)
        {
            tracker.Update(0.01);
        }

        Assert.Null(tracker.Floor());
    }

    [Fact]
    public void 攒够样本后返回p10估计()
    {
        var tracker = new NoiseFloorTracker(window: 150, minSamples: 50);

        // 稳定底噪 0.01 + 少量高电平帧（模拟说话）
        for (int i = 0; i < 100; i++)
        {
            tracker.Update(i % 20 == 0 ? 0.5 : 0.01);
        }

        double? floor = tracker.Floor();

        Assert.NotNull(floor);

        // p10 落在底噪附近（不是被偶发脉冲拉低，也不是被拉高）
        Assert.InRange(floor!.Value, 0.009, 0.011);
    }

    [Fact]
    public void 非有限值被忽略不会毒死估计()
    {
        var tracker = new NoiseFloorTracker(window: 10, minSamples: 5);

        tracker.Update(double.NaN);
        tracker.Update(double.PositiveInfinity);
        for (int i = 0; i < 5; i++)
        {
            tracker.Update(0.02);
        }

        Assert.NotNull(tracker.Floor());
        Assert.InRange(tracker.Floor()!.Value, 0.019, 0.021);
    }

    [Fact]
    public void 窗口只保留最近的样本()
    {
        // window=10：喂 100 个 0.01 再喂 10 个 0.5，估计应该跟着抬上去
        var tracker = new NoiseFloorTracker(window: 10, minSamples: 10);

        for (int i = 0; i < 100; i++)
        {
            tracker.Update(0.01);
        }

        Assert.InRange(tracker.Floor()!.Value, 0.009, 0.011);

        for (int i = 0; i < 10; i++)
        {
            tracker.Update(0.5);
        }

        Assert.InRange(tracker.Floor()!.Value, 0.49, 0.51);
    }
}
