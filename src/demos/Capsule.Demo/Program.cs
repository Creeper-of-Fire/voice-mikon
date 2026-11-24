using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using VoiceMikonCSharp.Core.Presentation;
using VoiceMikonCSharp.Shell;
using VoiceMikonCSharp.Ui;

namespace Capsule.Demo;

/// <summary>
/// Demo 的进程入口：Avalonia 启动 + 全局热键。
///
/// <para>为什么必须用全局热键：覆盖层窗口带 <c>WS_EX_NOACTIVATE</c>，永远拿不到键盘焦点，
/// <c>KeyDown</c> 根本不会触发（archive 意外发现 #5）。真机 voice-mikon 同样靠全局热键驱动。</para>
///
/// <para>这是**离线视觉预览**：热键直接推 mode，不接 controller。
/// 走的是正式版同一条入口（<see cref="CapsuleWindow.SetMode"/>），
/// 所以这里看到的形态与正式版一致。</para>
/// </summary>
internal static class Program
{
    /// <summary>程序入口。</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        TrySetUtf8Console();

        // ⚠ DPI 感知必须在**任何窗口创建之前**声明（voice-mikon 的 win32/dpi.py 同款规矩）。
        // 不声明的话 Avalonia 建窗时拿到的缩放不对，窗口尺寸与内容渲染会对不上。
        string dpiMode = VoiceMikonCSharp.Core.Interop.Dpi.SetDpiAwareness();
        Console.WriteLine($"DPI 感知: {dpiMode}");

        PrintBanner();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("启动失败：" + ex);
            return 1;
        }
    }

    /// <summary>构建 Avalonia 应用。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>控制台按 UTF-8 输出，避免中文变成乱码。</summary>
    private static void TrySetUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 某些宿主环境不支持设置编码，忽略
        }
    }

    private static void PrintBanner()
    {
        Console.WriteLine("=== voice-mikon 胶囊 UI 的 Avalonia 翻译（Capsule.Demo）===");
        Console.WriteLine("对照物：正在运行的 voice-mikon（原实现是 WebView2 + HTML/CSS）。");
        Console.WriteLine();
        Console.WriteLine("全局热键（demo 运行期间被占用）：");
        Console.WriteLine("  Ctrl+Alt+Shift+1  收起的悬浮球（orb 形态）");
        Console.WriteLine("  Ctrl+Alt+Shift+2  录音中（展开成胶囊 + 波形 + 计时）");
        Console.WriteLine("  Ctrl+Alt+Shift+3  识别中（转圈 + \"识别中…\"）");
        Console.WriteLine("  Ctrl+Alt+Shift+4  已完成（对勾 + \"已输入\"）");
        Console.WriteLine("  Ctrl+Alt+Shift+5  出错了（错误红）");
        Console.WriteLine("  Ctrl+Alt+Shift+6  没送出去（警告琥珀 + 可复制）");
        Console.WriteLine("  Ctrl+Alt+Shift+7  已取消（中性灰）");
        Console.WriteLine("  Ctrl+Alt+Shift+8  被拒（球上方冒一句提示）");
        Console.WriteLine("  Ctrl+Alt+Shift+T  主题循环：黑夜 → 白天 → 跟随系统");
        Console.WriteLine("  Ctrl+Alt+Shift+Q  退出");
        Console.WriteLine();
        Console.WriteLine("另外：球形态下**右键球**打开通用功能菜单；波形是模拟电平（正弦 + 噪声），");
        Console.WriteLine("      推进周期 140ms、电平 30fps，与真机一致。");
    }
}

/// <summary>
/// Demo 的全局热键。
///
/// <para>组合键一律带 Ctrl+Alt+Shift —— 免得和用户正在用的软件撞键。
/// 演示期间这些组合键会被占用，真机 voice-mikon 的默认热键不受影响。</para>
/// </summary>
internal static class DemoHotKeys
{
    private const int HotKeyOrb = 1;
    private const int HotKeyRecording = 2;
    private const int HotKeyProcessing = 3;
    private const int HotKeyDone = 4;
    private const int HotKeyError = 5;
    private const int HotKeyWarning = 6;
    private const int HotKeyCancelled = 7;
    private const int HotKeyRejected = 8;
    private const int HotKeyTheme = 9;
    private const int HotKeyQuit = 10;

    private static HotKeyWindow? _hotKeys;
    private static DispatcherTimer? _levelTimer;
    private static readonly SimulatedLevels _levels = new();
    private static double _elapsed;

    /// <summary>注册热键并挂到窗口上。</summary>
    public static void Attach(CapsuleWindow capsule)
    {
        _hotKeys = new HotKeyWindow();
        _hotKeys.HotKeyPressed += (_, id) => OnHotKey(capsule, id);

        uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT
                        | NativeMethods.MOD_NOREPEAT;

        // 0x31..0x38 = '1'..'8'；0x54 = 'T'；0x51 = 'Q'
        _hotKeys.Register(HotKeyOrb, modifiers, 0x31);
        _hotKeys.Register(HotKeyRecording, modifiers, 0x32);
        _hotKeys.Register(HotKeyProcessing, modifiers, 0x33);
        _hotKeys.Register(HotKeyDone, modifiers, 0x34);
        _hotKeys.Register(HotKeyError, modifiers, 0x35);
        _hotKeys.Register(HotKeyWarning, modifiers, 0x36);
        _hotKeys.Register(HotKeyCancelled, modifiers, 0x37);
        _hotKeys.Register(HotKeyRejected, modifiers, 0x38);
        _hotKeys.Register(HotKeyTheme, modifiers, 0x54);
        _hotKeys.Register(HotKeyQuit, modifiers, 0x51);

        // 电平泵：30fps 推一帧（真机是 LevelPump 在推）。
        _levelTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / WaveformData.LevelFps),
        };
        _levelTimer.Tick += (_, _) =>
        {
            _elapsed += _levelTimer!.Interval.TotalSeconds;
            var levels = new List<double> { _levels.LevelAt(_elapsed) };
            capsule.PushLevels(levels, _elapsed);
        };
        _levelTimer.Start();
    }

    /// <summary>注销热键（退出前必须调用，否则热键一直被占用）。</summary>
    public static void Detach()
    {
        _levelTimer?.Stop();
        _hotKeys?.Dispose();
    }

    private static void OnHotKey(CapsuleWindow capsule, int id)
    {
        switch (id)
        {
            case HotKeyOrb:
                // 球形态：胶囊收成 40px 的正圆，底色是录音红。
                capsule.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
                break;

            case HotKeyRecording:
                capsule.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
                break;

            case HotKeyProcessing:
                capsule.SetMode(
                    DisplayModes.Processing,
                    new DisplayPayload(Message: "识别中…"));
                break;

            case HotKeyDone:
                capsule.SetMode(
                    DisplayModes.Result,
                    new DisplayPayload(Message: "已输入", Text: "这是识别出来的最终文字。"));
                break;

            case HotKeyError:
                capsule.SetMode(
                    DisplayModes.Error,
                    new DisplayPayload(Message: "没听清，请再说一遍。"));
                break;

            case HotKeyWarning:
                // 警告态：正文是识别出来的内容，报告说明"为什么没送出去"，
                // 两者之间有一道分隔线（CSS: .textbar.has-report）。
                capsule.SetMode(
                    DisplayModes.Error,
                    new DisplayPayload(
                        Message: "目标窗口没有接收文本，内容已留在剪贴板里。",
                        Text: "识别出来的文字。",
                        CanCopy: true));
                break;

            case HotKeyCancelled:
                capsule.SetMode(
                    DisplayModes.Cancelled,
                    new DisplayPayload(Message: "已取消"));
                break;

            case HotKeyRejected:
                capsule.SetMode(
                    DisplayModes.Rejected,
                    new DisplayPayload(Message: "模型加载中…"));
                break;

            case HotKeyTheme:
                CapsuleTheme.Apply(CapsuleTheme.CycleNext());
                break;

            case HotKeyQuit:
                capsule.Close();
                break;
        }
    }
}
