using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using VmNext.Shell;

namespace VmNext.Core.Tests;

/// <summary>注入验证的目标窗口（编辑控件）。</summary>
internal interface IInjectTarget : IDisposable
{
    /// <summary>目标窗口名（用于日志）。</summary>
    string Name { get; }

    /// <summary>顶层窗口句柄。</summary>
    IntPtr Hwnd { get; }

    /// <summary>读回编辑控件的内容（WM_GETTEXT）。</summary>
    string ReadText();
}

/// <summary>
/// 注入验证目标工厂。
///
/// 优先用**真正的记事本**；拿不到时退回**自建的最小 Win32 编辑窗口**。
///
/// 为什么需要退回方案 —— Windows 11 的新记事本是 packaged 单实例应用：
/// * `notepad.exe` 只是"转发器"，真正的窗口不挂在 notepad.exe 进程上
///   （实测：新进程 MainWindowHandle 恒为 0）；
/// * 请求会被转给**已经开着**的实例，在用户正在编辑的文档里插标签。
///
/// 拿不到稳定句柄、还会动用户的窗口 —— 这两条都不可接受，所以：
/// * 用户**没有**开着的记事本时 → 用真记事本（按启动前后的进程差集找新窗口）；
/// * 否则 → 用自建的 EDIT 窗口（同样是真实 Win32 编辑控件，WM_GETTEXT 读得回，
///   但绝不碰用户已经打开的任何窗口）。
///
/// 写字板（write.exe）也考虑过：这台机器上 write.exe 启动即退出（WordPad 已被弃用）。
/// </summary>
internal static class InjectTargetFactory
{
    /// <summary>造一个注入验证目标。</summary>
    public static IInjectTarget Create()
    {
        // 强制指定目标（调试用）：VMNEXT_TEST_TARGET=notepad / edit
        string? forced = Environment.GetEnvironmentVariable("VMNEXT_TEST_TARGET");
        if (string.Equals(forced, "notepad", StringComparison.OrdinalIgnoreCase))
        {
            if (TryLaunchNotepad(out Process? np, out IntPtr npHwnd))
            {
                return new NotepadTarget(np, npHwnd);
            }
        }

        // 默认：自建的 Win32 编辑窗口。
        //
        // 为什么默认不是真记事本：新记事本是 packaged 应用，它的编辑区不是传统
        // Edit/RichEdit 控件，WM_GETTEXT 读不回内容（实测读回空串）；而写字板
        // 在这台机器上已被弃用（write.exe 启动即退出）。自建窗口同样是真实 Win32
        // 编辑控件，验证的是同一件事：Ctrl+V 有没有把文字送进目标窗口。
        return new TestEditHost();
    }

    /// <summary>
    /// 启动记事本，按**启动前后的进程差集**找新出现的、带主窗口的进程。
    ///
    /// 这样绝不会拿到用户已经开着的窗口。
    /// </summary>
    private static bool TryLaunchNotepad(out Process? process, out IntPtr hwnd)
    {
        process = null;
        hwnd = IntPtr.Zero;

        HashSet<int> before;
        try
        {
            before = Process.GetProcessesByName("notepad").Select(p => p.Id).ToHashSet();
        }
        catch (Exception)
        {
            before = new HashSet<int>();
        }

        try
        {
            var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            Process? starter = Process.Start(psi);

            for (int i = 0; i < 60; i++)
            {
                foreach (Process candidate in Process.GetProcessesByName("notepad"))
                {
                    if (before.Contains(candidate.Id))
                    {
                        // 用户已经开着的窗口 —— 绝不碰
                        continue;
                    }

                    try
                    {
                        candidate.Refresh();
                        if (candidate.MainWindowHandle != IntPtr.Zero)
                        {
                            process = candidate;
                            hwnd = candidate.MainWindowHandle;
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                        // 进程可能刚好退出
                    }
                }

                Thread.Sleep(250);
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>真正的记事本窗口（测试结束直接杀掉进程）。</summary>
internal sealed class NotepadTarget : IInjectTarget
{
    private readonly Process? _process;

    public NotepadTarget(Process? process, IntPtr hwnd)
    {
        _process = process;
        Hwnd = hwnd;
    }

    public string Name => "Notepad";

    public IntPtr Hwnd { get; }

    public string ReadText() => EditControlReader.Read(Hwnd) ?? string.Empty;

    public void Dispose()
    {
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // 关不掉就算了
        }
    }
}

/// <summary>
/// 自建的最小 Win32 编辑窗口（顶层窗口 + EDIT 子控件），跑在**独立线程 + 消息循环**上。
///
/// 用来在"拿不到真记事本"的环境里验证注入是否真的落地 —— 验证的仍然是
/// "SendInput 的 Ctrl+V 有没有把文字送进目标窗口的编辑控件"，读回仍然用 WM_GETTEXT。
///
/// ⚠ **为什么必须有自己的线程 + 消息循环**：
/// SendInput 把按键投进**前台线程**的消息队列，得有人取走并派发，控件才会收到
/// WM_KEYDOWN → WM_PASTE。窗口属于哪个线程，消息就得由那个线程的循环来取 ——
/// 主线程没有循环时，键事件会静静躺在队列里，粘贴永远不会发生。
///
/// 独立线程还更贴近真实场景：真实的目标是**别的应用**（别的进程、别的消息循环）。
/// </summary>
internal sealed class TestEditHost : IInjectTarget
{
    private const string ClassName = "VmNextTestEditHost";

    // WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | ES_MULTILINE | ES_AUTOVSCROLL | ES_AUTOHSCROLL
    private const uint EDIT_STYLE = 0x40000000 | 0x10000000 | 0x00010000 | 0x00800000 | 0x0004 | 0x0040 | 0x0080;

    private static bool _classRegistered;

    // 诊断：宿主线程收到的消息计数（keyDown > 0 说明按键真的到了）
    private static volatile int s_keyDownCount;
    private static volatile int s_pasteCount;
    private static volatile bool s_editHasFocus;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _quit;
    private IntPtr _hwnd;
    private IntPtr _editHwnd;

    public TestEditHost()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "vmnext-test-edit-host",
        };

        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public string Name => "TestEditHost";

    public IntPtr Hwnd => _hwnd;

    public IntPtr EditHwnd => _editHwnd;

    /// <summary>读编辑控件里的文本。</summary>
    public string ReadText()
    {
        int len = NativeMethods.GetWindowTextLengthW(_editHwnd);
        var buf = new StringBuilder(len + 1);
        NativeMethods.SendMessage(_editHwnd, NativeMethods.WM_GETTEXT, (IntPtr)buf.Capacity, buf);
        return buf.ToString();
    }

    /// <summary>诊断：宿主线程收到的按键数。</summary>
    public int KeyDownCount => s_keyDownCount;

    /// <summary>诊断：宿主线程收到的粘贴消息数。</summary>
    public int PasteCount => s_pasteCount;

    /// <summary>诊断：编辑控件当前是否有键盘焦点。</summary>
    public bool EditHasFocus => s_editHasFocus;

    public void Dispose()
    {
        _quit = true;
        NativeMethods.PostThreadMessage((uint)_thread.ManagedThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Run()
    {
        EnsureClassRegistered();

        _hwnd = NativeMethods.CreateWindowExW(
            0,
            ClassName,
            "VmNext 注入验证窗口",
            0x00CF0000,   // WS_OVERLAPPEDWINDOW
            100, 100, 480, 240,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        _editHwnd = NativeMethods.CreateWindowExW(
            0,
            "EDIT",
            string.Empty,
            EDIT_STYLE,
            0, 0, 460, 200,
            _hwnd,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.ShowWindow(_hwnd, 5); // SW_SHOW
        NativeMethods.SetActiveWindow(_hwnd);
        s_editHwnd = _editHwnd;
        NativeMethods.SetFocus(_editHwnd);

        _ready.Set();

        // 消息循环：没有它，SendInput 投进来的按键没人取。
        while (!_quit)
        {
            while (NativeMethods.PeekMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                if (msg.message == NativeMethods.WM_QUIT)
                {
                    _quit = true;
                    break;
                }

                if (msg.message == 0x0100) // WM_KEYDOWN
                {
                    s_keyDownCount++;
                }
                else if (msg.message == 0x0302) // WM_PASTE
                {
                    s_pasteCount++;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            s_editHasFocus = NativeMethods.GetFocus() == _editHwnd;

            // 焦点兜底：窗口被激活时，键盘焦点必须落在编辑控件上，
            // 否则 Ctrl+V 不会变成 WM_PASTE（按键到了，控件收不到）。
            if (!_quit && NativeMethods.GetActiveWindow() == _hwnd && NativeMethods.GetFocus() != _editHwnd)
            {
                NativeMethods.SetFocus(_editHwnd);
            }

            Thread.Sleep(10);
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered)
        {
            return;
        }

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = ClassName,
        };

        NativeMethods.RegisterClassExW(ref wc);
        _classRegistered = true;
    }

    private delegate IntPtr WindowProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>当前宿主的编辑控件句柄（窗口过程要靠它设置焦点）。</summary>
    private static IntPtr s_editHwnd;

    private static readonly WindowProcDelegate WindowProc = (h, m, w, l) =>
    {
        // WM_SETFOCUS：窗口拿到键盘焦点 —— 直接把焦点交给编辑控件，
        // 别让系统按 tab 顺序乱选（默认会选第一个 WS_TABSTOP 控件，但我们要确定就是它）。
        if (m == 0x0007)
        {
            NativeMethods.SetFocus(s_editHwnd);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(h, m, w, l);
    };
}

/// <summary>等修饰键松开（测试辅助）。</summary>
internal static class ModifierWaiter
{
    /// <summary>
    /// 等到没有修饰键按着就返回 true。
    ///
    /// 注入层自己的"等松键"策略最多等 2.5 秒；测试这边愿意等更久 ——
    /// 用户只是短暂按着 Ctrl 时，等一下就能测，不该直接放弃。
    /// </summary>
    public static bool WaitForRelease(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (VmNext.Core.Inject.Injector.HeldModifiers().Count == 0)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return VmNext.Core.Inject.Injector.HeldModifiers().Count == 0;
    }
}

/// <summary>用 WM_GETTEXT 读窗口里的编辑控件内容。</summary>
internal static class EditControlReader
{
    /// <summary>读第一个 Edit/RichEdit 控件的文本；找不到返回 null。</summary>
    public static string? Read(IntPtr mainHwnd)
    {
        var result = new StringBuilder();
        bool any = false;

        NativeMethods.EnumChildWindows(mainHwnd, (h, _) =>
        {
            var cls = new StringBuilder(256);
            NativeMethods.GetClassNameW(h, cls, 256);
            string name = cls.ToString();

            bool editable = name.Contains("Edit", StringComparison.OrdinalIgnoreCase);

            if (!editable)
            {
                return true;
            }

            int len = NativeMethods.GetWindowTextLengthW(h);
            var buf = new StringBuilder(len + 1);
            NativeMethods.SendMessage(h, NativeMethods.WM_GETTEXT, (IntPtr)buf.Capacity, buf);
            result.Append(buf.ToString());
            any = true;
            return false;
        }, IntPtr.Zero);

        return any ? result.ToString() : null;
    }
}
