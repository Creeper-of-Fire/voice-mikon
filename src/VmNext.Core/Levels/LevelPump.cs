using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace VmNext.Core.Levels;

/// <summary>
/// 电平数据源（录音器实现它）。
///
/// Core 不认识音频设备 —— 只认"谁能给我电平序列、底噪估计和计时"。
/// 这样电平泵可以脱离音频层单测。
/// </summary>
public interface ILevelSource
{
    /// <summary>最近的音量电平序列（线性 RMS，0..1 量纲）。</summary>
    IReadOnlyList<double> Levels { get; }

    /// <summary>环境底噪估计（线性 RMS）；样本不足时是 null（不是 0）。</summary>
    double? NoiseFloor { get; }

    /// <summary>已录音时长（秒）。</summary>
    double Elapsed { get; }
}

/// <summary>一帧电平（已映射到 0..1）+ 计时。</summary>
public sealed record LevelFrame(IReadOnlyList<double> Levels, double Elapsed);

/// <summary>
/// 电平映射（dBFS → 0..1）与 30fps 电平推送。
///
/// 翻译自两处：
/// * voice-mikon/src/voice_mikon/core/levels.py —— dbfs_to_unit / level_gate 两个纯函数
/// * voice-mikon/src/voice_mikon/core/controller.py —— 30fps 电平泵（LEVEL_PUSH_INTERVAL）
///
/// 规则不许改（实测教训）：
/// * 推给前端的必须是 **DbfsToUnit** 的输出（对数映射 + 底噪门），不是线性 RMS；
/// * **绝不能**按窗口内最大值归一化 —— 归一化之后电平条永远是满的，用户就没法
///   从条的高低看出"我说话声音大不大"，那条条就只剩装饰作用。
///
/// 关于"底噪门"：门限不是常数，而是**底噪估计 × 系数**（LevelGateFactor）。
/// 固定门限是按"某次录音"标定的，环境一变就被整个穿过（2026-11 实测：换环境后
/// 底噪抬高 9 dB，固定门被穿过，安静时电平条 65% 满格）。门随底噪走才是对的。
/// </summary>
public sealed class LevelPump : IDisposable
{
    /// <summary>电平映射区间：dBFS → 0..1。</summary>
    public const double DbFloor = -70.0;

    public const double DbCeil = 0.0;

    /// <summary>
    /// 显示门的动态门限 = max(LevelGateMin, 底噪估计 × LevelGateFactor)。
    ///
    /// 系数 1.5：比底噪高 50% 以内都算底噪（门限附近的过渡帧不闪）。
    /// 绝对下限 1e-5 只是兜底：防止"底噪估计接近 0"（近哑设备）时门坍缩成 0，
    /// 让任何数值都算"有声"。真正的静音判零由 DbfsToUnit 自带的 rms<=1e-6 承担，
    /// 两层各管各的。
    /// </summary>
    public const double LevelGateFactor = 1.5;

    public const double LevelGateMin = 1e-5;

    /// <summary>电平推送频率：30fps（前端电平条按这个节奏画）。</summary>
    public const double PushInterval = 1.0 / 30.0;

    private static readonly Logger _log = VmNext.Core.Log.GetLogger("core.levels");

    private readonly object _lock = new();
    private readonly List<Action<LevelFrame>> _listeners = new();
    private readonly ILevelSource? _source;

    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>电平泵（无数据源时只提供静态映射函数）。</summary>
    public LevelPump(ILevelSource? source = null) => _source = source;

    /// <summary>订阅电平（录音期间 30fps 推一次）。</summary>
    public void Subscribe(Action<LevelFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_lock)
        {
            _listeners.Add(callback);
        }
    }

    /// <summary>启动电平泵（幂等）。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_thread is not null)
            {
                return;
            }

            _stop = false;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "core-levels",
            };
        }

        _thread!.Start();
    }

    /// <summary>停止电平泵（幂等）。</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_lock)
        {
            _stop = true;
            thread = _thread;
            _thread = null;
        }

        if (thread is not null && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>
    /// 把线性 RMS 映射到 0..1（对数域）。
    ///
    /// DbFloor dBFS 映射到 0，0 dBFS 映射到 1，中间线性插值。
    /// 对数域而不是线性域，是因为人耳对 loudness 的感知接近对数，线性映射会让
    /// 正常说话的音量全部挤在 0.05 以下。
    ///
    /// floorRms 是**噪声门**：低于它的直接判 0。门限不是常数 —— 由调用方用噪声底
    /// 估计动态算（见 <see cref="LevelGate"/>）：固定门限是按"某次录音"标定的，
    /// 环境一变就失效（2026-11 实测：换环境后底噪抬高 9 dB，固定门被整个穿过，
    /// 安静时电平条 65% 满格）。
    /// </summary>
    public static double DbfsToUnit(double rms, double floorRms = 0.0)
    {
        if (rms <= floorRms || rms <= 1e-6)
        {
            return 0.0;
        }

        double db = 20.0 * Math.Log10(rms);
        if (db <= DbFloor)
        {
            return 0.0;
        }

        if (db >= DbCeil)
        {
            return 1.0;
        }

        return (db - DbFloor) / (DbCeil - DbFloor);
    }

    /// <summary>
    /// 按底噪估计算出显示门限。
    ///
    /// floorRms 是录音器 NoiseFloor 的读数（线性 RMS）；**样本不足时它是 null**
    /// （底噪估计还没收敛），这时退回绝对下限 <see cref="LevelGateMin"/> ——
    /// 宁可让头几帧底噪照常显示，也不能把轻声开头错杀成 0。
    /// </summary>
    public static double LevelGate(double? floorRms)
        => floorRms is null ? LevelGateMin : Math.Max(LevelGateMin, floorRms.Value * LevelGateFactor);

    /// <summary>释放（停止泵）。</summary>
    public void Dispose() => Stop();

    /// <summary>泵的主循环：每 1/30 秒采一次电平并推给订阅者。</summary>
    private void Loop()
    {
        long next = Stopwatch.GetTimestamp();

        while (!_stop)
        {
            try
            {
                LevelFrame? frame = Map();
                if (frame is not null && frame.Levels.Count > 0)
                {
                    Publish(frame);
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"电平推送异常: {ex.Message}");
            }

            // 按**固定节拍**推进（deadline 补偿），而不是"睡 interval 再来"：
            // 后者会把每次采集的耗时累加进去，节奏会越走越慢。
            next += (long)(PushInterval * Stopwatch.Frequency);
            double waitSeconds = (next - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;

            if (waitSeconds > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));
            }
            else
            {
                // 落后太多（机器卡了）就重新对齐，不要疯狂补帧
                next = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>采一次电平并映射到 0..1（门限随底噪走）。</summary>
    private LevelFrame? Map()
    {
        ILevelSource? source = _source;
        if (source is null)
        {
            return null;
        }

        IReadOnlyList<double> raw = source.Levels;
        if (raw.Count == 0)
        {
            return null;
        }

        double? floor = source.NoiseFloor;
        double gate = LevelGate(floor);

        var mapped = new double[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            mapped[i] = DbfsToUnit(raw[i], gate);
        }

        return new LevelFrame(mapped, source.Elapsed);
    }

    /// <summary>推给订阅者（一个炸了不影响其它）。</summary>
    private void Publish(LevelFrame frame)
    {
        Action<LevelFrame>[] targets;
        lock (_lock)
        {
            targets = _listeners.ToArray();
        }

        foreach (Action<LevelFrame> cb in targets)
        {
            try
            {
                cb(frame);
            }
            catch (Exception ex)
            {
                _log.Debug($"电平订阅者异常: {ex.Message}");
            }
        }
    }
}
