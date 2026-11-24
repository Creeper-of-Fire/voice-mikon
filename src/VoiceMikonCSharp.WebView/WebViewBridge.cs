using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using VoiceMikonCSharp.Core;

namespace VoiceMikonCSharp.WebView;

/// <summary>
/// WebView2 运行环境（整个进程共用一个 <see cref="CoreWebView2Environment"/>）。
///
/// <para>为什么共用：WebView2 的用户数据目录（浏览器 profile、缓存、LocalStorage）
/// 由 <see cref="CoreWebView2Environment"/> 决定。同一个目录只能被一个环境打开，
/// 胶囊窗口和设置面板要共用同一个 profile（否则两边的 localStorage / cookie 不一致，
/// 某些站点还会出现"多实例"冲突）。</para>
///
/// <para><b>用户数据目录放在项目内的 <c>data/webview2/</c></b>：
/// WebView2 默认写到 <c>%LOCALAPPDATA%</c>，那违反仓库的 portable 约定
/// （运行时数据只进项目目录内的 <c>data/</c>）。</para>
/// </summary>
public static class WebViewEnvironment
{
    /// <summary>
    /// 虚拟主机名。前端资源通过 <c>https://vm.local/capsule.html</c> 访问。
    ///
    /// <para>为什么不用 <c>file://</c>：file:// 下 fetch/XHR 会被同源策略挡住，
    /// 而且 <c>file://</c> 的 origin 是 <c>null</c>，Chromium 的一批特性直接不可用。
    /// 虚拟主机映射是官方推荐的做法（C#: <c>SetVirtualHostNameToFolderMapping</c>）。</para>
    /// </summary>
    public const string VirtualHostName = "vm.local";

    /// <summary>
    /// WebView2 用户数据目录（项目内 <c>data/webview2/</c>）。
    /// </summary>
    public static string UserDataDirectory => Path.Combine(Paths.DataDir, "webview2");

    /// <summary>
    /// 前端静态资源目录（随程序发布的 <c>Static/</c>）。
    ///
    /// <para>路径解析：先按"程序目录旁的 Static/"找，找不到再退到
    /// "程序目录上一级/Static/"（<c>dotnet run</c> 时程序集在 <c>bin/</c> 下，
    /// 内容文件被拷到输出目录 —— 两种布局都试一遍）。</para>
    /// </summary>
    public static string StaticDirectory
    {
        get
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Static"),
                Path.Combine(Path.GetDirectoryName(baseDir) ?? baseDir, "Static"),
                Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(baseDir) ?? baseDir) ?? baseDir, "Static"),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "capsule.html")))
                {
                    return candidate;
                }
            }

            throw new DirectoryNotFoundException(
                $"找不到前端静态资源（capsule.html）。已尝试：{string.Join("、", candidates)}");
        }
    }

    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static CoreWebView2Environment? _environment;

    /// <summary>
    /// 取得（或首次创建）进程级 WebView2 环境。
    ///
    /// <para>⚠ **必须在 UI 线程上调用**：WebView2 的 COM 对象是 STA 的，
    /// 在别的线程上创建环境会报 <c>RPC_E_CHANGED_MODE</c>。这里全程
    /// <c>ConfigureAwait(true)</c>，保证后续的 COM 调用还在 UI 线程。</para>
    /// </summary>
    public static async Task<CoreWebView2Environment> GetAsync()
    {
        CoreWebView2Environment? existing = _environment;
        if (existing is not null)
        {
            return existing;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_environment is not null)
            {
                return _environment;
            }

            string userData = UserDataDirectory;
            Directory.CreateDirectory(userData);

            // 浏览器可执行文件传 null = 用 Evergreen 运行时（本机已装 157.0.4341.60）。
            CoreWebView2Environment env = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userData)
                .ConfigureAwait(true);

            _environment = env;
            return env;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// 前端 → C# 的一条消息（解析自 <c>window.chrome.webview.postMessage</c> 的 JSON）。
/// </summary>
/// <param name="Id">请求编号：有返回值的动作靠它把结果回填给前端的 Promise。</param>
/// <param name="Method">方法名（与 Python 版 <c>_CapsuleApi</c> / <c>_PanelApi</c> 逐字一致）。</param>
/// <param name="Args">参数数组（位置参数）。</param>
public readonly record struct BridgeMessage(int Id, string Method, IReadOnlyList<JsonElement> Args)
{
    /// <summary>取第 <paramref name="index"/> 个参数（没有则返回 null）。</summary>
    public JsonElement? Arg(int index)
        => index >= 0 && index < Args.Count ? Args[index] : null;

    /// <summary>取第 <paramref name="index"/> 个参数并按字符串读（没有则返回空串）。</summary>
    public string ArgString(int index)
        => Arg(index) is { } el && el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;

    /// <summary>取第 <paramref name="index"/> 个参数并按布尔读（没有则返回 false）。</summary>
    public bool ArgBool(int index)
        => Arg(index) is { } el && el.ValueKind is JsonValueKind.True;

    /// <summary>取第 <paramref name="index"/> 个参数并按数字读（没有则返回 0）。</summary>
    public double ArgDouble(int index)
        => Arg(index) is { } el && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0d;
}

/// <summary>
/// WebView2 消息桥：前端 ↔ C# 的双向通道。
///
/// <para>两个方向：
/// <list type="bullet">
/// <item><b>前端 → C#</b>：<c>WebMessageReceived</c> 事件，宿主按 <c>method</c> 分发。</item>
/// <item><b>C# → 前端</b>：<c>ExecuteScriptAsync</c> 调用 <c>window.__capsule.*</c> /
/// <c>window.__panel.*</c>。</item>
/// </list></para>
///
/// <para><b>线程</b>：WebView2 的 COM 对象是 STA 的，所有调用必须在创建它的
/// UI 线程上做。宿主侧一律用 <see cref="InvokeOnUi"/> 切回 UI 线程。</para>
/// </summary>
public sealed class WebViewBridge
{
    private static readonly Logger _log = Log.GetLogger("webview.bridge");

    private static readonly JsonSerializerOptions _json = new()
    {
        // 保持中文等非 ASCII 字符原样（ExecuteScriptAsync 里要拼成 JS 字面量，
        // 转义成 \uXXXX 虽然也对，但日志里全是 \u 没法看）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CoreWebView2 _core;
    private readonly string _target;

    /// <summary>构造桥。</summary>
    /// <param name="core">WebView2 核心对象。</param>
    /// <param name="target">JS 侧的目标对象名（<c>__capsule</c> 或 <c>__panel</c>）。</param>
    public WebViewBridge(CoreWebView2 core, string target)
    {
        _core = core;
        _target = target;
        _core.WebMessageReceived += OnWebMessageReceived;
    }

    /// <summary>前端发来的消息。</summary>
    public event EventHandler<BridgeMessage>? MessageReceived;

    /// <summary>
    /// 收到前端消息时触发（在 UI 线程上）。
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        BridgeMessage? message = Parse(e.WebMessageAsJson);
        if (message is null)
        {
            _log.Debug($"收到无法解析的消息（目标 {_target}）");
            return;
        }

        MessageReceived?.Invoke(this, message.Value);
    }

    /// <summary>
    /// 解析前端发来的 JSON。
    /// </summary>
    /// <param name="json">消息体（<c>{"id":1,"method":"x","args":[...]}</c>）。</param>
    public static BridgeMessage? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // 容错：前端若发的是 JSON **字符串**（老写法），WebMessageAsJson
            // 给回来的就是一个字符串值 —— 再解一层才能拿到字段。
            if (root.ValueKind == JsonValueKind.String)
            {
                string inner = root.GetString() ?? string.Empty;
                using JsonDocument innerDoc = JsonDocument.Parse(inner);
                root = innerDoc.RootElement.Clone();
            }

            string method = root.TryGetProperty("method", out JsonElement m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? string.Empty
                : string.Empty;

            if (method.Length == 0)
            {
                return null;
            }

            int id = root.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.Number
                ? i.GetInt32()
                : 0;

            var args = new List<JsonElement>();
            if (root.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in a.EnumerateArray())
                {
                    args.Add(item.Clone());
                }
            }

            return new BridgeMessage(id, method, args);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 调用前端方法：<c>window.__capsule.setMode(...)</c> / <c>window.__panel.setState(...)</c>。
    /// </summary>
    /// <param name="method">方法名（前端 <c>window.__capsule</c> / <c>window.__panel</c> 上的方法）。</param>
    /// <param name="args">参数（按位置传）。</param>
    /// <remarks>
    /// <para>参数用 <c>System.Text.Json</c> 序列化后**作为 JS 字面量**拼进脚本 ——
    /// 所以每个参数必须自己成为一个 JSON 值（字符串带引号、数字不带）。
    /// 整体再 <c>JsonSerializer.Serialize</c> 一次是错的（那会把整段脚本变成一个字符串）。</para>
    /// </remarks>
    public void Invoke(string method, params object?[] args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("(function(){try{ if(window.").Append(_target)
          .Append(" && typeof window.").Append(_target).Append('.').Append(method)
          .Append(" === 'function'){ window.").Append(_target).Append('.').Append(method).Append('(');

        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(JsonSerializer.Serialize(args[i], _json));
        }

        sb.Append("); return true; } }catch(e){ return false; } return false;})()");

        _ = _core.ExecuteScriptAsync(sb.ToString());
    }

    /// <summary>
    /// 回填有返回值动作的结果：前端 Promise 会拿到这个值。
    /// </summary>
    /// <param name="id">消息的请求编号。</param>
    /// <param name="value">返回值（JSON 可序列化的值）。</param>
    public void Resolve(int id, object? value)
    {
        if (id <= 0)
        {
            return;
        }

        string payload = JsonSerializer.Serialize(value, _json);
        _ = _core.ExecuteScriptAsync($"window.__bridge && window.__bridge.resolve({id}, {payload});");
    }

    /// <summary>
    /// 在前端执行一段脚本并拿回结果（JSON 字符串）。
    ///
    /// <para>⚠ 必须在 UI 线程上调用（WebView2 是 STA COM 对象）。</para>
    /// </summary>
    /// <param name="script">要执行的 JS（表达式）。</param>
    /// <returns>结果的 JSON 形式（对象 / 数组会被序列化成 JSON 文本）。</returns>
    public async Task<string> EvaluateAsync(string script)
    {
        try
        {
            return await _core.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
