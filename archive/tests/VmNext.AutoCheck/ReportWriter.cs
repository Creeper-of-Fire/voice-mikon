using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VmNext.AutoCheck;

/// <summary>把检查结果写成 docs/feasibility.md（Markdown 表格）。</summary>
public static class ReportWriter
{
    /// <summary>生成报告正文。</summary>
    public static string Build(
        IReadOnlyList<CheckItem> items,
        IReadOnlyDictionary<string, string> environment,
        string conclusion)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# voice-mikon-next 可行性验证报告（Avalonia 窗口层）");
        sb.AppendLine();
        sb.AppendLine("> 本报告由 `tests/VmNext.AutoCheck` 自动生成；所有数值都是本机实测。");
        sb.AppendLine("> 自动化**不会**移动用户的鼠标或注入键盘（只用 PostMessage + 查询类 Win32 API）。");
        sb.AppendLine();

        sb.AppendLine("## 环境");
        sb.AppendLine();
        sb.AppendLine("| 项 | 值 |");
        sb.AppendLine("|---|---|");
        foreach (var (key, value) in environment)
        {
            sb.AppendLine($"| {key} | {value} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 检查结果");
        sb.AppendLine();
        sb.AppendLine("| 编号 | 项名 | 实测值 / 断言结果 | 通过 | 备注 |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (CheckItem item in items)
        {
            string verdict = item.Passed switch
            {
                true => "✅ 通过",
                false => "❌ 不通过",
                null => "ℹ️ 实测值",
            };

            sb.AppendLine($"| {item.Id} | {item.Name} | {Escape(item.Value)} | {verdict} | {Escape(item.Note)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 结论");
        sb.AppendLine();
        sb.AppendLine(conclusion);

        return sb.ToString();
    }

    /// <summary>Markdown 表格里的竖线要转义。</summary>
    private static string Escape(string text)
        => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
