using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VmNext.Engines.Streaming;

/// <summary>
/// 整段引擎:feed 只攒 chunk,close() 时一次性全量识别。
///
/// 包装现有的 <c>Transcribe(audio, rate)</c>,
/// **最终位用的就是它** —— 所以最终注入的文字和以前完全一样,
/// 一个字都不会因为预览层改了而变。
///
/// 插在预览位时(旧行为的回退)会周期性地把累积音频整体重识别一次
/// 并回调 partial:因为预览位要的是“过程中的文字”,
/// 而整段引擎只会在 close() 时给最终结果 —— 那时预览早就结束了。
/// 给不给 partial 回调就是这两种用法的区别:
///   - 不给(最终位):只在 close() 识别一次
///   - 给(预览位):按节奏重识别并回调
/// </summary>
public sealed class FullPassEngine : StreamingEngine
{
    /// <summary>
    /// 整段引擎插在预览位时的重识别节奏（秒,墙钟）与自适应系数:
    /// 实际间隔 = max(节奏, 上次推理耗时 × 系数)——
    /// 推理越慢间隔越长,避免请求越积越多(重喂全量音频的开销随长度线性涨)。
    /// </summary>
    public const double RefreshSeconds = 1.5;

    /// <summary>自适应系数（上次推理耗时 × 该系数）。</summary>
    public const double RefreshFactor = 1.2;

    /// <summary>
    /// 预览位整段重识别之前至少要有的音频长度(秒):太短的片段识别不出东西。
    /// </summary>
    public const double MinAudioSeconds = 0.5;

    private readonly Action<string>? _partial;
    private readonly double _refreshSeconds;
    private readonly double _refreshFactor;
    private readonly int _minSamples;
    private readonly Func<double> _clock;

    private readonly List<short[]> _parts = new();
    private int _totalSamples;
    private double _lastRefresh;
    private double _lastCost;

    /// <param name="engine">底层识别器。</param>
    /// <param name="sampleRate">音频实际采样率。</param>
    /// <param name="partial">预览回调（给 null = 最终位，只在 close() 识别一次）。</param>
    /// <param name="refreshSeconds">重识别节奏（秒）。</param>
    /// <param name="refreshFactor">自适应系数。</param>
    /// <param name="minAudioSeconds">重识别前至少要有的音频长度（秒）。</param>
    /// <param name="clock">单调时钟（秒）；测试可注入假时钟。</param>
    public FullPassEngine(
        IAsrBackend engine,
        int sampleRate = 16000,
        Action<string>? partial = null,
        double refreshSeconds = RefreshSeconds,
        double refreshFactor = RefreshFactor,
        double minAudioSeconds = MinAudioSeconds,
        Func<double>? clock = null)
        : base(engine, sampleRate)
    {
        _partial = partial;
        _refreshSeconds = refreshSeconds;
        _refreshFactor = refreshFactor;
        _minSamples = Math.Max(1, (int)(minAudioSeconds * SampleRate));
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    /// <summary>累计的样本数（诊断用）。</summary>
    public int TotalSamples => _totalSamples;

    public override void Feed(short[] chunk, int channels = 1)
    {
        lock (SyncRoot)
        {
            if (Closed)
            {
                return;
            }

            short[]? data = AsAudio(chunk, channels);
            if (data is null)
            {
                return;
            }

            _parts.Add(data);
            _totalSamples += data.Length;
            RefreshIfDue();
        }
    }

    public override string Close()
    {
        lock (SyncRoot)
        {
            if (Final is not null)
            {
                return Final;
            }

            MarkClosed();
            if (_parts.Count == 0)
            {
                Final = string.Empty;
                return Final;
            }

            short[] audio = Concat(_parts);
            _parts.Clear();
            try
            {
                Final = Backend.Transcribe(audio, SampleRate) ?? string.Empty;
            }
            catch (Exception)
            {
                // 失败也要把状态定死,免得 close() 被重试一次再跑一遍推理
                Final = string.Empty;
                throw;
            }

            return Final;
        }
    }

    /// <summary>
    /// 把累积音频整体重识别一次,并回调 partial(旧行为)。
    ///
    /// 只在给了 partial 回调时才有意义 —— 最终位不给回调,
    /// 它只在 close() 时识别一次。
    /// </summary>
    private void RefreshIfDue()
    {
        if (_partial is null || _refreshSeconds <= 0)
        {
            return;
        }

        if (_totalSamples < _minSamples)
        {
            // 音频还太短:识别不出东西,白跑一次推理
            return;
        }

        double now = _clock();
        // 间隔自适应:推理越慢,间隔拉得越长,避免越积越多
        double interval = Math.Max(_refreshSeconds, _lastCost * _refreshFactor);
        if (now - _lastRefresh < interval)
        {
            return;
        }

        short[] audio = Concat(_parts);
        double t0 = _clock();
        string text;
        try
        {
            text = Backend.Transcribe(audio, SampleRate) ?? string.Empty;
        }
        catch (Exception ex)
        {
            // 识别失败(引擎异常 / 切换模型时旧模型被换掉)只跳过这一轮。
            // 保持 debug:这是**旧回退路径**(默认走分句引擎),而且每 1.5 秒
            // 就会失败一次 —— 升到 WARNING 只会刷屏,分句路径的日志才有用。
            Trace.WriteLine($"[asr.stream] 整段预览识别失败: {ex.Message}");
            return;
        }

        _lastCost = _clock() - t0;
        _lastRefresh = _clock();
        if (text.Trim().Length > 0)
        {
            Notify(text);
        }
    }

    private void Notify(string text)
    {
        if (_partial is null)
        {
            return;
        }

        try
        {
            _partial(text);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[asr.stream] partial 回调异常: {ex.Message}");
        }
    }
}
