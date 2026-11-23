using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace VmNext.AutoCheck;

/// <summary>子进程一次运行的完整结果。</summary>
public sealed class RunResult
{
    /// <summary>命令行（用于报告复现）。</summary>
    public string CommandLine { get; init; } = string.Empty;

    /// <summary>进程启动到收到 OPENED 标记的毫秒数；没收到则为 -1。</summary>
    public double OpenedMs { get; set; } = -1;

    /// <summary>进程总耗时（毫秒）。</summary>
    public double TotalMs { get; set; }

    /// <summary>进程退出码。</summary>
    public int ExitCode { get; set; }

    /// <summary>标准输出所有行。</summary>
    public List<string> StdOut { get; } = new();

    /// <summary>标准错误所有行。</summary>
    public List<string> StdErr { get; } = new();

    /// <summary>解析出的检查结果：id → (是否通过, 详情)。</summary>
    public Dictionary<string, (bool Passed, string Detail)> Checks { get; } = new();

    /// <summary>解析出的原始数据：tag → 字段数组。</summary>
    public Dictionary<string, string[]> Raws { get; } = new();

    /// <summary>标记行：name → value。</summary>
    public Dictionary<string, string> Marks { get; } = new();

    /// <summary>取一个原始数据字段。</summary>
    public string? Raw(string tag, int index)
        => Raws.TryGetValue(tag, out var fields) && index < fields.Length ? fields[index] : null;

    /// <summary>取原始数据字段并转成 double。</summary>
    public double RawDouble(string tag, int index)
        => double.TryParse(Raw(tag, index), out var v) ? v : double.NaN;
}

/// <summary>子进程运行器：负责启动、逐行捕获输出、解析机器可读标记。</summary>
public static class ProcessRunner
{
    /// <summary>
    /// 运行一个子进程并等待它结束。
    /// 标记格式由 Shell 的 ProbeOutput 约定：
    ///   @@CHECK|id|PASS/FAIL|详情
    ///   @@MARK|名字|值
    ///   @@TAG|值1|值2...
    /// </summary>
    public static RunResult Run(string fileName, IEnumerable<string> arguments, string workingDirectory, int timeoutSeconds = 180)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var result = new RunResult
        {
            CommandLine = $"\"{fileName}\" {string.Join(' ', arguments)}",
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
            ParseLine(result, e.Data, stopwatch.Elapsed.TotalMilliseconds);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程可能刚好已经退出
            }

            result.StdErr.Add($"[AutoCheck] 进程超时（{timeoutSeconds}s）已强杀");
        }

        // 给异步读取器一点时间把剩余输出刷出来
        process.WaitForExit();
        Thread.Sleep(200);

        result.TotalMs = stopwatch.Elapsed.TotalMilliseconds;
        result.ExitCode = process.HasExited ? process.ExitCode : -1;
        result.StdOut.AddRange(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        result.StdErr.AddRange(stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));

        return result;
    }

    /// <summary>解析一行机器可读标记。</summary>
    private static void ParseLine(RunResult result, string line, double elapsedMs)
    {
        if (!line.StartsWith("@@", StringComparison.Ordinal))
        {
            return;
        }

        string[] parts = line.Split('|');
        string tag = parts[0].TrimStart('@');

        switch (tag)
        {
            case "CHECK" when parts.Length >= 4:
                result.Checks[parts[1]] = (parts[2] == "PASS", string.Join('|', parts[3..]));
                break;

            case "MARK" when parts.Length >= 3:
                result.Marks[parts[1]] = string.Join('|', parts[2..]);
                if (parts[1] == "OPENED" && result.OpenedMs < 0)
                {
                    result.OpenedMs = elapsedMs;
                }

                break;

            default:
                result.Raws[tag] = parts[1..];
                break;
        }
    }
}
