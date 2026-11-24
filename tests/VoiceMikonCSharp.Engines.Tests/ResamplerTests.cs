using VoiceMikonCSharp.Engines;
using Xunit;
using VoiceMikonCSharp.Engines.Dsp;

namespace VoiceMikonCSharp.Engines.Tests;

/// <summary>
/// 多相重采样的测试：输出长度、通带保真（频率不变、幅度误差 &lt; 1%）。
/// </summary>
public sealed class ResamplerTests
{
    [Fact]
    public void 输出长度是_ceil_n乘up除以down()
    {
        var input = new float[1000];

        Assert.Equal(334, Resampler.Resample(input, 48000, 16000).Length);   // ceil(1000/3)
        Assert.Equal(2000, Resampler.Resample(input, 16000, 32000).Length);  // ceil(1000×2)
        Assert.Equal(500, Resampler.Resample(input, 32000, 16000).Length);
        Assert.Equal(363, Resampler.Resample(input, 44100, 16000).Length);   // ceil(1000×160/441)
    }

    [Fact]
    public void 采样率相同_原样返回()
    {
        var input = new[] { 0.1f, -0.2f, 0.3f };

        float[] output = Resampler.Resample(input, 16000, 16000);

        Assert.Equal(input, output);
        Assert.NotSame(input, output);
    }

    [Fact]
    public void 采样率不合法_抛_AsrError()
    {
        Assert.Throws<AsrError>(() => Resampler.Resample(new float[10], 0, 16000));
        Assert.Throws<AsrError>(() => Resampler.Resample(new float[10], 16000, -1));
    }

    [Fact]
    public void 正弦重采样后_频率不变()
    {
        const int SrcRate = 48000;
        const int DstRate = 16000;
        const double Freq = 1000.0;
        const double Amplitude = 0.5;

        float[] input = MakeSine(SrcRate, Freq, 1.0, Amplitude);
        float[] output = Resampler.Resample(input, SrcRate, DstRate);

        double estimated = EstimateFrequency(output, DstRate);

        Assert.InRange(estimated, Freq * 0.99, Freq * 1.01);
    }

    [Fact]
    public void 正弦重采样后_幅度误差小于百分之一()
    {
        const int SrcRate = 48000;
        const int DstRate = 16000;
        const double Freq = 1000.0;
        const double Amplitude = 0.5;

        float[] input = MakeSine(SrcRate, Freq, 1.0, Amplitude);
        float[] output = Resampler.Resample(input, SrcRate, DstRate);

        // 去掉首尾各 10%（滤波器边缘瞬态）
        double inRms = Rms(input, input.Length / 10, input.Length * 80 / 100);
        double outRms = Rms(output, output.Length / 10, output.Length * 80 / 100);

        double error = Math.Abs(outRms - inRms) / inRms;

        Assert.True(error < 0.01, $"幅度误差 {error:P3} 超过 1%（in={inRms:F6}, out={outRms:F6}）");
    }

    [Fact]
    public void 直流信号重采样后幅度保持()
    {
        var input = new float[1000];
        Array.Fill(input, 0.8f);

        float[] output = Resampler.Resample(input, 48000, 16000);

        // 取中间部分（避开边缘瞬态）
        double mid = output[output.Length / 2];
        Assert.InRange(mid, 0.79, 0.81);
    }

    [Fact]
    public void 高频成分被抗混叠低通压下去()
    {
        // 10kHz 在 48k → 16k 时高于截止频率（8k），应该被滤掉
        const int SrcRate = 48000;
        const int DstRate = 16000;

        float[] input = MakeSine(SrcRate, 10000.0, 1.0, 0.5);
        float[] output = Resampler.Resample(input, SrcRate, DstRate);

        double inRms = Rms(input, input.Length / 10, input.Length * 80 / 100);
        double outRms = Rms(output, output.Length / 10, output.Length * 80 / 100);

        Assert.True(outRms < inRms * 0.2, $"混叠没有被压住（in={inRms:F6}, out={outRms:F6}）");
    }

    private static float[] MakeSine(int rate, double frequency, double seconds, double amplitude)
    {
        int n = (int)(rate * seconds);
        var result = new float[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
        }

        return result;
    }

    private static double Rms(float[] x, int start, int count)
    {
        double sum = 0;
        for (int i = start; i < start + count && i < x.Length; i++)
        {
            sum += x[i] * x[i];
        }

        return Math.Sqrt(sum / count);
    }

    /// <summary>自相关估频（整数滞后 → 分辨率 = rate/lag）。</summary>
    private static double EstimateFrequency(float[] x, double rate)
    {
        int n = x.Length;
        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            mean += x[i];
        }

        mean /= n;

        int maxLag = (int)(rate / 50.0);
        double best = double.MinValue;
        int bestLag = 1;
        for (int lag = 2; lag < maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < n; i++)
            {
                double a = x[i] - mean;
                double b = x[i + lag] - mean;
                sum += a * b;
            }

            if (sum > best)
            {
                best = sum;
                bestLag = lag;
            }
        }

        return rate / bestLag;
    }
}
