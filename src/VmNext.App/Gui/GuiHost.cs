using System;
using Avalonia.Controls.ApplicationLifetimes;
using VmNext.Core;
using VmNext.Core.Presentation;
using VmNext.Ui;

namespace VmNext.App.Gui;

/// <summary>
/// GUI 装配层：在 K3 的 <see cref="AppHost"/> 之上装上**胶囊窗口 + 绑定层**。
///
/// <para>职责边界（core-api.md §1 第 7 步）：
/// <list type="bullet">
/// <item>造 <see cref="CapsuleWindow"/>（覆盖层窗口：透明 / 不抢焦点 / 置顶 / 点击穿透）。</item>
/// <item>造 <see cref="UiBinder"/>：presenter 的显示状态 → 窗口；窗口的用户动作 → presenter。</item>
/// <item>按 <c>config.ui_mode</c> 决定球的显隐（orb 常驻球 / resident 无球 / on_demand 同 resident）。</item>
/// </list>
/// 不归这层的事：拖拽 / 摆位记忆 / 托盘 / 面板 —— 那是 K4b。</para>
/// </summary>
public sealed class GuiHost : IDisposable
{
    private readonly AppHost _host;
    private readonly UiBinder _binder;
    private bool _disposed;

    private GuiHost(AppHost host, CapsuleWindow window, UiBinder binder)
    {
        _host = host;
        Window = window;
        _binder = binder;
    }

    /// <summary>胶囊窗口。</summary>
    public CapsuleWindow Window { get; }

    /// <summary>UI 绑定层（窗口 ↔ presenter）。</summary>
    public UiBinder Binder => _binder;

    /// <summary>用户请求退出（菜单里的「退出」）。</summary>
    public event Action? QuitRequested;

    /// <summary>
    /// 装配 GUI：造窗口 + 绑定层，按界面形态决定球的显隐。
    /// </summary>
    /// <param name="host">K3 的装配链（controller + presenter 都在里面）。</param>
    /// <param name="desktop">Avalonia 生命周期（用来当 MainWindow）。</param>
    /// <param name="config">配置（看 <c>ui_mode</c> / <c>theme</c> / <c>show_preview</c>）。</param>
    public static GuiHost Create(
        AppHost host,
        IClassicDesktopStyleApplicationLifetime desktop,
        Config config)
    {
        var window = new CapsuleWindow();
        var binder = new UiBinder(host.Presenter, window);

        var gui = new GuiHost(host, window, binder);

        // 界面形态（orb / resident / on_demand）→ 球的显隐。
        // presenter 的构造函数已经读过 config.UiMode，这里再落一次到窗口上。
        window.SetUiMode(config.UiMode);

        // 主题：先按配置落一档（auto 时跟随系统）。
        window.SetTheme(config.Theme);

        // 预览开关。
        window.SetPreviewEnabled(config.ShowPreview);

        // presenter 的启动态（初始 mode / 主题 / 预览开关 / 球的显隐）推给窗口。
        host.Presenter.Start();

        binder.QuitRequested += (_, _) => gui.QuitRequested?.Invoke();

        return gui;
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
    }
}
