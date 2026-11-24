using System;
using System.IO;
using System.Text.Json;
using VoiceMikonCSharp.Core;
using Xunit;

namespace VoiceMikonCSharp.Core.Tests;

/// <summary>
/// 配置的单元测试。
///
/// 覆盖：默认值 / 最小片段覆盖 / 未知键告警不崩 / save→load 往返。
/// </summary>
public class ConfigTests
{
    [Fact]
    public void 默认值与Python版逐项一致()
    {
        var cfg = new Config();

        Assert.Equal("ctrl+alt+space", cfg.Hotkey);
        Assert.Equal("0.6B", cfg.Model);
        Assert.Equal(string.Empty, cfg.ModelRoot);
        Assert.Equal("local", cfg.Backend);
        Assert.Equal("asr-1.0", cfg.MinimaxModel);
        Assert.Equal("https://api.minimax.cn", cfg.MinimaxBaseUrl);
        Assert.Null(cfg.InputDevice);
        Assert.Equal(new[] { "USB" }, cfg.DeviceHints);
        Assert.Equal(16000, cfg.SampleRate);
        Assert.Equal(120.0, cfg.MaxDuration);
        Assert.True(cfg.SaveRecordings);
        Assert.Null(cfg.Language);
        Assert.Empty(cfg.Hotwords);
        Assert.Equal("silero", cfg.PreviewVad);
        Assert.True(cfg.RestoreClipboard);
        Assert.True(cfg.TrailingSpace);
        Assert.Equal(string.Empty, cfg.PanelGeometry);
        Assert.False(cfg.PanelVisible);
        Assert.True(cfg.ShowTray);
        Assert.Equal("auto", cfg.Theme);
        Assert.Equal("orb", cfg.UiMode);
        Assert.True(cfg.ShowPreview);
        Assert.Equal(string.Empty, cfg.Orb.Geometry);
        Assert.Equal(string.Empty, cfg.Orb.Monitor);
        Assert.Equal("INFO", cfg.LogLevel);

        Assert.Empty(cfg.Validate());
    }

    [Fact]
    public void 最小片段只覆盖出现的键其余保持默认()
    {
        const string json = """{"hotkey": "ctrl+shift+p", "sample_rate": 8000}""";

        Config cfg = Config.FromDictionary(Parse(json));

        Assert.Equal("ctrl+shift+p", cfg.Hotkey);
        Assert.Equal(8000, cfg.SampleRate);

        // 没出现的键保持内置默认
        Assert.Equal("0.6B", cfg.Model);
        Assert.Equal("local", cfg.Backend);
        Assert.Equal(new[] { "USB" }, cfg.DeviceHints);
        Assert.Equal("silero", cfg.PreviewVad);
    }

    [Fact]
    public void 未知键被忽略且不崩()
    {
        const string json = """
        {
            "hotkey": "ctrl+alt+k",
            "some_removed_option": 123,
            "orb": { "geometry": "12,34", "monitor": "\\\\.\\DISPLAY1", "gone": true }
        }
        """;

        Config cfg = Config.FromDictionary(Parse(json));

        Assert.Equal("ctrl+alt+k", cfg.Hotkey);
        Assert.Equal("12,34", cfg.Orb.Geometry);
        Assert.Equal(@"\\.\DISPLAY1", cfg.Orb.Monitor);
    }

    [Fact]
    public void 类型不对的键用默认值顶上且不崩()
    {
        const string json = """{"sample_rate": "不是数字", "theme": 5, "device_hints": "USB"}""";

        Config cfg = Config.FromDictionary(Parse(json));

        Assert.Equal(16000, cfg.SampleRate);
        Assert.Equal("auto", cfg.Theme);
        Assert.Equal(new[] { "USB" }, cfg.DeviceHints);
    }

    [Fact]
    public void save后load往返一致()
    {
        string dir = TempDir();
        try
        {
            var cfg = new Config
            {
                Hotkey = "ctrl+alt+space",
                Model = "1.7B",
                Backend = "minimax",
                MinimaxBaseUrl = "https://api.minimax.io",
                InputDevice = 3,
                DeviceHints = new List<string> { "USB", "麦克风" },
                SampleRate = 16000,
                MaxDuration = 90.5,
                SaveRecordings = false,
                Language = "zh",
                Hotwords = new List<string> { "光刻机", "量子" },
                PreviewVad = "energy",
                RestoreClipboard = false,
                TrailingSpace = false,
                PanelGeometry = "10,20,300,400",
                PanelVisible = true,
                ShowTray = false,
                Theme = "dark",
                UiMode = "resident",
                ShowPreview = false,
                LogLevel = "DEBUG",
                Orb = new OrbConfig { Geometry = "1,2", Monitor = @"\\.\DISPLAY2" },
            };

            string path = Path.Combine(dir, "config.json");
            cfg.Save(path);

            // 写出来的 json 用的是 Python 版的键名
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.True(doc.RootElement.TryGetProperty("model_root", out _));
                Assert.True(doc.RootElement.TryGetProperty("save_recordings", out _));
                Assert.True(doc.RootElement.TryGetProperty("restore_clipboard", out _));
                Assert.True(doc.RootElement.TryGetProperty("orb", out JsonElement orb));
                Assert.True(orb.TryGetProperty("geometry", out _));
            }

            Config loaded = Config.Load(path);

            Assert.Equal(cfg.Hotkey, loaded.Hotkey);
            Assert.Equal(cfg.Model, loaded.Model);
            Assert.Equal(cfg.Backend, loaded.Backend);
            Assert.Equal(cfg.MinimaxBaseUrl, loaded.MinimaxBaseUrl);
            Assert.Equal(cfg.InputDevice, loaded.InputDevice);
            Assert.Equal(cfg.DeviceHints, loaded.DeviceHints);
            Assert.Equal(cfg.SampleRate, loaded.SampleRate);
            Assert.Equal(cfg.MaxDuration, loaded.MaxDuration);
            Assert.Equal(cfg.SaveRecordings, loaded.SaveRecordings);
            Assert.Equal(cfg.Language, loaded.Language);
            Assert.Equal(cfg.Hotwords, loaded.Hotwords);
            Assert.Equal(cfg.PreviewVad, loaded.PreviewVad);
            Assert.Equal(cfg.RestoreClipboard, loaded.RestoreClipboard);
            Assert.Equal(cfg.TrailingSpace, loaded.TrailingSpace);
            Assert.Equal(cfg.PanelGeometry, loaded.PanelGeometry);
            Assert.Equal(cfg.PanelVisible, loaded.PanelVisible);
            Assert.Equal(cfg.ShowTray, loaded.ShowTray);
            Assert.Equal(cfg.Theme, loaded.Theme);
            Assert.Equal(cfg.UiMode, loaded.UiMode);
            Assert.Equal(cfg.ShowPreview, loaded.ShowPreview);
            Assert.Equal(cfg.LogLevel, loaded.LogLevel);
            Assert.Equal(cfg.Orb.Geometry, loaded.Orb.Geometry);
            Assert.Equal(cfg.Orb.Monitor, loaded.Orb.Monitor);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 读不到配置文件时回退默认值不崩()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "broken.json");
            File.WriteAllText(path, "{ 这不是 json");

            Config cfg = Config.Load(path);

            Assert.Equal("ctrl+alt+space", cfg.Hotkey);
            Assert.Equal("local", cfg.Backend);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 退役的后端名回退到local()
    {
        const string json = """{"backend": "dashscope"}""";

        Config cfg = Config.FromDictionary(Parse(json));
        Config.ApplyLegacyBackendFallback(cfg);

        Assert.Equal("local", cfg.Backend);
    }

    [Fact]
    public void 术语表读取忽略注释空行并去重()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "hotwords.txt");
            File.WriteAllText(path, "# 注释\n\n  光刻机  \n光刻机\n量子\n");

            var words = Config.LoadHotwordsFile(path);

            Assert.Equal(new[] { "光刻机", "量子" }, words);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Dictionary<string, JsonElement> Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            map[prop.Name] = prop.Value.Clone();
        }

        return map;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "voice-mikon-csharp-coretests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
