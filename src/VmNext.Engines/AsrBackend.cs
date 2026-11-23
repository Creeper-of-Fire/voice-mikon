using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace VmNext.Engines;

/// <summary>
/// 模型加载或推理失败。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/base.py 的 <c>ASRError</c>。
/// </summary>
public class AsrError : Exception
{
    public AsrError(string message) : base(message)
    {
    }

    public AsrError(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// 识别后端的统一接口（本地 / 网络）。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/base.py 的 <c>ASRBackend</c> 抽象基类。
///
/// 项目有两种识别后端：
///
///     LocalBackend    本地 Qwen3-ASR（transformers + GPU，完全离线）
///     MinimaxBackend  MiniMax 平台网络接口（asr-1.0，multipart 上传音频）
///
/// 两者实现**同一个抽象**，控制器和流式引擎只认这个接口，
/// 不关心底下跑的是模型还是网络请求。
///
/// **为什么要有 <see cref="Unload"/>**：两种后端是**互斥**的（同一时刻只留一个），
/// 切走后必须真的把资源还回去 —— 本地要释放显存（Python 版是 <c>del model</c> +
/// <c>empty_cache()</c>），不然“切到网络省显存”就是空话。
///
/// **预览不归后端管**：后端**不声明**“自己支不支持流式”—— 那种能力位
/// 会把“引擎种类”和“能不能预览”搅在一起，最后长出“网络引擎不支持
/// 实时预览”这种分支。预览有没有**只由用户开关**决定。
///
/// 热词（context biasing）只有本地后端有这条通道：词表有两个来源 ——
/// config.json 的 <c>hotwords</c> + <c>data/hotwords.txt</c>（mtime 热加载，改完立即生效），
/// 拼出来的格式是官方那套 <c>"Vocabulary: a, b, c."</c>。MiniMax 没有 context biasing 入口，
/// 那一路**不注入**热词（切换成功的通知里会明说，不静默装支持）。
/// </summary>
public interface IAsrBackend
{
    /// <summary>面板显示用的标识。</summary>
    string ModelId { get; }

    /// <summary>后端是否就绪（可以识别了）。</summary>
    bool Loaded { get; }

    /// <summary>加载后端，返回耗时秒数。失败抛 <see cref="AsrError"/>。</summary>
    double Load();

    /// <summary>切换模型规格（重新加载），返回耗时秒数。失败抛 <see cref="AsrError"/>。</summary>
    double SwitchModel(string modelTag);

    /// <summary>卸载后端：本地释放显存，网络关闭 HTTP 会话。</summary>
    void Unload();

    /// <summary>
    /// 识别音频，返回文本。
    ///
    /// <paramref name="audio"/> 是**单声道 int16** PCM；
    /// <paramref name="sampleRate"/> 是**传入音频的实际采样率**（设备原生率，通常是 48 kHz）。
    /// </summary>
    string Transcribe(short[] audio, int sampleRate);
}

/// <summary>
/// 后端抽象基类：把热词合并、context biasing、int16 → float32 这些共用的东西放在这里。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/base.py 的 <c>ASRBackend</c>。
/// </summary>
public abstract class AsrBackendBase : IAsrBackend
{
    protected AsrBackendBase(IEnumerable<string>? hotwords = null, string? hotwordsFile = null)
    {
        Hotwords = new HotwordSource(hotwords, hotwordsFile);
    }

    /// <summary>热词来源（config 列表 + 词表文件的合并去重）。</summary>
    public HotwordSource Hotwords { get; }

    public abstract string ModelId { get; }

    public abstract bool Loaded { get; }

    public abstract double Load();

    public abstract double SwitchModel(string modelTag);

    public abstract void Unload();

    public abstract string Transcribe(short[] audio, int sampleRate);

    /// <summary>
    /// 把热词拼成识别提示（context biasing）。
    ///
    /// 官方把这个通道叫 **Context Biasing**：可以传任意文本
    /// （API 上限 10000 token），让模型在**音频模糊时优先选这些词** ——
    /// 公司名、产品代号、医疗术语、领域黑话都靠它校准。
    ///
    /// 格式用官方 HF 版的写法 <c>"Vocabulary: a, b, c."</c>：
    /// 模型在 SFT 阶段就是用这种格式训练上下文偏置的，
    /// 格式对上了才吃得到那份偏置，自己编一句提示效果会打折。
    ///
    /// **只有本地后端有这条通道**。MiniMax 那边没有 context biasing 入口 ——
    /// 它把这个方法覆写成空串，热词一律**不注入**（切换成功的通知里会明说这件事，
    /// 不静默装支持）。
    /// </summary>
    public virtual string BuildContext() => Hotwords.BuildContext();

    /// <summary>
    /// 把音频统一成 [-1, 1] 的 float32（单声道）。
    ///
    /// 翻译自 base.py 的 <c>ASRBackend._to_float32</c>。
    ///
    /// 录音器给的是 int16，但 librosa（重采样用）只吃浮点，
    /// 网络后端写 WAV 也要浮点刻度。
    /// 已经在 [-1, 1] 范围的浮点原样保留。
    ///
    /// ⚠ 注意顺序：**先记住原始 dtype**，再混音 ——
    /// mean 会把 int16 提升成 float64，之后再判断“是不是整数”
    /// 就永远判不到了 —— 那样 int16 的刻度（±32768）会被当成
    /// 已经归一化的浮点，直接 clip 成 ±1（全幅方波）。
    /// （C# 侧入口就是 int16，混音在 <see cref="Streaming.StreamingEngine"/> 里做。）
    /// </summary>
    public static float[] ToFloat32(short[] audio)
    {
        if (audio.Length == 0)
        {
            return Array.Empty<float>();
        }

        var result = new float[audio.Length];
        for (int i = 0; i < audio.Length; i++)
        {
            // int16 的刻度是 ±32768，除完正好落在 ±1
            float v = audio[i] / 32768.0f;
            result[i] = Math.Clamp(v, -1.0f, 1.0f);
        }

        return result;
    }
}

/// <summary>
/// 热词来源：config 列表 + 词表文件的**双来源合并去重**。
///
/// 翻译自 base.py 的 <c>ASRBackend._effective_hotwords</c> / <c>_build_context</c>。
///
/// 词表文件**每次识别时**检查 mtime —— 文件变了就重读，
/// 所以 <c>data/hotwords.txt</c> 改完立即生效，不用重启程序。
/// 文件不存在 / 读不了是正常情况（只用传入的列表），不报错。
/// </summary>
public sealed class HotwordSource
{
    private readonly List<string> _configWords;
    private readonly string? _hotwordsFile;

    // 词表文件的 mtime 缓存：命中就不重读（"每次识别都 stat 一下"是对的，
    // "每次识别都重读一遍文件"是浪费）。
    private DateTime? _fileMtime;
    private IReadOnlyList<string> _fileWords = Array.Empty<string>();

    /// <param name="configWords">config.json 里配的术语表。</param>
    /// <param name="hotwordsFile">术语表文件（默认 data/hotwords.txt）。</param>
    public HotwordSource(IEnumerable<string>? configWords, string? hotwordsFile)
    {
        _configWords = configWords is null ? new List<string>() : new List<string>(configWords);
        _hotwordsFile = string.IsNullOrWhiteSpace(hotwordsFile) ? null : hotwordsFile;
    }

    /// <summary>词表文件路径（null = 不读文件）。</summary>
    public string? HotwordsFile => _hotwordsFile;

    /// <summary>
    /// 合并两个来源的热词：构造时传入的 + 词表文件的。
    ///
    /// 去重口径与 Python 版**逐字一致**：
    /// - config 列表内部不去重（Python 那边是 <c>[w.strip() for w in self.hotwords ...]</c>，
    ///   只滤空、不去重）；
    /// - 词表文件内部的重复由文件读取时去掉（Python 的 <c>load_hotwords_file</c> 自己有 seen 集合）；
    /// - 词表文件的词相对**已经收集到的全部词**去重后再追加。
    /// </summary>
    public IReadOnlyList<string> EffectiveWords()
    {
        RefreshFileWords();

        var words = new List<string>();
        foreach (string raw in _configWords)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            words.Add(raw.Trim());
        }

        var seen = new HashSet<string>(words, StringComparer.Ordinal);
        foreach (string w in _fileWords)
        {
            if (seen.Add(w))
            {
                words.Add(w);
            }
        }

        return words;
    }

    /// <summary>
    /// 拼成 context biasing 提示串（官方格式 <c>"Vocabulary: a, b, c."</c>）。
    /// 没有词就返回空串。
    /// </summary>
    public string BuildContext()
    {
        IReadOnlyList<string> words = EffectiveWords();
        if (words.Count == 0)
        {
            return string.Empty;
        }

        return "Vocabulary: " + string.Join(", ", words) + ".";
    }

    /// <summary>
    /// 按 mtime 检查词表文件，变了才重读。
    ///
    /// ⚠ 与 Python 版一致的行为：文件**不存在**时走的是 <c>except OSError: pass</c> 分支 ——
    /// 缓存里的旧词**继续生效**（不会因为文件被删就把词表清空）。
    /// 这条看着像 bug，但它是 Python 版实际行为，这里如实翻译。
    /// </summary>
    private void RefreshFileWords()
    {
        if (_hotwordsFile is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(_hotwordsFile);
            if (!info.Exists)
            {
                // 文件不存在或不可访问 —— 不是错误（Python: except OSError: pass）
                return;
            }

            DateTime mtime = info.LastWriteTimeUtc;
            if (_fileMtime.HasValue && _fileMtime.Value == mtime)
            {
                // mtime 没变 —— 命中缓存，不重读
                return;
            }

            _fileWords = LoadHotwordsFile(_hotwordsFile);
            _fileMtime = mtime;
        }
        catch (IOException)
        {
            // 与 Python 的 OSError 分支同义：读不了不是错误
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 读术语表文件（一行一词，<c>#</c> 开头是注释，空行忽略，文件内去重）。
    ///
    /// 翻译自 voice-mikon/src/voice_mikon/config.py 的 <c>load_hotwords_file</c>。
    /// 文件不存在是正常情况 —— 没有术语而已，不算错误。
    /// </summary>
    public static IReadOnlyList<string> LoadHotwordsFile(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[asr] 术语表 {path} 读取失败: {ex.Message}");
            return Array.Empty<string>();
        }

        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string w = raw.Trim();
            if (w.Length == 0 || w.StartsWith('#'))
            {
                continue;
            }

            if (seen.Add(w))
            {
                words.Add(w);
            }
        }

        return words;
    }
}
