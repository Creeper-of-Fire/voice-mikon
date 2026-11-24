using System.Globalization;
using System.Text.RegularExpressions;

namespace VoiceMikonCSharp.TokenCheck;

/// <summary>
/// 解析 capsule.css 里的 token 声明。
///
/// <para>只认三种颜色格式（capsule.css 里就这三种）：
/// <c>#rrggbb</c>、<c>#rrggbbaa</c>、<c>rgba(r,g,b,a)</c>。</para>
///
/// <para>⚠ <b>CSS 的 8 位十六进制是 #RRGGBBAA —— alpha 在最后。</b>
/// 这不是 ARGB（ARGB 是 Avalonia / .NET 的写法）。例如 <c>#ffffff33</c>
/// 是"纯白 + 20% 不透明"，不是"不透明的白 + 蓝色 33"。
/// capsule.css 的浅色变体注释也印证了这一点：
/// "描边加深到 rgba(17,17,19,.12) —— 浅底上 rgba(255,255,255,.2) 那种白描边根本看不见"。</para>
/// </summary>
public static class CssTokenReader
{
    /// <summary>一条 token 声明（原始 CSS 文本 + 解析出来的 ARGB）。</summary>
    /// <param name="Name">CSS 变量名（如 <c>--deep</c>）。</param>
    /// <param name="RawValue">CSS 里的原始值（如 <c>rgba(244, 244, 245, 0.5)</c>）。</param>
    /// <param name="Argb">换算后的 ARGB（0xAARRGGBB）。</param>
    public readonly record struct Token(string Name, string RawValue, uint Argb);

    /// <summary>剥掉 CSS 注释（注释里可能有花括号，会干扰块定位）。</summary>
    public static string StripComments(string css)
        => Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

    /// <summary>
    /// 取出某个选择器块的内容（花括号配对）。
    /// </summary>
    /// <param name="css">已剥注释的 CSS。</param>
    /// <param name="selectorRegex">选择器的正则（不含花括号）。</param>
    public static string? ExtractBlock(string css, string selectorRegex)
    {
        var match = Regex.Match(css, selectorRegex, RegexOptions.Multiline);
        if (!match.Success)
        {
            return null;
        }

        int open = css.IndexOf('{', match.Index + match.Length - 1);
        if (open < 0)
        {
            return null;
        }

        int depth = 0;
        for (int i = open; i < css.Length; i++)
        {
            if (css[i] == '{')
            {
                depth++;
            }
            else if (css[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return css[(open + 1)..i];
                }
            }
        }

        return null;
    }

    /// <summary>解析一个块里的 <c>--name: value;</c> 声明。</summary>
    /// <param name="block">块内容。</param>
    /// <param name="skipNonColors">非颜色（--font / --font-mono）跳过。</param>
    public static Dictionary<string, Token> ParseTokens(string block, bool skipNonColors = true)
    {
        var result = new Dictionary<string, Token>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(block, @"--(?<name>[\w-]+)\s*:\s*(?<value>[^;]+);"))
        {
            string name = "--" + match.Groups["name"].Value;
            string value = match.Groups["value"].Value.Trim();

            if (skipNonColors && !LooksLikeColor(value))
            {
                continue;
            }

            if (!TryParseColor(value, out uint argb))
            {
                throw new InvalidOperationException(
                    $"无法解析的颜色值：{name}: {value}（只支持 #rgb / #rrggbb / #rrggbbaa / rgba(r,g,b,a)）");
            }

            result[name] = new Token(name, value, argb);
        }

        return result;
    }

    /// <summary>值看起来像颜色吗（用来把 --font / --font-mono 排除掉）。</summary>
    public static bool LooksLikeColor(string value)
        => value.StartsWith('#') || value.StartsWith("rgb", StringComparison.Ordinal);

    /// <summary>
    /// 颜色 → ARGB（0xAARRGGBB）。
    ///
    /// <list type="bullet">
    /// <item><c>#rrggbb</c> → alpha = FF</item>
    /// <item><c>#rrggbbaa</c> → <b>alpha 在最后</b>（CSS 规范，不是 ARGB）</item>
    /// <item><c>rgba(r,g,b,a)</c> → alpha = round(a × 255)（远离零取整）</item>
    /// </list>
    /// </summary>
    public static bool TryParseColor(string value, out uint argb)
    {
        argb = 0;
        value = value.Trim();

        if (value.StartsWith('#'))
        {
            string hex = value[1..];
            if (hex.Length == 3)
            {
                // CSS 允许 3 位缩写（capsule.css 的 --deep: #fff 就是）→ 展开成 6 位
                argb = 0xFF000000
                       | ((uint)int.Parse(hex[0].ToString() + hex[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 16)
                       | ((uint)int.Parse(hex[1].ToString() + hex[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 8)
                       | (uint)int.Parse(hex[2].ToString() + hex[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return true;
            }

            if (hex.Length == 6)
            {
                argb = 0xFF000000 | uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return true;
            }

            if (hex.Length == 8)
            {
                // CSS: #RRGGBBAA
                uint r = uint.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint g = uint.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint b = uint.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint a = uint.Parse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                argb = (a << 24) | (r << 16) | (g << 8) | b;
                return true;
            }

            return false;
        }

        if (value.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
        {
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close < 0 || close < open)
            {
                return false;
            }

            string[] parts = value[(open + 1)..close].Split(',');
            if (parts.Length != 4)
            {
                return false;
            }

            int r = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            int g = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            int b = int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
            double a = double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture);

            // CSS 的 alpha → 8 位：round(a × 255)，四舍五入要远离零
            // （0.7 × 255 = 178.5 → 179 = 0xB3；用银行家舍入会得到 178，与浏览器不一致）。
            uint alpha = (uint)Math.Round(a * 255.0, MidpointRounding.AwayFromZero);

            argb = (alpha << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            return true;
        }

        return false;
    }

    /// <summary>ARGB → 常见的显示形式（#AARRGGBB）。</summary>
    public static string ToHex(uint argb) => $"#{argb:X8}";
}
