using System.Drawing;
using System.Windows.Forms;
using QrScanner.Core;

namespace QrScanner.Ui;

/// <summary>
/// 全屏选区窗：冻结画面 + 拖框。
///
/// <para>照搬 Python 版 <c>selector.py</c>：窗口里铺的是截屏那一帧的**变暗版**，
/// 拖出的矩形区域内实时贴回**原图** —— 选区内亮、选区外暗，所见即所得；
/// 还天然规避了"截屏期间屏幕还在变"的问题。</para>
///
/// <para>坐标约定：窗口铺满虚拟桌面，进程声明 per-monitor v2，
/// 鼠标坐标是物理像素 —— 与截图、选区是一套坐标。</para>
/// </summary>
public sealed class SelectorForm : Form
{
    /// <summary>选区边框 / 尺寸标签的颜色（琥珀，和主程序警告色同族）。</summary>
    private static readonly Color AccentColor = ColorTranslator.FromHtml("#f59e0b");

    /// <summary>选区外变暗的程度（与全黑混合的比例，照搬 Python 的 <c>_MASK_ALPHA = 0.45</c>）。</summary>
    private const double MaskAlpha = 0.45;

    /// <summary>操作提示文案（照搬 Python 的提示行）。</summary>
    private const string HintText = "拖拽框选二维码，松开即识别 — Esc 或右键取消";

    private readonly Bitmap _screenshot;
    private readonly Bitmap _dimmed;
    private readonly Rectangle _virtualScreen;

    private Point? _dragStart;
    private Rectangle _selection;
    private bool _accepted;

    private SelectorForm(Bitmap screenshot, Rectangle virtualScreen)
    {
        _screenshot = screenshot;
        _virtualScreen = virtualScreen;
        _dimmed = ImageOps.Dimmed(screenshot, MaskAlpha);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Text = "二维码扫描";
    }

    /// <summary>
    /// 弹全屏选区窗，返回用户框出的矩形。
    /// </summary>
    /// <para>坐标是物理像素、相对虚拟桌面原点，与 <paramref name="screenshot"/>
    /// （必须来自 <see cref="ScreenCapture.GrabVirtualScreen"/>）同一坐标系。
    /// Esc / 右键 / 拖太小 = 取消，返回 null。</para>
    public static Rectangle? SelectRegion(Bitmap screenshot)
    {
        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        using var form = new SelectorForm(screenshot, virtualScreen);
        form.ShowDialog();
        return form._accepted ? form._selection : null;
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right)
        {
            Cancel();
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            _dragStart = ClientToImage(e.Location);
            _selection = Rectangle.Empty;
            Invalidate();
        }
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragStart is null)
        {
            return;
        }

        _selection = SelectionGeometry.Normalize(_dragStart.Value, ClientToImage(e.Location));
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left || _dragStart is null)
        {
            return;
        }

        Rectangle region = SelectionGeometry.Normalize(_dragStart.Value, ClientToImage(e.Location));
        if (SelectionGeometry.IsTooSmall(region))
        {
            // 基本没拖：当误触（照搬 Python 的处理）。
            Cancel();
            return;
        }

        _selection = region;
        _accepted = true;
        Close();
    }

    /// <inheritdoc />
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Cancel();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.DrawImageUnscaled(_dimmed, 0, 0);

        if (_selection.Width > 0 && _selection.Height > 0)
        {
            // 选区内贴回原图：这就是"框内亮、框外暗"的来源。
            g.DrawImage(_screenshot, _selection, _selection, GraphicsUnit.Pixel);

            using (var pen = new Pen(AccentColor, 2))
            {
                g.DrawRectangle(pen, _selection);
            }

            // 尺寸标签跟在框右下角，快出屏时往里收。
            string size = $"{_selection.Width} × {_selection.Height}";
            int tx = Math.Min(_selection.Right + 10, ClientSize.Width - 90);
            int ty = Math.Min(_selection.Bottom + 10, ClientSize.Height - 30);
            using (var font = new Font("Segoe UI", 10, FontStyle.Bold))
            using (var brush = new SolidBrush(AccentColor))
            {
                g.DrawString(size, font, brush, tx, ty);
            }
        }

        if (_dragStart is null)
        {
            // 操作提示：开始拖了就让位。
            using var font = new Font("Segoe UI", 12);
            using var brush = new SolidBrush(Color.White);
            SizeF size = g.MeasureString(HintText, font);
            g.DrawString(HintText, font, brush, (ClientSize.Width - size.Width) / 2, 36);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dimmed.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 鼠标的客户区坐标 → 截图坐标。
    ///
    /// <para>走"屏幕坐标 → 减去虚拟桌面原点"这一步（而不是直接用客户区坐标）：
    /// 副屏挂在主屏左边时虚拟桌面原点是负的，这一步保证跨屏框选不错位。</para>
    /// </summary>
    private Point ClientToImage(Point clientPoint)
        => SelectionGeometry.ToImage(PointToScreen(clientPoint), _virtualScreen);

    /// <summary>取消（Esc / 右键）：静默退出。</summary>
    private void Cancel()
    {
        _accepted = false;
        _selection = Rectangle.Empty;
        Close();
    }
}
