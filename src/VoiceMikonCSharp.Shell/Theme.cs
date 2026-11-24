using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VoiceMikonCSharp.Shell;

/// <summary>
/// 主题机制的壳：**只含机制，不含色值**。
///
/// 正式版的色值与三档逻辑（跟随系统 / 白天 / 黑夜）从 voice-mikon 的 capsule.css
/// 翻译过来：:root 里的 token → ResourceDictionary 的 key，两套 palette 的值
/// 一字不改，媒体查询 prefers-color-scheme 对应"跟随系统"档。
/// 在那之前这个类不持有任何颜色，也不决定有哪些主题档位。
///
/// 两条机制从验证壳保留（都是实测下来的经验）：
/// · 运行时切色**就地改 brush.Color，不替换 brush 实例**——控件引用的是实例，
///   替换实例会瞬间跳色，就地改才能走补间。
/// · 补间自己写而不用 Avalonia 的 ColorTransition——避免在 NativeAOT 下
///   对 Animatable.Transitions 的裁剪行为产生依赖（验证壳 AOT 发布通过）。
///
/// 类名不能叫 Theme：Avalonia 11.x 给 StyledElement 加了同名实例属性（ControlTheme），
/// 在窗口子类里写 Theme.Xxx() 会解析到那个属性上。
/// </summary>
public static class OverlayTheme
{
    /// <summary>在应用资源树里找 brush（含合并字典）。找不到返回 null。</summary>
    public static SolidColorBrush? FindBrush(string key)
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

    /// <summary>读某个令牌当前的 brush 颜色（确认资源存在、切换生效）。找不到返回 false。</summary>
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

    /// <summary>
    /// 把某个令牌的 brush 颜色补间到目标色（就地改 Color，不换实例）。
    /// 时长和缓动按 capsule.css 对应的 transition 来——例如 .recording-pill 的
    /// background-color 是 .18s ease-out，那就传 180ms 和 CSS ease-out 对应的曲线。
    /// 这个方法本身不假设任何时长和缓动。
    /// </summary>
    /// <param name="key">ResourceDictionary 里的 brush 资源键。</param>
    /// <param name="target">目标颜色。</param>
    /// <param name="duration">补间时长；TimeSpan.Zero 表示直接跳。</param>
    /// <param name="easing">缓动函数，输入输出都是 0..1；null 表示线性。</param>
    public static void TweenBrush(string key, Color target, TimeSpan duration, Func<double, double>? easing = null)
    {
        var brush = FindBrush(key);
        if (brush is null)
        {
            return;
        }

        Tween(brush, target, duration, easing);
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
    private static void Tween(SolidColorBrush brush, Color target, TimeSpan duration, Func<double, double>? easing)
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
            double eased = easing is null ? t : easing(t);

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
