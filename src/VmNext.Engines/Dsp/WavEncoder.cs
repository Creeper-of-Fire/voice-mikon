using System;
using System.IO;
using System.Text;

namespace VmNext.Engines.Dsp;

/// <summary>
/// 把浮点音频编码成 WAV 字节流（单声道 / 16-bit PCM）。
///
/// 翻译自 voice-mikon/src/voice_mikon/engines/base.py 的 <c>encode_wav</c>。
///
/// Python 版用**标准库** <c>wave</c> + numpy int16 —— 不引 soundfile。
/// C# 侧同样手写 44 字节的标准 WAV 头，不引任何音频库。
/// </summary>
public static class WavEncoder
{
    /// <summary>
    /// 编码成 WAV（RIFF/WAVE，PCM 16-bit，单声道）。
    ///
    /// 刻度：<c>pcm * 32767</c> 后**向零截断**成 int16（与 numpy 的
    /// <c>astype(np.int16)</c> 同口径）；越界做饱和处理
    /// （numpy 那边是回绕 —— 差一个极端边界，饱和更安全，注释在文档里说明）。
    /// </summary>
    public static byte[] Encode(float[] audio, int sampleRate)
    {
        int byteCount = audio.Length * 2;
        using var ms = new MemoryStream(44 + byteCount);
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // RIFF 头
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + byteCount);        // ChunkSize
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt 子块（PCM）
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                    // Subchunk1Size
        w.Write((short)1);               // AudioFormat = PCM
        w.Write((short)1);               // NumChannels = 单声道
        w.Write(sampleRate);             // SampleRate
        w.Write(sampleRate * 2);         // ByteRate = sr * blockAlign
        w.Write((short)2);               // BlockAlign = 2 字节（16-bit 单声道）
        w.Write((short)16);              // BitsPerSample

        // data 子块
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(byteCount);

        foreach (float s in audio)
        {
            float v = Math.Clamp(s, -1.0f, 1.0f);
            int pcm = (int)Math.Truncate(v * 32767.0);
            pcm = Math.Clamp(pcm, short.MinValue, short.MaxValue);
            w.Write((short)pcm);
        }

        w.Flush();
        return ms.ToArray();
    }
}
