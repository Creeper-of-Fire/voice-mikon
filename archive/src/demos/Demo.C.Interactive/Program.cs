using System;
using System.Linq;
using System.Text;
using Avalonia;
using VmNext.Shell;

namespace DemoC;

/// <summary>Demo C 的进程入口。</summary>
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
            Console.WriteLine("Demo C 验什么：拖拽 + 多显示器 + 文字选择复制");
            Console.WriteLine("怎么看：①按住球拖 100px 再松手，要跟手、松手不跳");
            Console.WriteLine("        ②（双屏的话）把球拖到另一块屏，球的大小随缩放变、位置不能错位");
            Console.WriteLine("        ③单击球展开胶囊，里面那段文字能用鼠标拖选，Ctrl+C 后到别处能粘贴出来");
            Console.WriteLine("            （这一条是已知边界点：不工作就如实记现象，不算整体失败）");
            Console.WriteLine("按 Q 退出（会占用 Q 键）。");
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
