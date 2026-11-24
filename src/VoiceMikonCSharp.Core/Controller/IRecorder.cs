using VoiceMikonCSharp.Core.Levels;

namespace VoiceMikonCSharp.Core.Controller;

/// <summary>一次录音的结果：int16 单声道样本 + 设备原生采样率。</summary>
public readonly record struct RecorderStopResult(short[] Samples, int SampleRate);

/// <summary>
/// 录音器抽象 —— **Core 不认识音频设备**。
///
/// 翻译自 voice-mikon/src/voice_mikon/audio/recorder.py（Python 那边是具体类
/// <c>Recorder</c>，控制器直接用鸭子类型调它）。
///
/// 为什么要有这一层（疑问清单 #1）：
///
/// * <c>VoiceMikonCSharp.Audio</c> **引用** <c>VoiceMikonCSharp.Core</c>（电平泵在 Core 里），
///   反过来引用会成环。控制器住在 Core，就不能 <c>using VoiceMikonCSharp.Audio</c>。
/// * 所以 Core 只认"谁能给我录音 / 电平 / 底噪"，具体设备由装配层接进来
///   （<c>VoiceMikonCSharp.Audio.RecorderAdapter</c> 把 <c>Recorder</c> 包成这个接口）。
///
/// 依赖收窄到"谁来供数"这个窄接口，测试里可以给个假录音器，行为完全可复现。
/// </summary>
public interface IRecorder : ILevelSource
{
    /// <summary>启动录音。失败返回 false（原因写进日志）。</summary>
    bool Start();

    /// <summary>停止录音并返回**设备原生采样率**的单声道 int16 音频。</summary>
    RecorderStopResult Stop();

    /// <summary>
    /// 当前已录到的音频（原生采样率，int16 单声道）—— **不消费**。
    /// </summary>
    short[] Snapshot();

    /// <summary>设备原生采样率（Hz）。</summary>
    int NativeRate { get; }
}

/// <summary>
/// 支持**增量拉取**的录音器（正式录音器实现它）。
///
/// 翻译自 Python 的 <c>_make_chunk_puller</c>：优先用 <c>Recorder.drain_chunks()</c>，
/// 它只给**新增**的音频，不用每次都把全部音频重新拼一遍。
///
/// 只有支持 <c>snapshot()</c> 的录音器（比如测试里的假录音器）才退回差量拉取：
/// 记住已经喂过多少样本，只把新增的部分切出来。效果一样，只是每次仍然要拼一次全量。
/// </summary>
public interface IDrainableRecorder : IRecorder
{
    /// <summary>取走"上次取走之后新录到的音频"（原生采样率，int16 单声道）。</summary>
    short[] DrainChunks();
}
