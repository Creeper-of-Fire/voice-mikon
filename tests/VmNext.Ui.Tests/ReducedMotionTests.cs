using System;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VmNext.Core.Presentation;
using VmNext.Shell;
using VmNext.Ui;
using Xunit;

namespace VmNext.Ui.Tests;

/// <summary>
/// 动效降级的单元测试。
///
/// <para>CSS 的 <c>@media (prefers-reduced-motion: reduce)</c>（capsule.css L674-684）：
/// 系统关了"在显示中显示动画"时，<c>.recording-pill</c> / <c>.textbar</c> / <c>.bar-orb</c>
/// 的 transition 一律 <c>none</c>。</para>
///
/// <para>Windows 上对应 <c>SPI_GETCLIENTAREAANIMATION</c>。真机上这个开关由用户控制，
/// 所以这里用<b>可注入</b>的提供者模拟"系统关了动画"。</para>
/// </summary>
public class ReducedMotionTests
{
    /// <summary>把系统动画开关临时改成"关"，测完还原。</summary>
    private static IDisposable DisableSystemAnimation()
    {
        Func<bool> original = SystemAnimation.AnimationEnabledProvider;
        SystemAnimation.AnimationEnabledProvider = () => false;
        return new Restore(() => SystemAnimation.AnimationEnabledProvider = original);
    }

    /// <summary>还原委托的小工具。</summary>
    private sealed class Restore : IDisposable
    {
        private readonly Action _action;

        public Restore(Action action) => _action = action;

        public void Dispose() => _action();
    }

    /// <summary>取控件上所有过渡的时长（按具体类型取 —— ITransition 不暴露 Duration）。</summary>
    private static System.Collections.Generic.List<TimeSpan> DurationsOf(Control control)
    {
        var durations = new System.Collections.Generic.List<TimeSpan>();
        Avalonia.Animation.Transitions? transitions = control.Transitions;
        if (transitions is null)
        {
            return durations;
        }

        foreach (ITransition transition in transitions)
        {
            switch (transition)
            {
                case DoubleTransition d:
                    durations.Add(d.Duration);
                    break;

                case CornerRadiusTransition c:
                    durations.Add(c.Duration);
                    break;
            }
        }

        return durations;
    }

    /// <summary>
    /// 开了动效降级：所有过渡时长归零（= CSS 的 <c>transition: none</c>）。
    /// </summary>
    [AvaloniaFact]
    public async Task 降级开启_所有过渡时长归零()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            window.ApplyReducedMotion(true);

            foreach (Control control in new Control[] { window.Pill, window.TextBar, window.OrbHost })
            {
                foreach (TimeSpan duration in DurationsOf(control))
                {
                    Assert.Equal(TimeSpan.Zero, duration);
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 系统关了动画时，窗口建起来就是"没有动画"的（构造时就读了系统开关）。
    /// </summary>
    [AvaloniaFact]
    public async Task 系统关了动画_建窗即降级()
    {
        using (DisableSystemAnimation())
        {
            var window = await CapsuleWindowTests.OpenWindowAsync();
            try
            {
                window.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
                await CapsuleWindowTests.SettleAsync();

                foreach (TimeSpan duration in DurationsOf(window.Pill))
                {
                    Assert.Equal(TimeSpan.Zero, duration);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>降级关掉后，时长回到原来的值（不是一直 0）。</summary>
    [AvaloniaFact]
    public async Task 降级关闭_时长恢复()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Recording, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            window.ApplyReducedMotion(true);
            window.ApplyReducedMotion(false);

            // 胶囊的形状过渡（max-width .8s）应该回来了。
            bool found = false;
            foreach (TimeSpan duration in DurationsOf(window.Pill))
            {
                if (duration > TimeSpan.Zero)
                {
                    found = true;
                }
            }

            Assert.True(found, "降级关闭后过渡时长应该恢复");
        }
        finally
        {
            window.Close();
        }
    }
}
