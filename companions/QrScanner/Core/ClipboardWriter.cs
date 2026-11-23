using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace QrScanner.Core;

/// <summary>
/// 剪贴板写入。
///
/// <para>走 WinForms 的 <see cref="Clipboard.SetText"/> —— 本工具是**独立进程**，
/// 不参与主程序那套"剪贴板写入必须带 owner"的规矩（那条规矩属于主程序的
/// <c>ClipboardService</c>，工具与它无关）。</para>
///
/// <para>剪贴板可能正被别的程序占着（剪贴板管理器之类）：短重试几次。</para>
/// </summary>
public static class ClipboardWriter
{
    /// <summary>把文本真写进系统剪贴板。成功返回 true。</summary>
    public static bool TrySetText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }
}
