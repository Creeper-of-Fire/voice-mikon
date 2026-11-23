using System.Net;
using Xunit;
using System.Text;
using VmNext.Engines;
using VmNext.Engines.Minimax;

namespace VmNext.Engines.Tests;

/// <summary>
/// MiniMax 网络后端的单元测试（**全程不联网**：假传输层）。
///
/// 抽出传输层的唯一理由就是让后端逻辑能脱离网络被测 ——
/// 断言请求体长什么样、响应怎么解析、错误怎么报。
/// </summary>
public sealed class MinimaxTests
{
    [Fact]
    public void 没配_key_时明确报错_不静默回落()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MinimaxBackend.ApiKeyEnv)))
        {
            Assert.Skip("环境变量里已经有 MINIMAX_API_KEY，这个用例没法测'缺 key'");
        }

        using var tmp = new TempDir();
        using var backend = new MinimaxBackend(dataDir: tmp.Path);

        AsrError error = Assert.Throws<AsrError>(() => backend.Load());

        Assert.Contains("MINIMAX_API_KEY", error.Message);
        Assert.Contains("data/.env", error.Message);
        Assert.False(backend.Loaded);
    }

    [Fact]
    public void 从_data_dot_env_读_key()
    {
        using var tmp = new TempDir();
        File.WriteAllText(
            Path.Combine(tmp.Path, ".env"),
            "# 注释\nMINIMAX_API_KEY=\"sk-test-123\"\n");

        string key = MinimaxBackend.LoadApiKey(tmp.Path);

        Assert.Equal("sk-test-123", key);
    }

    [Fact]
    public void 环境变量优先于_dot_env_文件()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, ".env"), "MINIMAX_API_KEY=sk-from-file\n");

        string? original = Environment.GetEnvironmentVariable(MinimaxBackend.ApiKeyEnv);
        try
        {
            Environment.SetEnvironmentVariable(MinimaxBackend.ApiKeyEnv, "sk-from-env");
            string key = MinimaxBackend.LoadApiKey(tmp.Path);

            Assert.Equal("sk-from-env", key);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MinimaxBackend.ApiKeyEnv, original);
        }
    }

    [Fact]
    public void 成功响应_取出识别文本()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Json("{\"text\": \" 你好 \", \"duration\": 1.0}"),
        };

        using var backend = new MinimaxBackend(apiKey: "sk-test", handler: handler);
        backend.Load();

        string text = backend.Transcribe(Synth.Sine(16000, 220, 0.5), 16000);

        Assert.Equal("你好", text);
    }

    [Fact]
    public void 请求是_multipart_且带_wav_文件()
    {
        HttpRequestMessage? captured = null;
        byte[]? body = null;
        var handler = new FakeHandler
        {
            Responder = request =>
            {
                captured = request;
                body = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return Json("{\"text\": \"ok\"}");
            },
        };

        using var backend = new MinimaxBackend(apiKey: "sk-test", handler: handler);
        backend.Load();
        backend.Transcribe(Synth.Sine(16000, 220, 0.5), 16000);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://api.minimax.cn/v1/speech_to_text", captured.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", captured.Headers.GetValues("Authorization").Single());
        Assert.Contains("multipart/form-data", captured.Content!.Headers.ContentType!.ToString());
        Assert.NotNull(body);
        string bodyText = Encoding.UTF8.GetString(body!);
        Assert.Contains("audio.wav", bodyText);
        Assert.Contains("asr-1.0", bodyText);
        // multipart body 的开头是 boundary，WAV 文件在其中一个 part 里
        Assert.Contains("RIFF", bodyText);
        Assert.Contains("WAVE", bodyText);
    }

    [Fact]
    public void 海外站_base_url_可配()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler
        {
            Responder = request =>
            {
                captured = request;
                return Json("{\"text\": \"ok\"}");
            },
        };

        using var backend = new MinimaxBackend(
            apiKey: "sk-test",
            baseUrl: "https://api.minimax.io/",
            model: "asr-1.0",
            handler: handler);
        backend.Load();
        backend.Transcribe(Synth.Sine(16000, 220, 0.2), 16000);

        Assert.Equal("https://api.minimax.io/v1/speech_to_text", captured!.RequestUri!.ToString());
    }

    [Fact]
    public void 鉴权失败_错误文案带_http_code()
    {
        var handler = new FakeHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"type\":\"error\",\"error\":{\"type\":\"auth\",\"message\":\"invalid api key\",\"http_code\":401}}",
                    Encoding.UTF8,
                    "application/json"),
            },
        };

        using var backend = new MinimaxBackend(apiKey: "sk-test", handler: handler);
        backend.Load();

        AsrError error = Assert.Throws<AsrError>(() => backend.Transcribe(Synth.Sine(16000, 220, 0.2), 16000));

        Assert.Contains("http_code=401", error.Message);
        Assert.Contains("invalid api key", error.Message);
    }

    [Fact]
    public void 限流_退避重试后仍然报错_文案说清楚是限流()
    {
        int calls = 0;
        var handler = new FakeHandler
        {
            Responder = _ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(
                        "{\"type\":\"error\",\"error\":{\"type\":\"rate\",\"message\":\"slow down\",\"http_code\":429}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            },
        };

        using var backend = new MinimaxBackend(apiKey: "sk-test", handler: handler);
        backend.Load();

        AsrError error = Assert.Throws<AsrError>(() => backend.Transcribe(Synth.Sine(16000, 220, 0.2), 16000));

        Assert.Equal(3, calls);   // 首次 + 2 次重试
        Assert.Contains("限流", error.Message);
        Assert.Contains("http_code=429", error.Message);
    }

    [Fact]
    public void 响应不是_json_明确报错()
    {
        var handler = new FakeHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>不是 json</html>", Encoding.UTF8, "text/html"),
            },
        };

        using var backend = new MinimaxBackend(apiKey: "sk-test", handler: handler);
        backend.Load();

        AsrError error = Assert.Throws<AsrError>(() => backend.Transcribe(Synth.Sine(16000, 220, 0.2), 16000));

        Assert.Contains("不是合法 JSON", error.Message);
    }

    [Fact]
    public void 响应里没有识别文本_明确报错()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Json("{\"foo\": 1}"),
        };

        using var backend = new MinimaxBackend(apiKey: "sk-test", handler: handler);
        backend.Load();

        AsrError error = Assert.Throws<AsrError>(() => backend.Transcribe(Synth.Sine(16000, 220, 0.2), 16000));

        Assert.Contains("没有识别文本", error.Message);
    }

    [Fact]
    public void 未加载时识别_明确报错()
    {
        using var backend = new MinimaxBackend(apiKey: "sk-test");

        AsrError error = Assert.Throws<AsrError>(() => backend.Transcribe(Synth.Sine(16000, 220, 0.2), 16000));

        Assert.Contains("尚未加载", error.Message);
    }

    [Fact]
    public void 空音频_直接返回空串()
    {
        using var backend = new MinimaxBackend(apiKey: "sk-test");
        backend.Load();

        Assert.Equal(string.Empty, backend.Transcribe(Array.Empty<short>(), 16000));
    }

    [Fact]
    public void 热词一律不注入_minimax_没有_context_biasing()
    {
        using var tmp = new TempFile("词C\n");
        using var backend = new MinimaxBackend(
            apiKey: "sk-test",
            hotwords: new[] { "词A", "词B" },
            hotwordsFile: tmp.Path);

        Assert.Equal(string.Empty, backend.BuildContext());
    }

    [Fact]
    public void 切换模型规格_不支持_明确报错()
    {
        using var backend = new MinimaxBackend(apiKey: "sk-test");

        AsrError error = Assert.Throws<AsrError>(() => backend.SwitchModel("1.7B"));

        Assert.Contains("不支持切换模型", error.Message);
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => throw new InvalidOperationException("没有设置 Responder");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Responder(request));
        }

        // 后端走的是 HttpClient.Send（同步路径）—— 假传输层也得支持同步
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Responder(request);
        }
    }
}

/// <summary>临时目录（用完自动删）。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vmnext-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
