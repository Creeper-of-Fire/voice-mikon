using System;
using System.Windows.Forms;

namespace VoiceMikonCSharp.WebView;

/// <summary>
/// UI 线程调度：把"别的线程发出来的事"搬回 UI 线程。
///
/// <para><b>为什么需要</b>：presenter 的事件在**触发它的那条线程**上发出
/// （模型加载线程 / 识别线程 / 电平泵线程），而 WebView2 是 STA COM 对象 ——
/// 它的所有方法都必须在创建它的 UI 线程上调用。
/// Avalonia 版靠 <c>Dispatcher.UIThread.Post</c>，这里换成 WinForms 的
/// <c>Control.BeginInvoke</c>，语义完全一样。</para>
///
/// <para><b>锚点</b>：WinForms 的 <c>BeginInvoke</c> 需要一个已经建好句柄的控件。
/// 装配时把胶囊窗口绑上来（<see cref="Bind"/>），之后所有投递都走它。</para>
/// </summary>
public static class UiDispatch
{
    private static Control? _anchor;

    /// <summary>把 UI 线程调度锚定到某个控件（装配时调一次）。</summary>
    /// <param name="anchor">UI 线程上的控件（句柄必须已创建）。</param>
    public static void Bind(Control anchor) => _anchor = anchor;

    /// <summary>
    /// 投递一个动作到 UI 线程（**异步**，不等待执行）。
    ///
    /// <para>锚点还没绑好时退化成"就地执行"—— 那时本来就在 UI 线程上
    /// （装配阶段），直接跑不会错。</para>
    /// </summary>
    /// <param name="action">要执行的动作。</param>
    public static void Post(Action action)
    {
        if (action is null)
        {
            return;
        }

        Control? anchor = _anchor;
        if (anchor is null || !anchor.IsHandleCreated)
        {
            action();
            return;
        }

        try
        {
            anchor.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // 窗口已经关了 —— 这时候没什么可推的了。
        }
        catch (InvalidOperationException)
        {
            // 句柄正在销毁 —— 同上。
        }
    }

    /// <summary>在 UI 线程上**同步**执行一个动作（并等它跑完）。</summary>
    /// <param name="action">要执行的动作。</param>
    public static void Invoke(Action action)
    {
        if (action is null)
        {
            return;
        }

        Control? anchor = _anchor;
        if (anchor is null || !anchor.IsHandleCreated)
        {
            action();
            return;
        }

        if (anchor.InvokeRequired)
        {
            anchor.Invoke(action);
            return;
        }

        action();
    }
}
