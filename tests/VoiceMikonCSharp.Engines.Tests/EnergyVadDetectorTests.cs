using VoiceMikonCSharp.Engines.Vad;
using Xunit;

namespace VoiceMikonCSharp.Engines.Tests;

/// <summary>
/// 能量域判定（底噪估计 + 滞回双门）的测试。
/// </summary>
public sealed class EnergyVadDetectorTests
{
    private const int Rate = 16000;

    /// <summary>造一帧恒定幅度为 rms 的样本（RMS 就是这个值）。</summary>
    private static short[] FrameWithRms(double rms)
    {
        var frame = new short[320];
        short value = (short)Math.Round(rms * 32768.0);
        Array.Fill(frame, value);
        return frame;
    }

    [Fact]
    public void 帧长是_20ms()
    {
        var detector = new EnergyVadDetector(Rate);

        Assert.Equal(320, detector.FrameSamples);
        Assert.False(detector.RequiresExactFrames);
    }

    [Fact]
    public void 低于开门门限_不算说话()
    {
        var detector = new EnergyVadDetector(Rate, () => 0.01);

        // 开门门 = 0.01 × 2.5 = 0.025
        Assert.False(detector.IsSpeech(FrameWithRms(0.02)));
    }

    [Fact]
    public void 超过开门门限_算说话()
    {
        var detector = new EnergyVadDetector(Rate, () => 0.01);

        Assert.True(detector.IsSpeech(FrameWithRms(0.03)));
    }

    [Fact]
    public void 滞回_中间带保持说话状态()
    {
        var detector = new EnergyVadDetector(Rate, () => 0.01);

        Assert.True(detector.IsSpeech(FrameWithRms(0.03)));   // 进说话态
        // 关门门 = 0.01 × 1.5 = 0.015；0.02 在滞回带里 → 保持说话
        Assert.True(detector.IsSpeech(FrameWithRms(0.02)));
    }

    [Fact]
    public void 滞回_中间带保持静音状态()
    {
        var detector = new EnergyVadDetector(Rate, () => 0.01);

        Assert.False(detector.IsSpeech(FrameWithRms(0.02)));  // 没到开门门
        // 0.02 < 开门门 0.025 → 仍然不是说话
        Assert.False(detector.IsSpeech(FrameWithRms(0.02)));
    }

    [Fact]
    public void 跌破关门门限_停止说话()
    {
        var detector = new EnergyVadDetector(Rate, () => 0.01);

        Assert.True(detector.IsSpeech(FrameWithRms(0.03)));
        Assert.False(detector.IsSpeech(FrameWithRms(0.01)));   // < 关门门 0.015
    }

    [Fact]
    public void 底噪未收敛时退到兜底门()
    {
        var detector = new EnergyVadDetector(Rate, () => null);

        // 兜底门 1e-4：极小的声音也算说话（宁可把底噪包进句子）
        Assert.True(detector.IsSpeech(FrameWithRms(0.001)));
    }

    [Fact]
    public void 底噪回调抛异常时也退到兜底门()
    {
        var detector = new EnergyVadDetector(Rate, () => throw new InvalidOperationException("boom"));

        Assert.True(detector.IsSpeech(FrameWithRms(0.001)));
    }

    [Fact]
    public void reset_清掉滞回状态()
    {
        var detector = new EnergyVadDetector(Rate, () => 0.01);

        Assert.True(detector.IsSpeech(FrameWithRms(0.03)));
        Assert.True(detector.InSpeech);

        detector.Reset();

        Assert.False(detector.InSpeech);
    }

    [Fact]
    public void 归一化_rms_按_int16_刻度算()
    {
        // 全幅 int16（32767）→ 归一化后 ≈ 1.0
        var frame = new short[320];
        Array.Fill(frame, short.MaxValue);

        double rms = FrameRms.Compute(frame);

        Assert.InRange(rms, 0.999, 1.0);
        Assert.Equal(0.0, FrameRms.Compute(Array.Empty<short>()));
    }
}
