using System;
using System.IO;
using System.Linq;
using VmNext.Audio;
using Xunit;

namespace VmNext.Audio.Tests;

/// <summary>
/// 录音留存的单元测试：WAV 写得对、轮转只留 20 个。
/// </summary>
public class RecordingStoreTests
{
    [Fact]
    public void 写出的Wav是int16单声道原生采样率()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "a.wav");
            var audio = new short[] { 0, 1000, -1000, 32767, -32768 };

            RecordingStore.WriteWav(path, audio, 48000);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            Assert.Equal("RIFF", new string(reader.ReadChars(4)));
            reader.ReadInt32();               // 文件大小
            Assert.Equal("WAVE", new string(reader.ReadChars(4)));

            Assert.Equal("fmt ", new string(reader.ReadChars(4)));
            Assert.Equal(16, reader.ReadInt32());
            Assert.Equal((short)1, reader.ReadInt16());     // PCM
            Assert.Equal((short)1, reader.ReadInt16());     // 单声道
            Assert.Equal(48000, reader.ReadInt32());        // 采样率
            Assert.Equal(48000 * 2, reader.ReadInt32());    // 字节率
            Assert.Equal((short)2, reader.ReadInt16());     // 块对齐
            Assert.Equal((short)16, reader.ReadInt16());    // 位深

            Assert.Equal("data", new string(reader.ReadChars(4)));
            Assert.Equal(audio.Length * 2, reader.ReadInt32());

            var data = reader.ReadBytes(audio.Length * 2);
            for (int i = 0; i < audio.Length; i++)
            {
                Assert.Equal(audio[i], BitConverter.ToInt16(data, i * 2));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 只留最近20个录音()
    {
        string dir = TempDir();
        try
        {
            // 造 25 个"名字有序"的假录音
            for (int i = 0; i < 25; i++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"{i:D4}.wav"), new byte[] { 1, 2, 3 });
            }

            IReadOnlyList<string> removed = RecordingStore.PruneRecordings(dir);

            string[] left = Directory.GetFiles(dir, "*.wav").Select(Path.GetFileName).OrderBy(x => x).ToArray()!;

            Assert.Equal(20, left.Length);
            Assert.Equal(5, removed.Count);

            // 留下的应该是最新（名字靠后）的 20 个
            Assert.Equal("0005.wav", left[0]);
            Assert.Equal("0024.wav", left[^1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 存一轮录音后自动轮转()
    {
        string dir = TempDir();
        try
        {
            for (int i = 0; i < 20; i++)
            {
                // 假录音也要用真实的时间戳命名：轮转是按**文件名**排序的
                // （字典序 = 时间序），别的命名会被当成"最旧"删掉。
                File.WriteAllBytes(Path.Combine(dir, $"20260101-0000{i:D2}_1s.wav"), new byte[] { 1 });
            }

            string? path = RecordingStore.SaveRecording(new short[] { 1, 2, 3 }, 16000, dir);

            Assert.NotNull(path);
            Assert.True(File.Exists(path!));

            string[] left = Directory.GetFiles(dir, "*.wav");
            Assert.Equal(20, left.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 空音频不落盘()
    {
        string dir = TempDir();
        try
        {
            Assert.Null(RecordingStore.SaveRecording(Array.Empty<short>(), 16000, dir));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vmnext-audiotests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
