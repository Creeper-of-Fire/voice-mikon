using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using VmNext.Core;
using VmNext.Core.Presentation;
using Xunit;

namespace VmNext.Ui.Tests;

/// <summary>
/// 面板的显示状态测试：推一份 <see cref="PanelSnapshot"/>，断言各控件的状态。
///
/// <para>每条断言旁边都注了 panel.css / panel.js 的行号 —— 那是**唯一的真相**，
/// 断言写错行号比不写更糟。</para>
///
/// <para>无头平台下没有真实的 HWND，窗口真机显示 / 拖拽 / 焦点行为不测
/// （那是用户真机验证的事）。这里测的是**纯视觉状态**：选中项、显隐、配色、文案。</para>
/// </summary>
public class PanelWindowTests
{
    /// <summary>开一个面板窗口（主题固定成黑夜，断言才对得上确定的 palette）。</summary>
    private static async Task<PanelWindow> OpenPanelAsync()
    {
        var window = new PanelWindow();

        // 主题固定成"黑夜"（时长 0 = 立刻生效）：无头平台的系统主题变体不一定是深色，
        // 断言要对着确定的 palette 比。（放在建窗之后 —— 窗口构造时会先落一次 auto。）
        PanelTheme.Apply(PanelThemeMode.Dark, TimeSpan.Zero);

        window.Show();
        await Task.Delay(50);
        return window;
    }

    /// <summary>取控件背景的颜色。</summary>
    private static Color BackgroundOf(IBrush? brush)
    {
        Assert.NotNull(brush);
        var solid = Assert.IsType<SolidColorBrush>(brush);
        return solid.Color;
    }

    /// <summary>取控件前景的颜色。</summary>
    private static Color ForegroundOf(IBrush? brush)
    {
        Assert.NotNull(brush);
        var solid = Assert.IsType<SolidColorBrush>(brush);
        return solid.Color;
    }

    /// <summary>造一份快照（只填关心的字段）。</summary>
    private static PanelSnapshot Snapshot(
        string state = AppStates.Idle,
        string backend = "local",
        string model = "0.6B",
        string health = HealthStatuses.Loading,
        string reason = "",
        string theme = "auto",
        string uiMode = "orb",
        bool preview = true,
        bool restoreClipboard = true,
        string hotkey = "ctrl+alt+space",
        string notice = "",
        string noticeLevel = "info") => new()
        {
            State = state,
            Backend = backend,
            Model = model,
            NetworkModel = backend == "minimax" ? model : string.Empty,
            HealthStatus = health,
            HealthReason = reason,
            Theme = theme,
            UiMode = uiMode,
            PreviewEnabled = preview,
            RestoreClipboardEnabled = restoreClipboard,
            Hotkey = hotkey,
            Notice = notice,
            NoticeLevel = noticeLevel,
        };

    // ==================== 后端分段 ====================

    /// <summary>
    /// 后端分段：本地后端时选中"本地"。
    /// CSS L306-313 `.seg` + L337-342 `.seg-item.is-active`；
    /// panel.js L288-308 <c>applyBackend</c> 按 kind 高亮对应项。
    /// </summary>
    [AvaloniaFact]
    public async Task 后端_本地_选中本地项()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(backend: "local"));
            await Task.Delay(20);

            Assert.True(window.BackendLocalItem.IsChecked == true);
            Assert.False(window.BackendMinimaxItem.IsChecked == true);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>后端分段：网络后端时选中"MiniMax"。</summary>
    [AvaloniaFact]
    public async Task 后端_网络_选中MiniMax项()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(backend: "minimax", model: "asr-1.0"));
            await Task.Delay(20);

            Assert.False(window.BackendLocalItem.IsChecked == true);
            Assert.True(window.BackendMinimaxItem.IsChecked == true);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 模型行 ====================

    /// <summary>
    /// 模型行：本地后端下显示 0.6B / 1.7B 的档位，网络后端下显示远端模型名。
    /// panel.js L301-321 <c>applyBackend</c> / <c>renderNetModel</c>：
    /// 网络后端下 <c>modelSeg</c> 整条藏起，<c>netModelText</c> 顶上。
    /// </summary>
    [AvaloniaFact]
    public async Task 模型_本地_显示档位_藏起远端模型名()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(backend: "local", model: "1.7B"));
            await Task.Delay(20);

            Assert.True(window.ModelRow.IsVisible);
            Assert.False(window.NetModelText.IsVisible);
            Assert.True(window.Model17Item.IsChecked == true);
            Assert.False(window.Model06Item.IsChecked == true);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>模型行：网络后端下藏起档位、显示远端模型名（asr-1.0）。</summary>
    [AvaloniaFact]
    public async Task 模型_网络_藏起档位_显示远端模型名()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(backend: "minimax", model: "asr-1.0"));
            await Task.Delay(20);

            Assert.False(window.ModelRow.IsVisible);
            Assert.True(window.NetModelText.IsVisible);
            Assert.Equal("asr-1.0", window.NetModelText.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 主题 / 界面形态 ====================

    /// <summary>主题分段：黑夜档选中"黑夜"。</summary>
    [AvaloniaFact]
    public async Task 主题_黑夜_选中黑夜项()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(theme: "dark"));
            await Task.Delay(20);

            Assert.True(window.ThemeDarkItem.IsChecked == true);
            Assert.False(window.ThemeAutoItem.IsChecked == true);
            Assert.False(window.ThemeLightItem.IsChecked == true);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 界面形态分段：常驻档选中"常驻"。
    /// CSS L347-349 <c>.seg.is-text</c> 那一段（主题 / 界面形态）用非等宽字体。
    /// </summary>
    [AvaloniaFact]
    public async Task 界面形态_常驻_选中常驻项()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(uiMode: "resident"));
            await Task.Delay(20);

            Assert.True(window.UiModeResidentItem.IsChecked == true);
            Assert.False(window.UiModeOrbItem.IsChecked == true);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 「无界面」档是 disabled 的（Python 版也是 disabled，行为照搬）。
    /// panel.html L78：<c>&lt;button ... data-ui-mode="headless" disabled&gt;</c>。
    /// </summary>
    [AvaloniaFact]
    public async Task 界面形态_无界面档是禁用的()
    {
        var window = await OpenPanelAsync();
        try
        {
            Assert.False(window.UiModeHeadlessItem.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 两个开关 ====================

    /// <summary>预览开关与归还开关跟着快照走（panel.js L242-262）。</summary>
    [AvaloniaFact]
    public async Task 开关_预览与归还跟着快照走()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(preview: false, restoreClipboard: true));
            await Task.Delay(20);
            Assert.False(window.PreviewSwitch.IsChecked == true);
            Assert.True(window.RestoreClipboardSwitch.IsChecked == true);

            window.ApplySnapshot(Snapshot(preview: true, restoreClipboard: false));
            await Task.Delay(20);
            Assert.True(window.PreviewSwitch.IsChecked == true);
            Assert.False(window.RestoreClipboardSwitch.IsChecked == true);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 状态点 ====================

    /// <summary>
    /// 状态点：待机是绿色（--k-ask-b），录音中是红色（--danger）。
    /// CSS L198-228：<c>.dot[data-state=...]</c> 四档各有底色与光晕色。
    /// </summary>
    [AvaloniaFact]
    public async Task 状态点_待机是绿_录音中是红()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(state: AppStates.Idle));
            await Task.Delay(20);
            Assert.Equal(PanelTheme.DarkPalette["--k-ask-b"], BackgroundOf(window.StateDot.Background));
            Assert.Equal(PanelTheme.DarkPalette["--halo-ok"], BackgroundOf(window.StateDotHalo.Background));

            window.ApplySnapshot(Snapshot(state: AppStates.Recording));
            await Task.Delay(20);
            Assert.Equal(PanelTheme.DarkPalette["--danger"], BackgroundOf(window.StateDot.Background));
            Assert.Equal(PanelTheme.DarkPalette["--halo-rec"], BackgroundOf(window.StateDotHalo.Background));

            window.ApplySnapshot(Snapshot(state: AppStates.Starting));
            await Task.Delay(20);
            Assert.Equal(PanelTheme.DarkPalette["--warn"], BackgroundOf(window.StateDot.Background));

            window.ApplySnapshot(Snapshot(state: AppStates.Processing));
            await Task.Delay(20);
            Assert.Equal(PanelTheme.DarkPalette["--k-trans-b"], BackgroundOf(window.StateDot.Background));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>状态文字：panel.js L117-122 的 <c>STATE_TEXT</c>。</summary>
    [AvaloniaFact]
    public async Task 状态文字_四个状态各有文案()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(state: AppStates.Idle));
            await Task.Delay(20);
            Assert.Equal("待机", window.StateText.Text);

            window.ApplySnapshot(Snapshot(state: AppStates.Starting));
            await Task.Delay(20);
            Assert.Equal("准备中", window.StateText.Text);

            window.ApplySnapshot(Snapshot(state: AppStates.Recording));
            await Task.Delay(20);
            Assert.Equal("录音中", window.StateText.Text);

            window.ApplySnapshot(Snapshot(state: AppStates.Processing));
            await Task.Delay(20);
            Assert.Equal("识别中", window.StateText.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 健康徽章 ====================

    /// <summary>
    /// 健康徽章：三态各有文案与配色（CSS L391-409）。
    /// 失败原因常驻显示（CSS L413-424）。
    /// </summary>
    [AvaloniaFact]
    public async Task 健康徽章_三态文案与配色_失败原因常驻()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(health: HealthStatuses.Loading));
            await Task.Delay(20);
            Assert.Equal("加载中", window.HealthBadgeText.Text);
            Assert.Equal(PanelTheme.DarkPalette["--nav"], BackgroundOf(window.HealthBadge.Background));
            Assert.Equal(PanelTheme.DarkPalette["--text2"], ForegroundOf(window.HealthBadgeText.Foreground));
            Assert.False(window.HealthReason.IsVisible);

            window.ApplySnapshot(Snapshot(health: HealthStatuses.Ready));
            await Task.Delay(20);
            Assert.Equal("就绪", window.HealthBadgeText.Text);
            Assert.Equal(PanelTheme.DarkPalette["--k-ask"], BackgroundOf(window.HealthBadge.Background));
            Assert.Equal(PanelTheme.DarkPalette["--k-ask-b"], ForegroundOf(window.HealthBadgeText.Foreground));

            window.ApplySnapshot(Snapshot(
                health: HealthStatuses.Failed,
                reason: "本地权重缺失:Qwen3-ASR-0.6B 不存在"));
            await Task.Delay(20);
            Assert.Equal("失败", window.HealthBadgeText.Text);
            Assert.Equal(PanelTheme.DarkPalette["--danger-soft"], BackgroundOf(window.HealthBadge.Background));
            Assert.Equal(PanelTheme.DarkPalette["--danger"], ForegroundOf(window.HealthBadgeText.Foreground));

            // 失败原因常驻显示（不自动消失）—— 原因才是有用的那半截。
            Assert.True(window.HealthReason.IsVisible);
            Assert.Equal("本地权重缺失:Qwen3-ASR-0.6B 不存在", window.HealthReason.Text);
            Assert.Equal(PanelTheme.DarkPalette["--danger"], ForegroundOf(window.HealthReason.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 提示语 ====================

    /// <summary>
    /// 提示语：空的时候不占位置（CSS L438-440 <c>.notice:empty { display:none }</c>），
    /// 有内容时按 level 配色。
    /// </summary>
    [AvaloniaFact]
    public async Task 提示语_空时不占位置_按级别配色()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot());
            await Task.Delay(20);
            Assert.False(window.Notice.IsVisible);
            Assert.Equal(string.Empty, window.Notice.Text);

            window.ApplySnapshot(Snapshot(notice: "正在切换到 MiniMax…", noticeLevel: "info"));
            await Task.Delay(20);
            Assert.True(window.Notice.IsVisible);
            Assert.Equal("正在切换到 MiniMax…", window.Notice.Text);
            Assert.Equal(PanelTheme.DarkPalette["--text2"], ForegroundOf(window.Notice.Foreground));

            window.ApplySnapshot(Snapshot(notice: "录音中不能切换识别后端", noticeLevel: "warning"));
            await Task.Delay(20);
            Assert.Equal(PanelTheme.DarkPalette["--warn"], ForegroundOf(window.Notice.Foreground));

            window.ApplySnapshot(Snapshot(notice: "切换失败", noticeLevel: "error"));
            await Task.Delay(20);
            Assert.Equal(PanelTheme.DarkPalette["--danger"], ForegroundOf(window.Notice.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 热键 ====================

    /// <summary>
    /// 热键：页脚里显示按键组合（panel.html L111 的 <c>&lt;kbd id="hotkeyLabel"&gt;</c>）。
    /// panel.js L198-207 <c>applyHotkey</c>：去掉"热键 "前缀，空的话显示"未设置"。
    /// </summary>
    [AvaloniaFact]
    public async Task 热键_显示按键组合_空的话显示未设置()
    {
        var window = await OpenPanelAsync();
        try
        {
            window.ApplySnapshot(Snapshot(hotkey: "ctrl+alt+v"));
            await Task.Delay(20);
            Assert.Equal("ctrl+alt+v", window.HotkeyLabel.Text);

            window.ApplySnapshot(Snapshot(hotkey: "热键 Ctrl+Alt+V"));
            await Task.Delay(20);
            Assert.Equal("Ctrl+Alt+V", window.HotkeyLabel.Text);

            window.ApplySnapshot(Snapshot(hotkey: string.Empty));
            await Task.Delay(20);
            Assert.Equal("未设置", window.HotkeyLabel.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ==================== 用户动作 ====================

    /// <summary>
    /// 用户点分段 / 开关 → 抛事件（由装配层接回 presenter）。
    /// panel.js L436-539 的事件绑定。
    /// </summary>
    [AvaloniaFact]
    public async Task 用户动作_抛对应事件()
    {
        var window = await OpenPanelAsync();
        try
        {
            var backends = new List<string>();
            var models = new List<string>();
            var themes = new List<string>();
            var uiModes = new List<string>();
            var previews = new List<bool>();
            var restores = new List<bool>();
            bool closed = false;
            bool quit = false;

            window.BackendChanged += (_, kind) => backends.Add(kind);
            window.ModelChanged += (_, tag) => models.Add(tag);
            window.ThemeChanged += (_, mode) => themes.Add(mode);
            window.UiModeChanged += (_, mode) => uiModes.Add(mode);
            window.PreviewChanged += (_, enabled) => previews.Add(enabled);
            window.RestoreClipboardChanged += (_, enabled) => restores.Add(enabled);
            window.CloseRequested += (_, _) => closed = true;
            window.QuitRequested += (_, _) => quit = true;

            window.BackendMinimaxItem.IsChecked = true;
            window.Model17Item.IsChecked = true;
            window.ThemeDarkItem.IsChecked = true;
            window.UiModeResidentItem.IsChecked = true;
            window.PreviewSwitch.IsChecked = false;
            window.RestoreClipboardSwitch.IsChecked = false;

            await Task.Delay(20);

            Assert.Equal(new[] { "minimax" }, backends);
            Assert.Equal(new[] { "1.7B" }, models);
            Assert.Equal(new[] { "dark" }, themes);
            Assert.Equal(new[] { "resident" }, uiModes);
            Assert.Equal(new[] { false }, previews);
            Assert.Equal(new[] { false }, restores);

            window.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.QuitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(20);

            Assert.True(closed);
            Assert.True(quit);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 套用快照**不**触发用户动作事件（否则会形成"推快照 → 用户动作 → 再推"的回环）。
    /// </summary>
    [AvaloniaFact]
    public async Task 套用快照_不触发用户动作事件()
    {
        var window = await OpenPanelAsync();
        try
        {
            int count = 0;
            window.BackendChanged += (_, _) => count++;
            window.ThemeChanged += (_, _) => count++;
            window.UiModeChanged += (_, _) => count++;
            window.PreviewChanged += (_, _) => count++;

            window.ApplySnapshot(Snapshot(backend: "minimax", theme: "dark", uiMode: "resident", preview: false));
            await Task.Delay(20);

            Assert.Equal(0, count);
        }
        finally
        {
            window.Close();
        }
    }
}
