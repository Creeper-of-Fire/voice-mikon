using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using VmNext.Shell;

namespace VmNext.Core.Controller;

/// <summary>热键相关错误（解析失败 / 热键线程没起来）。</summary>
public class HotkeyError : Exception
{
    public HotkeyError(string message) : base(message)
    {
    }

    public HotkeyError(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// 全局热键绑定：把 "ctrl+alt+space" 这样的字符串变成（修饰键 + 主键），
/// 并在**独立线程**上注册 / 注销。
///
/// 翻译自 voice-mikon/src/voice_mikon/host/hotkey.py（<c>parse_hotkey</c> +
/// <c>HotkeyManager</c>）。
///
/// 为什么必须独立线程：<c>RegisterHotKey</c> 把 WM_HOTKEY 投递给目标窗口，
/// 而窗口消息要**它自己线程的循环**去取。主线程被 UI 占着，
/// 热键就永远收不到（Python 版也是单独起了一条 "host-hotkey" 线程）。
///
/// 注册 / 注销都发生在这条线程上 —— <c>RegisterHotKey</c> 的 hWnd 必须是
/// 接收 WM_HOTKEY 的那个窗口，跨线程注册会让消息投递到错误的队列。
/// </summary>
public sealed class HotkeyBinding : IDisposable
{
    /// <summary>热键线程最多等它起来多久（秒）。</summary>
    public const double ReadyTimeoutSeconds = 5.0;

    /// <summary>命名主键表（对齐 Python 的 NAMED_KEYS）。</summary>
    public static readonly IReadOnlyDictionary<string, uint> NamedKeys =
        new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["space"] = 0x20,
            ["enter"] = 0x0D,
            ["return"] = 0x0D,
            ["tab"] = 0x09,
            ["esc"] = 0x1B,
            ["escape"] = 0x1B,
            ["backspace"] = 0x08,
            ["insert"] = 0x2D,
            ["delete"] = 0x2E,
            ["home"] = 0x24,
            ["end"] = 0x23,
            ["pageup"] = 0x21,
            ["pagedown"] = 0x22,
            ["up"] = 0x26,
            ["down"] = 0x28,
            ["left"] = 0x25,
            ["right"] = 0x27,
        };

    private static readonly Logger _log = Log.GetLogger("host.hotkey");

    private readonly object _lock = new();
    private readonly ConcurrentQueue<RegistrationRequest> _requests = new();
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly NotificationHub _notifier;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private uint _threadId;
    private HotKeyWindow? _window;
    private int _nextId = 1;
    private volatile bool _stop;
    private bool _disposed;

    public HotkeyBinding(NotificationHub? notifier = null)
    {
        _notifier = notifier ?? NotificationHub.Notify;
    }

    /// <summary>热键窗口句柄（0 = 还没起来）。测试用它 <c>PostMessage(WM_HOTKEY)</c> 模拟按键。</summary>
    public IntPtr WindowHandle => _window?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// 最近一次注册成功的热键 id（1 起）。
    ///
    /// 测试用它 <c>PostMessage(hwnd, WM_HOTKEY, id, 0)</c> 模拟一次按键 ——
    /// 系统投递 WM_HOTKEY 时 wParam 就是这个 id。
    /// </summary>
    public int LastRegisteredId { get; private set; }

    /// <summary>起热键线程（幂等）。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_thread is not null && _thread.IsAlive)
            {
                return;
            }

            _stop = false;
            _ready.Reset();
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "host-hotkey",
            };
            _thread.Start();
        }

        if (!_ready.Wait(TimeSpan.FromSeconds(ReadyTimeoutSeconds)))
        {
            throw new HotkeyError($"热键线程 {ReadyTimeoutSeconds:F0} 秒内没有起来");
        }
    }

    /// <summary>
    /// 注册一个全局热键。返回 false 表示解析失败或注册失败（原因已发通知）。
    /// </summary>
    public bool Register(string combo, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        uint modifiers;
        uint vk;
        try
        {
            (modifiers, vk) = Parse(combo);
        }
        catch (HotkeyError ex)
        {
            _log.Warning($"热键 '{combo}' 解析失败: {ex.Message}");
            Notify("warning", "hotkey_invalid", $"快捷键 {combo} 写错了,没注册上",
                new Dictionary<string, object?>
                {
                    ["combo"] = combo,
                    ["reason"] = ex.Message,
                });
            return false;
        }

        Thread? thread = _thread;
        if (thread is null || !thread.IsAlive)
        {
            _log.Warning("热键线程没起来,注册失败");
            Notify("warning", "hotkey_failed", $"快捷键 {combo} 没注册上(热键线程没起来)",
                new Dictionary<string, object?> { ["combo"] = combo });
            return false;
        }

        int id;
        lock (_lock)
        {
            id = _nextId++;
        }

        var request = new RegistrationRequest(id, modifiers, vk, callback);
        _requests.Enqueue(request);

        if (!request.Done.Wait(TimeSpan.FromSeconds(3)))
        {
            Notify("warning", "hotkey_failed", $"快捷键 {combo} 没注册上,可能已被其它程序占用",
                new Dictionary<string, object?> { ["combo"] = combo });
            return false;
        }

        if (!request.Ok)
        {
            Notify("warning", "hotkey_failed", $"快捷键 {combo} 没注册上,可能已被其它程序占用",
                new Dictionary<string, object?> { ["combo"] = combo });
            return false;
        }

        _log.Info($"热键已注册: {combo}");
        LastRegisteredId = id;
        return true;
    }

    /// <summary>注销所有热键（进程退出前必须做 —— 热键是全局资源）。</summary>
    public void UnregisterAll()
    {
        HotKeyWindow? window = _window;
        if (window is null)
        {
            return;
        }

        int[] ids;
        lock (_lock)
        {
            ids = new List<int>(_callbacks.Keys).ToArray();
            _callbacks.Clear();
        }

        foreach (int id in ids)
        {
            try
            {
                window.Unregister(id);
            }
            catch (Exception ex)
            {
                _log.Debug($"注销热键 {id} 异常: {ex.Message}");
            }
        }
    }

    /// <summary>停热键线程（幂等）。</summary>
    public void Stop()
    {
        UnregisterAll();

        _stop = true;

        Thread? thread = _thread;
        uint tid = _threadId;
        if (thread is not null && tid != 0)
        {
            NativeMethods.PostThreadMessage(tid, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(3));
            if (thread.IsAlive)
            {
                NativeMethods.PostThreadMessage(tid, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread.Join(TimeSpan.FromSeconds(1));
            }
        }

        lock (_lock)
        {
            _thread = null;
            _threadId = 0;
        }
    }

    /// <summary>释放（等价于 Stop）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _ready.Dispose();
    }

    // ---------- 解析 ----------

    /// <summary>
    /// 把热键字符串解析成（修饰键, 主键）。
    ///
    /// 对齐 Python 的 <c>parse_hotkey</c>：
    /// * 修饰键：ctrl / alt / shift / win（win 也认 super / cmd / meta）
    /// * 主键：命名键（space / enter / …）、功能键（f1..f24）、单个字符
    /// * 总是带上 MOD_NOREPEAT（长按不重复触发）
    /// </summary>
    public static (uint Modifiers, uint Vk) Parse(string spec)
    {
        uint modifiers = 0;
        uint? vk = null;

        foreach (string raw in (spec ?? string.Empty).ToLowerInvariant().Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            if (part == "ctrl")
            {
                modifiers |= NativeMethods.MOD_CONTROL;
            }
            else if (part == "alt")
            {
                modifiers |= NativeMethods.MOD_ALT;
            }
            else if (part == "shift")
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }
            else if (part is "win" or "super" or "cmd" or "meta")
            {
                modifiers |= NativeMethods.MOD_WIN;
            }
            else if (NamedKeys.TryGetValue(part, out uint named))
            {
                vk = named;
            }
            else if (part.Length > 1 && part[0] == 'f' && IsDigits(part.AsSpan(1)))
            {
                int n = int.Parse(part.AsSpan(1));
                if (n is >= 1 and <= 24)
                {
                    vk = (uint)(0x6F + n);
                }
            }
            else if (part.Length == 1)
            {
                // 单个字符：取**大写**的码位（Python 的 ord(part.upper())）。
                // 键位是按大写字母定义的（VK_A = 0x41），小写 'a' 那个码位
                // 根本不是 A 键。
                vk = char.ToUpperInvariant(part[0]);
            }
            else
            {
                throw new HotkeyError($"无法识别的热键片段: '{raw}'");
            }
        }

        if (vk is null)
        {
            throw new HotkeyError("热键缺少主键");
        }

        return (modifiers | NativeMethods.MOD_NOREPEAT, vk.Value);
    }

    private static bool IsDigits(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return span.Length > 0;
    }

    // ---------- 线程主体 ----------

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        var window = new HotKeyWindow();
        window.HotKeyPressed += OnHotKeyPressed;
        _window = window;

        _ready.Set();

        while (!_stop)
        {
            // 消息循环：没有它，WM_HOTKEY 进了队列没人取，热键永远不触发。
            while (NativeMethods.PeekMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                if (msg.message == NativeMethods.WM_QUIT)
                {
                    _stop = true;
                    break;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            // 处理注册请求（注册必须在窗口所属的线程上做）
            while (_requests.TryDequeue(out RegistrationRequest? request))
            {
                bool ok = false;
                try
                {
                    ok = window.Register(request.Id, request.Modifiers, request.Vk);
                }
                catch (Exception ex)
                {
                    _log.Warning($"热键注册失败(id={request.Id}): {ex.Message}");
                }

                if (ok)
                {
                    lock (_lock)
                    {
                        _callbacks[request.Id] = request.Callback;
                    }
                }

                request.Ok = ok;
                request.Done.Set();
            }

            Thread.Sleep(5);
        }

        // 线程退出前把热键都注销掉
        UnregisterAll();

        try
        {
            window.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug($"热键窗口收尾异常: {ex.Message}");
        }

        _window = null;
        _threadId = 0;
    }

    private void OnHotKeyPressed(object? sender, int id)
    {
        Action? callback;
        lock (_lock)
        {
            _callbacks.TryGetValue(id, out callback);
        }

        if (callback is null)
        {
            return;
        }

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            _log.Error("热键回调异常", ex);
            Notify("error", "hotkey_callback_failed", "快捷键触发的动作失败了",
                new Dictionary<string, object?> { ["reason"] = ex.Message });
        }
    }

    private void Notify(string level, string kind, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        try
        {
            _notifier.Publish(level, kind, message, fields);
        }
        catch (Exception ex)
        {
            _log.Debug($"发通知失败(kind={kind}): {ex.Message}");
        }
    }

    /// <summary>注册请求（在热键线程上执行 RegisterHotKey）。</summary>
    private sealed class RegistrationRequest
    {
        public RegistrationRequest(int id, uint modifiers, uint vk, Action callback)
        {
            Id = id;
            Modifiers = modifiers;
            Vk = vk;
            Callback = callback;
            Done = new ManualResetEventSlim(false);
        }

        public int Id { get; }

        public uint Modifiers { get; }

        public uint Vk { get; }

        public Action Callback { get; }

        public ManualResetEventSlim Done { get; }

        public bool Ok { get; set; }
    }
}
