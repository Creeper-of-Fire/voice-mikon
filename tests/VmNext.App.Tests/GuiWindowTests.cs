using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace VmNext.App.Tests;

/// <summary>
/// GUI 形态的测试：拉起子进程 → 等胶囊窗口出现 → 用 PrintWindow 抓窗口自身的内容
/// → 统计非透明像素 → 关窗退出。
///
/// <para>为什么用 <c>PrintWindow</c> 而不是"抓屏幕那块地方"：屏幕抓取走的是 GDI 的
/// 桌面 DC，**看不到 DWM 合成出来的窗口内容**（本机实测：同一个纯 Win32 窗口
/// PrintWindow 有像素、屏幕抓取没有）。而 PrintWindow 抓的是窗口自己的绘制结果，
/// 正好回答"窗口里到底画出来没有"。</para>
///
/// <para>⚠ 这条测试**要真的建窗口**，所以不能跑在无头平台里 —— 它起的是真的子进程。</para>
/// </summary>
public class GuiWindowTests
{
    /// <summary>
    /// GUI 形态：窗口起来、画出了东西（红球）、关掉就退出。
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void gui_窗口起来_画出了红球_关窗能退出()
    {
        string exe = RequireAppExecutable();

        using Process process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        }) ?? throw new InvalidOperationException($"起不来子进程: {exe}");

        try
        {
            // ---- 等胶囊窗口出现（最多 30 秒：模型加载 + Avalonia 启动可能要几秒）----
            IntPtr hwnd = IntPtr.Zero;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 30)
            {
                hwnd = FindWindow(process.Id);
                if (hwnd != IntPtr.Zero)
                {
                    break;
                }

                Thread.Sleep(200);
            }

            Assert.True(hwnd != IntPtr.Zero, "30 秒内没等到胶囊窗口");

            // ---- 抓窗口自身的内容，统计非透明像素 ----
            // 窗口刚创建时还没排版完（客户区尺寸还是默认值），所以要**轮询**，
            // 直到真的画出东西为止 —— 第一次抓到空的就判失败会误伤。
            int opaque = 0;
            int total = 0;
            int redCount = 0;
            var sw2 = Stopwatch.StartNew();
            while (sw2.Elapsed.TotalSeconds < 30)
            {
                GetClientRect(hwnd, out RECT client);
                int width = client.Right;
                int height = client.Bottom;

                if (width > 0 && height > 0)
                {
                    (opaque, total, redCount) = CountPixels(hwnd, width, height);
                    if (redCount > 200)
                    {
                        break;
                    }
                }

                Thread.Sleep(300);
            }

            // 球是 40 逻辑像素直径（capsule.css 的 .recording-pill height:40px），
            // 至少要画出几百个像素。
            Assert.True(
                opaque > 500,
                $"窗口里几乎没有非透明像素（{opaque}/{total}）—— 渲染没出来？");

            // 录音红（#ff5a5a）至少要占几十个像素：球本身。
            Assert.True(
                redCount > 200,
                $"没找到录音红的球（红像素 {redCount}）—— 渲染没出来？");

            // ---- 关窗 → 正常退出 ----
            PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

            Assert.True(
                process.WaitForExit(20_000),
                "发 WM_CLOSE 之后 20 秒没退出");

            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>找这个进程的顶层窗口（只要有客户区的）。</summary>
    private static IntPtr FindWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner != (uint)pid)
            {
                return true;
            }

            GetClientRect(h, out RECT c);
            if (c.Right > 0 && c.Bottom > 0 && GetWindowTextLength(h) > 0)
            {
                found = h;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>抓窗口内容，返回（非透明像素数, 总像素数, 录音红像素数）。</summary>
    private static (int Opaque, int Total, int Red) CountPixels(IntPtr hwnd, int width, int height)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = (uint)(width * height * 4),
            },
        };

        IntPtr bits;
        IntPtr bitmap = CreateDIBSection(memDc, ref info, 0, out bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDc, bitmap);

        bool ok = PrintWindow(hwnd, memDc, 0x00000002 /* PW_RENDERFULLCONTENT */);
        var buffer = new byte[width * height * 4];
        Marshal.Copy(bits, buffer, 0, buffer.Length);

        SelectObject(memDc, old);
        DeleteObject(bitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        Assert.True(ok, "PrintWindow 失败");

        int opaque = 0;
        int red = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            int a = buffer[i + 3];
            int b = buffer[i];
            int g = buffer[i + 1];
            int r = buffer[i + 2];

            if (a > 8)
            {
                opaque++;
            }

            // 录音红 #ff5a5a
            if (a > 128 && r > 200 && g < 120 && b < 120)
            {
                red++;
            }
        }

        return (opaque, width * height, red);
    }

    /// <summary>找不到构建出来的可执行文件就跳过（没构建过 App 时不该红）。</summary>
    private static string RequireAppExecutable()
    {
        string? exe = FindAppExecutable();
        if (exe is null)
        {
            Assert.Skip("没找到 VmNext.App.exe（先 dotnet build src/VmNext.App）");
        }

        return exe;
    }

    /// <summary>从测试程序集所在目录往上找仓库根，再找构建产物。</summary>
    private static string? FindAppExecutable()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VmNext.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        foreach (string configuration in new[] { "Debug", "Release" })
        {
            string exe = Path.Combine(
                dir.FullName, "src", "VmNext.App", "bin", configuration, "net10.0", "VmNext.App.exe");

            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    // ---------- Win32 ----------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
