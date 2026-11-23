using System;

namespace VmNext.Shell;

/// <summary>
/// 系统动画开关（对应 CSS 的 <c>@media (prefers-reduced-motion: reduce)</c>）。
///
/// <para>capsule.css L674-684：用户把"在显示中显示动画"关掉时，
/// <c>.recording-pill</c> / <c>.textbar</c> / <c>.bar-orb</c> 的 transition 一律
/// <c>none</c>，<c>.bar-orb svg</c> 的动画也停掉。</para>
///
/// <para>Windows 上对应的开关是 <c>SPI_GETCLIENTAREAANIMATION</c>
/// （"轻松使用 → 视觉效果 → 在显示中显示动画"）。</para>
///
/// <para><b>可注入</b>：<see cref="AnimationEnabledProvider"/> 让测试能塞一个假值进去 ——
/// 无头环境里读不到真实系统设置，断言就没法做。</para>
/// </summary>
public static class SystemAnimation
{
    private static Func<bool>? _provider;

    /// <summary>
    /// 系统动画是否开着。默认读 <c>SPI_GETCLIENTAREAANIMATION</c>；
    /// 可以换成别的实现（测试用）。
    /// </summary>
    public static Func<bool> AnimationEnabledProvider
    {
        get => _provider ?? QuerySystem;
        set => _provider = value;
    }

    /// <summary>系统动画是否开着（true = 正常播动画）。</summary>
    public static bool IsAnimationEnabled()
    {
        try
        {
            return AnimationEnabledProvider();
        }
        catch
        {
            // 读不到就按"有动画"处理 —— 宁可多播动画，也不要在用户没要求时把界面搞死板。
            return true;
        }
    }

    /// <summary>读系统设置（Windows 的"在显示中显示动画"）。</summary>
    private static bool QuerySystem()
    {
        bool enabled = true;
        NativeMethods.SystemParametersInfoBool(
            NativeMethods.SPI_GETCLIENTAREAANIMATION,
            0,
            ref enabled,
            0);
        return enabled;
    }
}
