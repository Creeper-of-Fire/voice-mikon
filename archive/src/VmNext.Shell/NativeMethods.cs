using System;
using System.Runtime.InteropServices;

namespace VmNext.Shell;

/// <summary>
/// 全部 Win32 P/Invoke 集中在本文件。
///
/// 约定（重构骨架级要求，后续正式项目沿用）：
/// 1. 只做「查询」与「窗口样式 / 区域设置」，绝不注入输入。
///    禁止使用 SendInput / SetCursorPos / keybd_event / mouse_event 等会劫持用户鼠标键盘的 API
///    —— 本机验证期间用户正在使用电脑。
/// 2. 每个 Win32 常量旁边用中文说明它的确切含义，避免"抄来的魔法数字"。
/// 3. 新增调用一律先写 XML 注释，说明用途、参数、返回值与失败语义。
/// </summary>
public static class NativeMethods
{
    #region 常量：扩展窗口样式（GWL_EXSTYLE）

    /// <summary>GWL_EXSTYLE：GetWindowLong/SetWindowLong 用来读写"扩展窗口样式"的索引。</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>WS_EX_TOPMOST (0x8)：窗口永远位于普通窗口之上。</summary>
    public const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>WS_EX_TRANSPARENT (0x20)：整个窗口对鼠标命中测试透明（会连自己的点击也一起吞掉，本项目不用它，用区域裁剪代替）。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>WS_EX_TOOLWINDOW (0x80)：不在任务栏出现，Alt+Tab 也不显示。</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>WS_EX_LAYERED (0x80000)：分层窗口，支持逐像素 alpha 合成。</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>WS_EX_APPWINDOW (0x40000)：强制作为应用窗口出现在任务栏（与 WS_EX_TOOLWINDOW 互斥，本项目不用）。</summary>
    public const int WS_EX_APPWINDOW = 0x00040000;

    /// <summary>WS_EX_NOACTIVATE (0x8000000)：用户点击该窗口时**不会**把它变成前台窗口，键盘焦点留在原窗口。</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    #endregion

    #region 常量：消息与鼠标激活

    /// <summary>WM_MOUSEACTIVATE (0x21)：鼠标按下时由窗口决定是否激活自己。</summary>
    public const uint WM_MOUSEACTIVATE = 0x0021;

    /// <summary>WM_LBUTTONDOWN (0x201)：鼠标左键按下（合成消息用，不注入真实输入）。</summary>
    public const uint WM_LBUTTONDOWN = 0x0201;

    /// <summary>WM_LBUTTONUP (0x202)：鼠标左键抬起。</summary>
    public const uint WM_LBUTTONUP = 0x0202;

    /// <summary>WM_HOTKEY (0x312)：RegisterHotKey 注册的全局热键被按下时投递到目标窗口。</summary>
    public const uint WM_HOTKEY = 0x0312;

    /// <summary>MA_ACTIVATE (0)：允许激活窗口。</summary>
    public const int MA_ACTIVATE = 0;

    /// <summary>MA_NOACTIVATE (3)：不允许激活窗口（点击后焦点留在原窗口）。</summary>
    public const int MA_NOACTIVATE = 3;

    /// <summary>MA_NOACTIVATEANDEAT (4)：不允许激活，且吃掉这次点击。</summary>
    public const int MA_NOACTIVATEANDEAT = 4;

    #endregion

    #region 常量：SetWindowPos 标志

    /// <summary>SWP_NOSIZE (0x1)：不改变窗口大小。</summary>
    public const uint SWP_NOSIZE = 0x0001;

    /// <summary>SWP_NOZORDER (0x4)：不改变 Z 序。</summary>
    public const uint SWP_NOZORDER = 0x0004;

    /// <summary>SWP_NOACTIVATE (0x10)：不激活窗口（移动窗口时必须加，否则会抢焦点）。</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    #endregion

    #region 常量：GetWindowRgn 返回值

    /// <summary>GetWindowRgn 失败（返回值 0）。</summary>
    public const int RGNNULL_ERROR = 0;

    /// <summary>GetWindowRgn：窗口没有设置任何区域（整个矩形可命中，点击不会穿透）。</summary>
    public const int RGNNULL_NULLREGION = 1;

    /// <summary>GetWindowRgn：区域是单个矩形/椭圆等简单形状。</summary>
    public const int RGNNULL_SIMPLEREGION = 2;

    /// <summary>GetWindowRgn：区域是多个形状的组合。</summary>
    public const int RGNNULL_COMPLEXREGION = 3;

    #endregion

    #region 结构体

    /// <summary>Win32 RECT：整数像素矩形。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        /// <summary>左边界。</summary>
        public int Left;

        /// <summary>上边界。</summary>
        public int Top;

        /// <summary>右边界。</summary>
        public int Right;

        /// <summary>下边界。</summary>
        public int Bottom;

        /// <summary>宽度（Right - Left）。</summary>
        public int Width => Right - Left;

        /// <summary>高度（Bottom - Top）。</summary>
        public int Height => Bottom - Top;
    }

    /// <summary>Win32 POINT：整数像素点。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>X 坐标。</summary>
        public int X;

        /// <summary>Y 坐标。</summary>
        public int Y;
    }

    /// <summary>Win32 WNDCLASSEX：注册窗口类用。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        /// <summary>结构体大小（字节）。</summary>
        public int cbSize;

        /// <summary>窗口类样式。</summary>
        public int style;

        /// <summary>窗口过程函数指针。</summary>
        public IntPtr lpfnWndProc;

        /// <summary>类附加数据字节数。</summary>
        public int cbClsExtra;

        /// <summary>窗口附加数据字节数。</summary>
        public int cbWndExtra;

        /// <summary>模块句柄。</summary>
        public IntPtr hInstance;

        /// <summary>图标句柄。</summary>
        public IntPtr hIcon;

        /// <summary>光标句柄。</summary>
        public IntPtr hCursor;

        /// <summary>背景画刷句柄。</summary>
        public IntPtr hbrBackground;

        /// <summary>菜单名。</summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;

        /// <summary>窗口类名。</summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;

        /// <summary>小图标句柄。</summary>
        public IntPtr hIconSm;
    }

    #endregion

    #region 窗口样式读写

    // 说明：GWL_EXSTYLE 的值本身是 32 位 DWORD，用 SetWindowLong（而非 SetWindowLongPtr）
    // 在 64 位进程上也是安全的；SetWindowLongPtr 只在设置指针型项目（GWL_WNDPROC 等）时才必需。
    // 这里仍然做 32/64 位分派，避免在 32 位系统上 GetWindowLongPtrW 不存在导致 EntryPointNotFoundException。

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>读取窗口的扩展样式（GWL_EXSTYLE）。</summary>
    public static int GetWindowExStyle(IntPtr hWnd)
        => IntPtr.Size == 8
            ? unchecked((int)GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64())
            : GetWindowLong32(hWnd, GWL_EXSTYLE);

    /// <summary>写入窗口的扩展样式（GWL_EXSTYLE），返回修改前的值；失败返回 0（用 Marshal.GetLastWin32Error 判错）。</summary>
    public static int SetWindowExStyle(IntPtr hWnd, int newStyle)
        => IntPtr.Size == 8
            ? unchecked((int)SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(newStyle)).ToInt64())
            : SetWindowLong32(hWnd, GWL_EXSTYLE, newStyle);

    /// <summary>把若干扩展样式位加上（不改动其它位）。</summary>
    public static int AddWindowExStyle(IntPtr hWnd, int bits)
    {
        int current = GetWindowExStyle(hWnd);
        return SetWindowExStyle(hWnd, current | bits);
    }

    #endregion

    #region 窗口区域（点击穿透）

    /// <summary>创建一个矩形区域（用作 GetWindowRgn 的输出缓冲区）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    /// <summary>
    /// 创建一个圆角矩形区域。左右上下是"右/下边界"（Win32 用的是排他边界），
    /// widthEllipse/heightEllipse 是圆角椭圆的宽高：传 (直径, 直径) 就是正圆。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    /// <summary>把区域设置到窗口上：区域之外的像素不再属于窗口，点击会"穿透"到下层窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    /// <summary>取窗口当前区域，返回值见 RGNNULL_* 常量。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    /// <summary>取窗口区域的包围盒（物理像素，窗口客户区坐标）。注意：它在 user32.dll，不在 gdi32。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowRgnBox(IntPtr hWnd, out RECT lpRect);

    /// <summary>删除 GDI 对象（区域）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr hObject);

    #endregion

    #region 窗口查询

    /// <summary>取窗口在屏幕上的矩形（物理像素）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>取窗口客户区矩形（物理像素）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>返回包含该屏幕点的窗口（命中测试会考虑窗口区域，所以能验证"点击穿透"）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(POINT point);

    /// <summary>取当前前台窗口句柄。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>取窗口所在显示器的 DPI（Windows 10 1607+）；返回 0 表示取不到。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>向窗口投递消息（不阻塞、不注入真实输入）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>同步向窗口发消息并取返回值。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>移动/调整窗口。移动时务必带 SWP_NOACTIVATE，否则会抢走用户焦点。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>枚举顶层窗口的回调。</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>枚举所有顶层窗口。</summary>
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>取窗口所属线程与进程 ID。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>取窗口标题（用于自动化里定位窗口）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    /// <summary>窗口是否可见。</summary>
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>SPI_GETWORKAREA (0x0030)：取主显示器工作区矩形（物理像素）。</summary>
    public const uint SPI_GETWORKAREA = 0x0030;

    /// <summary>取系统/主显示器参数（这里只用工作区）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

    #endregion

    #region 全局热键

    /// <summary>注册全局热键；热键消息投递到 hWnd。成功返回 true。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>注销全局热键。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>MOD_ALT：Alt 修饰键。</summary>
    public const uint MOD_ALT = 0x0001;

    /// <summary>MOD_CONTROL：Ctrl 修饰键。</summary>
    public const uint MOD_CONTROL = 0x0002;

    /// <summary>MOD_SHIFT：Shift 修饰键。</summary>
    public const uint MOD_SHIFT = 0x0004;

    /// <summary>MOD_NOREPEAT (0x4000)：长按不重复触发。</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    #endregion

    #region 屏幕像素抓取（用于验证"真透明"）

    /// <summary>BITMAPINFOHEADER：位图信息头，CreateDIBSection 用。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        /// <summary>结构体大小（sizeof(BITMAPINFOHEADER)）。</summary>
        public int biSize;

        /// <summary>位图宽（像素）。</summary>
        public int biWidth;

        /// <summary>位图高（像素），负数表示自上而下排列。</summary>
        public int biHeight;

        /// <summary>平面数，恒为 1。</summary>
        public ushort biPlanes;

        /// <summary>每像素位数，这里用 32（BGRA）。</summary>
        public ushort biBitCount;

        /// <summary>压缩方式，0 = BI_RGB（无压缩）。</summary>
        public uint biCompression;

        /// <summary>图像数据大小（BI_RGB 时可写 0）。</summary>
        public uint biSizeImage;

        /// <summary>水平分辨率（像素/米）。</summary>
        public int biXPelsPerMeter;

        /// <summary>垂直分辨率（像素/米）。</summary>
        public int biYPelsPerMeter;

        /// <summary>实际使用的颜色数。</summary>
        public uint biClrUsed;

        /// <summary>重要颜色数。</summary>
        public uint biClrImportant;
    }

    /// <summary>BITMAPINFO：位图信息 = 信息头 + 调色板（32bpp BI_RGB 时调色板不用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        /// <summary>位图信息头。</summary>
        public BITMAPINFOHEADER bmiHeader;

        /// <summary>调色板首项（32bpp BI_RGB 不使用）。</summary>
        public uint bmiColors0;
    }

    /// <summary>取设备上下文（传 IntPtr.Zero 表示整个屏幕）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    /// <summary>释放设备上下文。</summary>
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>创建与指定 DC 兼容的内存 DC。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    /// <summary>删除 DC。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    /// <summary>创建 DIB 段（可以直接拿到像素内存指针）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

    /// <summary>把 GDI 对象选入 DC，返回原来的对象（用于恢复）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    /// <summary>SRCCOPY：直接复制源 DC 的像素。</summary>
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>位块传输（这里是"从屏幕复制一块出来"）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    #endregion

    #region 自建窗口类（用于接收 WM_HOTKEY）

    /// <summary>取当前模块句柄（注册窗口类需要）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>注册窗口类。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    /// <summary>创建窗口。hWndParent 传 HWND_MESSAGE(-3) 创建"消息专用窗口"（不在屏幕上显示）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    /// <summary>销毁窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>默认窗口过程。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>取当前进程的控制台窗口句柄（没有控制台时返回 0）。</summary>
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    /// <summary>SW_HIDE (0)：隐藏窗口。</summary>
    public const int SW_HIDE = 0;

    /// <summary>显示/隐藏窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    #endregion
}
