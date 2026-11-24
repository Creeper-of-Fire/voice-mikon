using System;
using System.IO;

namespace VoiceMikonCSharp.Core;

/// <summary>
/// 运行时路径 —— 全部 portable，跟着项目目录走。
///
/// 约定见仓库根目录的 AGENTS.md：配置、日志、缓存一律放在项目目录内部，
/// 不要写到 %APPDATA% / %LOCALAPPDATA% 等系统位置。
///
/// 原因：这个仓库里的工具迭代很快、配置格式经常变。把配置放到系统目录里，
/// 旧版本留下的文件会污染新版本，而且它不在项目里，git status 看不到，
/// 删掉项目目录它还在 —— 换机器还复现不了。状态跟着代码走才能复现和回滚。
///
/// 可以用环境变量 VOICE_MIKON_HOME 覆盖数据目录（便携使用 / 测试）。
///
/// 翻译自 voice-mikon/src/voice_mikon/paths.py。
/// </summary>
public static class Paths
{
    /// <summary>项目根目录（源码在 src/VoiceMikonCSharp.Core/ 下时，根 = 上两级）。</summary>
    public static string ProjectDir { get; } = ResolveProjectDir();

    /// <summary>
    /// 运行时数据目录（配置、日志都在这）。
    ///
    /// 环境变量 VOICE_MIKON_HOME 覆盖；否则是 &lt;项目根&gt;/data。
    /// </summary>
    public static string DataDir
    {
        get
        {
            string? over = Environment.GetEnvironmentVariable("VOICE_MIKON_HOME");
            if (!string.IsNullOrWhiteSpace(over))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(over));
            }

            return Path.Combine(ProjectDir, "data");
        }
    }

    /// <summary>用户配置文件（VOICE_MIKON_CONFIG 可覆盖）。</summary>
    public static string ConfigPath
    {
        get
        {
            string? over = Environment.GetEnvironmentVariable("VOICE_MIKON_CONFIG");
            if (!string.IsNullOrWhiteSpace(over))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(over));
            }

            return Path.Combine(DataDir, "config.json");
        }
    }

    /// <summary>
    /// 日志文件（VOICE_MIKON_LOG 可覆盖）。
    ///
    /// ⚠ 这个路径**不是配置项**：曾经允许配置，结果 Config.Save() 把当时的
    /// 绝对路径烧进配置文件，项目目录一改名日志就写到旧位置去了。
    /// 路径永远派生自数据目录 + 环境变量。
    /// </summary>
    public static string LogPath
    {
        get
        {
            string? over = Environment.GetEnvironmentVariable("VOICE_MIKON_LOG");
            if (!string.IsNullOrWhiteSpace(over))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(over));
            }

            return Path.Combine(DataDir, "voice_input.log");
        }
    }

    /// <summary>
    /// 术语表文件（一行一词，# 开头是注释）。
    ///
    /// 独立于 config.json —— 术语表经常要增删，放单独文件方便编辑，
    /// 也不用每次改都去动配置。文件不存在是正常情况（没有术语）。
    /// </summary>
    public static string HotwordsPath => Path.Combine(DataDir, "hotwords.txt");

    /// <summary>
    /// 录音留存目录（调试用的原始音频）。
    ///
    /// 放在 data/ 下面，跟着项目走 —— 见仓库根目录 AGENTS.md 的
    /// portable 约定：程序写出的数据一律留在项目目录内。
    /// </summary>
    public static string RecordingsDir => Path.Combine(DataDir, "recordings");

    /// <summary>
    /// WebView2 运行时数据目录。
    ///
    /// WebView2 默认把用户数据（Cookies、缓存、LocalStorage）写到
    /// %LOCALAPPDATA%，那违反 portable 约定 —— 项目目录之外的东西
    /// git status 看不到、删项目也删不掉。所以显式指到项目内的 data/。
    /// </summary>
    public static string WebViewDir => Path.Combine(DataDir, "webview");

    /// <summary>确保数据目录存在（建不出来就算了，不拖垮程序）。</summary>
    public static void EnsureDirs()
    {
        foreach (string dir in new[] { DataDir, RecordingsDir, WebViewDir })
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (IOException)
            {
                // 与 Python 版一致：目录建不出来不是致命错误。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// 往上找项目根：先找带 VoiceMikonCSharp.sln 的目录（那是仓库根），
    /// 找不到就退回"程序目录往上三级"（bin/Debug/net10.0 → 项目目录）。
    /// </summary>
    private static string ResolveProjectDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VoiceMikonCSharp.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        // 兜底：<项目>/src/VoiceMikonCSharp.Core/bin/Debug/net10.0 → 上三级
        DirectoryInfo? fallback = new(AppContext.BaseDirectory);
        for (int i = 0; i < 3 && fallback?.Parent is not null; i++)
        {
            fallback = fallback.Parent;
        }

        return fallback?.FullName ?? AppContext.BaseDirectory;
    }
}
