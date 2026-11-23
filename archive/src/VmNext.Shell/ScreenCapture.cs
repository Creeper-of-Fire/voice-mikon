using System;
using System.Runtime.InteropServices;

namespace VmNext.Shell;

/// <summary>
/// 屏幕像素抓取（纯 P/Invoke，不依赖 System.Drawing，保证 NativeAOT 下也能用）。
///
/// 用途：自动化验证"透明窗口是不是真透明"。
/// 做法是同一个屏幕矩形抓两次 —— 窗口显示前一次、显示后一次：
/// · 球的中心像素应该变了（内容画上去了）
/// · 球外的角像素应该**没变**（说明那部分真的透出了桌面，而不是画了一层灰底）
/// </summary>
public static class ScreenCapture
{
    /// <summary>抓取屏幕上一块矩形，返回 BGRA 像素（每像素 4 字节，顺序 B,G,R,A）。</summary>
    /// <param name="x">屏幕左（物理像素）。</param>
    /// <param name="y">屏幕上（物理像素）。</param>
    /// <param name="width">宽（物理像素）。</param>
    /// <param name="height">高（物理像素）。</param>
    /// <returns>长度 = width * height * 4 的数组；失败返回 null。</returns>
    public static int[]? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldObject = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // 负数 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,    // BI_RGB
                    biSizeImage = (uint)(width * height * 4),
                },
            };

            IntPtr bits;
            bitmap = NativeMethods.CreateDIBSection(memDc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            oldObject = NativeMethods.SelectObject(memDc, bitmap);
            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, x, y, NativeMethods.SRCCOPY))
            {
                return null;
            }

            var buffer = new byte[width * height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            var pixels = new int[buffer.Length / 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                // 以 BGRA 字节序存成 int，低位在前
                pixels[i] = buffer[i * 4] | (buffer[i * 4 + 1] << 8) | (buffer[i * 4 + 2] << 16) | (buffer[i * 4 + 3] << 24);
            }

            return pixels;
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>取某个像素的颜色，返回 0xRRGGBB。</summary>
    public static int PixelAt(int[] pixels, int width, int x, int y)
    {
        // pixels 是"每像素一个 int"的数组，所以下标就是 y * width + x（不是字节偏移）
        int index = (y * width) + x;
        if (index < 0 || index >= pixels.Length)
        {
            return 0;
        }

        int p = pixels[index];
        int b = p & 0xFF;
        int g = (p >> 8) & 0xFF;
        int r = (p >> 16) & 0xFF;
        return (r << 16) | (g << 8) | b;
    }

    /// <summary>两个颜色的差异（各通道绝对差之和）。</summary>
    public static int ColorDistance(int a, int b)
    {
        int dr = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
        int dg = Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
        int db = Math.Abs((a & 0xFF) - (b & 0xFF));
        return dr + dg + db;
    }
}
