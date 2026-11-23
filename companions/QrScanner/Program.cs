using System.Drawing;
using System.Windows.Forms;
using QrScanner.Core;
using QrScanner.Ui;

namespace QrScanner;

/// <summary>
/// 二维码扫描工具 —— 入口。
///
/// <para><b>架构独立</b>：与语音主程序零 import（双向都不认识对方）。
/// 主程序只把本工具当**独立进程**拉起：不等它、不看它的输出、不管它的死活；
/// 本工具崩了不影响主程序。</para>
///
/// <para>交互流程照搬 Python 版（<c>voice-mikon/companions/qr_scanner</c>）：
/// 截屏 → 全屏框选 → 解码 → 结果窗（自动复制）。</para>
///
/// <para>单独跑：
/// <code>
/// QrScanner.exe              截屏 → 框选 → 识别 → 结果窗
/// QrScanner.exe --image X    直接识别一张图片（调试 / 二次利用）
/// QrScanner.exe --selftest   不弹窗：截屏 + 全图解码，链路自检
/// </code></para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // DPI aware 是全工具坐标系的根，必须先于任何截屏 / 建窗。
        // per-monitor v2 → 缩放不同的屏上，各屏坐标与截图像素是一套。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // 工具是 GUI 场景，多半没终端看 stderr —— 弹窗说清楚，别静默。
            ReportFatal("二维码扫描工具出错：\n" + ex.Message);
            return 1;
        }
    }

    /// <summary>跑一遍工具（正常流程 / --image / --selftest）。</summary>
    private static int Run(string[] args)
    {
        // --image X：跳过截屏和框选，直接识别图片（主要给调试用，
        // 顺带让工具能当命令行小解码器使）。
        string? imagePath = FlagValue(args, "--image");

        if (imagePath is not null)
        {
            using Bitmap? image = LoadImage(imagePath);
            if (image is null)
            {
                ReportFatal("图片读不了：\n" + imagePath);
                return 1;
            }

            IReadOnlyList<QrHit> hits = QrDecoder.Decode(image);
            foreach (QrHit hit in hits)
            {
                Console.WriteLine($"[{hit.Kind}] {hit.Text}");
            }

            if (hits.Count == 0)
            {
                Console.WriteLine("没识别到码");
            }

            return 0;
        }

        if (Array.IndexOf(args, "--selftest") >= 0)
        {
            // 链路自检：截屏 + 全图解码，结果走 stdout，不弹任何窗。
            using Bitmap shot = ScreenCapture.GrabVirtualScreen();
            IReadOnlyList<QrHit> found = QrDecoder.Decode(shot);
            foreach (QrHit hit in found)
            {
                Console.WriteLine($"[{hit.Kind}] {hit.Text}");
            }

            Console.WriteLine(found.Count > 0
                ? "selftest OK: 截屏/解码链路通"
                : "selftest OK: 截屏/解码链路通，当前屏幕上没找到码");
            return 0;
        }

        // 正常流程：截屏 → 框选 → 解码 → 结果窗。
        using Bitmap screenshot = ScreenCapture.GrabVirtualScreen();

        Rectangle? region = SelectorForm.SelectRegion(screenshot);
        if (region is null)
        {
            // 用户取消（Esc / 右键 / 误触）：静默退出，别弹什么窗。
            return 0;
        }

        using Bitmap target = ImageOps.Crop(screenshot, region.Value);
        IReadOnlyList<QrHit> decoded = QrDecoder.Decode(target);

        using var result = new ResultForm(decoded);
        result.ShowDialog();
        return 0;
    }

    /// <summary>取 <c>--flag VALUE</c> 的 VALUE；没有这个 flag 返回 null。</summary>
    private static string? FlagValue(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        if (index >= 0 && index + 1 < args.Length)
        {
            return args[index + 1];
        }

        return null;
    }

    /// <summary>读一张图片（读不了返回 null）。</summary>
    private static Bitmap? LoadImage(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            // 复制到内存再解码：Image.FromFile 会锁着文件，图像还没用完就删不掉。
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return new Bitmap(buffer);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>启动期致命错误：弹窗报告（工具是 GUI 场景，多半没终端看 stderr）。</summary>
    private static void ReportFatal(string message)
    {
        Console.Error.WriteLine("qr_scanner: " + message);

        try
        {
            MessageBox.Show(
                message,
                "二维码扫描",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // 连弹窗都起不来，stderr 那份就是全部了。
        }
    }
}
