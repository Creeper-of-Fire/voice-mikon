using System;
using System.Globalization;

namespace VoiceMikonCSharp.Ui;

/// <summary>
/// 面板窗口的位置记忆（**纯函数**）。
///
/// <para>翻译自 voice-mikon 的 <c>host/panel.py</c>：<c>_parse_geometry</c>（L480）
/// 与 <c>_save_geometry</c>（L500）。</para>
///
/// <para><b>格式必须与 Python 版逐字兼容</b>：<c>"x,y,w,h"</c>，四个整数，
/// 单位是**逻辑像素**（物理像素 ÷ 缩放系数）—— 换一台缩放率不同的显示器时
/// 位置还能落在合理的地方。用户拿旧的 <c>data/config.json</c> 直接能用。</para>
/// </summary>
public static class PanelGeometry
{
    /// <summary>
    /// 面板宽度（逻辑像素）。来源：voice-mikon 的 <c>host/panel.py</c> <c>BASE_W = 320</c>。
    ///
    /// <para>为什么固定 320：配置行的分段控件（"本地/网络""0.6B/1.7B""自动/白天/黑夜"）
    /// 加上标签，320 刚好不挤。</para>
    /// </summary>
    public const int BaseWidth = 320;

    /// <summary>
    /// 面板高度（逻辑像素）。来源：voice-mikon 的 <c>host/panel.py</c> <c>BASE_H = 340</c>。
    ///
    /// <para>Python 那边是**下限**（内容装不下时窗口自己长高）；Avalonia 侧内容是
    /// 固定布局，用不着量高度，固定 340 即可。</para>
    /// </summary>
    public const int BaseHeight = 340;

    /// <summary>
    /// 解析保存过的窗口位置。
    ///
    /// <para>格式 <c>"x,y,w,h"</c>，四个整数。解析不出来（空串 / 段数不对 /
    /// 某段不是整数）就返回 false —— 调用方退回默认摆位，**不崩**。</para>
    /// </summary>
    /// <param name="geometry">配置里的 <c>panel_geometry</c> 原串。</param>
    /// <param name="x">解析出来的左上角 x（逻辑像素）。</param>
    /// <param name="y">解析出来的左上角 y（逻辑像素）。</param>
    public static bool TryParse(string? geometry, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (string.IsNullOrWhiteSpace(geometry))
        {
            return false;
        }

        string[] parts = geometry.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int px)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int py))
        {
            return false;
        }

        // 宽高两段只做格式校验（Python 那边是 _w / _h，同样不用）。
        if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        x = px;
        y = py;
        return true;
    }

    /// <summary>
    /// 编码窗口位置（逻辑像素）成配置里的 <c>"x,y,w,h"</c>。
    ///
    /// <para>与 Python 的 <c>f"{self._pos_x},{self._pos_y},{BASE_W},{BASE_H}"</c>
    /// 逐字一致 —— 宽高写的是**基准尺寸**，不是当次窗口的实际尺寸
    /// （Python 那边也是这么写的：面板尺寸固定，只有位置会变）。</para>
    /// </summary>
    /// <param name="x">左上角 x（逻辑像素）。</param>
    /// <param name="y">左上角 y（逻辑像素）。</param>
    /// <param name="width">宽度（默认 <see cref="BaseWidth"/>）。</param>
    /// <param name="height">高度（默认 <see cref="BaseHeight"/>）。</param>
    public static string Format(int x, int y, int width = BaseWidth, int height = BaseHeight)
        => string.Create(CultureInfo.InvariantCulture, $"{x},{y},{width},{height}");
}
