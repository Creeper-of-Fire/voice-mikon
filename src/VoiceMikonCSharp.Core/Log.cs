using System;
using System.IO;
using System.Text;

namespace VoiceMikonCSharp.Core;

/// <summary>日志级别（与 Python 版 logging 的级别名一一对应）。</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// 一个轻量 logger（名字形如 "voice_mikon.config"）。
///
/// 翻译自 voice-mikon/src/voice_mikon/logging_setup.py。
/// </summary>
public sealed class Logger
{
    private readonly string _name;

    internal Logger(string name) => _name = name;

    /// <summary>logger 名字（对齐 Python 的 "voice_mikon.xxx"）。</summary>
    public string Name => _name;

    public void Debug(string message) => Log.Write(LogLevel.Debug, _name, message);

    public void Info(string message) => Log.Write(LogLevel.Info, _name, message);

    public void Warning(string message) => Log.Write(LogLevel.Warning, _name, message);

    public void Error(string message) => Log.Write(LogLevel.Error, _name, message);

    /// <summary>带异常的错误日志（对齐 Python 的 log.exception）。</summary>
    public void Error(string message, Exception ex)
        => Log.Write(LogLevel.Error, _name, $"{message}: {ex}");
}

/// <summary>
/// 日志。同时输出到控制台和文件，文件路径来自配置。
///
/// 翻译自 voice-mikon/src/voice_mikon/logging_setup.py。
///
/// 约定：
/// * Setup() 重复调用只会生效一次（对齐 Python 的 _configured 标志）。
/// * 日志文件写不了不该拖垮程序（对齐 Python 里对 OSError 的吞掉）。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static bool _configured;
    private static LogLevel _level = LogLevel.Info;
    private static StreamWriter? _file;

    /// <summary>
    /// 初始化日志，重复调用只会生效一次。
    ///
    /// 级别名非法时退回 INFO（对齐 Python 的 getattr(logging, level.upper(), logging.INFO)）。
    /// </summary>
    public static void Setup(string logFile, string level = "INFO")
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }

            _level = ParseLevel(level);

            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(logFile));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                _file = new StreamWriter(
                    new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception)
            {
                // 日志文件写不了不该拖垮程序
                _file = null;
            }

            _configured = true;
        }
    }

    /// <summary>取 logger（name 为空时就是根 logger）。</summary>
    public static Logger GetLogger(string name = "")
        => new(string.IsNullOrEmpty(name) ? "voice_mikon" : $"voice_mikon.{name}");

    /// <summary>内部写日志（级别过滤 + 格式化）。</summary>
    internal static void Write(LogLevel level, string name, string message)
    {
        if (level < _level)
        {
            return;
        }

        // 格式对齐 Python："[%(asctime)s] %(levelname)s %(message)s"，时间精度到秒
        string line = $"[{DateTime.Now:HH:mm:ss}] {LevelName(level)} {message}";

        lock (Gate)
        {
            Console.Out.WriteLine(line);

            try
            {
                _file?.WriteLine(line);
            }
            catch (Exception)
            {
                // 文件写失败不影响主流程
            }
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };

    private static LogLevel ParseLevel(string level)
    {
        string name = (level ?? string.Empty).Trim().ToUpperInvariant();
        return name switch
        {
            "DEBUG" => LogLevel.Debug,
            "INFO" => LogLevel.Info,
            "WARNING" or "WARN" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info,
        };
    }
}
