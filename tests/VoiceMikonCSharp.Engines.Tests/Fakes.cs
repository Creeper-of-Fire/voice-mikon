using VoiceMikonCSharp.Engines;
using Xunit;
using VoiceMikonCSharp.Engines.Vad;

namespace VoiceMikonCSharp.Engines.Tests;

/// <summary>假的识别后端：记录调用、返回预设文本，不联网不加载模型。</summary>
internal sealed class FakeBackend : IAsrBackend
{
    /// <summary>每次识别传入的音频长度。</summary>
    public List<int> CallLengths { get; } = new();

    /// <summary>识别回调（可改返回值 / 抛异常）。</summary>
    public Func<short[], string> OnTranscribe { get; set; } = _ => "文本";

    /// <summary>识别次数。</summary>
    public int CallCount => CallLengths.Count;

    public string ModelId { get; set; } = "fake";

    public bool Loaded { get; set; } = true;

    public double Load() => 0.0;

    public double SwitchModel(string modelTag)
    {
        ModelId = modelTag;
        return 0.0;
    }

    public void Unload() => Loaded = false;

    public string Transcribe(short[] audio, int sampleRate)
    {
        CallLengths.Add(audio.Length);
        return OnTranscribe(audio);
    }
}

/// <summary>
/// 假的判定层：直接按"第几帧算人声"给答案。
///
/// 这相当于**已经滞回过的判定结果** —— 切句状态机只跟这个接口打交道，
/// 所以测试切句行为时不需要真的跑能量法或 Silero。
/// </summary>
internal sealed class FakeDetector : VoiceDetector
{
    private readonly Func<int, bool> _isSpeech;
    private bool _inSpeech;

    public FakeDetector(int frameSamples, Func<int, bool> isSpeech, bool requiresExactFrames = false)
    {
        FrameSamples = frameSamples;
        RequiresExactFrames = requiresExactFrames;
        _isSpeech = isSpeech;
    }

    /// <summary>已判定的帧数。</summary>
    public int FrameIndex { get; private set; }

    public override int FrameSamples { get; }

    public override bool RequiresExactFrames { get; }

    public override void Reset() => FrameIndex = 0;

    public override bool IsSpeech(short[] frame)
    {
        bool result = _isSpeech(FrameIndex);
        FrameIndex++;
        _inSpeech = result;
        return result;
    }

    public override bool InSpeech => _inSpeech;
}

/// <summary>假时钟（秒）。用于测"自适应刷新间隔"。</summary>
internal sealed class FakeClock
{
    /// <summary>
    /// 起始值刻意给一个大数 —— 模拟 <c>time.monotonic()</c>：
    /// 首次刷新时 <c>now - last_refresh</c> 必然远大于间隔。
    /// </summary>
    public double Now { get; set; } = 1000.0;

    public void Advance(double seconds) => Now += seconds;
}

/// <summary>测试用的合成音频工具。</summary>
internal static class Synth
{
    /// <summary>恒定幅度正弦（int16）。</summary>
    public static short[] Sine(int sampleRate, double frequency, double seconds, double amplitude = 0.5, int phase = 0)
    {
        int n = (int)(sampleRate * seconds);
        var result = new short[n];
        for (int i = 0; i < n; i++)
        {
            double v = amplitude * Math.Sin((2 * Math.PI * frequency * i / sampleRate) + phase);
            result[i] = (short)Math.Round(v * 32767.0);
        }

        return result;
    }

    /// <summary>静音（int16 全 0）。</summary>
    public static short[] Silence(int sampleRate, double seconds)
    {
        return new short[(int)(sampleRate * seconds)];
    }

    /// <summary>把两段拼起来。</summary>
    public static short[] Concat(params short[][] parts)
    {
        int total = 0;
        foreach (short[] p in parts)
        {
            total += p.Length;
        }

        var result = new short[total];
        int offset = 0;
        foreach (short[] p in parts)
        {
            Array.Copy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }

        return result;
    }
}
