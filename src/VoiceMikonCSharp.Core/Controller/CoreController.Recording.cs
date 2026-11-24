using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using VoiceMikonCSharp.Core.Inject;
using VoiceMikonCSharp.Core.Levels;
using VoiceMikonCSharp.Engines;
using VoiceMikonCSharp.Engines.Streaming;
using VoiceMikonCSharp.Engines.Vad;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.Core.Controller;

/// <summary>录音编排：开始 / 停止 / 取消、实时预览、电平泵、识别与注入。</summary>
public sealed partial class CoreController
{
    /// <summary>预览线程最多等它退出多久（秒）。</summary>
    public const double PreviewStopTimeoutSeconds = 2.0;

    private IRecorder? _recorder;
    private StreamingEngine? _previewEngine;
    private StreamingEngine? _finalEngine;
    private Thread? _previewThread;
    private ManualResetEventSlim? _previewStop;
    private LevelPump? _levelPump;
    private Timer? _maxDurationTimer;

    // 会话代号：每开始一轮录音 +1。
    // 识别线程（阻塞推理，停不下来）靠它判断"我还是当前这一轮吗" ——
    // 取消或开了新一轮都会让它过期，过期线程醒来后直接丢弃结果，
    // 绝不注入文字（用户已经明确说了不要）。
    //
    // 注意：**"绝不注入作废文本"的主防线是这个代号**，
    // `_cancelled` 标志只是补充（先查代号再注入，竞态窗口里迟到的取消靠 session 递增兜住）。
    private int _session;
    private volatile bool _cancelled;

    // ---------- 录音控制 ----------

    /// <summary>热键 / 按钮的统一入口。</summary>
    public void ToggleRecording()
    {
        if (State == ControllerState.Recording)
        {
            StopRecording();
        }
        else if (State is ControllerState.Idle or ControllerState.Error)
        {
            StartRecording();
        }
        else
        {
            _log.Debug($"当前状态 {State},忽略切换请求");

            // V1 在这里只是静默忽略；现在补一条通知 —— 用户按了热键什么反应都没有，
            // 只会以为程序死了。
            if (State == ControllerState.Processing)
            {
                Reject("recording_rejected", ReasonProcessing, "识别中,暂不开始新的录音");
            }
            else
            {
                Reject("recording_rejected", ReasonModelNotReady, "模型还在加载,暂不能开始录音");
            }
        }
    }

    /// <summary>开始录音。</summary>
    public CommandResult StartRecording()
    {
        if (State == ControllerState.Recording)
        {
            return Reject("recording_rejected", ReasonAlreadyRecording, "已经在录音了");
        }

        if (State == ControllerState.Processing)
        {
            _log.Debug("识别中,暂不开始新的录音");
            return Reject("recording_rejected", ReasonProcessing, "识别中,暂不开始新的录音");
        }

        if (State == ControllerState.Starting)
        {
            // 模型还在加载。这一挡是功能级的：放行的话录音能开，
            // 停止时整段识别会直接抛"模型尚未加载"。
            // 热键路径 ToggleRecording 的 else 分支本来就不放行，
            // 这里挡的是**点悬浮球**那条直通路径。
            _log.Debug("模型还在加载,暂不能开始录音");
            return Reject("recording_rejected", ReasonModelNotReady, "模型还在加载,暂不能开始录音");
        }

        IRecorder recorder;
        try
        {
            recorder = CreateRecorder();
        }
        catch (Exception ex)
        {
            _log.Error($"录音启动失败: {ex.Message}");
            Notify("error", "recording_failed", "录音启动失败,请检查麦克风");
            return new CommandResult(false, ReasonRecorderStartFailed, "录音启动失败,请检查麦克风");
        }

        if (!recorder.Start())
        {
            Notify("error", "recording_failed", "录音启动失败,请检查麦克风");
            return new CommandResult(false, ReasonRecorderStartFailed, "录音启动失败,请检查麦克风");
        }

        _recorder = recorder;

        // 新的一轮：会话代号 +1，并清掉上一轮的取消标记。
        // 上一轮遗留的识别线程（如果还在跑）会因此过期，自动丢弃结果。
        _session++;
        _cancelled = false;

        SetState(ControllerState.Recording);

        // 新一轮录音：先把上一轮残留的预览文字清掉
        UpdateState(s => s with { PreviewText = string.Empty });

        // 电平泵跟着录音起（30fps 推**已映射**电平）
        StartLevelPump();

        // 最终位的引擎：整段引擎，最终结果只认它。
        // 先造好放在那儿，停止录音时把完整音频喂进去、close 出最终文本。
        int nativeRate = recorder.NativeRate > 0 ? recorder.NativeRate : Config.SampleRate;
        _finalEngine = MakeFinalEngine(nativeRate);

        StartPreview(recorder);

        // 单次录音上限（config.max_duration）：到点自动停。
        // ⚠ Python 版**没有**实现这条（配置项存在但控制器不认）——
        // 详见 docs/k3-controller.md 疑问 #3。
        StartMaxDurationTimer();

        return new CommandResult(true, string.Empty, "开始录音");
    }

    /// <summary>
    /// 停止录音：关流 → 后台线程跑最终识别 → 注入 → 校验 → 归还剪贴板 / 留。
    /// </summary>
    public void StopRecording()
    {
        IRecorder? recorder = _recorder;
        if (State != ControllerState.Recording || recorder is null)
        {
            return;
        }

        // 先停预览线程 —— 它用的是同一个引擎，不停掉会和下面的整段识别抢 GPU
        StopPreview();

        // 录音器的原生采样率（音频就是按这个采样率采的）
        int nativeRate = recorder.NativeRate > 0 ? recorder.NativeRate : Config.SampleRate;

        RecorderStopResult result = recorder.Stop();
        _recorder = null;

        // 录音器没了，电平也断了 —— 电平泵跟着收。
        StopLevelPump();
        StopMaxDurationTimer();

        double duration = result.Samples.Length / (double)nativeRate;
        _log.Info($"停止录音,时长 {duration:F1}s");

        // 录音留存：调试期需要**同一段真实录音**反复回放才能定位
        // "预览零出字"这类问题（日志分不清是没喂进去还是没识别出来）。
        // 存盘失败绝不影响主流程 —— 只是个调试基建，不值得为它中断识别。
        if (Config.SaveRecordings && _recordingSaver is not null)
        {
            try
            {
                _recordingSaver(result.Samples, nativeRate);
            }
            catch (Exception ex)
            {
                _log.Debug($"录音留存失败: {ex.Message}");
            }
        }

        // **不做任何前置判定**（时长 / 音量）：录到多少就送多少，整段交给模型。
        // 识别得到就是结果，识别不到就空 —— 该不该听，由模型说，不由本地阈值说。
        //
        // 以前这里有两道门（"录音太短"、"没有检测到声音"），初衷是防呆：
        // 怕用户误按热键白跑一次 GPU 推理。实际代价远大于收益：
        //   - 静音门用的是**整段平均 RMS**，静音占比越高越容易被误杀 ——
        //     说得越久、停顿越多，反而越容易被判成"没说话"；
        //   - 阈值卡在麦克风增益偏低时的语音主体电平附近，说话轻一点就整段丢。
        // 用户宁可自己删掉一句识别错的，也不要自己的话被本地阈值替它拦下。
        SetState(ControllerState.Processing);

        int session = _session;
        var worker = new Thread(() => Process(result.Samples, nativeRate, session))
        {
            IsBackground = true,
            Name = "asr-run",
        };
        worker.Start();
    }

    /// <summary>
    /// 放弃当前这一轮：不识别、不注入。
    ///
    /// 录音中：停掉录音器和预览线程，音频直接丢掉。
    /// 识别中：推理是阻塞的，停不下来 —— 只能**标记**这一轮作废，
    /// 识别线程醒来后发现自己过期，于是丢掉结果、不注入文字。
    ///
    /// 两种情况都回到 IDLE（用户可以立刻开始下一轮）。胶囊显示「已取消」
    /// 靠的是**状态从录音 / 识别流回 idle** 这件事本身 ——
    /// 消费端（presenter）自己判断，不靠事件通知。
    /// </summary>
    public CommandResult CancelRecording()
    {
        if (State == ControllerState.Recording)
        {
            // 先把这轮标成作废，再停预览线程：
            // 预览线程可能正卡在一次识别里，它醒来后的 partial 回调
            // 必须已经知道自己过期了 —— 作废的文字一个字都不许推给界面。
            _cancelled = true;

            // 再停预览线程 —— 它用的是同一个引擎，不停掉会跟识别抢 GPU
            StopPreview();

            IRecorder? recorder = _recorder;
            _recorder = null;
            StopLevelPump();
            StopMaxDurationTimer();

            if (recorder is not null)
            {
                try
                {
                    recorder.Stop();
                }
                catch (Exception ex)
                {
                    _log.Debug($"取消录音时停止录音器失败: {ex.Message}");
                }
            }

            SetState(ControllerState.Idle);
            UpdateState(s => s with { PreviewText = string.Empty });
            return new CommandResult(true, string.Empty, "已取消");
        }

        if (State == ControllerState.Processing)
        {
            // 识别线程还在跑，停不掉；标记作废 + 立刻回 IDLE，
            // 免得用户被卡在"识别中"按不动热键
            _cancelled = true;
            SetState(ControllerState.Idle);
            UpdateState(s => s with { PreviewText = string.Empty });
            return new CommandResult(true, string.Empty, "已取消");
        }

        return new CommandResult(false, ReasonNotRecording, "当前没有进行中的录音");
    }

    /// <summary>造一个录音器（走可注入的工厂）。</summary>
    private IRecorder CreateRecorder()
    {
        if (_recorderFactory is null)
        {
            throw new InvalidOperationException("未装配录音器（CoreController 需要一个 recorderFactory）");
        }

        return _recorderFactory(InputDevice);
    }

    // ---------- 实时预览 ----------

    /// <summary>造最终位的引擎。</summary>
    private StreamingEngine MakeFinalEngine(int sampleRate)
    {
        // **固定整段引擎** —— 最终结果必须永远来自整段识别。
        // 分句预览带来的不一致只允许停留在预览层。
        return new FullPassEngine(Engine, sampleRate);
    }

    /// <summary>造预览位的引擎。</summary>
    private StreamingEngine MakePreviewEngine(int sampleRate, Action<string> partial, Func<double?>? noiseFloor)
    {
        // 默认按模块级常量 PreviewEngine 选（分句 / 整段回退）。
        //
        // noise_floor 是 Recorder 的底噪估计读数，分句引擎的 VAD 门随它走 ——
        // 门限是"测出来的"，不是按某次录音标定的固定值（固定门被环境变化穿透过，
        // 实测切句判定整轮失效）。
        if (PreviewEngine == PreviewEngineKind.FullPass)
        {
            // 整段引擎不切句，压根不造判定器，也就不会因为 Silero 不可用而降级。
            return new FullPassEngine(Engine, sampleRate, partial);
        }

        VoiceDetector detector = VadFactory.Make(Config.PreviewVad, sampleRate, noiseFloor, OnVadFallback);
        return new SentencePreviewEngine(Engine, sampleRate, partial, noiseFloor, detector: detector);
    }

    /// <summary>
    /// 预览引擎的 partial 回调（跑在预览线程里）。
    ///
    /// 必须过 session 检查：作废的一轮（被取消，或已被新一轮取代）
    /// 的文字**绝不推 UI** —— 和最终结果一个待遇。
    /// </summary>
    private Action<string> MakePartialCallback(int session)
    {
        return text =>
        {
            if (IsStale(session))
            {
                _log.Debug("这一轮已作废,丢弃预览文字");
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                UpdateState(s => s with { PreviewText = text });
            }
        };
    }

    /// <summary>
    /// 启动实时预览线程。
    ///
    /// **预览有没有，只由 <c>config.show_preview</c> 决定** —— 引擎不参与这个决定：
    /// 本地 / 网络都是"能建就建"，开关关着就一个预览线程都不建、一个请求都不发。
    ///
    /// 开关是**下一轮生效**：每轮录音都重走这里，上一轮的开关状态不影响这一轮 ——
    /// 不做广播机制（开关变化没有事件要广播，下一轮重读配置就够）。
    /// </summary>
    private void StartPreview(IRecorder recorder)
    {
        StopPreview();

        if (!Config.ShowPreview)
        {
            // 关着：不建预览线程、不建预览引擎、零调用。
            _log.Info("预览开关关闭,跳过预览线程");
            return;
        }

        Func<short[]?>? pull = MakeChunkPuller(recorder);
        if (pull is null)
        {
            // 录音器拿不到音频 → 没有预览可做
            return;
        }

        // 快照 / 增量都是设备原生采样率，阈值和重采样都要按原生采样率算
        int nativeRate = recorder.NativeRate > 0 ? recorder.NativeRate : Config.SampleRate;

        StreamingEngine engine = MakePreviewEngine(
            nativeRate,
            MakePartialCallback(_session),
            () => SafeNoiseFloor(recorder));

        var stop = new ManualResetEventSlim(false);
        var thread = new Thread(() => PreviewLoop(stop, engine, pull))
        {
            IsBackground = true,
            Name = "asr-preview",
        };

        _previewStop = stop;
        _previewThread = thread;
        _previewEngine = engine;
        thread.Start();
    }

    /// <summary>取底噪（读失败就当没有 —— 别让一次异常把预览线程带走）。</summary>
    private static double? SafeNoiseFloor(IRecorder recorder)
    {
        try
        {
            return recorder.NoiseFloor;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 造一个"取录音增量"的函数。
    ///
    /// 优先用 <c>DrainChunks()</c>：它只给**新增**的音频，
    /// 不用每次都把全部音频重新拼一遍。
    ///
    /// 只有支持快照的录音器（测试里的假录音器）才退回差量拉取：
    /// 记住已经喂过多少样本，只把新增的部分切出来。效果一样，
    /// 只是每次仍然要拼一次全量（那是旧录音器的固有成本）。
    /// </summary>
    private static Func<short[]?>? MakeChunkPuller(IRecorder recorder)
    {
        if (recorder is IDrainableRecorder drainable)
        {
            return () =>
            {
                short[] chunk = drainable.DrainChunks();
                return chunk.Length == 0 ? null : chunk;
            };
        }

        int fed = 0;
        return () =>
        {
            short[] audio = recorder.Snapshot();
            if (audio.Length == 0)
            {
                return null;
            }

            int total = audio.Length;
            if (total <= fed)
            {
                return null;
            }

            var chunk = new short[total - fed];
            Array.Copy(audio, fed, chunk, 0, chunk.Length);
            fed = total;
            return chunk;
        };
    }

    /// <summary>
    /// 停掉预览线程。
    ///
    /// 最多等 2 秒 —— 线程可能正卡在一次推理里，不能让它把
    /// <c>StopRecording()</c> 堵住。超时就放弃，让它自己退出。
    ///
    /// 预览引擎由**预览线程**负责 close（它才是唯一的写者）：
    /// 线程退出时会把最后一句认掉，close 出来的 final 只用于收尾，
    /// 最终结果不取它。线程没退出时控制器不抢着 close ——
    /// 那会和正在进行的识别撞车。
    /// </summary>
    private void StopPreview()
    {
        ManualResetEventSlim? stop = _previewStop;
        Thread? thread = _previewThread;
        _previewStop = null;
        _previewThread = null;

        stop?.Set();

        if (thread is not null && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(PreviewStopTimeoutSeconds));
            if (thread.IsAlive)
            {
                _log.Warning($"预览线程未在 {PreviewStopTimeoutSeconds:F0} 秒内退出(可能卡在推理中)");
            }
        }

        stop?.Dispose();
    }

    /// <summary>
    /// 后台线程：把录音增量喂给预览位的引擎。
    ///
    /// 绝不能把这个循环放到录音回调线程或 UI 线程里 ——
    /// 识别是阻塞的，会卡住录音或界面。
    ///
    /// 切句识别是在 <c>Feed</c> 内部同步做的：断句间隔天然大于 300ms，
    /// 单句识别几百毫秒，不会积压。识别期间攒下的音频会在下一次拉取时一次性喂进来
    /// （引擎内部按固定帧长切分，句边界不会丢）。
    /// </summary>
    private void PreviewLoop(ManualResetEventSlim stop, StreamingEngine engine, Func<short[]?> pull)
    {
        try
        {
            while (!stop.IsSet)
            {
                short[]? chunk;
                try
                {
                    chunk = pull();
                }
                catch (Exception ex)
                {
                    _log.Debug($"取录音增量失败: {ex.Message}");
                    chunk = null;
                }

                if (chunk is null || chunk.Length == 0)
                {
                    // 暂时没有新音频（刚开始录音 / 已经停了）
                    stop.Wait(TimeSpan.FromMilliseconds(50));
                    continue;
                }

                try
                {
                    engine.Feed(chunk);
                }
                catch (Exception ex)
                {
                    // 喂音频失败（引擎异常）只跳过这一块，线程绝对不能死
                    _log.Debug($"预览喂音频失败: {ex.Message}");
                }
            }
        }
        finally
        {
            // 预览引擎归这条线程所有：退出前 close 掉它
            try
            {
                engine.Close();
            }
            catch (Exception ex)
            {
                _log.Debug($"关闭预览引擎失败: {ex.Message}");
            }
        }
    }

    // ---------- 电平 ----------

    /// <summary>起电平泵（30fps）。</summary>
    private void StartLevelPump()
    {
        IRecorder? recorder = _recorder;
        if (recorder is null)
        {
            return;
        }

        var pump = new LevelPump(recorder);

        // 只订阅**一个**回调，再由它转发给当前的订阅者列表 ——
        // 这样"电平泵起来之后才订阅"的人也能收到（对齐 Python 的实时查表）。
        pump.Subscribe(PublishLevels);

        _levelPump = pump;
        pump.Start();
    }

    /// <summary>停电平泵。</summary>
    private void StopLevelPump()
    {
        LevelPump? pump = _levelPump;
        _levelPump = null;
        pump?.Stop();
    }

    /// <summary>推给订阅者（一个炸了不影响其它）。</summary>
    private void PublishLevels(LevelFrame frame)
    {
        Action<LevelFrame>[] targets;
        lock (_lock)
        {
            targets = _levelListeners.ToArray();
        }

        foreach (Action<LevelFrame> cb in targets)
        {
            try
            {
                cb(frame);
            }
            catch (Exception ex)
            {
                _log.Debug($"电平订阅者回调异常: {ex.Message}");
            }
        }
    }

    // ---------- 单次录音上限 ----------

    /// <summary>起"单次录音上限"定时器（config.max_duration）。</summary>
    private void StartMaxDurationTimer()
    {
        StopMaxDurationTimer();

        if (Config.MaxDuration <= 0)
        {
            return;
        }

        _maxDurationTimer = new Timer(
            _ =>
            {
                if (State != ControllerState.Recording)
                {
                    return;
                }

                _log.Info($"达到单次录音上限({Config.MaxDuration:F0}s),自动停止");
                Notify("info", "recording_timeout", $"已达到单次录音上限({Config.MaxDuration:F0}秒),自动停止");
                StopRecording();
            },
            null,
            TimeSpan.FromSeconds(Config.MaxDuration),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>停"单次录音上限"定时器。</summary>
    private void StopMaxDurationTimer()
    {
        Timer? timer = _maxDurationTimer;
        _maxDurationTimer = null;
        timer?.Dispose();
    }

    // ---------- 识别与注入 ----------

    /// <summary>这一轮是不是已经作废了（被取消，或被新的一轮取代）。</summary>
    private bool IsStale(int session) => _cancelled || session != _session;

    /// <summary>
    /// 识别 + 注入（跑在后台线程里）。
    /// </summary>
    private void Process(short[] audio, int sampleRate, int session)
    {
        string text;
        try
        {
            // 最终位：整段引擎。喂完整音频、close 出最终文本 ——
            // 和以前直接调 engine.transcribe(audio, rate) 完全等价。
            StreamingEngine? final = _finalEngine;
            if (final is null)
            {
                // 兜底：没走过 StartRecording（比如测试直接摆状态）也能跑
                final = MakeFinalEngine(sampleRate);
                _finalEngine = final;
            }

            final.Feed(audio);
            text = final.Close();
        }
        catch (AsrError ex)
        {
            if (IsStale(session))
            {
                return;
            }

            SetState(ControllerState.Idle);
            Notify("error", "asr_failed", ex.Message);
            UpdateState(s => s with { PreviewText = string.Empty });
            return;
        }
        catch (Exception ex)
        {
            // 引擎抛任何异常都不能让工作线程死掉
            _log.Error($"识别异常: {ex}");
            if (IsStale(session))
            {
                return;
            }

            SetState(ControllerState.Idle);
            Notify("error", "asr_failed", $"识别失败: {ex.Message}");
            UpdateState(s => s with { PreviewText = string.Empty });
            return;
        }

        // 作废的一轮：直接丢弃（不注入、不发结果、不动状态 ——
        // 状态可能已经被新一轮的 RECORDING 接管了）
        if (IsStale(session))
        {
            _log.Debug("这一轮已被取消,丢弃识别结果");
            return;
        }

        LastText = text;

        // 识别完成的一瞬间就把最终文本亮出来（复用预览通道）：
        // 预览拼接版与整段识别版可能有差异，事实已变，屏幕不能停在过时的预览上 ——
        // 尤其注入前等修饰键的窗口（最长 2.5s）。
        // 之后的注入状态（已输入 / 等键警告 / 失败）都在这份最终文本上叠加。
        UpdateState(s => s with { PreviewText = text });

        if (string.IsNullOrWhiteSpace(text))
        {
            SetState(ControllerState.Idle);
            Notify("warning", "asr_failed", "没听清,请再说一遍");
            UpdateState(s => s with { PreviewText = string.Empty });
            return;
        }

        // 注入目标：**识别完成后、注入之前**取当前前台窗口（与 Python 版一致，
        // `core/controller.py` 的 `hwnd = get_foreground_window()`）。
        //
        // 为什么不是"开始录音时"记下的那个：说一两分钟、中途干别的事，
        // 用户早就忘了开始时在哪个窗口 —— 结束录音那一刻看着的窗口才是意图所在。
        //
        // 这一步保护的是"取前台 → 粘贴落地"这几百毫秒：这段里焦点又变了，
        // `Injector` 会把它还回去（`RestoreForeground` → AttachThreadInput +
        // SetForegroundWindow）。**识别那 1 秒里用户切了窗口，那个窗口就是落点** ——
        // 那正是用户的意思。
        IntPtr hwnd = NativeMethods.GetForegroundWindow();

        // 最后一道作废检查：上面 text.Trim() 到这里只有几毫秒，
        // 但用户的取消就落在这个窗口里的话，文字一样不该出门
        if (IsStale(session))
        {
            _log.Debug("注入前发现这一轮已被取消,丢弃");
            return;
        }

        InjectResult result = _injector(text, hwnd, Config.RestoreClipboard, Config.TrailingSpace);

        SetState(ControllerState.Idle);

        if (!result.Ok)
        {
            // 失败也要把原文带出去：只报一句"文本注入失败"，用户既看不到自己说了什么，
            // 也没法把它捞回来 —— 那段话就等于不存在了。
            // 胶囊会把它显示出来，并给一个「复制」按钮。
            //
            // 文案按**实况**分类（硬规则：诊断信息必须与失败原因一致 ——
            // 以前 modifier 类失败（根本没碰剪贴板）也报"复制到剪贴板失败"，
            // 把用户的排查方向带偏了）：
            //   modifier_timeout  → 修饰键一直没松开（与剪贴板无关）
            //   剪贴板里确实有我们的文本 → 只是没送出去，Ctrl+V 就能拿到
            //   剪贴板里没有 → 复制这一步就失败了，得手动重来
            Notification? failure = Injector.BuildFailureNotification(text, result);
            if (failure is not null)
            {
                PublishNotification(failure);
            }
        }

        // 最终结果到了，预览文字就没用了（最终结果始终来自整段识别，不是预览）
        UpdateState(s => s with { PreviewText = string.Empty });
    }

    /// <summary>把一条现成的通知发出去（失败不许拖垮调用方）。</summary>
    private void PublishNotification(Notification note)
    {
        try
        {
            _notifier.Publish(note.Level, note.Kind, note.Message, note.Fields);
        }
        catch (Exception ex)
        {
            _log.Debug($"发通知失败(kind={note.Kind}): {ex.Message}");
        }
    }
}
