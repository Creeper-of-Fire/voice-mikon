using System;
using VmNext.Core;
using VmNext.Core.Controller;
using VmNext.Shell;
using Xunit;

namespace VmNext.App.Tests;

/// <summary>
/// 热键链路的端到端测试：系统投递 WM_HOTKEY → 绑定回调 → 控制器进入录音。
///
/// 用假录音器（不碰麦克风）：这条链路要验的是"热键消息 → 控制器"，
/// 不是"麦克风能不能录到声音"。
/// </summary>
[Collection("interop")]
public class HotKeyTests
{
    /// <summary>
    /// PostMessage(WM_HOTKEY) 触发 → 进入录音。
    ///
    /// 组合键用一个冷门的（ctrl+alt+f12）：用户自己的 voice-mikon 正占着
    /// ctrl+alt+space，注册同一个键必然失败。
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void 热键消息触发进入录音()
    {
        using var app = new TestApp(recorder: new FakeRecorder());
        using var hotkey = new HotkeyBinding(app.Notifier);

        hotkey.Start();

        bool registered = hotkey.Register("ctrl+alt+f12", () => app.Controller.ToggleRecording());
        if (!registered)
        {
            Assert.Skip("热键 ctrl+alt+f12 注册不上（可能被别的程序占用），跳过");
        }

        Assert.NotEqual(IntPtr.Zero, hotkey.WindowHandle);

        // 系统投递 WM_HOTKEY 时 wParam 就是注册时用的 id
        bool posted = NativeMethods.PostMessage(
            hotkey.WindowHandle,
            NativeMethods.WM_HOTKEY,
            (IntPtr)hotkey.LastRegisteredId,
            IntPtr.Zero);
        Assert.True(posted, "PostMessage 失败");

        Assert.True(
            TestApp.WaitFor(
                () => string.Equals(app.Controller.StateSnapshot.State, AppStates.Recording, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
            "热键没有让控制器进入录音状态");
    }

    /// <summary>
    /// 组合键解析：修饰键 + 主键（对齐 Python 的 <c>parse_hotkey</c>）。
    /// </summary>
    [Fact]
    public void 热键字符串解析成修饰键加主键()
    {
        (uint modifiers, uint vk) = HotkeyBinding.Parse("ctrl+alt+space");

        Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, modifiers);
        Assert.Equal(0x20u, vk);

        (uint f12Mods, uint f12Vk) = HotkeyBinding.Parse("ctrl+shift+F12");
        Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, f12Mods);
        Assert.Equal(0x7Bu, f12Vk);

        (uint winMods, uint winVk) = HotkeyBinding.Parse("win+a");
        Assert.Equal(NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT, winMods);
        Assert.Equal(0x41u, winVk);
    }
}
