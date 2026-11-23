using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VmNext.Core;

/// <summary>
/// 一条通知。
///
/// 翻译自 voice-mikon/src/voice_mikon/notify.py 的 Notification dataclass。
/// level / kind / message 三元组是**契约**，别改名字 —— 上层（胶囊、面板）
/// 按 kind 分支处理。
/// </summary>
public sealed record Notification(
    string Level,
    string Kind,
    string Message,
    IReadOnlyDictionary<string, object?> Fields,
    DateTimeOffset Timestamp);

/// <summary>
/// 通知中心：订阅者 + 日志出口（data/notify.log）。
///
/// 翻译自 voice-mikon/src/voice_mikon/notify.py 的 NotificationHub。
/// </summary>
public sealed class NotificationHub
{
    /// <summary>合法级别。</summary>
    public static readonly string[] Levels = { "error", "warning", "info" };

    private readonly object _lock = new();
    private readonly List<Action<Notification>> _subscribers = new();
    private readonly string _logFile;
    private readonly Logger _log = Log.GetLogger("notify");

    public NotificationHub(string? logFile = null)
    {
        _logFile = string.IsNullOrWhiteSpace(logFile)
            ? Path.Combine(Paths.DataDir, "notify.log")
            : logFile;
    }

    /// <summary>全局单例（对齐 Python 的 notify = NotificationHub()）。</summary>
    public static NotificationHub Notify { get; } = new();

    /// <summary>订阅通知。</summary>
    public void Subscribe(Action<Notification> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_lock)
        {
            _subscribers.Add(callback);
        }
    }

    /// <summary>当前订阅者数量。</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>
    /// 通知发出事件（level / kind / message 三元组是契约）。
    ///
    /// ⚠ **触发线程**：发通知的那条线程（识别线程 / 模型加载线程 / 调用方线程）。
    /// UI 侧要自己 <c>Dispatcher.UIThread.Post</c>。
    ///
    /// 与 <see cref="Subscribe"/> 的区别：<c>Subscribe</c> 是**订阅即投递**的
    /// 通道（回调在发布的那一刻同步跑）；这个事件只管"发出"这一下。
    /// 两条路都在，是因为 UI 那边更像是"事件"而不是"回调"。
    /// </summary>
    public event EventHandler<Notification>? NotificationPosted;

    /// <summary>
    /// 发布一条通知。
    ///
    /// 校验规则与 Python 版一致：level 必须是 error/warning/info 之一，
    /// kind 必须是非空字符串，message 必须是字符串。违反 → 抛异常（不是"悄悄忽略"）。
    /// </summary>
    public void Publish(string level, string kind, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (!Levels.Contains(level))
        {
            throw new ArgumentException(
                $"通知 level 非法:期望 {string.Join(" / ", Levels)} 之一,实际 '{level}'", nameof(level));
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException($"通知 kind 非法:期望非空字符串,实际 '{kind}'", nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(message);

        var note = new Notification(
            level,
            kind,
            message,
            fields ?? new Dictionary<string, object?>(),
            DateTimeOffset.Now);

        WriteLog(note);

        Action<Notification>[] targets;
        lock (_lock)
        {
            targets = _subscribers.ToArray();
        }

        foreach (Action<Notification> cb in targets)
        {
            try
            {
                cb(note);
            }
            catch (Exception ex)
            {
                // 一个订阅者炸了不该影响其它订阅者（对齐 Python 版逐个 try）。
                _log.Error($"通知订阅者回调异常(kind={note.Kind})", ex);
            }
        }

        try
        {
            NotificationPosted?.Invoke(this, note);
        }
        catch (Exception ex)
        {
            _log.Error($"通知事件处理异常(kind={note.Kind})", ex);
        }
    }

    /// <summary>写通知日志（data/notify.log）。</summary>
    private void WriteLog(Notification note)
    {
        string stamp = note.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        string line = $"{stamp} | {note.Level} | {note.Kind} | {note.Message}";
        if (note.Fields.Count > 0)
        {
            line += " | " + FieldsToJson(note.Fields);
        }

        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(_logFile));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(_logFile, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _log.Warning($"通知日志 {_logFile} 写入失败: {ex.Message}");
        }

        LogLevel level = note.Level switch
        {
            "error" => LogLevel.Error,
            "warning" => LogLevel.Warning,
            _ => LogLevel.Info,
        };

        Log.Write(level, "voice_mikon.notify", $"notify {note.Kind}: {note.Message}");
    }

    /// <summary>
    /// 字段序列化：中文不转义（对齐 Python 的 ensure_ascii=False）。
    ///
    /// Python 那边是 default=str（拿不动的值退化成字符串）—— C# 这边同理：
    /// 值先 ToString()，不让一个奇怪的对象把整条通知序列化搞炸。
    /// </summary>
    private static string FieldsToJson(IReadOnlyDictionary<string, object?> fields)
    {
        var flat = new Dictionary<string, string?>(fields.Count);
        foreach (KeyValuePair<string, object?> kv in fields)
        {
            flat[kv.Key] = kv.Value switch
            {
                null => null,
                string s => s,
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => kv.Value.ToString(),
            };
        }

        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        return JsonSerializer.Serialize(flat, options);
    }
}
