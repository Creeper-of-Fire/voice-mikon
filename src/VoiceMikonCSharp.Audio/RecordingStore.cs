using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VoiceMikonCSharp.Core;

namespace VoiceMikonCSharp.Audio;

/// <summary>
/// 录音留存：把每轮录音的原始音频落成 WAV。
///
/// 翻译自 voice-mikon/src/voice_mikon/audio/recording_store.py。
///
/// 为什么要有这个：真机上调"预览零出字"这类问题时，光看日志不够 —— 得能拿
/// **同一段真实录音**反复回放，看分句引擎到底断没断句、识别出了什么。
/// 日志会骗人（没喂进去 / 全判有声 / 断句识别为空三种情况在日志上都可能长得
/// 一样），音频不会。
///
/// 约束：
/// * 落盘在 data/recordings/（portable 约定：数据只进 data/）
/// * int16 单声道、原生采样率原样落盘（重采样是识别链路的事，留存要保真）
/// * 只留最近 KEEP_RECORDINGS 个：调试期够用，又不会让 data/ 无限膨胀。
/// </summary>
public static class RecordingStore
{
    /// <summary>最多保留几个录音文件（超出就把最旧的删掉）。</summary>
    public const int KeepRecordings = 20;

    private static readonly Logger _log = VoiceMikonCSharp.Core.Log.GetLogger("audio.store");

    /// <summary>把音频写成 WAV（int16 单声道，小端）。</summary>
    public static void WriteWav(string path, short[] audio, int sampleRate)
    {
        byte[] data = new byte[audio.Length * 2];
        Buffer.BlockCopy(audio, 0, data, 0, data.Length);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(fs, new UTF8Encoding(false));

        int byteRate = sampleRate * 2;      // 单声道 × 16 bit
        int blockAlign = 2;

        // RIFF 头（44 字节）
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);     // 文件大小 - 8
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt 块（PCM，16 字节）
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);             // 1 = PCM
        writer.Write((short)1);             // 声道数 = 1
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)16);            // 位深

        // data 块
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);

        writer.Flush();
    }

    /// <summary>
    /// 只保留最近 keep 个录音，其余删掉，返回被删的路径列表。
    ///
    /// 排序用文件名：文件名以时间戳开头，字典序就是时间序，比 mtime 可靠
    /// （改文件时间不会把顺序搞乱）。
    /// </summary>
    public static IReadOnlyList<string> PruneRecordings(string? directory = null, int keep = KeepRecordings)
    {
        string target = directory ?? Paths.RecordingsDir;
        if (!Directory.Exists(target))
        {
            return Array.Empty<string>();
        }

        string[] files = Directory.GetFiles(target, "*.wav");
        Array.Sort(files, StringComparer.Ordinal);

        var removed = new List<string>();
        int keepCount = Math.Max(0, keep);

        // keep <= 0 表示全删
        for (int i = 0; i < files.Length - keepCount; i++)
        {
            try
            {
                File.Delete(files[i]);
                removed.Add(files[i]);
            }
            catch (IOException ex)
            {
                _log.Debug($"删除旧录音 {files[i]} 失败: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Debug($"删除旧录音 {files[i]} 失败: {ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>
    /// 存一轮录音，返回落盘路径（音频为空时返回 null）。
    ///
    /// 文件名带时间戳和时长（例如 20261029-164630_31s.wav）—— 拿到文件名就知道
    /// 是哪一轮、多长，不用逐个打开听。
    ///
    /// **失败会抛异常** —— 调用方负责兜住，保证不影响主流程。
    /// </summary>
    public static string? SaveRecording(
        short[]? audio,
        int sampleRate,
        string? directory = null,
        int keep = KeepRecordings)
    {
        if (audio is null || audio.Length == 0)
        {
            return null;
        }

        string targetDir = directory ?? Paths.RecordingsDir;
        Directory.CreateDirectory(targetDir);

        double duration = audio.Length / (double)Math.Max(1, sampleRate);
        string stem = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Math.Round(duration):0}s";
        string path = UniquePath(targetDir, stem);

        WriteWav(path, audio, sampleRate);
        _log.Debug($"录音已留存: {path}");

        try
        {
            PruneRecordings(targetDir, keep);
        }
        catch (Exception ex)
        {
            // 清理失败不影响这一轮已经落盘
            _log.Debug($"清理旧录音失败: {ex.Message}");
        }

        return path;
    }

    /// <summary>同一秒里录了两轮也不会互相覆盖。</summary>
    private static string UniquePath(string directory, string stem)
    {
        string path = Path.Combine(directory, stem + ".wav");
        int idx = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{stem}-{idx}.wav");
            idx++;
        }

        return path;
    }
}
