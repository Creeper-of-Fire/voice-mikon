using System.IO;

namespace VoiceMikonCSharp.Core;

/// <summary>
/// 附属工具（<c>companions/</c>）的定位。
///
/// <para><b>零 import</b>：附属工具是**独立进程**，主程序只经 <c>Process.Start</c>
/// 拉起它 —— 不引用它的任何程序集、不认识它的类型。这条规矩在 C# 里的含义：
/// 附属工具项目**不引用任何 <c>VoiceMikonCSharp.*</c> 项目**；主程序这边也不 Reference
/// 它的程序集（构建期把产物复制过去**不算引用**）。</para>
///
/// <para>本类只做一件事：**算出附属工具的可执行文件在哪**（纯函数，不启动任何东西）。
/// 启动是宿主层（<c>GuiHost</c>）的事。</para>
/// </summary>
public static class Companions
{
    /// <summary>附属工具目录名（相对程序目录）。</summary>
    public const string DirectoryName = "companions";

    /// <summary>二维码扫描工具的可执行文件名。</summary>
    public const string QrScannerFileName = "QrScanner.exe";

    /// <summary>
    /// 二维码扫描工具的**期望路径**（不判断文件是否存在）。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录（通常是 <c>AppContext.BaseDirectory</c>）。</param>
    public static string QrScannerPath(string baseDirectory)
        => Path.Combine(baseDirectory, DirectoryName, "QrScanner", QrScannerFileName);

    /// <summary>
    /// 定位二维码扫描工具的可执行文件；**文件不存在返回 null**。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录（通常是 <c>AppContext.BaseDirectory</c>）。</param>
    public static string? LocateQrScanner(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        string path = QrScannerPath(baseDirectory);
        return File.Exists(path) ? path : null;
    }
}
