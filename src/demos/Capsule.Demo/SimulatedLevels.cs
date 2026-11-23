using System;

namespace Capsule.Demo;

/// <summary>
/// 模拟电平源：把"说话包络 × 音节起伏 + 噪声"算成一个 0..1 的响度。
///
/// <para>真机的电平是 presenter 每 30fps 推过来的（<c>LevelPump.DbfsToUnit</c> 的输出），
/// demo 里没有麦克风，用正弦 + 噪声模拟，形状与真机一致：
/// 慢包络让波形有说 / 停的段落（停的段落电平落到死区以下 → 画灰色静音条），
/// 快起伏模拟音节，噪声模拟环境底噪。</para>
/// </summary>
public sealed class SimulatedLevels
{
    private readonly Random _random = new(20261123);

    /// <summary>算某一时刻的响度（0..1）。</summary>
    /// <param name="timeSeconds">从开始录制算起的秒数（驱动正弦相位）。</param>
    public double LevelAt(double timeSeconds)
    {
        // 说话包络：0.35 Hz 的"说 / 停"节奏；平方一下让停顿段落更明显
        double envelope = 0.5 + (0.5 * Math.Sin((2 * Math.PI * 0.35 * timeSeconds) - 1.2));
        envelope *= envelope;

        // 音节起伏：1.8 Hz
        double syllable = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 1.8 * timeSeconds));

        // 环境底噪（Random 的 ±0.08）
        double noise = (_random.NextDouble() - 0.5) * 0.16;

        double raw = 0.06 + (0.75 * envelope * syllable) + noise;
        return Math.Clamp(raw, 0, 1);
    }
}
