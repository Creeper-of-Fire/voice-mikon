using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QrScanner.Core;

/// <summary>
/// 位图小工具：统一格式 / 裁剪 / 反色 / 放大 / 取像素。
///
/// <para>统一成 <see cref="PixelFormat.Format32bppArgb"/>（内存序 BGRA）：
/// ZXing 的 <c>RGBLuminanceSource</c> 直接吃 BGRA，省掉一次通道转换；
/// 也让"取像素"这一步的 stride 处理只有一种情况。</para>
/// </summary>
public static class ImageOps
{
    /// <summary>统一成 32bpp ARGB，返回**新图**（不改原图）。</summary>
    public static Bitmap AsArgb(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(result);
        g.DrawImage(source, 0, 0);
        return result;
    }

    /// <summary>裁一块（坐标相对原图左上角）。</summary>
    public static Bitmap Crop(Bitmap source, Rectangle region)
    {
        var result = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(result);
        g.DrawImage(
            source,
            new Rectangle(0, 0, region.Width, region.Height),
            region,
            GraphicsUnit.Pixel);
        return result;
    }

    /// <summary>反色（救"黑底白码"）。</summary>
    public static Bitmap Inverted(Bitmap source)
    {
        Bitmap result = AsArgb(source);
        byte[] pixels = ToBgraBytes(result);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = (byte)(255 - pixels[i + 0]); // B
            pixels[i + 1] = (byte)(255 - pixels[i + 1]); // G
            pixels[i + 2] = (byte)(255 - pixels[i + 2]); // R
        }

        FromBgraBytes(result, pixels);
        return result;
    }

    /// <summary>放大一倍（救轻度模糊；高质量插值）。</summary>
    public static Bitmap Scale2x(Bitmap source)
    {
        var result = new Bitmap(source.Width * 2, source.Height * 2, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height));
        return result;
    }

    /// <summary>按比例调暗（选区外的底图；<paramref name="alpha"/> = 与全黑混合的比例）。</summary>
    public static Bitmap Dimmed(Bitmap source, double alpha)
    {
        Bitmap result = AsArgb(source);
        byte[] pixels = ToBgraBytes(result);
        double k = 1 - alpha;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = (byte)(pixels[i + 0] * k);
            pixels[i + 1] = (byte)(pixels[i + 1] * k);
            pixels[i + 2] = (byte)(pixels[i + 2] * k);
        }

        FromBgraBytes(result, pixels);
        return result;
    }

    /// <summary>取紧凑的 BGRA 像素（每像素 4 字节，已去掉每行的 stride 填充）。</summary>
    public static byte[] ToBgraBytes(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = new byte[width * height * 4];

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            for (int y = 0; y < height; y++)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(row, pixels, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return pixels;
    }

    /// <summary>把紧凑 BGRA 像素写回（长度必须与图一致）。</summary>
    public static void FromBgraBytes(Bitmap bitmap, byte[] pixels)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            for (int y = 0; y < height; y++)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(pixels, y * width * 4, row, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
