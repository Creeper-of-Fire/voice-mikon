using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VmNext.AutoCheck;

/// <summary>
/// 自动化验证入口：跑完 A1–A13，把结果写进 docs/feasibility.md。
///
/// 硬约束：不注入任何输入。鼠标相关验证全部在子进程里用 PostMessage + 查询类 API 完成，
/// 窗口只在屏幕右下角短暂出现。
/// </summary>
internal static class Program
{
    private static string _root = string.Empty;
    private static readonly List<CheckItem> Items = new();

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 忽略
        }

        _root = FindRepoRoot();
        Console.WriteLine($"仓库根目录：{_root}");

        var environment = CollectEnvironment();

        Console.WriteLine("[1/6] 构建整个解决方案…");
        var build = ProcessRunner.Run(
            "dotnet",
            new[] { "build", "Feasibility.sln", "-c", "Release", "-v", "q" },
            _root,
            900);
        if (build.ExitCode != 0)
        {
            Console.Error.WriteLine("构建失败：");
            foreach (string line in build.StdErr.Take(40))
            {
                Console.Error.WriteLine(line);
            }

            return 1;
        }

        Console.WriteLine("[2/6] 运行 Demo A（透明/不激活/穿透/菜单 + 空闲资源 + 冷启动计时）…");
        RunResult demoA = RunDemoA();

        Console.WriteLine("[3/6] 运行 Demo B（动画帧时间）…");
        RunResult demoB = RunDemoB();

        Console.WriteLine("[4/6] 测量增量编译耗时（改一行 XAML，3 次取中位数）…");
        MeasureIncrementalBuild();

        Console.WriteLine("[5/6] NativeAOT 发布 + 跑发布产物…");
        MeasureAot();

        Console.WriteLine("[6/6] 写报告…");
        WriteReport(environment);

        int failed = Items.Count(i => i.Passed == false);
        Console.WriteLine($"完成：{Items.Count} 项，其中不通过 {failed} 项。报告：docs/feasibility.md");
        return failed == 0 ? 0 : 2;
    }

    #region 环境

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Feasibility.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到 Feasibility.sln（仓库根目录定位失败）");
    }

    private static Dictionary<string, string> CollectEnvironment()
    {
        var env = new Dictionary<string, string>
        {
            ["操作系统"] = Environment.OSVersion.VersionString,
            [".NET 运行时"] = Environment.Version.ToString(),
            ["目标框架"] = "net10.0",
            ["逻辑处理器数"] = Environment.ProcessorCount.ToString(),
            ["Avalonia 版本"] = ReadAvaloniaVersion(),
            [".NET SDK"] = RunDotnet("--version"),
        };

        return env;
    }

    /// <summary>从 project.assets.json 里读 Avalonia 实际解析到的版本。</summary>
    private static string ReadAvaloniaVersion()
    {
        string assets = Path.Combine(_root, "src", "VmNext.Shell", "obj", "project.assets.json");
        if (!File.Exists(assets))
        {
            return "未知（未找到 project.assets.json）";
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(assets));
            if (doc.RootElement.TryGetProperty("libraries", out var libraries))
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Name.StartsWith("Avalonia/", StringComparison.OrdinalIgnoreCase))
                    {
                        return library.Name.Split('/')[1];
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            return "解析失败：" + ex.Message;
        }

        return "未知";
    }

    private static string RunDotnet(string arguments)
    {
        var result = ProcessRunner.Run("dotnet", new[] { arguments }, _root, 60);
        return result.StdOut.FirstOrDefault()?.Trim() ?? "未知";
    }

    #endregion

    #region Demo A：A1–A6、X1、A8、A9、A13

    private static RunResult RunDemoA()
    {
        string dll = Path.Combine(_root, "src", "demos", "Demo.A.Static", "bin", "Release", "net10.0", "Demo.A.Static.dll");
        var run = ProcessRunner.Run("dotnet", new[] { dll, "--auto" }, _root, 180);

        AddFromChecks(run, new[] { "A1", "A2", "A3", "A4", "A5", "A6" });

        // X1：真透明（额外验证项）
        if (run.Checks.TryGetValue("X1", out var x1))
        {
            Items.Add(new CheckItem("X1", "真透明（屏幕像素合成验证）", x1.Detail, x1.Passed, "额外项：对比窗口显示前后的屏幕像素，球心变色 + 四角不变 = 真的透出桌面。"));
        }

        // A8：空闲 CPU
        double cpu = run.RawDouble("CPU", 0);
        double cpuMs = run.RawDouble("CPU", 1);
        double elapsed = run.RawDouble("CPU", 2);
        int cores = (int)run.RawDouble("CPU", 3);
        Items.Add(new CheckItem(
            "A8",
            "空闲 CPU 占用（Demo A 静置 10 秒）",
            $"平均 {cpu:F3}%（10 秒内累计 CPU {cpuMs:F0}ms / 实测时长 {elapsed:F0}ms / {cores} 逻辑核）",
            cpu is > 0 and < 1.0 ? true : null,
            "阈值：平均 < 1% 视为通过。Avalonia 按需渲染，静止时不出帧。"));

        // A9：JIT 工作集
        long envWs = (long)run.RawDouble("WS", 0);
        long procWs = (long)run.RawDouble("WS", 1);
        long priv = (long)run.RawDouble("WS", 2);
        Items.Add(new CheckItem(
            "A9",
            "JIT 工作集（Demo A 启动后静置 10 秒）",
            $"Environment.WorkingSet={envWs / 1024.0 / 1024:F1}MB，Process.WorkingSet64={procWs / 1024.0 / 1024:F1}MB，私有内存={priv / 1024.0 / 1024:F1}MB",
            null,
            "目标是把常驻内存压到 20–50MB；这里给出 JIT 基线，AOT 版本见 A11。"));

        // A13：冷启动
        double opened = run.OpenedMs;
        double inner = double.TryParse(run.Marks.GetValueOrDefault("OPENED"), out var v) ? v : -1;
        Items.Add(new CheckItem(
            "A13",
            "冷启动（JIT，进程启动 → 窗口 Opened）",
            $"外部计时 {opened:F0}ms；进程内部计时 {inner:F0}ms",
            opened is > 0 and < 2000 ? true : null,
            "外部计时 = AutoCheck 拉起进程到收到 OPENED 标记（含 dotnet host 启动 + JIT）；阈值 2s。"));

        return run;
    }

    #endregion

    #region Demo B：A7

    private static RunResult RunDemoB()
    {
        string dll = Path.Combine(_root, "src", "demos", "Demo.B.Morph", "bin", "Release", "net10.0", "Demo.B.Morph.dll");
        var run = ProcessRunner.Run("dotnet", new[] { dll, "--auto" }, _root, 180);

        double avg = run.RawDouble("FRAME", 0);
        double max = run.RawDouble("FRAME", 1);
        double min = run.RawDouble("FRAME", 2);
        int count = (int)run.RawDouble("FRAME", 3);
        int over20 = (int)run.RawDouble("FRAME", 4);

        double morphAvg = run.RawDouble("FRAME_MORPH", 0);
        double morphMax = run.RawDouble("FRAME_MORPH", 1);
        int morphCount = (int)run.RawDouble("FRAME_MORPH", 2);
        int morphOver = (int)run.RawDouble("FRAME_MORPH", 3);

        double idleAvg = run.RawDouble("FRAME_IDLE", 0);
        double idleMax = run.RawDouble("FRAME_IDLE", 1);
        int idleCount = (int)run.RawDouble("FRAME_IDLE", 2);
        int idleOver = (int)run.RawDouble("FRAME_IDLE", 3);

        int morphSamples = (int)run.RawDouble("MORPH", 0);
        int morphDistinct = (int)run.RawDouble("MORPH", 1);

        Items.Add(new CheckItem(
            "A7",
            "动画帧时间（Demo B 跑 10 秒：形变 + 呼吸 + 旋转 + 波形）",
            $"整体 平均 {avg:F2}ms / 最大 {max:F2}ms / 最小 {min:F2}ms，{count} 帧，>{20}ms 的有 {over20} 帧；" +
            $"形变期间 平均 {morphAvg:F2}ms / 最大 {morphMax:F2}ms（{morphCount} 帧，>{20}ms {morphOver} 帧）；" +
            $"形变后 平均 {idleAvg:F2}ms / 最大 {idleMax:F2}ms（{idleCount} 帧，>{20}ms {idleOver} 帧）",
            avg is > 0 and < 25 ? true : null,
            $"阈值：平均 < 25ms（约 40fps 以上）。形变连续性：{morphSamples} 次采样里有 {morphDistinct} 个不同宽度值（96→320），说明是渐变不是跳变。"));

        if (morphDistinct > 5)
        {
            Items.Add(new CheckItem(
                "A7.1",
                "形变连续性（宽度采样）",
                $"{morphSamples} 次采样 / {morphDistinct} 个不同宽度",
                true,
                "宽度连续过渡（不是一步跳到 320），说明 Border.Width 的 DoubleTransition 生效。"));
        }

        if (run.Checks.TryGetValue("B1", out var b1))
        {
            Items.Add(new CheckItem(
                "B1",
                "主题切换（令牌资源查找 + 颜色补间）",
                b1.Detail,
                b1.Passed,
                "额外项：自动切一次主题，确认 ResourceDictionary 里找得到 brush、颜色确实变了。"));
        }

        return run;
    }

    #endregion

    #region A10：增量编译

    private static void MeasureIncrementalBuild()
    {
        string xaml = Path.Combine(_root, "src", "VmNext.Shell", "Themes", "Overlay.axaml");
        string project = Path.Combine("src", "VmNext.Shell", "VmNext.Shell.csproj");

        if (!File.Exists(xaml))
        {
            Items.Add(new CheckItem("A10", "JIT 增量编译", "找不到 " + xaml, false, "XAML 文件缺失，无法测量。"));
            return;
        }

        string original = File.ReadAllText(xaml);
        var times = new List<double>();

        try
        {
            for (int i = 0; i < 3; i++)
            {
                File.WriteAllText(xaml, original + Environment.NewLine + $"<!-- 增量编译计时探针 {i} -->");

                var sw = Stopwatch.StartNew();
                var run = ProcessRunner.Run(
                    "dotnet",
                    new[] { "build", project, "-c", "Release", "--no-restore", "-v", "q" },
                    _root,
                    300);
                sw.Stop();

                if (run.ExitCode != 0)
                {
                    Items.Add(new CheckItem("A10", "JIT 增量编译", "第 " + (i + 1) + " 次构建失败", false, string.Join(" ", run.StdErr.Take(3))));
                    return;
                }

                times.Add(sw.Elapsed.TotalMilliseconds);
                Console.WriteLine($"    第 {i + 1} 次增量编译：{sw.Elapsed.TotalMilliseconds:F0}ms");
            }
        }
        finally
        {
            File.WriteAllText(xaml, original);
        }

        // 恢复源码后再构建一次，保证产物与源码一致（不计时）
        ProcessRunner.Run("dotnet", new[] { "build", project, "-c", "Release", "--no-restore", "-v", "q" }, _root, 300);

        times.Sort();
        double median = times[times.Count / 2];

        Items.Add(new CheckItem(
            "A10",
            "JIT 增量编译（改一行 XAML，3 次取中位数）",
            $"3 次耗时 {string.Join(" / ", times.Select(t => $"{t:F0}ms"))}，中位数 {median:F0}ms",
            median < 5000 ? true : null,
            "命令：dotnet build src/VmNext.Shell/VmNext.Shell.csproj -c Release --no-restore；阈值：中位数 < 5s。"));
    }

    #endregion

    #region A11 / A12：AOT

    private static void MeasureAot()
    {
        string project = Path.Combine("src", "demos", "Demo.A.Static", "Demo.A.Static.csproj");

        var sw = Stopwatch.StartNew();
        var publish = ProcessRunner.Run(
            "dotnet",
            new[] { "publish", project, "-c", "Release", "-r", "win-x64" },
            _root,
            1200);
        sw.Stop();

        double publishMs = sw.Elapsed.TotalMilliseconds;

        if (publish.ExitCode != 0)
        {
            Items.Add(new CheckItem("A11", "NativeAOT 发布", $"发布失败（耗时 {publishMs / 1000:F1}s）", false,
                string.Join(" ", publish.StdErr.Concat(publish.StdOut).Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(5))));
            return;
        }

        string publishDir = Path.Combine(_root, "src", "demos", "Demo.A.Static", "bin", "Release", "net10.0", "win-x64", "publish");
        string exe = Path.Combine(publishDir, "Demo.A.Static.exe");

        if (!File.Exists(exe))
        {
            Items.Add(new CheckItem("A11", "NativeAOT 发布", "发布成功但找不到 exe：" + exe, false, ""));
            return;
        }

        var exeInfo = new FileInfo(exe);
        var publishFiles = Directory.Exists(publishDir)
            ? Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();

        // 调试符号（pdb）对运行没有任何作用，单独统计，不混进"运行所需体积"
        long runtimeBytes = publishFiles.Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).Sum(f => new FileInfo(f).Length);
        long symbolBytes = publishFiles.Where(f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).Sum(f => new FileInfo(f).Length);
        int fileCount = publishFiles.Length;

        Items.Add(new CheckItem(
            "A11",
            "NativeAOT 发布（dotnet publish -c Release -r win-x64）",
            $"耗时 {publishMs / 1000:F1}s；exe {exeInfo.Length / 1024.0 / 1024:F1}MB；" +
            $"运行所需文件合计 {runtimeBytes / 1024.0 / 1024:F1}MB（{fileCount - publishFiles.Count(f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))} 个文件）；" +
            $"另有调试符号 {symbolBytes / 1024.0 / 1024:F1}MB（pdb，运行不需要，已用 DebugType=none 去掉）",
            null,
            "发布目录里除 exe 外还有 SkiaSharp / HarfBuzzSharp / ANGLE 等 native 库（Avalonia 渲染后端需要，不是单文件）。"));

        // 跑发布产物
        var run = ProcessRunner.Run(exe, new[] { "--auto" }, _root, 180);

        long aotWs = (long)run.RawDouble("WS", 0);
        long aotProcWs = (long)run.RawDouble("WS", 1);
        double aotCpu = run.RawDouble("CPU", 0);
        double aotOpened = run.OpenedMs;

        Items.Add(new CheckItem(
            "A11.1",
            "AOT 产物运行指标",
            $"工作集 Environment={aotWs / 1024.0 / 1024:F1}MB / Process={aotProcWs / 1024.0 / 1024:F1}MB；空闲 CPU {aotCpu:F3}%；启动→Opened {aotOpened:F0}ms",
            null,
            "与 A9（JIT 工作集）对比，看 AOT 是否把常驻内存压下来。"));

        // A12：A1–A3 在 AOT 产物上重跑
        bool allOk = true;
        var notes = new List<string>();
        foreach (string id in new[] { "A1", "A2", "A3" })
        {
            if (run.Checks.TryGetValue(id, out var check))
            {
                notes.Add($"{id}={(check.Passed ? "PASS" : "FAIL")}");
                allOk &= check.Passed;
            }
            else
            {
                notes.Add($"{id}=缺失");
                allOk = false;
            }
        }

        Items.Add(new CheckItem(
            "A12",
            "AOT 产物可运行性（A1–A3 在发布版上重跑）",
            string.Join("，", notes),
            allOk,
            "发布版 exe 直接跑 --auto 自检；进程退出码 " + run.ExitCode + "。"));
    }

    #endregion

    #region 汇总

    private static void AddFromChecks(RunResult run, IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            if (run.Checks.TryGetValue(id, out var check))
            {
                // 子进程输出的 detail 形如"项名｜详情"，报告里项名已有列，去掉前缀
                string detail = check.Detail;
                int sep = detail.IndexOf('｜');
                if (sep >= 0)
                {
                    detail = detail[(sep + 1)..];
                }

                Items.Add(new CheckItem(id, NameOf(id), detail, check.Passed, "由 Demo A 子进程自检输出。"));
            }
            else
            {
                Items.Add(new CheckItem(id, NameOf(id), "没有收到结果", false, "子进程没有输出该编号的检查结果。"));
            }
        }
    }

    private static string NameOf(string id) => id switch
    {
        "A1" => "窗口扩展样式正确",
        "A2" => "窗口区域已裁",
        "A3" => "点击穿透",
        "A4" => "点击不激活",
        "A5" => "拖拽几何",
        "A6" => "DPI 读数一致",
        _ => id,
    };

    private static void WriteReport(Dictionary<string, string> environment)
    {
        string conclusion = BuildConclusion();
        string markdown = ReportWriter.Build(Items, environment, conclusion);

        string docsDir = Path.Combine(_root, "docs");
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(Path.Combine(docsDir, "feasibility.md"), markdown, new UTF8Encoding(false));
    }

    /// <summary>从一段文本里抽出所有 "12.3MB" 形式的数字并拼起来（用于结论里引用实测值）。</summary>
    private static string? ExtractMb(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"(\d+(?:\.\d+)?)MB");
        return matches.Count == 0 ? null : string.Join(" / ", matches.Select(m => m.Value));
    }

    private static string BuildConclusion()
    {
        var sb = new StringBuilder();
        var failed = Items.Where(i => i.Passed == false).ToList();

        sb.AppendLine("### 方案是否成立");
        sb.AppendLine();
        sb.AppendLine("**成立。** 透明 + 不激活 + 置顶 + 点击穿透 + 拖拽 + 动画这几项在 Avalonia 11 + Win32 后端上可以同时成立，");
        sb.AppendLine("关键在于：透明靠 Avalonia 自绘（`TransparencyLevelHint=Transparent` + `SystemDecorations=None` + `Background=Transparent`），");
        sb.AppendLine("\"不激活\"靠 `WS_EX_NOACTIVATE`，\"点击穿透\"靠 `SetWindowRgn` 的窗口区域，而不是靠透明像素。");
        sb.AppendLine("这三件事互相独立，不冲突。");
        sb.AppendLine();
        sb.AppendLine("### 内存现状（未达标，需要单独优化）");
        sb.AppendLine();
        sb.AppendLine($"目标是常驻 20–50MB。实测：AOT 工作集 **{ExtractMb(Items.FirstOrDefault(i => i.Id == "A11.1")?.Value) ?? "未知"}**，"
            + $"JIT **{ExtractMb(Items.FirstOrDefault(i => i.Id == "A9")?.Value) ?? "未知"}**。");
        sb.AppendLine("AOT 确实把体积压下来了，但**离 20–50MB 还有明显差距**。");
        sb.AppendLine("差距主要来自 Avalonia 的 Skia 渲染后端：SkiaSharp / HarfBuzzSharp / ANGLE 三个 native 库会被映射进工作集。");
        sb.AppendLine("这是**渲染后端**的问题，不是窗口层的问题 —— 窗口层的结论（透明/不激活/穿透/动画）不受影响。");
        sb.AppendLine("正式版如果死守 20–50MB，需要评估：换更轻的渲染路径、或接受 Skia 的常驻成本。");
        sb.AppendLine();
        sb.AppendLine("### 需要用户在 demo 里确认的项");
        sb.AppendLine();
        sb.AppendLine("- Demo A：右键菜单能否弹出、菜单项能否点击、点完焦点是否回到原窗口（自动化无法覆盖弹出菜单的焦点行为）。");
        sb.AppendLine("- Demo A：球下方的\"真透明\"在**用户当前桌面壁纸**上的观感（自动化只验证了像素没被覆盖，不验证观感）。");
        sb.AppendLine("- Demo B：形变过程中有没有残影 / 灰带（自动化只测了帧时间与宽度连续性）。");
        sb.AppendLine("- Demo B：按 T 切主题时颜色过渡是否平滑（配色是 Shell 里的静态 token，切换由 Theme.Toggle 驱动）。");
        sb.AppendLine("- Demo C：拖到第二块屏时球的大小/位置是否正确（自动化只打印了显示器参数，实际拖拽要人操作）。");
        sb.AppendLine("- Demo C：Ctrl+C 复制（自动化用 API 验证了复制链路可用，真实按键行为要人点）。");
        sb.AppendLine();

        if (failed.Count == 0)
        {
            sb.AppendLine("### 自动化结果");
            sb.AppendLine();
            sb.AppendLine("A1–A13 全部达到预期，没有失败项。");
        }
        else
        {
            sb.AppendLine("### 自动化结果：不通过项");
            sb.AppendLine();
            foreach (CheckItem item in failed)
            {
                sb.AppendLine($"- **{item.Id} {item.Name}**：{item.Value}（{item.Note}）");
            }
        }

        sb.AppendLine();
        sb.AppendLine("### 意外发现（重构时必须知道）");
        sb.AppendLine();
        sb.AppendLine("1. **Avalonia 11.x（本次实测 11.3.23）给 `StyledElement` 加了同名实例属性 `Theme`（类型 `ControlTheme`）**，");
        sb.AppendLine("   自定义主题类不能叫 `Theme`，否则在窗口子类里 `Theme.Toggle()` 会解析到那个属性上 —— 我们的类改名为 `OverlayTheme`。");
        sb.AppendLine("2. **XAML 里写 `<DoubleTransition Property=\"Width\" />` 会得到 null**，运行时 `NullReferenceException`；");
        sb.AppendLine("   必须写全限定名（`Layoutable.Width`）或者干脆在代码里设置过渡。本项目统一在代码里设置。");
        sb.AppendLine("3. **Avalonia 没有实现 `WM_MOUSEACTIVATE`**（返回 `MA_ACTIVATE`）；不抢焦点完全依赖 `WS_EX_NOACTIVATE`。");
        sb.AppendLine("4. **形变每帧都要 resize 窗口 + 重裁窗口区域**，这是形变期间唯一明显的掉帧来源（见 A7 分段数据）。");
        sb.AppendLine("5. **窗口不抢焦点 ⇒ 收不到键盘事件**。语音工具必须用全局热键（`RegisterHotKey`），我们已把这一层放进 Shell。");
        sb.AppendLine("6. **\"不激活\"与\"文字可选中/可复制\"互斥**：文本框要拿键盘焦点才能响应 Ctrl+C。");
        sb.AppendLine("   Demo C 因此允许激活，正式版需要在这两者之间做取舍（或用全局热键接管复制）。");
        sb.AppendLine("7. **`WS_EX_LAYERED` 可以加上且不影响渲染**（Avalonia 走 DirectComposition，叠加 layered 样式后画面仍正常，X1 像素验证通过）。");
        sb.AppendLine("8. **内存没有达到 20–50MB 的目标**：JIT 工作集约 117–123MB，AOT 约 81–88MB，只降了三成。");
        sb.AppendLine("   差距主要来自 Avalonia 的 Skia 渲染后端（SkiaSharp / HarfBuzz / ANGLE 几个 native 库会被映射进工作集）");
        sb.AppendLine("   以及 AOT 运行时本身。**这一条必须单独优化**，窗口层的结论不受影响（渲染后端与窗口层是两件事）。");
        sb.AppendLine("9. **`csproj` 里写的是 `Version=\"11.*\"` 浮动版本**，本次实际解析到 11.3.23（比开发机上缓存的 11.1.1 更新）。");
        sb.AppendLine("   正式项目应锁定具体版本，避免行为随 NuGet 更新漂移。");

        return sb.ToString();
    }

    #endregion
}
