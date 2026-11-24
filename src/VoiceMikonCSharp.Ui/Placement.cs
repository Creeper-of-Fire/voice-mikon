using System;
using System.Globalization;
using VoiceMikonCSharp.Shell;

namespace VoiceMikonCSharp.Ui;

/// <summary>
/// 胶囊摆位计算（**纯逻辑**，不碰任何窗口句柄）。
///
/// <para>翻译自 voice-mikon/src/voice_mikon/host/placement.py，函数名、常量、
/// 四舍五入规则**逐一对应**（Python 的 <c>round()</c> 是"银行家舍入"，
/// C# 的 <see cref="Math.Round(double)"/> 默认也是 <see cref="MidpointRounding.ToEven"/>，
/// 两边一致 —— 这里刻意不用 <c>OverlayGeometry.ToPixels</c>，那个是"远离零"舍入）。</para>
///
/// <para>坐标约定（与 Python 版一致）：
/// <list type="bullet">
/// <item><b>物理像素</b>：Win32 的窗口坐标是物理像素；本类对外的坐标全是物理像素。</item>
/// <item><b>逻辑像素</b>：用户看到的尺寸 = 物理 ÷ 缩放。位置记忆存的是逻辑偏移。</item>
/// </list></para>
///
/// <para>与 Python 版的一处**故意差异**：Python 版窗口是固定 512×260，球在窗口底边居中；
/// Avalonia 版窗口宽度仍是 512（K4a 定值，见 <c>CapsuleWindow.WindowWidth</c>），
/// 但**高度跟着内容走**。所以本类的 <c>winW</c>/<c>winH</c> 是参数而不是常量 ——
/// 调用方传窗口的**实际**物理尺寸即可，公式与 Python 版逐字相同。</para>
///
/// <para><b>位置记忆的格式必须与 Python 版兼容</b>（用户拿旧的 config.json 要能用）：
/// 存的是 <c>"dx,dy"</c> —— 球相对所在工作区左上角的**逻辑**像素偏移，
/// 外加显示器设备名（<c>orb.monitor</c>）。</para>
/// </summary>
public static class Placement
{
    /// <summary>
    /// 胶囊与工作区边缘的最小留白（物理像素）。
    /// 来源：placement.py 的 <c>EDGE_MARGIN = 8</c>。
    /// </summary>
    public const int EdgeMargin = 8;

    /// <summary>
    /// 球的直径（逻辑像素）。来源：placement.py 的 <c>ORB_SIZE = 40</c>。
    ///
    /// ⚠ 必须等于胶囊的高度（capsule.css 的 <c>.recording-pill { height: 40px }</c>）：
    /// 球不是另一个元素 —— 它就是这颗胶囊收窄成的正圆。
    /// </summary>
    public const int OrbSize = 40;

    /// <summary>
    /// 球的下边到窗口下边（逻辑像素）。来源：placement.py 的 <c>ORB_INSET_Y = 10</c>
    /// （= CSS <c>.stack</c> 的 padding-bottom 10px）。
    /// </summary>
    public const int OrbInsetY = 10;

    /// <summary>
    /// 球与工作区边缘的最小留白（逻辑像素）。来源：placement.py 的 <c>ORB_MARGIN = 16</c>。
    /// </summary>
    public const int OrbMargin = 16;

    /// <summary>
    /// 把窗口左上角夹进工作区（留 <paramref name="margin"/> 的边距）。
    ///
    /// 窗口比工作区还大时（极小分辨率 + 高缩放）不能算出负的边界 ——
    /// <c>max(...)</c> 保证左/上界不小于最小留白，至少不会跑到屏幕外。
    /// </summary>
    /// <param name="x">窗口左上角 X（物理像素）。</param>
    /// <param name="y">窗口左上角 Y（物理像素）。</param>
    /// <param name="winW">窗口宽（物理像素）。</param>
    /// <param name="winH">窗口高（物理像素）。</param>
    /// <param name="workArea">工作区矩形（物理像素）。</param>
    /// <param name="margin">边距（逻辑像素，默认 <see cref="EdgeMargin"/>）。</param>
    public static (int X, int Y) ClampToWorkArea(
        int x,
        int y,
        int winW,
        int winH,
        NativeMethods.RECT workArea,
        int margin = EdgeMargin)
    {
        int minX = workArea.Left + margin;
        int minY = workArea.Top + margin;
        int maxX = Math.Max(minX, workArea.Right - winW - margin);
        int maxY = Math.Max(minY, workArea.Bottom - winH - margin);
        return (Math.Min(Math.Max(x, minX), maxX), Math.Min(Math.Max(y, minY), maxY));
    }

    /// <summary>
    /// 把窗口左上角的物理坐标存成"相对工作区的逻辑偏移"。
    ///
    /// 存逻辑偏移而不是绝对坐标：换一台缩放率不同的显示器时，胶囊相对工作区的位置不变
    /// （不会飞到屏幕外或挤到边角上）。
    ///
    /// 存相对工作区而不是绝对屏幕坐标：多屏场景下，副屏的绝对坐标里含有该屏的物理原点，
    /// 换机器接的屏不一样就全错。
    ///
    /// 格式 <c>"dx,dy"</c> —— **与 Python 版逐字一致**（旧的 config.json 能直接用）。
    /// </summary>
    /// <param name="xPhys">X（物理像素）。</param>
    /// <param name="yPhys">Y（物理像素）。</param>
    /// <param name="workArea">工作区矩形（物理像素）。</param>
    /// <param name="scale">该显示器的缩放系数。</param>
    public static string EncodeOffset(int xPhys, int yPhys, NativeMethods.RECT workArea, double scale)
    {
        double divisor = scale > 0 ? scale : 1.0;
        int dx = (int)Math.Round((xPhys - workArea.Left) / divisor, MidpointRounding.ToEven);
        int dy = (int)Math.Round((yPhys - workArea.Top) / divisor, MidpointRounding.ToEven);
        return string.Create(CultureInfo.InvariantCulture, $"{dx},{dy}");
    }

    /// <summary>解析 <c>"dx,dy"</c>；解析不了返回 null（调用方走兜底）。</summary>
    /// <param name="geometry">配置里的 <c>orb.geometry</c> 字符串。</param>
    public static (int X, int Y)? DecodeOffset(string? geometry)
    {
        if (string.IsNullOrEmpty(geometry))
        {
            return null;
        }

        string[] parts = geometry.Split(',');
        if (parts.Length != 2)
        {
            return null;
        }

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dx)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dy))
        {
            return null;
        }

        return (dx, dy);
    }

    /// <summary>
    /// 球在窗口里底边居中 → 球左边到窗口左边的偏移（逻辑像素）。
    ///
    /// 球和胶囊共用底边居中线，横坐标就对齐了 —— 展开时不用挪位置。
    /// </summary>
    /// <param name="clientW">客户区宽（逻辑像素）。</param>
    /// <param name="orbSize">球的直径（逻辑像素）。</param>
    public static int OrbInsetX(int clientW, int orbSize = OrbSize)
        => Math.Max(0, (clientW - orbSize) / 2);

    /// <summary>
    /// 窗口左上角（物理像素）→ 球的左上角（物理像素）。
    ///
    /// 球是底边居中的：横坐标 = 窗口左边 + (窗口宽 − 球宽) / 2，
    /// 纵坐标 = 窗口底边 − (下边距 + 球高)。
    /// </summary>
    public static (int X, int Y) OrbScreenPosition(
        int winX,
        int winY,
        int winW,
        int winH,
        double scale,
        int orbSize = OrbSize,
        int insetY = OrbInsetY)
    {
        int clientW = (int)Math.Round(winW / scale, MidpointRounding.ToEven);
        int x = winX + (int)Math.Round(OrbInsetX(clientW, orbSize) * scale, MidpointRounding.ToEven);
        int y = winY + winH - (int)Math.Round((insetY + orbSize) * scale, MidpointRounding.ToEven);
        return (x, y);
    }

    /// <summary>
    /// 把球夹回"允许待着"的范围内（物理像素）。
    ///
    /// 纵向比横向严：球拖得太靠上，展开后的胶囊（文字条 + 胶囊）会出屏，
    /// 所以球的上边至少要留出"预留高度 − (下边距 + 球高)"的高度。
    /// </summary>
    /// <param name="reserveHeight">
    /// 球上方要预留的窗口高度（物理像素）。null = 按 <paramref name="winH"/> 推
    /// （= 窗口整高的做法；Python 版窗口固定 260 时就是这个）。
    /// 窗口高度随内容变时，预留量要按**最大**形态算 ——
    /// 否则窗口一变高，纵向夹取就把球往下推，球在屏幕上会跳。
    /// </param>
    public static (int X, int Y) ClampOrbPosition(
        int orbX,
        int orbY,
        int winW,
        int winH,
        double scale,
        NativeMethods.RECT workArea,
        int orbSize = OrbSize,
        int insetY = OrbInsetY,
        int margin = OrbMargin,
        int? reserveHeight = null)
    {
        int sizePhys = (int)Math.Round(orbSize * scale, MidpointRounding.ToEven);
        int edge = (int)Math.Round(margin * scale, MidpointRounding.ToEven);

        // 球上方要留给胶囊（文字条 + 胶囊）的高度
        int head = Math.Max(
            0,
            (reserveHeight ?? winH) - (int)Math.Round((insetY + orbSize) * scale, MidpointRounding.ToEven));

        int minX = workArea.Left + edge;
        int maxX = Math.Max(minX, workArea.Right - edge - sizePhys);
        int minY = workArea.Top + head;
        int maxY = Math.Max(minY, workArea.Bottom - edge - sizePhys);

        return (Math.Min(Math.Max(orbX, minX), maxX), Math.Min(Math.Max(orbY, minY), maxY));
    }

    /// <summary>
    /// 球的位置（物理像素）→ 窗口左上角（物理像素）。
    ///
    /// 球的位置先夹回允许的范围，再反推窗口该摆哪 —— 拖到屏幕边上也不会把窗口甩到屏幕外。
    /// </summary>
    public static (int X, int Y) OrbWindowPosition(
        int orbX,
        int orbY,
        int winW,
        int winH,
        double scale,
        NativeMethods.RECT workArea,
        int orbSize = OrbSize,
        int insetY = OrbInsetY,
        int margin = OrbMargin,
        int? reserveHeight = null)
    {
        (int ox, int oy) = ClampOrbPosition(orbX, orbY, winW, winH, scale, workArea, orbSize, insetY, margin, reserveHeight);
        int clientW = (int)Math.Round(winW / scale, MidpointRounding.ToEven);
        int x = ox - (int)Math.Round(OrbInsetX(clientW, orbSize) * scale, MidpointRounding.ToEven);
        int y = oy + (int)Math.Round((insetY + orbSize) * scale, MidpointRounding.ToEven) - winH;
        return (x, y);
    }

    /// <summary>
    /// 默认摆在工作区**右下角**（物理像素）。
    ///
    /// 右下角是这类悬浮球的惯例位置：不挡中间的编辑区，也不挡任务栏。
    /// </summary>
    public static (int X, int Y) DefaultOrbPosition(
        NativeMethods.RECT workArea,
        double scale,
        int orbSize = OrbSize,
        int margin = OrbMargin)
    {
        int edge = (int)Math.Round(margin * scale, MidpointRounding.ToEven);
        int sizePhys = (int)Math.Round(orbSize * scale, MidpointRounding.ToEven);
        return (workArea.Right - edge - sizePhys, workArea.Bottom - edge - sizePhys);
    }

    /// <summary>
    /// 记忆的偏移 → 窗口左上角（物理像素）；记忆无效时返回 null。
    ///
    /// 存的是**球**的偏移（不是窗口的）：球是用户真正盯着的那一小块，
    /// 记住它在哪，换屏之后它还在同一个相对位置。
    /// </summary>
    public static (int X, int Y)? OrbPositionFromGeometry(
        string? geometry,
        NativeMethods.RECT workArea,
        double scale,
        int winW,
        int winH,
        int orbSize = OrbSize,
        int insetY = OrbInsetY,
        int margin = OrbMargin,
        int? reserveHeight = null)
    {
        (int dx, int dy)? offset = DecodeOffset(geometry);
        if (offset is null)
        {
            return null;
        }

        int orbX = workArea.Left + (int)Math.Round(offset.Value.dx * scale, MidpointRounding.ToEven);
        int orbY = workArea.Top + (int)Math.Round(offset.Value.dy * scale, MidpointRounding.ToEven);
        return OrbWindowPosition(orbX, orbY, winW, winH, scale, workArea, orbSize, insetY, margin, reserveHeight);
    }
}
