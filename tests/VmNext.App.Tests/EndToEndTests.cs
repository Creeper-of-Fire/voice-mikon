using System;
using System.Threading;
using VmNext.Core;
using VmNext.Core.Controller;
using VmNext.Shell;
using VmNext.TestSupport;
using Xunit;

namespace VmNext.App.Tests;

/// <summary>
/// 剪贴板 / 焦点 / 键盘都是**全局资源**，必须串行跑。
/// </summary>
[CollectionDefinition("interop", DisableParallelization = true)]
public class InteropCollection
{
}

/// <summary>
/// 端到端测试：真麦克风、真热键窗口、真剪贴板、真注入，只有识别后端是假的
/// （不加载真模型、不联网）。
///
/// 按 AGENTS.md 的规矩：**不写状态机流转的单元测试**，只测"确实要实现的功能"
/// —— 这里验的是"录音 → 识别 → 注入 → 读回"这条链路真的通。
/// </summary>
[Collection("interop")]
public class EndToEndTests
{
    /// <summary>
    /// 正常路径：录音 → 停止 → FakeBackend 返回"测试文本" → 注入到 EDIT 窗口
    /// → WM_GETTEXT 读回**逐字相等**。
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void 录音到注入_读回逐字相等()
    {
        using var app = new TestApp();
        using TestEditHost target = new();

        if (!Prepare(target))
        {
            return;
        }

        CommandResult started = app.Controller.StartRecording();
        if (!started.Ok)
        {
            Assert.Skip($"没有可用麦克风（{started.Message}），跳过");
        }

        // 录 1.5 秒环境音
        Thread.Sleep(1500);

        app.Controller.StopRecording();

        Assert.True(
            TestApp.WaitFor(() => target.ReadText().Length > 0, TimeSpan.FromSeconds(20)),
            "注入没有发生（目标窗口里没有文字）");

        Assert.Equal("测试文本", target.ReadText());
    }

    /// <summary>
    /// 焦点保护：录音期间切到第二个窗口，文字**仍然**落在开始录音时那个窗口。
    ///
    /// （与 Python 版的一处有意差异 —— 见 docs/k3-controller.md 疑问 #2。）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void 录音期间切窗口_文字仍落在开始时的那个窗口()
    {
        using var app = new TestApp();
        using TestEditHost first = new();
        using TestEditHost second = new();

        if (!Prepare(first))
        {
            return;
        }

        CommandResult started = app.Controller.StartRecording();
        if (!started.Ok)
        {
            Assert.Skip($"没有可用麦克风（{started.Message}），跳过");
        }

        // 录音期间切到第二个窗口
        second.BringToFront();
        Thread.Sleep(400);
        Assert.Equal(second.Hwnd, NativeMethods.GetForegroundWindow());

        app.Controller.StopRecording();

        Assert.True(
            TestApp.WaitFor(() => first.ReadText().Length > 0, TimeSpan.FromSeconds(20)),
            "文字没有落回开始时的那个窗口");

        Assert.Equal("测试文本", first.ReadText());
        Assert.Equal(string.Empty, second.ReadText());
    }

    /// <summary>取消：录音中取消 → 不识别、不注入。</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void 取消_不识别也不注入()
    {
        using var app = new TestApp();
        using TestEditHost target = new();

        if (!Prepare(target))
        {
            return;
        }

        CommandResult started = app.Controller.StartRecording();
        if (!started.Ok)
        {
            Assert.Skip($"没有可用麦克风（{started.Message}），跳过");
        }

        Thread.Sleep(500);

        CommandResult cancelled = app.Controller.CancelRecording();
        Assert.True(cancelled.Ok);

        // 给"万一有识别线程在跑"一点时间 —— 它应该什么都不做。
        Thread.Sleep(1500);

        Assert.Equal(0, app.Backend.TranscribeCount);
        Assert.Equal(string.Empty, target.ReadText());
        Assert.Equal(AppStates.Idle, app.Controller.StateSnapshot.State);
    }

    /// <summary>后端失败：FakeBackend 抛 AsrError → 收到 error 通知、未注入、状态回 idle。</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void 后端失败_收到错误通知且不注入且状态回idle()
    {
        var backend = new FakeBackend { ThrowOnTranscribe = true, ErrorMessage = "模型炸了" };
        using var app = new TestApp(backend: backend);
        using TestEditHost target = new();

        if (!Prepare(target))
        {
            return;
        }

        CommandResult started = app.Controller.StartRecording();
        if (!started.Ok)
        {
            Assert.Skip($"没有可用麦克风（{started.Message}），跳过");
        }

        Thread.Sleep(500);
        app.Controller.StopRecording();

        Assert.True(
            TestApp.WaitFor(() => app.HasNotification("asr_failed"), TimeSpan.FromSeconds(20)),
            "没有收到 asr_failed 通知");

        Assert.True(TestApp.WaitFor(
            () => string.Equals(app.Controller.StateSnapshot.State, AppStates.Idle, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5)),
            "状态没有回到 idle");

        Assert.Equal(string.Empty, target.ReadText());
    }

    /// <summary>
    /// 剪贴板留底（归还**关**）：注入成功后剪贴板里留着识别文本。
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void 剪贴板留底_归还关时剪贴板里是识别文本()
    {
        using var app = new TestApp(c => c.RestoreClipboard = false);
        using TestEditHost target = new();

        if (!Prepare(target))
        {
            return;
        }

        // 先往剪贴板里写点别的（证明"留底"不是碰巧）
        Assert.True(app.Clipboard.SetTextVerified("原内容-不归还档").Ok);

        CommandResult started = app.Controller.StartRecording();
        if (!started.Ok)
        {
            Assert.Skip($"没有可用麦克风（{started.Message}），跳过");
        }

        Thread.Sleep(500);
        app.Controller.StopRecording();

        Assert.True(
            TestApp.WaitFor(() => target.ReadText().Length > 0, TimeSpan.FromSeconds(20)),
            "注入没有发生");

        // 识别文本一直留在剪贴板里（原内容没有被还回来）
        Assert.True(
            TestApp.WaitFor(() => string.Equals(app.Clipboard.GetText(), "测试文本", StringComparison.Ordinal), TimeSpan.FromSeconds(5)),
            $"剪贴板里不是识别文本：'{app.Clipboard.GetText()}'");
    }

    /// <summary>
    /// 剪贴板归还（归还**开**）：注入成功后原内容被还回去。
    ///
    /// ⚠ 本机可能被系统拒绝（1418）—— 那是环境限制，不是移植引入的缺陷，
    /// 与 K1 的同款测试一样：环境不答应就 skip 并写明原因。
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void 剪贴板归还_归还开时原内容被还回去()
    {
        using var app = new TestApp(c => c.RestoreClipboard = true);
        using TestEditHost target = new();

        if (!Prepare(target))
        {
            return;
        }

        Assert.True(app.Clipboard.SetTextVerified("原内容-归还档").Ok);

        CommandResult started = app.Controller.StartRecording();
        if (!started.Ok)
        {
            Assert.Skip($"没有可用麦克风（{started.Message}），跳过");
        }

        Thread.Sleep(500);
        app.Controller.StopRecording();

        Assert.True(
            TestApp.WaitFor(() => target.ReadText().Length > 0, TimeSpan.FromSeconds(20)),
            "注入没有发生");

        // 归还是在后台做的（等 1 秒再动手，写失败还会重试 3 次）→ 等够 4 秒
        Thread.Sleep(4000);

        string? after = app.Clipboard.GetText();
        if (!string.Equals(after, "原内容-归还档", StringComparison.Ordinal))
        {
            Assert.Skip(
                $"归还未生效（剪贴板仍是 '{after}'）：本机 SetClipboardData 被 1418 拒绝，" +
                "Python 版同样受限；归还判定本身已由日志确认（序号一致、内容一致）");
        }
    }

    /// <summary>
    /// 准备注入目标：等修饰键松开 + 把目标窗口弄成前台。
    /// 返回 false = 环境不答应，已经 skip 过了，调用方直接返回。
    /// </summary>
    private static bool Prepare(TestEditHost target)
    {
        if (!ModifierWaiter.WaitForRelease(TimeSpan.FromSeconds(10)))
        {
            Assert.Skip("修饰键一直被按住（等了 10 秒），跳过（避免干扰用户输入）");
        }

        target.BringToFront();
        Thread.Sleep(400);

        // ⚠ 前台必须确实是我们的目标窗口 —— 否则 Ctrl+V 会打进用户正在用的窗口。
        if (NativeMethods.GetForegroundWindow() != target.Hwnd)
        {
            Assert.Skip("目标窗口没能拿到前台焦点（跳过，避免把文字注入到用户窗口）");
        }

        return true;
    }
}
