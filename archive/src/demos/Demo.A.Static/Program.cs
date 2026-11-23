using System;
using System.Linq;
using System.Text;
using Avalonia;
using VmNext.Shell;

namespace DemoA;

/// <summary>Demo A 的进程入口。</summary>
internal static class Program
{
    /// <summary>程序入口。</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        TrySetUtf8Console();

        DemoOptions.Auto = args.Any(a => string.Equals(a, "--auto", StringComparison.OrdinalIgnoreCase));

        if (!DemoOptions.Auto)
        {
            Console.WriteLine("Demo A 验什么：真透明 + 不抢焦点 + 点击穿透 + 右键菜单");
            Console.WriteLine("怎么看：①球底下应该透出桌面（不是灰底）②先点你正在用的窗口，再点球/右键球，它的标题栏不能变灰");
            Console.WriteLine("        ③右键球看菜单能不能点 ④点球的四角（透明处），点击应该落到下面的窗口");
            Console.WriteLine("按 Q 退出（会占用 Q 键；窗口不抢焦点，所以用全局热键退出）。");
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("启动失败：" + ex);
            return 1;
        }
    }

    /// <summary>构建 Avalonia 应用。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>控制台按 UTF-8 输出，避免中文变成乱码。</summary>
    private static void TrySetUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 某些宿主环境不支持设置编码，忽略
        }
    }
}
