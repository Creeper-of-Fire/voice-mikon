using System;
using System.Linq;
using System.Text;
using Avalonia;
using VmNext.Shell;

namespace DemoB;

/// <summary>Demo B 的进程入口。</summary>
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
            Console.WriteLine("Demo B 验什么：球↔胶囊形变 + 呼吸 + 旋转 + 波形条 + 主题切换");
            Console.WriteLine("怎么看：①点球（或按空格）看球在约 0.8 秒里长成胶囊，过渡要顺，透明处不能有灰带/残影");
            Console.WriteLine("        ②胶囊里有 13 根从右往左滚的波形条，左边渐隐；右上一个 loader 在转，右下麦克风在呼吸");
            Console.WriteLine("        ③按 T 切深/浅主题，颜色要平滑过渡、深浅两套文字都看得清");
            Console.WriteLine("        ④整体看 10 秒有没有掉帧感");
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
