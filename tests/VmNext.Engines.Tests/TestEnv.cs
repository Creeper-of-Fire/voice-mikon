using System.Diagnostics;
using Xunit;
using System.Text;

namespace VmNext.Engines.Tests;

/// <summary>
/// 测试环境探测：python / 脚本 / 模型文件在不在。
///
/// ⚠ 探测用**系统 python**，绝不用 voice-mikon 的 venv（那是另一个工程的依赖环境）。
/// </summary>
internal static class TestEnv
{
    private static readonly Lazy<string?> LazyPython = new(FindPython);
    private static readonly Lazy<string?> LazyScript = new(FindScript);
    private static readonly Lazy<string?> LazyModel = new(FindModel);

    /// <summary>可用的 python 解释器（null = 没有）。</summary>
    public static string? Python => LazyPython.Value;

    /// <summary>asr_server.py 路径（null = 没找到）。</summary>
    public static string? ScriptPath => LazyScript.Value;

    /// <summary>silero_vad.onnx 路径（null = 没找到）。</summary>
    public static string? ModelPath => LazyModel.Value;

    /// <summary>python 是否可用。</summary>
    public static bool PythonAvailable => Python is not null;

    /// <summary>python 里有没有 onnxruntime（有才能跑参考脚本）。</summary>
    public static bool OnnxRuntimeAvailable { get; } = CheckOnnxRuntime();

    /// <summary>跑一条 python 命令，返回退出码（-1 = 跑不起来）。</summary>
    public static int RunPython(string arguments, string? workingDirectory = null, TextWriter? output = null)
    {
        string? python = Python;
        if (python is null)
        {
            return -1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(arguments);

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            return -1;
        }

        string stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(60000);
        output?.Write(stdout);
        return proc.ExitCode;
    }

    private static string? FindPython()
    {
        string? over = Environment.GetEnvironmentVariable("VMNEXT_PYTHON");
        if (!string.IsNullOrWhiteSpace(over) && File.Exists(over))
        {
            return over;
        }

        foreach (string candidate in new[] { "python", "python3" })
        {
            var psi = new ProcessStartInfo
            {
                FileName = candidate,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");

            try
            {
                using Process? proc = Process.Start(psi);
                if (proc is null)
                {
                    continue;
                }

                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit(10000);
                if (proc.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 换下一个候选
            }
        }

        return null;
    }

    private static string? FindScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "python", "asr_server", "asr_server.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? FindModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "src", "VmNext.Engines", "Models", "silero_vad.onnx");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string copied = Path.Combine(dir.FullName, "Models", "silero_vad.onnx");
            if (File.Exists(copied))
            {
                return copied;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool CheckOnnxRuntime()
    {
        if (Python is null)
        {
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = Python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import onnxruntime");

        try
        {
            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            return proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>CRC-32（IEEE，zlib 同款）—— 用来验证音频"原样到达"。</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
