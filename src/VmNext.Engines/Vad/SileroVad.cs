using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VmNext.Engines.Vad;

/// <summary>
/// Silero VAD 判定器：神经网络版的“这 一帧是不是人声”。
///
/// **模型文件随包走、离线优先**：<c>silero_vad.onnx</c> 放在
/// <c>Models/</c>（随程序分发），运行时**不联网**下载。上游仓库 snakers4/silero-vad，
/// MIT 许可，允许再分发。
///
/// **为什么换成 ONNX（而不是 torch.jit）**：Silero 只是**判定层**，
/// 它在整条链路上最轻，却会把 torch 拖进来 —— 网络后端（MiniMax）模式下本来可以
/// 零 torch，结果光一个 VAD 就把 torch + 几百 MB 内存拖上桌。实测同一段录音，
/// 只造判定器跑 1000 窗：torch 版进程 RSS 222MB、ONNX 版 70MB
/// （其中 numpy 基线 25MB）；载入 1.36s → 0.23s，单窗 0.39ms → 0.13ms。
/// 判定结果几乎逐帧一致（实测最大概率偏差 1.7e-06，滞回后的逐帧判定完全相同）。
///
/// **为什么是神经网络**：能量法只看 RMS 高低，分不清“人在小声说话”和
/// “环境突然吵了”。真机实测里换环境后底噪抬升，能量门限整轮失效，
/// 预览基本不出字。Silero 是训练过的模型，能把人声和噪声分开。
///
/// **采样率**：官方 README 只写了 8000 Hz 和 16000 Hz，但模型自己还认
/// 16000 的整数倍（内部按 <c>step = sr // 16000</c> 抽取到 16k 再算）。实测能跑的组合：
///
///     8000 Hz  → 256 样本/窗
///     16000 Hz → 512 样本/窗
///     32000 Hz → 1024 样本/窗
///     48000 Hz → 1536 样本/窗
///
/// 全部是 **32ms** 的窗（256/8000 = 512/16000 = 1024/32000 = 1536/48000）。
/// 44100 Hz 这类“不是 16k 整数倍”的率跑不了，那种设备自动降级到能量法并发通知。
///
/// **本项目按设备原生率采集**（常见 48k），支持的率直接按窗长喂，
/// 不做重采样 —— 重采样会引入混叠失真，而原生率喂进去模型自己会抽。
///
/// **ONNX 图的调用协议**（照上游 <c>src/silero_vad/utils_vad.py</c> 里的
/// <c>OnnxWrapper</c> 来，不是自己猜的）：
///
///     输入  input : float32[batch, N]   N = 上一窗尾巴 + 当前窗
///                   state: float32[2, batch, 128]  LSTM 的 h/c
///                   sr   : int64           16k 或 8k
///     输出  output: float32[batch, 1]    语音概率
///                   stateN: float32[2, batch, 128] 新的 LSTM 状态
///
/// 关键两点：
///
/// 1. **模型只认 16k / 8k 两档**，48k/32k 必须先抽稀（<c>step = sr//16000</c>）
///    再喂 —— 直接喂 1536 样本的原生窗，ONNX 图会在 LSTM 上直接报形状错。
/// 2. **必须拼上“上一窗尾巴”（context prepending）**。上游 <c>OnnxWrapper</c>
///    在每次调用前把上一次的输入尾部（context_size 样本）接在当前窗前面
///    一起喂：16k 拼 64 样本（64+512=576），8k 拼 32 样本（32+256=288）。
///    模型是在带 context 的形态下训练的 —— 实测不拼 context，概率会直接
///    塌到 0.0006 上下（全是静音），判定彻底失灵。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/vad_silero.py。
/// </summary>
public sealed class SileroVadDetector : VoiceDetector
{
    /// <summary>模型文件（随程序分发）。</summary>
    public const string ModelFileName = "silero_vad.onnx";

    /// <summary>
    /// 采样率 → 每次推理需要的样本数（每档都是 32ms 的窗）。
    ///
    /// 这是**判定层喂进去的原生率窗长**：8k/16k 是模型原生支持的；32k/48k
    /// 是 16k 的整数倍，推理时按 <c>step = sr // 16000</c> 抽取到 16k 再算 ——
    /// 所以喂进去的窗长要按原生率乘上去（48k → 1536 样本，抽完正好 512）。
    ///
    /// 实测：48000/1536、32000/1024、16000/512、8000/256 全部 OK；
    /// 44100/24000 直接不支持（降级能量法）。
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> WindowSamples = new Dictionary<int, int>
    {
        [8000] = 256,
        [16000] = 512,
        [32000] = 1024,
        [48000] = 1536,
    };

    /// <summary>
    /// 采样率 → 推理计划 (模型内部率, 抽稀步长, 抽稀后的窗长, context 长度)。
    ///
    /// 这几个常量全部来自上游 <c>utils_vad.py</c> 的 <c>OnnxWrapper</c>：
    ///   - 抽稀步长 <c>step = sr // 16000</c>（只对 16k 的整数倍生效）
    ///   - 模型窗长 <c>num_samples = 512 if sr == 16000 else 256</c>
    ///   - context 长度 <c>context_size = 64 if sr == 16000 else 32</c>
    ///     （和 TorchScript 版模型里的 <c>context_size_samples</c> 属性同值）
    ///
    /// 8k 那一档**不抽稀**：8000 不是 16000 的整数倍，上游是让它走模型里
    /// 专门的 8k 分支（_model_8k），ONNX 图里对应按 <c>sr</c> 走的 If 分支。
    /// </summary>
    public static readonly IReadOnlyDictionary<int, (int ModelRate, int Step, int InnerWindow, int ContextSamples)> Plans =
        new Dictionary<int, (int, int, int, int)>
        {
            [8000] = (8000, 1, 256, 32),
            [16000] = (16000, 1, 512, 64),
            [32000] = (16000, 2, 512, 64),
            [48000] = (16000, 3, 512, 64),
        };

    /// <summary>LSTM 状态形状：2 = [h; c] 拼在一起（batch=1, hidden=128）。</summary>
    private static readonly int[] StateShape = { 2, 1, 128 };

    /// <summary>
    /// 进入语音的阈值：概率 ≥ 这个值算“开始说话”。
    ///
    /// 取官方推荐值 0.5（“lazy 0.5 is pretty good for most datasets”）。
    /// 项目原则是“宁可把底噪包进句子，不吞轻声开头” —— 偏向召回，
    /// 所以开门不往上调。
    /// </summary>
    public const double SpeechOnThreshold = 0.5;

    /// <summary>
    /// 退出语音的阈值：概率 &lt; 这个值算“停止说话”。
    ///
    /// 官方 get_speech_timestamps 的负阈值默认是 <c>threshold - 0.15</c>（= 0.35）。
    /// 比开门低，中间留一条滞回带 —— 和能量法的双门同构：
    /// 只有一个门的话，概率在门附近抖一下就会翻转，连续静音永远攒不满，
    /// 该切的时候切不出来。
    /// </summary>
    public const double SpeechOffThreshold = 0.35;

    // 进程级共享的 InferenceSession。
    //
    // **为什么能共享**：ONNX 图的 LSTM 状态是**显式**的 —— state 从入参进、
    // stateN 从出参出，谁调用谁拿着。所以同一个 session 能同时服务多颗
    // 判定器，天然不会串味。
    //
    // **线程安全**：onnxruntime 的 InferenceSession.Run() 是线程安全的
    // （状态在入参/出参里，不在 session 里），多线程并发调用同一个 session
    // 不会串数据。
    private static readonly Dictionary<string, InferenceSession> SessionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SessionLock = new();

    private readonly int _sampleRate;
    private readonly int _windowSamples;
    private readonly int _modelRate;
    private readonly int _step;
    private readonly int _innerWindow;
    private readonly int _contextSamples;
    private readonly InferenceSession _session;

    private bool _inSpeech;
    private float[] _state = new float[2 * 1 * 128];
    private float[] _context;

    public SileroVadDetector(int sampleRate = 16000, string? modelPath = null)
    {
        _sampleRate = sampleRate;
        if (!WindowSamples.TryGetValue(sampleRate, out int window))
        {
            throw new SileroUnavailable(
                "unsupported_sample_rate",
                $"采样率 {sampleRate} Hz 不受支持（支持 {string.Join("/", WindowSamples.Keys)} Hz）");
        }

        _windowSamples = window;
        (_modelRate, _step, _innerWindow, _contextSamples) = Plans[sampleRate];

        // session 是进程级共享的；状态（state / context）是每颗判定器自己的 ——
        // 这就是“不串味”的结构保证。
        _session = LoadSession(modelPath ?? ResolveModelPath());

        _context = new float[_contextSamples];

        // 模型是 LSTM，带着上一轮的状态会判歪 —— 每轮先重置。
        Reset();
    }

    public override int FrameSamples => _windowSamples;

    /// <summary>
    /// Silero 只吃**精确**窗长（窗长不对，ONNX 图会直接报形状错），
    /// 所以引擎必须跨块拼帧，不足一窗的留到下一块。
    /// </summary>
    public override bool RequiresExactFrames => true;

    /// <summary>
    /// 重置滞回状态 + 模型状态（LSTM 的 h/c + context）。
    ///
    /// ONNX 的状态是显式的两份：<c>state</c>（LSTM 的 h/c）由 <c>Run()</c>
    /// 返回、调用方自己留，<c>context</c>（上一窗尾巴）也是调用方自己留的。
    /// 两份一起清零 = 每轮录音从干净状态开始，等价于 TorchScript 版的
    /// <c>reset_states()</c>。
    /// </summary>
    public override void Reset()
    {
        _inSpeech = false;
        _state = new float[2 * 1 * 128];
        _context = new float[_contextSamples];
    }

    /// <summary>
    /// 判定这一帧是不是人声（带滞回）。
    ///
    /// 迟滞方向和能量法一致：不在说话态时用开门阈值，说话态中用关门阈值。
    /// </summary>
    public override bool IsSpeech(short[] frame)
    {
        double prob = Probability(frame);
        if (_inSpeech)
        {
            if (prob < SpeechOffThreshold)
            {
                _inSpeech = false;
            }
        }
        else if (prob >= SpeechOnThreshold)
        {
            _inSpeech = true;
        }

        return _inSpeech;
    }

    public override bool InSpeech => _inSpeech;

    /// <summary>
    /// 跑一次推理，返回语音概率。
    ///
    /// 归一化到 [-1, 1] 再喂 —— 模型是这么训练的。
    ///
    /// 三步走（和上游 OnnxWrapper 同序）：抽稀到模型内部率 → 拼上
    /// 上一窗尾巴（context）→ Run()，把新 state 和新 context 收下。
    /// </summary>
    public double Probability(short[] frame)
    {
        if (frame.Length != _windowSamples)
        {
            // TorchScript 版是模型自己抛 ValueError；ONNX 图的输入长度
            // 是动态的，喂错了不一定报错，只会算出一个看着像回事但其实
            // 不对的值 —— 所以这里自己把住这道门，错要错得明白。
            throw new ArgumentException(
                $"窗口样本数不对: 期望 {_windowSamples},实际 {frame.Length}(采样率 {_sampleRate} Hz)");
        }

        var buf = new float[frame.Length];
        for (int i = 0; i < frame.Length; i++)
        {
            buf[i] = frame[i] / 32768.0f;
        }

        // 抽稀到模型内部率（48k → 16k 每 3 个取 1 个）。
        if (_step > 1)
        {
            int inner = buf.Length / _step;
            var decimated = new float[inner];
            for (int i = 0; i < inner; i++)
            {
                decimated[i] = buf[i * _step];
            }

            buf = decimated;
        }

        if (buf.Length != _innerWindow)
        {
            // ONNX 图只吃 512（16k）/256（8k）两种窗长，喂别的长度会在
            // LSTM 上报一串看不懂的形状错 —— 自己先把话说明白。
            throw new ArgumentException(
                $"抽稀后窗口样本数不对: 期望 {_innerWindow},实际 {buf.Length}(采样率 {_sampleRate} Hz)");
        }

        // 拼上“上一窗尾巴”。模型是在带 context 的形态下训练的 ——
        // 少这一段,概率会塌到 0.0006 上下,判定彻底失灵。
        int total = _contextSamples + buf.Length;
        var xin = new float[total];
        Array.Copy(_context, 0, xin, 0, _contextSamples);
        Array.Copy(buf, 0, xin, _contextSamples, buf.Length);

        // ORT 的输入：input[1, N]、state[2,1,128]、sr(标量 int64)
        using var inputValue = OrtValue.CreateTensorValueFromMemory(xin, new long[] { 1, total });
        using var stateValue = OrtValue.CreateTensorValueFromMemory(_state, new long[] { 2, 1, 128 });
        using var srValue = OrtValue.CreateTensorValueFromMemory(
            new long[] { _modelRate },
            Array.Empty<long>());

        var inputs = new Dictionary<string, OrtValue>
        {
            ["input"] = inputValue,
            ["state"] = stateValue,
            ["sr"] = srValue,
        };

        using var runOptions = new RunOptions();
        using IDisposableReadOnlyCollection<OrtValue> results =
            _session.Run(runOptions, inputs, new[] { "output", "stateN" });

        float prob = 0f;
        float[] newState = _state;
        int index = 0;
        foreach (OrtValue item in results)
        {
            if (index == 0)
            {
                prob = item.GetTensorDataAsSpan<float>()[0];
            }
            else
            {
                newState = item.GetTensorDataAsSpan<float>().ToArray();
            }

            index++;
        }

        _state = newState;

        // 新 context = 本次输入（context + 当前窗）的最后 context_samples 个样本
        var newContext = new float[_contextSamples];
        Array.Copy(xin, total - _contextSamples, newContext, 0, _contextSamples);
        _context = newContext;

        return prob;
    }

    /// <summary>拿一个进程级共享的 InferenceSession。失败抛 <see cref="SileroUnavailable"/>。</summary>
    private static InferenceSession LoadSession(string modelPath)
    {
        lock (SessionLock)
        {
            if (SessionCache.TryGetValue(modelPath, out InferenceSession? cached))
            {
                return cached;
            }

            if (!File.Exists(modelPath))
            {
                throw new SileroUnavailable("model_missing", $"模型文件不存在: {modelPath}");
            }

            InferenceSession session;
            try
            {
                var options = new SessionOptions
                {
                    // 单线程：一窗才 32ms，线程调度开销比推理本身还大；
                    // 上游 OnnxWrapper 也是这么配的。
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = 1,
                };

                // 显式钉 CPU：VAD 这点算力不值得去抢 GPU，而且网络后端
                // 模式下 GPU 可能压根没启用。
                options.AppendExecutionProvider_CPU();

                session = new InferenceSession(modelPath, options);
            }
            catch (Exception ex)
            {
                throw new SileroUnavailable("model_load_failed", $"模型加载失败({modelPath}): {ex.Message}");
            }

            SessionCache[modelPath] = session;
            return session;
        }
    }

    /// <summary>
    /// 找模型文件：先看程序目录下的 <c>Models/</c>（发布形态），
    /// 再退回源码目录（<c>src/VmNext.Engines/Models/</c>）。
    /// </summary>
    public static string ResolveModelPath()
    {
        string baseDir = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            Path.Combine(baseDir, "Models", ModelFileName),
            Path.Combine(baseDir, ModelFileName),
        };

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "src", "VmNext.Engines", "Models", ModelFileName));
            dir = dir.Parent;
        }

        foreach (string cand in candidates)
        {
            if (File.Exists(cand))
            {
                return cand;
            }
        }

        // 找不到就返回"最像的那个"，让上层报"模型文件不存在"的准确原因
        return candidates[0];
    }
}
