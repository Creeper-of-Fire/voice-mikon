using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace VoiceMikonCSharp.Companions.Tests;

/// <summary>
/// 测试用的图像工具：把 ZXing 生成的码位图落成 <see cref="Bitmap"/>。
///
/// <para>ZXing 的 <see cref="PixelData"/> 是 **BGRA**（每像素 4 字节），
/// 与工具内部统一成的 32bpp ARGB 内存序一致 —— 直接搬进位图即可。</para>
/// </summary>
internal static class TestImages
{
    /// <summary>生成一张二维码位图。</summary>
    public static Bitmap EncodeQr(string text, int size = 320)
    {
        var options = new EncodingOptions
        {
            Width = size,
            Height = size,
            Margin = 2,
        };

        // QR 码默认字符集是 ISO-8859-1；中文必须显式声明 UTF-8
        // （ZXing 会据此写 ECI 段，解码端才认得出中文）。
        options.Hints[EncodeHintType.CHARACTER_SET] = "UTF-8";

        var writer = new BarcodeWriterPixelData
        {
            // 默认的 Format 不是 QR_CODE（值 0 是 AZTEC）—— 必须显式写。
            Format = BarcodeFormat.QR_CODE,
            Options = options,
        };

        PixelData data = writer.Write(text);

        var image = new Bitmap(data.Width, data.Height, PixelFormat.Format32bppArgb);
        BitmapData bits = image.LockBits(
            new Rectangle(0, 0, image.Width, image.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            for (int y = 0; y < image.Height; y++)
            {
                IntPtr row = IntPtr.Add(bits.Scan0, y * bits.Stride);
                Marshal.Copy(data.Pixels, y * data.Width * 4, row, data.Width * 4);
            }
        }
        finally
        {
            image.UnlockBits(bits);
        }

        return image;
    }

    /// <summary>一张纯白（没有码）的位图。</summary>
    public static Bitmap Blank(int width = 240, int height = 240)
    {
        var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(image);
        g.Clear(Color.White);
        return image;
    }
}
