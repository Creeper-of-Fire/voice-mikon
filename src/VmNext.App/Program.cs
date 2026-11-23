using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia;
using VmNext.Core;
using VmNext.Core.Interop;

namespace VmNext.App;

/// <summary>
/// 应用入口。
///
/// <para>翻译自 voice-mikon/src/voice_mikon/__main__.py 的装配部分
/// （<c>main</c> / <c>run_gui</c> 的装配顺序 / <c>_shutdown_and_exit</c> /
/// headless 分支 / 超时）。</para>
///
/// ── 两种运行形态──
///
/// <list type="bullet">
/// <item><b>默认（GUI）</b>：启动 Avalonia，弹出胶囊覆盖层。窗口是 <c>WinExe</c>
/// ——双击运行时没有控制台窗口，程序安静地在后台跑。</item>
/// <item><c>--headless</c>：<b>自检</b>。装配 core，等引擎就绪后干净退出
/// （退出码 0；引擎加载失败 / 超时 = 2）。不是常驻服务。</item>
/// </list>
///
/// <para><b>WinExe 下怎么还能看到控制台输出</b>：从 cmd / PowerShell 启动时
/// 子进程**继承**父进程的控制台（GUI 子系统只是"不自己开控制台窗口"），
/// 输出照常走；双击启动时没有父控制台，<c>Console</c> 写到哪里都是丢弃 → 静默。
/// 保险起见再调一次 <c>AttachConsole(ATTACH_PARENT_PROCESS)</c>：
/// 万一父进程没传下控制台，附加一次就能拿到。</para>
///
/// 命令行：
/// <list type="bullet">
/// <item><c>--headless</c>：无头自检</item>
/// <item><c>--timeout &lt;秒&gt;</c>：等待引擎就绪的上限（默认 300）</item>
/// <item><c>--backend minimax|local</c>：覆盖配置里的后端</item>
/// <item><c>--config &lt;路径&gt;</c>：覆盖配置文件路径</item>
/// <item><c>--mock</c>：本地后端走 mock 模式（**不加载真模型**）</item>
/// </list>
/// </summary>
public static class Program
{
    /// <summary>附加到父进程的控制台（WinExe 用）。</summary>
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    /// <summary>STD_OUTPUT_HANDLE。</summary>
    private const int STD_OUTPUT_HANDLE = -11;

    /// <summary>入口。</summary>
    public static int Main(string[] args)
    {
        // 输出被重定向（管道 / 文件）时固定用 UTF-8：脚本与自动化读到的字节是
        // 确定的，不受本机 OEM 代码页影响。
        // 交互式控制台**保持系统默认** —— 那是用户看得懂的编码，
        // 强行改 UTF-8 在 GBK 控制台上会变成乱码。
        if (Console.IsOutputRedirected)
        {
            try
            {
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            }
            catch (Exception)
            {
                // 改不了编码不该拖垮程序
            }
        }

        AppOptions options = AppOptions.Parse(args);

        try
        {
            return Run(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"voice-mikon: 启动失败: {ex}");
            return 1;
        }
    }

    private static int Run(AppOptions options)
    {
        // ---- 1) DPI 感知 —— 必须在**任何窗口创建之前** ----
        // Avalonia 建窗时要用缩放换算尺寸，声明晚了窗口尺寸会与内容对不上。
        string dpiMode = Dpi.SetDpiAwareness();

        // ---- 3) 配置 + 日志（之后才有地方写日志）----
        Paths.EnsureDirs();
        Config config = Config.Load(options.ConfigPath);

        // 命令行覆盖后端（**不写回配置文件** —— 那是一次性覆盖，不是用户改的设置）。
        if (!string.IsNullOrWhiteSpace(options.Backend))
        {
            config.Backend = options.Backend.Trim().ToLowerInvariant();
        }

        Log.Setup(Paths.LogPath, config.LogLevel);
        Logger log = Log.GetLogger("main");

        log.Info(new string('=', 60));
        log.Info($"Voice Mikon 启动  热键={config.Hotkey}  模型={config.Model}  后端={config.Backend}  DPI={dpiMode}");

        // ---- 形态分流 ----
        // headless 两来路：命令行 --headless，或者配置里的 ui_mode=headless
        // （面板里的"无界面"档 —— 那个值到不了 GUI，只能改 config 手动开启）。
        if (options.Headless || string.Equals(config.UiMode, "headless", StringComparison.OrdinalIgnoreCase))
        {
            TryAttachParentConsole();
            return RunConsole(options, config);
        }

        return RunGui(options, config);
    }

    /// <summary>
    /// GUI 形态：启动 Avalonia，弹出胶囊覆盖层。
    /// </summary>
    private static int RunGui(AppOptions options, Config config)
    {
        Gui.App.StartupOptions = options;
        Gui.App.StartupConfig = config;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"voice-mikon: GUI 启动失败: {ex}");
            return 1;
        }
    }

    /// <summary>构建 Avalonia 应用（GUI 形态）。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Gui.App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// 控制台形态：装配整条链，等引擎就绪（<c>--headless</c>）或等 Ctrl+C（常驻服务）。
    ///
    /// GUI 阶段接管了"常驻服务"那一支；<c>--headless</c> 这一支保留成自检。
    /// </summary>
    private static int RunConsole(AppOptions options, Config config)
    {
        using var host = new AppHost(options, config);

        // 进程退出（关控制台 / 未捕获异常）也要走同一条收尾路径。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => host.Shutdown();

        host.Start();

        try
        {
            return options.Headless ? RunHeadless(host, options) : RunConsoleService(host);
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>
    /// 无头自检：等引擎就绪（或超时）后**干净退出**。
    ///
    /// 退出码：0 = 就绪；2 = 引擎加载失败 / 超时。
    /// </summary>
    private static int RunHeadless(AppHost host, AppOptions options)
    {
        Logger log = Log.GetLogger("main");
        log.Info($"无头模式已启动(等待引擎就绪,上限 {options.TimeoutSeconds:F0} 秒)");

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < options.TimeoutSeconds)
        {
            AppState snapshot = host.Controller.StateSnapshot;
            BackendHealth health = snapshot.BackendHealth;

            if (health.Ready)
            {
                Console.WriteLine("[无头] 引擎就绪");
                return 0;
            }

            if (health.Failed)
            {
                Console.WriteLine($"[无头] 引擎加载失败: {health.Reason}");
                return 2;
            }

            Thread.Sleep(200);
        }

        Console.WriteLine("[无头] 等待引擎就绪超时");
        return 2;
    }

    /// <summary>
    /// 控制台常驻服务：装配整条链，等 Ctrl+C。
    /// </summary>
    private static int RunConsoleService(AppHost host)
    {
        Logger log = Log.GetLogger("main");

        Console.CancelKeyPress += (_, e) =>
        {
            // 拦下默认的"立刻杀掉进程"，让我们按顺序收尾。
            e.Cancel = true;
            log.Info("收到 Ctrl+C");
            host.RequestQuit();
        };

        log.Info("控制台服务已启动。按 Ctrl+C 退出。");
        host.WaitForQuit();

        return 0;
    }

    /// <summary>
    /// 附加到父进程的控制台。
    ///
    /// 从 cmd / PowerShell 启动时其实**已经继承**了控制台（GUI 子系统只是不自己开窗口），
    /// 这里再调一次是兜底：附加失败说明本来就有（或压根没有）→ 什么都不做。
    /// 双击启动时没有父控制台，附加也会失败 → 保持静默。
    ///
    /// ⚠ **已经有可用的标准输出时绝不能附加**：输出被重定向成管道时（测试、
    /// 脚本），附加会把输出改道到父控制台，管道里的内容就丢了 ——
    /// 自动化读不到"引擎就绪"这类关键字。
    /// </summary>
    private static void TryAttachParentConsole()
    {
        if (HasUsableStdHandle())
        {
            return;
        }

        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                return;
            }

            // 附加成功后标准句柄要重新拿一遍，否则 Console 写不到父控制台。
            var stdout = Console.OpenStandardOutput();
            Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
        }
        catch (Exception)
        {
            // 附加不上就算了：双击启动本来就该静默。
        }
    }

    /// <summary>标准输出句柄是不是可用的（管道 / 继承的控制台都算）。</summary>
    private static bool HasUsableStdHandle()
    {
        try
        {
            IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
            return handle != IntPtr.Zero && handle != new IntPtr(-1);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
