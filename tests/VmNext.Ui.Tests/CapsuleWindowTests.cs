using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using VmNext.Core.Presentation;
using Xunit;

namespace VmNext.Ui.Tests;

/// <summary>
/// 胶囊的显示状态测试：按 capsule.css 的状态表逐个推 mode，断言关键元素属性。
///
/// <para>每条断言旁边都注了 capsule.css 的行号 —— 那是**唯一的真相**，
/// 断言写错行号比不写更糟。</para>
///
/// <para>无头平台下没有真实的 HWND，<c>OverlayWindow</c> 的 Win32 部分
/// （SetWindowRgn / 焦点 / 置顶）不工作 —— 这些不在这里测（见
/// tests/VmNext.App.Tests 的 GUI 子进程那条）。这里测的是**纯视觉状态**：
/// 形状、显隐、配色。</para>
/// </summary>
public class CapsuleWindowTests
{
    /// <summary>
    /// 开一个窗口并等颜色补间走完（补间是 180ms 的动画，capsule.css 的
    /// <c>background-color .18s ease-out</c>）。
    /// </summary>
    public static async Task<CapsuleWindow> OpenWindowAsync()
    {
        var window = new CapsuleWindow();

        // 主题固定成"黑夜"（时长 0 = 立刻生效）：无头平台的系统主题变体不一定是深色，
        // 断言要对着确定的 palette 比。（放在建窗之后 —— 窗口构造时会先落一次 auto。）
        CapsuleTheme.Apply(CapsuleThemeMode.Dark, TimeSpan.Zero);

        window.Show();
        await Task.Delay(50);
        return window;
    }

    /// <summary>
    /// 等动画走完。
    ///
    /// ⚠ 要等两段：形状过渡 800ms（capsule.css 的 <c>max-width .8s</c>）+
    /// 颜色补间 180ms（<c>background-color .18s</c>）—— 只等颜色的话
    /// MaxWidth 还在补间中间，断言会读到 459.8 这种半截值。
    /// </summary>
    public static async Task SettleAsync()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < MorphDurationMs + ColorSettleMs)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>形状过渡时长（capsule.css L339 的 <c>max-width .8s</c>）。</summary>
    private const int MorphDurationMs = 800;

    /// <summary>颜色补间时长 + 余量（capsule.css L343 的 <c>.18s</c>）。</summary>
    private const int ColorSettleMs = 320;

    /// <summary>取控件背景的颜色。</summary>
    private static Color BackgroundOf(IBrush? brush)
    {
        Assert.NotNull(brush);
        var solid = Assert.IsType<SolidColorBrush>(brush);
        return solid.Color;
    }

    // ==================== orb（待机）====================

    /// <summary>
    /// orb（待机）：胶囊收成 40px 的正圆，底色是录音红，文字条整块淡出。
    /// </summary>
    [AvaloniaFact]
    public async Task orb_球形态_宽40_底色录音红_文字条隐藏()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
            await SettleAsync();

            // CSS L588-592: body.orb-mode.orb-idle .recording-pill { max-width:40px; background-color:var(--wave); }
            Assert.Equal(40, window.Pill.MaxWidth);
            Assert.Equal(CapsuleTheme.DarkPalette["--wave"], BackgroundOf(window.Pill.Background));

            // CSS L615-621: body.orb-mode.orb-idle .recording-pill > *:not(.bar-orb) { opacity:0 }
            // —— 球形态下胶囊里的控件是"透明度 0"，不是 display:none（CSS 没写 display）。
            Assert.Equal(0, window.BarContent.Opacity);
            Assert.Equal(0, window.StopButton.Opacity);

            // 文字条在球形态整块淡出，且不占布局（CSS L651-654）。
            Assert.False(window.TextBar.IsVisible);
            Assert.Equal(0, window.TextBar.Opacity);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== recording（录音中）====================

    /// <summary>
    /// recording：胶囊展开成 480 上限的胶囊，底色是深色卡片，波形 + 计时出来，
    /// 状态图标藏起来（右侧换成结束钮）。
    /// </summary>
    [AvaloniaFact]
    public async Task recording_展开_宽480_底色深色_波形与计时可见_状态图标隐藏()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
            await SettleAsync();

            // CSS L635-641: body.orb-mode.orb-expanded .recording-pill { max-width:480px; background-color:var(--deep); }
            Assert.Equal(480, window.Pill.MaxWidth);
            Assert.Equal(CapsuleTheme.DarkPalette["--deep"], BackgroundOf(window.Pill.Background));

            // CSS L511-514: .recording-pill.recording .waveform{display:block} .bar-timer{display:inline} .bar-stop{display:grid}
            Assert.True(window.Waveform.IsVisible);
            Assert.True(window.BarTimer.IsVisible);
            Assert.True(window.StopButton.IsVisible);

            // CSS L514: .recording-pill.recording .bar-state { display:none }
            Assert.False(window.StateIcon.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== processing（识别中）====================

    /// <summary>processing：状态图标（转圈）出来，状态文字是"识别中…"。</summary>
    [AvaloniaFact]
    public async Task processing_转圈图标可见_状态文字是识别中()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Processing, new DisplayPayload(Message: "识别中…"));
            await SettleAsync();

            // CSS L526-527: .recording-pill.processing .icon-loader { display:block }
            Assert.True(window.StateLoaderIcon.IsVisible);
            Assert.False(window.StateCheckIcon.IsVisible);
            Assert.False(window.StateInfoIcon.IsVisible);

            // CSS L521: 非录音态 → .bar-label { display:block }；文案见 capsule.js 的 PHASE_TEXT
            Assert.True(window.BarLabel.IsVisible);
            Assert.Equal("识别中…", window.BarLabel.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== result_terminal（结果）====================

    /// <summary>result_terminal：对勾出来，文案"已输入"，正文是识别出来的文字。</summary>
    [AvaloniaFact]
    public async Task result_对勾可见_正文是识别文字()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Result, new DisplayPayload(Message: "已输入", Text: "识别出来的文字"));
            await SettleAsync();

            // CSS L528: .recording-pill.done .icon-check { display:block }
            Assert.True(window.StateCheckIcon.IsVisible);

            // capsule.js 的 applyMode('result_terminal') → writeText(text, 'normal')
            Assert.Equal("识别出来的文字", window.TextBody.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== error_terminal（错误，无原文）====================

    /// <summary>
    /// error_terminal（**没有原文**）：胶囊与文字条都是错误红，复制钮**不出现**
    /// （CSS: 复制按钮只在"带原文"时亮）。
    /// </summary>
    [AvaloniaFact]
    public async Task error_无原文_胶囊与文字条都是错误红_复制钮不出现()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Error, new DisplayPayload(Message: "没听清，请再说一遍。"));
            await SettleAsync();

            // CSS L464-466: .recording-pill.error { background:var(--err-bg); border-color:var(--err-stroke); }
            Assert.Equal(CapsuleTheme.DarkPalette["--err-bg"], BackgroundOf(window.Pill.Background));

            // CSS L248-252: .textbar.is-error { background:var(--err-bg); ... }
            Assert.Equal(CapsuleTheme.DarkPalette["--err-bg"], BackgroundOf(window.TextBar.Background));

            // CSS L483-489: .recording-pill .bar-copy { display:none }（只有 .can-copy 才亮）
            Assert.False(window.CopyButton.IsVisible);

            // CSS L529: .recording-pill.error .icon-info { display:block }
            Assert.True(window.StateInfoIcon.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== error_terminal + has-text（带原文）====================

    /// <summary>
    /// error_terminal + has-text（**带原文**，注入失败那种）：整态是**警告**琥珀
    /// （内容拿到了，只是没送出去），复制钮亮出来，状态图标让位。
    /// </summary>
    [AvaloniaFact]
    public async Task error_带原文_胶囊与文字条都是警告琥珀_复制钮可见_状态图标让位()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Error, new DisplayPayload(
                Message: "目标窗口没有接收文本，内容已留在剪贴板里。",
                Text: "识别出来的文字。",
                CanCopy: true));
            await SettleAsync();

            // CSS L468-470: .recording-pill.warning { background:var(--warn-bg); border-color:var(--warn-stroke); }
            Assert.Equal(CapsuleTheme.DarkPalette["--warn-bg"], BackgroundOf(window.Pill.Background));

            // CSS L257-261: .textbar.is-warning { background:var(--warn-bg); ... }
            Assert.Equal(CapsuleTheme.DarkPalette["--warn-bg"], BackgroundOf(window.TextBar.Background));

            // CSS L484-489: .recording-pill.error.can-copy .bar-copy / .warning.can-copy .bar-copy { display:grid }
            Assert.True(window.CopyButton.IsVisible);

            // CSS L495-496: 有复制按钮时状态图标让位
            Assert.False(window.StateIcon.IsVisible);

            // CSS L271-281: .textbar.has-report .textbar-report { ... border-bottom:1px solid var(--warn-stroke); }
            Assert.True(window.TextReportFrame.IsVisible);
            Assert.Equal("目标窗口没有接收文本，内容已留在剪贴板里。", window.TextReport.Text);

            // 正文是识别出来的文字（capsule.js: writeText(text, 'warning', message)）
            Assert.Equal("识别出来的文字。", window.TextBody.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== cancelled_terminal（取消）====================

    /// <summary>cancelled_terminal：文字条文字是中性灰（--dim3）。</summary>
    [AvaloniaFact]
    public async Task cancelled_文字条中性灰()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Cancelled, new DisplayPayload(Message: "已取消"));
            await SettleAsync();

            // CSS L284-286: .textbar.is-cancelled { color:var(--dim3); }
            Assert.Equal(CapsuleTheme.DarkPalette["--dim3"], BackgroundOf(window.TextBody.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 空态 ====================

    /// <summary>
    /// 空态：没有内容时正文是占位文案"正在聆听…"，颜色是 --dim。
    /// CSS L223-225: .textbar.is-empty .textbar-text { color:var(--dim); }
    /// </summary>
    [AvaloniaFact]
    public async Task 空态_正文是占位文案_颜色是dim()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
            await SettleAsync();

            // capsule.js 的 PLACEHOLDER = '正在聆听…'
            Assert.Equal("正在聆听…", window.TextBody.Text);

            // CSS L223-225
            Assert.Equal(CapsuleTheme.DarkPalette["--dim"], BackgroundOf(window.TextBody.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== rejected_bounce（被拒）====================

    /// <summary>rejected_bounce：球上方冒一句提示（气泡可见，文案是拒绝原因）。</summary>
    [AvaloniaFact]
    public async Task rejected_球上方冒提示()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Rejected, new DisplayPayload(Message: "模型加载中…"));
            await SettleAsync();

            // CSS L785-803: .orb-hint（球上方的一句话气泡）
            Assert.True(window.OrbHint.IsVisible);
            Assert.Equal("模型加载中…", window.OrbHintText.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 预览文字 ====================

    /// <summary>预览文字：整段替换（不是追加）。</summary>
    [AvaloniaFact]
    public async Task 预览文字_整段替换()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
            window.SetPreviewText("第一句");
            window.SetPreviewText("第一句第二句");
            await SettleAsync();

            Assert.Equal("第一句第二句", window.TextBody.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 主题 ====================

    /// <summary>主题：切到浅色后，胶囊底色跟着换成浅色基准的 --deep。</summary>
    [AvaloniaFact]
    public async Task 主题_切浅色_派生色跟着换()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
            await SettleAsync();
            Assert.Equal(CapsuleTheme.DarkPalette["--deep"], BackgroundOf(window.Pill.Background));

            window.SetTheme("light");
            await SettleAsync();
            Assert.Equal(CapsuleTheme.LightPalette["--deep"], BackgroundOf(window.Pill.Background));

            window.SetTheme("dark");
            await SettleAsync();
            Assert.Equal(CapsuleTheme.DarkPalette["--deep"], BackgroundOf(window.Pill.Background));
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 界面形态 ====================

    /// <summary>
    /// 界面形态 resident：没有球 —— 胶囊保持展开态（非常驻模式没有 orb-idle 这一档）。
    /// CSS L633-642: 非常驻模式没有 body.orb-mode.*，胶囊一直是展开态。
    /// </summary>
    [AvaloniaFact]
    public async Task resident_没有球形态_胶囊保持展开()
    {
        var window = await OpenWindowAsync();
        try
        {
            window.SetUiMode("resident");
            window.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
            await SettleAsync();

            Assert.Equal(CapsuleForm.Expanded, window.Form);
            Assert.Equal(480, window.Pill.MaxWidth);
        }
        finally
        {
            window.Close();
        }
    }
}
