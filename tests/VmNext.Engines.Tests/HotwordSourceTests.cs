using VmNext.Engines;
using Xunit;

namespace VmNext.Engines.Tests;

/// <summary>
/// 热词合并（双来源 + mtime 缓存）的测试。
///
/// 翻译自 Python 版 <c>ASRBackend._effective_hotwords</c> 的行为。
/// </summary>
public sealed class HotwordSourceTests
{
    [Fact]
    public void 跨来源去重_词表文件里已有的词不重复追加()
    {
        using var tmp = new TempFile("# 注释\n词B\n词C\n");
        var source = new HotwordSource(new[] { "词A", "词B" }, tmp.Path);

        Assert.Equal(new[] { "词A", "词B", "词C" }, source.EffectiveWords());
    }

    [Fact]
    public void config_列表内部不去重_与_python_同口径()
    {
        // Python: words = [w.strip() for w in self.hotwords if w and w.strip()]
        //         —— 只滤空，**不去重**；去重只发生在"文件词 vs 已收集词"之间。
        var source = new HotwordSource(new[] { "词A", "词A", " 词B " }, null);

        Assert.Equal(new[] { "词A", "词A", "词B" }, source.EffectiveWords());
    }

    [Fact]
    public void 空词被过滤掉()
    {
        var source = new HotwordSource(new[] { "词A", "", "   ", null! }, null);

        Assert.Equal(new[] { "词A" }, source.EffectiveWords());
    }

    [Fact]
    public void 文件不存在时只用配置列表_不报错()
    {
        var source = new HotwordSource(new[] { "词A" }, Path.Combine(Path.GetTempPath(), "vmnext-不存在的词表-" + Guid.NewGuid().ToString("N") + ".txt"));

        Assert.Equal(new[] { "词A" }, source.EffectiveWords());
    }

    [Fact]
    public void mtime_不变时命中缓存_不重读文件()
    {
        using var tmp = new TempFile("词A\n");
        var source = new HotwordSource(null, tmp.Path);

        Assert.Equal(new[] { "词A" }, source.EffectiveWords());

        // 改内容，但**把 mtime 钉回原值** —— 缓存应当命中，词表不变
        DateTime original = tmp.OriginalWriteTimeUtc;
        tmp.WriteText("词B\n");
        tmp.SetLastWriteTimeUtc(original);

        Assert.Equal(new[] { "词A" }, source.EffectiveWords());
    }

    [Fact]
    public void 文件改动后重读_新词立即生效()
    {
        using var tmp = new TempFile("词A\n");
        var source = new HotwordSource(null, tmp.Path);

        Assert.Equal(new[] { "词A" }, source.EffectiveWords());

        tmp.WriteText("词B\n词C\n");
        // 显式把 mtime 往前推 —— 别赌文件系统的写入时间精度
        tmp.SetLastWriteTimeUtc(DateTime.UtcNow.AddSeconds(2));

        Assert.Equal(new[] { "词B", "词C" }, source.EffectiveWords());
    }

    [Fact]
    public void context_用官方格式拼装()
    {
        using var tmp = new TempFile("词C\n");
        var source = new HotwordSource(new[] { "词A", "词B" }, tmp.Path);

        Assert.Equal("Vocabulary: 词A, 词B, 词C.", source.BuildContext());
    }

    [Fact]
    public void 没有词时_context_是空串()
    {
        var source = new HotwordSource(Array.Empty<string>(), null);

        Assert.Equal(string.Empty, source.BuildContext());
    }

    [Fact]
    public void 词表文件内重复只保留一次()
    {
        using var tmp = new TempFile("词A\n词A\n词B\n");
        var source = new HotwordSource(null, tmp.Path);

        Assert.Equal(new[] { "词A", "词B" }, source.EffectiveWords());
    }
}

/// <summary>临时文件（写完自动删）。</summary>
internal sealed class TempFile : IDisposable
{
    public TempFile(string content)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vmnext-hotwords-" + Guid.NewGuid().ToString("N") + ".txt");
        WriteText(content);
    }

    public string Path { get; }

    /// <summary>首次写入时的 mtime（用来把 mtime 钉回去测缓存命中）。</summary>
    public DateTime OriginalWriteTimeUtc { get; private set; }

    public void WriteText(string content)
    {
        File.WriteAllText(Path, content);
        OriginalWriteTimeUtc = File.GetLastWriteTimeUtc(Path);
    }

    public void SetLastWriteTimeUtc(DateTime time) => File.SetLastWriteTimeUtc(Path, time);

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
        }
    }
}
