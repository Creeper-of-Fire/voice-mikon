using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace VmNext.Shell;

/// <summary>空闲期资源占用指标。</summary>
/// <param name="AvgCpuPercent">平均 CPU 占用（%，已按逻辑处理器数归一）。</param>
/// <param name="ElapsedMs">测量时长（毫秒）。</param>
/// <param name="TotalCpuMs">窗口内累计 CPU 时间（毫秒）。</param>
/// <param name="WorkingSetEnv">Environment.WorkingSet（字节）。</param>
/// <param name="WorkingSetProc">Process.WorkingSet64（字节）。</param>
/// <param name="PrivateBytes">私有内存（字节）。</param>
/// <param name="ProcessorCount">逻辑处理器数。</param>
public sealed record IdleMetrics(
    double AvgCpuPercent,
    double ElapsedMs,
    long TotalCpuMs,
    long WorkingSetEnv,
    long WorkingSetProc,
    long PrivateBytes,
    int ProcessorCount);

/// <summary>进程资源指标测量。</summary>
public static class ProcessMetrics
{
    /// <summary>
    /// 静置一段时间并测量 CPU / 工作集。
    /// 用 await 而不是 Thread.Sleep：消息泵继续跑，才是"活着但没事干"的真实状态。
    /// </summary>
    public static async Task<IdleMetrics> MeasureIdleAsync(TimeSpan window)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();

        var cpuStart = process.TotalProcessorTime;
        long wsBefore = process.WorkingSet64;
        var stopwatch = Stopwatch.StartNew();

        await Task.Delay(window).ConfigureAwait(true);

        stopwatch.Stop();
        process.Refresh();
        var cpuUsed = process.TotalProcessorTime - cpuStart;

        int cores = Environment.ProcessorCount;
        double avgCpu = stopwatch.Elapsed.TotalMilliseconds <= 0
            ? 0
            : cpuUsed.TotalMilliseconds / stopwatch.Elapsed.TotalMilliseconds / cores * 100.0;

        return new IdleMetrics(
            avgCpu,
            stopwatch.Elapsed.TotalMilliseconds,
            (long)cpuUsed.TotalMilliseconds,
            Environment.WorkingSet,
            wsBefore,
            process.PrivateMemorySize64,
            cores);
    }
}
