using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VoiceMikonCSharp.Engines.Local;

/// <summary>
/// **本地后端**：Qwen3-ASR（transformers + GPU，完全离线）。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/engine.py 的 <c>ASREngine</c>。
///
/// **唯一一处架构变化**：Python 版是进程内 <c>import torch</c> + <c>import qwen_asr</c>
/// （几百 MB 常驻）。C# 版改成：启动 <c>python/asr_server/asr_server.py</c> 子进程，
/// 通过 named pipe 发命令收结果 —— Python 侧复用 <c>engines/engine.py</c> 的
/// <c>Qwen3ASRModel.from_pretrained(...)</c> 调用方式，热词合并自己实现，
/// **不 import voice_mikon 包**（脚本自包含）。
///
/// 模型常驻显存（子进程里），按热键时零等待。0.6B / 1.7B 两个尺寸可运行时切换
/// （切换会重新加载模型，耗时几秒）。
///
/// 热词有**两个来源**，识别时合并(去重):
///   1. 构造时传入的列表(来自 config.json 的 <c>hotwords</c> 字段)
///   2. <c>data/hotwords.txt</c> 文件 —— **每次识别时实时读取**(mtime 缓存),
///      所以改完词表文件立刻生效,不用重启。
///      （C# 侧在 <see cref="HotwordSource"/> 里做这件事；Python 子进程侧还会
///      再合并一次自己那份词表文件 —— 两边都合并，任一侧都不会丢词。）
/// </summary>
public sealed class LocalBackend : AsrBackendBase, IDisposable
{
    /// <summary>支持的模型规格。</summary>
    public static readonly string[] SupportedTags = { "0.6B", "1.7B" };

    private readonly string _modelRoot;
    private readonly string? _language;
    private readonly string _pythonExe;
    private readonly string _scriptPath;
    private readonly bool _mock;
    private readonly double _readyTimeoutSeconds;

    private string _modelTag;
    private PipeAsrClient? _client;
    private bool _ready;
    private bool _fatal;
    private int _reqCounter;

    /// <param name="modelRoot">本地 Qwen3-ASR 权重所在的根目录。</param>
    /// <param name="modelTag">模型规格（0.6B / 1.7B）。</param>
    /// <param name="language">识别语种（null = 自动检测）。</param>
    /// <param name="hotwords">config.json 里的术语表。</param>
    /// <param name="hotwordsFile">术语表文件（默认 data/hotwords.txt）。</param>
    /// <param name="pythonExe">Python 解释器（默认取环境变量 VOICE_MIKON_CSHARP_PYTHON，其次 "python"）。</param>
    /// <param name="scriptPath">asr_server.py 路径（默认在仓库里向上找）。</param>
    /// <param name="mock">mock 模式（不加载真模型，给测试用）。</param>
    /// <param name="readyTimeoutSeconds">模型加载就绪超时（秒）。</param>
    public LocalBackend(
        string modelRoot,
        string modelTag = "0.6B",
        string? language = null,
        IEnumerable<string>? hotwords = null,
        string? hotwordsFile = null,
        string? pythonExe = null,
        string? scriptPath = null,
        bool mock = false,
        double readyTimeoutSeconds = 300)
        : base(hotwords, hotwordsFile)
    {
        _modelRoot = modelRoot;
        _modelTag = modelTag;
        _language = language;
        _pythonExe = pythonExe ?? ResolvePythonExe();
        _scriptPath = scriptPath ?? ResolveScriptPath();
        _mock = mock;
        _readyTimeoutSeconds = readyTimeoutSeconds;
    }

    /// <summary>
    /// 当前模型的标识(面板显示用)。
    ///
    /// 永远显示本地权重路径 —— 目录不存在也照实显示,**不再**回退成
    /// HuggingFace 仓库名:<c>load()</c> 已不允许从那里拉取,显示仓库名
    /// 等于谎称"会自动下载"。
    /// </summary>
    public override string ModelId => Path.Combine(_modelRoot, $"Qwen3-ASR-{_modelTag}");

    /// <summary>
    /// 模型是否就绪。
    ///
    /// 子进程退出 / 收到 fatal 错误 → 立刻变 false（不健康的后端不该被当成可用）。
    /// </summary>
    public override bool Loaded => _ready && !_fatal && _client is not null && _client.IsAlive;

    /// <summary>最后一次 fatal 错误文案（诊断用）。</summary>
    public string? LastFatalError { get; private set; }

    /// <summary>当前模型规格。</summary>
    public string ModelTag => _modelTag;

    /// <summary>本地推理子进程是否还活着。</summary>
    public bool ProcessAlive => _client?.IsAlive ?? false;

    /// <summary>
    /// 请求子进程优雅退出（运维/诊断用；正常卸载用 <see cref="Unload"/>）。
    ///
    /// 退出后 <see cref="Loaded"/> 变 false；需要再用本地后端时调
    /// <see cref="Load"/> 重新拉起子进程。
    /// </summary>
    public void StopServer()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            _client.Send(new Dictionary<string, object?>
            {
                ["cmd"] = "shutdown",
            });
        }
        catch (AsrError)
        {
            // 进程已经没了 —— 照样算退出成功
        }

        _ready = false;
    }

    /// <summary>
    /// 加载模型(可顺便切换规格),返回耗时秒数。
    ///
    /// 只认本地权重目录,**绝不静默联网下载**:目录不在就把原因和
    /// 出路说清楚,由用户(或他的 AI 助手)决定下载权重还是走网络后端。
    /// （权重检查在 Python 子进程里做 —— 那边才有 torch 环境。）
    /// </summary>
    public override double Load()
    {
        EnsureClient();

        var response = _client!.Send(new Dictionary<string, object?>
        {
            ["cmd"] = "load",
            ["model_root"] = _modelRoot,
            ["tag"] = _modelTag,
        });

        if (response.Type == "error")
        {
            MarkFatalIfNeeded(response);
            throw new AsrError(response.Message ?? "本地模型加载失败");
        }

        if (response.Type != "ready")
        {
            throw new AsrError($"本地模型加载返回了意外响应: {response.Type}");
        }

        _ready = true;
        _fatal = false;
        LastFatalError = null;
        double elapsed = response.Elapsed ?? 0.0;
        Trace.WriteLine($"[asr] 模型加载完成,耗时 {elapsed:F1}s");
        return elapsed;
    }

    /// <summary>切换模型规格(重新加载)。</summary>
    public override double SwitchModel(string modelTag)
    {
        if (Array.IndexOf(SupportedTags, modelTag) < 0)
        {
            throw new AsrError($"不支持的模型规格: {modelTag}");
        }

        EnsureClient();

        var response = _client!.Send(new Dictionary<string, object?>
        {
            ["cmd"] = "switch",
            ["tag"] = modelTag,
        });

        if (response.Type == "error")
        {
            MarkFatalIfNeeded(response);
            throw new AsrError(response.Message ?? "切换模型失败");
        }

        if (response.Type != "loaded")
        {
            throw new AsrError($"切换模型返回了意外响应: {response.Type}");
        }

        _modelTag = modelTag;
        return response.Elapsed ?? 0.0;
    }

    /// <summary>
    /// 卸载模型,**把显存还给系统**。
    ///
    /// 切到网络后端时走这条路 —— 本地模型常驻显存是几 GB,
    /// 不卸掉的话"切到网络省显存"就是空话。
    ///
    /// ⚠ 与 Python 版的一处**有意差异**：Python 的 <c>unload()</c> 只
    /// <c>del model</c> + <c>torch.cuda.empty_cache()</c>，**Python 进程还留着**
    /// （torch 运行时本身还占着几百 MB）。C# 版的子进程存在的唯一理由就是
    /// 持有模型，所以这里连进程一起结束 —— 显存和内存都整个还回去。
    /// 需要再用本地后端时调 <see cref="Load"/> 重新拉起子进程。
    /// </summary>
    public override void Unload()
    {
        if (_client is null)
        {
            _ready = false;
            return;
        }

        try
        {
            AsrResponse response = _client.Send(new Dictionary<string, object?>
            {
                ["cmd"] = "unload",
            });

            if (response.Type == "error")
            {
                MarkFatalIfNeeded(response);
            }
        }
        catch (AsrError)
        {
            // 进程已经没了 —— 卸载照样算成功
        }
        finally
        {
            _client.Dispose();
            _client = null;
            _ready = false;
        }
    }

    /// <summary>
    /// 识别音频,返回文本。
    ///
    /// <paramref name="sampleRate"/> 是**传入音频的实际采样率**(设备原生率,通常是 48 kHz)。
    /// 重采样到 16 kHz 由 <c>qwen_asr</c> 内部用 librosa 完成 —— 与官方评测
    /// 路径完全一致,不要在外面自己降采样(线性插值没有抗混叠滤波)。
    ///
    /// 音频以 int16 裸帧（管道里小端）发给 Python 侧，那边再转成
    /// [-1,1] 的 float32 交给 qwen_asr（**必须是浮点** —— librosa 只接受浮点）。
    /// </summary>
    public override string Transcribe(short[] audio, int sampleRate)
    {
        if (audio.Length == 0)
        {
            return string.Empty;
        }

        if (!Loaded)
        {
            throw new AsrError("模型尚未加载");
        }

        string reqId = Interlocked.Increment(ref _reqCounter).ToString();
        IReadOnlyList<string> words = Hotwords.EffectiveWords();

        var command = new Dictionary<string, object?>
        {
            ["cmd"] = "transcribe",
            ["req_id"] = reqId,
            ["sample_rate"] = sampleRate,
            ["language"] = _language,
            ["context"] = words,
            ["n_samples"] = audio.Length,
        };

        AsrResponse response = _client!.Send(command, ToLittleEndianBytes(audio));

        if (response.Type == "error")
        {
            MarkFatalIfNeeded(response);
            throw new AsrError(response.Message ?? "识别失败");
        }

        if (response.Type != "result")
        {
            throw new AsrError($"识别返回了意外响应: {response.Type}");
        }

        if (!string.Equals(response.ReqId, reqId, StringComparison.Ordinal))
        {
            throw new AsrError($"识别响应 req_id 不匹配(期望 {reqId},实际 {response.ReqId})");
        }

        return response.Text ?? string.Empty;
    }

    public void Dispose()
    {
        Unload();
    }

    /// <summary>fatal 错误 → 标记后端不健康。</summary>
    private void MarkFatalIfNeeded(AsrResponse response)
    {
        if (!response.Fatal)
        {
            return;
        }

        _fatal = true;
        _ready = false;
        LastFatalError = response.Message;
        Trace.WriteLine($"[asr] 本地后端进入不健康状态: {response.Message}");
    }

    /// <summary>确保子进程已启动并连上管道。</summary>
    private void EnsureClient()
    {
        if (_client is not null && _client.IsAlive)
        {
            return;
        }

        _client?.Dispose();
        _client = new PipeAsrClient(
            BuildPipeName(),
            _pythonExe,
            _scriptPath,
            _mock);
        _client.Start((int)Math.Max(1000, _readyTimeoutSeconds * 1000));
    }

    /// <summary>
    /// 管道名由 C# 生成（带 PID + 随机后缀，避免多实例冲突）。
    /// </summary>
    public static string BuildPipeName()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        return $"voice-mikon-csharp-asr-{Environment.ProcessId}-{suffix}";
    }

    /// <summary>int16 → 小端裸字节。</summary>
    public static byte[] ToLittleEndianBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            ushort v = unchecked((ushort)samples[i]);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[(i * 2) + 1] = (byte)(v >> 8);
        }

        return bytes;
    }

    /// <summary>Python 解释器：环境变量 VOICE_MIKON_CSHARP_PYTHON 优先，其次 "python"。</summary>
    private static string ResolvePythonExe()
    {
        string? over = Environment.GetEnvironmentVariable("VOICE_MIKON_CSHARP_PYTHON");
        return string.IsNullOrWhiteSpace(over) ? "python" : over;
    }

    /// <summary>在仓库里向上找 <c>python/asr_server/asr_server.py</c>。</summary>
    public static string ResolveScriptPath()
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

        return Path.Combine(AppContext.BaseDirectory, "python", "asr_server", "asr_server.py");
    }
}
