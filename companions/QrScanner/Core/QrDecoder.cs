using System.Drawing;
using ZXing;
using ZXing.Common;

namespace QrScanner.Core;

/// <summary>
/// 二维码 / 条形码解码：ZXing.Net（纯托管、多格式 —— 对等 Python 版的 pyzbar / zbar）。
///
/// <para>照搬 Python 版的补救顺序：截图场景的码一般清晰正对，zbar 一次就中；
/// 认不到时按「原图 → 反色 → 放大一倍」的顺序各试一遍（反色救"黑底白码"，
/// 放大救轻度模糊），还不行就如实返回空 —— 不做更重的预处理。</para>
///
/// <para>条形码也顺手认（ZXing 本来就都认），结果里如实标注符号类型。</para>
/// </summary>
public static class QrDecoder
{
    /// <summary>在图里找码，返回全部命中（通常 0 或 1 个）。</summary>
    public static IReadOnlyList<QrHit> Decode(Bitmap image)
    {
        var hits = new List<QrHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        using Bitmap plain = ImageOps.AsArgb(image);

        // 候选版本逐个试，某一遍有命中就停 —— 避免重复解码同一个码。
        if (TryDecodeInto(plain, hits, seen))
        {
            return hits;
        }

        using (Bitmap inverted = ImageOps.Inverted(plain))
        {
            if (TryDecodeInto(inverted, hits, seen))
            {
                return hits;
            }
        }

        using (Bitmap scaled = ImageOps.Scale2x(plain))
        {
            TryDecodeInto(scaled, hits, seen);
        }

        return hits;
    }

    /// <summary>
    /// 解一张候选图，把命中并进 <paramref name="hits"/>。
    /// 返回是否已有命中（用来决定要不要试下一档候选）。
    /// </summary>
    private static bool TryDecodeInto(Bitmap candidate, List<QrHit> hits, HashSet<string> seen)
    {
        byte[] pixels = ImageOps.ToBgraBytes(candidate);

        // BGRA 内存序 = ZXing 的 BitmapFormat.BGRA32，直接喂，不用换通道。
        var source = new RGBLuminanceSource(
            pixels,
            candidate.Width,
            candidate.Height,
            RGBLuminanceSource.BitmapFormat.BGRA32);

        var reader = new MultiFormatReader
        {
            Hints = new Dictionary<DecodeHintType, object>
            {
                [DecodeHintType.TRY_HARDER] = true,
            },
        };

        var bitmap = new BinaryBitmap(new HybridBinarizer(source));

        Result? result;
        try
        {
            result = reader.decode(bitmap);
        }
        catch (ReaderException)
        {
            // 认不到 —— 如实返回空（这是常态，不是错误）。
            return false;
        }
        catch (Exception)
        {
            // 解码器内部异常也不该拖垮工具：当作这一档没认出来。
            return false;
        }

        if (result is null || string.IsNullOrEmpty(result.Text))
        {
            return false;
        }

        // 同一个码只报一次（多档候选可能命中同一个码）。
        if (seen.Add(result.Text))
        {
            hits.Add(new QrHit(result.Text, result.BarcodeFormat.ToString()));
        }

        return hits.Count > 0;
    }
}
