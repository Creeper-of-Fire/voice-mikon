using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VmNext.Shell;

/// <summary>主题（深/浅）。</summary>
public enum ThemeMode
{
    /// <summary>深色主题。</summary>
    Dark,

    /// <summary>浅色主题。</summary>
    Light,
}

/// <summary>
/// 主题令牌与运行时切换。
///
/// 注意类名：不能叫 <c>Theme</c> —— Avalonia 11.1 给 <c>StyledElement</c> 加了同名实例属性
/// <c>Theme</c>（类型 ControlTheme），在窗口子类里写 <c>Theme.Toggle()</c> 会解析到那个属性上。
///
/// 实现约定：
/// · 颜色一律放在 <c>Themes/Overlay.axaml</c> 的 ResourceDictionary 里，控件用 {DynamicResource} 引用；
/// · 运行时切换**不替换 brush 实例**，而是就地改 brush.Color，并用一个 260ms 的补间动画过渡，
///   这样颜色变化是平滑的（替换实例会瞬间跳色）；
/// · 之所以用自己写的补间而不用 Avalonia 的 ColorTransition，是为了避免在
///   NativeAOT 下对 Animatable.Transitions 的裁剪行为产生额外依赖。
/// </summary>
public static class OverlayTheme
{
    /// <summary>主色（球体 / 波形条）。</summary>
    public const string AccentKey = "Overlay.Accent.Brush";

    /// <summary>表面色（胶囊底板）。</summary>
    public const string SurfaceKey = "Overlay.Surface.Brush";

    /// <summary>前景文字色。</summary>
    public const string ForegroundKey = "Overlay.Foreground.Brush";

    /// <summary>描边色。</summary>
    public const string BorderKey = "Overlay.Border.Brush";

    /// <summary>波形条颜色。</summary>
    public const string WaveKey = "Overlay.Wave.Brush";

    /// <summary>右键菜单底色。</summary>
    public const string MenuKey = "Overlay.Menu.Brush";

    /// <summary>当前主题。</summary>
    public static ThemeMode Current { get; private set; } = ThemeMode.Dark;

    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        [AccentKey] = Color.FromRgb(0x3B, 0x6E, 0xF6),
        [SurfaceKey] = Color.FromArgb(0xE6, 0x14, 0x1A, 0x22),
        [ForegroundKey] = Color.FromRgb(0xF2, 0xF5, 0xFA),
        [BorderKey] = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
        [WaveKey] = Color.FromRgb(0x7F, 0xD1, 0xFF),
        [MenuKey] = Color.FromArgb(0xF0, 0x1B, 0x20, 0x29),
    };

    private static readonly Dictionary<string, Color> LightPalette = new()
    {
        [AccentKey] = Color.FromRgb(0x1E, 0x63, 0xE9),
        [SurfaceKey] = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF),
        [ForegroundKey] = Color.FromRgb(0x18, 0x1C, 0x22),
        [BorderKey] = Color.FromArgb(0x33, 0x00, 0x00, 0x00),
        [WaveKey] = Color.FromRgb(0x11, 0x7A, 0xC9),
        [MenuKey] = Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF),
    };

    /// <summary>切换主题（深 ↔ 浅）。</summary>
    public static void Toggle() => Apply(Current == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark);

    /// <summary>
    /// 读某个令牌当前的 brush 颜色（自检用：确认资源确实被找到、主题确实切过去了）。
    /// </summary>
    public static bool TryGetBrushColor(string key, out Color color)
    {
        color = default;
        var brush = FindBrush(key);
        if (brush is null)
        {
            return false;
        }

        color = brush.Color;
        return true;
    }

    /// <summary>应用主题，带 260ms 的颜色过渡。</summary>
    public static void Apply(ThemeMode mode, TimeSpan? duration = null)
    {
        Current = mode;
        var palette = mode == ThemeMode.Dark ? DarkPalette : LightPalette;

        foreach (var (key, target) in palette)
        {
            var brush = FindBrush(key);
            if (brush is null)
            {
                continue;
            }

            Tween(brush, target, duration ?? TimeSpan.FromMilliseconds(260));
        }
    }

    /// <summary>在应用资源树里找 brush（含合并字典）。</summary>
    private static SolidColorBrush? FindBrush(string key)
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        // Application.Resources 的静态类型是 IResourceProvider，具体实现是 ResourceDictionary（实现了字典索引器）
        if (app.Resources is ResourceDictionary resources && resources[key] is SolidColorBrush direct)
        {
            return direct;
        }

        return SearchMerged(app.Resources as ResourceDictionary, key);
    }

    /// <summary>递归在合并字典里找 brush（ResourceInclude 进来的资源在这里）。</summary>
    private static SolidColorBrush? SearchMerged(ResourceDictionary? dictionary, string key)
    {
        if (dictionary is null)
        {
            return null;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (merged is ResourceDictionary mergedDictionary)
            {
                if (mergedDictionary[key] is SolidColorBrush brush)
                {
                    return brush;
                }

                var nested = SearchMerged(mergedDictionary, key);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>颜色补间：就地改 brush.Color，过渡期间不新建 brush（控件引用不会断）。</summary>
    private static void Tween(SolidColorBrush brush, Color target, TimeSpan duration)
    {
        Color start = brush.Color;
        if (start == target || duration <= TimeSpan.Zero)
        {
            brush.Color = target;
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        void Step()
        {
            double t = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic

            brush.Color = Color.FromArgb(
                (byte)Math.Round(start.A + (target.A - start.A) * eased),
                (byte)Math.Round(start.R + (target.R - start.R) * eased),
                (byte)Math.Round(start.G + (target.G - start.G) * eased),
                (byte)Math.Round(start.B + (target.B - start.B) * eased));

            if (t < 1.0)
            {
                Dispatcher.UIThread.Post(Step, DispatcherPriority.Render);
            }
        }

        Dispatcher.UIThread.Post(Step, DispatcherPriority.Render);
    }
}
