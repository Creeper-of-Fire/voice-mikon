using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VmNext.Core;

namespace VmNext.App.Gui;

/// <summary>
/// GUI 形态的 Avalonia 应用。
///
/// <para>装配顺序照 <c>docs/core-api.md</c> §1（每一步都不能换位置）：
/// DPI 感知 → 剪贴板 owner → 配置 + 日志 → 通知出口 → controller → presenter
/// → **胶囊窗口** → 热键 → 引擎。</para>
///
/// <para>前六步由 <see cref="AppHost"/> 的构造函数做完（K3 的那条链），
/// 这一类只补第 7 步（窗口 + 绑定层），然后把第 8、9 步交回
/// <see cref="AppHost.Start"/> —— 顺序不能变：热键是全局资源，
/// 注册得太早会在退出时跟"引擎卸载"抢时间。</para>
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 启动参数与配置（由 <see cref="Program"/> 在进 Avalonia 之前放进来）。
    /// 用静态字段是因为 Avalonia 的 <c>Application</c> 是由框架实例化的，
    /// 没有构造参数可传。
    /// </summary>
    public static AppOptions StartupOptions { get; set; } = new();

    /// <summary>已加载的配置。</summary>
    public static Config StartupConfig { get; set; } = new();

    /// <summary>
    /// 装配链（controller + presenter + 热键 + 引擎）。
    ///
    /// ⚠ **必须活到进程结束**：<c>OnFrameworkInitializationCompleted</c> 一返回就 dispose
    /// 的话，控制器当场就被收掉了（热键注销、模型卸载、剪贴板归还）。
    /// 所以挂在字段上，退出时再按 core-api.md §1 的顺序收尾。
    /// </summary>
    private AppHost? _host;

    /// <summary>GUI 装配层（窗口 + 绑定层）。</summary>
    private GuiHost? _gui;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ---- 5) controller + 6) presenter（AppHost 的构造函数里做完）----
            var host = new AppHost(StartupOptions, StartupConfig);
            _host = host;

            // 进程退出（关窗口 / 未捕获异常）也要走同一条收尾路径。
            // ⚠ 顺序：**先删托盘图标**（否则托盘里留一枚点不动的死图标），
            //    再注销热键（热键是全局资源，占着不放会挡住"马上重启"）。
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                _gui?.Dispose();
                host.Shutdown();
            };

            // ---- 7) 胶囊窗口 + 绑定层 + 托盘 ----
            var gui = GuiHost.Create(host, desktop, StartupConfig, StartupOptions.RawArgs);
            _gui = gui;

            // ---- 8) 热键 + 9) 引擎启动（后台加载模型，主流程不阻塞）----
            host.Start();

            desktop.MainWindow = gui.Window;

            // 退出：三条退出路（托盘菜单 / 球菜单 / 面板按钮）都汇到 GuiHost.RequestQuit，
            // 那里已经按 core-api.md §1 的顺序收完尾了（停推送 → 销毁窗口 → 删托盘图标 → 注销热键），
            // 这里只负责关掉 Avalonia 生命周期。
            gui.QuitRequested += () => desktop.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
