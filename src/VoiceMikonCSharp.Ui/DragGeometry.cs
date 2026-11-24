using Avalonia;

namespace VoiceMikonCSharp.Ui;

/// <summary>
/// 拖拽几何（**纯函数**）：鼠标位移 → 窗口位移。
///
/// <para>翻译自 voice-mikon/src/voice_mikon/host/drag.py 的 <c>DragSession.move</c>：
/// 前端持续发**绝对目标位置**（逻辑像素），这边换算成物理像素、夹回允许的范围，
/// 再挪窗口。这里只保留"换算"这一段纯逻辑 —— 夹取在
/// <see cref="Placement"/>，挪窗口在 <c>CapsuleWindow</c>。</para>
///
/// <para>为什么不用"用窗口尺寸 + 常量反推球位置"那套（Python 版的
/// <c>window_scale</c> / <c>window_size</c>）：那套是为"窗口固定 512×260、球在里面"
/// 设计的；Avalonia 版的窗口位置**就是**球的位置（球在窗口底边居中，偏移是常量），
/// 直接用鼠标位移更简单，也不会因为窗口高度变化而算飞。</para>
/// </summary>
public static class DragGeometry
{
    /// <summary>
    /// 拖动中窗口的新位置（物理像素）。
    ///
    /// <para>算法（与 Python 版同款）：
    /// <list type="number">
    /// <item>鼠标在屏幕上挪了多远，是**物理**像素 —— 它跟缩放无关。</item>
    /// <item>但窗口坐标在 Python 版是**逻辑**像素，所以先除以**按下时的**缩放
    /// 折成逻辑位移（= 前端看到的位移）。</item>
    /// <item>再乘上**当前**的缩放折回物理像素 —— 这一步对应 Python 的
    /// <c>move_window_to(hwnd, round(nx * scale), round(ny * scale))</c>，
    /// 其中 <c>scale</c> 取的是**当前**窗口所在显示器的缩放。</item>
    /// </list>
    /// 单屏上 scale 不变 → 窗口与鼠标 1:1 跟手；拖到另一块缩放不同的屏上，
    /// 位移按新屏的缩放换算（与 Python 版的行为一致）。</para>
    /// </summary>
    /// <param name="windowStart">按下时的窗口左上角（物理像素）。</param>
    /// <param name="deltaXPhys">鼠标在屏幕上的 X 位移（物理像素）。</param>
    /// <param name="deltaYPhys">鼠标在屏幕上的 Y 位移（物理像素）。</param>
    /// <param name="scaleAtPress">按下时的渲染缩放。</param>
    /// <param name="scaleNow">当前的渲染缩放（跨屏时会变）。</param>
    public static PixelPoint WindowPosition(
        PixelPoint windowStart,
        double deltaXPhys,
        double deltaYPhys,
        double scaleAtPress,
        double scaleNow)
    {
        double press = scaleAtPress > 0 ? scaleAtPress : 1.0;
        double now = scaleNow > 0 ? scaleNow : 1.0;

        // 物理位移 → 逻辑位移（前端看到的那个量）
        double dxLogical = deltaXPhys / press;
        double dyLogical = deltaYPhys / press;

        // 逻辑位移 → 物理位移（当前缩放）
        int dx = (int)Math.Round(dxLogical * now, MidpointRounding.ToEven);
        int dy = (int)Math.Round(dyLogical * now, MidpointRounding.ToEven);

        return new PixelPoint(windowStart.X + dx, windowStart.Y + dy);
    }

    /// <summary>
    /// 这一下拖动有没有真的挪过位置（物理像素比较）。
    ///
    /// 点球（想开始录音）和拖球（想挪位置）走的是同一套按下/松手流程 ——
    /// 靠"有没有真的动过"区分（与 Python 的 <c>DragSession._moved</c> 同款）。
    /// </summary>
    /// <param name="start">按下时的窗口左上角。</param>
    /// <param name="current">当前窗口左上角。</param>
    public static bool HasMoved(PixelPoint start, PixelPoint current)
        => start.X != current.X || start.Y != current.Y;
}
