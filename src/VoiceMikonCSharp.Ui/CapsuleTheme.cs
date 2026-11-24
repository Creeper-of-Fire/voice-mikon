using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.Ui;

/// <summary>
/// 胶囊的主题档位。名字与 capsule.js 的 <c>THEMES = ['auto', 'light', 'dark']</c> 对齐：
/// auto = 跟随系统（CSS 里**不设** data-theme，媒体查询自然接管）。
/// </summary>
public enum CapsuleThemeMode
{
    /// <summary>跟随系统。</summary>
    Auto,

    /// <summary>强制白天（对应 CSS 的 <c>:root[data-theme="light"]</c>）。</summary>
    Light,

    /// <summary>强制黑夜（对应 CSS 的 <c>:root[data-theme="dark"]</c>，即 :root 的深色基准）。</summary>
    Dark,
}

/// <summary>
/// 胶囊的主题：两张 palette + 三档切换。
///
/// <para>色值来源与唯一真相：<c>voice-mikon/src/voice_mikon/host/static/capsule.css</c>。
/// 深色基准是 <c>:root { ... }</c>，浅色是 <c>:root[data-theme="light"]</c> 与
/// <c>@media (prefers-color-scheme: light) :root:not([data-theme="dark"]) { ... }</c>。
/// CSS 注释明确写了"两处的 token 值必须一致（改一处就要改另一处）"，
/// 所以这里只有**一张**浅色 palette。</para>
///
/// <para>⚠ <b>CSS 的 8 位十六进制是 #RRGGBBAA（alpha 在最后），不是 ARGB。</b>
/// 例如 <c>--stroke: #ffffff33</c> 是「白色 + 20% 不透明」，
/// 换算成 Avalonia 的 ARGB 是 <c>#33FFFFFF</c>。
/// 浅色变体那句注释（"rgba(255,255,255,.2) 那种白描边根本看不见"）也印证了这一点。
/// 本类的 Color 一律是 Avalonia 的 ARGB；转换由 tests/VoiceMikonCSharp.TokenCheck 自动核对。</para>
///
/// <para>为什么不用 Avalonia 的 <c>ThemeDictionaries</c>：它是**替换 brush 实例**，
/// 切主题会瞬间跳色；而 capsule.css 的 <c>.recording-pill { transition: background-color .18s }</c>
/// 要求平滑过渡。所以这里走 <see cref="OverlayTheme.TweenBrush"/>：就地改 brush.Color，
/// 控件引用的实例不变，动画才有得补。</para>
///
/// <para>类名不能叫 <c>Theme</c>：Avalonia 11.x 给 <c>StyledElement</c> 加了同名实例属性
/// <c>Theme</c>（类型 ControlTheme），在窗口子类里写 <c>Theme.Toggle()</c> 会解析到那个属性上
/// （archive 意外发现 #1）。</para>
/// </summary>
public static class CapsuleTheme
{
    /// <summary>CSS 变量名 → Avalonia 资源键。palette 与资源字典靠这张表对齐。</summary>
    private static readonly (string Token, string Key)[] TokenMap =
    {
        ("--deep", "Capsule.Deep.Brush"),
        ("--fg", "Capsule.Fg.Brush"),
        ("--stroke", "Capsule.Stroke.Brush"),
        ("--err-bg", "Capsule.ErrBg.Brush"),
        ("--err-stroke", "Capsule.ErrStroke.Brush"),
        ("--err-fg", "Capsule.ErrFg.Brush"),
        ("--err-icon", "Capsule.ErrIcon.Brush"),
        ("--warn-bg", "Capsule.WarnBg.Brush"),
        ("--warn-stroke", "Capsule.WarnStroke.Brush"),
        ("--warn-fg", "Capsule.WarnFg.Brush"),
        ("--warn-icon", "Capsule.WarnIcon.Brush"),
        ("--cancel-btn", "Capsule.CancelBtn.Brush"),
        ("--cancel-btn-hover", "Capsule.CancelBtnHover.Brush"),
        ("--state-bg", "Capsule.StateBg.Brush"),
        ("--wave-fg", "Capsule.WaveFg.Brush"),
        ("--dim", "Capsule.Dim.Brush"),
        ("--dim2", "Capsule.Dim2.Brush"),
        ("--dim3", "Capsule.Dim3.Brush"),
        ("--wave", "Capsule.Wave.Brush"),
        ("--wave-dim", "Capsule.WaveDim.Brush"),
    };

    /// <summary>深色基准：capsule.css 的 <c>:root { ... }</c>（L43–82）。</summary>
    private static readonly Dictionary<string, Color> DarkTokens = new()
    {
        ["--deep"] = Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0C),           // #0b0b0c
        ["--fg"] = Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5),             // #f4f4f5
        ["--stroke"] = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),        // #ffffff33
        ["--err-bg"] = Color.FromArgb(0xFF, 0x2A, 0x0F, 0x11),         // #2a0f11
        ["--err-stroke"] = Color.FromArgb(0x4D, 0xFF, 0x6B, 0x6B),    // #ff6b6b4d
        ["--err-fg"] = Color.FromArgb(0xFF, 0xFE, 0xCA, 0xCA),        // #fecaca
        ["--err-icon"] = Color.FromArgb(0xFF, 0xFC, 0xA5, 0xA5),      // #fca5a5
        ["--warn-bg"] = Color.FromArgb(0xFF, 0x2A, 0x1F, 0x0B),       // #2a1f0b
        ["--warn-stroke"] = Color.FromArgb(0x4D, 0xFB, 0xBF, 0x24),   // #fbbf244d
        ["--warn-fg"] = Color.FromArgb(0xFF, 0xFD, 0xE6, 0x8A),       // #fde68a
        ["--warn-icon"] = Color.FromArgb(0xFF, 0xFC, 0xD3, 0x4D),     // #fcd34d
        ["--cancel-btn"] = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3E),    // #3a3a3e
        ["--cancel-btn-hover"] = Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4F), // #4a4a4f
        ["--state-bg"] = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),      // #ffffff14
        ["--wave-fg"] = Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0C),       // #0b0b0c
        ["--dim"] = Color.FromArgb(0x80, 0xF4, 0xF4, 0xF5),           // rgba(244,244,245,.5)
        ["--dim2"] = Color.FromArgb(0xB3, 0xF4, 0xF4, 0xF5),          // rgba(244,244,245,.7)
        ["--dim3"] = Color.FromArgb(0xA6, 0xF4, 0xF4, 0xF5),          // rgba(244,244,245,.65)
        ["--wave"] = Color.FromArgb(0xFF, 0xFF, 0x5A, 0x5A),          // #ff5a5a（两套主题一样）
        ["--wave-dim"] = Color.FromArgb(0x47, 0xF4, 0xF4, 0xF5),      // rgba(244,244,245,.28)
    };

    /// <summary>
    /// 浅色：capsule.css 的 <c>:root[data-theme="light"] { ... }</c>（L117–137），
    /// 与 <c>@media (prefers-color-scheme: light) :root:not([data-theme="dark"]) { ... }</c>（L92–114）
    /// **逐值一致**（CSS 注释要求）。浅色块没有覆盖的 token（--wave）沿用深色基准。
    /// </summary>
    private static readonly Dictionary<string, Color> LightOverrides = new()
    {
        ["--deep"] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),           // #fff
        ["--fg"] = Color.FromArgb(0xFF, 0x11, 0x11, 0x13),             // #111113
        ["--stroke"] = Color.FromArgb(0x1F, 0x11, 0x11, 0x13),        // rgba(17,17,19,.12)
        ["--err-bg"] = Color.FromArgb(0xFF, 0xFE, 0xF2, 0xF2),         // #fef2f2
        ["--err-stroke"] = Color.FromArgb(0x59, 0xE5, 0x48, 0x4D),    // rgba(229,72,77,.35)
        ["--err-fg"] = Color.FromArgb(0xFF, 0xB9, 0x1C, 0x1C),        // #b91c1c
        ["--err-icon"] = Color.FromArgb(0xFF, 0xB9, 0x1C, 0x1C),      // #b91c1c
        ["--warn-bg"] = Color.FromArgb(0xFF, 0xFF, 0xFB, 0xEB),       // #fffbeb
        ["--warn-stroke"] = Color.FromArgb(0x59, 0xD9, 0x77, 0x06),   // rgba(217,119,6,.35)
        ["--warn-fg"] = Color.FromArgb(0xFF, 0xB4, 0x53, 0x09),       // #b45309
        ["--warn-icon"] = Color.FromArgb(0xFF, 0xB4, 0x53, 0x09),     // #b45309
        ["--cancel-btn"] = Color.FromArgb(0xFF, 0xEC, 0xEC, 0xEE),    // #ececee
        ["--cancel-btn-hover"] = Color.FromArgb(0xFF, 0xE4, 0xE4, 0xE7), // #e4e4e7
        ["--state-bg"] = Color.FromArgb(0x14, 0x11, 0x11, 0x13),      // rgba(17,17,19,.08)
        ["--wave-fg"] = Color.FromArgb(0xFF, 0x11, 0x11, 0x13),       // #111113
        ["--wave-dim"] = Color.FromArgb(0x38, 0x11, 0x11, 0x13),      // rgba(17,17,19,.22)
        ["--dim"] = Color.FromArgb(0x73, 0x11, 0x11, 0x13),           // rgba(17,17,19,.45)
        ["--dim2"] = Color.FromArgb(0x99, 0x11, 0x11, 0x13),          // rgba(17,17,19,.6)
        ["--dim3"] = Color.FromArgb(0x8C, 0x11, 0x11, 0x13),          // rgba(17,17,19,.55)
    };

    /// <summary>
    /// CSS 的 <c>ease</c> = cubic-bezier(0.25, 0.1, 0.25, 1)。
    ///
    /// ⚠ Avalonia 11.3 把 <c>CubicBezierEasing</c> 标记为过时（"Use SplineEasing instead"），
    /// <see cref="SplineEasing"/> 是它的替代：同样是 CSS 的 cubic-bezier 语义
    /// （X 轴是时间、Y 轴是进度），四个控制点参数一一对应。
    /// </summary>
    public static readonly SplineEasing CssEase = new(0.25, 0.1, 0.25, 1);

    /// <summary>CSS 的 <c>ease-out</c> = cubic-bezier(0, 0, 0.58, 1)。</summary>
    public static readonly SplineEasing CssEaseOut = new(0, 0, 0.58, 1);

    /// <summary>形变用的曲线：capsule.css 的 <c>cubic-bezier(.25,.85,.3,1)</c>。</summary>
    public static readonly SplineEasing MorphEase = new(0.25, 0.85, 0.3, 1);

    /// <summary>
    /// 颜色过渡时长（毫秒）。来源：capsule.css 的
    /// <c>.recording-pill { transition: ... background-color .18s ease-out, border-color .18s ease-out, color .18s ease-out }</c>。
    /// </summary>
    public const int ColorTransitionMs = 180;

    /// <summary>深色基准 palette（键是 CSS 变量名）。</summary>
    public static IReadOnlyDictionary<string, Color> DarkPalette { get; } = DarkTokens;

    /// <summary>浅色 palette（键是 CSS 变量名）= 深色基准 + 浅色块覆盖。</summary>
    public static IReadOnlyDictionary<string, Color> LightPalette { get; } = BuildLightPalette();

    /// <summary>CSS 变量名 → Avalonia 资源键。</summary>
    public static IReadOnlyDictionary<string, string> ResourceKeyByToken { get; } = BuildTokenMap();

    /// <summary>当前档位。</summary>
    public static CapsuleThemeMode Current { get; private set; } = CapsuleThemeMode.Auto;

    /// <summary>主题已应用（补间已启动）时触发。窗口据此重算派生色。</summary>
    public static event EventHandler? Applied;

    private static bool _platformHooked;

    /// <summary>
    /// 系统主题变化的回调。Avalonia 11 的事件签名是
    /// <c>EventHandler&lt;PlatformColorValues&gt;</c>（参数就是新的颜色值本身）。
    /// 存成字段是为了能正确退订 —— lambda 每次都是新实例，退订不掉。
    /// </summary>
    private static readonly EventHandler<PlatformColorValues> PlatformHandler = (_, _) => OnColorValuesChanged();

    /// <summary>当前实际生效的 palette（Auto 档按系统主题解析）。</summary>
    public static IReadOnlyDictionary<string, Color> CurrentPalette =>
        Current == CapsuleThemeMode.Light ? LightPalette
        : Current == CapsuleThemeMode.Dark ? DarkPalette
        : (IsSystemDark() ? DarkPalette : LightPalette);

    /// <summary>
    /// 订阅系统主题变化。"跟随系统"档要跟着系统走 —— CSS 里靠媒体查询，
    /// Avalonia 里靠 <see cref="IPlatformSettings.ColorValuesChanged"/>。
    /// </summary>
    public static void AttachPlatformWatcher()
    {
        if (_platformHooked)
        {
            return;
        }

        var platform = Application.Current?.PlatformSettings;
        if (platform is null)
        {
            return;
        }

        platform.ColorValuesChanged += PlatformHandler;
        _platformHooked = true;
    }

    /// <summary>取消订阅系统主题变化。</summary>
    public static void DetachPlatformWatcher()
    {
        var platform = Application.Current?.PlatformSettings;
        if (platform is not null && _platformHooked)
        {
            platform.ColorValuesChanged -= PlatformHandler;
        }

        _platformHooked = false;
    }

    /// <summary>
    /// 应用主题档位：逐个令牌补间到目标色，再通知外部重算派生色。
    /// </summary>
    /// <param name="mode">档位。</param>
    /// <param name="duration">补间时长；null = <see cref="ColorTransitionMs"/> 毫秒。</param>
    public static void Apply(CapsuleThemeMode mode, TimeSpan? duration = null)
    {
        Current = mode;

        var palette = CurrentPalette;
        var span = duration ?? TimeSpan.FromMilliseconds(ColorTransitionMs);

        foreach (var (token, key) in TokenMap)
        {
            if (!palette.TryGetValue(token, out var target))
            {
                continue;
            }

            // 就地改 brush.Color（不替换实例）—— 替换实例会瞬间跳色，
            // 而且控件上挂的 {DynamicResource} 引用会跟着断。
            OverlayTheme.TweenBrush(key, target, span, t => CssEaseOut.Ease(t));
        }

        Applied?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 把字符串档位解析成枚举（对应 capsule.js 的 <c>THEMES</c>）。
    /// 认不出来就当 auto（CSS 里 data-theme 只认 light / dark，其余都走跟随系统）。
    /// </summary>
    public static CapsuleThemeMode ParseMode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => CapsuleThemeMode.Light,
        "dark" => CapsuleThemeMode.Dark,
        _ => CapsuleThemeMode.Auto,
    };

    /// <summary>
    /// 主题档位循环：黑夜 → 白天 → 跟随系统 → 黑夜。
    /// （demo 的验收顺序：先看两套固定主题，再看"跟随系统"跟不跟得上。）
    /// </summary>
    public static CapsuleThemeMode CycleNext() => Current switch
    {
        CapsuleThemeMode.Auto => CapsuleThemeMode.Dark,
        CapsuleThemeMode.Dark => CapsuleThemeMode.Light,
        _ => CapsuleThemeMode.Auto,
    };

    /// <summary>系统当前是不是深色。读 Avalonia 的 PlatformSettings 主题变体。</summary>
    public static bool IsSystemDark()
    {
        var variant = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
        return variant != PlatformThemeVariant.Light;
    }

    /// <summary>取某个令牌的当前 brush 颜色（自检 / 调试用）。</summary>
    public static bool TryGetBrushColor(string key, out Color color)
        => OverlayTheme.TryGetBrushColor(key, out color);

    private static void OnColorValuesChanged()
    {
        // 只有"跟随系统"档才跟着系统变；强制档不动。
        if (Current == CapsuleThemeMode.Auto)
        {
            Apply(CapsuleThemeMode.Auto);
        }
    }

    private static Dictionary<string, Color> BuildLightPalette()
    {
        var palette = new Dictionary<string, Color>(DarkTokens);
        foreach (var (token, color) in LightOverrides)
        {
            palette[token] = color;
        }

        return palette;
    }

    private static Dictionary<string, string> BuildTokenMap()
    {
        var map = new Dictionary<string, string>();
        foreach (var (token, key) in TokenMap)
        {
            map[token] = key;
        }

        return map;
    }
}
