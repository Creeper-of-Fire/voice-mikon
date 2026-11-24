using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VmNext.Core.Presentation;
using VmNext.Ui;

namespace Capsule.Demo;

/// <summary>
/// Demo 的 Avalonia 应用。
///
/// <para>这是**离线视觉预览**：不接 controller、不录音，用热键直接推 mode，
/// 波形用模拟电平喂。正式版（src/VmNext.App）走的是同一条
/// <see cref="CapsuleWindow.SetMode"/> 入口 —— 这里只是把 mode 换成人手点的。</para>
///
/// <para>palette 资源来自 VmNext.Ui（<c>CapsuleTheme.axaml</c>），
/// 正式版与 demo 引的是同一份，样式不会走偏。</para>
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new CapsuleWindow();
            desktop.MainWindow = window;

            // 覆盖层窗口不抢焦点 → 键盘只能走全局热键。
            DemoHotKeys.Attach(window);

            // 命令行指定形态（截图 / 回归验证用）。
            DemoCommandLine.Apply(window, Environment.GetCommandLineArgs());

            if (Environment.GetEnvironmentVariable("VMUI_DEBUG") == "1")
            {
                DispatcherTimer.RunOnce(() => Console.Error.WriteLine(
                    "[demo] 命中区=" + string.Join(" | ", window.ComputeHitRegions().Select(r => "[" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + "]"))), TimeSpan.FromSeconds(2));

                // 胶囊自检：实际宽度 vs 按 CSS 手算的宽度；图标中心 vs 控件几何中心。
                DispatcherTimer.RunOnce(() => Console.Error.WriteLine(
                    $"[demo] 胶囊 实际宽={window.PillWidth:F2} 手算宽={window.PillExpandedWidth:F2} " +
                    $"图标中心=({window.PillIconCenter.X:F2},{window.PillIconCenter.Y:F2}) " +
                    $"控件中心=({window.PillCenter.X:F2},{window.PillCenter.Y:F2}) " +
                    $"偏差=({window.PillIconCenter.X - window.PillCenter.X:F3},{window.PillIconCenter.Y - window.PillCenter.Y:F3})"),
                    TimeSpan.FromSeconds(3));
            }

            // 退出前必须注销全局热键（否则组合键一直被占着）。
            window.Closed += (_, _) => DemoHotKeys.Detach();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
