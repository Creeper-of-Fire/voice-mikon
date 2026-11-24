using System;
using System.Diagnostics;

namespace VoiceMikonCSharp.Engines.Vad;

/// <summary>
/// 语音/静音判定层（VAD）：只回答“这 一帧是不是人声”，不管切句。
///
/// **这个模块的边界很重要**：它只做**判定**，不做**切句**。
///
/// 切句状态机（连续静音多久断句、单句上限、最短句长）在
/// <see cref="Streaming.SentencePreviewEngine"/>，那里一行都没动 ——
/// 这里换掉的只是“这 一帧是不是人声”的答案从哪来。
///
/// 两种实现，同一个接口：
///
///     <see cref="EnergyVadDetector"/>  能量域判定：底噪估计 + 滞回双门（旧实现，原样保留）
///     <see cref="SileroVadDetector"/>   Silero VAD 神经网络判定（见 SileroVad.cs）
///
/// **为什么要有这一层**：能量法只看 RMS 高低，分不清“有人小声说话”和
/// “环境噪声大一点” —— 换环境/换麦之后底噪抬升，固定门限就被穿透过，
/// “连续静音 0.3s”的判定永不触发。Silero 是训练出来的神经网络判定，
/// 分得清人声和噪声。但神经网络会加载失败、会有不支持的采样率，
/// 所以能量法**保留**作为降级路径，而不是删掉。
///
/// **帧长由判定层决定**（<see cref="FrameSamples"/>）：Silero 要求固定的 32ms 窗
/// （8k→256 / 16k→512 / 32k→1024 / 48k→1536 样本），能量法则继续用
/// 20ms。两者的帧长不同，但切句阈值一律按**样本数**算，
/// 所以切句行为不随判定层变化。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/vad.py。
/// </summary>
public abstract class VoiceDetector
{
    /// <summary>
    /// 本判定器的一帧多少样本。
    ///
    /// 帧长是**判定层的属性**，不是切句层的：Silero 要求固定 32ms 窗，
    /// 能量法用 20ms。切句阈值一律按样本数算，两者互不影响。
    /// </summary>
    public abstract int FrameSamples { get; }

    /// <summary>
    /// 是否要求每帧长度**精确**等于 <see cref="FrameSamples"/>。
    ///
    /// True  = 判定器只吃固定长度的窗（Silero），引擎必须跨块拼帧，
    ///         不足一窗的尾巴留到下一块。
    /// False = 允许短尾帧（能量法），保持旧行为：按块切，块尾剩多少算多少。
    /// </summary>
    public virtual bool RequiresExactFrames => false;

    /// <summary>
    /// 每轮录音开始时重置内部状态。
    ///
    /// Silero 背后是 LSTM，带着上一轮的状态开会判歪 —— 必须重置。
    /// 能量法的滞回状态同理（上一轮结束时正在说话，不该影响下一轮）。
    /// </summary>
    public abstract void Reset();

    /// <summary>这一帧是不是人声。</summary>
    /// <remarks>
    /// 判定层内部自己做**滞回**（能量法的双门 / Silero 的双阈值），
    /// 所以切句层拿到的是一个稳定的状态，而不是每帧抖动的原始值。
    /// </remarks>
    public abstract bool IsSpeech(short[] frame);

    /// <summary>当前滞回状态（诊断用）。</summary>
    public abstract bool InSpeech { get; }
}

/// <summary>
/// 能量域判定：底噪估计 + 滞回双门。
///
/// 这是**旧实现，原样保留** —— 从 engines/streaming.py 搬过来，
/// 阈值、滞回方向、底噪口径一个都没改。它现在的身份是
/// **Silero 不可用时的降级路径**（模型加载失败 / 采样率不支持）。
/// </summary>
public sealed class EnergyVadDetector : VoiceDetector
{
    /// <summary>开门：超过底噪 2.5 倍才算“说话了”。</summary>
    public const double GateOpenFactor = 2.5;

    /// <summary>关门：低于底噪 1.5 倍才算“停了”。</summary>
    public const double GateCloseFactor = 1.5;

    /// <summary>估计不可用时的兜底门（≈ -80 dBFS）。</summary>
    public const double GateMin = 1e-4;

    private readonly int _sampleRate;
    private readonly Func<double?>? _noiseFloor;
    private readonly int _frameSamples;
    private bool _inSpeech;

    /// <param name="sampleRate">音频采样率。</param>
    /// <param name="noiseFloor">
    /// 底噪估计的供给口：每帧问一次当前底噪（null = 还没收敛）。
    /// 不直接依赖 Recorder —— 依赖的是“谁来供数”这个窄接口，测试里
    /// 可以给个固定值的 lambda，行为完全可复现。
    /// 只有能量法判定器用它（Silero 不看底噪）。
    /// </param>
    /// <param name="frameMs">判定帧长（毫秒），默认 <see cref="VadConstants.FrameMs"/>。</param>
    public EnergyVadDetector(int sampleRate = 16000, Func<double?>? noiseFloor = null, double frameMs = VadConstants.FrameMs)
    {
        _sampleRate = sampleRate == 0 ? 16000 : sampleRate;
        _noiseFloor = noiseFloor;
        _frameSamples = Math.Max(1, (int)(frameMs * _sampleRate / 1000.0));
        _inSpeech = false;
    }

    public override int FrameSamples => _frameSamples;

    public override void Reset() => _inSpeech = false;

    /// <summary>
    /// 判定用**滞后双门**：
    ///
    ///   - 不在说话态：frame_rms 越过开门门（2.5×底噪）才进说话态；
    ///   - 说话态中：frame_rms 跌破关门门（1.5×底噪）才算停。
    /// </summary>
    public override bool IsSpeech(short[] frame)
    {
        double level = FrameRms.Compute(frame);
        (double gateOpen, double gateClose) = Gates();
        if (_inSpeech)
        {
            if (level < gateClose)
            {
                _inSpeech = false;
            }
        }
        else if (level > gateOpen)
        {
            _inSpeech = true;
        }

        return _inSpeech;
    }

    public override bool InSpeech => _inSpeech;

    /// <summary>当前（开门, 关门）门限。估计没收敛就双双退到兜底门。</summary>
    private (double Open, double Close) Gates()
    {
        double? floor = null;
        if (_noiseFloor is not null)
        {
            try
            {
                floor = _noiseFloor();
            }
            catch (Exception)
            {
                floor = null;
            }
        }

        if (floor is null || floor <= 0)
        {
            return (GateMin, GateMin);
        }

        return (
            Math.Max(GateMin, floor.Value * GateOpenFactor),
            Math.Max(GateMin, floor.Value * GateCloseFactor));
    }
}

/// <summary>
/// 判定层常量。
///
/// 翻译自 vad.py 的模块级常量。
/// </summary>
public static class VadConstants
{
    /// <summary>
    /// VAD 的分析帧长（毫秒）：断句必须在**固定粒度**上判断。
    /// 不这样的话，识别期间攒下的一大块音频会被当成一整段，
    /// 里面的句边界就全丢了。
    /// </summary>
    public const double FrameMs = 20.0;

    /// <summary>
    /// VAD 切句门**不再用固定绝对阈值**。
    ///
    /// 旧做法：按 2026-10-29 那次真机录音标定的固定门限，换环境后底噪抬升，
    /// 底噪 RMS **全部高过门限** —— “连续静音 0.3s”的判定永不触发，
    /// 句子只能等 20s 强切，预览基本不出字。固定阈值在环境面前活不过
    /// 一次换麦/换房间，已退役。
    ///
    /// 现做法：门限 = max(VAD_GATE_MIN, 底噪估计 × 系数)。估计还没收敛
    /// （null，冷启动头一秒）时门退到 VAD_GATE_MIN —— 宁可把底噪包进句子
    /// （识别不在乎句首尾一点底噪），也不能把轻声开头判成静音
    /// （旧注释的教训：0.01 的门曾把 94% 的语音帧判成静音，
    /// 句子碎成渣，预览大量缺字）。
    /// </summary>
    public const double GateOpenFactor = EnergyVadDetector.GateOpenFactor;

    /// <summary>关门：低于底噪 1.5 倍才算“停了”。</summary>
    public const double GateCloseFactor = EnergyVadDetector.GateCloseFactor;

    /// <summary>估计不可用时的兜底门（≈ -80 dBFS）。</summary>
    public const double GateMin = EnergyVadDetector.GateMin;

    /// <summary>
    /// 开关门限为什么要不一样（**滞后**）：门限附近若只有一个门，RMS 在
    /// 门附近抖一下，“有声/静音”就翻转一次，silence_run 反复清零，
    /// 一句话永远攒不出 0.3s 的连续静音 —— 该切的时候切不出来。
    /// 开门严（2.5×）、关门松（1.5×），中间留一条滞回带，状态才稳。
    /// </summary>
}

/// <summary>
/// 归一化 RMS（0..1）。
///
/// 翻译自 vad.py 的 <c>frame_rms</c>。
///
/// 用 chunk **自己**算 —— 便宜，也不依赖录音器的电平通道
/// （那条通道给界面画波形用，不该被识别链路依赖）。
/// </summary>
public static class FrameRms
{
    public static double Compute(short[] audio)
    {
        if (audio.Length == 0)
        {
            return 0.0;
        }

        double sum = 0.0;
        foreach (short s in audio)
        {
            double v = s / 32768.0;
            sum += v * v;
        }

        return Math.Sqrt(sum / audio.Length);
    }
}

/// <summary>
/// Silero VAD 用不了（模型缺失 / 加载失败 / 采样率不支持）。
///
/// 翻译自 vad.py 的 <c>SileroUnavailable</c>。
///
/// 带 <see cref="Reason"/> 是为了通知里说清楚**为什么**降级 —— 三条路径的
/// 排查方向完全不同。
/// </summary>
public class SileroUnavailable : Exception
{
    public SileroUnavailable(string reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>降级原因标识（model_missing / model_load_failed / unsupported_sample_rate / onnxruntime_missing）。</summary>
    public string Reason { get; }
}

/// <summary>
/// 造一颗判定器（带降级）。
///
/// 翻译自 vad.py 的 <c>make_vad_detector</c>。
///
/// <paramref name="vadKind"/> 为 null 时用 <paramref name="defaultKind"/>。
///
/// **降级策略**（三条路径都会回调 <paramref name="onFallback"/>，原因标识与 Python 版一致）：
///
///     模型文件缺失   → 静默降能量法
///     模型加载失败   → 静默降能量法
///     采样率不支持   → 静默降能量法
///
/// 显式配 <c>energy</c> **不通知** —— 那是用户自己主动选的，不是故障。
/// 降级永远不抛异常：判定层挂了不该让整个录音流程起不来。
/// </summary>
public static class VadFactory
{
    /// <summary>判定层可选值（与 config.json 的 <c>preview_vad</c> 一致）。</summary>
    public static readonly string[] Modes = { "silero", "energy" };

    public static VoiceDetector Make(
        string? vadKind,
        int sampleRate,
        Func<double?>? noiseFloor = null,
        Action<string, string>? onFallback = null,
        string defaultKind = "silero")
    {
        string kind = (vadKind ?? defaultKind ?? string.Empty).Trim().ToLowerInvariant();

        if (kind == "energy")
        {
            // 用户主动选的，不通知。
            return new EnergyVadDetector(sampleRate, noiseFloor);
        }

        if (!Array.Exists(Modes, m => m == kind))
        {
            onFallback?.Invoke(
                "invalid_config",
                $"preview_vad 取值 '{kind}' 不认识，预览断句已降级为能量判定");
            return new EnergyVadDetector(sampleRate, noiseFloor);
        }

        try
        {
            return new SileroVadDetector(sampleRate);
        }
        catch (SileroUnavailable ex)
        {
            onFallback?.Invoke(
                ex.Reason,
                $"Silero VAD 不可用({ex.Message})，预览断句已降级为能量判定");
            Trace.WriteLine($"[asr.vad] Silero 不可用: {ex.Message}");
            return new EnergyVadDetector(sampleRate, noiseFloor);
        }
    }
}
