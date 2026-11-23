using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using VmNext.Shell;

namespace DemoC;

/// <summary>
/// Demo C 的窗口：拖拽、多显示器、文字选择复制。
///
/// 与 Demo A/B 的关键差异：这个窗口**允许激活**（不加 WS_EX_NOACTIVATE）。
/// 原因：Avalonia 的 SelectableTextBlock 复制走的是键盘命令，
/// 窗口没有焦点就收不到 Ctrl+C —— "不抢焦点"与"文字可编辑"天然互斥，
/// 这是覆盖层设计必须先决定的一件事（见 docs/feasibility.md 的结论）。
/// </summary>
public partial class MainWindow : OverlayWindow
{
    private const int HotKeyQuit = 1;

    private const double BallDiameter = 96;
    private const double CapsuleTargetWidth = 320;

    private readonly HotKeyWindow _hotKeys;
    private bool _dragging;
    private bool _moved;
    private PixelPoint _dragOriginScreen;
    private bool _expanded;

    /// <summary>创建窗口。</summary>
    public MainWindow()
    {
        InitializeComponent();

        _hotKeys = new HotKeyWindow();
        _hotKeys.HotKeyPressed += OnHotKeyPressed;
        _hotKeys.Register(HotKeyQuit, NativeMethods.MOD_NOREPEAT, 0x51); // Q

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    private void OnHotKeyPressed(object? sender, int id)
    {
        if (id == HotKeyQuit)
        {
            Close();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _moved = false;
        _dragOriginScreen = this.PointToScreen(point.Position);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        PixelPoint screen = this.PointToScreen(point.Position);

        // 用增量更新，避免累计误差导致松手时跳一下
        int dxPhysical = screen.X - _dragOriginScreen.X;
        int dyPhysical = screen.Y - _dragOriginScreen.Y;
        if (Math.Abs(dxPhysical) > 1 || Math.Abs(dyPhysical) > 1)
        {
            _moved = true;
        }

        _dragOriginScreen = screen;

        double scale = CurrentRenderScaling;
        MoveByLogicalPixels(dxPhysical / scale, dyPhysical / scale);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);

        // 没有明显位移 = 点击：展开/收起胶囊
        if (!_moved)
        {
            ToggleExpand();
        }

        e.Handled = true;
    }

    /// <summary>展开/收起胶囊（露出可选中的文字）。</summary>
    public void ToggleExpand()
    {
        _expanded = !_expanded;

        Capsule.Width = _expanded ? CapsuleTargetWidth : BallDiameter;
        Capsule.CornerRadius = new CornerRadius(BallDiameter / 2);
        RegionShape = _expanded ? OverlayShape.Capsule : OverlayShape.Circle;

        BallIcon.IsVisible = !_expanded;
        CapsuleContent.IsVisible = _expanded;
        BallIcon.Opacity = _expanded ? 0 : 1;
        CapsuleContent.Opacity = _expanded ? 1 : 0;
    }

    /// <summary>自动化用：文字控件是否支持复制。</summary>
    public bool TextCanCopy => SampleText.CanCopy;

    /// <summary>自动化用：全选文字（用来验证复制链路）。</summary>
    public void SelectAllText() => SampleText.SelectAll();

    /// <summary>自动化用：把选中的文字写进系统剪贴板（走 Avalonia 的 TopLevel.Clipboard）。</summary>
    public async Task<bool> CopySelectionAsync()
    {
        string text = SampleText.SelectedText ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            var clipboard = this.Clipboard;
            if (clipboard is null)
            {
                ProbeOutput.Info("[剪贴板] 当前平台没有提供剪贴板服务");
                return false;
            }

            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            ProbeOutput.Info("[剪贴板] 写入失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>自动化用：显示器信息（多屏/不同缩放）。</summary>
    public IReadOnlyList<string> DescribeScreens()
    {
        var result = new List<string>();

        foreach (var screen in this.Screens.All)
        {
            result.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "屏幕 Bounds(逻辑)=({0},{1},{2}x{3}) 缩放={4} 主屏={5} 物理≈({6},{7},{8}x{9})",
                screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height,
                screen.Scaling, screen.IsPrimary,
                (int)(screen.Bounds.X * screen.Scaling), (int)(screen.Bounds.Y * screen.Scaling),
                (int)(screen.Bounds.Width * screen.Scaling), (int)(screen.Bounds.Height * screen.Scaling)));
        }

        return result;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _hotKeys.Dispose();
        base.OnClosed(e);
    }
}
