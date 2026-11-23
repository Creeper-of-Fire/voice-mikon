using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace VmNext.Shell;

/// <summary>帧时间统计结果（毫秒）。</summary>
public sealed record FrameStats(
    double AverageMs,
    double MaxMs,
    double MinMs,
    int SampleCount,
    int Over20MsCount)
{
    /// <summary>超过 20ms（低于 ~50fps）的帧占比。</summary>
    public double Over20MsRatio => SampleCount == 0 ? 0 : (double)Over20MsCount / SampleCount;
}

/// <summary>
/// 帧时间记录器：用 <see cref="TopLevel.RequestAnimationFrame"/> 连续取帧，
/// 用相邻帧时间戳之差算出每帧间隔。
///
/// 注意：Avalonia 是按需渲染的 —— 没有动画时不会持续出帧，
/// 所以这个统计必须在"确实有东西在动"的时候测，否则样本会断断续续。
/// </summary>
public sealed class FrameTimeRecorder
{
    private readonly List<(double TimeMs, double DeltaMs)> _samples = new();
    private TopLevel? _topLevel;
    private TimeSpan _lastTimestamp;
    private bool _hasLast;
    private bool _running;
    private DateTime _startedAt = DateTime.Now;

    /// <summary>是否正在记录。</summary>
    public bool IsRunning => _running;

    /// <summary>开始记录帧间隔。</summary>
    public void Start(TopLevel topLevel)
    {
        _topLevel = topLevel;
        _samples.Clear();
        _hasLast = false;
        _running = true;
        _startedAt = DateTime.Now;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    /// <summary>停止记录。</summary>
    public void Stop() => _running = false;

    /// <summary>当前相对起始的毫秒数。</summary>
    public double NowMs() => (DateTime.Now - _startedAt).TotalMilliseconds;

    private void OnFrame(TimeSpan timestamp)
    {
        if (!_running || _topLevel is null)
        {
            return;
        }

        if (_hasLast)
        {
            double delta = (timestamp - _lastTimestamp).TotalMilliseconds;
            // 过滤掉异常值（切后台/断点调试会返回巨大间隔）
            if (delta > 0 && delta < 5000)
            {
                _samples.Add((NowMs(), delta));
            }
        }

        _lastTimestamp = timestamp;
        _hasLast = true;
        _topLevel.RequestAnimationFrame(OnFrame);
    }

    /// <summary>统计结果（全部样本）。</summary>
    public FrameStats GetStats() => GetStats(null, null);

    /// <summary>
    /// 统计指定时间窗口内的帧间隔。
    /// </summary>
    /// <param name="fromMs">起始毫秒（相对开始），null 表示不限。</param>
    /// <param name="toMs">结束毫秒（相对开始），null 表示不限。</param>
    public FrameStats GetStats(double? fromMs, double? toMs)
    {
        var values = new List<double>();
        foreach (var (time, delta) in _samples)
        {
            if (fromMs.HasValue && time < fromMs.Value)
            {
                continue;
            }

            if (toMs.HasValue && time > toMs.Value)
            {
                continue;
            }

            values.Add(delta);
        }

        if (values.Count == 0)
        {
            return new FrameStats(0, 0, 0, 0, 0);
        }

        double sum = 0;
        double max = double.MinValue;
        double min = double.MaxValue;
        int over20 = 0;

        foreach (double v in values)
        {
            sum += v;
            if (v > max)
            {
                max = v;
            }

            if (v < min)
            {
                min = v;
            }

            if (v > 20)
            {
                over20++;
            }
        }

        return new FrameStats(sum / values.Count, max, min, values.Count, over20);
    }
}
