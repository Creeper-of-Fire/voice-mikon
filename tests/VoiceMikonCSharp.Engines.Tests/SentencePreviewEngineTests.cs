using VoiceMikonCSharp.Engines;
using Xunit;
using VoiceMikonCSharp.Engines.Streaming;
using VoiceMikonCSharp.Engines.Vad;

namespace VoiceMikonCSharp.Engines.Tests;

/// <summary>
/// 分句预览引擎（切句状态机）的测试。
///
/// 用合成帧序列（有声 / 静音交替）+ <see cref="FakeDetector"/> 验证：
/// 断句位置、0.3s 阈值、20s 强切、&lt;0.2s 丢弃、全静音段丢弃、close 幂等。
/// </summary>
public sealed class SentencePreviewEngineTests
{
    private const int Rate = 16000;
    private const int Frame = 320;   // 20ms @ 16k（能量法的帧长）

    [Fact]
    public void 连续静音_0_3秒后断句_句尾静音被裁掉()
    {
        var backend = new FakeBackend { OnTranscribe = _ => "第一句" };
        var detector = new FakeDetector(Frame, idx => idx < 25);   // 前 25 帧（0.5s）有声
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(Synth.Concat(Synth.Sine(Rate, 200, 0.5), Synth.Silence(Rate, 0.4)));

        Assert.Equal(1, backend.CallCount);
        // 句子 = 0.5s 有声（尾部 0.3s 静音被裁掉）
        Assert.Equal(8000, backend.CallLengths[0]);
        Assert.Equal(new[] { "第一句" }, engine.Sentences);
    }

    [Fact]
    public void 静音不足_0_3秒不断句()
    {
        var backend = new FakeBackend();
        var detector = new FakeDetector(Frame, idx => idx < 25);
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        // 0.28s 静音（14 帧 = 4480 样本 < 4800）
        engine.Feed(Synth.Concat(Synth.Sine(Rate, 200, 0.5), Synth.Silence(Rate, 0.28)));

        Assert.Equal(0, backend.CallCount);
        Assert.Empty(engine.Sentences);
    }

    [Fact]
    public void 静音刚到_0_3秒就断句()
    {
        var backend = new FakeBackend();
        var detector = new FakeDetector(Frame, idx => idx < 25);
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        // 0.3s 静音（15 帧 = 4800 样本 = 阈值）
        engine.Feed(Synth.Concat(Synth.Sine(Rate, 200, 0.5), Synth.Silence(Rate, 0.3)));

        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public void 一直说话不停_20秒强切()
    {
        var backend = new FakeBackend();
        var detector = new FakeDetector(Frame, _ => true);   // 全程有声
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(Synth.Sine(Rate, 200, 20.0));

        Assert.Equal(1, backend.CallCount);
        Assert.Equal(320000, backend.CallLengths[0]);   // 20s @ 16k
    }

    [Fact]
    public void 短于_0_2秒的片段被丢弃()
    {
        var backend = new FakeBackend();
        var detector = new FakeDetector(Frame, idx => idx < 5);   // 只有 0.1s 有声
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(Synth.Concat(Synth.Sine(Rate, 200, 0.1), Synth.Silence(Rate, 0.4)));

        Assert.Equal(0, backend.CallCount);
        Assert.Empty(engine.Sentences);
    }

    [Fact]
    public void 全静音段被丢弃_不产生幻觉句子()
    {
        var backend = new FakeBackend();
        var detector = new FakeDetector(Frame, _ => false);
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(Synth.Silence(Rate, 1.0));

        Assert.Equal(0, backend.CallCount);
        Assert.Empty(engine.Sentences);
    }

    [Fact]
    public void close_认掉最后一句_并返回拼接文本()
    {
        var backend = new FakeBackend { OnTranscribe = a => $"[{a.Length}]" };
        var detector = new FakeDetector(Frame, _ => true);
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(Synth.Sine(Rate, 200, 0.5));   // 没有静音 → 不断句

        Assert.Equal(0, backend.CallCount);

        string text = engine.Close();

        Assert.Equal(1, backend.CallCount);
        Assert.Equal("[8000]", text);
    }

    [Fact]
    public void close_幂等_不重复识别()
    {
        var backend = new FakeBackend { OnTranscribe = _ => "句子" };
        var detector = new FakeDetector(Frame, idx => idx < 25);
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(Synth.Concat(Synth.Sine(Rate, 200, 0.5), Synth.Silence(Rate, 0.4)));

        string first = engine.Close();
        string second = engine.Close();

        Assert.Equal(first, second);
        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public void 多句拼接_按顺序返回()
    {
        var backend = new FakeBackend { OnTranscribe = a => $"<{a.Length}>" };
        // 前 25 帧有声 → 第 2 段（第 40 帧起）有声
        var detector = new FakeDetector(Frame, idx => (idx < 25) || (idx >= 40 && idx < 65));
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(Synth.Concat(
            Synth.Sine(Rate, 200, 0.5),
            Synth.Silence(Rate, 0.3),
            Synth.Sine(Rate, 200, 0.5),
            Synth.Silence(Rate, 0.3)));

        string text = engine.Close();

        Assert.Equal(2, backend.CallCount);
        Assert.Equal("<8000><8000>", text);
    }

    [Fact]
    public void close_不再回调_partial()
    {
        var partials = new List<string>();
        var backend = new FakeBackend { OnTranscribe = _ => "句子" };
        var detector = new FakeDetector(Frame, idx => idx < 25);
        var engine = new SentencePreviewEngine(backend, Rate, partial: partials.Add, detector: detector);

        engine.Feed(Synth.Concat(Synth.Sine(Rate, 200, 0.5), Synth.Silence(Rate, 0.4)));
        Assert.Single(partials);   // 断句时回调过一次

        engine.Close();

        Assert.Single(partials);   // close 不再推一次
    }

    [Fact]
    public void 要求精确帧长的判定器_跨块拼窗()
    {
        var backend = new FakeBackend();
        // Silero 型：512 样本一窗，且必须精确
        var detector = new FakeDetector(512, _ => true, requiresExactFrames: true);
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(new short[1024 + 100]);   // 1024 整窗 + 100 的尾巴

        Assert.Equal(2, detector.FrameIndex);   // 尾巴留到下一块
    }

    [Fact]
    public void 要求精确帧长的判定器_close_时补零判掉半窗()
    {
        var backend = new FakeBackend();
        var detector = new FakeDetector(512, _ => true, requiresExactFrames: true);
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        engine.Feed(new short[300]);   // 不足一窗

        Assert.Equal(0, detector.FrameIndex);

        engine.Close();

        Assert.Equal(1, detector.FrameIndex);   // 补零凑满一窗判掉
    }

    [Fact]
    public void 多声道_混成单声道_整数刻度不毁掉()
    {
        short[]? received = null;
        var backend = new FakeBackend
        {
            OnTranscribe = a =>
            {
                received = a;
                return "句子";
            },
        };

        var detector = new FakeDetector(Frame, idx => idx < 10);   // 前 10 帧（0.2s）有声
        var engine = new SentencePreviewEngine(backend, Rate, detector: detector);

        // 交错双声道：左右都是 10000
        const int Frames = 3200;
        var stereo = new short[Frames * 2];
        for (int i = 0; i < Frames; i++)
        {
            stereo[i * 2] = 10000;
            stereo[(i * 2) + 1] = 10000;
        }

        engine.Feed(stereo, channels: 2);
        engine.Feed(Synth.Silence(Rate, 0.4));

        Assert.Equal(1, backend.CallCount);
        Assert.Equal(Frames, backend.CallLengths[0]);

        // 混音值必须还是 10000（不是 ±1 的浮点刻度，也不是 0）
        Assert.NotNull(received);
        Assert.All(received!, s => Assert.Equal(10000, s));
    }
}
