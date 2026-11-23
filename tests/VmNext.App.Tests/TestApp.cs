using System;
using System.Collections.Generic;
using System.Threading;
using VmNext.Audio;
using VmNext.Core;
using VmNext.Core.Controller;
using VmNext.Core.Inject;
using VmNext.Core.Interop;
using VmNext.Core.Presentation;
using VmNext.Engines;

namespace VmNext.App.Tests;

/// <summary>
/// 端到端测试的装配台。
///
/// 装的**是真链**：真剪贴板（带 owner 窗口）、真注入器、真录音器（可选假）、
/// 真 presenter；只有识别后端是假的（不加载真模型、不联网）。
///
/// 装配顺序与 <c>src/VmNext.App/AppHost.cs</c> 同款（剪贴板 owner 在主线程
/// —— 这里"主线程"= 测试线程）。
/// </summary>
public sealed class TestApp : IDisposable
{
    private readonly List<Notification> _notifications = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>装配一条链。</summary>
    /// <param name="configure">改配置（热键、归还开关、预览开关……）。</param>
    /// <param name="backend">假后端（null = 新建一个）。</param>
    /// <param name="recorder">录音器（null = 真麦克风）。</param>
    public TestApp(
        Action<Config>? configure = null,
        FakeBackend? backend = null,
        IRecorder? recorder = null)
    {
        Backend = backend ?? new FakeBackend();

        Config = new Config
        {
            // 预览层会**额外**调一次 Transcribe（分句识别），会让"调用了几次"
            // 这种断言变得不确定。端到端要验的是**最终结果**，所以关掉预览。
            ShowPreview = false,

            // 末尾补空格会把读回的内容变成"测试文本 "，逐字相等就不成立了。
            TrailingSpace = false,

            // 不存录音：测试跑完不留垃圾在 data/recordings/。
            SaveRecordings = false,
        };

        configure?.Invoke(Config);

        // ---- 剪贴板 owner 窗口：**必须在当前线程创建** ----
        Clipboard = new ClipboardService();
        Clipboard.EnsureOwnerWindow();

        Injector = new Injector(Clipboard);

        Notifier = new NotificationHub(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vmnext-notify-{Guid.NewGuid():N}.log"));
        Notifier.Subscribe(note =>
        {
            lock (_lock)
            {
                _notifications.Add(note);
            }
        });

        Controller = new CoreController(
            Config,
            backendFactory: _ => Backend,
            recorderFactory: _ => recorder ?? new RecorderAdapter(new Recorder(null, Config.SampleRate)),
            injector: (text, hwnd, restore, trailingSpace) =>
                Injector.PasteText(text, hwnd, restore, trailingSpace),
            notifier: Notifier);

        Presenter = new VoiceBarPresenter(Controller, Config, Notifier);
        Presenter.Install();

        // 起引擎（后台线程加载"模型" —— 这里就是 FakeBackend，秒回）。
        // 不起的话控制器一直停在 starting，录音请求会被拒（"模型还在加载"）。
        Controller.Start();
        WaitFor(
            () => !string.Equals(Controller.StateSnapshot.State, AppStates.Starting, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>配置。</summary>
    public Config Config { get; }

    /// <summary>剪贴板服务。</summary>
    public ClipboardService Clipboard { get; }

    /// <summary>注入器。</summary>
    public Injector Injector { get; }

    /// <summary>通知中心（测试自己订阅了一份）。</summary>
    public NotificationHub Notifier { get; }

    /// <summary>控制器。</summary>
    public CoreController Controller { get; }

    /// <summary>presenter。</summary>
    public VoiceBarPresenter Presenter { get; }

    /// <summary>假后端。</summary>
    public FakeBackend Backend { get; }

    /// <summary>收到的通知（快照）。</summary>
    public IReadOnlyList<Notification> Notifications
    {
        get
        {
            lock (_lock)
            {
                return _notifications.ToArray();
            }
        }
    }

    /// <summary>有没有收到某个 kind 的通知。</summary>
    public bool HasNotification(string kind)
    {
        foreach (Notification note in Notifications)
        {
            if (string.Equals(note.Kind, kind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>等一个条件成立（默认最多 10 秒）。</summary>
    public static bool WaitFor(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);

        while (sw.Elapsed < limit)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return predicate();
    }

    /// <summary>释放（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Presenter.Shutdown();
        }
        catch (Exception)
        {
        }

        try
        {
            Controller.Shutdown();
        }
        catch (Exception)
        {
        }

        try
        {
            Clipboard.ReleaseOwnership();
        }
        catch (Exception)
        {
        }
    }
}
