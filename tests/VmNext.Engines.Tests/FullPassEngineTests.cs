using VmNext.Engines;
using Xunit;
using VmNext.Engines.Streaming;

namespace VmNext.Engines.Tests;

/// <summary>
/// 整段引擎（最终位）的测试：close 幂等、预览位的自适应刷新间隔。
/// </summary>
public sealed class FullPassEngineTests
{
    private const int Rate = 16000;

    [Fact]
    public void close_只识别一次_且幂等()
    {
        var backend = new FakeBackend { OnTranscribe = _ => "最终文本" };
        var engine = new FullPassEngine(backend, Rate);

        engine.Feed(Synth.Sine(Rate, 200, 1.0));

        string first = engine.Close();
        string second = engine.Close();

        Assert.Equal("最终文本", first);
        Assert.Equal(first, second);
        Assert.Equal(1, backend.CallCount);
        Assert.Equal(Rate, backend.CallLengths[0]);   // 整段音频
    }

    [Fact]
    public void close_空音频_返回空串()
    {
        var backend = new FakeBackend();
        var engine = new FullPassEngine(backend, Rate);

        Assert.Equal(string.Empty, engine.Close());
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public void 没有_partial_回调时不做周期性重识别()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        var engine = new FullPassEngine(backend, Rate, clock: () => clock.Now);

        engine.Feed(Synth.Sine(Rate, 200, 1.0));
        clock.Advance(10.0);
        engine.Feed(Synth.Sine(Rate, 200, 1.0));

        Assert.Equal(0, backend.CallCount);   // 最终位只在 close 时识别
    }

    [Fact]
    public void 预览位_按节奏重识别并回调_partial()
    {
        var clock = new FakeClock();
        var partials = new List<string>();
        var backend = new FakeBackend { OnTranscribe = _ => "预览文本" };
        var engine = new FullPassEngine(backend, Rate, partial: partials.Add, clock: () => clock.Now);

        // 音频够长（>= 0.5s）→ 首次 feed 就该刷新一次
        engine.Feed(Synth.Sine(Rate, 200, 1.0));
        Assert.Equal(1, backend.CallCount);
        Assert.Single(partials);

        // 还没到刷新间隔 → 不刷新
        clock.Advance(0.5);
        engine.Feed(Synth.Sine(Rate, 200, 1.0));
        Assert.Equal(1, backend.CallCount);

        // 超过间隔 → 刷新
        clock.Advance(2.0);
        engine.Feed(Synth.Sine(Rate, 200, 1.0));
        Assert.Equal(2, backend.CallCount);
        Assert.Equal(2, partials.Count);
    }

    [Fact]
    public void 预览位_间隔自适应_推理越慢间隔越长()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend
        {
            // 每次推理花 2 秒（把假时钟往前推）
            OnTranscribe = _ =>
            {
                clock.Advance(2.0);
                return "预览文本";
            },
        };

        var engine = new FullPassEngine(backend, Rate, partial: _ => { }, clock: () => clock.Now);

        engine.Feed(Synth.Sine(Rate, 200, 1.0));   // 第一次：推理耗时 2s → last_cost = 2
        Assert.Equal(1, backend.CallCount);

        // 间隔 = max(1.5, 2.0 × 1.2) = 2.4s
        clock.Advance(2.0);
        engine.Feed(Synth.Sine(Rate, 200, 1.0));
        Assert.Equal(1, backend.CallCount);   // 2.0 < 2.4 → 不刷新

        clock.Advance(0.5);   // 累计 2.5s > 2.4s
        engine.Feed(Synth.Sine(Rate, 200, 1.0));
        Assert.Equal(2, backend.CallCount);
    }

    [Fact]
    public void 预览位_音频太短不重识别()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        var engine = new FullPassEngine(backend, Rate, partial: _ => { }, clock: () => clock.Now);

        engine.Feed(Synth.Sine(Rate, 200, 0.2));   // < 0.5s

        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public void close_在预览位也返回最终文本()
    {
        var backend = new FakeBackend { OnTranscribe = _ => "最终文本" };
        var engine = new FullPassEngine(backend, Rate, partial: _ => { });

        engine.Feed(Synth.Sine(Rate, 200, 1.0));

        Assert.Equal("最终文本", engine.Close());
    }
}
