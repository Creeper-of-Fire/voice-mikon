using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VmNext.Core;
using VmNext.Core.Presentation;
using VmNext.Shell;
using VmNext.Ui;

namespace VmNext.App.Gui;

/// <summary>
/// GUI 装配层：在 K3 的 <see cref="AppHost"/> 之上装上**胶囊窗口 + 绑定层 + 托盘**。
///
/// <para>职责边界（core-api.md §1 第 7 步）：
/// <list type="bullet">
/// <item>造 <see cref="CapsuleWindow"/>（覆盖层窗口：透明 / 不抢焦点 / 置顶 / 点击穿透）。</item>
/// <item>造 <see cref="UiBinder"/>：presenter 的显示状态 → 窗口；窗口的用户动作 → presenter。</item>
/// <item>造 <see cref="TrayIcon"/>：托盘图标 + 右键菜单（灰/黄/红三色跟状态走）。</item>
/// <item>按 <c>config.ui_mode</c> 决定球的显隐（orb 常驻球 / resident 无球 / on_demand 同 resident）。</item>
/// <item>球的位置记忆：拖完写 <c>config.orb</c>，启动时读回来。</item>
/// </list>
/// 不归这层的事：面板（K5）。</para>
///
/// <para><b>退出顺序</b>（core-api.md §1 + 任务书）：
/// <code>
/// 停 UI 推送（UiBinder 解绑） → 销毁窗口 → 删托盘图标 → 注销热键（AppHost.Shutdown）
/// </code>
/// 热键是全局资源，注销晚会导致"退出后马上重启"注册失败。
/// 重启 = 置标记走同一条退出路径，<b>全部清理完成后</b>才拉新实例。</para>
/// </summary>
public sealed class GuiHost : IDisposable
{
    private static readonly Logger _log = Log.GetLogger("gui.host");

    private readonly AppHost _host;
    private readonly UiBinder _binder;
    private readonly Config _config;
    private readonly TrayIcon _tray;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    private bool _disposed;
    private bool _quitStarted;
    private bool _restartRequested;

    private GuiHost(
        AppHost host,
        CapsuleWindow window,
        UiBinder binder,
        Config config,
        TrayIcon tray,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        _host = host;
        _desktop = desktop;
        Window = window;
        _binder = binder;
        _config = config;
        _tray = tray;
    }

    /// <summary>胶囊窗口。</summary>
    public CapsuleWindow Window { get; }

    /// <summary>UI 绑定层（窗口 ↔ presenter）。</summary>
    public UiBinder Binder => _binder;

    /// <summary>托盘图标。</summary>
    public TrayIcon Tray => _tray;

    /// <summary>用户请求退出（菜单里的「退出」）。</summary>
    public event Action? QuitRequested;

    /// <summary>
    /// 装配 GUI：造窗口 + 绑定层 + 托盘，按界面形态决定球的显隐。
    /// </summary>
    /// <param name="host">K3 的装配链（controller + presenter 都在里面）。</param>
    /// <param name="desktop">Avalonia 生命周期（用来当 MainWindow + 收摊）。</param>
    /// <param name="config">配置（看 <c>ui_mode</c> / <c>theme</c> / <c>show_preview</c> / <c>orb</c>）。</param>
    /// <param name="restartArgs">重启时要带上的命令行参数（与启动时一样）。</param>
    public static GuiHost Create(
        AppHost host,
        IClassicDesktopStyleApplicationLifetime desktop,
        Config config,
        string[]? restartArgs = null)
    {
        var window = new CapsuleWindow();
        var binder = new UiBinder(host.Presenter, window);
        var tray = new TrayIcon();

        var gui = new GuiHost(host, window, binder, config, tray, desktop);
        gui._restartArgs = restartArgs ?? Array.Empty<string>();

        // 界面形态（orb / resident / on_demand）→ 球的显隐。
        // presenter 的构造函数已经读过 config.UiMode，这里再落一次到窗口上。
        window.SetUiMode(config.UiMode);

        // 主题：先按配置落一档（auto 时跟随系统）。
        window.SetTheme(config.Theme);

        // 预览开关。
        window.SetPreviewEnabled(config.ShowPreview);

        // 位置记忆：启动时读回来，拖完写回去。
        // UI 层不直接碰 Config —— 读写都通过这两个委托（"配置怎么存"留在宿主层）。
        window.PositionHintProvider = () => new OrbPlacementHint(
            config.Orb?.Geometry ?? string.Empty,
            config.Orb?.Monitor ?? string.Empty);

        window.PositionSaver = hint =>
        {
            config.Orb ??= new OrbConfig();
            config.Orb.Geometry = hint.Geometry;
            config.Orb.Monitor = hint.Monitor;

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
        // 「设置」在 Python 版是"设置面板"（打开面板）；面板是 K5，这里先发一条通知。
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
        _tray.MenuItemActivated += (_, key) => Dispatcher.UIThread.Post(() => OnTrayMenuItem(key));
        _tray.DefaultItemActivated += (_, key) => Dispatcher.UIThread.Post(() => OnTrayMenuItem(key));

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
            if (!File.Exists(path))
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

    /// <summary>
    /// 「设置」：面板是 K5，还没做 —— 发一条通知 + 球上冒一句提示。
    /// </summary>
    private void OnSettingsRequested()
    {
        _host.Notifier.Publish("info", "panel_not_implemented", "面板尚未实现");
        Window.ShowOrbHint("面板尚未实现");
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
        try
        {
            Window.Close();
        }
        catch (Exception ex)
        {
            _log.Debug($"窗口收尾异常: {ex.Message}");
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
                    Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
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
