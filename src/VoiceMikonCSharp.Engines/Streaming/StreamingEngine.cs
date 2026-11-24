using System;
using System.Collections.Generic;

namespace VoiceMikonCSharp.Engines.Streaming;

/// <summary>
/// 流式识别引擎：统一接口 + 两个实现。
///
/// 这个模块是“预览 / 最终双插槽”重构的核心。
///
/// **只有一个引擎接口**（只有这一种形态）：
///
///     Feed(chunk)          持续喂音频 chunk；实现内部可在任意时刻回调 partial(text)
///     Close() -&gt; final  结束,返回最终文本
///
/// 预览位和最终位各插一颗引擎,能力差异全在实现里,接口不做任何特殊化：
///
///     预览位 —— 要“快”：<see cref="SentencePreviewEngine"/>,切一句认一句,整体替换预览文字
///     最终位 —— 要“准”：<see cref="FullPassEngine"/>,一次性全量识别
///
/// **任何引擎都能插任何位** —— 插槽只决定“这个引擎的输出给谁用”,
/// 不决定引擎本身的能力。默认组合是“预览位分句 + 最终位整段”,
/// 整段引擎插预览位(旧行为)也照样能跑(见 <see cref="FullPassEngine"/> 的刷新策略)。
///
/// 为什么分句不一致只允许污染预览层:分句识别天然会断在奇怪的地方,
/// 一句话被切成两半、标点乱跳都是常态。但最终注入的文字必须干净 ——
/// 所以最终位固定整段引擎,预览层怎么马虎都不影响它。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/streaming.py。
/// </summary>
public abstract class StreamingEngine
{
    // feed 和 close 都要碰内部状态,加锁防止并发调用撞车
    // (比如预览线程还在识别,主线程就 close 了)。
    private readonly object _lock = new();

    /// <param name="engine">底层的整段识别器（任何有 <c>Transcribe(audio, sample_rate)</c> 的对象）。</param>
    /// <param name="sampleRate">
    /// 喂进来的音频的采样率 —— 必须是**音频的实际采样率**（设备原生率），
    /// 重采样由后端内部做。
    /// </param>
    protected StreamingEngine(IAsrBackend engine, int sampleRate = 16000)
    {
        Backend = engine ?? throw new ArgumentNullException(nameof(engine));
        SampleRate = sampleRate == 0 ? 16000 : sampleRate;
    }

    /// <summary>底层的整段识别器。</summary>
    protected IAsrBackend Backend { get; }

    /// <summary>喂进来的音频的采样率。</summary>
    public int SampleRate { get; }

    /// <summary>引擎是否已经关闭。</summary>
    public bool Closed { get; private set; }

    /// <summary>close() 的最终结果（幂等缓存）。</summary>
    protected string? Final { get; set; }

    /// <summary>
    /// 喂一段音频进来。
    ///
    /// <paramref name="channels"/> 是声道数（&gt;1 时按**交错**顺序混成单声道）。
    /// 实现内部可以在任意时刻回调 partial 报告进展。
    /// </summary>
    public abstract void Feed(short[] chunk, int channels = 1);

    /// <summary>
    /// 结束,返回最终文本。
    ///
    /// 必须**幂等**:重复调用返回同一个结果,不重复识别。
    /// </summary>
    public abstract string Close();

    /// <summary>把引擎标记为已关闭（子类在 close 流程里调用）。</summary>
    protected void MarkClosed() => Closed = true;

    /// <summary>取锁（子类在 Feed / Close 里用）。</summary>
    protected object SyncRoot => _lock;

    /// <summary>
    /// 把任意形态的音频块统一成单声道。
    ///
    /// 翻译自 streaming.py 的 <c>StreamingEngine._as_audio</c>。
    ///
    /// 多声道混成单声道。注意整数刻度要还原:
    /// mean 会把 int16 提升成 float64,之后再当成“已归一化的浮点”
    /// 就会把 ±32768 的样本当成 ±1 的音频,直接毁掉。
    ///
    /// ⚠ 与 numpy 的差别: numpy 的 <c>astype(np.int16)</c> 越界是**回绕**
    /// （+32768 会变成 -32768），C# 这里用**饱和**（clamp 到 ±32767/32768）——
    /// 全幅正样本混音后不会出现负尖峰。差的是一个极端边界情况。
    /// </summary>
    protected static short[]? AsAudio(short[]? chunk, int channels)
    {
        if (chunk is null || chunk.Length == 0)
        {
            return null;
        }

        if (channels <= 1)
        {
            return chunk;
        }

        int frames = chunk.Length / channels;
        if (frames <= 0)
        {
            return null;
        }

        var mono = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            long sum = 0;
            int baseIdx = i * channels;
            for (int c = 0; c < channels; c++)
            {
                sum += chunk[baseIdx + c];
            }

            // 整数除法向零取整（与 numpy 的 astype 截断同口径）
            long avg = sum / channels;
            mono[i] = (short)Math.Clamp(avg, short.MinValue, short.MaxValue);
        }

        return mono;
    }

    /// <summary>拼接多个音频块。</summary>
    protected static short[] Concat(IReadOnlyList<short[]> parts)
    {
        if (parts.Count == 0)
        {
            return Array.Empty<short>();
        }

        if (parts.Count == 1)
        {
            return parts[0];
        }

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

    /// <summary>取一段切片（避免 LINQ 的开销）。</summary>
    protected static short[] Slice(short[] source, int start, int length)
    {
        if (length <= 0)
        {
            return Array.Empty<short>();
        }

        var result = new short[length];
        Array.Copy(source, start, result, 0, length);
        return result;
    }
}
