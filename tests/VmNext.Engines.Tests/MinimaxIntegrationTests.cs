using VmNext.Engines.Minimax;
using Xunit;

namespace VmNext.Engines.Tests;

/// <summary>
/// MiniMax 网络后端的**集成测试**（真发网络请求）。
///
/// 条件：
/// - 环境变量 <c>MINIMAX_API_KEY</c> 必须存在，否则**跳过**；
/// - 只上传**合成 WAV**（不碰用户麦克风、不跑真模型）。
///
/// ⚠ 会消耗 MiniMax 配额 —— 只在明确要验证网络路径时跑。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MinimaxIntegrationTests
{
    [Fact]
    public void 合成_wav_上传识别()
    {
        string? key = Environment.GetEnvironmentVariable(MinimaxBackend.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip($"环境变量 {MinimaxBackend.ApiKeyEnv} 不存在 —— 跳过网络集成测试");
        }

        using var backend = new MinimaxBackend(apiKey: key);
        backend.Load();

        Assert.True(backend.Loaded);

        // 合成一段 1 秒的 220Hz 正弦（不是语音，识别结果可能为空，但**请求必须成功**）
        var audio = Synth.Sine(16000, 220, 1.0, 0.3);

        string text = backend.Transcribe(audio, 16000);

        // 不校验文本内容（合成音本来就没有内容），只校验"没抛异常、拿到字符串"
        Assert.NotNull(text);
    }

    [Fact]
    public void 空音频_不发请求直接返回空串()
    {
        string? key = Environment.GetEnvironmentVariable(MinimaxBackend.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip($"环境变量 {MinimaxBackend.ApiKeyEnv} 不存在 —— 跳过网络集成测试");
        }

        using var backend = new MinimaxBackend(apiKey: key);
        backend.Load();

        Assert.Equal(string.Empty, backend.Transcribe(Array.Empty<short>(), 16000));
    }
}
