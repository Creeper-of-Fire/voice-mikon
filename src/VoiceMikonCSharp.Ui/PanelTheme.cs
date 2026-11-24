using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Platform;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.Ui;

/// <summary>
/// 面板的主题档位。名字与 panel.js 的 <c>THEMES = ['auto', 'light', 'dark']</c> 对齐：
/// auto = 跟随系统，light = 白天，dark = 黑夜。
/// </summary>
public enum PanelThemeMode
{
    /// <summary>跟随系统。</summary>
    Auto,

    /// <summary>强制白天（对应 CSS 的 <c>:root</c>）。</summary>
    Light,

    /// <summary>强制黑夜（对应 CSS 的 <c>:root[data-theme="dark"]</c>）。</summary>
    Dark,
}

/// <summary>
/// 面板的主题：两张 palette + 三档切换。
///
/// <para>色值来源与唯一真相：<c>voice-mikon/src/voice_mikon/host/static/panel.css</c>。
/// ⚠ 与胶囊那套**方向相反**：panel.css 的 <c>:root</c> 是**浅色**，
/// 深色在 <c>:root[data-theme="dark"]</c> 与
/// <c>@media(prefers-color-scheme: dark) :root:not([data-theme="light"])</c> 两处，
/// CSS 注释明确写了"两处的 token 值必须一致（改一处就要改另一处）"，
/// 所以这里只有**一张**深色 palette。</para>
///
/// <para>为什么不用 Avalonia 的 <c>ThemeDictionaries</c>：它是**替换 brush 实例**，
/// 切主题会瞬间跳色；而 panel.css 的 <c>.seg-item { transition: background 0.15s }</c>
/// 要求平滑过渡。所以这里走 <see cref="OverlayTheme.TweenBrush"/>：就地改 brush.Color，
/// 控件引用的实例不变，动画才有得补。</para>
///
/// <para>类名不能叫 <c>Theme</c>：Avalonia 11.x 给 <c>StyledElement</c> 加了同名实例属性
/// <c>Theme</c>（类型 ControlTheme），在窗口子类里写 <c>Theme.Toggle()</c> 会解析到那个属性上
/// （archive 意外发现 #1）。</para>
/// </summary>
public static class PanelTheme
{
    /// <summary>CSS 变量名 → Avalonia 资源键。palette 与资源字典靠这张表对齐。</summary>
    private static readonly (string Token, string Key)[] TokenMap =
    {
        ("--bg", "Panel.Bg.Brush"),
        ("--card", "Panel.Card.Brush"),
        ("--nav", "Panel.Nav.Brush"),
        ("--hover", "Panel.Hover.Brush"),
        ("--text", "Panel.Text.Brush"),
        ("--text2", "Panel.Text2.Brush"),
        ("--muted", "Panel.Muted.Brush"),
        ("--line", "Panel.Line.Brush"),
        ("--accent", "Panel.Accent.Brush"),
        ("--danger", "Panel.Danger.Brush"),
        ("--danger-soft", "Panel.DangerSoft.Brush"),
        ("--warn", "Panel.Warn.Brush"),
        ("--warn-soft", "Panel.WarnSoft.Brush"),
        ("--k-ask", "Panel.Ask.Brush"),
        ("--k-ask-b", "Panel.AskB.Brush"),
        ("--k-trans", "Panel.Trans.Brush"),
        ("--k-trans-b", "Panel.TransB.Brush"),
        ("--halo-ok", "Panel.HaloOk.Brush"),
        ("--halo-warn", "Panel.HaloWarn.Brush"),
        ("--halo-rec", "Panel.HaloRec.Brush"),
        ("--halo-processing", "Panel.HaloProcessing.Brush"),
    };

    /// <summary>浅色基准：panel.css 的 <c>:root { ... }</c>（L40-82）。</summary>
    private static readonly Dictionary<string, Color> LightTokens = new()
    {
        ["--bg"] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),           // #fff
        ["--card"] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),         // #fff
        ["--nav"] = Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5),          // #f4f4f5
        ["--hover"] = Color.FromArgb(0xFF, 0xEC, 0xEC, 0xEE),        // #ececee
        ["--text"] = Color.FromArgb(0xFF, 0x11, 0x11, 0x13),         // #111113
        ["--text2"] = Color.FromArgb(0xFF, 0x3F, 0x3F, 0x46),        // #3f3f46
        ["--muted"] = Color.FromArgb(0xFF, 0x8B, 0x8B, 0x93),        // #8b8b93
        ["--line"] = Color.FromArgb(0xFF, 0xEC, 0xEC, 0xEE),         // #ececee
        ["--accent"] = Color.FromArgb(0xFF, 0x11, 0x11, 0x13),      // #111113
        ["--danger"] = Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D),      // #e5484d
        ["--danger-soft"] = Color.FromArgb(0xFF, 0xFD, 0xEE, 0xEE), // #fdeeee
        ["--warn"] = Color.FromArgb(0xFF, 0xB7, 0x79, 0x1F),        // #b7791f
        ["--warn-soft"] = Color.FromArgb(0xFF, 0xFD, 0xF5, 0xE6),   // #fdf5e6
        ["--k-ask"] = Color.FromArgb(0xFF, 0xE8, 0xF7, 0xEE),       // #e8f7ee
        ["--k-ask-b"] = Color.FromArgb(0xFF, 0x1D, 0x7A, 0x45),     // #1d7a45
        ["--k-trans"] = Color.FromArgb(0xFF, 0xEA, 0xF0, 0xFF),     // #eaf0ff
        ["--k-trans-b"] = Color.FromArgb(0xFF, 0x2B, 0x4F, 0xBF),   // #2b4fbf
        ["--halo-ok"] = Color.FromArgb(0xFF, 0xE8, 0xF7, 0xEE),     // var(--k-ask)
        ["--halo-warn"] = Color.FromArgb(0xFF, 0xFD, 0xF5, 0xE6),   // var(--warn-soft)
        ["--halo-rec"] = Color.FromArgb(0xFF, 0xFD, 0xEE, 0xEE),    // var(--danger-soft)
        ["--halo-processing"] = Color.FromArgb(0xFF, 0xEA, 0xF0, 0xFF), // var(--k-trans)
    };

    /// <summary>
    /// 深色：panel.css 的 <c>:root[data-theme="dark"] { ... }</c>（L120-140），
    /// 与 <c>@media (prefers-color-scheme: dark) :root:not([data-theme="light"]) { ... }</c>
    /// （L95-117）**逐值一致**（CSS 注释要求）。
    /// </summary>
    private static readonly Dictionary<string, Color> DarkTokens = new()
    {
        ["--bg"] = Color.FromArgb(0xFF, 0x0F, 0x0F, 0x11),          // #0f0f11
        ["--card"] = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1D),        // #1a1a1d
        ["--nav"] = Color.FromArgb(0xFF, 0x18, 0x18, 0x1B),          // #18181b
        ["--hover"] = Color.FromArgb(0xFF, 0x27, 0x27, 0x2B),       // #27272b
        ["--text"] = Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5),        // #f4f4f5
        ["--text2"] = Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD8),       // #d4d4d8
        ["--muted"] = Color.FromArgb(0xFF, 0x8B, 0x8B, 0x93),       // #8b8b93
        ["--line"] = Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2F),        // #2a2a2f
        ["--accent"] = Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5),      // #f4f4f5
        ["--danger"] = Color.FromArgb(0xFF, 0xFF, 0x7B, 0x7F),      // #ff7b7f
        ["--danger-soft"] = Color.FromArgb(0xFF, 0x3A, 0x1C, 0x1E), // #3a1c1e
        ["--warn"] = Color.FromArgb(0xFF, 0xE3, 0xB2, 0x5E),        // #e3b25e
        ["--warn-soft"] = Color.FromArgb(0xFF, 0x33, 0x28, 0x1A),   // #33281a
        ["--k-ask"] = Color.FromArgb(0xFF, 0x15, 0x29, 0x1E),       // #15291e
        ["--k-ask-b"] = Color.FromArgb(0xFF, 0x5F, 0xD0, 0x8F),     // #5fd08f
        ["--k-trans"] = Color.FromArgb(0xFF, 0x1B, 0x24, 0x40),     // #1b2440
        ["--k-trans-b"] = Color.FromArgb(0xFF, 0x8A, 0xA6, 0xFF),   // #8aa6ff
        ["--halo-ok"] = Color.FromArgb(0xFF, 0x15, 0x29, 0x1E),     // var(--k-ask)
        ["--halo-warn"] = Color.FromArgb(0xFF, 0x33, 0x28, 0x1A),   // var(--warn-soft)
        ["--halo-rec"] = Color.FromArgb(0xFF, 0x3A, 0x1C, 0x1E),    // var(--danger-soft)
        ["--halo-processing"] = Color.FromArgb(0xFF, 0x1B, 0x24, 0x40), // var(--k-trans)
    };

    /// <summary>
    /// CSS 的 <c>ease</c> = cubic-bezier(0.25, 0.1, 0.25, 1)。
    ///
    /// <para>⚠ Avalonia 11.3 把 <c>CubicBezierEasing</c> 标记为过时（"Use SplineEasing instead"），
    /// <see cref="SplineEasing"/> 是它的替代。</para>
    /// </summary>
    public static readonly SplineEasing CssEase = new(0.25, 0.1, 0.25, 1);

    /// <summary>CSS 的 <c>ease-out</c> = cubic-bezier(0, 0, 0.58, 1)。</summary>
    public static readonly SplineEasing CssEaseOut = new(0, 0, 0.58, 1);

    /// <summary>
    /// 颜色过渡时长（毫秒）。
    /// 来源：panel.css 的 <c>.seg-item { transition: background 0.15s, color 0.15s, box-shadow 0.15s }</c>。
    /// </summary>
    public const int ColorTransitionMs = 150;

    /// <summary>
    /// 浅色阴影：panel.css 的 <c>--shadow: 0 1px 2px rgba(17,17,19,.04), 0 6px 24px rgba(17,17,19,.06)</c>。
    ///
    /// <para>⚠ Avalonia 的 <c>BoxShadows.Parse</c> **不吃 px 单位**（实测：
    /// "0 1px 2px #AARRGGBB" 直接抛 FormatException），所以这里写纯数字，
    /// 顺序是 offsetX offsetY blur spread color。alpha 按 CSS 规则
    /// round(a × 255) 换算（0.04 → 10、0.06 → 15）。</para>
    /// </summary>
    private const string LightShadowText = "0 1 2 0 #0A111113, 0 6 24 0 #0F111113";

    /// <summary>
    /// 深色阴影：panel.css 的 <c>--shadow: 0 1px 2px rgba(0,0,0,.4), 0 6px 24px rgba(0,0,0,.3)</c>。
    /// </summary>
    private const string DarkShadowText = "0 1 2 0 #66000000, 0 6 24 0 #4D000000";

    /// <summary>浅色 palette（键是 CSS 变量名）。</summary>
    public static IReadOnlyDictionary<string, Color> LightPalette { get; } = LightTokens;

    /// <summary>深色 palette（键是 CSS 变量名）。</summary>
    public static IReadOnlyDictionary<string, Color> DarkPalette { get; } = DarkTokens;

    /// <summary>CSS 变量名 → Avalonia 资源键。</summary>
    public static IReadOnlyDictionary<string, string> ResourceKeyByToken { get; } = BuildTokenMap();

    /// <summary>当前档位。</summary>
    public static PanelThemeMode Current { get; private set; } = PanelThemeMode.Auto;

    /// <summary>当前生效的卡片阴影（切档时换；CSS 的 --shadow）。</summary>
    public static BoxShadows CurrentShadow { get; private set; } = BoxShadows.Parse(LightShadowText);

    /// <summary>主题已应用（补间已启动）时触发。窗口据此重算依赖颜色的元素。</summary>
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
        Current == PanelThemeMode.Light ? LightPalette
        : Current == PanelThemeMode.Dark ? DarkPalette
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

        var platform = Avalonia.Application.Current?.PlatformSettings;
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
        var platform = Avalonia.Application.Current?.PlatformSettings;
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
    public static void Apply(PanelThemeMode mode, TimeSpan? duration = null)
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
            OverlayTheme.TweenBrush(key, target, span, t => CssEase.Ease(t));
        }

        // 阴影不是 brush（BoxShadows 是结构体），补间不了 —— 直接换。
        // Auto 档跟着系统走（与 palette 的解析口径一致）。
        bool dark = mode == PanelThemeMode.Dark
            || (mode == PanelThemeMode.Auto && IsSystemDark());
        CurrentShadow = BoxShadows.Parse(dark ? DarkShadowText : LightShadowText);

        Applied?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 把字符串档位解析成枚举（对应 panel.js 的 <c>THEMES</c>）。
    /// 认不出来就当 auto。
    /// </summary>
    public static PanelThemeMode ParseMode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => PanelThemeMode.Light,
        "dark" => PanelThemeMode.Dark,
        _ => PanelThemeMode.Auto,
    };

    /// <summary>
    /// 系统当前是不是深色。
    ///
    /// <para>复用 <see cref="CapsuleTheme.IsSystemDark"/> —— 读 Avalonia 的
    /// PlatformSettings 主题变体，两个窗口读的是同一份系统事实，
    /// 不必各写一套。</para>
    /// </summary>
    public static bool IsSystemDark() => CapsuleTheme.IsSystemDark();

    /// <summary>取某个令牌的当前 brush 颜色（自检 / 调试用）。</summary>
    public static bool TryGetBrushColor(string key, out Color color)
        => OverlayTheme.TryGetBrushColor(key, out color);

    private static void OnColorValuesChanged()
    {
        // 只有"跟随系统"档才跟着系统变；强制档不动。
        if (Current == PanelThemeMode.Auto)
        {
            Apply(PanelThemeMode.Auto);
        }
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
