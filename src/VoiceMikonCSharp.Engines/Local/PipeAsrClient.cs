using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VoiceMikonCSharp.Engines.Local;

/// <summary>
/// IPC 的一行响应。
///
/// 协议见 python/asr_server/README.md。
/// </summary>
public sealed class AsrResponse
{
    /// <summary>响应类型：pong / ready / loaded / unloaded / result / error。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>请求 id（result / error 带）。</summary>
    public string? ReqId { get; init; }

    /// <summary>识别文本（result 带）。</summary>
    public string? Text { get; init; }

    /// <summary>错误文案（error 带）。</summary>
    public string? Message { get; init; }

    /// <summary>错误是否致命（error 带）：致命 = 后端整体不可用。</summary>
    public bool Fatal { get; init; }

    /// <summary>耗时秒数（ready / loaded 带）。</summary>
    public double? Elapsed { get; set; }

    /// <summary>模型规格（loaded 带）。</summary>
    public string? Tag { get; init; }

    /// <summary>解析一行 JSON 响应。</summary>
    public static AsrResponse Parse(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;
        var response = new AsrResponse
        {
            Type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty,
            ReqId = root.TryGetProperty("req_id", out JsonElement r) ? r.GetString() : null,
            Text = root.TryGetProperty("text", out JsonElement x) ? x.GetString() : null,
            Message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() : null,
            Fatal = root.TryGetProperty("fatal", out JsonElement f) && f.ValueKind == JsonValueKind.True,
            Tag = root.TryGetProperty("tag", out JsonElement g) ? g.GetString() : null,
        };

        if (root.TryGetProperty("elapsed", out JsonElement e) && e.ValueKind == JsonValueKind.Number)
        {
            response.Elapsed = e.GetDouble();
        }

        return response;
    }
}

/// <summary>
/// 本地推理子进程的管道客户端。
///
/// 职责：启动 <c>python/asr_server/asr_server.py</c> 子进程 → 连命名管道 →
/// 发命令收结果 → 进程退出时把一切标成不健康。
///
/// **协议的行/块混排**：控制消息是 UTF-8 JSON 行；<c>transcribe</c> 请求后面
/// 紧跟 <c>n_samples × 2</c> 字节 int16 小端裸帧。所以读侧要自己管缓冲：
/// 先按行切 JSON，剩下的字节留给裸帧。
///
/// 翻译自 Python 版的进程内 <c>ASREngine</c>（唯一一处架构变化：
/// 本地推理从“进程内 import torch”改成“Python 子进程 + named pipe”）。
/// </summary>
public sealed class PipeAsrClient : IDisposable
{
    private readonly string _pipeName;
    private readonly string _pythonExe;
    private readonly string _scriptPath;
    private readonly bool _mock;
    private readonly List<string> _extraArgs;

    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private readonly List<byte> _buffer = new();
    private bool _disposed;

    /// <param name="pipeName">管道名（不含 <c>\\.\pipe\</c> 前缀）。</param>
    /// <param name="pythonExe">Python 解释器路径。</param>
    /// <param name="scriptPath">asr_server.py 的路径。</param>
    /// <param name="mock">是否 mock 模式（不 import torch / qwen_asr）。</param>
    /// <param name="extraArgs">额外命令行参数。</param>
    public PipeAsrClient(string pipeName, string pythonExe, string scriptPath, bool mock = false, IEnumerable<string>? extraArgs = null)
    {
        _pipeName = pipeName;
        _pythonExe = pythonExe;
        _scriptPath = scriptPath;
        _mock = mock;
        _extraArgs = extraArgs is null ? new List<string>() : new List<string>(extraArgs);
    }

    /// <summary>管道名。</summary>
    public string PipeName => _pipeName;

    /// <summary>子进程是否还活着。</summary>
    public bool IsAlive => _process is not null && !_process.HasExited;

    /// <summary>启动子进程并连上管道。</summary>
    /// <param name="connectTimeout">连接超时（毫秒）。</param>
    public void Start(int connectTimeout = 15000)
    {
        if (_process is not null)
        {
            return;
        }

        if (!File.Exists(_scriptPath))
        {
            throw new AsrError($"本地推理脚本不存在: {_scriptPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_scriptPath)) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add(_scriptPath);
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(_pipeName);
        if (_mock)
        {
            psi.ArgumentList.Add("--mock");
        }

        foreach (string arg in _extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            _process = Process.Start(psi) ?? throw new AsrError("启动 Python 子进程失败: 返回空进程对象");
        }
        catch (Exception ex)
        {
            throw new AsrError($"启动 Python 子进程失败({_pythonExe}): {ex.Message}", ex);
        }

        // stderr 必须有人消费 —— 否则管道写满会把子进程卡死。
        _process.BeginErrorReadLine();
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Trace.WriteLine($"[asr.pipe] python: {e.Data}");
            }
        };
        _process.EnableRaisingEvents = true;

        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
        try
        {
            _pipe.Connect(connectTimeout);
        }
        catch (Exception ex)
        {
            string stderr = ReadStderrTail();
            Dispose();
            throw new AsrError(
                $"连接本地推理管道超时或失败({_pipeName}): {ex.Message}" +
                (stderr.Length > 0 ? $"；子进程输出: {stderr}" : string.Empty), ex);
        }
    }

    /// <summary>发一条命令（可带裸音频帧），返回响应。</summary>
    public AsrResponse Send(Dictionary<string, object?> command, byte[]? audioPayload = null)
    {
        if (_pipe is null)
        {
            throw new AsrError("本地推理管道未连接");
        }

        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n");
        try
        {
            _pipe.Write(json, 0, json.Length);
            if (audioPayload is not null && audioPayload.Length > 0)
            {
                _pipe.Write(audioPayload, 0, audioPayload.Length);
            }

            _pipe.Flush();
        }
        catch (Exception ex)
        {
            throw new AsrError($"向本地推理进程发送命令失败: {ex.Message}", ex);
        }

        return Read();
    }

    /// <summary>读一条响应（按行切 JSON）。</summary>
    public AsrResponse Read()
    {
        string line = ReadLine();
        if (line.Length == 0)
        {
            throw new AsrError("本地推理进程返回了空响应");
        }

        try
        {
            return AsrResponse.Parse(line);
        }
        catch (Exception ex)
        {
            throw new AsrError($"本地推理进程返回了无法解析的响应: {line}", ex);
        }
    }

    /// <summary>读满 n 个字节（裸音频帧）。</summary>
    public byte[] ReadExact(int count)
    {
        if (count <= 0)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            if (_buffer.Count > 0)
            {
                int take = Math.Min(_buffer.Count, count - filled);
                _buffer.CopyTo(0, result, filled, take);
                _buffer.RemoveRange(0, take);
                filled += take;
                continue;
            }

            byte[] chunk = new byte[8192];
            int n = _pipe!.Read(chunk, 0, chunk.Length);
            if (n <= 0)
            {
                throw new AsrError("本地推理进程已关闭连接");
            }

            _buffer.AddRange(new ReadOnlySpan<byte>(chunk, 0, n).ToArray());
        }

        return result;
    }

    /// <summary>读一行（'\n' 分隔），返回不含换行的 UTF-8 文本。</summary>
    private string ReadLine()
    {
        while (true)
        {
            for (int i = 0; i < _buffer.Count; i++)
            {
                if (_buffer[i] != (byte)'\n')
                {
                    continue;
                }

                byte[] line = _buffer.GetRange(0, i).ToArray();
                _buffer.RemoveRange(0, i + 1);
                return Encoding.UTF8.GetString(line).TrimEnd('\r');
            }

            byte[] chunk = new byte[8192];
            int n = _pipe!.Read(chunk, 0, chunk.Length);
            if (n <= 0)
            {
                throw new AsrError("本地推理进程已退出或关闭了连接");
            }

            _buffer.AddRange(new ReadOnlySpan<byte>(chunk, 0, n).ToArray());
        }
    }

    /// <summary>读子进程 stderr 的尾部（连接失败时给用户看原因）。</summary>
    private string ReadStderrTail()
    {
        try
        {
            if (_process is null)
            {
                return string.Empty;
            }

            _process.WaitForExit(500);
            string text = _process.StandardError.ReadToEnd();
            return text.Length > 400 ? text[^400..] : text;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _pipe?.Dispose();
        }
        catch (Exception)
        {
        }

        _pipe = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                _process.WaitForExit(3000);
            }
            catch (Exception)
            {
                // 杀不掉就算了 —— 进程自己退出也行
            }

            _process.Dispose();
            _process = null;
        }
    }
}
