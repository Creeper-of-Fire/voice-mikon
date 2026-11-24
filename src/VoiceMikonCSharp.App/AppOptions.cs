using System;
using System.Globalization;

namespace VoiceMikonCSharp.App;

/// <summary>
/// 命令行参数。
///
/// 翻译自 voice-mikon/src/voice_mikon/__main__.py 的 <c>build_parser()</c>，
/// 外加两个本次新增的开关（见各自的说明）。
/// </summary>
public sealed class AppOptions
{
    /// <summary>
    /// 无头模式（**自检**）：不启动常驻服务，只装配 core，
    /// 等引擎就绪后干净退出。超时非 0。
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>等待引擎加载完成的秒数上限（默认 300）。</summary>
    public double TimeoutSeconds { get; set; } = 300.0;

    /// <summary>后端覆盖（minimax / local）；null = 用配置里的。</summary>
    public string? Backend { get; set; }

    /// <summary>配置文件路径覆盖；null = 用 data/config.json。</summary>
    public string? ConfigPath { get; set; }

    /// <summary>
    /// 本地后端走 mock 模式（不加载真模型）。
    ///
    /// ⚠ **本次新增的开关**（Python 版没有）：自检/测试不能加载真模型 ——
    /// 一来用户的 voice-mikon 正占着显存，二来真模型加载要几十秒。
    /// mock 模式只起一个空壳子进程，<c>load</c> 立刻返回 ready。
    /// </summary>
    public bool Mock { get; set; }

    /// <summary>
    /// 原始命令行参数（逐字保留）。
    ///
    /// <para>托盘「重启」要"带着同样的参数再起一个" —— 解析过的开关再拼回去
    /// 容易漏，所以这里留一份原样。</para>
    /// </summary>
    public string[] RawArgs { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 解析命令行。
    ///
    /// 两种写法都认：<c>--timeout 30</c>（空格分隔，任务书里的写法）与
    /// <c>--timeout=30</c>（等号分隔，省得跟引号打架）。
    ///
    /// 未知参数**照旧忽略、不报错**（对齐 Python 的 <c>parse_known_args</c>：
    /// V1 的命令行没有参数语义，别为了新开关把老习惯破了）。
    /// </summary>
    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        string[] source = args ?? Array.Empty<string>();

        // 原样留一份（重启时"带着同样的参数再起一个"）。
        options.RawArgs = source;

        for (int i = 0; i < source.Length; i++)
        {
            string arg = source[i] ?? string.Empty;
            string key = arg;
            string? value = null;

            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                key = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else if (NeedsValue(key) && i + 1 < source.Length)
            {
                // 空格分隔：值在下一个参数里
                value = source[++i];
            }

            switch (key)
            {
                case "--headless":
                    options.Headless = true;
                    break;

                case "--timeout":
                    if (value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    {
                        options.TimeoutSeconds = seconds;
                    }

                    break;

                case "--backend":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        options.Backend = value.Trim().ToLowerInvariant();
                    }

                    break;

                case "--config":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        options.ConfigPath = value;
                    }

                    break;

                case "--mock":
                    options.Mock = true;
                    break;

                default:
                    // 不认识的参数忽略（Python 的 parse_known_args 同款）
                    break;
            }
        }

        return options;
    }

    /// <summary>这些开关的值在下一个参数里（而不是紧跟的等号后）。</summary>
    private static bool NeedsValue(string key)
        => key is "--timeout" or "--backend" or "--config";
}
