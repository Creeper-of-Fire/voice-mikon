using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using VoiceMikonCSharp.Core.Presentation;

namespace VoiceMikonCSharp.Core;

/// <summary>
/// 悬浮球的位置记忆。
///
/// 翻译自 voice-mikon/src/voice_mikon/config.py 的 OrbConfig。
///
/// 球的**显示形态**归 Config.UiMode 管（合并后那是唯一开关）；
/// 这里只剩**位置** —— 拖球松手后记下相对工作区的偏移和所在
/// 显示器，重启后还原。ui_mode 不是 orb 时这份记忆只是躺着，
/// 等哪天切回来再用。
/// </summary>
public sealed class OrbConfig
{
    /// <summary>"dx,dy"，相对所在工作区左上角的逻辑像素偏移。</summary>
    public string Geometry { get; set; } = string.Empty;

    /// <summary>显示器设备名（如 \\.\DISPLAY1）：还原时先找那块屏，找不到就退回当前屏。</summary>
    public string Monitor { get; set; } = string.Empty;

    public OrbConfig Clone() => new() { Geometry = Geometry, Monitor = Monitor };
}

/// <summary>
/// 全部可配置项。
///
/// 翻译自 voice-mikon/src/voice_mikon/config.py。
///
/// ⚠ **JSON 字段名与 Python 版逐字一致**（hotkey / model_root / save_recordings …），
/// 用户现有的 data/config.json 直接能用 —— 这是当初合并 UI 形态时拍板的规矩。
///
/// ⚠ **Save() 不把绝对路径烧进 json**：日志路径永远派生自数据目录
/// （Paths.LogPath，可用 VOICE_MIKON_LOG 覆盖），不是配置项。
/// 曾经允许配置日志文件，结果 config.save() 把当时的绝对路径烧进配置文件，
/// 项目目录一改名日志就写到旧位置去了。
/// </summary>
public sealed class Config : IPanelConfig
{
    // ---- 主题档位
    // auto  跟随系统（Windows 的"应用模式"）
    // light 强制白天 / dark 强制黑夜
    // 面板和胶囊共用这一套 —— 面板改了，胶囊跟着换。
    public static readonly string[] ThemeModes = { "auto", "light", "dark" };

    // 界面形态四档（悬浮球开关 × 待机策略合并后的单一维度，用户拍板）：
    //   orb        悬浮球常驻
    //   resident   无球，窗口常驻
    //   on_demand  按需：**存意图、不存现实** —— 运行期按 resident 行为执行
    //   headless   无界面：只能改 config 手动开启（重启生效）
    public static readonly string[] UiModes = { "orb", "resident", "on_demand", "headless" };

    // 识别后端（互斥，同一时刻只留一个）：
    //   local    本地 Qwen3-ASR 模型（离线）
    //   minimax  MiniMax 平台网络接口
    // API Key **不进配置文件** —— 环境变量 MINIMAX_API_KEY 优先，data/.env 文件兜底。
    public static readonly string[] AsrBackends = { "local", "minimax" };

    // 退役的后端名 → 回退到哪个。
    //
    // 为什么留这张表而不是"直接崩掉"：老配置不该把程序搞死 —— 用户只是
    // 升级了程序，配置还是老的。但也**不能静默**：回退时发一条 warning，
    // 让用户知道配的后端没了（不然他会以为"切了没反应"）。
    public static readonly IReadOnlyDictionary<string, string> LegacyBackendAliases =
        new Dictionary<string, string> { ["dashscope"] = "local" };

    // 预览断句用哪种"人声判定层"（只影响**预览**的切句，不影响最终识别）：
    //   silero  Silero VAD 神经网络判定（默认）
    //   energy  能量域判定（底噪估计 + 滞回门）—— Silero 不可用时的降级路径
    public static readonly string[] PreviewVadModes = { "silero", "energy" };

    private static readonly Logger _log = VoiceMikonCSharp.Core.Log.GetLogger("config");

    // ---- 热键与模型
    public string Hotkey { get; set; } = "ctrl+alt+space";

    /// <summary>可选 0.6B / 1.7B。</summary>
    public string Model { get; set; } = "0.6B";

    /// <summary>
    /// 本地 Qwen3-ASR 权重所在的根目录。
    /// 默认空 = 不指给任何人本机路径：本地后端必须显式配置，缺权重时
    /// 明确报错指路，**绝不静默联网下载**。
    /// </summary>
    public string ModelRoot { get; set; } = string.Empty;

    // ---- 识别后端（本地 / 网络，互斥）
    public string Backend { get; set; } = "local";

    /// <summary>网络后端的模型名（国内站 api.minimax.cn；海外站改成 api.minimax.io）。</summary>
    public string MinimaxModel { get; set; } = "asr-1.0";

    public string MinimaxBaseUrl { get; set; } = "https://api.minimax.cn";

    // ---- 音频
    /// <summary>null = 自动挑选。</summary>
    public int? InputDevice { get; set; }

    /// <summary>自动挑选麦克风时优先匹配这些名称子串。</summary>
    public List<string> DeviceHints { get; set; } = new() { "USB" };

    /// <summary>ASR 模型要求的采样率；重采样由引擎内部完成。</summary>
    public int SampleRate { get; set; } = 16000;

    public double MaxDuration { get; set; } = 120.0;

    /// <summary>
    /// 每轮录音结束后把原始音频存成 WAV（调试用）。
    /// 默认开：出问题时需要**同一段真实录音**反复回放定位，光看日志不够。
    /// </summary>
    public bool SaveRecordings { get; set; } = true;

    // ---- 识别
    /// <summary>null = 自动检测语种。</summary>
    public string? Language { get; set; }

    public List<string> Hotwords { get; set; } = new();

    // ---- 预览断句
    /// <summary>
    /// 预览层用什么判定"这一帧是不是人声"。**只影响预览**：
    /// 最终识别永远是把整段音频直送引擎，不加任何本地预判。
    /// </summary>
    public string PreviewVad { get; set; } = "silero";

    // ---- 文本注入
    /// <summary>
    /// 粘贴成功后要不要把原剪贴板内容还回去。
    ///   True  还回去（默认）：借板用完还原。代价是**多一次写入** —— 那次写入
    ///        带着我们的 owner 窗口，剪贴板管理器会把"原内容"这条记录的来源
    ///        标成我们，原本的来源（chrome/word…）就被冲掉了。
    ///   False 不还：识别文本一直留在剪贴板里，随时能复制走；也不会有还原
    ///        那次写入污染来源。
    /// </summary>
    public bool RestoreClipboard { get; set; } = true;

    public bool TrailingSpace { get; set; } = true;

    // ---- 界面
    /// <summary>"x,y,w,h"，空 = 用默认位置。</summary>
    public string PanelGeometry { get; set; } = string.Empty;

    /// <summary>面板启动时是否显示（默认不常驻，托盘打开）。</summary>
    public bool PanelVisible { get; set; }

    public bool ShowTray { get; set; } = true;

    /// <summary>auto（跟随系统）/ light（白天）/ dark（黑夜）。</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>界面形态四档（见 UiModes）。默认 orb（球常驻）。</summary>
    public string UiMode { get; set; } = "orb";

    /// <summary>录音时要不要显示实时预览文字条。</summary>
    public bool ShowPreview { get; set; } = true;

    /// <summary>悬浮球的位置记忆（拖球松手后记住，重启还原）。</summary>
    public OrbConfig Orb { get; set; } = new();

    // ---- 其它
    // ⚠ log_file **不是配置项**：日志路径永远派生自 data/（Paths.LogPath），
    //   可用环境变量 VOICE_MIKON_LOG 覆盖 —— 曾经允许配置，结果 Save()
    //   把当时的绝对路径烧进配置文件，项目目录一改名日志就写到旧位置去了。
    public string LogLevel { get; set; } = "INFO";

    /// <summary>返回问题列表，空列表表示配置正常。</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        if (Model is not ("0.6B" or "1.7B"))
        {
            issues.Add($"model 只能是 0.6B 或 1.7B,当前 '{Model}'");
        }

        if (!AsrBackends.Contains(Backend))
        {
            issues.Add($"backend 只能是 {string.Join(" / ", AsrBackends)},当前 '{Backend}'");
        }

        if (SampleRate <= 0)
        {
            issues.Add("sample_rate 必须为正数");
        }

        if (!PreviewVadModes.Contains(PreviewVad))
        {
            issues.Add($"preview_vad 只能是 {string.Join(" / ", PreviewVadModes)},当前 '{PreviewVad}'");
        }

        if (MaxDuration <= 0)
        {
            issues.Add("max_duration 必须为正数");
        }

        if (!ThemeModes.Contains(Theme))
        {
            issues.Add($"theme 只能是 {string.Join(" / ", ThemeModes)},当前 '{Theme}'");
        }

        if (!UiModes.Contains(UiMode))
        {
            issues.Add($"ui_mode 只能是 {string.Join(" / ", UiModes)},当前 '{UiMode}'");
        }

        if (DeviceHints is null || DeviceHints.Any(h => h is null))
        {
            issues.Add("device_hints 必须是字符串列表");
        }

        if (Orb is null)
        {
            issues.Add("orb 必须是对象,当前 null");
        }

        return issues;
    }

    /// <summary>
    /// 序列化成"键 → 值"（顺序即字段顺序，写文件时更好读）。
    ///
    /// 键名是 Python 版的原名（hotkey / model_root / …），不是 C# 属性名。
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["hotkey"] = Hotkey,
            ["model"] = Model,
            ["model_root"] = ModelRoot,
            ["backend"] = Backend,
            ["minimax_model"] = MinimaxModel,
            ["minimax_base_url"] = MinimaxBaseUrl,
            ["input_device"] = InputDevice,
            ["device_hints"] = DeviceHints,
            ["sample_rate"] = SampleRate,
            ["max_duration"] = MaxDuration,
            ["save_recordings"] = SaveRecordings,
            ["language"] = Language,
            ["hotwords"] = Hotwords,
            ["preview_vad"] = PreviewVad,
            ["restore_clipboard"] = RestoreClipboard,
            ["trailing_space"] = TrailingSpace,
            ["panel_geometry"] = PanelGeometry,
            ["panel_visible"] = PanelVisible,
            ["show_tray"] = ShowTray,
            ["theme"] = Theme,
            ["ui_mode"] = UiMode,
            ["show_preview"] = ShowPreview,
            ["orb"] = new Dictionary<string, object?>
            {
                ["geometry"] = Orb?.Geometry ?? string.Empty,
                ["monitor"] = Orb?.Monitor ?? string.Empty,
            },
            ["log_level"] = LogLevel,
        };
    }

    /// <summary>
    /// 从"键 → JSON 值"构造配置：**只接受已知字段，忽略未知键**。
    ///
    /// 未知键打一条 warning 但**不崩**（老配置文件不该把程序搞死）。
    /// 已知键的类型对不上时同样不崩：记一条 warning、用默认值顶上。
    ///
    /// 最小片段覆盖：没出现的键保持内置默认值。
    /// </summary>
    public static Config FromDictionary(IReadOnlyDictionary<string, JsonElement> data)
    {
        var cfg = new Config();
        var unknown = new List<string>();

        foreach (KeyValuePair<string, JsonElement> kv in data)
        {
            switch (kv.Key)
            {
                case "hotkey": cfg.Hotkey = ReadString(kv.Value, kv.Key) ?? cfg.Hotkey; break;
                case "model": cfg.Model = ReadString(kv.Value, kv.Key) ?? cfg.Model; break;
                case "model_root": cfg.ModelRoot = ReadString(kv.Value, kv.Key) ?? cfg.ModelRoot; break;
                case "backend": cfg.Backend = ReadString(kv.Value, kv.Key) ?? cfg.Backend; break;
                case "minimax_model": cfg.MinimaxModel = ReadString(kv.Value, kv.Key) ?? cfg.MinimaxModel; break;
                case "minimax_base_url": cfg.MinimaxBaseUrl = ReadString(kv.Value, kv.Key) ?? cfg.MinimaxBaseUrl; break;
                case "input_device": cfg.InputDevice = ReadNullableInt(kv.Value, kv.Key); break;
                case "device_hints": cfg.DeviceHints = ReadStringList(kv.Value, kv.Key) ?? cfg.DeviceHints; break;
                case "sample_rate": cfg.SampleRate = ReadInt(kv.Value, kv.Key) ?? cfg.SampleRate; break;
                case "max_duration": cfg.MaxDuration = ReadDouble(kv.Value, kv.Key) ?? cfg.MaxDuration; break;
                case "save_recordings": cfg.SaveRecordings = ReadBool(kv.Value, kv.Key) ?? cfg.SaveRecordings; break;
                case "language": cfg.Language = ReadNullableString(kv.Value, kv.Key); break;
                case "hotwords": cfg.Hotwords = ReadStringList(kv.Value, kv.Key) ?? cfg.Hotwords; break;
                case "preview_vad": cfg.PreviewVad = ReadString(kv.Value, kv.Key) ?? cfg.PreviewVad; break;
                case "restore_clipboard": cfg.RestoreClipboard = ReadBool(kv.Value, kv.Key) ?? cfg.RestoreClipboard; break;
                case "trailing_space": cfg.TrailingSpace = ReadBool(kv.Value, kv.Key) ?? cfg.TrailingSpace; break;
                case "panel_geometry": cfg.PanelGeometry = ReadString(kv.Value, kv.Key) ?? cfg.PanelGeometry; break;
                case "panel_visible": cfg.PanelVisible = ReadBool(kv.Value, kv.Key) ?? cfg.PanelVisible; break;
                case "show_tray": cfg.ShowTray = ReadBool(kv.Value, kv.Key) ?? cfg.ShowTray; break;
                case "theme": cfg.Theme = ReadString(kv.Value, kv.Key) ?? cfg.Theme; break;
                case "ui_mode": cfg.UiMode = ReadString(kv.Value, kv.Key) ?? cfg.UiMode; break;
                case "show_preview": cfg.ShowPreview = ReadBool(kv.Value, kv.Key) ?? cfg.ShowPreview; break;
                case "orb": cfg.Orb = ReadOrb(kv.Value, kv.Key); break;
                case "log_level": cfg.LogLevel = ReadString(kv.Value, kv.Key) ?? cfg.LogLevel; break;
                default: unknown.Add(kv.Key); break;
            }
        }

        if (unknown.Count > 0)
        {
            _log.Warning($"配置含未知键(已忽略): {string.Join(", ", unknown)}");
        }

        return cfg;
    }

    /// <summary>更新字段并忽略未知键（orb 传对象时整体替换）。</summary>
    public void Update(IReadOnlyDictionary<string, JsonElement> changes)
    {
        foreach (KeyValuePair<string, JsonElement> kv in changes)
        {
            switch (kv.Key)
            {
                case "hotkey": Hotkey = ReadString(kv.Value, kv.Key) ?? Hotkey; break;
                case "model": Model = ReadString(kv.Value, kv.Key) ?? Model; break;
                case "model_root": ModelRoot = ReadString(kv.Value, kv.Key) ?? ModelRoot; break;
                case "backend": Backend = ReadString(kv.Value, kv.Key) ?? Backend; break;
                case "minimax_model": MinimaxModel = ReadString(kv.Value, kv.Key) ?? MinimaxModel; break;
                case "minimax_base_url": MinimaxBaseUrl = ReadString(kv.Value, kv.Key) ?? MinimaxBaseUrl; break;
                case "input_device": InputDevice = ReadNullableInt(kv.Value, kv.Key); break;
                case "device_hints": DeviceHints = ReadStringList(kv.Value, kv.Key) ?? DeviceHints; break;
                case "sample_rate": SampleRate = ReadInt(kv.Value, kv.Key) ?? SampleRate; break;
                case "max_duration": MaxDuration = ReadDouble(kv.Value, kv.Key) ?? MaxDuration; break;
                case "save_recordings": SaveRecordings = ReadBool(kv.Value, kv.Key) ?? SaveRecordings; break;
                case "language": Language = ReadNullableString(kv.Value, kv.Key); break;
                case "hotwords": Hotwords = ReadStringList(kv.Value, kv.Key) ?? Hotwords; break;
                case "preview_vad": PreviewVad = ReadString(kv.Value, kv.Key) ?? PreviewVad; break;
                case "restore_clipboard": RestoreClipboard = ReadBool(kv.Value, kv.Key) ?? RestoreClipboard; break;
                case "trailing_space": TrailingSpace = ReadBool(kv.Value, kv.Key) ?? TrailingSpace; break;
                case "panel_geometry": PanelGeometry = ReadString(kv.Value, kv.Key) ?? PanelGeometry; break;
                case "panel_visible": PanelVisible = ReadBool(kv.Value, kv.Key) ?? PanelVisible; break;
                case "show_tray": ShowTray = ReadBool(kv.Value, kv.Key) ?? ShowTray; break;
                case "theme": Theme = ReadString(kv.Value, kv.Key) ?? Theme; break;
                case "ui_mode": UiMode = ReadString(kv.Value, kv.Key) ?? UiMode; break;
                case "show_preview": ShowPreview = ReadBool(kv.Value, kv.Key) ?? ShowPreview; break;
                case "orb": Orb = ReadOrb(kv.Value, kv.Key); break;
                case "log_level": LogLevel = ReadString(kv.Value, kv.Key) ?? LogLevel; break;
                default:
                    // 未知键忽略（对齐 Python 的 update：只改已知字段）
                    break;
            }
        }
    }

    /// <summary>保存配置到 data/config.json（路径见 Paths.ConfigPath）。</summary>
    public void Save(string? path = null)
    {
        string target = path ?? Paths.ConfigPath;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonWriter(stream, options);

        writer.WriteStartObject();
        foreach (KeyValuePair<string, object?> kv in ToDictionary())
        {
            writer.WritePropertyName(kv.Key);
            WriteValue(writer, kv.Value);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// <see cref="IPanelConfig.Save"/>：面板 presenter 写完配置后要落盘。
    ///
    /// <para>显式实现：<see cref="Save(string?)"/> 带一个可选参数，
    /// 签名上**不**满足无参的接口方法（可选参数不算重载），
    /// 所以这里显式转一下 —— 面板那条路永远用默认路径（data/config.json）。</para>
    /// </summary>
    void IPanelConfig.Save() => Save();

    /// <summary>
    /// 加载配置。
    ///
    /// 优先级：显式参数 &gt; data/config.json &gt; 内置默认值。
    /// 读失败（文件坏了 / 读不到）→ warning + 回退默认值，**不崩**。
    ///
    /// 退役的后端值：回退 + **一条** warning（不崩，也不静默）。
    /// </summary>
    public static Config Load(string? path = null)
    {
        Paths.EnsureDirs();

        var candidates = new List<string>();
        if (path is not null)
        {
            candidates.Add(path);
        }

        candidates.Add(Paths.ConfigPath);

        Config cfg = new();

        foreach (string cand in candidates)
        {
            if (!File.Exists(cand))
            {
                continue;
            }

            try
            {
                string text = File.ReadAllText(cand);
                using JsonDocument doc = JsonDocument.Parse(text);
                var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    map[prop.Name] = prop.Value.Clone();
                }

                cfg = FromDictionary(map);
                _log.Debug($"配置来自 {cand}");
                break;
            }
            catch (Exception ex)
            {
                _log.Warning($"配置文件 {cand} 读取失败({ex.Message}),回退默认值");
            }
        }

        ApplyLegacyBackendFallback(cfg);
        return cfg;
    }

    /// <summary>读术语表文件（一行一词，# 开头是注释，空行忽略）。文件不存在是正常情况。</summary>
    public static IReadOnlyList<string> LoadHotwordsFile(string? path = null)
    {
        string target = path ?? Paths.HotwordsPath;
        if (!File.Exists(target))
        {
            return Array.Empty<string>();
        }

        string text;
        try
        {
            text = File.ReadAllText(target);
        }
        catch (Exception ex)
        {
            _log.Warning($"术语表 {target} 读取失败: {ex.Message}");
            return Array.Empty<string>();
        }

        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string w = raw.Trim();
            if (w.Length == 0 || w.StartsWith('#'))
            {
                continue;
            }

            if (seen.Add(w))
            {
                words.Add(w);
            }
        }

        return words;
    }

    /// <summary>当前实际使用的配置文件路径（用于「打开设置文件」菜单）。</summary>
    public static string CurrentConfigPath => Paths.ConfigPath;

    /// <summary>
    /// 退役的后端值 → 回退 + 一条 warning。
    ///
    /// 单独暴露出来，是因为"读配置文件"和"处理退役值"是两件事：
    /// 配置也可能来自别处（面板改的、测试构造的），那两处同样要过这道回退。
    /// </summary>
    public static void ApplyLegacyBackendFallback(Config cfg)
    {
        string raw = (cfg.Backend ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0)
        {
            return;
        }

        if (LegacyBackendAliases.TryGetValue(raw, out string? fallback))
        {
            cfg.Backend = fallback;

            // **把退役的值写进日志** —— 用户得看见自己配的是哪个，
            // 才知道该改哪里。
            _log.Warning($"配置里的后端值已退役({raw}):已回退为 {fallback} —— 请把 backend 改成 \"local\" 或 \"minimax\"");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case List<string> list:
                writer.WriteStartArray();
                foreach (string item in list)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, object?> kv in map)
                {
                    writer.WritePropertyName(kv.Key);
                    WriteValue(writer, kv.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static OrbConfig ReadOrb(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            _log.Warning($"配置键 '{key}' 不是对象(已忽略),保留默认值");
            return new OrbConfig();
        }

        var orb = new OrbConfig();
        var unknown = new List<string>();

        foreach (JsonProperty prop in element.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "geometry":
                    orb.Geometry = ReadString(prop.Value, "orb.geometry") ?? orb.Geometry;
                    break;
                case "monitor":
                    orb.Monitor = ReadString(prop.Value, "orb.monitor") ?? orb.Monitor;
                    break;
                default:
                    unknown.Add(prop.Name);
                    break;
            }
        }

        if (unknown.Count > 0)
        {
            _log.Warning($"配置键 'orb' 含未知子键(已忽略): {string.Join(", ", unknown)}");
        }

        return orb;
    }

    private static string? ReadString(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        _log.Warning($"配置键 '{key}' 不是字符串(已忽略),保留默认值");
        return null;
    }

    private static string? ReadNullableString(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadString(element, key);
    }

    private static int? ReadInt(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int v))
        {
            return v;
        }

        _log.Warning($"配置键 '{key}' 不是整数(已忽略),保留默认值");
        return null;
    }

    private static int? ReadNullableInt(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadInt(element, key);
    }

    private static double? ReadDouble(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double v))
        {
            return v;
        }

        _log.Warning($"配置键 '{key}' 不是数字(已忽略),保留默认值");
        return null;
    }

    private static bool? ReadBool(JsonElement element, string key)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                _log.Warning($"配置键 '{key}' 不是布尔值(已忽略),保留默认值");
                return null;
        }
    }

    private static List<string>? ReadStringList(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            _log.Warning($"配置键 '{key}' 不是字符串列表(已忽略),保留默认值");
            return null;
        }

        var list = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString() ?? string.Empty);
            }
            else
            {
                _log.Warning($"配置键 '{key}' 含非字符串元素(已忽略该元素)");
            }
        }

        return list;
    }
}
