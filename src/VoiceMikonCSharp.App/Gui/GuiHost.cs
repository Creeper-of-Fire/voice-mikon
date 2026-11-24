using System;
using System.Diagnostics;
using System.Linq;
using VoiceMikonCSharp.Core;
using VoiceMikonCSharp.Core.Presentation;
using VoiceMikonCSharp.Shell;
using VoiceMikonCSharp.WebView;

namespace VoiceMikonCSharp.App.Gui;

/// <summary>
/// GUI 装配层：在 K3 的 <see cref="AppHost"/> 之上装上**胶囊窗口 + 绑定层 + 托盘**。
///
/// <para>职责边界（core-api.md §1 第 7 步）：
/// <list type="bullet">
/// <item>造 <see cref="CapsuleWindow"/>（WebView2 覆盖层窗口：透明 / 不抢焦点 / 置顶 / 点击穿透）。</item>
/// <item>造 <see cref="CapsuleBinder"/>：presenter 的显示状态 → 窗口；窗口的用户动作 → presenter。</item>
/// <item>造 <see cref="TrayIcon"/>：托盘图标 + 右键菜单（灰/黄/红三色跟状态走）。</item>
/// <item>按 <c>config.ui_mode</c> 决定球的显隐（orb 常驻球 / resident 无球 / on_demand 同 resident）。</item>
/// <item>球的位置记忆：拖完写 <c>config.orb</c>，启动时读回来。</item>
/// <item>设置面板（K5）：按需创建、关闭即销毁；面板的主题 / 界面形态改动要推给胶囊。</item>
/// </list>
/// 面板的翻译层（<c>PanelPresenter</c>）在 core 里，窗口在 VoiceMikonCSharp.WebView；
/// 这层只负责把两头接上。</para>
///
/// <para><b>退出顺序</b>（core-api.md §1 + 任务书）：
/// <code>
/// 停 UI 推送（Binder 解绑） → 销毁窗口 → 删托盘图标 → 注销热键（AppHost.Shutdown）
/// </code>
/// 热键是全局资源，注销晚会导致"退出后马上重启"注册失败。
/// 重启 = 置标记走同一条退出路径，<b>全部清理完成后</b>才拉新实例。</para>
/// </summary>
public sealed class GuiHost : IDisposable
{
    private static readonly Logger _log = Log.GetLogger("gui.host");

    private readonly AppHost _host;
    private readonly CapsuleBinder _binder;
    private readonly Config _config;
    private readonly TrayIcon _tray;

    /// <summary>面板的翻译层（core 快照 / 通知 → 面板快照）。</summary>
    private readonly PanelPresenter _panelPresenter;

    /// <summary>当前的面板窗口（按需创建，关闭即销毁）。</summary>
    private PanelWindow? _panel;

    private bool _disposed;
    private bool _quitStarted;
    private bool _restartRequested;

    private GuiHost(
        AppHost host,
        CapsuleWindow window,
        CapsuleBinder binder,
        Config config,
        TrayIcon tray)
    {
        _host = host;
        Window = window;
        _binder = binder;
        _config = config;
        _tray = tray;

        // 面板的翻译层（K5）：core 快照 / 通知 → 面板快照。
        // 它**不认识窗口**：面板窗口按需创建、关闭即销毁，presenter 常驻
        // （core / 通知的订阅只订一次）。
        _panelPresenter = new PanelPresenter(host.Controller, config, host.Notifier);
        _panelPresenter.Install();
    }

    /// <summary>胶囊窗口。</summary>
    public CapsuleWindow Window { get; }

    /// <summary>UI 绑定层（窗口 ↔ presenter）。</summary>
    public CapsuleBinder Binder => _binder;

    /// <summary>托盘图标。</summary>
    public TrayIcon Tray => _tray;

    /// <summary>用户请求退出（菜单里的「退出」）。</summary>
    public event Action? QuitRequested;

    /// <summary>
    /// 装配 GUI：造窗口 + 绑定层 + 托盘，按界面形态决定球的显隐。
    /// </summary>
    /// <param name="host">K3 的装配链（controller + presenter 都在里面）。</param>
    /// <param name="config">配置（看 <c>ui_mode</c> / <c>theme</c> / <c>show_preview</c> / <c>orb</c>）。</param>
    /// <param name="restartArgs">重启时要带上的命令行参数（与启动时一样）。</param>
    public static GuiHost Create(
        AppHost host,
        Config config,
        string[]? restartArgs = null)
    {
        // 建窗前记下前台窗口：建窗的副作用可能把焦点抢走，
        // 胶囊要是成了前台窗口，用户的键盘输入就进了胶囊。
        IntPtr focusRestore = NativeMethods.GetForegroundWindow();

        var window = new CapsuleWindow(focusRestore);

        // UI 线程调度锚定到胶囊窗口（presenter 的事件在别的线程上发出来）。
        UiDispatch.Bind(window);

        var binder = new CapsuleBinder(host.Presenter, window);
        var tray = new TrayIcon();

        var gui = new GuiHost(host, window, binder, config, tray);
        gui._restartArgs = restartArgs ?? Array.Empty<string>();

        // ---- 面板的翻译层（K5）----
        var panelPresenter = gui._panelPresenter;

        // 面板改了主题 → 胶囊跟着换（Python 版：theme_broadcaster=capsule.set_theme）。
        // 胶囊的配色由 CSS 的 data-theme 决定，这里把新档位推过去即可。
        panelPresenter.ThemeChanged += (_, mode) => UiDispatch.Post(() =>
            gui.Window.SetTheme(mode));

        // 面板改了界面形态 → 胶囊当场收球 / 露球
        // （Python 版：ui_mode_broadcaster=presenter.apply_ui_mode）。
        panelPresenter.UiModeChanged += (_, mode) => UiDispatch.Post(() =>
            host.Presenter.ApplyUiMode(mode));

        // 面板的「退出」走与托盘**同一条**收尾路径（K4b 留的回调位）。
        panelPresenter.QuitRequested += (_, _) => UiDispatch.Post(() => gui.RequestQuit());

        // 面板的关闭钮：关掉面板（**不是**退出程序）。
        panelPresenter.CloseRequested += (_, _) => UiDispatch.Post(() => gui.ClosePanel());

        // 界面形态（orb / resident / on_demand）→ 球的显隐。
        // presenter 的构造函数已经读过 config.UiMode，这里再落一次到窗口上。
        window.OrbEnabledProvider = () =>
            string.Equals(config.UiMode, "orb", StringComparison.OrdinalIgnoreCase);

        // 主题 / 预览开关：窗口不直接碰 Config —— 读值都通过这两个委托。
        window.ThemeProvider = () => config.Theme ?? "auto";
        window.PreviewEnabledProvider = () => config.ShowPreview;

        // 位置记忆：启动时读回来，拖完写回去。
        window.OrbGeometryProvider = () => config.Orb?.Geometry ?? string.Empty;
        window.OrbMonitorProvider = () => config.Orb?.Monitor ?? string.Empty;
        window.OrbPositionSaver = (geometry, monitor) =>
        {
            config.Orb ??= new OrbConfig();
            config.Orb.Geometry = geometry;
            config.Orb.Monitor = monitor;

            try
            {
                config.Save();
            }
            catch (Exception ex)
            {
                _log.Error($"保存球的位置失败: {ex.Message}");
            }
        };

        // presenter 的启动态（初始 mode / 主题 / 预览开关 / 球的显隐）推给窗口。
        host.Presenter.Start();

        // 球菜单的「退出」与托盘的「退出」汇到**同一条**收尾路径。
        binder.QuitRequested += (_, _) => gui.RequestQuit();
        binder.SettingsRequested += (_, _) => gui.OnSettingsRequested();
        binder.QrScanRequested += (_, _) => gui.LaunchQrScanner();

        // ---- 托盘 ----
        gui.StartTray();

        return gui;
    }

    /// <summary>重启时要带上的命令行参数。</summary>
    private string[] _restartArgs = Array.Empty<string>();

    // ==================== 托盘 ====================

    /// <summary>
    /// 起托盘：图标 + 右键菜单 + 状态跟随。
    /// </summary>
    private void StartTray()
    {
        // 菜单项文案与顺序照 Python 版（voice-mikon 的 __main__.py 的 tray.set_menu）。
        _tray.SetMenu(new[]
        {
            new TrayMenuEntry("开始 / 停止录音", "record", IsDefault: true),
            new TrayMenuEntry("设置", "panel"),
            new TrayMenuEntry("重新探测麦克风", "reload_mic"),
            new TrayMenuEntry("打开配置文件", "open_config"),
            new TrayMenuEntry("查看日志", "open_log"),
            new TrayMenuEntry("重启", "restart"),
            new TrayMenuEntry("退出", "quit"),
        });

        // 托盘菜单的回调在**托盘线程**上 —— 要动 UI / controller 的东西都得切回 UI 线程。
        _tray.MenuItemActivated += (_, key) => UiDispatch.Post(() => OnTrayMenuItem(key));
        _tray.DefaultItemActivated += (_, key) => UiDispatch.Post(() => OnTrayMenuItem(key));

        if (!_tray.Start())
        {
            _log.Error("托盘启动失败(菜单功能将不可用)");
            _host.Notifier.Publish("error", "tray_failed", "托盘图标创建失败");
            return;
        }

        // 图标颜色跟着状态走（照 Python 的 on_core_state 映射）。
        _host.Controller.StateChanged += (_, state) =>
        {
            TrayIconState iconState = state.State switch
            {
                AppStates.Recording => TrayIconState.Recording,
                AppStates.Processing => TrayIconState.Warn,
                AppStates.Starting => TrayIconState.Warn,
                _ => TrayIconState.Idle,
            };

            _tray.SetState(iconState);
        };

        // 起手先把当前状态落上去（托盘起来时 controller 可能已经在某个状态了）。
        AppState snapshot = _host.Controller.StateSnapshot;
        _tray.SetState(snapshot.State switch
        {
            AppStates.Recording => TrayIconState.Recording,
            AppStates.Processing => TrayIconState.Warn,
            AppStates.Starting => TrayIconState.Warn,
            _ => TrayIconState.Idle,
        });
    }

    /// <summary>托盘菜单被选中。</summary>
    private void OnTrayMenuItem(string key)
    {
        switch (key)
        {
            case "record":
                try
                {
                    _host.Controller.ToggleRecording();
                }
                catch (Exception ex)
                {
                    _log.Error($"托盘录音动作失败: {ex.Message}");
                }

                break;

            case "panel":
                OnSettingsRequested();
                break;

            case "reload_mic":
                // 重新探测麦克风（探测要 2 秒，同步做完；配置由控制器自己存）。
                _host.Controller.ReloadMic();
                break;

            case "open_config":
                OpenInShell(Paths.ConfigPath);
                break;

            case "open_log":
                OpenInShell(Paths.LogPath);
                break;

            case "restart":
                RequestQuit(restart: true);
                break;

            case "quit":
                RequestQuit();
                break;
        }
    }

    /// <summary>
    /// 用系统默认程序打开一个文件（托盘「打开配置文件」/「查看日志」）。
    ///
    /// <para>照 Python 的 <c>os.startfile</c> —— C# 对应 <c>Process.Start(UseShellExecute = true)</c>。</para>
    /// </summary>
    private void OpenInShell(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                _log.Warning($"要打开的文件不存在: {path}");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error($"打开文件失败: {ex.Message}");
        }
    }

    // ==================== 附属工具（二维码扫描）====================

    /// <summary>
    /// 球菜单的「二维码扫描」：拉起附属工具（**独立进程**）。
    ///
    /// <para>照 Python 版 <c>make_qr_scanner_launcher</c> 的语义：不等它、不看它的输出、
    /// 不管它的死活 —— 它崩了不影响主程序；工具缺失时**明确报错**（不静默）。</para>
    ///
    /// <para>本层**不引用**工具的任何程序集（零 import）：只有一行 <c>Process.Start</c>。</para>
    /// </summary>
    private void LaunchQrScanner()
    {
        string? exe = Companions.LocateQrScanner(AppContext.BaseDirectory);

        if (exe is null)
        {
            string expected = Companions.QrScannerPath(AppContext.BaseDirectory);
            _log.Error($"二维码扫描工具不见了: {expected}");
            _host.Notifier.Publish("error", "qr_scanner_missing", $"二维码扫描工具缺失：{expected}");
            return;
        }

        try
        {
            // UseShellExecute = false：直接跑 exe，不经 shell 解释。
            // 不等它、不读输出 —— 工具自己管自己的窗子与剪贴板。
            using Process? process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            });

            _log.Info($"二维码扫描：已拉起附属工具({exe})");
        }
        catch (Exception ex)
        {
            _log.Error($"拉起二维码扫描工具失败: {ex.Message}");
            _host.Notifier.Publish("error", "qr_scanner_launch_failed", $"拉起二维码扫描工具失败：{ex.Message}");
        }
    }

    // ==================== 面板 ====================

    /// <summary>
    /// 「设置」：打开设置面板。
    ///
    /// <para>面板是**按需创建、关闭即销毁**的（Python 版必须常驻只是隐藏，
    /// 因为 pywebview 的 <c>start()</c> 之后不能建窗 —— 那个限制不搬）。
    /// 已经开着就把它激活到前面，不重复建。</para>
    /// </summary>
    private void OnSettingsRequested() => OpenPanel();

    /// <summary>打开设置面板（托盘「设置」与球菜单「设置」都走这里）。</summary>
    public void OpenPanel()
    {
        if (_panel is not null)
        {
            // 已经开着：激活到前面就行，别重复建。
            _panel.Activate();
            return;
        }

        var window = new PanelWindow(_config.Theme);
        _panel = window;

        // ---- 位置记忆（UI 层不直接碰 Config）----
        window.GeometryProvider = () => _config.PanelGeometry ?? string.Empty;
        window.GeometrySaver = geometry =>
        {
            _config.PanelGeometry = geometry;
            try
            {
                _config.Save();
            }
            catch (Exception ex)
            {
                _log.Error($"保存面板位置失败: {ex.Message}");
            }
        };

        // ---- presenter → 面板 ----
        // 快照在**触发它的那条线程**上发出来（core-api.md §3），要切回 UI 线程。
        void OnSnapshot(object? sender, PanelSnapshot snapshot)
            => UiDispatch.Post(() =>
            {
                if (_panel == window)
                {
                    window.ApplySnapshot(snapshot);
                }
            });

        _panelPresenter.SnapshotChanged += OnSnapshot;
        window.FormClosed += (_, _) =>
        {
            _panelPresenter.SnapshotChanged -= OnSnapshot;
            if (_panel == window)
            {
                _panel = null;
            }
        };

        // ---- 面板 → presenter ----
        window.BackendChanged += (_, kind) => _panelPresenter.SwitchBackend(kind);
        window.ModelChanged += (_, tag) => _panelPresenter.SwitchModel(tag);
        window.ThemeChanged += (_, mode) => _panelPresenter.SetTheme(mode);
        window.UiModeChanged += (_, mode) => _panelPresenter.SetUiMode(mode);
        window.PreviewChanged += (_, enabled) => _panelPresenter.SetPreview(enabled);
        window.RestoreClipboardChanged += (_, enabled) => _panelPresenter.SetRestoreClipboard(enabled);

        // 面板的关闭钮 / 退出钮：走 presenter 的同名动作（翻译层收口）。
        window.CloseRequested += (_, _) => _panelPresenter.ClosePanel();
        window.QuitRequested += (_, _) => _panelPresenter.QuitApp();

        // 提示语 8 秒后自己淡掉 —— 事实也归零，免得下一条快照把它顶回来。
        window.NoticeExpired += (_, _) => _panelPresenter.ClearNotice();

        // 前端就绪之前的所有推送都被丢弃了（面板会停在默认值上）——
        // 就绪那一刻把当前快照**重推一遍**（照 Python 版 on_frontend_ready 的 push_full_state）。
        window.FrontendReadyChanged += (_, _) => UiDispatch.Post(() =>
        {
            if (_panel == window)
            {
                window.ApplySnapshot(_panelPresenter.Snapshot);
            }
        });

        // 首屏：把当前快照铺上去（订阅即投递，此刻 presenter 已经有快照了）。
        window.ApplySnapshot(_panelPresenter.Snapshot);

        // 面板本来就要拿焦点做交互（与胶囊相反）—— 显示时激活。
        window.Show();
    }

    /// <summary>关掉面板（**不是**退出程序）。</summary>
    public void ClosePanel()
    {
        PanelWindow? panel = _panel;
        if (panel is null)
        {
            return;
        }

        _panel = null;
        panel.Close();
    }

    // ==================== 退出 / 重启 ====================

    /// <summary>
    /// 请求退出。三条退出路（托盘菜单 / 球菜单 / 面板按钮）都汇到这一条。
    /// </summary>
    /// <param name="restart">true = 退出后拉一个新实例（重启）。</param>
    public void RequestQuit(bool restart = false)
    {
        if (_quitStarted)
        {
            return;
        }

        _quitStarted = true;
        _restartRequested = restart;
        _log.Info(restart ? "收到重启请求" : "收到退出请求");

        // 1) 停"往界面推东西" —— 推给一个没人看的界面没有意义，还会拖慢退出。
        try
        {
            _binder.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug($"绑定层收尾异常: {ex.Message}");
        }

        // 2) 销毁窗口。
        //    胶囊窗口**从不显示为 WinForms 窗体**（显示走 Win32 的 SW_SHOWNOACTIVATE），
        //    所以 Close() 发的 WM_CLOSE 未必有人处理 —— 直接 Dispose：
        //    WebView2 控件随之释放，msedgewebview2.exe 子进程才会干净退出。
        try
        {
            Window.Shutdown();
            Window.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug($"窗口收尾异常: {ex.Message}");
        }

        // 2b) 销毁面板窗口（开着就关掉 —— 程序要退了，面板不能留着）。
        try
        {
            ClosePanel();
        }
        catch (Exception ex)
        {
            _log.Debug($"面板收尾异常: {ex.Message}");
        }

        // 3) 删托盘图标 —— **不做的话托盘里会留一枚死图标**（进程没了，图标还在）。
        try
        {
            _tray.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug($"托盘收尾异常: {ex.Message}");
        }

        // 4) 再注销热键 —— 它是全局资源，占着不放的话，"退出后马上重启"
        //    会因为注册冲突而失败。里面还会停 core、归还剪贴板所有权。
        try
        {
            _host.Shutdown();
        }
        catch (Exception ex)
        {
            _log.Debug($"收尾异常: {ex.Message}");
        }

        // 5) 全部清理完成之后才拉新实例（热键已注销、窗口已销毁、模型已卸载）。
        if (_restartRequested)
        {
            StartRestart();
        }

        QuitRequested?.Invoke();
    }

    /// <summary>
    /// 拉起新实例。
    ///
    /// <para>照 Python 的 <c>_spawn_restart</c> 的**目的**（清理完再拉，避免撞车），
    /// 但**不搬**它"忠实复刻启动方式"的那套（Python 有脚本 / <c>-m</c> / frozen / 薄壳
    /// 四种入口；C# 只有一个 exe）。</para>
    /// </summary>
    private void StartRestart()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                _log.Error("重启失败: 拿不到当前进程路径");
                return;
            }

            var info = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Paths.ProjectDir,
            };

            // `dotnet run` / `dotnet xxx.dll` 时 ProcessPath 是 dotnet.exe ——
            // 那就还得把入口 dll 带上，不然拉起来的是个空的 dotnet。
            if (string.Equals(
                    System.IO.Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string? entry = Environment.GetCommandLineArgs().FirstOrDefault();
                if (!string.IsNullOrEmpty(entry))
                {
                    info.ArgumentList.Add(entry);
                }
            }

            foreach (string arg in _restartArgs)
            {
                info.ArgumentList.Add(arg);
            }

            Process.Start(info);
            _log.Info("已拉起新实例");
        }
        catch (Exception ex)
        {
            _log.Error($"重启失败: {ex.Message}");
        }
    }

    /// <summary>解绑（退订 presenter 事件、断开窗口回调）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _binder.Dispose();

        // 面板的翻译层：停掉"往界面推东西"（推给一个没人看的界面没有意义）。
        try
        {
            _panelPresenter.Shutdown();
        }
        catch (Exception ex)
        {
            _log.Debug($"面板 presenter 收尾异常: {ex.Message}");
        }

        // 托盘图标一定要删 —— 进程退出后图标还在 = 托盘里留一枚点不动的死图标。
        // （RequestQuit 里已经删过一次，这里是"意外退出"的兜底；Dispose 是幂等的。）
        try
        {
            _tray.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug($"托盘收尾异常: {ex.Message}");
        }
    }
}
