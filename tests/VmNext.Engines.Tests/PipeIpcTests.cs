using VmNext.Engines;
using Xunit;
using VmNext.Engines.Local;

namespace VmNext.Engines.Tests;

/// <summary>
/// 本地推理 IPC（named pipe）的测试：mock 模式跑全流程。
///
/// 覆盖：ping → load → transcribe → switch → unload、req_id 匹配、
/// 音频原样到达（crc32）、Python 抛异常时收到 error、fatal 标记、
/// 子进程退出后 Loaded 变 false。
///
/// 没有 python（或找不到脚本）时**动态跳过** —— 这台机器上跑不了就是跑不了，
/// 不该让整个测试项目变红。
/// </summary>
public sealed class PipeIpcTests
{
    private const int Rate = 16000;

    [Fact]
    public void mock_全流程_ping_load_transcribe_switch_unload()
    {
        RequirePython();

        using var client = StartMock();

        AsrResponse pong = client.Send(new Dictionary<string, object?> { ["cmd"] = "ping" });
        Assert.Equal("pong", pong.Type);

        AsrResponse ready = client.Send(new Dictionary<string, object?>
        {
            ["cmd"] = "load",
            ["model_root"] = @"C:\不存在的权重目录",
            ["tag"] = "0.6B",
        });
        Assert.Equal("ready", ready.Type);

        var audio = Synth.Sine(Rate, 200, 0.25);
        AsrResponse result = client.Send(
            new Dictionary<string, object?>
            {
                ["cmd"] = "transcribe",
                ["req_id"] = "req-1",
                ["sample_rate"] = Rate,
                ["language"] = null,
                ["context"] = new[] { "词A" },
                ["n_samples"] = audio.Length,
            },
            LocalBackend.ToLittleEndianBytes(audio));

        Assert.Equal("result", result.Type);
        Assert.Equal("req-1", result.ReqId);
        Assert.Equal($"[mock:{audio.Length}:{Crc32.Compute(LocalBackend.ToLittleEndianBytes(audio)):x8}]", result.Text);

        AsrResponse loaded = client.Send(new Dictionary<string, object?>
        {
            ["cmd"] = "switch",
            ["tag"] = "1.7B",
        });
        Assert.Equal("loaded", loaded.Type);
        Assert.Equal("1.7B", loaded.Tag);

        AsrResponse unloaded = client.Send(new Dictionary<string, object?> { ["cmd"] = "unload" });
        Assert.Equal("unloaded", unloaded.Type);
    }

    [Fact]
    public void mock_音频原样到达_校验和一致()
    {
        RequirePython();

        using var client = StartMock();
        client.Send(new Dictionary<string, object?>
        {
            ["cmd"] = "load",
            ["model_root"] = @"C:\不存在的权重目录",
            ["tag"] = "0.6B",
        });

        var audio = Synth.Sine(Rate, 440, 0.1);
        byte[] payload = LocalBackend.ToLittleEndianBytes(audio);

        AsrResponse response = client.Send(
            new Dictionary<string, object?>
            {
                ["cmd"] = "transcribe",
                ["req_id"] = "abc",
                ["sample_rate"] = Rate,
                ["context"] = Array.Empty<string>(),
                ["n_samples"] = audio.Length,
            },
            payload);

        Assert.Equal("result", response.Type);
        // mock 回显的 crc32 必须等于本地算的 —— 说明音频一个字节都没坏
        Assert.Contains(Crc32.Compute(payload).ToString("x8"), response.Text ?? string.Empty);
        Assert.Contains(audio.Length.ToString(), response.Text ?? string.Empty);
    }

    [Fact]
    public void mock_多个请求的_req_id_各自匹配()
    {
        RequirePython();

        using var client = StartMock();
        client.Send(new Dictionary<string, object?>
        {
            ["cmd"] = "load",
            ["model_root"] = @"C:\不存在的权重目录",
            ["tag"] = "0.6B",
        });

        var audio = Synth.Sine(Rate, 300, 0.05);
        for (int i = 0; i < 3; i++)
        {
            string reqId = $"r{i}";
            AsrResponse response = client.Send(
                new Dictionary<string, object?>
                {
                    ["cmd"] = "transcribe",
                    ["req_id"] = reqId,
                    ["sample_rate"] = Rate,
                    ["context"] = Array.Empty<string>(),
                    ["n_samples"] = audio.Length,
                },
                LocalBackend.ToLittleEndianBytes(audio));

            Assert.Equal("result", response.Type);
            Assert.Equal(reqId, response.ReqId);
        }
    }

    [Fact]
    public void mock_python_抛异常时收到_error_不是挂死()
    {
        RequirePython();

        using var client = StartMock();
        client.Send(new Dictionary<string, object?>
        {
            ["cmd"] = "load",
            ["model_root"] = @"C:\不存在的权重目录",
            ["tag"] = "0.6B",
        });

        var audio = Synth.Sine(Rate, 300, 0.05);
        AsrResponse response = client.Send(
            new Dictionary<string, object?>
            {
                ["cmd"] = "transcribe",
                ["req_id"] = "boom",
                ["sample_rate"] = Rate,
                ["context"] = Array.Empty<string>(),
                ["n_samples"] = audio.Length,
                ["mock_fail"] = true,
            },
            LocalBackend.ToLittleEndianBytes(audio));

        Assert.Equal("error", response.Type);
        Assert.Equal("boom", response.ReqId);
        Assert.False(response.Fatal);
        Assert.Contains("mock 故障注入", response.Message ?? string.Empty);

        // 出了错还能继续用（不是挂死）
        AsrResponse ok = client.Send(new Dictionary<string, object?> { ["cmd"] = "ping" });
        Assert.Equal("pong", ok.Type);
    }

    [Fact]
    public void mock_fatal_标记能被解析出来()
    {
        RequirePython();

        using var client = StartMock();
        client.Send(new Dictionary<string, object?>
        {
            ["cmd"] = "load",
            ["model_root"] = @"C:\不存在的权重目录",
            ["tag"] = "0.6B",
        });

        var audio = Synth.Sine(Rate, 300, 0.05);
        AsrResponse response = client.Send(
            new Dictionary<string, object?>
            {
                ["cmd"] = "transcribe",
                ["req_id"] = "fatal",
                ["sample_rate"] = Rate,
                ["context"] = Array.Empty<string>(),
                ["n_samples"] = audio.Length,
                ["mock_fail"] = true,
                ["mock_fatal"] = true,
            },
            LocalBackend.ToLittleEndianBytes(audio));

        Assert.Equal("error", response.Type);
        Assert.True(response.Fatal);
    }

    [Fact]
    public void 后端_load_之后可以识别_并拿到_mock_文本()
    {
        RequirePython();

        using var backend = NewMockBackend();
        backend.Load();

        Assert.True(backend.Loaded);

        var audio = Synth.Sine(Rate, 200, 0.2);
        string text = backend.Transcribe(audio, Rate);

        Assert.StartsWith("[mock:", text);
        Assert.Contains(audio.Length.ToString(), text);
    }

    [Fact]
    public void 后端_切换模型_走_switch()
    {
        RequirePython();

        using var backend = NewMockBackend();
        backend.Load();

        backend.SwitchModel("1.7B");

        Assert.Equal("1.7B", backend.ModelTag);
        Assert.True(backend.Loaded);
    }

    [Fact]
    public void 后端_未加载时识别_抛_AsrError()
    {
        RequirePython();

        using var backend = NewMockBackend();

        AsrError error = Assert.Throws<AsrError>(() => backend.Transcribe(Synth.Sine(Rate, 200, 0.1), Rate));
        Assert.Contains("模型尚未加载", error.Message);
    }

    [Fact]
    public void 后端_unload_之后_Loaded_变_false()
    {
        RequirePython();

        using var backend = NewMockBackend();
        backend.Load();
        Assert.True(backend.Loaded);

        backend.Unload();

        Assert.False(backend.Loaded);
        Assert.False(backend.ProcessAlive);
    }

    [Fact]
    public void 后端_子进程退出后_Loaded_变_false()
    {
        RequirePython();

        using var backend = NewMockBackend();
        backend.Load();
        Assert.True(backend.ProcessAlive);

        backend.StopServer();

        // 给子进程一点时间退出
        for (int i = 0; i < 50 && backend.ProcessAlive; i++)
        {
            Thread.Sleep(100);
        }

        Assert.False(backend.ProcessAlive);
        Assert.False(backend.Loaded);
    }

    [Fact]
    public void 真模式_权重目录不存在时收到_fatal_错误()
    {
        RequirePython();

        // 不带 --mock：Python 侧会检查权重目录 → 失败 → error{fatal:true}
        using var backend = new LocalBackend(
            modelRoot: Path.Combine(Path.GetTempPath(), "vmnext-不存在的权重-" + Guid.NewGuid().ToString("N")),
            modelTag: "0.6B",
            pythonExe: TestEnv.Python,
            scriptPath: TestEnv.ScriptPath,
            mock: false,
            readyTimeoutSeconds: 120);

        AsrError error = Assert.Throws<AsrError>(() => backend.Load());

        Assert.Contains("权重缺失", error.Message);
        Assert.False(backend.Loaded);
        Assert.NotNull(backend.LastFatalError);
    }

    private static void RequirePython()
    {
        if (!TestEnv.PythonAvailable)
        {
            Assert.Skip("本机没有可用的 python（IPC 测试跑不了）");
        }

        if (TestEnv.ScriptPath is null)
        {
            Assert.Skip("找不到 python/asr_server/asr_server.py");
        }
    }

    private static PipeAsrClient StartMock()
    {
        var client = new PipeAsrClient(
            LocalBackend.BuildPipeName(),
            TestEnv.Python!,
            TestEnv.ScriptPath!,
            mock: true);
        client.Start(20000);
        return client;
    }

    private static LocalBackend NewMockBackend()
    {
        return new LocalBackend(
            modelRoot: @"C:\不存在的权重目录",
            modelTag: "0.6B",
            pythonExe: TestEnv.Python,
            scriptPath: TestEnv.ScriptPath,
            mock: true,
            readyTimeoutSeconds: 30);
    }
}
