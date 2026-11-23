using System;
using System.Collections.Generic;
using System.Threading;
using VmNext.Core.Controller;
using VmNext.Core.Levels;
using VmNext.Engines;

namespace VmNext.App.Tests;

/// <summary>
/// 假识别后端。
///
/// 可配置：返回什么文本 / 延迟多久 / 抛不抛 <see cref="AsrError"/>。
/// 不依赖 Python、不联网、不加载真模型。
/// </summary>
public sealed class FakeBackend : IAsrBackend
{
    private int _transcribeCount;
    private int _loadCount;

    /// <summary>识别返回的文本。</summary>
    public string Text { get; set; } = "测试文本";

    /// <summary>每次识别前的延迟（毫秒）。</summary>
    public int DelayMs { get; set; }

    /// <summary>识别时是否抛 <see cref="AsrError"/>。</summary>
    public bool ThrowOnTranscribe { get; set; }

    /// <summary>抛错时的文案。</summary>
    public string ErrorMessage { get; set; } = "识别失败(测试注入)";

    /// <summary>模型标识（面板显示用）。</summary>
    public string ModelId { get; set; } = "fake-backend";

    /// <summary>被调用过几次识别。</summary>
    public int TranscribeCount => Volatile.Read(ref _transcribeCount);

    /// <summary>被调用过几次加载。</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <inheritdoc />
    public bool Loaded { get; private set; }

    /// <inheritdoc />
    public double Load()
    {
        Interlocked.Increment(ref _loadCount);
        Loaded = true;
        return 0.0;
    }

    /// <inheritdoc />
    public double SwitchModel(string modelTag)
    {
        ModelId = modelTag;
        return 0.0;
    }

    /// <inheritdoc />
    public void Unload() => Loaded = false;

    /// <inheritdoc />
    public string Transcribe(short[] audio, int sampleRate)
    {
        Interlocked.Increment(ref _transcribeCount);

        if (DelayMs > 0)
        {
            Thread.Sleep(DelayMs);
        }

        if (ThrowOnTranscribe)
        {
            throw new AsrError(ErrorMessage);
        }

        return Text;
    }
}

/// <summary>
/// 假录音器。
///
/// 只在"不关心真实麦克风"的测试里用（比如热键 → 录音这条链路）。
/// 真麦克风的链路在端到端测试里单独覆盖（没有麦克风就动态跳过）。
/// </summary>
public sealed class FakeRecorder : IDrainableRecorder
{
    private readonly int _sampleRate;
    private readonly int _samples;

    public FakeRecorder(int sampleRate = 16000, int samples = 16000)
    {
        _sampleRate = sampleRate;
        _samples = samples;
    }

    /// <summary>Start 被调用过几次。</summary>
    public int StartCount { get; private set; }

    /// <summary>Stop 被调用过几次。</summary>
    public int StopCount { get; private set; }

    /// <inheritdoc />
    public bool Start()
    {
        StartCount++;
        return true;
    }

    /// <inheritdoc />
    public RecorderStopResult Stop()
    {
        StopCount++;
        return new RecorderStopResult(new short[_samples], _sampleRate);
    }

    /// <inheritdoc />
    public short[] Snapshot() => Array.Empty<short>();

    /// <inheritdoc />
    public short[] DrainChunks() => Array.Empty<short>();

    /// <inheritdoc />
    public int NativeRate => _sampleRate;

    /// <inheritdoc />
    public IReadOnlyList<double> Levels => Array.Empty<double>();

    /// <inheritdoc />
    public double? NoiseFloor => null;

    /// <inheritdoc />
    public double Elapsed => 0.0;
}
