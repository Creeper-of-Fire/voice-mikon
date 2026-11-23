using System;
using System.Threading;
using VmNext.Audio;
using VmNext.Core;
using VmNext.Core.Controller;
using VmNext.Core.Inject;
using VmNext.Core.Interop;
using VmNext.Core.Presentation;
using VmNext.Engines;
using VmNext.Engines.Local;
using VmNext.Engines.Minimax;

namespace VmNext.App;

/// <summary>
/// 装配链 + 退出收尾。
///
/// 翻译自 voice-mikon/src/voice_mikon/__main__.py 的 <c>run_gui</c> / <c>run_headless</c> /
/// <c>_shutdown_and_exit</c>（GUI 窗口那几步本阶段没有，见下）。
///
/// ── 装配链（顺序有讲究，每一步都不能换位置）──
///
/// 1. DPI 感知      —— 必须在**任何窗口**创建之前，否则高分屏下布局全乱
/// 2. 剪贴板 owner  —— 必须在主线程建，owner 窗口归创建它的线程
/// 3. 配置 + 日志   —— 之后才有地方写日志（由 <see cref="Program"/> 在进这里之前做完）
/// 4. 通知出口      —— 通知的日志出口（拒绝 / 失败路径都要落到它）
/// 5. controller    —— 业务核心
/// 6. presenter     —— 把 core 的状态 / 通知翻译成显示状态
/// 7. 胶囊窗口      —— GUI 阶段才有（本阶段不做 GUI）
/// 8. 面板窗口      —— 同上
/// 9. 托盘 / 热键   —— 独立线程（本阶段没有托盘，只有热键）
/// 10. 引擎启动     —— 后台加载模型，主循环不阻塞
///
/// ── 退出收尾顺序：先停窗口推送，再注销热键 ──
/// 热键是全局资源，占着不放的话，用户"退出后马上重启"会因为注册冲突而失败
/// （热键注销要等进程真正退出）；而窗口推送停晚了，退出过程中还会往一个
/// 已经没人看的窗口里推东西。
/// </summary>
public sealed class AppHost : IDisposable
{
    private static readonly Logger _log = Log.GetLogger("main");

    private readonly object _lock = new();
    private readonly Injector _injector;
    private readonly ManualResetEventSlim _quit = new(false);
    private bool _finalized;

    /// <summary>装配整条链。</summary>
    /// <param name="options">命令行参数。</param>
    /// <param name="config">配置（已加载、已套用命令行覆盖）。</param>
    public AppHost(AppOptions options, Config config)
    {
        Options = options;
        Config = config;

        // ---- 1) DPI 感知 —— 必须在创建任何窗口之前 ----
        DpiMode = Dpi.SetDpiAwareness();
        _log.Info($"DPI 感知: {DpiMode}");

        // ---- 2) 剪贴板 owner 窗口：**必须在主线程创建** ----
        // owner 窗口归创建它的线程，线程退出窗口就没了，而 Windows 在 owner
        // 窗口销毁时会**连带清空剪贴板** —— 写在录音线程上会把"识别结果留在
        // 剪贴板"这个兜底直接废掉。这里趁主线程还空闲时建好，活到进程结束。
        Clipboard = new ClipboardService();
        Clipboard.EnsureOwnerWindow();

        // ---- 3) 通知出口 —— 拒绝 / 失败路径都落到它(data/notify.log) ----
        Notifier = NotificationHub.Notify;
        Notifier.Subscribe(PrintNotification);
        _log.Info($"通知出口: {Path.Combine(Paths.DataDir, "notify.log")}");

        _injector = new Injector(Clipboard);

        // ---- 4) controller ----
        Controller = new CoreController(
            config,
            backendFactory: MakeBackend,
            recorderFactory: MakeRecorder,
            injector: (text, hwnd, restore, trailingSpace) =>
                _injector.PasteText(text, hwnd, restore, trailingSpace),
            notifier: Notifier,
            recordingSaver: (audio, rate) => RecordingStore.SaveRecording(audio, rate),
            micProbe: ProbeMic,
            clipboard: Clipboard);

        // ---- 5) presenter ----
        Presenter = new VoiceBarPresenter(Controller, config, Notifier);
        Presenter.Install();
        Presenter.DisplayModeChanged += OnDisplayModeChanged;

        Controller.Subscribe(PrintState);
    }

    /// <summary>命令行参数。</summary>
    public AppOptions Options { get; }

    /// <summary>配置。</summary>
    public Config Config { get; }

    /// <summary>DPI 感知模式（already / per-monitor-v2 / …）。</summary>
    public string DpiMode { get; }

    /// <summary>剪贴板服务（owner 窗口在主线程）。</summary>
    public ClipboardService Clipboard { get; }

    /// <summary>通知中心。</summary>
    public NotificationHub Notifier { get; }

    /// <summary>业务核心。</summary>
    public CoreController Controller { get; }

    /// <summary>显示状态翻译层。</summary>
    public VoiceBarPresenter Presenter { get; }

    /// <summary>全局热键（无头自检模式下不注册）。</summary>
    public HotkeyBinding? Hotkey { get; private set; }

    /// <summary>
    /// 起热键 + 起引擎（后台加载模型，主循环不阻塞）。
    ///
    /// 热键注册**可能失败**（组合键被别的程序占着 —— 比如用户自己的
    /// voice-mikon 正开着）。失败只发一条 warning，不影响程序继续跑。
    /// </summary>
    public void Start()
    {
        if (!Options.Headless)
        {
            // ---- 9) 热键（独立线程）----
            var hotkey = new HotkeyBinding(Notifier);
            Hotkey = hotkey;

            bool hotkeyOk = false;
            try
            {
                hotkey.Start();
                hotkeyOk = hotkey.Register(Config.Hotkey, OnHotkey);
            }
            catch (HotkeyError ex)
            {
                _log.Error(ex.Message);
            }

            if (!hotkeyOk)
            {
                _log.Warning($"热键 {Config.Hotkey} 没注册上,只能用别的办法触发录音");
            }

            // 退出时由 controller 第一个停它（热键是全局资源）
            Controller.AttachHotkey(hotkey.Stop);
        }

        // ---- 10) 引擎启动（后台加载模型，主循环不阻塞）----
        Controller.Start();
        _log.Info($"已就绪。按 {Config.Hotkey} 开始录音,再按一次结束。");
    }

    /// <summary>
    /// 请求退出（UI 的「退出」按钮那条路）。
    ///
    /// 三个退出请求（托盘菜单 / 面板按钮 / Ctrl+C）都汇到这一条收尾路径上 ——
    /// 顺序完全一样，不会漏步骤。
    /// </summary>
    public void RequestQuit()
    {
        _log.Info("收到退出请求");
        _quit.Set();
    }

    /// <summary>等退出请求（主循环用它挡着）。</summary>
    public void WaitForQuit() => _quit.Wait();

    /// <summary>
    /// 退出收尾（幂等）。所有退出路都走这一条，避免漏步骤。
    ///
    /// 顺序：**先停窗口推送（presenter），再注销热键**。
    /// </summary>
    public void Shutdown()
    {
        lock (_lock)
        {
            if (_finalized)
            {
                return;
            }

            _finalized = true;
        }

        // 1) 先停"往界面推东西"—— 推给一个没人看的界面没有意义，还会拖慢退出。
        try
        {
            Presenter.Shutdown();
        }
        catch (Exception ex)
        {
            _log.Debug($"presenter 收尾异常: {ex.Message}");
        }

        // 2) 再注销热键 —— 它是全局资源，占着不放的话，"退出后马上重启"
        //    会因为注册冲突而失败。
        try
        {
            Hotkey?.Stop();
        }
        catch (Exception ex)
        {
            _log.Debug($"热键收尾异常: {ex.Message}");
        }

        // 3) core 收尾（里面还会再停一次热键 —— 幂等）
        try
        {
            Controller.Shutdown();
        }
        catch (Exception ex)
        {
            _log.Debug($"core 收尾异常: {ex.Message}");
        }

        // 4) 剪贴板 owner 窗口随进程退出被销毁时，Windows 会连带清空剪贴板 ——
        //    注入失败时留在剪贴板里的识别结果就没了。
        //    退出前先把所有权交还系统(owner 置 NULL)，内容留住。
        try
        {
            Clipboard.ReleaseOwnership();
        }
        catch (Exception ex)
        {
            _log.Debug($"剪贴板归还异常: {ex.Message}");
        }

        Console.Out.Flush();
        Console.Error.Flush();
    }

    /// <summary>释放（等价于 Shutdown）。</summary>
    public void Dispose() => Shutdown();

    // ---------- 装配用的工厂 ----------

    /// <summary>按后端名字造一颗后端。</summary>
    private IAsrBackend MakeBackend(string kind)
    {
        if (kind == CoreController.BackendMinimax)
        {
            return new MinimaxBackend(
                model: Config.MinimaxModel,
                baseUrl: Config.MinimaxBaseUrl,
                hotwords: Config.Hotwords,
                hotwordsFile: Paths.HotwordsPath,
                // data 目录必须由装配层注入 —— Engines 不自己猜位置。
                // 自猜的回落是 AppContext.BaseDirectory/data,`dotnet run` 时那是
                // bin/Debug/net*/data/,找不到仓库根的 data/.env,key 永远读不到。
                dataDir: Paths.DataDir);
        }

        return new LocalBackend(
            modelRoot: Config.ModelRoot,
            modelTag: Config.Model,
            language: Config.Language,
            hotwords: Config.Hotwords,
            hotwordsFile: Paths.HotwordsPath,
            mock: Options.Mock);
    }

    /// <summary>造一个录音器（Core 只认接口，具体设备在这里接进去）。</summary>
    private IRecorder MakeRecorder(int? device)
        => new RecorderAdapter(new Recorder(device, Config.SampleRate));

    /// <summary>探测麦克风（后台线程里跑，探测要 2 秒）。</summary>
    private (int? Index, string Description) ProbeMic()
    {
        DeviceChoice choice = DevicePicker.ChooseDevice(Config.InputDevice, Config.DeviceHints);
        return (choice.Index, choice.Description);
    }

    // ---------- 热键 ----------

    private void OnHotkey()
    {
        _log.Debug("热键触发");
        try
        {
            Controller.ToggleRecording();
        }
        catch (Exception ex)
        {
            _log.Error("热键处理异常", ex);
            Notifier.Publish(
                "error",
                "hotkey_callback_failed",
                "快捷键触发的动作失败了",
                new System.Collections.Generic.Dictionary<string, object?> { ["reason"] = ex.Message });
        }
    }

    // ---------- 控制台输出（_print_state / _print_notification 的翻译） ----------

    private void PrintState(AppState state)
    {
        BackendHealth health = state.BackendHealth;
        string detail = health.Reason.Length > 0 ? $" ({health.Reason})" : string.Empty;
        string model = state.Model.Length > 0 ? state.Model : "-";
        Console.WriteLine($"[状态] {state.State} | 后端={state.Backend} | 健康={health.Status}{detail} | 模型={model}");

        if (!string.IsNullOrEmpty(state.PreviewText))
        {
            Console.WriteLine($"[预览] {state.PreviewText}");
        }
    }

    private void PrintNotification(Notification note)
    {
        Console.WriteLine($"[通知] {note.Level} | {note.Kind} | {note.Message}");
    }

    private void OnDisplayModeChanged(object? sender, DisplayModeChangedEventArgs e)
    {
        string message = e.Payload.Message;
        Console.WriteLine($"[显示] {e.Mode}{(message.Length > 0 ? " | " + message : string.Empty)}");
    }
}
