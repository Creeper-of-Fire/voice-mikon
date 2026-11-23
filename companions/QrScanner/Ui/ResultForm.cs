using System.Drawing;
using System.Windows.Forms;
using QrScanner.Core;

namespace QrScanner.Ui;

/// <summary>
/// 识别结果窗：置顶小窗 + 真剪贴板复制。
///
/// <para>照搬 Python 版 <c>result.py</c>：有命中就展示类型与内容，并把全部内容
/// 拼接后**自动复制**；没命中就展示"没找到"的引导文案。</para>
///
/// <para>窗子本身也是同一条原则：识别完**不自动退出**，用户读完自己关
/// （Esc / 关闭按钮），看多久都行 —— 复制已经落到系统剪贴板，关了也不丢。</para>
/// </summary>
public sealed class ResultForm : Form
{
    /// <summary>复制成功的提示色（照搬 Python 的 #2e7d32）。</summary>
    private static readonly Color OkColor = ColorTranslator.FromHtml("#2e7d32");

    /// <summary>复制失败的提示色（照搬 Python 的 #b00020）。</summary>
    private static readonly Color ErrColor = ColorTranslator.FromHtml("#b00020");

    /// <summary>正文底色（照搬 Python 的 #f4f4f4）。</summary>
    private static readonly Color BodyColor = ColorTranslator.FromHtml("#f4f4f4");

    private readonly IReadOnlyList<QrHit> _hits;
    private readonly string _payload;
    private readonly Label _statusLabel;

    /// <summary>展示结果（有命中就展示类型与内容，没命中就展示引导文案）。</summary>
    public ResultForm(IReadOnlyList<QrHit> hits)
    {
        _hits = hits;
        _payload = string.Join("\n", hits.Select(hit => hit.Text));

        Text = "二维码扫描";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(420, 260);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(14, 12, 14, 12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        int row = 0;

        // 标题
        var title = new Label
        {
            Text = hits.Count > 1 ? $"识别到 {hits.Count} 个码" : hits.Count == 1 ? "识别到内容" : "未识别到二维码",
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            AutoSize = false,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        AddRow(layout, ref row, title, 26);

        // 正文（没命中时给一段引导文案）
        if (hits.Count == 0)
        {
            var hint = new Label
            {
                Text = "试把框画得紧一点（只框住码本身），或者离码近一点再截。",
                ForeColor = Color.FromArgb(0x77, 0x77, 0x77),
                AutoSize = false,
                Height = 40,
            };
            AddRow(layout, ref row, hint, 40);
        }
        else
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits.Count > 1)
                {
                    var kind = new Label
                    {
                        Text = $"#{i + 1} · {hits[i].Kind}",
                        ForeColor = Color.FromArgb(0x77, 0x77, 0x77),
                        AutoSize = false,
                        Height = 18,
                        TextAlign = ContentAlignment.MiddleLeft,
                    };
                    AddRow(layout, ref row, kind, 18);
                }

                // 文本框保持可选中 —— 用户可以只复制其中一段。
                var box = new TextBox
                {
                    Text = hits[i].Text,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = BodyColor,
                    Font = new Font("Consolas", 10f),
                    Height = TextHeight(hits[i].Text),
                };
                AddRow(layout, ref row, box, box.Height);
            }
        }

        // 状态行（已复制 / 复制失败）
        _statusLabel = new Label
        {
            AutoSize = false,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        AddRow(layout, ref row, _statusLabel, 22);

        // 按钮行
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 36,
            Padding = new Padding(0, 8, 0, 0),
        };

        var copyButton = new Button { Text = "复制", Width = 84, Height = 28 };
        copyButton.Click += (_, _) => UpdateStatus(ClipboardWriter.TrySetText(_payload));

        var closeButton = new Button { Text = "关闭", Width = 84, Height = 28 };
        closeButton.Click += (_, _) => Close();

        buttons.Controls.Add(copyButton);
        buttons.Controls.Add(closeButton);
        AddRow(layout, ref row, buttons, 36);

        Controls.Add(layout);

        // 自动复制：内容已经在剪贴板里了，用户粘哪都能用。
        UpdateStatus(hits.Count > 0 && ClipboardWriter.TrySetText(_payload));
    }

    /// <inheritdoc />
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <inheritdoc />
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 窗口挪到主屏中间偏上（跟截图时手的位置接近）。
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.VirtualScreen;
        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Top + (area.Height - Height) / 3);
    }

    /// <summary>加一行（行高固定，内容自己排）。</summary>
    private static void AddRow(TableLayoutPanel layout, ref int row, Control control, int height)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(control, 0, row);
        row++;
    }

    /// <summary>更新状态行（复制成功 / 失败）。</summary>
    private void UpdateStatus(bool copied)
    {
        if (_hits.Count == 0)
        {
            _statusLabel.Text = string.Empty;
            _statusLabel.ForeColor = Color.Transparent;
            return;
        }

        _statusLabel.Text = copied ? "已复制到剪贴板" : "复制失败，请在上面手动选中复制";
        _statusLabel.ForeColor = copied ? OkColor : ErrColor;
    }

    /// <summary>文本框高度：内容自适应，长 URL 折行也放得下（照搬 Python 的 _text_height）。</summary>
    private static int TextHeight(string text)
    {
        int lines = Math.Max(1, text.Split('\n').Length);
        return Math.Min(6, lines + 1) * 18 + 10;
    }
}
