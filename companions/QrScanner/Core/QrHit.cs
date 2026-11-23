namespace QrScanner.Core;

/// <summary>
/// 一次解码命中。
/// </summary>
/// <param name="Text">解出的文本（非 utf-8 的字节按替换字符保底，绝不抛异常）。</param>
/// <param name="Kind">符号类型（如 <c>QR_CODE</c> / <c>CODE_128</c>）。</param>
public sealed record QrHit(string Text, string Kind);
