using System;
using System.Collections.Generic;
using System.Threading;
using VoiceMikonCSharp.Core.Levels;
using Xunit;

namespace VoiceMikonCSharp.Core.Tests;

/// <summary>
/// 电平泵的单元测试：映射规则 + 30fps 节奏。
/// </summary>
public class LevelPumpTests
{
    /// <summary>假的电平数据源（固定电平 + 固定底噪 + 递增计时）。</summary>
    private sealed class FakeLevelSource : ILevelSource
    {
        private double _elapsed;

        public IReadOnlyList<double> Levels { get; } = new[] { 0.01, 0.02, 0.05 };

        public double? NoiseFloor => 0.001;

        public double Elapsed
        {
            get
            {
                _elapsed += 0.03;
                return _elapsed;
            }
        }
    }

    [Fact]
    public void 节奏约每秒30次推送()
    {
        using var pump = new LevelPump(new FakeLevelSource());

        int count = 0;
        pump.Subscribe(_ => Interlocked.Increment(ref count));

        pump.Start();
        Thread.Sleep(1000);
        pump.Stop();

        int seen = Volatile.Read(ref count);

        // 30fps → 1 秒 ≈ 30 次；给足余量（机器忙时可能少几帧）
        Assert.InRange(seen, 18, 40);
    }

    [Fact]
    public void 停止后不再推送()
    {
        using var pump = new LevelPump(new FakeLevelSource());

        int count = 0;
        pump.Subscribe(_ => Interlocked.Increment(ref count));

        pump.Start();
        Thread.Sleep(200);
        pump.Stop();

        int after = Volatile.Read(ref count);
        Thread.Sleep(300);

        Assert.Equal(after, Volatile.Read(ref count));
    }

    [Fact]
    public void 底噪门以下的电平判零()
    {
        Assert.Equal(0.0, LevelPump.DbfsToUnit(0.0005, floorRms: 0.001));
        Assert.Equal(0.0, LevelPump.DbfsToUnit(0.0));
    }

    [Fact]
    public void 对数映射把语音区间拉到中高段()
    {
        // 人声约 -35 ~ -20 dBFS → 0.5 ~ 0.71（对齐 Python 版的实测区间）
        double loud = LevelPump.DbfsToUnit(0.0176);   // -35 dBFS
        double near = LevelPump.DbfsToUnit(0.1);      // -20 dBFS

        Assert.InRange(loud, 0.45, 0.56);
        Assert.InRange(near, 0.66, 0.72);
    }

    [Fact]
    public void 底噪没收敛时退回绝对下限()
    {
        Assert.Equal(LevelPump.LevelGateMin, LevelPump.LevelGate(null));
        Assert.Equal(LevelPump.LevelGateMin, LevelPump.LevelGate(0.0));
    }

    [Fact]
    public void 门限随底噪走()
    {
        // 底噪 0.01 → 门限 = 0.01 × 1.5
        Assert.Equal(0.015, LevelPump.LevelGate(0.01), 10);
    }
}
