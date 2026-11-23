using System.Runtime.CompilerServices;

// 测试项目要直接读 XAML 生成的控件字段（Pill / TextBar / Waveform …）来断言视觉状态，
// 那些字段是 internal —— 这里开一条口子给 VmNext.Ui.Tests。
[assembly: InternalsVisibleTo("VmNext.Ui.Tests")]
