using System;
using System.Globalization;

namespace VmNext.Shell;

/// <summary>
/// 自动化输出的机器可读格式。AutoCheck 靠这些行解析子进程的结果，
/// 因此格式不要随意改（改了就同步改 tests/VmNext.AutoCheck 的解析器）。
/// </summary>
public static class ProbeOutput
{
    /// <summary>检查结果行：@@CHECK|编号|PASS/FAIL|详情</summary>
    public static void Check(string id, bool passed, string detail)
    {
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "@@CHECK|{0}|{1}|{2}",
            id,
            passed ? "PASS" : "FAIL",
            detail.Replace("\r", " ").Replace("\n", " ")));
        Console.Out.Flush();
    }

    /// <summary>标记行：@@MARK|名字|值</summary>
    public static void Mark(string name, string value)
    {
        Console.WriteLine("@@MARK|" + name + "|" + value);
        Console.Out.Flush();
    }

    /// <summary>原始数据行：@@TAG|值1|值2|...</summary>
    public static void Raw(string tag, params string[] values)
    {
        Console.WriteLine("@@" + tag + "|" + string.Join('|', values));
        Console.Out.Flush();
    }

    /// <summary>普通信息行（不会干扰解析）。</summary>
    public static void Info(string message)
    {
        Console.WriteLine(message);
        Console.Out.Flush();
    }
}
