using System.Globalization;
using VoiceMikonCSharp.Ui;

namespace VoiceMikonCSharp.TokenCheck;

/// <summary>
/// 令牌核对：读 capsule.css 的三个 token 块，和 <see cref="CapsuleTheme"/> 的两张 palette 逐一对比。
///
/// <para>核对三件事：</para>
/// <list type="number">
/// <item>深色基准：CSS 的 <c>:root { ... }</c> ↔ <see cref="CapsuleTheme.DarkPalette"/></item>
/// <item>浅色：CSS 的 <c>:root[data-theme="light"] { ... }</c> 与
/// <c>@media (prefers-color-scheme: light) :root:not([data-theme="dark"]) { ... }</c>
/// **必须彼此一致**（CSS 注释明确要求："改一处就要改另一处"），
/// 且合并到深色基准之后的值 ↔ <see cref="CapsuleTheme.LightPalette"/></item>
/// <item>键集合必须完全一致（不能少 token，也不能多 token）</item>
/// </list>
///
/// <para>任何一项不一致 → 退出码非 0。</para>
/// </summary>
internal static class Program
{
    private const string DarkSelector = @"^:root\s*\{";
    private const string LightMediaSelector = @":root:not\(\[data-theme=""dark""\]\)\s*\{";
    private const string LightForcedSelector = @":root\[data-theme=""light""\]\s*\{";

    private static int _failures;

    private static int Main(string[] args)
    {
        string? cssPath = args.Length > 0 ? args[0] : FindCssFile();

        if (cssPath is null || !File.Exists(cssPath))
        {
            Console.Error.WriteLine("找不到 capsule.css。");
            Console.Error.WriteLine("用法：VoiceMikonCSharp.TokenCheck [capsule.css 的路径]");
            return 2;
        }

        Console.WriteLine($"capsule.css : {cssPath}");
        Console.WriteLine();

        string raw = File.ReadAllText(cssPath);
        string css = CssTokenReader.StripComments(raw);

        // ---- 解析三个块 ----
        string? darkBlock = CssTokenReader.ExtractBlock(css, DarkSelector);
        string? lightMediaBlock = CssTokenReader.ExtractBlock(css, LightMediaSelector);
        string? lightForcedBlock = CssTokenReader.ExtractBlock(css, LightForcedSelector);

        if (darkBlock is null)
        {
            Console.Error.WriteLine("✗ 找不到 :root { ... } 块");
            return 2;
        }

        if (lightMediaBlock is null)
        {
            Console.Error.WriteLine("✗ 找不到 @media (prefers-color-scheme: light) 里的 :root:not([data-theme=\"dark\"]) 块");
            return 2;
        }

        if (lightForcedBlock is null)
        {
            Console.Error.WriteLine("✗ 找不到 :root[data-theme=\"light\"] 块");
            return 2;
        }

        var darkTokens = CssTokenReader.ParseTokens(darkBlock);
        var lightMediaTokens = CssTokenReader.ParseTokens(lightMediaBlock);
        var lightForcedTokens = CssTokenReader.ParseTokens(lightForcedBlock);

        // ---- 检查 1：两处浅色块必须一致 ----
        Console.WriteLine("── 检查 1：两处浅色 token 是否一致 ──────────────────────────────");
        CompareTokenSets("媒体查询", lightMediaTokens, "强制白天", lightForcedTokens);
        Console.WriteLine();

        // ---- 期望的浅色 palette = 深色基准 + 浅色块覆盖 ----
        var expectedLight = new Dictionary<string, CssTokenReader.Token>(darkTokens, StringComparer.Ordinal);
        foreach (var (name, token) in lightMediaTokens)
        {
            expectedLight[name] = token;
        }

        // ---- 检查 2：深色基准 ----
        Console.WriteLine("── 检查 2：深色基准（CSS :root ↔ CapsuleTheme.DarkPalette）────────────");
        CompareWithPalette("深色", darkTokens, CapsuleTheme.DarkPalette);
        Console.WriteLine();

        // ---- 检查 3：浅色 ----
        Console.WriteLine("── 检查 3：浅色（CSS 浅色块 ↔ CapsuleTheme.LightPalette）──────────────");
        CompareWithPalette("浅色", expectedLight, CapsuleTheme.LightPalette);
        Console.WriteLine();

        // ---- 结论 ----
        if (_failures == 0)
        {
            Console.WriteLine("✓ 全部一致：两张 palette 与 capsule.css 完全对得上。");
            return 0;
        }

        Console.WriteLine($"✗ 共 {_failures} 处不一致。");
        return 1;
    }

    /// <summary>比较两个 token 集合（用于检查两处浅色块是否一致）。</summary>
    private static void CompareTokenSets(
        string leftName,
        Dictionary<string, CssTokenReader.Token> left,
        string rightName,
        Dictionary<string, CssTokenReader.Token> right)
    {
        var names = new SortedSet<string>(left.Keys, StringComparer.Ordinal);
        names.UnionWith(right.Keys);

        if (names.Count == 0)
        {
            Console.WriteLine("  （两边都没有 token）");
            return;
        }

        foreach (string name in names)
        {
            bool hasLeft = left.TryGetValue(name, out var l);
            bool hasRight = right.TryGetValue(name, out var r);

            if (!hasLeft || !hasRight)
            {
                _failures++;
                Console.WriteLine($"  ✗ {name}：只在 {(hasLeft ? leftName : rightName)} 里有");
                continue;
            }

            bool same = l.RawValue == r.RawValue && l.Argb == r.Argb;
            if (!same)
            {
                _failures++;
                Console.WriteLine($"  ✗ {name}：{leftName} = {l.RawValue}（{CssTokenReader.ToHex(l.Argb)}）"
                                 + $" vs {rightName} = {r.RawValue}（{CssTokenReader.ToHex(r.Argb)}）");
            }
            else
            {
                Console.WriteLine($"  ✓ {name}：两处一致（{l.RawValue} → {CssTokenReader.ToHex(l.Argb)}）");
            }
        }
    }

    /// <summary>比较 CSS token 集合与 palette。</summary>
    private static void CompareWithPalette(
        string label,
        Dictionary<string, CssTokenReader.Token> cssTokens,
        IReadOnlyDictionary<string, Avalonia.Media.Color> palette)
    {
        var names = new SortedSet<string>(cssTokens.Keys, StringComparer.Ordinal);
        names.UnionWith(palette.Keys);

        foreach (string name in names)
        {
            bool hasCss = cssTokens.TryGetValue(name, out var cssToken);
            bool hasPalette = palette.TryGetValue(name, out var color);

            if (!hasCss)
            {
                _failures++;
                Console.WriteLine($"  ✗ {name}：palette 里有，CSS 里没有（多余 token）");
                continue;
            }

            if (!hasPalette)
            {
                _failures++;
                Console.WriteLine($"  ✗ {name}：CSS 里有，palette 里没有（漏了 token）");
                continue;
            }

            uint expected = cssToken.Argb;
            // Avalonia 的 Color 是 A/R/G/B 四个字节，自己拼成 0xAARRGGBB
            uint actual = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

            if (expected != actual)
            {
                _failures++;
                Console.WriteLine($"  ✗ {name}：CSS {cssToken.RawValue} → {CssTokenReader.ToHex(expected)}"
                                 + $" 但 palette 是 {CssTokenReader.ToHex(actual)}");
            }
            else
            {
                Console.WriteLine($"  ✓ {name}：{cssToken.RawValue} → {CssTokenReader.ToHex(expected)}");
            }
        }
    }

    /// <summary>
    /// 定位 capsule.css：从程序集所在目录往上找 <c>voice-mikon/src/voice_mikon/host/static/capsule.css</c>。
    /// </summary>
    private static string? FindCssFile()
    {
        string relative = Path.Combine("voice-mikon", "src", "voice_mikon", "host", "static", "capsule.css");

        var roots = new List<string> { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };

        foreach (string root in roots)
        {
            var dir = new DirectoryInfo(root);
            for (int i = 0; i < 8 && dir is not null; i++)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                // 也看看当前目录树里有没有别的同名文件
                dir = dir.Parent;
            }
        }

        return null;
    }
}
