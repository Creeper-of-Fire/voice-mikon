using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VmNext.Shell;

namespace DemoB;

/// <summary>Demo B 的应用入口。</summary>
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
                window.NativeReady += async (_, _) => await RunAutoAsync(window);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>自动化流程：跑 10 秒（含形变 + 呼吸 + 旋转），统计帧间隔。</summary>
    private static async Task RunAutoAsync(MainWindow window)
    {
        var process = Process.GetCurrentProcess();
        double openedMs = (DateTime.Now - process.StartTime).TotalMilliseconds;
        ProbeOutput.Mark("OPENED", openedMs.ToString("F1"));

        window.StartFrameRecording();

        // 先让球稳定 0.8 秒，再触发形变
        await Task.Delay(800);
        double morphStart = window.RecorderElapsedMs;
        window.ToggleMorph();

        // 继续跑满 10 秒（形变 0.8s + 呼吸 + 旋转 + 波形滚动）
        await Task.Delay(10000);

        double morphEnd = morphStart + 800;

        var all = window.GetFrameStats(null, null);
        ProbeOutput.Raw(
            "FRAME",
            all.AverageMs.ToString("F2"),
            all.MaxMs.ToString("F2"),
            all.MinMs.ToString("F2"),
            all.SampleCount.ToString(),
            all.Over20MsCount.ToString());

        // 分段：形变期间（每帧都在 resize 窗口 + 重裁窗口区域）vs 形变之后
        var during = window.GetFrameStats(morphStart, morphEnd);
        ProbeOutput.Raw(
            "FRAME_MORPH",
            during.AverageMs.ToString("F2"),
            during.MaxMs.ToString("F2"),
            during.SampleCount.ToString(),
            during.Over20MsCount.ToString());

        var after = window.GetFrameStats(morphEnd, null);
        ProbeOutput.Raw(
            "FRAME_IDLE",
            after.AverageMs.ToString("F2"),
            after.MaxMs.ToString("F2"),
            after.SampleCount.ToString(),
            after.Over20MsCount.ToString());

        // 形变连续性：宽度采样里不同取值的个数（越多说明过渡越连续，不是跳变）
        var widths = window.GetWidthSamples();
        int distinct = widths.Select(w => Math.Round(w, 1)).Distinct().Count();
        ProbeOutput.Raw(
            "MORPH",
            widths.Count.ToString(),
            distinct.ToString(),
            widths.Count > 0 ? widths.Min().ToString("F1") : "0",
            widths.Count > 0 ? widths.Max().ToString("F1") : "0");

        // 主题切换自检：确认令牌资源确实被找到、颜色确实变了
        bool found = OverlayTheme.TryGetBrushColor(OverlayTheme.SurfaceKey, out var colorBefore);
        OverlayTheme.Toggle();
        await Task.Delay(400); // 等 260ms 的颜色补间跑完
        OverlayTheme.TryGetBrushColor(OverlayTheme.SurfaceKey, out var colorAfter);
        ProbeOutput.Check(
            "B1",
            found && colorBefore != colorAfter,
            $"主题切换：令牌资源找到={found}，Surface 色 {colorBefore} → {colorAfter}（当前主题 {OverlayTheme.Current}）");

        AppShutdown.Exit();
    }
}
