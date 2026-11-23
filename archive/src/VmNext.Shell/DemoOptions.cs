namespace VmNext.Shell;

/// <summary>demo 的运行模式开关。</summary>
public static class DemoOptions
{
    /// <summary>
    /// true = 自动化模式：跑完自检、打印机器可读结果后自动退出（给 AutoCheck 用）。
    /// false = 人工模式：打印引导文字，等用户看完按 Q 退出。
    /// </summary>
    public static bool Auto { get; set; }
}
