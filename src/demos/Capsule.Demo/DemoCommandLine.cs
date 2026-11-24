using System;
using Avalonia.Threading;
using VmNext.Core.Presentation;
using VmNext.Ui;

namespace Capsule.Demo;

/// <summary>
/// Demo 的形态入口：命令行 <c>--mode &lt;orb|recording|processing|done|error&gt;</c>
/// 或环境变量 <c>CAPSULE_DEMO_AUTO=orb|menu|recording|processing</c>。
///
/// <para>用途：截图 / 回归验证时需要**确定**某个形态，
/// 不必守着键盘按热键（热键还得跟别的程序抢）。</para>
///
/// <para>环境变量里的 <c>menu</c> 是"球形态 + 1.5 秒后自动弹出右键菜单" ——
/// 用来验证菜单与球的相对位置、以及开合菜单时窗口会不会挪位置。</para>
/// </summary>
internal static class DemoCommandLine
{
    /// <summary>环境变量名（自动形态）。</summary>
    private const string AutoModeVariable = "CAPSULE_DEMO_AUTO";

    /// <summary>菜单自动弹出的延迟（毫秒）。</summary>
    private const int MenuAutoDelayMs = 1500;

    /// <summary>按命令行参数 / 环境变量切形态（窗口打开后再切，OnOpened 会先摆成球形态）。</summary>
    public static void Apply(CapsuleWindow capsule, string[] args)
    {
        string? mode = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase))
            {
                mode = args[i + 1].Trim().ToLowerInvariant();
            }
        }

        // 命令行优先；没有就用环境变量。
        mode ??= Environment.GetEnvironmentVariable(AutoModeVariable)?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(mode))
        {
            mode = "orb";
        }

        Dispatcher.UIThread.Post(() => ApplyMode(capsule, mode!), DispatcherPriority.Loaded);
    }

    private static void ApplyMode(CapsuleWindow capsule, string mode)
    {
        switch (mode)
        {
            case "orb":
                capsule.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
                break;

            case "menu":
                // 球形态 + 稍后自动弹菜单（验证菜单在球正上方、开合时窗口不挪位置）。
                capsule.SetMode(DisplayModes.Orb, DisplayPayload.Empty);

                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(MenuAutoDelayMs),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    capsule.ToggleOrbMenu();
                };
                timer.Start();
                break;

            case "recording":
                capsule.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
                break;

            case "processing":
                capsule.SetMode(DisplayModes.Processing, new DisplayPayload(Message: "识别中…"));
                break;

            case "done":
                capsule.SetMode(
                    DisplayModes.Result,
                    new DisplayPayload(Message: "已输入", Text: "这是识别出来的最终文字。"));
                break;

            case "error":
                capsule.SetMode(DisplayModes.Error, new DisplayPayload(Message: "没听清，请再说一遍。"));
                break;

            default:
                Console.Error.WriteLine($"未知的形态：{mode}（忽略）");
                break;
        }
    }
}
