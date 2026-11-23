using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VmNext.Shell;

namespace DemoA;

/// <summary>
/// Demo A 的窗口：透明球 + 右键菜单。
///
/// 注意：窗口带 WS_EX_NOACTIVATE，永远拿不到键盘焦点，所以 KeyDown 收不到 Q，
/// 退出必须走全局热键（HotKeyWindow）。这正是正式版语音工具需要的能力。
/// </summary>
public partial class MainWindow : OverlayWindow
{
    private const int QuitHotKeyId = 1;

    private readonly HotKeyWindow _hotKeys;

    /// <summary>创建窗口。</summary>
    public MainWindow()
    {
        InitializeComponent();

        _hotKeys = new HotKeyWindow();
        _hotKeys.HotKeyPressed += OnHotKeyPressed;
        if (!_hotKeys.Register(QuitHotKeyId, NativeMethods.MOD_NOREPEAT, 0x51 /* 'Q' */))
        {
            ProbeOutput.Info("[提示] Q 键注册失败（可能已被别的程序占用），可以手动关掉这个窗口。");
        }
    }

    private void OnHotKeyPressed(object? sender, int id)
    {
        if (id == QuitHotKeyId)
        {
            Close();
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _hotKeys.Dispose();
        base.OnClosed(e);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
