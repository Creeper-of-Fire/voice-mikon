using System;
using System.Collections.Generic;
using System.Threading;
using VmNext.Audio;
using Xunit;

namespace VmNext.Audio.Tests;

/// <summary>
/// 设备的集成测试会真的开音频流 —— 串行跑，别和别的流打架。
/// </summary>
[CollectionDefinition("audio", DisableParallelization = true)]
public class AudioCollection
{
}

/// <summary>
/// 录音的集成测试：真的开流录 2 秒。
///
/// 无麦克风 / 设备被占用时跳过（skip），并在报告里写明原因。
/// </summary>
[Collection("audio")]
public class RecorderIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public RecorderIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Integration")]
    public void 录两秒返回约两秒原生采样率的音频()
    {
        IReadOnlyList<InputDevice> devices = DevicePicker.ListInputs();
        _output.WriteLine($"输入设备数: {devices.Count}");
        foreach (InputDevice d in devices)
        {
            _output.WriteLine($"  {d.Display}  默认率={d.DefaultRate}  虚拟={d.IsVirtual}");
        }

        if (devices.Count == 0)
        {
            Assert.Skip("没有找到任何输入设备（无麦克风）");
        }

        using var recorder = new Recorder(levelWindow: 48);

        if (!recorder.Start())
        {
            Assert.Skip("录音启动失败（设备被占用或没有可用麦克风）");
        }

        Thread.Sleep(2000);

        RecordingResult result = recorder.Stop();
        int nativeRate = result.SampleRate;

        _output.WriteLine($"native_rate = {nativeRate} Hz");
        _output.WriteLine($"样本数 = {result.Samples.Length}（2 秒期望 ≈ {2 * nativeRate}）");

        double rms = DevicePicker.RmsOf(result.Samples);
        _output.WriteLine($"RMS = {rms:F6}");
        _output.WriteLine($"电平帧数 = {recorder.Levels.Count}");
        _output.WriteLine($"底噪估计 = {(recorder.NoiseFloor?.ToString("F6") ?? "null")}");

        // native_rate 必须是设备真实值（不是硬编码的 16000）
        Assert.True(nativeRate > 0, "native_rate 必须为正");

        // 长度 ≈ 2 秒 × 原生采样率（给足余量：起停有开销）
        double expected = 2.0 * nativeRate;
        Assert.InRange(result.Samples.Length, expected * 0.4, expected * 1.6);

        // RMS 合理（0..1，且不是 NaN）
        Assert.False(double.IsNaN(rms));
        Assert.InRange(rms, 0.0, 1.0);

        // 采样率是常见的设备率（16k/44.1k/48k 之类）
        Assert.Contains(nativeRate, new[] { 8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000 });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void 快照不消费增量游标能取到新增部分()
    {
        using var recorder = new Recorder(levelWindow: 48);

        if (!recorder.Start())
        {
            Assert.Skip("录音启动失败（设备被占用或没有可用麦克风）");
        }

        try
        {
            Thread.Sleep(600);

            short[] snapshot = recorder.Snapshot();
            short[] firstDrain = recorder.DrainChunks();

            _output.WriteLine($"快照 {snapshot.Length} 样本；首次增量 {firstDrain.Length} 样本");

            Assert.True(snapshot.Length > 0, "快照应该拿到已录到的音频");

            // 快照不消费：增量游标拿到的应该与快照内容一致（都是从头算起）
            Assert.Equal(snapshot.Length, firstDrain.Length);

            // 再取一次增量 → 应该是空（没有新数据）
            short[] secondDrain = recorder.DrainChunks();
            Assert.Empty(secondDrain);
        }
        finally
        {
            recorder.Stop();
        }
    }
}
