namespace VoiceMikonCSharp.Ui;

/// <summary>
/// 悬浮球的位置记忆（**只读的一份快照**，由宿主提供）。
///
/// <para>对应 Python 版配置里的 <c>orb.geometry</c> + <c>orb.monitor</c>：
/// <list type="bullet">
/// <item><c>Geometry</c>：<c>"dx,dy"</c> —— 球相对所在工作区左上角的**逻辑**像素偏移；</item>
/// <item><c>Monitor</c>：显示器设备名（如 <c>\\.\DISPLAY1</c>）——
/// 还原时先找那块屏，找不到就退回当前屏。</item>
/// </list></para>
///
/// <para>UI 层**不直接碰** <c>Config</c>：读记忆与写记忆都通过宿主注入的委托
/// （<see cref="CapsuleWindow.PositionHintProvider"/> / <see cref="CapsuleWindow.PositionSaver"/>），
/// 这样"配置怎么存"留在宿主层，窗口只管"位置算得对不对"。</para>
/// </summary>
/// <param name="Geometry">相对工作区的逻辑像素偏移（<c>"dx,dy"</c>）；空 = 没拖过。</param>
/// <param name="Monitor">显示器设备名；空 = 不指定。</param>
public sealed record OrbPlacementHint(string Geometry, string Monitor);
