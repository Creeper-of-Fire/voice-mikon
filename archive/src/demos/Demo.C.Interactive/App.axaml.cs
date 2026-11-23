using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VmNext.Shell;

namespace DemoC;

/// <summary>Demo C 的应用入口。</summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // 显示器信息打给用户看（多屏/不同缩放的情况）
            foreach (string line in window.DescribeScreens())
            {
                Console.WriteLine("[显示器] " + line);
            }

            if (DemoOptions.Auto)
            {
                window.NativeReady += async (_, _) => await RunAutoAsync(window);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>自动化流程：拖拽几何 + 文字可复制性 + 显示器信息 → 退出。</summary>
    private static async Task RunAutoAsync(MainWindow window)
    {
        var process = Process.GetCurrentProcess();
        double openedMs = (DateTime.Now - process.StartTime).TotalMilliseconds;
        ProbeOutput.Mark("OPENED", openedMs.ToString("F1"));

        await Task.Delay(400);

        // 展开胶囊，露出文字
        window.ToggleExpand();
        await Task.Delay(300);

        var probe = new OverlayProbe(window);

        // 拖拽几何（与 A5 同一套断言）
        var move = probe.CheckMove();
        ProbeOutput.Check("A5", move.Passed, $"{move.Name}｜{move.Detail}");

        // 文字选择/复制能力：全选 → CanCopy → 写剪贴板
        window.SelectAllText();
        await Task.Delay(150);
        bool copied = await window.CopySelectionAsync();
        ProbeOutput.Check(
            "C1",
            window.TextCanCopy && copied,
            $"SelectableTextBlock.CanCopy={window.TextCanCopy}，选中后写剪贴板={(copied ? "成功" : "失败")}（Avalonia 11.1 提供 Copy()/CanCopy/SelectedText）");

        ProbeOutput.Raw("SCREENS", window.DescribeScreens().Count.ToString());

        AppShutdown.Exit();
    }
}
