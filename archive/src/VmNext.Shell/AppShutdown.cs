using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace VmNext.Shell;

/// <summary>退出当前 Avalonia 应用（Avalonia 11 的 Application 没有 Shutdown 方法）。</summary>
public static class AppShutdown
{
    /// <summary>关闭桌面生命周期并退出进程。</summary>
    public static void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            return;
        }

        Environment.Exit(0);
    }
}
