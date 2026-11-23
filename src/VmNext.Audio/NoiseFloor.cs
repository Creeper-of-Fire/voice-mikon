namespace VmNext.Audio;

/// <summary>
/// 噪声底跟踪：用滑动窗口的低分位数估计环境底噪。
///
/// 翻译自 voice-mikon/src/voice_mikon/audio/noise.py 的 NoiseFloorTracker。
///
/// 为什么需要它：环境底噪是"测出来的"，不是"定出来的"。
/// 旧做法是给三处（电平显示门 / VAD 切句门 / 前端死区）各配一个按**某次录音**
/// 标定的绝对 RMS 常量。2026-10-29 按当时的 USB 麦标定（底噪 -63~-60 dBFS），
/// 换环境后底噪抬到 -52.6 dBFS，三处阈值同一天全部失效。固定值在环境面前
/// 活不过一次换麦/换房间。
///
/// 怎么做：滚动窗口（默认 3 秒）收最近一串 RMS，取**低分位数 p10**。
/// * 为什么是 p10 而不是 min：min 会被偶发脉冲压低（一次 USB 咔哒的静音帧把估计
///   拖得过低，门就漏底噪）；p10 意味着"窗口里最安静的那 10% 帧"，对孤立脉冲免疫。
/// * 样本不足（min_samples 之前）返回 null：调用方自行兜底 —— 语义是"还不知道
///   环境有多吵"，而不是"环境很安静"。冷启动那不到一秒宁可把底噪包进句子。
///
/// 这个类是**纯估计**，不含任何门系数：开门/关门用几倍底噪，是消费者
/// （显示/切句）各自的策略，不在这里定。
/// </summary>
public sealed class NoiseFloorTracker
{
    private readonly Queue<double> _samples = new();
    private readonly int _maxLen;
    private readonly int _minSamples;

    /// <summary>
    /// 构造一个跟踪器。
    ///
    /// window：窗口按**帧数**算 —— 调用方自己换算（20ms 帧给 150、64ms 帧给 48，
    /// 都是约 3 秒）。
    /// minSamples：攒够这么多帧才开始给估计。
    /// </summary>
    public NoiseFloorTracker(int window = 150, int minSamples = 50)
    {
        _maxLen = Math.Max(2, window);
        _minSamples = Math.Max(1, Math.Min(minSamples, _maxLen));
    }

    /// <summary>喂一帧 RMS。只收有限值 —— NaN/inf 会把分位数毒死。</summary>
    public void Update(double rms)
    {
        if (double.IsNaN(rms) || double.IsInfinity(rms))
        {
            return;
        }

        lock (_samples)
        {
            _samples.Enqueue(rms);
            while (_samples.Count > _maxLen)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>
    /// 当前底噪估计；样本不足返回 null（不是 0 —— 0 是"很安静"，None 是"还不知道"，
    /// 两者对调用方的意义完全不同）。
    /// </summary>
    public double? Floor()
    {
        double[] snapshot;
        lock (_samples)
        {
            if (_samples.Count < _minSamples)
            {
                return null;
            }

            snapshot = _samples.ToArray();
        }

        return Percentile(snapshot, 10.0);
    }

    /// <summary>
    /// 线性插值分位数（与 numpy.percentile 的默认 linear 模式一致）。
    /// </summary>
    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return 0.0;
        }

        Array.Sort(values);

        if (values.Length == 1)
        {
            return values[0];
        }

        double pos = (percentile / 100.0) * (values.Length - 1);
        int lower = (int)Math.Floor(pos);
        int upper = (int)Math.Ceiling(pos);

        if (lower == upper)
        {
            return values[lower];
        }

        double frac = pos - lower;
        return values[lower] + ((values[upper] - values[lower]) * frac);
    }
}
