namespace VmNext.AutoCheck;

/// <summary>
/// 一项检查的结果。
/// </summary>
/// <param name="Id">编号（A1–A13）。</param>
/// <param name="Name">项名。</param>
/// <param name="Value">实测值 / 断言结果。</param>
/// <param name="Passed">是否通过；null 表示"这是性能数值，不做二值判定"。</param>
/// <param name="Note">备注。</param>
public sealed record CheckItem(string Id, string Name, string Value, bool? Passed, string Note);
