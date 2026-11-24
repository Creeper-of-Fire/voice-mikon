using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;

[assembly: AvaloniaTestApplication(typeof(VoiceMikonCSharp.Ui.Tests.TestAppBuilder))]

namespace VoiceMikonCSharp.Ui.Tests;

/// <summary>
/// 无头测试的应用宿主。
///
/// <para>palette 装在 <c>TestApp.axaml</c> 里（与正式版、demo 引同一份
/// <c>avares://VoiceMikonCSharp.Ui/CapsuleTheme.axaml</c>，断言的颜色才对得上）。</para>
/// </summary>
public partial class TestApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}

/// <summary>Avalonia 无头平台的构建器（<c>AvaloniaTestApplication</c> 要它）。</summary>
public static class TestAppBuilder
{
    /// <summary>构建无头应用。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
