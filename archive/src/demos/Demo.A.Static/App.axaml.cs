using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VmNext.Shell;

namespace DemoA;

/// <summary>Demo A 的应用入口。</summary>
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

            if (DemoOptions.Auto)
            {
                // 显示之前先抓一块屏幕基线，用来证明"透明处真的透出了桌面"
                var probe = new OverlayProbe(window);
                CaptureBaseline(probe, window);

                window.NativeReady += async (_, _) => await RunAutoAsync(window, probe);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>抓屏幕右下角一块区域作为"窗口显示前"的基线。</summary>
    private static void CaptureBaseline(OverlayProbe probe, OverlayWindow window)
    {
        const int regionSize = 800;

        if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, out var work, 0))
        {
            return;
        }

        int left = Math.Max(work.Left, work.Right - regionSize);
        int top = Math.Max(work.Top, work.Bottom - regionSize);
        int width = Math.Min(regionSize, work.Right - left);
        int height = Math.Min(regionSize, work.Bottom - top);

        // 先把窗口位置算好（真正显示时 OnOpened 会再算一次）
        window.AnchorToBottomRight();
        probe.CaptureBaseline(left, top, width, height);
    }

    /// <summary>自动化流程：透明验证 → 空闲 10 秒测资源 → A1–A6 → 退出。</summary>
    private static async Task RunAutoAsync(MainWindow window, OverlayProbe probe)
    {
        var process = Process.GetCurrentProcess();
        double openedMs = (DateTime.Now - process.StartTime).TotalMilliseconds;
        ProbeOutput.Mark("OPENED", openedMs.ToString("F1"));

        // 等第一帧渲染出来
        await Task.Delay(400);

        // X1：真透明（对比显示前后的屏幕像素）
        Report(probe.VerifyTransparency());

        // A8 / A9：静置 10 秒的 CPU 与工作集
        var metrics = await ProcessMetrics.MeasureIdleAsync(TimeSpan.FromSeconds(10));
        ProbeOutput.Raw(
            "CPU",
            metrics.AvgCpuPercent.ToString("F3"),
            metrics.TotalCpuMs.ToString(),
            metrics.ElapsedMs.ToString("F0"),
            metrics.ProcessorCount.ToString());
        ProbeOutput.Raw(
            "WS",
            metrics.WorkingSetEnv.ToString(),
            metrics.WorkingSetProc.ToString(),
            metrics.PrivateBytes.ToString());

        // A1–A6：窗口层自检
        Report(probe.CheckExStyle());
        Report(probe.CheckRegion());
        Report(probe.CheckClickThrough());
        Report(probe.CheckNoActivate());
        Report(probe.CheckMove());
        Report(probe.CheckDpi());

        AppShutdown.Exit();
    }

    /// <summary>打印一条检查结果。</summary>
    private static void Report(ProbeResult result)
        => ProbeOutput.Check(result.Id, result.Passed, $"{result.Name}｜{result.Detail}");
}
