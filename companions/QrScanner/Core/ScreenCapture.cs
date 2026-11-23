using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace QrScanner.Core;

/// <summary>
/// 屏幕截取：<c>Graphics.CopyFromScreen</c> + 虚拟桌面几何。
///
/// <para>照搬 Python 版的 mss：截的是**整个虚拟桌面**（所有显示器拼起来的那一整块），
/// 原点平移到虚拟桌面左上角 —— 选区窗口也从那儿铺满，两套坐标天然一致。</para>
///
/// <para>坐标系约定（全工具统一）：**物理像素，原点在虚拟桌面左上角**。
/// 进程声明 per-monitor v2 之后，<see cref="SystemInformation.VirtualScreen"/> /
/// <c>CopyFromScreen</c> / 鼠标事件坐标说的是同一套坐标，不再有任何换算。</para>
/// </summary>
public static class ScreenCapture
{
    /// <summary>截取整个虚拟桌面（所有显示器拼起来的那一整块）。</summary>
    public static Bitmap GrabVirtualScreen()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(image);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return image;
    }
}
