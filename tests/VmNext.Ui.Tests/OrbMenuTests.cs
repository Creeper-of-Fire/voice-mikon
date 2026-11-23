using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VmNext.Core.Presentation;
using VmNext.Ui;
using Xunit;

// 别名：避免与 System.IO.Path 撞名（同 CapsuleWindow.axaml.cs 的做法）。
using IconPath = Avalonia.Controls.Shapes.Path;

namespace VmNext.Ui.Tests;

/// <summary>
/// 球菜单的单元测试。
///
/// <para>菜单的**形态**是照搬来的（Python 版的球菜单：设置 / 二维码扫描 / 退出，
/// 顺序一致）—— 这属于有对错规范的部分，可以断言。
/// 菜单的观感（图标长什么样、hover 什么色）不写测试。</para>
/// </summary>
public class OrbMenuTests
{
    /// <summary>球菜单三项，顺序照 Python 版（capsule.html 的 .orb-menu）。</summary>
    private static readonly string[] ExpectedMenuButtons =
    {
        "SettingsMenuButton",
        "QrMenuButton",
        "QuitMenuButton",
    };

    /// <summary>菜单打开时，三项都在，且顺序一致。</summary>
    [AvaloniaFact]
    public async Task 球菜单_三项齐全且顺序一致()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            window.ToggleOrbMenu();
            await CapsuleWindowTests.SettleAsync();

            List<string> names = window.GetVisualDescendants()
                .OfType<Button>()
                .Select(button => button.Name)
                .Where(name => name is not null && ExpectedMenuButtons.Contains(name))
                .ToList()!;

            Assert.Equal(ExpectedMenuButtons, names);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 二维码图标的**几何**落在 24×24 视框里（SVG 视框，缩放到 17×17 显示）。
    /// 断言它不是空的、也没跑到视框外 —— 路径抄错时这里会炸。
    /// </summary>
    [AvaloniaFact]
    public async Task 二维码图标_几何落在视框内()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            window.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            // 菜单默认是收起的：按钮的模板没展开，它里面的 Path 还不进可视树。
            window.ToggleOrbMenu();
            await CapsuleWindowTests.SettleAsync();

            // 图标是二维码按钮里唯一的 Path —— 从按钮往下找，不依赖 Name 的赋值语义。
            Button? button = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Name == "QrMenuButton");

            Assert.NotNull(button);
            IconPath icon = Assert.Single(button!.GetVisualDescendants().OfType<IconPath>());

            Rect bounds = icon.Data!.Bounds;

            Assert.InRange(bounds.Left, 2, 4);
            Assert.InRange(bounds.Top, 2, 4);
            Assert.InRange(bounds.Right, 20, 22);
            Assert.InRange(bounds.Bottom, 20, 22);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>点「二维码扫描」→ 窗口发出拉起请求（宿主去起独立进程）。</summary>
    [AvaloniaFact]
    public async Task 点二维码按钮_发出拉起请求()
    {
        var window = await CapsuleWindowTests.OpenWindowAsync();
        try
        {
            bool requested = false;
            window.QrScanRequested += (_, _) => requested = true;

            window.SetMode(DisplayModes.Orb, DisplayPayload.Empty);
            await CapsuleWindowTests.SettleAsync();

            window.ToggleOrbMenu();
            await CapsuleWindowTests.SettleAsync();

            Button? button = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Name == "QrMenuButton");

            Assert.NotNull(button);
            button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(requested);
        }
        finally
        {
            window.Close();
        }
    }
}
