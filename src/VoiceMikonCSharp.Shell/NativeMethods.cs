using System;
using System.Runtime.InteropServices;

namespace VoiceMikonCSharp.Shell;

/// <summary>
/// 全部 Win32 P/Invoke 集中在本文件。
///
/// 约定（重构骨架级要求，后续正式项目沿用）：
/// 1. 骨架期只做「查询」与「窗口样式 / 区域设置」，绝不注入输入。
///    禁止使用 SendInput / SetCursorPos / keybd_event / mouse_event 等会劫持用户鼠标键盘的 API
///    —— 本机验证期间用户正在使用电脑。
///    ⚠ **K1 起这条有例外**：产品本身就是"把文字注入当前光标"的语音输入工具，
///    必须能注入键盘事件。注入能力集中在 <c>VoiceMikonCSharp.Core.Inject.Injector</c> 一处，
///    唯一的调用方式是「剪贴板 + Ctrl+V」并且**必须读回校验**；
///    其余地方（窗口层、验证壳）仍然只做查询。
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

    /// <summary>SWP_NOMOVE (0x2)：不改变窗口位置。</summary>
    public const uint SWP_NOMOVE = 0x0002;

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

    /// <summary>GWL_STYLE：GetWindowLong/SetWindowLong 用来读写"窗口样式"的索引。</summary>
    public const int GWL_STYLE = -16;

    /// <summary>WS_CAPTION (0x00C00000)：标题栏。</summary>
    public const int WS_CAPTION = 0x00C00000;

    /// <summary>WS_THICKFRAME (0x00040000)：可调整大小的边框。</summary>
    public const int WS_THICKFRAME = 0x00040000;

    /// <summary>WS_BORDER (0x00800000)：单线边框。</summary>
    public const int WS_BORDER = 0x00800000;

    /// <summary>WS_DLGFRAME (0x00400000)：对话框边框。</summary>
    public const int WS_DLGFRAME = 0x00400000;

    /// <summary>WS_POPUP (0x80000000)：弹出窗口（无标题栏、无边框）。</summary>
    public const int WS_POPUP = unchecked((int)0x80000000);

    /// <summary>读取窗口的窗口样式（GWL_STYLE）。</summary>
    public static int GetWindowStyle(IntPtr hWnd)
        => IntPtr.Size == 8
            ? unchecked((int)GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64())
            : GetWindowLong32(hWnd, GWL_STYLE);

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

    #region 分层窗口与透明

    /// <summary>LWA_ALPHA (0x2)：整体 alpha 生效（配合 <c>bAlpha</c>）。</summary>
    public const uint LWA_ALPHA = 0x00000002;

    /// <summary>LWA_COLORKEY (0x1)：颜色键生效（与 <c>crKey</c> 相同的像素完全透明）。</summary>
    public const uint LWA_COLORKEY = 0x00000001;

    /// <summary>
    /// 设置分层窗口（<c>WS_EX_LAYERED</c>）的属性。
    ///
    /// <para><b>两种用法</b>：
    /// <list type="bullet">
    /// <item><c>LWA_COLORKEY</c>：与 <paramref name="crKey"/> 颜色相同的像素**完全透明** ——
    /// 透明窗口的经典做法（色键）。</item>
    /// <item><c>LWA_ALPHA</c>：整个窗口按 <paramref name="bAlpha"/>（0..255）半透明。</item>
    /// </list></para>
    ///
    /// <para>⚠ 窗口必须**先**带上 <c>WS_EX_LAYERED</c>，本调用才有效。</para>
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="crKey">颜色键（RGB）。</param>
    /// <param name="bAlpha">整体 alpha（0 = 全透明，255 = 不透明）。</param>
    /// <param name="dwFlags"><see cref="LWA_COLORKEY"/> / <see cref="LWA_ALPHA"/>。</param>
    /// <returns>成功返回 true。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(
        IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>DWM 边距（把 DWM 的边框 / 阴影扩展进客户区）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        /// <summary>左边扩展的宽度（物理像素）。</summary>
        public int cxLeftWidth;

        /// <summary>右边扩展的宽度（物理像素）。</summary>
        public int cxRightWidth;

        /// <summary>上边扩展的高度（物理像素）。</summary>
        public int cyTopHeight;

        /// <summary>下边扩展的高度（物理像素）。</summary>
        public int cyBottomHeight;
    }

    /// <summary>
    /// 把 DWM 的边框 / 阴影扩展到客户区（<c>dwmapi.dll</c>）。
    ///
    /// <para>传 <c>-1</c> 的四边会让整块客户区变成"玻璃"——
    /// 这是 Windows 7 时代的 Aero Glass 做法；在 Win10/11 上要配合
    /// <c>WS_EX_LAYERED</c> 才看得出效果。</para>
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="pMarInset">四边的扩展量（物理像素）。</param>
    /// <returns>0 = 成功；非 0 是 HRESULT 错误码。</returns>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

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

    /// <summary>
    /// 把两个区域合并成一个（CombineRgn 的模式）。
    /// RGN_OR = 2：取并集（两块都在）。
    /// </summary>
    public const int RGN_OR = 2;

    /// <summary>
    /// 合并区域：hDest = hSrc1 ∪ hSrc2（模式 RGN_OR）。
    /// 目标区域的所有权归调用方（合并完要 DeleteObject）。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CombineRgn(IntPtr hDest, IntPtr hSrc1, IntPtr hSrc2, int iMode);

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

    /// <summary>
    /// 同步向窗口发消息，lParam 是字符串缓冲区（WM_GETTEXT 读控件文本用）。
    ///
    /// wParam 传缓冲区容量；返回复制的字符数（不含结束符）。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, System.Text.StringBuilder lParam);

    /// <summary>移动/调整窗口。移动时务必带 SWP_NOACTIVATE，否则会抢走用户焦点。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>枚举顶层窗口的回调。</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>枚举所有顶层窗口。</summary>
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>枚举指定窗口的所有子窗口（回调返回 false 停止枚举）。</summary>
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

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

    /// <summary>
    /// MOD_WIN：Win 修饰键。
    ///
    /// K3 补的：热键字符串解析要认 win / super / cmd / meta（对齐 Python 的
    /// <c>parse_hotkey</c>），而 Win32 的这个常量之前没落进 P/Invoke 源。
    /// </summary>
    public const uint MOD_WIN = 0x0008;

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

    /// <summary>SW_SHOWNOACTIVATE (4)：显示窗口但**不激活**它（不抢焦点）。</summary>
    public const int SW_SHOWNOACTIVATE = 4;

    /// <summary>HWND_TOPMOST：<c>SetWindowPos</c> 的"插到置顶组最顶端"句柄。</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>显示/隐藏窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    #endregion

    #region 剪贴板（K1：注入层与剪贴板层）

    /// <summary>CF_UNICODETEXT (13)：剪贴板里的 Unicode 文本格式。</summary>
    public const uint CF_UNICODETEXT = 13;

    /// <summary>GMEM_MOVEABLE (0x0002)：GlobalAlloc 分配可移动内存（SetClipboardData 要求可移动）。</summary>
    public const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// 打开剪贴板。hWndNewOwner 传窗口句柄时，该窗口成为剪贴板 owner；
    /// 传 IntPtr.Zero 表示不指定 owner（内容归属查不到来源）。
    ///
    /// 失败语义：剪贴板被别人占着时返回 false。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    /// <summary>关闭剪贴板。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    /// <summary>清空剪贴板（**不可撤销**：用户原来的内容就没了）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    /// <summary>取剪贴板里指定格式的数据句柄（HGLOBAL）；没有该格式返回 0。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    /// <summary>
    /// 把内存块交给剪贴板。成功后**所有权归系统**，调用方不能再 free。
    /// 失败返回 0（此时内存仍归调用方，必须自己 GlobalFree）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    /// <summary>
    /// 剪贴板变更序号。任何一次改动（哪怕内容相同）都会让它递增。
    /// 用它判断"剪贴板现在还是我写的那份吗"——避免覆盖用户新复制的内容。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    /// <summary>取剪贴板 owner 窗口句柄（没有 owner 时返回 0）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardOwner();

    /// <summary>分配全局内存（SetClipboardData 需要可移动内存）。失败返回 0。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    /// <summary>锁定全局内存，取得可写指针。失败返回 0。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    /// <summary>解锁全局内存。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    /// <summary>释放全局内存（仅在所有权还在自己手里时调用）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>取最近一次 Win32 错误的代码（GetLastError）。</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetLastError();

    /// <summary>ERROR_CLASS_ALREADY_EXISTS (1410)：RegisterClass 时"类已存在"（不算失败）。</summary>
    public const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    #endregion

    #region 键盘输入注入（K1：Injector）

    /// <summary>KEYEVENTF_KEYUP (0x0002)：这是一个"抬起"事件。</summary>
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>KEYEVENTF_UNICODE (0x0004)：按键事件带 Unicode 字符（wScan 有效，wVk 无效）。</summary>
    public const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>INPUT_KEYBOARD (1)：INPUT.type 表示这是键盘事件。</summary>
    public const uint INPUT_KEYBOARD = 1;

    /// <summary>VK_CONTROL (0x11)：Ctrl 键。</summary>
    public const ushort VK_CONTROL = 0x11;

    /// <summary>VK_SHIFT (0x10)：Shift 键。</summary>
    public const ushort VK_SHIFT = 0x10;

    /// <summary>VK_MENU (0x12)：Alt 键。</summary>
    public const ushort VK_MENU = 0x12;

    /// <summary>VK_V (0x56)：V 键。</summary>
    public const ushort VK_V = 0x56;

    /// <summary>VK_LWIN (0x5B)：左 Win 键。</summary>
    public const ushort VK_LWIN = 0x5B;

    /// <summary>VK_RWIN (0x5C)：右 Win 键。</summary>
    public const ushort VK_RWIN = 0x5C;

    /// <summary>KEYBDINPUT：一次键盘事件。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        /// <summary>虚拟键码（非 Unicode 事件时有效）。</summary>
        public ushort wVk;

        /// <summary>扫描码（Unicode 事件时是字符码）。</summary>
        public ushort wScan;

        /// <summary>事件标志（KEYEVENTF_KEYUP / KEYEVENTF_UNICODE）。</summary>
        public uint dwFlags;

        /// <summary>时间戳（0 = 由系统填）。</summary>
        public uint time;

        /// <summary>附加信息（0）。</summary>
        public UIntPtr dwExtraInfo;
    }

    /// <summary>MOUSEINPUT：一次鼠标事件（只为把 INPUT 的 union 撑到 Win32 要求的尺寸）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        /// <summary>X 坐标（绝对/相对，取决于 flags）。</summary>
        public int dx;

        /// <summary>Y 坐标。</summary>
        public int dy;

        /// <summary>滚轮/按键数据。</summary>
        public uint mouseData;

        /// <summary>事件标志。</summary>
        public uint dwFlags;

        /// <summary>时间戳。</summary>
        public uint time;

        /// <summary>附加信息。</summary>
        public UIntPtr dwExtraInfo;
    }

    /// <summary>HARDWAREINPUT：底层硬件事件（只为把 INPUT 的 union 撑到 Win32 要求的尺寸）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        /// <summary>消息。</summary>
        public uint uMsg;

        /// <summary>参数低位。</summary>
        public ushort wParamL;

        /// <summary>参数高位。</summary>
        public ushort wParamH;
    }

    /// <summary>
    /// INPUT 的 union 部分。
    ///
    /// ⚠ 尺寸必须和 Win32 一致（x64 上 union = 32 字节、整个 INPUT = 40 字节）——
    /// SendInput 的 cbSize 传错会直接失败。MOUSEINPUT 那一条就是为此保留的。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        /// <summary>键盘事件。</summary>
        [FieldOffset(0)]
        public KEYBDINPUT ki;

        /// <summary>鼠标事件。</summary>
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        /// <summary>硬件事件。</summary>
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    /// <summary>INPUT：一次输入事件（键盘 / 鼠标 / 硬件）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        /// <summary>事件类型（键盘用 INPUT_KEYBOARD）。</summary>
        public uint type;

        /// <summary>事件内容。</summary>
        public INPUTUNION u;
    }

    /// <summary>
    /// 投递输入事件，返回**实际投递**的个数。
    ///
    /// 用它而不是 keybd_event：后者投递成功与否完全不可知，被 UIPI 拦掉时
    /// 也照样"成功"，表现出来就是"按了热键没反应"。SendInput 只插入数组前缀，
    /// 靠它才能知道到底有没有发出去、发出去几个。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>取某个键的即时状态（0x8000 位表示"当前按着"）。</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    #endregion

    #region 焦点与窗口线程（K1：Injector 焦点还原）

    /// <summary>把指定窗口设为前台窗口。失败返回 false（Windows 的前台锁策略）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 把两个线程的输入队列连起来（AttachThreadInput）。
    ///
    /// 关键：不挂线程的话，SetForegroundWindow 在后台线程调用时经常失败。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>取当前线程 ID。</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>句柄是否还指着活窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>取窗口类名。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>把键盘焦点设给指定窗口（同一线程内才有效）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>取当前线程的键盘焦点窗口（同线程才有效）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    /// <summary>把窗口设为本线程的活动窗口（同线程才有效）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr hWnd);

    /// <summary>取本线程的活动窗口（同线程才有效）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetActiveWindow();

    /// <summary>Win32 MSG：线程消息队列里的一条消息。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        /// <summary>目标窗口句柄。</summary>
        public IntPtr hwnd;

        /// <summary>消息号。</summary>
        public uint message;

        /// <summary>消息参数。</summary>
        public IntPtr wParam;

        /// <summary>消息参数。</summary>
        public IntPtr lParam;

        /// <summary>入队时间。</summary>
        public uint time;

        /// <summary>鼠标位置 X。</summary>
        public int x;

        /// <summary>鼠标位置 Y。</summary>
        public int y;
    }

    /// <summary>PM_REMOVE (0x0001)：PeekMessage 取走消息（而不是只拷贝一份）。</summary>
    public const uint PM_REMOVE = 0x0001;

    /// <summary>WM_QUIT (0x0012)：结束线程消息循环。</summary>
    public const uint WM_QUIT = 0x0012;

    /// <summary>WM_NULL (0x0000)：空消息（菜单弹出后用来让菜单正确收起）。</summary>
    public const uint WM_NULL = 0x0000;

    /// <summary>结束当前线程的消息循环（投递 WM_QUIT）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostQuitMessage(int nExitCode);

    /// <summary>查看消息队列里有没有消息（轮询式消息循环用）。</summary>
    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    /// <summary>
    /// 取一条消息（**阻塞式**消息循环用）：队列为空时挂起等，直到有消息到达。
    ///
    /// <para>返回值：
    /// <list type="bullet">
    /// <item><b>&gt; 0</b>：取到一条消息，正常处理；</item>
    /// <item><b>0</b>：取到了 WM_QUIT —— 消息循环该结束了；</item>
    /// <item><b>-1</b>：出错（用 GetLastError 查原因），同样要退出循环，不能死循环。</item>
    /// </list></para>
    ///
    /// <para>⚠ 轮询式（PeekMessage）只适合"抽干队列就退出"的场合；
    /// 要让窗口消息常驻，必须用这个阻塞版本，否则线程一退出窗口就没了。</para>
    /// </summary>
    /// <param name="lpMsg">取到的消息。</param>
    /// <param name="hWnd">取哪个窗口的消息；传 IntPtr.Zero = 本线程所有窗口的消息。</param>
    /// <param name="wMsgFilterMin">消息号下界（含）；0 = 不设下界。</param>
    /// <param name="wMsgFilterMax">消息号上界（含）；0 = 不设上界。</param>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>把键盘消息翻译成字符消息。</summary>
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    /// <summary>把消息派发给窗口过程。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    /// <summary>往指定线程的消息队列投递消息（用来叫停别的线程的消息循环）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>取窗口标题长度（字符数）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);

    /// <summary>WM_GETTEXT (0x000D)：取控件文本（读回注入结果用）。</summary>
    public const uint WM_GETTEXT = 0x000D;

    /// <summary>WM_GETTEXTLENGTH (0x000E)：取控件文本长度。</summary>
    public const uint WM_GETTEXTLENGTH = 0x000E;

    #endregion

    #region 显示器与 DPI（K1：Monitors / Dpi）

    /// <summary>MDT_EFFECTIVE_DPI (0)：拿"有效 DPI"（含系统缩放与用户的缩放调整）。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>MONITORINFOEXW：比 MONITORINFO 多一个设备名（显示器的稳定标识）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        /// <summary>结构体大小（= sizeof(MONITORINFOEX)）。</summary>
        public int cbSize;

        /// <summary>整块屏幕矩形（物理像素）。</summary>
        public RECT rcMonitor;

        /// <summary>任务栏之外的可用区域（物理像素）。</summary>
        public RECT rcWork;

        /// <summary>标志（MONITORINFOF_PRIMARY 等）。</summary>
        public uint dwFlags;

        /// <summary>设备名（如 \\.\DISPLAY1）。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    /// <summary>枚举显示器的回调（返回 false 停止枚举）。</summary>
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr lParam);

    /// <summary>逐个枚举显示器。</summary>
    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>取显示器信息（工作区、整屏矩形、设备名）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    /// <summary>取包含该屏幕点的显示器。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>取系统 DPI（拿不到显示器 DPI 时的兜底）。返回 0 表示取不到。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    /// <summary>SM_CXSCREEN (0)：屏幕宽度（像素）。</summary>
    public const int SM_CXSCREEN = 0;

    /// <summary>SM_CYSCREEN (1)：屏幕高度（像素）。</summary>
    public const int SM_CYSCREEN = 1;

    /// <summary>
    /// 取系统度量（像素）。
    ///
    /// <para>⚠ DPI aware 进程里返回的是**物理像素**（主显示器的大小）。</para>
    /// </summary>
    /// <param name="nIndex">度量索引（见 <see cref="SM_CXSCREEN"/> / <see cref="SM_CYSCREEN"/>）。</param>
    /// <returns>度量值（像素）。</returns>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// 取显示器的有效 DPI（shcore.dll，Win8.1+）。
    ///
    /// ⚠ 不用 GetDeviceCaps(LOGPIXELSX)：那是"调用线程的 DPI 上下文"，
    /// 进程声明 Per-Monitor V2 之后不同线程拿到的结果不一样（实测主线程 96、
    /// 探测线程 168），位置会算飞。这里给的是显示器本身的缩放，与线程无关。
    /// </summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (-4)：Win10 1703+ 的 Per-Monitor V2 感知。</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>声明进程 Per-Monitor V2 DPI 感知。失败返回 false。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>声明进程 Per-Monitor DPI 感知（shcore.dll）。返回 0 表示成功。</summary>
    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwareness(int awareness);

    /// <summary>声明进程 System DPI 感知（仅 Vista+ 的老接口）。</summary>
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    #endregion

    #region 托盘图标（Shell_NotifyIcon）

    /// <summary>WM_RBUTTONUP (0x0205)：鼠标右键在托盘图标上抬起。</summary>
    public const uint WM_RBUTTONUP = 0x0205;

    /// <summary>WM_LBUTTONDBLCLK (0x0203)：鼠标左键在托盘图标上双击。</summary>
    public const uint WM_LBUTTONDBLCLK = 0x0203;

    /// <summary>WM_CONTEXTMENU (0x007B)：弹出上下文菜单（v4 约定下由它触发托盘右键）。</summary>
    public const uint WM_CONTEXTMENU = 0x007B;

    /// <summary>WM_APP (0x8000)：应用自定义消息的起点。</summary>
    public const uint WM_APP = 0x8000;

    /// <summary>NIM_ADD (0)：把图标加进托盘。</summary>
    public const int NIM_ADD = 0;

    /// <summary>NIM_MODIFY (1)：改托盘图标（换图标 / 换提示文字）。</summary>
    public const int NIM_MODIFY = 1;

    /// <summary>NIM_DELETE (2)：把图标从托盘里删掉。**退出前必须调**，否则留死图标。</summary>
    public const int NIM_DELETE = 2;

    /// <summary>NIM_SETVERSION (4)：告诉 shell 用哪一版回调约定（v4 = WM_CONTEXTMENU）。</summary>
    public const int NIM_SETVERSION = 4;

    /// <summary>NIF_MESSAGE (0x1)：<c>uCallbackMessage</c> 有效。</summary>
    public const int NIF_MESSAGE = 0x00000001;

    /// <summary>NIF_ICON (0x2)：<c>hIcon</c> 有效。</summary>
    public const int NIF_ICON = 0x00000002;

    /// <summary>NIF_TIP (0x4)：<c>szTip</c> 有效。</summary>
    public const int NIF_TIP = 0x00000004;

    /// <summary>NOTIFYICONDATAW：托盘图标的完整描述。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        /// <summary>结构体大小（= sizeof(NOTIFYICONDATAW)）。</summary>
        public int cbSize;

        /// <summary>接收托盘回调消息的窗口句柄。</summary>
        public IntPtr hWnd;

        /// <summary>图标 id（同一个窗口里可以有多枚图标，用它区分）。</summary>
        public uint uID;

        /// <summary>哪些字段有效（NIF_* 的按位或）。</summary>
        public uint uFlags;

        /// <summary>用户操作图标时投递的消息（一般是 WM_RBUTTONUP / WM_LBUTTONDBLCLK）。</summary>
        public uint uCallbackMessage;

        /// <summary>图标句柄。</summary>
        public IntPtr hIcon;

        /// <summary>鼠标悬停时的提示文字。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        /// <summary>图标状态（NIS_*）。</summary>
        public uint dwState;

        /// <summary>状态掩码（哪些状态位由本图标控制）。</summary>
        public uint dwStateMask;

        /// <summary>气泡提示正文。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        /// <summary>气泡超时（毫秒）或版本号（配合 NIM_SETVERSION）。</summary>
        public uint uTimeoutOrVersion;

        /// <summary>气泡标题。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        /// <summary>气泡标志（NIIF_*）。</summary>
        public uint dwInfoFlags;

        /// <summary>图标 guid（v4+ 用；不用就填零）。</summary>
        public Guid guidItem;

        /// <summary>气泡里用的小图标。</summary>
        public IntPtr hBalloonIcon;
    }

    /// <summary>
    /// 增删改托盘图标。
    /// 失败返回 false（用 <c>Marshal.GetLastWin32Error</c> 判错）。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    #endregion

    #region 弹出菜单（托盘右键菜单）

    /// <summary>MF_STRING (0x0000)：普通菜单项（文字）。</summary>
    public const uint MF_STRING = 0x00000000;

    /// <summary>MF_SEPARATOR (0x0800)：分隔线。</summary>
    public const uint MF_SEPARATOR = 0x00000800;

    /// <summary>TPM_RIGHTBUTTON (0x0002)：右键也能选中菜单项（不区分左右键）。</summary>
    public const uint TPM_RIGHTBUTTON = 0x0002;

    /// <summary>TPM_RETURNCMD (0x0100)：<c>TrackPopupMenu</c> 直接返回被选中的命令 id（不发 WM_COMMAND）。</summary>
    public const uint TPM_RETURNCMD = 0x0100;

    /// <summary>创建一个弹出菜单（还没显示）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    /// <summary>往菜单里加一项。文本用 <paramref name="lpNewItem"/>。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    /// <summary>
    /// 显示弹出菜单并等用户选择。
    /// 返回被选中的命令 id（<c>TPM_RETURNCMD</c>）；用户取消返回 0。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr lpRect);

    /// <summary>销毁菜单。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    /// <summary>取鼠标在屏幕上的位置（物理像素）—— 弹出菜单要贴着鼠标。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    #endregion

    #region 图标绘制（GDI）

    /// <summary>NULL_BRUSH (4)：不填充（画空心图形）。</summary>
    public const int NULL_BRUSH = 4;

    /// <summary>TRANSPARENT (1)：背景不擦除（画线时用）。</summary>
    public const int TRANSPARENT = 1;

    /// <summary>PS_SOLID (0)：实线画笔。</summary>
    public const int PS_SOLID = 0;

    /// <summary>创建实心画刷（填充用）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateSolidBrush(uint color);

    /// <summary>创建画笔（描边用）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreatePen(int style, int width, uint color);

    /// <summary>取系统预置对象（NULL_BRUSH 等）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr GetStockObject(int i);

    /// <summary>设置背景模式（TRANSPARENT = 画线时不擦掉底下的东西）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int SetBkMode(IntPtr hdc, int mode);

    /// <summary>画椭圆（用当前画刷填充、当前画笔描边）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    /// <summary>画圆角矩形（用当前画刷填充、当前画笔描边）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    /// <summary>
    /// 画一组三次 Bezier 曲线（点数必须是 4 的倍数）。
    /// 比 <c>Arc</c> 好在**方向完全由控制点决定**，不用跟 GDI 的角度约定较劲。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool PolyBezier(IntPtr hdc, POINT[] points, int count);

    /// <summary>把画笔移到某个点（不画线）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lpPoint);

    /// <summary>从当前位置画一条直线到给定点。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool LineTo(IntPtr hdc, int x, int y);

    /// <summary>创建与指定 DC 兼容的位图。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    /// <summary>ICONINFO：图标的两张位图（单色掩码 + 彩色图）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        /// <summary>TRUE = 图标，FALSE = 光标。</summary>
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;

        /// <summary>热点的 X（光标用）。</summary>
        public int xHotspot;

        /// <summary>热点的 Y（光标用）。</summary>
        public int yHotspot;

        /// <summary>单色掩码位图。</summary>
        public IntPtr hbmMask;

        /// <summary>彩色位图。</summary>
        public IntPtr hbmColor;
    }

    /// <summary>由两张位图造出一个图标。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    /// <summary>销毁图标（<c>CreateIconIndirect</c> 造出来的必须销毁）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    #endregion

    #region 系统动画开关（prefers-reduced-motion）

    /// <summary>
    /// SPI_GETCLIENTAREAANIMATION (0x1042)：取"客户区动画"开关（"在显示中显示动画"）。
    /// 用户在"轻松使用 → 动画效果"里关掉时返回 FALSE。
    /// </summary>
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    /// <summary>取布尔型系统参数（<c>pvParam</c> 当作 BOOL 用）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfoBool(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    #endregion
}
