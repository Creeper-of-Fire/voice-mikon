using System;
using System.Collections.Generic;
using System.Diagnostics;
using VmNext.Engines.Vad;

namespace VmNext.Engines.Streaming;

/// <summary>
/// 分句预览引擎:边说边切句,切一句认一句。
///
/// 做法：
///
/// 1. Feed 收到音频块,自己按固定帧长切帧
/// 2. 每帧交给**判定层**(“这是不是人声”)—— 能量法或 Silero VAD,
///    由 <see cref="VoiceDetector"/> 实现,切句逻辑不关心是哪一个
/// 3. 连续静音约 300ms → 断句;单句超过 20s → 强切
///    (一直说不停也得切,否则预览会一直干等)
/// 4. 断句后立刻识别这一句,把结果拼到已识别文本后面,
///    通过 partial 回调吐出去 —— 回调给的是**已识别句子的拼接**,
///    调用方按“整体替换”处理即可
/// 5. Close 把最后一句也认掉,返回全部句子的拼接
///
/// 拼接就是直接 join,不做任何跨句去重:句子是按静音切出来的,
/// 本来就不重叠,硬做去重反而会把正常内容吃掉。
/// 真出现重复了再说。
///
/// **切句状态机与判定层是分开的**:静音时长(0.3s)、强切(20s)、
/// 最短句长(0.2s)这些参数一个都没改,换的只是“这一帧是不是
/// 人声”的答案从哪来。
/// </summary>
public sealed class SentencePreviewEngine : StreamingEngine
{
    /// <summary>连续静音多久算断句(秒)。</summary>
    public const double SilenceSeconds = 0.3;

    /// <summary>单句上限(秒):一直说不停也得切,否则预览会一直干等。</summary>
    public const double MaxSeconds = 20.0;

    /// <summary>单句下限(秒):太短的片段识别不出东西,丢掉。</summary>
    public const double MinSeconds = 0.2;

    private readonly Action<string>? _partial;
    private readonly int _silenceSamples;
    private readonly int _maxSamples;
    private readonly int _minSamples;

    // 判定层:默认能量法(底噪估计 + 滞回双门)。
    // 传 detector 进来就换成别的实现(比如 Silero VAD)。
    private readonly VoiceDetector _detector;

    // 判定帧长由判定层决定(Silero 要 32ms 整窗,能量法 20ms)。
    // 切句阈值全按样本数算,所以帧长变了也不影响切句行为。
    private readonly int _frameSamples;

    // 判定器要求精确帧长时(Silero),跨块拼帧 —— 不足一窗的尾巴
    // 留到下一块再拼。录音器的块(1024 样本)通常切不进一个 32ms 窗。
    private short[]? _carry;

    private readonly List<short[]> _frames = new();   // 当前句的音频帧
    private int _frameCount;                          // 当前句的样本数
    private int _speechSamples;                       // 当前句里“有声”的样本数
    private int _silenceRun;                          // 连续静音的样本数
    private int _trailingSilence;                     // 句尾连续静音的样本数(切句时裁掉)
    private readonly List<string> _sentences = new();

    /// <param name="engine">底层识别器。</param>
    /// <param name="sampleRate">音频实际采样率。</param>
    /// <param name="partial">预览回调（给的是**已识别句子的拼接**）。</param>
    /// <param name="noiseFloor">
    /// 底噪估计的供给口:callable,每帧问一次当前底噪(null=还没收敛)。
    /// 不直接依赖 Recorder —— 依赖的是“谁来供数”这个窄接口,测试里
    /// 可以给个固定值的 lambda,行为完全可复现。
    /// 只有能量法判定器用它(Silero 不看底噪)。
    /// </param>
    /// <param name="silenceSeconds">断句所需的连续静音时长（秒）。</param>
    /// <param name="maxSeconds">单句上限（秒）。</param>
    /// <param name="minSeconds">单句下限（秒），太短的句子丢掉。</param>
    /// <param name="frameMs">判定帧长（毫秒，仅在没传 detector 时用）。</param>
    /// <param name="detector">判定层（null = 能量法）。</param>
    public SentencePreviewEngine(
        IAsrBackend engine,
        int sampleRate = 16000,
        Action<string>? partial = null,
        Func<double?>? noiseFloor = null,
        double silenceSeconds = SilenceSeconds,
        double maxSeconds = MaxSeconds,
        double minSeconds = MinSeconds,
        double frameMs = VadConstants.FrameMs,
        VoiceDetector? detector = null)
        : base(engine, sampleRate)
    {
        _partial = partial;
        _silenceSamples = Math.Max(1, (int)(silenceSeconds * SampleRate));
        _maxSamples = Math.Max(1, (int)(maxSeconds * SampleRate));
        _minSamples = Math.Max(1, (int)(minSeconds * SampleRate));

        _detector = detector ?? new EnergyVadDetector(SampleRate, noiseFloor, frameMs);
        _frameSamples = Math.Max(1, _detector.FrameSamples);
        _carry = _detector.RequiresExactFrames ? Array.Empty<short>() : null;
    }

    /// <summary>已识别出的句子（调试/测试用）。</summary>
    public IReadOnlyList<string> Sentences => _sentences;

    /// <summary>
    /// 滞后双门的当前状态（诊断用：回放工具要逐帧打印判定结果）。
    /// 判定层自己维护这个状态,这里只留一份镜像。
    /// </summary>
    public bool InSpeech => _detector.InSpeech;

    public override void Feed(short[] chunk, int channels = 1)
    {
        lock (SyncRoot)
        {
            if (Closed)
            {
                return;
            }

            short[]? data = AsAudio(chunk, channels);
            if (data is null)
            {
                return;
            }

            foreach (short[] frame in Split(data))
            {
                ProcessFrame(frame);
            }
        }
    }

    public override string Close()
    {
        lock (SyncRoot)
        {
            if (Final is not null)
            {
                return Final;
            }

            MarkClosed();

            // 判定器要求精确帧长时(Silero),最后可能还剩半窗没判过 ——
            // 补零凑满一窗判掉,否则这截音频永远进不了句子。
            // 补出来的零就是尾部静音,会被 Flush 的句尾裁剪去掉。
            FlushCarry();

            // 最后一句也要认掉 —— close() 的语义就是“给我最终文本”。
            // 但**不再回调 partial**:停止录音时预览条马上就要被
            // 最终结果接管,再推一次只会闪一下。
            Flush(notify: false);

            Final = string.Join(string.Empty, _sentences);
            return Final;
        }
    }

    // ---------- 内部 ----------

    /// <summary>
    /// 把音频块切成固定长度的分析帧。
    ///
    /// 必须切:识别期间攒下的一大块音频,如果不切就整块算 RMS,
    /// 里面的静音/有声边界会被抹平,句子永远断不出来。
    ///
    /// **帧长由判定层决定**(能量法 20ms / Silero 32ms 整窗)。
    /// 切法分两种:
    ///
    /// - 判定器不要求精确帧长(能量法):按老规矩切,块尾不足一帧的
    ///   零头也照样成帧 —— **保持旧行为不变**。
    /// - 判定器要求精确帧长(Silero):模型只吃固定长度的窗(喂错长度
    ///   直接抛 ValueError),所以跨块攒够一整窗才成帧,零头留到下一块。
    /// </summary>
    private IEnumerable<short[]> Split(short[] data)
    {
        int step = _frameSamples;
        if (_carry is null)
        {
            for (int start = 0; start < data.Length; start += step)
            {
                yield return Slice(data, start, Math.Min(step, data.Length - start));
            }

            yield break;
        }

        short[] buf = _carry.Length > 0 ? Concat2(_carry, data) : data;
        int usable = buf.Length - (buf.Length % step);
        _carry = Slice(buf, usable, buf.Length - usable);
        for (int start = 0; start < usable; start += step)
        {
            yield return Slice(buf, start, step);
        }
    }

    /// <summary>收尾:把攒着的半窗补零凑满,判掉。</summary>
    private void FlushCarry()
    {
        if (_carry is null || _carry.Length == 0)
        {
            return;
        }

        short[] carry = _carry;
        _carry = Array.Empty<short>();
        int pad = _frameSamples - carry.Length;
        if (pad > 0)
        {
            carry = Concat2(carry, new short[pad]);
        }

        ProcessFrame(carry);
    }

    /// <summary>
    /// 处理一个分析帧:更新判定状态,必要时断句。
    ///
    /// “这是不是人声”由**判定层**回答(能量法的滞后双门 /
    /// Silero 的双阈值),这里只把答案拿来喂切句状态机 ——
    /// 状态机本身一行没改。
    /// </summary>
    private void ProcessFrame(short[] frame)
    {
        bool speech = _detector.IsSpeech(frame);
        bool silent = !speech;

        if (!silent && _speechSamples == 0)
        {
            // 有声来了,但这一句前面还挂着静音(断句后残留的那点尾巴)——
            // 把它扔掉:句子从头开始,别让静音混进识别。
            Reset();
        }

        _frames.Add(frame);
        _frameCount += frame.Length;
        if (silent)
        {
            _silenceRun += frame.Length;
            _trailingSilence += frame.Length;
        }
        else
        {
            _silenceRun = 0;
            _trailingSilence = 0;
            _speechSamples += frame.Length;
        }

        if (_frameCount >= _maxSamples)
        {
            // 强切:一直说不停也得切
            Flush();
            return;
        }

        if (_silenceRun < _silenceSamples)
        {
            return;
        }

        if (_speechSamples > 0)
        {
            Flush();
        }
        else
        {
            // 全是静音,不是一句话 —— 丢掉。
            // 留着会把后面那句的音频前面塞一大段静音,
            // 识别时容易冒出幻觉文字。
            Reset();
        }
    }

    /// <summary>把当前句切出去:识别 + 回调。</summary>
    private void Flush(bool notify = true)
    {
        // ⚠ 这里必须**复制**一份:Python 那边 Reset() 是给 self._frames 重新赋值
        // (旧列表仍持有内容),C# 的 List.Clear() 会把内容一起清掉 —— 别名坑。
        List<short[]> frames = new(_frames);
        int total = _frameCount;
        int speech = _speechSamples;
        int trailing = _trailingSilence;
        Reset();

        if (frames.Count == 0 || total <= 0 || speech <= 0)
        {
            return;
        }

        short[] audio = Concat(frames);
        if (trailing > 0)
        {
            // 裁掉句尾的静音:它不属于这句话,留着只会拖慢识别
            audio = Slice(audio, 0, Math.Max(0, audio.Length - trailing));
        }

        if (audio.Length < _minSamples)
        {
            // 太短,识别不出东西 —— 丢掉,别往预览里塞垃圾
            return;
        }

        string text = Recognize(audio);
        if (text.Length == 0)
        {
            return;
        }

        // INFO 级:真机日志里要能直接看到“分句在出字”。
        // 之前这条链路上只有 debug,一旦预览零出字,日志一片空白,
        // 分不清是“没识别”还是“识别了没显示”——这两种的修法完全不同。
        // 一句一条,不会刷屏。
        Trace.WriteLine($"[asr.stream] 预览句 {audio.Length / (double)SampleRate:F1}s: {text}");
        _sentences.Add(text);
        if (notify)
        {
            Notify(string.Join(string.Empty, _sentences));
        }
    }

    /// <summary>清空当前句的状态。</summary>
    private void Reset()
    {
        _frames.Clear();
        _frameCount = 0;
        _speechSamples = 0;
        _silenceRun = 0;
        _trailingSilence = 0;
    }

    /// <summary>识别一句。失败只丢这一句,引擎不能死。</summary>
    private string Recognize(short[] audio)
    {
        try
        {
            return (Backend.Transcribe(audio, SampleRate) ?? string.Empty).Trim();
        }
        catch (Exception ex)
        {
            // 模型没加载 / 切模型时旧模型被换掉 —— 只跳过这一句。
            // WARNING 级(而不是 debug):这正是“预览零出字”的**第一种**
            // 可能原因,必须肉眼可见;而且只在真出错时才出现,不会刷屏。
            Trace.WriteLine($"[asr.stream] 分句识别失败: {ex.Message}");
            return string.Empty;
        }
    }

    private void Notify(string text)
    {
        if (_partial is null || text.Length == 0)
        {
            return;
        }

        try
        {
            _partial(text);
        }
        catch (Exception ex)
        {
            // 认出来了但推不出去 —— “预览零出字”的**第二种**可能原因。
            // 同样提到 WARNING:能区分“没识别”和“没显示”,比 debug 有用得多。
            Trace.WriteLine($"[asr.stream] partial 回调异常: {ex.Message}");
        }
    }

    private static short[] Concat2(short[] a, short[] b)
    {
        var result = new short[a.Length + b.Length];
        Array.Copy(a, 0, result, 0, a.Length);
        Array.Copy(b, 0, result, a.Length, b.Length);
        return result;
    }
}
