using System.Drawing;
using QrScanner.Core;
using Xunit;

namespace VmNext.Companions.Tests;

/// <summary>
/// 解码的单元测试。
///
/// <para>覆盖：生成一张码 → 解码 → 文本一致；空白图 → 认不出来（空结果）；
/// 反色的码（"黑底白码"那一档补救）也认得出来。</para>
///
/// <para>真机行为（遮罩、框选、弹窗、剪贴板）**不写自动化测试** ——
/// 那些没有对错规范，由用户实测。</para>
/// </summary>
public class QrDecoderTests
{
    [Fact]
    public void 解出二维码文本()
    {
        const string text = "https://example.com/voice-mikon?k=7";
        using Bitmap image = TestImages.EncodeQr(text);

        IReadOnlyList<QrHit> hits = QrScanner.Core.QrDecoder.Decode(image);

        Assert.Single(hits);
        Assert.Equal(text, hits[0].Text);
        Assert.Equal("QR_CODE", hits[0].Kind);
    }

    [Fact]
    public void 中文文本也解得出来()
    {
        const string text = "语音输入 —— 二维码";
        using Bitmap image = TestImages.EncodeQr(text);

        IReadOnlyList<QrHit> hits = QrScanner.Core.QrDecoder.Decode(image);

        Assert.Single(hits);
        Assert.Equal(text, hits[0].Text);
    }

    [Fact]
    public void 空白图解不出来()
    {
        using Bitmap image = TestImages.Blank();

        Assert.Empty(QrScanner.Core.QrDecoder.Decode(image));
    }

    [Fact]
    public void 反色的码也解得出来()
    {
        const string text = "inverted-qr";
        using Bitmap plain = TestImages.EncodeQr(text);
        using Bitmap inverted = ImageOps.Inverted(plain);

        IReadOnlyList<QrHit> hits = QrScanner.Core.QrDecoder.Decode(inverted);

        Assert.Single(hits);
        Assert.Equal(text, hits[0].Text);
    }
}
