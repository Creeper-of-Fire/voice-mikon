using System;
using System.Diagnostics;
using System.Threading;
using VmNext.Core;

namespace VmNext.App;

/// <summary>
/// 应用入口（控制台）。
///
/// 翻译自 voice-mikon/src/voice_mikon/__main__.py 的装配部分
/// （<c>main</c> / <c>run_gui</c> 的装配顺序 / <c>_shutdown_and_exit</c> /
/// headless 分支 / 超时）。
///
/// ── 两种运行形态（K3 阶段还没有 GUI）──
///
/// * <c>--headless</c>：**自检**。装配 core，等引擎就绪后干净退出
///   （退出码 0；引擎加载失败 / 超时 = 2）。不是常驻服务。
/// * 默认：**控制台常驻服务**。装配整条链（含热键），实时打印状态与通知，
///   Ctrl+C 退出。GUI 阶段会把这一支换成真正的窗口层。
///
/// 命令行：
/// <list type="bullet">
/// <item><c>--headless</c>：无头自检</item>
/// <item><c>--timeout &lt;秒&gt;</c>：等待引擎就绪的上限（默认 300）</item>
/// <item><c>--backend minimax|local</c>：覆盖配置里的后端</item>
/// <item><c>--config &lt;路径&gt;</c>：覆盖配置文件路径</item>
/// <item><c>--mock</c>：本地后端走 mock 模式（**本次新增**，不加载真模型）</item>
/// </list>
/// </summary>
public static class Program
{
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
        // ---- 配置 + 日志（之后才有地方写日志）----
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
        log.Info($"Voice Mikon 启动  热键={config.Hotkey}  模型={config.Model}  后端={config.Backend}");

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
    ///
    /// GUI 阶段这一支会被窗口层接管；本阶段它就是"看得见发生了什么"的
    /// 运行形态：状态、通知、显示模式都实时打在控制台上。
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
}
