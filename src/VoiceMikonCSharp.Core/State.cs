namespace VoiceMikonCSharp.Core;

/// <summary>
/// 应用状态与后端健康度（K3 的状态机要用）。
///
/// 翻译自 voice-mikon/src/voice_mikon/core/state.py。
/// </summary>
public static class AppStates
{
    public const string Starting = "starting";
    public const string Idle = "idle";
    public const string Recording = "recording";
    public const string Processing = "processing";

    public static readonly string[] All = { Starting, Idle, Recording, Processing };
}

/// <summary>后端健康度状态。</summary>
public static class HealthStatuses
{
    public const string Loading = "loading";
    public const string Ready = "ready";
    public const string Failed = "failed";

    public static readonly string[] All = { Loading, Ready, Failed };
}

/// <summary>
/// 后端健康度（不可变快照）。
///
/// 对应 Python 的 frozen dataclass BackendHealth。
/// </summary>
public sealed record BackendHealth(string Status = HealthStatuses.Loading, string Reason = "")
{
    public bool Ready => Status == HealthStatuses.Ready;

    public bool Failed => Status == HealthStatuses.Failed;

    public bool Loading => Status == HealthStatuses.Loading;
}

/// <summary>
/// 应用状态快照（不可变）。
///
/// 对应 Python 的 frozen dataclass AppState；Python 的 with_changes(**changes)
/// 对应 C# 的 record `with` 表达式。
/// </summary>
public sealed record AppState
{
    public string State { get; init; } = AppStates.Starting;

    public string Backend { get; init; } = "local";

    public BackendHealth BackendHealth { get; init; } = new();

    public string Model { get; init; } = string.Empty;

    public string PreviewText { get; init; } = string.Empty;
}

/// <summary>
/// 命令执行结果（不可变）。
///
/// 对应 Python 的 frozen dataclass CommandResult。
/// Python 的 __bool__ 语义（真 = ok）在这里体现为 <see cref="Ok"/>。
/// </summary>
public sealed record CommandResult(bool Ok, string Reason = "", string Message = "", string Text = "")
{
    public bool Rejected => !Ok;
}
