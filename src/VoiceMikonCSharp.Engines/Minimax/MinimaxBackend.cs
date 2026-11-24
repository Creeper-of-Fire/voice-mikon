using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceMikonCSharp.Engines.Dsp;

namespace VoiceMikonCSharp.Engines.Minimax;

/// <summary>
/// MiniMax **网络后端**（asr-1.0，multipart 上传音频）。
///
///     POST https://api.minimax.cn/v1/speech_to_text
///     Authorization: Bearer $MINIMAX_API_KEY
///     multipart/form-data:
///         model=asr-1.0
///         file=&lt;WAV bytes&gt;
///         response_format=json
///         stream=false
///
/// 几个刻意的选择：
///
/// - **API Key 从环境变量 <c>MINIMAX_API_KEY</c> 或 <c>data/.env</c> 文件读**（前者
///   临时覆盖，后者持久存储），绝不进 config.json
///   —— 配置文件会被用户手工编辑、截图、分享。缺 key 就明确报错，
///   **不静默回落到本地**（那会让用户以为在用网络）。
/// - **key 绝不进日志**：需要提及时只报长度（够判断“配没配上”），
///   打码串本身也还是进了日志文件 —— 干脆别提。
/// - **串行锁**：每后端实例一把锁，所有出网请求排队。MiniMax 的并发
///   配额是 CONN=2，而预览位和最终位共用同一颗后端实例，一次只放一个
///   请求出去 —— 天然落在配额之内，也不会让“分句预览”的请求和“最终整段”
///   的请求互相抢。
/// - **429 指数退避重试 2 次**（0.5s / 1.5s）：限流是暂时的，直接报错
///   会让用户白白重说一遍。
/// - **热词不注入**：MiniMax 没有 context biasing 入口 —— 见
///   <see cref="AsrBackendBase.BuildContext"/>。切换成功的通知里会**明说**这件事，
///   不静默装支持。
/// - **重采样用多相 FIR**（带抗混叠低通），理由与本地引擎一致，
///   见 <see cref="Resampler"/>。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/minimax.py 与 _http.py。
/// </summary>
public sealed class MinimaxBackend : AsrBackendBase, IDisposable
{
    /// <summary>API Key 的环境变量名（临时覆盖/测试用；日常持久存储在 data/.env）。</summary>
    public const string ApiKeyEnv = "MINIMAX_API_KEY";

    /// <summary>
    /// key 的持久存储文件（相对项目 data/ 目录）。
    /// 文件格式：每行 KEY=VALUE，# 开头是注释。放文件而不是注册表环境变量：
    /// 暴露面小（不会被任意进程的用户环境块读到），且跟着项目目录走（portable）。
    /// </summary>
    public const string EnvFileName = ".env";

    /// <summary>端点前缀：国内站是 api.minimax.cn，海外站改成 https://api.minimax.io。</summary>
    public const string DefaultBaseUrl = "https://api.minimax.cn";

    /// <summary>默认模型（官方目前只对外提供 asr-1.0）。</summary>
    public const string DefaultModel = "asr-1.0";

    /// <summary>网络后端要求（也是最通用的组合）：单声道 / 16 kHz / 16-bit PCM WAV。</summary>
    public const int TargetRate = 16000;

    /// <summary>官方限制：单次 ≤500 秒 / ≤50 MB。</summary>
    public const double MaxDurationSeconds = 500.0;

    /// <summary>官方限制：单次 ≤50 MB。</summary>
    public const int MaxBytes = 50 * 1024 * 1024;

    /// <summary>429 限流的退避节奏（秒）：第 1 次等 0.5s，第 2 次等 1.5s。</summary>
    public static readonly double[] RetryDelays = { 0.5, 1.5 };

    /// <summary>单次请求超时（秒）的默认值。给得比最长的音频还长一点，免得慢网络被误杀。</summary>
    public const double DefaultTimeout = 60.0;

    private readonly string _model;
    private readonly string _baseUrl;
    private readonly double _timeout;
    private readonly string _dataDir;

    // 串行锁：一把锁管住这个实例的全部出网请求（见模块 docstring）
    private readonly SemaphoreSlim _lock = new(1, 1);

    private HttpClient? _client;
    private bool _ownsClient;
    private string? _apiKey;
    private bool _ready;

    /// <param name="apiKey">显式传入的 key（优先）；不传则在 <see cref="Load"/> 时从环境变量 / data/.env 读。</param>
    /// <param name="model">模型名（国内站 asr-1.0；海外站相同）。</param>
    /// <param name="baseUrl">端点前缀（国内 .cn / 海外 .io）。</param>
    /// <param name="hotwords">config.json 里配的术语表（**这一路不会注入**，见 <see cref="AsrBackendBase.BuildContext"/>）。</param>
    /// <param name="hotwordsFile">术语表文件路径。</param>
    /// <param name="dataDir">data 目录（读 .env 用）；null = 用环境变量 VOICE_MIKON_HOME 或程序目录下的 data。</param>
    /// <param name="handler">注入的 HTTP 处理器（测试用：假传输层，全程不联网）。</param>
    /// <param name="timeout">单次请求超时（秒）。</param>
    public MinimaxBackend(
        string? apiKey = null,
        string model = DefaultModel,
        string baseUrl = DefaultBaseUrl,
        IEnumerable<string>? hotwords = null,
        string? hotwordsFile = null,
        string? dataDir = null,
        HttpMessageHandler? handler = null,
        double timeout = DefaultTimeout)
        : base(hotwords, hotwordsFile)
    {
        _model = string.IsNullOrEmpty(model) ? DefaultModel : model;
        _baseUrl = (string.IsNullOrEmpty(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        _timeout = timeout;
        _dataDir = dataDir ?? ResolveDataDir();
        _apiKey = apiKey;

        if (handler is not null)
        {
            _client = new HttpClient(handler, disposeHandler: false);
            _ownsClient = true;
        }
    }

    public override string ModelId => _model;

    public override bool Loaded => _ready;

    /// <summary>
    /// MiniMax **没有** context biasing 通道 —— 热词一律不注入。
    ///
    /// 覆写成空串而不是“忘了调用”：这一路哪天不小心调了
    /// <see cref="AsrBackendBase.BuildContext"/>，拿到的也是空串，
    /// 不会把词表悄悄塞进请求。
    /// 用户能不能用热词、在切换成功的通知里**明说**，不静默装支持。
    /// </summary>
    public override string BuildContext() => string.Empty;

    /// <summary>
    /// 检查 API Key、建好会话，返回耗时秒数。
    ///
    /// **这一步就会失败**：没配 key 就直接报错，不让用户以为网络后端能用。
    /// </summary>
    public override double Load()
    {
        string key = _apiKey ?? LoadApiKey(_dataDir);
        key = (key ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            throw new AsrError(
                $"未配置 {ApiKeyEnv} —— MiniMax 后端不可用。" +
                $"把 key 写进 data/{EnvFileName}({ApiKeyEnv}=...)" +
                "或设置同名环境变量后重试(本地后端不受影响)");
        }

        _apiKey = key;

        var sw = Stopwatch.StartNew();
        _client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(_timeout) };
        _ownsClient = true;
        _ready = true;
        sw.Stop();

        // **只报 key 的长度** —— key 本身绝不能进日志。
        Trace.WriteLine($"[asr.net] MiniMax 后端就绪: {_model} ({_baseUrl}, key 长度 {_apiKey.Length})");
        return sw.Elapsed.TotalSeconds;
    }

    /// <summary>关掉 HTTP 会话（切回本地时走这条路）。</summary>
    public override void Unload()
    {
        _ready = false;
        if (_client is not null && _ownsClient)
        {
            try
            {
                _client.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[asr.net] 关闭 HTTP 会话失败: {ex.Message}");
            }
        }

        _client = null;
    }

    /// <summary>
    /// MiniMax 没有本地模型规格可切 —— 换后端不是这里的事。
    /// </summary>
    public override double SwitchModel(string modelTag)
    {
        throw new AsrError("MiniMax 后端不支持切换模型规格(没有本地权重可切)");
    }

    /// <summary>
    /// 把音频发给 MiniMax，返回识别文本。
    ///
    /// 同一段音频既可能来自最终位（整段），也可能来自分句预览（一句一段）
    /// —— 这个方法对两者一视同仁，消费者只认同一个契约。
    /// </summary>
    public override string Transcribe(short[] audio, int sampleRate)
    {
        if (audio.Length == 0)
        {
            return string.Empty;
        }

        if (!_ready)
        {
            throw new AsrError("MiniMax 后端尚未加载");
        }

        int srcRate = sampleRate == 0 ? TargetRate : sampleRate;
        float[] wav = ToFloat32(audio);
        float[] wav16 = Resampler.Resample(wav, srcRate, TargetRate);
        byte[] payload = WavEncoder.Encode(wav16, TargetRate);
        CheckLimits(audio.Length / (double)srcRate, payload.Length);

        string url = $"{_baseUrl}/v1/speech_to_text";
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {_apiKey}",
        };
        var data = new Dictionary<string, string>
        {
            ["model"] = _model,
            ["response_format"] = "json",
            ["stream"] = "false",
        };
        // 文件名给个正经的：有的服务端会拿扩展名判格式
        var files = new Dictionary<string, (string Name, byte[] Content, string ContentType)>
        {
            ["file"] = ("audio.wav", payload, "audio/wav"),
        };

        var sw = Stopwatch.StartNew();
        string body = PostWithRetry(url, headers, data, files);
        string text = ParseText(body);
        sw.Stop();
        Trace.WriteLine(
            $"[asr.net] MiniMax 识别完成 {audio.Length / (double)srcRate:F1}s 音频 / {sw.Elapsed.TotalSeconds:F1}s 耗时: {text}");
        return text;
    }

    /// <summary>对照官方的单次上限（≤500 秒 / ≤50 MB）。</summary>
    private static void CheckLimits(double duration, int size)
    {
        if (duration > MaxDurationSeconds)
        {
            throw new AsrError($"音频太长({duration:F0}s):MiniMax 单次上限 {MaxDurationSeconds:F0}s");
        }

        if (size > MaxBytes)
        {
            throw new AsrError($"音频太大({size / 1024 / 1024:F1} MB):MiniMax 单次上限 {MaxBytes / 1024 / 1024} MB");
        }
    }

    /// <summary>
    /// 发请求（含 429 退避重试）。
    ///
    /// 整段（含重试）都在串行锁里 —— 重试等待期间别的请求不许插队。
    /// </summary>
    private string PostWithRetry(
        string url,
        Dictionary<string, string> headers,
        Dictionary<string, string> data,
        Dictionary<string, (string Name, byte[] Content, string ContentType)> files)
    {
        _lock.Wait();
        try
        {
            int attempt = 0;
            while (true)
            {
                (int status, string body) = Request(url, headers, data, files);
                if (status != 429 || attempt >= RetryDelays.Length)
                {
                    if (status != 200)
                    {
                        throw ErrorFrom(body, status);
                    }

                    return body;
                }

                double delay = RetryDelays[attempt];
                Trace.WriteLine($"[asr.net] MiniMax 限流(HTTP 429),{delay:F1}s 后重试({attempt + 1}/{RetryDelays.Length})");
                Thread.Sleep(TimeSpan.FromSeconds(delay));
                attempt++;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>发一次请求，把传输层的异常统一翻译成 <see cref="AsrError"/>。</summary>
    private (int Status, string Body) Request(
        string url,
        Dictionary<string, string> headers,
        Dictionary<string, string> data,
        Dictionary<string, (string Name, byte[] Content, string ContentType)> files)
    {
        HttpClient client = _client ?? throw new AsrError("MiniMax 后端尚未加载");

        using var form = new MultipartFormDataContent();
        foreach (KeyValuePair<string, string> kv in data)
        {
            form.Add(new StringContent(kv.Value), kv.Key);
        }

        foreach (KeyValuePair<string, (string Name, byte[] Content, string ContentType)> kv in files)
        {
            var content = new ByteArrayContent(kv.Value.Content);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(kv.Value.ContentType);
            form.Add(content, kv.Key, kv.Value.Name);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        foreach (KeyValuePair<string, string> kv in headers)
        {
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        try
        {
            using HttpResponseMessage response = client.Send(request);
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)response.StatusCode, body);
        }
        catch (TaskCanceledException ex)
        {
            // .NET 的超时就是 TaskCanceledException（内含 TimeoutException）
            throw new AsrError($"网络请求超时({_timeout:F0}s):{ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            if (ex.InnerException is TimeoutException)
            {
                throw new AsrError($"网络请求超时({_timeout:F0}s):{ex.Message}", ex);
            }

            throw new AsrError($"网络请求失败:{ex.Message}", ex);
        }
        catch (AsrError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AsrError($"网络请求失败:{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 从响应里取出识别文本。
    ///
    /// 成功：<c>{"text": str, "duration": float, "trace_id": str}</c>
    /// 失败：<c>{"type":"error","error":{"type","message","http_code"}}</c>
    /// </summary>
    internal static string ParseText(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            throw new AsrError($"MiniMax 响应不是合法 JSON:{ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new AsrError("MiniMax 响应格式不对(不是 JSON 对象)");
            }

            if (!doc.RootElement.TryGetProperty("text", out JsonElement textEl)
                || textEl.ValueKind != JsonValueKind.String)
            {
                string brief = body.Length > 200 ? body[..200] : body;
                throw new AsrError($"MiniMax 响应里没有识别文本:{brief}");
            }

            return (textEl.GetString() ?? string.Empty).Trim();
        }
    }

    /// <summary>
    /// 把错误响应翻译成 <see cref="AsrError"/>（带 http_code 与 message）。
    ///
    /// 官方错误信封：<c>{"type":"error","error":{"type","message","http_code"}}</c>
    /// —— HTTP 状态码就是错误码（401 鉴权失败 / 429 限流 / 413 超大 …）。
    /// </summary>
    internal static AsrError ErrorFrom(string body, int status)
    {
        string etype = string.Empty;
        string detail = string.Empty;
        int httpCode = status;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out JsonElement err)
                && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("type", out JsonElement t) && t.ValueKind != JsonValueKind.Null)
                {
                    etype = t.ToString();
                }

                if (err.TryGetProperty("message", out JsonElement m) && m.ValueKind != JsonValueKind.Null)
                {
                    detail = m.ToString();
                }

                if (err.TryGetProperty("http_code", out JsonElement hc)
                    && hc.ValueKind == JsonValueKind.Number
                    && hc.TryGetInt32(out int code))
                {
                    httpCode = code;
                }
            }
        }
        catch (Exception)
        {
            // 响应不是 JSON —— 下面用原始文本兜底
        }

        if (detail.Length == 0)
        {
            detail = body.Length > 200 ? body[..200] : body;
        }

        string tag = $"http_code={httpCode}";
        if (etype.Length > 0)
        {
            tag += $" type={etype}";
        }

        if (status == 429)
        {
            // 重试过了还是 429 —— 真的被限流了，得说清楚是哪一种
            return new AsrError($"MiniMax 限流({tag}):{detail}");
        }

        return new AsrError($"MiniMax 识别失败({tag}):{detail}");
    }

    /// <summary>
    /// 取 MiniMax 的 API Key：进程环境变量优先，data/.env 文件兜底。
    ///
    /// 优先级是有意的：环境变量是**临时通道**（测试换 key、CI 注入），
    /// data/.env 才是**持久真相** —— 用户不用往系统里写任何东西。
    /// 手写的极简解析（键=值、# 注释、容忍引号），不引 dotenv。
    /// </summary>
    public static string LoadApiKey(string? dataDir = null)
    {
        string? env = Environment.GetEnvironmentVariable(ApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        try
        {
            string dir = dataDir ?? ResolveDataDir();
            string envFile = Path.Combine(dir, EnvFileName);
            if (!File.Exists(envFile))
            {
                return string.Empty;
            }

            foreach (string raw in File.ReadAllLines(envFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith(ApiKeyEnv + "=", StringComparison.Ordinal))
                {
                    string value = line[(ApiKeyEnv.Length + 1)..].Trim();
                    return value.Trim('"').Trim('\'');
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[asr.net] 读取 {EnvFileName} 失败: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// data 目录：环境变量 VOICE_MIKON_HOME 优先，否则程序目录下的 data。
    /// （portable 约定：数据只落在项目目录内部。）
    /// </summary>
    private static string ResolveDataDir()
    {
        string? over = Environment.GetEnvironmentVariable("VOICE_MIKON_HOME");
        if (!string.IsNullOrWhiteSpace(over))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(over));
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    /// <summary>释放 HTTP 会话（等同 <see cref="Unload"/>）。</summary>
    public void Dispose() => Unload();
}
