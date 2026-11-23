using System;
using System.Threading;
using VmNext.Core.Interop;
using VmNext.Core.Inject;
using VmNext.Shell;
using Xunit;

namespace VmNext.Core.Tests;

/// <summary>
/// 注入的集成测试：启动记事本 → 注入文本 → 用 WM_GETTEXT 读回验证。
///
/// 不截图、不看屏幕 —— 全部靠 API 读数。
/// </summary>
[Collection("interop")]
public class InjectorIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public InjectorIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Integration")]
    public void 注入的文本能被记事本读回()
    {
        using IInjectTarget target = InjectTargetFactory.Create();
        IntPtr hwnd = target.Hwnd;

        try
        {
            // 用户正按着修饰键时，注入会等 2.5 秒然后放弃自动粘贴 —— 先等它松开
            if (!ModifierWaiter.WaitForRelease(TimeSpan.FromSeconds(10)))
            {
                Assert.Skip("修饰键一直被按住（等了 10 秒），跳过（避免干扰用户输入）");
            }

            // 归还焦点用 AttachThreadInput 那套（Windows 前台锁会让裸 SetForegroundWindow 失败）
            bool restored = Core.Inject.Injector.RestoreForeground(hwnd);
            Thread.Sleep(400);

            _output.WriteLine($"RestoreForeground = {restored}");
            _output.WriteLine($"前台窗口 = {NativeMethods.GetForegroundWindow():X}（目标 = {hwnd:X}）");

            // ⚠ 前台必须确实是我们的目标窗口 —— 否则 Ctrl+V 会打进用户正在用的窗口。
            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                Assert.Skip("目标窗口没能拿到前台焦点（跳过，避免把文字注入到用户窗口）");
            }

            using var service = new ClipboardService();
            var injector = new Injector(service);

            const string text = "测试文本";
            InjectResult result = injector.PasteText(text, hwnd, restore: true, trailingSpace: false);
            Assert.True(result.Ok, $"注入失败: {result.Reason}");

            // 等目标应用把粘贴消费掉
            Thread.Sleep(600);

            string? content = target.ReadText();
            _output.WriteLine($"注入目标应用 = {target.Name}");
            _output.WriteLine($"读回内容 = {content ?? "(读不到控件)"}");
            Console.WriteLine($"[注入验证] 目标={target.Name} 读回内容={content}");

            if (target is TestEditHost host)
            {
                _output.WriteLine($"宿主线程收到 WM_KEYDOWN = {host.KeyDownCount}，WM_PASTE = {host.PasteCount}，编辑控件有焦点 = {host.EditHasFocus}");
            }

            Assert.Contains(text, content);
        }
        finally
        {
            target.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void 空文本直接判失败不碰剪贴板()
    {
        using var service = new ClipboardService();
        var injector = new Injector(service);

        InjectResult result = injector.PasteText(string.Empty, IntPtr.Zero, restore: true);

        Assert.False(result.Ok);
        Assert.Equal(Injector.ReasonEmptyText, result.Reason);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void 失败通知文案按实况分类()
    {
        // 没送达但剪贴板里有我们的文本 → warning + "按 Ctrl+V 即可"
        Notification? withText = Injector.BuildFailureNotification(
            "识别文本", new InjectResult(false, Injector.ReasonKeysNotSent, TextInClipboard: true));
        Assert.NotNull(withText);
        Assert.Equal("warning", withText!.Level);
        Assert.Equal("inject_failed", withText.Kind);
        Assert.Contains("Ctrl+V", withText.Message);

        // 修饰键没松开 → warning + 另一句文案
        Notification? modTimeout = Injector.BuildFailureNotification(
            "识别文本", new InjectResult(false, Injector.ReasonModifierTimeout, TextInClipboard: true));
        Assert.NotNull(modTimeout);
        Assert.Equal("warning", modTimeout!.Level);
        Assert.Contains("修饰键", modTimeout.Message);

        // 剪贴板里没有我们的文本 → error
        Notification? noText = Injector.BuildFailureNotification(
            "识别文本", new InjectResult(false, Injector.ReasonClipboardWriteFailed, TextInClipboard: false));
        Assert.NotNull(noText);
        Assert.Equal("error", noText!.Level);
        Assert.Contains("复制到剪贴板失败", noText.Message);

        // 成功时没有失败通知
        Assert.Null(Injector.BuildFailureNotification("识别文本", new InjectResult(true, string.Empty, true)));
    }
}
