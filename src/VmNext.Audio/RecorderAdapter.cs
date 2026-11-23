using System;
using VmNext.Core.Controller;
using VmNext.Core.Levels;

namespace VmNext.Audio;

/// <summary>
/// 把 <see cref="Recorder"/> 包成 Core 的 <see cref="IRecorder"/>。
///
/// 为什么要有这一层（疑问清单 #1）：
///
/// * <c>VmNext.Audio</c> **引用** <c>VmNext.Core</c>（电平泵在 Core 里），
///   反过来引用会成环。控制器住在 Core，就不能 <c>using VmNext.Audio</c>。
/// * 所以 Core 只认"谁能给我录音 / 电平 / 底噪"，具体设备由这一层接进去。
///
/// 行为**一字不差**：所有状态都在 <see cref="Recorder"/> 里面，
/// 这里只是换了个接口形状（<c>Stop()</c> 的返回类型）。
/// </summary>
public sealed class RecorderAdapter : IDrainableRecorder
{
    private readonly Recorder _recorder;

    /// <summary>包一个录音器。</summary>
    public RecorderAdapter(Recorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <summary>底层录音器（诊断用）。</summary>
    public Recorder Inner => _recorder;

    /// <inheritdoc />
    public bool Start() => _recorder.Start();

    /// <inheritdoc />
    public RecorderStopResult Stop()
    {
        RecordingResult result = _recorder.Stop();
        return new RecorderStopResult(result.Samples, result.SampleRate);
    }

    /// <inheritdoc />
    public short[] Snapshot() => _recorder.Snapshot();

    /// <inheritdoc />
    public short[] DrainChunks() => _recorder.DrainChunks();

    /// <inheritdoc />
    public int NativeRate => _recorder.NativeRate;

    /// <inheritdoc />
    public IReadOnlyList<double> Levels => _recorder.Levels;

    /// <inheritdoc />
    public double? NoiseFloor => _recorder.NoiseFloor;

    /// <inheritdoc />
    public double Elapsed => _recorder.Elapsed;
}
