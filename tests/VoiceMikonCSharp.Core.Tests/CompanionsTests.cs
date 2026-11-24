using System.IO;
using VoiceMikonCSharp.Core;
using Xunit;

namespace VoiceMikonCSharp.Core.Tests;

/// <summary>
/// 附属工具定位的单元测试。
///
/// <para>只测**路径算得对不对**、**文件不在时给不给 null** ——
/// 真的拉起进程属于"碰桌面"，不写自动化测试（用户实测）。</para>
/// </summary>
public class CompanionsTests
{
    [Fact]
    public void 期望路径在_companions_子目录下()
    {
        string path = Companions.QrScannerPath(Path.Combine("C:", "app"));

        Assert.Equal(
            Path.Combine("C:", "app", "companions", "QrScanner", "QrScanner.exe"),
            path);
    }

    [Fact]
    public void 文件存在时定位到它()
    {
        string root = Directory.CreateTempSubdirectory("voice-mikon-csharp_companions_").FullName;

        try
        {
            string exe = Path.Combine(root, "companions", "QrScanner", "QrScanner.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "占位：测试只需要这个文件在");

            Assert.Equal(exe, Companions.LocateQrScanner(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 文件不存在时返回_null()
    {
        string root = Directory.CreateTempSubdirectory("voice-mikon-csharp_companions_").FullName;

        try
        {
            Assert.Null(Companions.LocateQrScanner(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 空目录参数返回_null()
    {
        Assert.Null(Companions.LocateQrScanner(string.Empty));
    }
}
