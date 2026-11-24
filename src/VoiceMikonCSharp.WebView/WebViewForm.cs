using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using VoiceMikonCSharp.Core;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.WebView;

/// <summary>
/// WebView2 承载窗口的基类。
///
/// <para>它负责三件事：
/// <list type="bullet">
/// <item><b>环境</b>：共用一个 <see cref="CoreWebView2Environment"/>（用户数据目录在
/// 项目内的 <c>data/webview2/</c>），把静态资源映射到虚拟主机
/// <c>https://vm.local/</c>（见 <see cref="WebViewEnvironment"/>）。</item>
/// <item><b>ready 门控</b>：前端就绪之前**不推任何东西**；就绪之后调
/// <see cref="OnFrontendReadyCore"/> 补推一遍当前状态。</item>
/// <item><b>桥接</b>：前端 → C# 的消息统一从 <see cref="OnBridgeMessageCore"/> 出去。</item>
/// </list></para>
///
/// <para>窗口尺寸按**逻辑像素**给（CSS 像素），实际按窗口所在显示器的缩放
/// 换算成物理像素 —— 这样页面视口的 CSS 尺寸就是给定的逻辑尺寸，
/// 同一份 CSS 在任何缩放下都画出同一张图。</para>
/// </summary>
public abstract class WebViewForm : Form
{
    private static readonly Logger _log = Log.GetLogger("webview.form");

    private readonly WebView2 _webView;
    private WebViewBridge? _bridge;
    private bool _initStarted;

    /// <summary>造一个 WebView 承载窗口。</summary>
    /// <param name="pageFile">静态资源里的页面文件名（相对虚拟主机根）。</param>
    /// <param name="backgroundColor">页面未绘制区域的底色（透明窗口用色键色）。</param>
    /// <param name="transparent">true = 透明窗口（按 <paramref name="backgroundColor"/> 做色键）。</param>
    protected WebViewForm(string pageFile, Color backgroundColor, bool transparent)
    {
        PageFile = pageFile;
        PageBackgroundColor = backgroundColor;

        // Control 的真实背景色：透明窗口下它就是色键色。
        BackColor = backgroundColor;

        FormBorderStyle = FormBorderStyle.None;

        // 不进任务栏 / Alt+Tab（与 Python 版一致：胶囊是工具窗口）。
        ShowInTaskbar = false;

        // 位置由我们自己摆（WinForms 的默认摆位会按屏幕中心算，跨屏时全错）。
        StartPosition = FormStartPosition.Manual;

        // ⚠ 别让 AutoScale 再缩一次客户区 —— 那是 pywebview 那边"请求 512 实得 872"
        //   的同一个坑（WinForms 按字体自动缩放）。窗口尺寸我们自己算。
        AutoScaleMode = AutoScaleMode.None;

        // 透明窗口：色键把网页里未被绘制的区域抠掉，透出桌面。
        // 抠不掉的颜色 = WebView2 的 DefaultBackgroundColor，所以两者必须一致。
        if (transparent)
        {
            TransparencyKey = backgroundColor;
        }

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,

            // ⚠ 透明窗口的关键：WebView2 必须**不渲染背景**（Color.Transparent），
            //   那样网页的透明区域 alpha=0，后面的桌面直接透出来。
            //   若把它设成色键色（洋红），WebView2 会用不透明的洋红铺满整个客户区，
            //   而**色键对子窗口无效**（子窗口不参与父窗口的 layered 合成）——
            //   洋红就留在屏幕上了（实测：色键抠不掉）。
            //   父窗口这边仍用 WinForms 的 TransparencyKey 把自己的底色抠掉。
            DefaultBackgroundColor = transparent ? Color.Transparent : backgroundColor,
        };

        Controls.Add(_webView);
    }

    /// <summary>静态资源里的页面文件名。</summary>
    public string PageFile { get; }

    /// <summary>页面底色（透明窗口下也是色键色）。</summary>
    public Color PageBackgroundColor { get; }

    /// <summary>前端桥接对象（WebView2 初始化完成前为 null）。</summary>
    public WebViewBridge? Bridge => _bridge;

    /// <summary>前端就绪（就绪之前不推任何东西）。</summary>
    public bool FrontendReady { get; private set; }

    /// <summary>
    /// 前端就绪了（宿主可在这里补推一次全量状态）。
    ///
    /// <para>⚠ 前端就绪之前的所有推送都被丢弃了 —— 宿主必须在这个事件里
    /// 把当前状态**重新推一遍**，否则界面停在默认值上。</para>
    /// </summary>
    public event EventHandler? FrontendReadyChanged;

    /// <summary>桥接目标对象名（<c>window.__capsule</c> / <c>window.__panel</c>）。</summary>
    protected abstract string BridgeTarget { get; }

    /// <summary>窗口的逻辑像素尺寸（CSS 像素）。</summary>
    protected abstract (int Width, int Height) LogicalSize { get; }

    /// <summary>前端就绪：补推一遍当前状态。</summary>
    protected abstract void OnFrontendReadyCore();

    /// <summary>前端发来的消息。</summary>
    /// <param name="message">消息（方法名 + 参数）。</param>
    protected abstract void OnBridgeMessageCore(BridgeMessage message);

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 句柄建好才能拿到 DPI，也才能初始化 WebView2。
        ApplyLogicalSize();
        StartWebView();
    }

    /// <summary>
    /// 把窗口客户区调成"逻辑像素尺寸 × 当前缩放"（物理像素）。
    ///
    /// <para>为什么不直接给 <see cref="System.Windows.Forms.Form.ClientSize"/> 逻辑值：
    /// WinForms 的尺寸属性是**物理像素**，而 WebView2 的页面视口是
    /// CSS 像素 = 物理 ÷ 缩放 —— 所以物理尺寸必须自己乘上去。</para>
    /// </summary>
    private void ApplyLogicalSize()
    {
        try
        {
            (int logicalWidth, int logicalHeight) = LogicalSize;
            double scale = CurrentScale();
            int width = (int)Math.Round(logicalWidth * scale, MidpointRounding.ToEven);
            int height = (int)Math.Round(logicalHeight * scale, MidpointRounding.ToEven);
            ClientSize = new Size(width, height);

            NativeMethods.GetClientRect(Handle, out NativeMethods.RECT client);
            NativeMethods.GetWindowRect(Handle, out NativeMethods.RECT window);
            _log.Info(
                $"窗口尺寸: 逻辑 {logicalWidth}x{logicalHeight} 缩放 {scale:F2} " +
                $"请求客户区 {width}x{height} 实际客户区 {client.Width}x{client.Height} " +
                $"窗口 {window.Width}x{window.Height}");
        }
        catch (Exception ex)
        {
            _log.Warning($"设置窗口尺寸失败: {ex.Message}");
        }
    }

    /// <summary>窗口所在显示器的缩放系数（拿不到按 1.0）。</summary>
    protected double CurrentScale()
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(Handle);
            if (dpi > 0)
            {
                return Math.Max(1.0, dpi / 96.0);
            }
        }
        catch (Exception)
        {
            // 忽略
        }

        return 1.0;
    }

    /// <summary>初始化 WebView2（只跑一次）。</summary>
    private void StartWebView()
    {
        if (_initStarted)
        {
            return;
        }

        _initStarted = true;
        _ = InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            _log.Info($"WebView2 初始化开始（静态资源目录: {WebViewEnvironment.StaticDirectory}）");

            // ⚠ WebView2 控件的子窗口必须带 WS_VISIBLE，否则 Chromium 认为窗口不可见：
            //   不布局 → getBoundingClientRect() 全 0 → 前端报不出命中区 →
            //   窗口不裁形（整块 512×260 会吃掉底下的点击）。
            // 父窗口走的是 Win32 的 SW_SHOWNOACTIVATE（不能抢焦点），WinForms 那边
            // 一直以为窗口不可见，子控件的 WS_VISIBLE 也就没被设上 —— 这里补一刀。
            IntPtr childHandle = _webView.Handle;
            NativeMethods.ShowWindow(childHandle, NativeMethods.SW_SHOWNOACTIVATE);
            _log.Debug($"WebView2 子窗口已置可见: 0x{childHandle.ToInt64():X}");

            NativeMethods.GetWindowRect(Handle, out NativeMethods.RECT formRect);
            NativeMethods.GetClientRect(Handle, out NativeMethods.RECT formClient);
            NativeMethods.GetWindowRect(childHandle, out NativeMethods.RECT childRect);
            int style = NativeMethods.GetWindowStyle(Handle);
            _log.Debug(
                $"尺寸实测: 窗口 {formRect.Width}x{formRect.Height} 客户区 {formClient.Width}x{formClient.Height} " +
                $"控件 {childRect.Width}x{childRect.Height} " +
                $"WinForms ClientSize {_webView.Parent!.ClientSize.Width}x{_webView.Parent.ClientSize.Height} " +
                $"控件WinForms {_webView.Width}x{_webView.Height} DeviceDpi {_webView.DeviceDpi} FormDpi {DeviceDpi} " +
                $"style=0x{style:X} (CAPTION={((style & 0x00C00000) == 0x00C00000)} THICKFRAME={((style & 0x00040000) != 0)})");

            CoreWebView2Environment env = await WebViewEnvironment.GetAsync().ConfigureAwait(true);
            _log.Info("WebView2 环境就绪");

            await _webView.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            _log.Info("WebView2 控件就绪");

            if (IsDisposed || Disposing)
            {
                return;
            }

            CoreWebView2 core = _webView.CoreWebView2;

            // 静态资源 → 虚拟主机。https:// 而不是 file://：file:// 下 origin 是 null，
            // 一批 Chromium 特性直接不可用（官方推荐做法）。
            core.SetVirtualHostNameToFolderMapping(
                WebViewEnvironment.VirtualHostName,
                WebViewEnvironment.StaticDirectory,
                CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.IsWebMessageEnabled = true;

            // 关掉 WebView2 自带的右键菜单：胶囊 / 面板的右键是**前端自绘**的
            // （球菜单、面板菜单），再叠一层浏览器菜单会露怯。
            core.Settings.AreDefaultContextMenusEnabled = false;

            core.NavigationCompleted += (_, args) =>
                _log.Info($"导航完成: 成功={args.IsSuccess} 错误码={args.WebErrorStatus}");

            _bridge = new WebViewBridge(core, BridgeTarget);
            _bridge.MessageReceived += OnBridgeMessage;

            // 导航到虚拟主机上的页面。
            _webView.Source = new Uri($"https://{WebViewEnvironment.VirtualHostName}/{PageFile}");
            _log.Info($"已导航: {_webView.Source}");
        }
        catch (Exception ex)
        {
            _log.Error($"WebView2 初始化失败: {ex}");
        }
    }

    /// <summary>前端消息入口（UI 线程）。</summary>
    private void OnBridgeMessage(object? sender, BridgeMessage message)
    {
        try
        {
            if (string.Equals(message.Method, "ready", StringComparison.Ordinal))
            {
                _log.Info("前端就绪");
                FrontendReady = true;
                OnFrontendReadyCore();
                FrontendReadyChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            OnBridgeMessageCore(message);
        }
        catch (Exception ex)
        {
            _log.Warning($"处理前端消息 '{message.Method}' 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 窗口参数：**强制无边框**。
    ///
    /// <para>为什么不能只靠 <c>Form.FormBorderStyle = None</c>：实测（175% 缩放）
    /// 窗口样式里仍带着 <c>WS_CAPTION</c>（style=0x6C10000），标题栏 + 边框
    /// 吃掉了 24×64 的**非客户区** —— 客户区只剩 872×391，
    /// 而窗口矩形是 896×455。两个后果：
    /// <list type="bullet">
    /// <item>WebView2 的视口尺寸与 CSS 里写死的 512×260 对不上；</item>
    /// <item>命中区（SetWindowRgn）的坐标是相对窗口左上角的，
    /// 差出来的非客户区会让裁形整体偏移。</item>
    /// </list></para>
    ///
    /// <para>Python 版（pywebview <c>frameless=True</c>）的窗口同样没有非客户区，
    /// 这里去掉才与它对齐。</para>
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.Style &= ~(NativeMethods.WS_CAPTION
                | NativeMethods.WS_THICKFRAME
                | NativeMethods.WS_BORDER
                | NativeMethods.WS_DLGFRAME);
            cp.Style |= NativeMethods.WS_POPUP;
            return cp;
        }
    }

    /// <summary>
    /// 视口自检：把页面视口的实际尺寸与设备像素比读出来（诊断用）。
    ///
    /// <para>Chromium 的设备像素比必须与窗口的 DPI 缩放对上：
    /// 对上了，CSS 逻辑像素与物理像素才是 1:1.75 的关系，
    /// 前端量出来的矩形乘上缩放系数才是物理像素。</para>
    /// </summary>
    protected async Task LogViewportAsync()
    {
        WebViewBridge? bridge = Bridge;
        if (bridge is null)
        {
            return;
        }

        try
        {
            await Task.Delay(1500).ConfigureAwait(true);
            string json = await bridge.EvaluateAsync(
                "(function(){var p=document.getElementById('pill');var r=p?p.getBoundingClientRect():null;" +
                "return {iw:window.innerWidth,ih:window.innerHeight,dpr:window.devicePixelRatio," +
                "cw:document.documentElement?document.documentElement.clientWidth:0," +
                "ch:document.documentElement?document.documentElement.clientHeight:0," +
                "ow:window.outerWidth,oh:window.outerHeight," +
                "pill:r?[r.left,r.top,r.width,r.height]:null," +
                "hasPywebview:!!window.pywebview,hasApi:!!(window.pywebview&&window.pywebview.api)," +
                "readyState:document.readyState,bodyClass:document.body?document.body.className:''," +
                "offset:p?[p.offsetLeft,p.offsetTop,p.offsetWidth,p.offsetHeight]:null};})()").ConfigureAwait(true);
            _log.Debug($"视口自检: {json}");
        }
        catch (Exception ex)
        {
            _log.Debug($"视口自检失败: {ex.Message}");
        }
    }

    /// <summary>往前端推一个方法（前端没就绪时**不推**）。</summary>
    /// <param name="method">前端方法名。</param>
    /// <param name="args">参数（按位置）。</param>
    protected void Push(string method, params object?[] args)
    {
        WebViewBridge? bridge = _bridge;
        if (bridge is null || !FrontendReady)
        {
            return;
        }

        bridge.Invoke(method, args);
    }

    /// <summary>
    /// 把键盘焦点交给窗口里的 WebView2 控件。
    ///
    /// <para>文字条被按下时用：窗口默认带 <c>WS_EX_NOACTIVATE</c>（不抢用户输入焦点，
    /// 设计使然），鼠标点击不会让它成为前台 —— 于是 WebView2 拿不到键盘焦点，
    /// 文字条里选字后的 Ctrl+C、右键菜单的复制全都使不上劲。</para>
    /// </summary>
    protected void FocusWebView()
    {
        try
        {
            _webView.Focus();
        }
        catch (Exception ex)
        {
            _log.Debug($"WebView2 聚焦失败: {ex.Message}");
        }
    }
}
