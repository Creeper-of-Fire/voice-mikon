using System;
using System.Collections.Generic;
using VmNext.Core;
using VmNext.Core.Presentation;
using Xunit;

namespace VmNext.Core.Tests;

/// <summary>
/// 面板 presenter 的单元测试。
///
/// <para>翻译源：voice-mikon/src/voice_mikon/apps/panel/presenter.py（381 行）。
/// 这里用**假 core / 假配置**驱动 —— 不加载模型、不碰剪贴板、不碰桌面。</para>
///
/// <para>覆盖：动作方法调了正确的下游并 <c>Save</c>；core 快照变化 →
/// <c>SnapshotChanged</c> 的字段正确；健康状态 + 原因映射到快照；
/// 提示语只收"面板发起的操作"那六种通知。</para>
/// </summary>
public class PanelPresenterTests
{
    // ==================== 假实现 ====================

    /// <summary>假 core：只记动作、只发快照。</summary>
    private sealed class FakeCore : IPanelCore
    {
        private readonly List<Action<AppState>> _listeners = new();

        public AppState StateSnapshot { get; set; } = new();

        public List<string> SwitchedModels { get; } = new();

        public List<string> SwitchedBackends { get; } = new();

        public void Subscribe(Action<AppState> callback)
        {
            _listeners.Add(callback);

            // 订阅即投递（core 的规矩）：新订阅者立刻收到当前快照一次。
            callback(StateSnapshot);
        }

        public void SwitchModel(string modelTag) => SwitchedModels.Add(modelTag);

        public void SwitchBackend(string kind) => SwitchedBackends.Add(kind);

        /// <summary>推一份新快照（模拟 core 状态变化）。</summary>
        public void Push(AppState state)
        {
            StateSnapshot = state;
            foreach (Action<AppState> listener in _listeners)
            {
                listener(state);
            }
        }
    }

    /// <summary>假配置：只记 Save 的次数。</summary>
    private sealed class FakeConfig : IPanelConfig
    {
        public string Theme { get; set; } = "auto";

        public string UiMode { get; set; } = "orb";

        public bool ShowPreview { get; set; } = true;

        public bool RestoreClipboard { get; set; } = true;

        public string Hotkey { get; set; } = "ctrl+alt+space";

        public int SaveCount { get; private set; }

        public void Save() => SaveCount++;
    }

    // ==================== 装配 ====================

    [Fact]
    public void 安装_立刻收到当前快照()
    {
        var core = new FakeCore
        {
            StateSnapshot = new AppState
            {
                State = AppStates.Idle,
                Backend = "local",
                Model = "0.6B",
                BackendHealth = new BackendHealth(HealthStatuses.Ready),
            },
        };
        var presenter = new PanelPresenter(core);

        var snapshots = new List<PanelSnapshot>();
        presenter.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);

        presenter.Install();

        // core 的订阅是"订阅即投递"的：漏掉这一次，面板得一直等到下一次变化。
        Assert.Single(snapshots);
        Assert.Equal(AppStates.Idle, snapshots[0].State);
        Assert.Equal("local", snapshots[0].Backend);
        Assert.Equal("0.6B", snapshots[0].Model);
        Assert.Equal(HealthStatuses.Ready, snapshots[0].HealthStatus);
    }

    // ==================== 动作方法 ====================

    [Fact]
    public void 切换后端_调core并记下档位()
    {
        var core = new FakeCore();
        var presenter = new PanelPresenter(core);
        presenter.Install();

        presenter.SwitchBackend("minimax");

        Assert.Equal(new[] { "minimax" }, core.SwitchedBackends);
    }

    [Fact]
    public void 切换模型_调core并记下档位()
    {
        var core = new FakeCore();
        var presenter = new PanelPresenter(core);
        presenter.Install();

        presenter.SwitchModel("1.7B");

        Assert.Equal(new[] { "1.7B" }, core.SwitchedModels);
    }

    [Fact]
    public void 换主题_存配置并落盘_广播主题事件()
    {
        var core = new FakeCore();
        var config = new FakeConfig();
        var presenter = new PanelPresenter(core, config);
        presenter.Install();

        string? broadcast = null;
        presenter.ThemeChanged += (_, mode) => broadcast = mode;

        presenter.SetTheme("dark");

        Assert.Equal("dark", config.Theme);
        Assert.Equal(1, config.SaveCount);
        Assert.Equal("dark", broadcast);
    }

    [Fact]
    public void 换主题_未知档位按auto处理()
    {
        var core = new FakeCore();
        var config = new FakeConfig();
        var presenter = new PanelPresenter(core, config);
        presenter.Install();

        string? broadcast = null;
        presenter.ThemeChanged += (_, mode) => broadcast = mode;

        presenter.SetTheme("霓虹");

        Assert.Equal("auto", config.Theme);
        Assert.Equal("auto", broadcast);
    }

    [Fact]
    public void 开关预览_存配置并落盘()
    {
        var core = new FakeCore();
        var config = new FakeConfig();
        var presenter = new PanelPresenter(core, config);
        presenter.Install();

        presenter.SetPreview(false);

        Assert.False(config.ShowPreview);
        Assert.Equal(1, config.SaveCount);
        Assert.False(presenter.Snapshot.PreviewEnabled);
    }

    [Fact]
    public void 开关归还_存配置并落盘()
    {
        var core = new FakeCore();
        var config = new FakeConfig();
        var presenter = new PanelPresenter(core, config);
        presenter.Install();

        presenter.SetRestoreClipboard(false);

        Assert.False(config.RestoreClipboard);
        Assert.Equal(1, config.SaveCount);
        Assert.False(presenter.Snapshot.RestoreClipboardEnabled);
    }

    [Fact]
    public void 换界面形态_存配置并落盘_广播事件()
    {
        var core = new FakeCore();
        var config = new FakeConfig();
        var presenter = new PanelPresenter(core, config);
        presenter.Install();

        string? broadcast = null;
        presenter.UiModeChanged += (_, mode) => broadcast = mode;

        presenter.SetUiMode("resident");

        Assert.Equal("resident", config.UiMode);
        Assert.Equal(1, config.SaveCount);
        Assert.Equal("resident", broadcast);
    }

    [Fact]
    public void 换界面形态_headless被忽略()
    {
        // Python 版：无界面档在面板上是 disabled，到不了这里；防御性忽略。
        var core = new FakeCore();
        var config = new FakeConfig();
        var presenter = new PanelPresenter(core, config);
        presenter.Install();

        bool fired = false;
        presenter.UiModeChanged += (_, _) => fired = true;

        presenter.SetUiMode("headless");

        Assert.Equal("orb", config.UiMode);
        Assert.Equal(0, config.SaveCount);
        Assert.False(fired);
    }

    [Fact]
    public void 退出_抛退出事件_关面板_抛关闭事件()
    {
        var presenter = new PanelPresenter(new FakeCore());
        presenter.Install();

        bool quit = false;
        bool close = false;
        presenter.QuitRequested += (_, _) => quit = true;
        presenter.CloseRequested += (_, _) => close = true;

        presenter.QuitApp();
        presenter.ClosePanel();

        Assert.True(quit);
        Assert.True(close);
    }

    // ==================== core 快照 → 快照 ====================

    [Fact]
    public void core快照变化_推新快照_字段正确()
    {
        var core = new FakeCore();
        var presenter = new PanelPresenter(core);
        presenter.Install();

        PanelSnapshot? last = null;
        presenter.SnapshotChanged += (_, snapshot) => last = snapshot;

        core.Push(new AppState
        {
            State = AppStates.Recording,
            Backend = "local",
            Model = "1.7B",
            BackendHealth = new BackendHealth(HealthStatuses.Ready),
        });

        Assert.NotNull(last);
        Assert.Equal(AppStates.Recording, last!.State);
        Assert.Equal("1.7B", last.Model);
        Assert.Equal(HealthStatuses.Ready, last.HealthStatus);
    }

    [Fact]
    public void 健康状态与原因进快照()
    {
        var core = new FakeCore();
        var presenter = new PanelPresenter(core);
        presenter.Install();

        core.Push(new AppState
        {
            State = AppStates.Idle,
            Backend = "local",
            BackendHealth = new BackendHealth(HealthStatuses.Failed, "本地权重缺失:Qwen3-ASR-0.6B 不存在"),
        });

        PanelSnapshot snapshot = presenter.Snapshot;
        Assert.Equal(HealthStatuses.Failed, snapshot.HealthStatus);
        Assert.Equal("本地权重缺失:Qwen3-ASR-0.6B 不存在", snapshot.HealthReason);
    }

    [Fact]
    public void 网络后端_远端模型名进NetworkModel_本地后端为空()
    {
        var core = new FakeCore();
        var presenter = new PanelPresenter(core);
        presenter.Install();

        core.Push(new AppState
        {
            State = AppStates.Idle,
            Backend = "minimax",
            Model = "asr-1.0",
            BackendHealth = new BackendHealth(HealthStatuses.Ready),
        });

        Assert.Equal("asr-1.0", presenter.Snapshot.Model);
        Assert.Equal("asr-1.0", presenter.Snapshot.NetworkModel);

        core.Push(new AppState
        {
            State = AppStates.Idle,
            Backend = "local",
            Model = "0.6B",
            BackendHealth = new BackendHealth(HealthStatuses.Ready),
        });

        Assert.Equal("0.6B", presenter.Snapshot.Model);
        Assert.Equal(string.Empty, presenter.Snapshot.NetworkModel);
    }

    [Fact]
    public void 同值不重复推()
    {
        // Python 版的 _push 去重账本：空推会白刷前端。
        var core = new FakeCore();
        var presenter = new PanelPresenter(core);

        int count = 0;
        presenter.SnapshotChanged += (_, _) => count++;

        presenter.Install();
        Assert.Equal(1, count);

        core.Push(core.StateSnapshot);
        Assert.Equal(1, count);
    }

    // ==================== 通知 → 提示语 ====================

    [Fact]
    public void 切后端的通知_变成提示语()
    {
        var core = new FakeCore();
        var notifier = new NotificationHub();
        var presenter = new PanelPresenter(core, notifier: notifier);
        presenter.Install();

        notifier.Publish("info", "backend_switching", "正在切换到 MiniMax…");

        Assert.Equal("正在切换到 MiniMax…", presenter.Snapshot.Notice);
        Assert.Equal("info", presenter.Snapshot.NoticeLevel);
    }

    [Fact]
    public void 切失败的通知_带级别变提示语()
    {
        var core = new FakeCore();
        var notifier = new NotificationHub();
        var presenter = new PanelPresenter(core, notifier: notifier);
        presenter.Install();

        notifier.Publish("warning", "backend_switch_failed", "录音中不能切换识别后端,请先结束这一轮");

        Assert.Equal("录音中不能切换识别后端,请先结束这一轮", presenter.Snapshot.Notice);
        Assert.Equal("warning", presenter.Snapshot.NoticeLevel);
    }

    [Fact]
    public void 别的通知不当提示语()
    {
        // 录音失败、注入失败…归胶囊的 presenter，面板不重复显示。
        var core = new FakeCore();
        var notifier = new NotificationHub();
        var presenter = new PanelPresenter(core, notifier: notifier);
        presenter.Install();

        notifier.Publish("error", "asr_failed", "没听清，请再说一遍。");

        Assert.Equal(string.Empty, presenter.Snapshot.Notice);
    }

    [Fact]
    public void 清提示语_快照里的提示语归零()
    {
        var core = new FakeCore();
        var notifier = new NotificationHub();
        var presenter = new PanelPresenter(core, notifier: notifier);
        presenter.Install();

        notifier.Publish("info", "model_switched", "已切换到 1.7B");
        Assert.NotEqual(string.Empty, presenter.Snapshot.Notice);

        presenter.ClearNotice();

        Assert.Equal(string.Empty, presenter.Snapshot.Notice);
    }

    // ==================== 收尾 ====================

    [Fact]
    public void 收尾之后不再推快照()
    {
        var core = new FakeCore();
        var presenter = new PanelPresenter(core);
        presenter.Install();

        int count = 0;
        presenter.SnapshotChanged += (_, _) => count++;

        presenter.Shutdown();
        core.Push(new AppState { State = AppStates.Recording });

        Assert.Equal(0, count);
    }
}
