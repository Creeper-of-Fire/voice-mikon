using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace VmNext.App.Tests;

/// <summary>
/// 命令行入口的测试：<c>--headless</c> 自检。
///
/// 跑的是**真的子进程**（不是直接调函数）：退出码是命令行契约的一部分，
/// 必须在进程边界上验。
/// </summary>
public class HeadlessCliTests
{
    /// <summary>--headless：引擎就绪后干净退出，退出码 0。</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void headless_引擎就绪后退出码0()
    {
        string exe = RequireAppExecutable();

        (int code, string output) = Run(exe, "--headless", "--mock", "--timeout", "60");

        Assert.True(
            output.Contains("引擎就绪", StringComparison.Ordinal),
            $"输出里没有'引擎就绪'：\n{output}");

        Assert.Equal(0, code);
    }

    /// <summary>--timeout 生效：等不到引擎就绪就超时退出（退出码非 0）。</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void headless_超时参数生效()
    {
        string exe = RequireAppExecutable();

        // timeout=0：窗口是 0 秒，等不到就绪 → 超时退出
        (int code, string output) = Run(exe, "--headless", "--mock", "--timeout", "0");

        Assert.True(
            output.Contains("超时", StringComparison.Ordinal),
            $"输出里没有'超时'：\n{output}");

        Assert.NotEqual(0, code);
    }

    /// <summary>找不到构建出来的可执行文件就跳过（没构建过 App 时不该红）。</summary>
    private static string RequireAppExecutable()
    {
        string? exe = FindAppExecutable();
        if (exe is null)
        {
            Assert.Skip("没找到 VmNext.App.exe（先 dotnet build src/VmNext.App）");
        }

        return exe;
    }

    /// <summary>从测试程序集所在目录往上找仓库根，再找构建产物。</summary>
    private static string? FindAppExecutable()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VmNext.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        foreach (string configuration in new[] { "Debug", "Release" })
        {
            string exe = Path.Combine(
                dir.FullName, "src", "VmNext.App", "bin", configuration, "net10.0", "VmNext.App.exe");

            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    /// <summary>跑一次子进程，返回（退出码, 合并输出）。</summary>
    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,

            // 子进程在输出被重定向时固定用 UTF-8（见 Program.Main），这里对得上。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"起不来子进程: {exe}");

        var sb = new StringBuilder();
        void Read(StreamReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lock (sb)
                {
                    sb.AppendLine(line);
                }
            }
        }

        Task outTask = Task.Run(() => Read(process.StandardOutput));
        Task errTask = Task.Run(() => Read(process.StandardError));

        if (!process.WaitForExit(120_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            Assert.Fail("子进程 120 秒没退出");
        }

        Task.WaitAll(outTask, errTask);

        lock (sb)
        {
            return (process.ExitCode, sb.ToString());
        }
    }
}
