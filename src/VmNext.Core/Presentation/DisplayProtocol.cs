using System;
using System.Collections.Generic;

namespace VmNext.Core.Presentation;

/// <summary>
/// 显示状态（mode）常量。
///
/// 翻译自 voice-mikon/src/voice_mikon/apps/voicebar/protocol.py。
///
/// 这些字符串是**前端契约**：前端只演，不决策 —— 什么时候发生
/// （停多久、到点回哪）全归 presenter 的 dwell 表。
/// </summary>
public static class DisplayModes
{
    /// <summary>常驻球形态（待机）。</summary>
    public const string Orb = "orb";

    /// <summary>启动中。</summary>
    public const string Starting = "starting";

    /// <summary>展开中（进入录音的过渡态）。</summary>
    public const string Expanding = "expanding";

    /// <summary>录音中。</summary>
    public const string Recording = "recording";

    /// <summary>识别中。</summary>
    public const string Processing = "processing";

    /// <summary>结果终态（已输入）。</summary>
    public const string Result = "result_terminal";

    /// <summary>错误终态（无原文）。</summary>
    public const string Error = "error_terminal";

    /// <summary>取消终态。</summary>
    public const string Cancelled = "cancelled_terminal";

    /// <summary>被拒（命令被拒绝时的弹跳提示）。</summary>
    public const string Rejected = "rejected_bounce";

    /// <summary>
    /// 带原文的错误态（注入失败）的**标记**。
    ///
    /// 为什么单独标一个：<see cref="Error"/> 只说明"现在是错误态"，区分不了
    /// "错误带原文（常驻，可无限次点复制重试）"和"错误没原文（自动收回）"
    /// —— 而这两种的**何时收回**正好相反，得让 dwell 表能分开查。
    /// </summary>
    public const string ErrorWithText = "error_terminal+has-text";

    /// <summary>全部 mode（顺序同 Python 的 MODES）。</summary>
    public static readonly string[] All =
    {
        Orb,
        Starting,
        Expanding,
        Recording,
        Processing,
        Result,
        Error,
        Cancelled,
        Rejected,
    };
}

/// <summary>
/// 显示协议：mode 枚举 + dwell 转移表 + payload 字段。
///
/// 翻译自 voice-mikon/src/voice_mikon/apps/voicebar/protocol.py。
/// </summary>
public static class DisplayProtocol
{
    /// <summary>
    /// dwell 转移表：**整个项目只有这一份**。
    ///
    /// "何时发生"归 presenter：终态停多久、到点回哪，都查这张表；
    /// 前端不再有任何收回定时器（"如何呈现"归前端，那是动画的事）。
    ///
    /// key = 终态，value = 停留秒数；到点回 orb（常驻模式则收成球）。
    /// value 为 null = **不排自动转移**（带原文的错误态）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double?> DwellSeconds =
        new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [DisplayModes.Result] = 1.5,

            // **无原文**的错误：错误文案要让人看清，停久一点
            [DisplayModes.Error] = 4.5,

            [DisplayModes.Cancelled] = 1.0,
            [DisplayModes.Rejected] = 0.6,

            // 带原文的错误态：**无自动转移**（值 null = 不排）。
            //
            // 用户可能要反复点「复制」重试（剪贴板被占、目标窗口不给焦点），
            // 自动收回会把重试的口子堵上。只有点 ✕（dismiss）或开始下一轮
            // 录音才收。
            [DisplayModes.ErrorWithText] = null,
        };

    /// <summary>取 dwell 秒数；键不存在或值为 null 表示"不排自动转移"。</summary>
    public static double? DwellFor(string key)
        => DwellSeconds.TryGetValue(key, out double? seconds) ? seconds : null;
}

/// <summary>
/// 一条显示状态的载荷。
///
/// 字段**按 mode 取用**，用不到的字段一律给空值（前端不按 mode 猜）：
///
/// <list type="bullet">
/// <item><b>Message</b> —— 提示文案：错误原因 / 识别中 / 已输入 / 已取消 / 被拒的原因</item>
/// <item><b>Text</b> —— 识别正文：结果态是识别出来的文字；带原文的错误态是
/// **用户还能复制回来的那段原文**（注入失败时没送出去）</item>
/// <item><b>CanCopy</b> —— 有没有可复制的原文（bool）：带原文的错误态为 true。
/// 这类错误**不自动收回**，只有 dismiss 或新一轮录音才收</item>
/// <item><b>Reason</b> —— 机器可读的原因码（暂不参与渲染，留着以后排查）</item>
/// <item><b>Warn</b> / <b>Body</b> —— 只在"等用户松开修饰键"的瞬态覆盖里出现
/// （对齐 Python presenter 的 <c>modifier_wait</c> 分支）</item>
/// </list>
/// </summary>
public sealed record DisplayPayload(
    string Message = "",
    string Text = "",
    bool CanCopy = false,
    string Reason = "",
    bool Warn = false,
    string Body = "")
{
    /// <summary>空载荷（四个基础字段全空）。</summary>
    public static DisplayPayload Empty { get; } = new();
}

/// <summary>显示状态变化事件（mode + payload）。</summary>
public sealed class DisplayModeChangedEventArgs : EventArgs
{
    public DisplayModeChangedEventArgs(string mode, DisplayPayload payload)
    {
        Mode = mode;
        Payload = payload;
    }

    /// <summary>显示状态（见 <see cref="DisplayModes"/>）。</summary>
    public string Mode { get; }

    /// <summary>载荷。</summary>
    public DisplayPayload Payload { get; }
}

/// <summary>预览文字变化事件。</summary>
public sealed class PreviewTextChangedEventArgs : EventArgs
{
    public PreviewTextChangedEventArgs(string text)
    {
        Text = text;
    }

    /// <summary>预览文字（整段替换，不是追加）。</summary>
    public string Text { get; }
}

/// <summary>电平帧事件（30fps）。</summary>
public sealed class LevelsChangedEventArgs : EventArgs
{
    public LevelsChangedEventArgs(IReadOnlyList<double> levels, double elapsed)
    {
        Levels = levels;
        Elapsed = elapsed;
    }

    /// <summary>已映射电平（0..1）。</summary>
    public IReadOnlyList<double> Levels { get; }

    /// <summary>本轮已录音时长（秒）。</summary>
    public double Elapsed { get; }
}
