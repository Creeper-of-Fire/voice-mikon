using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.Core.Interop;

/// <summary>
/// 一次剪贴板写入的结果。
///
/// 翻译自 voice-mikon/src/voice_mikon/win32/clipboard.py 的 ClipboardResult。
/// </summary>
public readonly record struct ClipboardResult(bool Ok, string Reason = ClipboardService.WriteReasonOk)
{
    /// <summary>成功（原因码是空串）。</summary>
    public static ClipboardResult Success => new(true, ClipboardService.WriteReasonOk);
}

/// <summary>
/// 剪贴板读写（Win32 API，不走任何剪贴板库）。
///
/// 翻译自 voice-mikon/src/voice_mikon/win32/clipboard.py。
///
/// ── 三条不能破的规矩（来自 voice-mikon/AGENTS.md + Python 源码的踩坑记录）──
///
/// 1. **写入必须带 owner**（本类的 message-only 窗口）。
///    OpenClipboard(NULL) 写入的话，第三方剪贴板管理器（Ditto 一类）查不到来源，
///    只能把识别文本标注成"用户正聚焦的那个应用" —— 带上我们自己的 owner 窗口，
///    来源标注才归位。别改回 OpenClipboard(0)。
///
/// 2. **owner 窗口必须活在长命线程上**。
///    Windows 规矩：owner 窗口被销毁时，系统连带清空剪贴板；而 owner 窗口属于
///    创建它的线程，线程一退出窗口就被系统销毁。生产环境里 owner 窗口固定在
///    **主线程**创建（进程活多久窗口就活多久）；别的线程只复用、不重建。
///
/// 3. **写入要读回校验**。
///    SetClipboardData 返回非零只说明"系统收下了这块内存"，不代表剪贴板里真的有
///    这份内容（被别人占着、写完立刻被覆盖、owner 窗口已失效都会让"写入成功"
///    变成一句空话）。实测症状：程序报"已复制成功"，剪贴板里什么都没有。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    /// <summary>成功（原因码是空串）。</summary>
    public const string WriteReasonOk = "";

    /// <summary>剪贴板被别人占着，开不了。</summary>
    public const string WriteReasonOpenFailed = "open_failed";

    /// <summary>SetClipboardData 没成功。</summary>
    public const string WriteReasonWriteFailed = "write_failed";

    /// <summary>写完读回来对不上。</summary>
    public const string WriteReasonVerifyFailed = "verify_failed";

    /// <summary>写失败时的重试次数（SetClipboardData 偶发 1418 时用）。</summary>
    public const int WriteRetryAttempts = 3;

    /// <summary>
    /// 打开剪贴板的重试节奏：等间隔 10 次 × 50ms，总窗口 ~0.5s。
    ///
    /// 剪贴板是全局资源，别的进程可能正占着它（OpenClipboard 会失败）。
    /// 提到 10 × 50ms 是因为第三方剪贴板管理器（Ditto 一类）稍微多占用一会儿，
    /// 写入就直接失败；而失败之后拿不出任何证据说明"到底写没写进去"。
    ///
    /// ⚠ 这个窗口只影响"打不开板"这一件事；"读失败"与"里面没有文本"的
    /// 区分语义一点没动（见 <see cref="ReadText"/>）。
    /// </summary>
    private static readonly int[] OpenRetryDelaysMs = { 50, 50, 50, 50, 50, 50, 50, 50, 50, 50 };

    private const string OwnerClassName = "VoiceMikonCSharpClipboardOwner";
    private const uint WM_DESTROY = 0x0002;

    /// <summary>HWND_MESSAGE (-3)：消息专用窗口的父窗口句柄（不在屏幕上显示）。</summary>
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WindowProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 委托必须保持静态引用，否则会被 GC 回收，窗口过程变成野指针。
    private static readonly WindowProcDelegate OwnerWndProc = OwnerWindowProcedure;

    private static readonly object OwnerGate = new();
    private static IntPtr _ownerHwnd;
    private static bool _ownerClassRegistered;
    private static bool _ownerDestroying;

    private static readonly Logger _log = Log.GetLogger("win32.clipboard");
    private readonly int _ownerThreadId;
    private bool _disposed;

    /// <summary>
    /// 创建一个剪贴板服务。
    ///
    /// ⚠ **必须在主线程创建**（生产环境：程序启动早期，和 Python 版
    /// <c>__main__.main</c> 里 <c>ensure_owner_window()</c> 一个位置）。
    /// owner 窗口属于创建它的线程，这个线程就是"owner 线程"。
    /// 其它线程调用 <see cref="EnsureOwnerWindow"/> 时**只复用**已有窗口，
    /// 不会重建（重建出来的窗口活不过调用线程）。
    /// </summary>
    public ClipboardService()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        RegisterCurrent(this);
    }

    /// <summary>owner 窗口句柄（0 = 还没有 / 建不出来）。</summary>
    public IntPtr OwnerWindow => EnsureOwnerWindow();

    /// <summary>
    /// 拿到剪贴板 owner 窗口句柄，失败返回 0（降级为无 owner）。
    ///
    /// 1. 已经有窗口且还活着（IsWindow）→ 直接复用；
    /// 2. 还没有 → **只在 owner 线程创建**；别的线程（录音线程、UI 线程池）
    ///    来了就返回 0（功能不受影响，只是第三方剪贴板管理器看不到来源）。
    /// </summary>
    public IntPtr EnsureOwnerWindow()
    {
        lock (OwnerGate)
        {
            if (_ownerHwnd != IntPtr.Zero)
            {
                // 句柄可能已经随着线程退出而失效 —— 死句柄传给 OpenClipboard
                // 会直接失败（实测：读都读不到剪贴板）。
                if (NativeMethods.IsWindow(_ownerHwnd))
                {
                    return _ownerHwnd;
                }

                _log.Debug("剪贴板 owner 窗口已失效,重建");
                _ownerHwnd = IntPtr.Zero;
            }

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                // 非 owner 线程不建：短命线程建的 owner 会在线程退出时带走
                // 剪贴板内容。等 owner 线程建好即可。
                return IntPtr.Zero;
            }

            try
            {
                EnsureOwnerClassRegistered();

                _ownerHwnd = NativeMethods.CreateWindowExW(
                    0,
                    OwnerClassName,
                    null,
                    0,
                    0, 0, 0, 0,
                    HWND_MESSAGE,
                    IntPtr.Zero,
                    NativeMethods.GetModuleHandle(null),
                    IntPtr.Zero);

                if (_ownerHwnd == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                _log.Debug($"剪贴板 owner 窗口已创建: 0x{_ownerHwnd.ToInt64():X}");
                return _ownerHwnd;
            }
            catch (Exception ex)
            {
                _log.Debug($"创建剪贴板 owner 窗口失败(降级为无 owner): {ex.Message}");
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 读剪贴板里的 Unicode 文本，返回 (ok, text)。
    ///
    /// **ok 说的是"读到了没有"，不是"有没有文本"** —— 这两件事必须分得清：
    ///
    /// * (true, null)  读成功了，剪贴板里**确实没有文本**
    ///   （空的，或者只有图片之类读不出文本的内容）
    /// * (false, null) **读失败**：打不开剪贴板（被别人占着）、锁内存失败、
    ///   或者中途抛了异常
    ///
    /// 为什么要分：注入逻辑要拿原内容来"用完还回去"。分不清的话，"读失败"会被
    /// 当成"原剪贴板是空的" → 归还那一步被跳过 → 用户原来的内容被 EmptyClipboard
    /// 清掉，**永远还不回来**。那是真丢数据。
    /// </summary>
    public (bool Ok, string? Text) ReadText()
    {
        try
        {
            if (!OpenClipboard())
            {
                return (false, null);
            }

            try
            {
                return ReadTextLocked();
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    /// <summary>读取剪贴板中的 Unicode 文本，没有文本时返回 null（读失败也返回 null，要区分请用 ReadText）。</summary>
    public string? GetText()
    {
        (bool ok, string? text) = ReadText();
        return ok ? text : null;
    }

    /// <summary>
    /// 剪贴板变更序号。任何一次剪贴板改动（哪怕内容相同）都会让它递增。
    /// 用它判断"剪贴板现在还是我写的那份吗" —— 这是避免覆盖用户新复制内容的关键。
    /// </summary>
    public uint GetSequenceNumber()
    {
        try
        {
            return NativeMethods.GetClipboardSequenceNumber();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 把文本写入剪贴板，并**读回校验**，返回带原因码的结果。
    ///
    /// 读回用的是 GetClipboardData，**不会**递增剪贴板变更序号 ——
    /// 注入层"这份剪贴板还是我写的那份吗"的序号判定不受影响。
    /// </summary>
    public ClipboardResult SetTextVerified(string text)
    {
        try
        {
            if (!OpenClipboard())
            {
                _log.Warning("剪贴板打不开(可能被别的程序占着),写入失败");
                return new ClipboardResult(false, WriteReasonOpenFailed);
            }

            bool written;
            try
            {
                written = WriteTextLocked(text);
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            for (int attempt = 1; attempt <= WriteRetryAttempts && !written; attempt++)
            {
                // 重试：SetClipboardData 偶尔会返回 1418（ERROR_NOT_ENOUGH_QUOTA）——
                // 剪贴板刚被别的消费者（粘贴方、剪贴板管理器、IME）动过的时候，
                // 旧数据的配额还没释放。这种失败是**瞬时**的：等一下、关板重开
                // 再写通常就成了。
                int wait = attempt * 500;
                _log.Debug($"写剪贴板失败,等 {wait}ms 后重试（第 {attempt} 次）");
                Thread.Sleep(wait);

                if (OpenClipboard())
                {
                    try
                    {
                        written = WriteTextLocked(text);
                    }
                    finally
                    {
                        NativeMethods.CloseClipboard();
                    }
                }
            }

            if (!written)
            {
                _log.Warning("写剪贴板失败(SetClipboardData 没成功)");
                return new ClipboardResult(false, WriteReasonWriteFailed);
            }

            // 读回校验：必须**关板再读** —— 开着板读回来的是自己刚写进内存的那一份，
            // 验的是内存不是剪贴板。
            (bool ok, string? current) = ReadText();
            if (!ok)
            {
                _log.Warning("剪贴板写入后读回失败,当作没写进去");
                return new ClipboardResult(false, WriteReasonVerifyFailed);
            }

            if (current != text)
            {
                _log.Warning($"剪贴板写入后读回不一致(写入 {text.Length} 字,读回 {current?.Length ?? 0} 字),当作没写进去");
                return new ClipboardResult(false, WriteReasonVerifyFailed);
            }

            return ClipboardResult.Success;
        }
        catch (Exception ex)
        {
            _log.Debug($"写剪贴板异常: {ex.Message}");
            return new ClipboardResult(false, WriteReasonWriteFailed);
        }
    }

    /// <summary>把文本写入剪贴板，成功返回 true（带读回校验）。</summary>
    public bool SetText(string text) => SetTextVerified(text).Ok;

    /// <summary>
    /// 退出前把剪贴板所有权还给系统（内容留着）。
    ///
    /// owner 窗口随进程退出被销毁时，Windows 会**连带清空剪贴板** ——
    /// 注入失败时"识别结果留在剪贴板"的承诺就废了。所以在退出前先转交所有权
    /// （owner 置 NULL），内容就不会跟着窗口陪葬。
    ///
    /// 转交完再**读回核对一次**：这里承诺的是"内容留着"，那就得确认它真的留着。
    /// 核对失败只记日志：这条路径跑在退出前，那时已经没有补救余地。
    ///
    /// 核对走的是 OpenClipboard(0)，**不碰 owner 窗口** —— 本方法可能正在
    /// owner 窗口的 WM_DESTROY 里跑，那时去重建 owner 窗口等于在窗口销毁回调里
    /// 造新窗口，自己给自己挖坑。
    /// </summary>
    public void ReleaseOwnership()
    {
        string? text = null;

        try
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return;
            }

            try
            {
                (bool ok, string? current) = ReadTextLocked();
                if (!ok || current is null)
                {
                    return;
                }

                text = current;

                // ⚠ 这里**不能**先 EmptyClipboard：写回一旦失败（SetClipboardData
                // 偶发 1418），用户剪贴板的内容就永远没了 ——
                // "内容留着"的承诺就废了。WriteTextLocked 内部本来就有
                // "先分配、后清板"的顺序（分配失败时板不动），直接调它就行。
                if (!WriteTextLocked(text))
                {
                    _log.Warning("剪贴板转交失败:写回文本没成功");
                    return;
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                _log.Warning("剪贴板转交后核对失败:剪贴板打不开");
                return;
            }

            string? after = null;
            try
            {
                (bool ok, string? current) = ReadTextLocked();
                if (ok)
                {
                    after = current;
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            if (after != text)
            {
                _log.Warning("剪贴板转交后读回不一致,内容可能没留住");
            }
        }
        catch (Exception)
        {
            // 核对失败没有补救余地,忽略
        }
    }

    /// <summary>释放 owner 窗口（退出前调用）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        ReleaseOwnership();

        lock (OwnerGate)
        {
            if (_ownerHwnd != IntPtr.Zero)
            {
                // 所有权已经转交,销毁窗口不会再连带清空剪贴板。
                _ownerDestroying = true;
                NativeMethods.DestroyWindow(_ownerHwnd);
                _ownerHwnd = IntPtr.Zero;
            }
        }
    }

    /// <summary>打开剪贴板，被占用时按节奏表重试。带 owner。</summary>
    private bool OpenClipboard()
    {
        IntPtr owner = EnsureOwnerWindow();

        foreach (int delay in OpenRetryDelaysMs)
        {
            if (NativeMethods.OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(delay);
        }

        // 节奏表走完还打不开才是真失败（最后一次 sleep 后再试一下，
        // 保证"尝试次数 = 间隔数 + 1"）。
        return NativeMethods.OpenClipboard(owner);
    }

    /// <summary>
    /// 剪贴板**已打开**的前提下读取文本。
    ///
    /// (true, null) = 读到了，但里面没有 CF_UNICODETEXT 格式；
    /// (false, null) = 读失败（锁内存失败）。
    /// </summary>
    private static (bool Ok, string? Text) ReadTextLocked()
    {
        IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            // 读到了，只是里面没有 CF_UNICODETEXT 格式
            return (true, null);
        }

        IntPtr ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return (false, null);
        }

        try
        {
            return (true, Marshal.PtrToStringUni(ptr));
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// 剪贴板**已打开**的前提下写入文本。
    ///
    /// 顺序是硬要求：**先分配，后清板**。
    ///
    /// 为什么不能反：EmptyClipboard 一执行，用户原来那份内容就**没了**
    /// （不可撤销）。而分配内存是会失败的 —— 内存紧张时 GlobalAlloc 直接返回 0。
    /// "先清板再分配"的写法在这个失败分支上就是"把用户剪贴板清空了，新内容却
    /// 没写进去"：用户按 Ctrl+V 贴出空，而且**再也找不回来**。
    ///
    /// SetClipboardData 本身**不**清掉板上的其它格式（只替换同格式），所以清板
    /// 这步不能省；但它必须发生在写入**之前**，否则新内容会被随后的 EmptyClipboard
    /// 一起清掉。
    /// </summary>
    private static bool WriteTextLocked(string text)
    {
        byte[] data = Encoding.Unicode.GetBytes(text + "\0");

        IntPtr handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero)
        {
            // 分配失败：**板还没动**，用户原来的内容完好无损
            _log.Debug("GlobalAlloc 失败(内存紧张),板未改动");
            return false;
        }

        IntPtr ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            _log.Debug($"GlobalLock 失败(err={NativeMethods.GetLastError()})");
            NativeMethods.GlobalFree(handle);
            return false;
        }

        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        // 内容已经在自己手里了，这才可以动板
        if (!NativeMethods.EmptyClipboard())
        {
            _log.Debug($"EmptyClipboard 失败(err={NativeMethods.GetLastError()})");
        }

        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, handle) == IntPtr.Zero)
        {
            _log.Debug($"SetClipboardData 失败(err={NativeMethods.GetLastError()}),释放内存");
            NativeMethods.GlobalFree(handle);
            return false;
        }

        // 所有权已交给系统，不能再 free
        return true;
    }

    private static void EnsureOwnerClassRegistered()
    {
        if (_ownerClassRegistered)
        {
            return;
        }

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(OwnerWndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = OwnerClassName,
        };

        // RegisterClassExW 返回的是原子值（ushort）：0 = 失败。
        if (NativeMethods.RegisterClassExW(ref wc) == 0)
        {
            // 已注册（别的线程抢了先）不算失败
            if (NativeMethods.GetLastError() != (uint)NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
            {
                return;
            }
        }

        _ownerClassRegistered = true;
    }

    /// <summary>owner 窗口的消息过程：只拦 WM_DESTROY，其余交给 DefWindowProc。</summary>
    private static IntPtr OwnerWindowProcedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY && !_ownerDestroying)
        {
            // 窗口销毁时 Windows 会连带清空剪贴板 —— 先把所有权转给 NULL，内容留住。
            try
            {
                ClipboardService? svc = _current;
                svc?.ReleaseOwnership();
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 当前实例（静态，供 owner 窗口过程回调用）。
    ///
    /// owner 窗口是进程级唯一的，所以这个回调也只有一个。
    /// </summary>
    private static ClipboardService? _current;

    /// <summary>把自己登记成当前 owner 窗口的宿主（构造时调用）。</summary>
    internal static void RegisterCurrent(ClipboardService service) => _current = service;
}
