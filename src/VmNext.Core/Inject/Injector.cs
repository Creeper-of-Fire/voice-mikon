using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VmNext.Core.Interop;
using VmNext.Shell;

namespace VmNext.Core.Inject;

/// <summary>
/// 一次注入的结果。
///
/// 翻译自 voice-mikon/src/voice_mikon/core/inject.py 的 InjectResult。
/// </summary>
public readonly record struct InjectResult(bool Ok, string Reason = "", bool TextInClipboard = false);

/// <summary>
/// 文本注入：把识别结果送进当前光标位置。
///
/// 翻译自 voice-mikon/src/voice_mikon/core/inject.py。
///
/// 策略：**剪贴板 + 模拟 Ctrl+V**。
/// * 不依赖目标应用的输入法状态，中文绝不出错
/// * 一次粘贴，不存在逐字输入的丢字/乱序问题
///
/// 为什么不用 SendInput + KEYEVENTF_UNICODE 逐字打字：
/// 那种方式不碰剪贴板，看着更"干净"，但 1) 打字路径下 \r \n \t 会被 Windows
/// 当成真实按键 —— 回车在聊天软件里就是"发送消息"，太危险；2) 部分应用
/// （记事本、WordPad）接受 Unicode 键事件但丢弃字形，表现为"光标在动但没字"。
/// 保持剪贴板为主路径。
///
/// 关于"占用剪贴板"的处理：剪贴板只有一份，借来用完必须还回去。但**无脑还回去
/// 会吃掉用户刚复制的新内容** —— 用户在这半秒里复制了别的东西，我们的恢复就把它
/// 盖掉了，那才是真的丢数据。所以恢复前先做归属判定：
///   1. 记录写入之后的剪贴板序号
///   2. 恢复前比对序号，以及当前内容是不是我们写的那份
///   3. 对不上 = 用户动过 → **放弃恢复**，用户的新内容优先
///
/// **没送达就不还**（本项目自己的规矩）：我们没有 UI Automation 那种读回控件
/// 内容校验的能力，只认"事件确实送出去了"这一个信号：
/// * 送出去了 → 归还原内容，一切照旧
/// * 没送出去 → **不还**，把识别结果留在剪贴板里
/// 代价是失败时用户原来那份剪贴板内容被顶掉了 —— 宁可丢一份旧的，也不能丢
/// 刚说出口的。
/// </summary>
public sealed class Injector
{
    /// <summary>粘贴后等待目标应用消费剪贴板的时间。</summary>
    public const double PasteSettle = 0.15;

    /// <summary>归还原内容前等待的时间（给目标应用留出消费窗口）。</summary>
    public const double RestoreSettle = 1.0;

    /// <summary>注入整体重试：总尝试次数。</summary>
    public const int InjectAttempts = 3;

    /// <summary>重试间隔（秒）。</summary>
    public const double InjectRetryDelay = 0.2;

    /// <summary>
    /// 等修饰键松开的耐心上限（秒）。
    /// 用户按完热键松手是必然的，等比报失败便宜；2.5s 是"真按住不放"
    /// （有意长按）与"还没松完"的分界。
    /// </summary>
    public const double ModWaitTimeout = 2.5;

    /// <summary>等修饰键的采样步长（秒）。</summary>
    public const double ModWaitStep = 0.03;

    // 失败原因码：把"到底卡在哪一步"说清楚。
    //
    // 以前所有失败都长成同一个样子（一个 False），上层只好写一句万能文案
    // "注入失败,文字已留在剪贴板" —— 复制其实失败的时候，这句话是**假的**
    // （剪贴板里什么都没有）。有了原因码 + 剪贴板实况，失败通知才能说实话。
    public const string ReasonEmptyText = "empty_text";
    public const string ReasonClipboardOpenFailed = "clipboard_open_failed";
    public const string ReasonClipboardWriteFailed = "clipboard_write_failed";
    public const string ReasonClipboardVerifyFailed = "clipboard_verify_failed";
    public const string ReasonModifierTimeout = "modifier_timeout";
    public const string ReasonKeysNotSent = "keys_not_sent";

    private static readonly Logger _log = VmNext.Core.Log.GetLogger("core.inject");

    private readonly ClipboardService _clipboard;

    public Injector(ClipboardService clipboard)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    /// <summary>
    /// 把 text 粘贴到 hwnd 窗口的光标处。
    ///
    /// hwnd 为 IntPtr.Zero 表示不恢复焦点（粘贴到当前前台窗口）。
    ///
    /// 临时故障（修饰键未松开/剪贴板被占/事件没投出去）会整体重试
    /// <see cref="InjectAttempts"/> 次；**粘贴已触发就绝不重试** —— 那会造成
    /// 重复文本，比失败更糟。
    ///
    /// 送不到就必须如实说送不到 —— 谎报成功比失败更糟：用户以为文字已经进输入框了，
    /// 不会再去别处找，结果那段话其实哪儿都没有。
    /// </summary>
    public InjectResult PasteText(
        string text,
        IntPtr hwnd = default,
        bool restore = true,
        bool trailingSpace = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new InjectResult(false, ReasonEmptyText);
        }

        if (trailingSpace && !text.EndsWith(" ", StringComparison.Ordinal)
            && !text.EndsWith("\n", StringComparison.Ordinal)
            && !text.EndsWith("。", StringComparison.Ordinal)
            && !text.EndsWith("!", StringComparison.Ordinal)
            && !text.EndsWith("?", StringComparison.Ordinal)
            && !text.EndsWith("，", StringComparison.Ordinal))
        {
            text += " ";
        }

        // ---- 备份原剪贴板（只备份文本，**只做一次**）----
        // 必须在重试循环外：第一次尝试写过剪贴板之后，里面已经是我们的文本，
        // 循环里再备份会把它误当成"原内容"还回去。
        // "读失败"和"里面没有文本"必须分得清：读失败说明我们**不知道**原内容是什么，
        // 也就没法归还 —— 那就干脆不还，把识别结果留在剪贴板里。
        string? original = null;
        if (restore)
        {
            (bool readOk, string? current) = _clipboard.ReadText();
            if (!readOk)
            {
                _log.Warning("读不到原剪贴板内容(可能被别的程序占着),本次不归还,识别结果将留在剪贴板");
            }
            else
            {
                original = current;
            }
        }

        string reason = ReasonEmptyText;

        for (int attempt = 1; attempt <= InjectAttempts; attempt++)
        {
            (bool delivered, uint publishedSeq, string attemptReason) = AttemptInject(text, hwnd, attempt);
            reason = attemptReason;

            if (delivered)
            {
                // 恢复原剪贴板放到后台：Restore 里要等 RestoreSettle（约 1 秒）才动手，
                // 同步等它的话 UI 状态会滞后 —— 用户已经看到文字进输入框了，
                // 胶囊却还转着"识别中"。
                // 归属守卫（序号比对）在后台做完全等价：比对的是**这次成功尝试**
                // 发布时的序号，谁先检查都一样。
                //
                // original 为 null 有两种情况：配置关了归还，或者原剪贴板里确实没有
                // 文本。两种都不动 —— 识别结果留在剪贴板里，用户自己还能用。
                if (original is not null)
                {
                    _log.Debug($"启动归还线程(original={original.Length} 字, seq={publishedSeq})");
                    _ = Task.Run(() => Restore(original, text, publishedSeq));
                }
                else
                {
                    _log.Debug("不归还:原内容为 null");
                }

                return new InjectResult(true, string.Empty, true);
            }

            if (reason == ReasonModifierTimeout)
            {
                // 等键超时不重试：修饰键不会因为重试而松开，再等 2.5s×N 只是拖时间。
                // 文字已在剪贴板里（写板在等键之前），直接如实上报。
                _log.Warning($"修饰键 {ModWaitTimeout:F1}s 未松开,放弃自动粘贴(文本已在剪贴板)");
                return new InjectResult(false, reason, TextInClipboard(text));
            }

            if (attempt < InjectAttempts)
            {
                _log.Info($"注入未送达(第 {attempt}/{InjectAttempts} 次尝试),{InjectRetryDelay * 1000:F0}ms 后重试");
                Thread.Sleep(TimeSpan.FromSeconds(InjectRetryDelay));
            }
        }

        // 失败时的实况：剪贴板里到底有没有我们的那份文本 —— **读回比对**，不猜。
        // 这一句决定了上层能不能说"按 Ctrl+V 就能拿到"。
        bool inClipboard = TextInClipboard(text);
        _log.Warning($"注入重试 {InjectAttempts} 次仍未送达({reason}),识别结果{(inClipboard ? "保留在剪贴板里" : "没能写进剪贴板")}");
        return new InjectResult(false, reason, inClipboard);
    }

    /// <summary>
    /// 同上，但会记录焦点窗口标题，便于排障。返回值保持 bool。
    /// </summary>
    public bool PasteWithFeedback(string text, IntPtr hwnd = default, bool restore = true)
    {
        if (hwnd != IntPtr.Zero)
        {
            string title = GetWindowTitle(hwnd);
            if (GetForegroundWindow() != hwnd)
            {
                Console.WriteLine($"[focus] 焦点已变化,恢复到: {title[..Math.Min(40, title.Length)]}");
            }
        }

        return PasteText(text, hwnd, restore, trailingSpace: false).Ok;
    }

    /// <summary>
    /// 按**实况**生成失败通知（成功时返回 null）。
    ///
    /// 文案按实况分类（硬规则：**诊断信息必须与失败原因一致** —— 以前 modifier 类
    /// 失败（根本没碰剪贴板）也报"复制到剪贴板失败"，把用户的排查方向带偏了）：
    /// * modifier_timeout → 修饰键一直没松开（与剪贴板无关）
    /// * 剪贴板里确实有我们的文本 → 只是没送出去，Ctrl+V 就能拿到
    /// * 剪贴板里没有 → 复制这一步就失败了，得手动重来
    ///
    /// 文案逐字取自 voice-mikon 的 core/controller.py。
    /// </summary>
    public static Notification? BuildFailureNotification(string text, InjectResult result)
    {
        if (result.Ok)
        {
            return null;
        }

        var fields = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["reason"] = result.Reason,
        };

        if (result.Reason == ReasonModifierTimeout)
        {
            return new Notification(
                "warning",
                "inject_failed",
                "修饰键还没松开,没能自动粘贴;松开后可直接 Ctrl+V,或点「复制」",
                fields,
                DateTimeOffset.Now);
        }

        if (result.TextInClipboard)
        {
            return new Notification(
                "warning",
                "inject_failed",
                "文字已复制到剪贴板,但没能自动粘贴出去,按 Ctrl+V 即可",
                fields,
                DateTimeOffset.Now);
        }

        return new Notification(
            "error",
            "inject_failed",
            "复制到剪贴板失败,请在胶囊上点「复制」重试",
            fields,
            DateTimeOffset.Now);
    }

    /// <summary>剪贴板里现在是不是我们的那份文本（读回来比对，不猜）。</summary>
    private bool TextInClipboard(string text)
    {
        try
        {
            (bool ok, string? current) = _clipboard.ReadText();
            return ok && current == text;
        }
        catch (Exception ex)
        {
            _log.Debug($"核对剪贴板内容失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 单次注入尝试，返回（是否送达, 发布时的剪贴板序号, 失败原因）。
    ///
    /// 任何一步失败都返回 false —— 由上层决定是否重试。
    /// 粘贴已触发（见下方 sent 判定）必须返回 true：重试会造成重复文本。
    ///
    /// 剪贴板写入失败（含**读回校验**不过）都发生在发按键**之前**，此时粘贴还没
    /// 触发，重试是安全的 —— 不会贴出重复文字。
    /// </summary>
    private (bool Delivered, uint PublishedSeq, string Reason) AttemptInject(string text, IntPtr hwnd, int attempt)
    {
        // ---- 先把文字写进剪贴板（保住文字优先于贴出去）----
        // 无论修饰键状态如何，识别出的话必须先存在于剪贴板 —— "没送达就不还"的
        // 铁律：哪怕后面等键超时，文字已经在板上，用户松手后 Ctrl+V 就能拿到。
        // 反过来（先等键再写板）超时路径文字哪儿都不在，文案却说"可直接 Ctrl+V"
        // —— 又是谎报。
        // 代价是占板窗口变长（写入到粘贴之间隔着等键，最多 ModWaitTimeout）：
        // 用户在这期间复制新内容 → 序号变了 → 归属守卫会让位，不会覆盖他的新内容。
        ClipboardResult write = _clipboard.SetTextVerified(text);
        if (!write.Ok)
        {
            // 写不进去就别往下走：剪贴板里没有文字，Ctrl+V 贴出来的会是用户原来那份
            // （或空的）—— 那比不贴更糟。
            _log.Debug($"写入剪贴板失败(第 {attempt} 次尝试):{write.Reason}");
            return (false, 0, MapClipboardReason(write.Reason));
        }

        // 写入自带的读回校验用的是 GetClipboardData，**不递增变更序号** ——
        // 这里取到的序号仍然是"我们这份"的序号，归属判定不受影响。
        uint publishedSeq = _clipboard.GetSequenceNumber();

        // ---- 等修饰键松开（决定"怎么贴"，不影响"文字已在板上"）----
        // 用户结束录音的方式就是再按一次热键，识别完成时手指常还留在修饰键上 ——
        // 这不是故障，是必然的时间差：
        //   - 全松 → 正常合成 Ctrl+V
        //   - 稳定只剩 Ctrl（用户有意按住）→ **顺势只补一个 V**
        //   - Alt/Shift/Win 按着 → 发什么都不再是普通粘贴，继续等
        //   - 等满 ModWaitTimeout → 放弃"贴"，但文字已在剪贴板里
        string mod = WaitModifiers(attempt, text);
        if (mod == "timeout")
        {
            // 注意带上 publishedSeq：text_in_clipboard 判定据此走
            // "已复制到剪贴板但没自动粘贴"文案（这句现在是真的）。
            return (false, publishedSeq, ReasonModifierTimeout);
        }

        bool ctrlHeldByUser = mod == "ctrl";

        // 转写这 1 秒里用户可能切了窗口，把焦点还回去再贴
        if (hwnd != IntPtr.Zero && GetForegroundWindow() != hwnd)
        {
            RestoreForeground(hwnd);
            Thread.Sleep(TimeSpan.FromSeconds(0.25));
        }

        int sent;
        int vDownAt;
        int total;
        if (ctrlHeldByUser)
        {
            // 顺势补 V：Ctrl 是用户手指物理按着的（真实按下状态），我们只投 V↓/V↑。
            // 绝不合成 Ctrl↓/Ctrl↑ —— 那会跟他真实的手指状态打架。
            sent = SendV();
            vDownAt = 1;
            total = 2;   // [V↓, V↑]：投出 1 个 = V↓ 已送达
        }
        else
        {
            sent = SendCtrlV();
            vDownAt = 2;
            total = 4;   // [Ctrl↓, V↓, V↑, Ctrl↑]：前 2 个 = V↓ 已送达
        }

        // **V↓ 送出去 = 目标应用已经收到"粘贴"这个动作了**；
        // 没送出去才是"什么都没发生"（重试安全）。
        if (sent < vDownAt)
        {
            _log.Warning($"粘贴按键没送出去(sent={sent}/{total},第 {attempt} 次尝试)");
            return (false, publishedSeq, ReasonKeysNotSent);
        }

        if (sent < total)
        {
            // 抬起事件没全送达，但 V↓ 已送 = 粘贴**已经触发**（补抬是微秒级，
            // 远快过按键重复间隔）—— 绝不能重贴，那会重复文本。
            _log.Info($"粘贴只投递了 {sent}/{total} 个事件,V↓ 已送达");
        }

        Thread.Sleep(TimeSpan.FromSeconds(PasteSettle));
        return (true, publishedSeq, string.Empty);
    }

    /// <summary>
    /// 等修饰键松开，返回 "clear" / "ctrl" / "timeout"。
    ///
    /// * "clear"：全松开，正常合成 Ctrl+V；
    /// * "ctrl"：**稳定**只剩 Ctrl 按住（连续两个采样周期都是）—— 判定为用户有意
    ///   按住，可顺势补 V。"稳定"这个条件是在滤掉松键途中的一瞬（全松的路径会经过
    ///   {ctrl,alt}→{ctrl}→{}，那一瞬的 ctrl-only 说明用户其实在松手）；
    /// * "timeout"：等满上限还按着 —— 由调用方如实报 modifier_timeout。
    /// </summary>
    private static string WaitModifiers(int attempt, string finalText)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(ModWaitTimeout * Stopwatch.Frequency);
        int ctrlStable = 0;
        bool notified = false;

        while (true)
        {
            HashSet<string> mods = HeldModifiers();
            if (mods.Count == 0)
            {
                return "clear";
            }

            // 真的在等才提示（全松的大多数轮次零通知零打扰）：
            // 胶囊的"识别中…"实时换成"松开修饰键后粘贴"，用户看到就会松，
            // 等待实际时长随之缩短。
            if (!notified)
            {
                notified = true;
                NotificationHub.Notify.Publish(
                    "warning",
                    "modifier_wait",
                    "松开修饰键后自动粘贴；按住 Ctrl 也可以直接粘贴",
                    new Dictionary<string, object?> { ["text"] = finalText });
            }

            if (mods.Count == 1 && mods.Contains("ctrl"))
            {
                ctrlStable++;
                if (ctrlStable >= 2)
                {
                    return "ctrl";
                }
            }
            else
            {
                ctrlStable = 0;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                _log.Debug($"等修饰键松开超时(第 {attempt} 次尝试,仍按着:{string.Join(",", mods)})");
                return "timeout";
            }

            Thread.Sleep(TimeSpan.FromSeconds(ModWaitStep));
        }
    }

    /// <summary>
    /// 把原剪贴板还回去 —— 但只在它还是"我们写的那份"时才还。
    ///
    /// original 为 null 表示原剪贴板没有文本（或者只有图片之类读不出来的内容）。
    /// 这时**不动**：我们无法完整还原它，硬还原只会把用户的东西弄丢。
    /// </summary>
    private void Restore(string? original, string written, uint publishedSeq)
    {
        if (original is null)
        {
            return;
        }

        Thread.Sleep(TimeSpan.FromSeconds(RestoreSettle));

        uint currentSeq = _clipboard.GetSequenceNumber();
        _log.Debug($"归还检查: 当前序号={currentSeq} 发布序号={publishedSeq}");
        if (currentSeq != publishedSeq)
        {
            // 序号变了 = 有人在这期间动过剪贴板（多半是用户复制了新内容）。
            // 用户的新内容优先，我们的文本让位 —— 但**不覆盖**它。
            _log.Info("剪贴板在这期间被改动,放弃恢复(用户的新内容优先)");
            return;
        }

        try
        {
            if (_clipboard.GetText() != written)
            {
                _log.Info("剪贴板内容已不是我们写入的,放弃恢复");
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"读取剪贴板失败: {ex.Message}");
            return;
        }

        try
        {
            if (!_clipboard.SetText(original))
            {
                _log.Debug("恢复剪贴板失败");
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"恢复剪贴板失败: {ex.Message}");
        }
    }

    /// <summary>剪贴板层的原因码 → 注入层的原因码。</summary>
    private static string MapClipboardReason(string clipboardReason) => clipboardReason switch
    {
        ClipboardService.WriteReasonOpenFailed => ReasonClipboardOpenFailed,
        ClipboardService.WriteReasonWriteFailed => ReasonClipboardWriteFailed,
        ClipboardService.WriteReasonVerifyFailed => ReasonClipboardVerifyFailed,
        _ => ReasonClipboardWriteFailed,
    };

    // ---------------- Win32 小工具（翻译 voice-mikon/src/voice_mikon/win32/api.py） ----------------

    /// <summary>当前前台窗口句柄。</summary>
    internal static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>窗口标题（用于自动化里定位窗口）。</summary>
    internal static string GetWindowTitle(IntPtr hwnd)
    {
        var buf = new StringBuilder(512);
        NativeMethods.GetWindowTextW(hwnd, buf, 512);
        return buf.ToString();
    }

    /// <summary>
    /// 把焦点还给指定窗口。
    ///
    /// AttachThreadInput 是关键：不挂线程的话 SetForegroundWindow 在后台线程调用时
    /// 经常失败（Windows 的前台锁策略）。
    /// </summary>
    public static bool RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            uint curThread = NativeMethods.GetCurrentThreadId();
            var attached = new List<uint>();
            foreach (IntPtr target in new[] { GetForegroundWindow(), hwnd })
            {
                if (target == IntPtr.Zero)
                {
                    continue;
                }

                uint tid = NativeMethods.GetWindowThreadProcessId(target, out _);
                if (tid != 0 && tid != curThread && !attached.Contains(tid))
                {
                    NativeMethods.AttachThreadInput(tid, curThread, true);
                    attached.Add(tid);
                }
            }

            bool ok = NativeMethods.SetForegroundWindow(hwnd);
            foreach (uint tid in attached)
            {
                NativeMethods.AttachThreadInput(tid, curThread, false);
            }

            return ok;
        }
        catch (Exception)
        {
            try
            {
                return NativeMethods.SetForegroundWindow(hwnd);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 此刻正按着的修饰键集合（"ctrl" / "alt" / "shift" / "win"）。
    ///
    /// 注入的"等松键"策略需要知道**具体是谁**按着：只剩 Ctrl 时可以顺势只补一个 V
    /// （用户的 Ctrl + 我们的 V = 粘贴），Alt/Shift 按着时发什么都不是普通粘贴。
    /// </summary>
    public static HashSet<string> HeldModifiers()
    {
        var mods = new HashSet<string>(StringComparer.Ordinal);
        if (KeyIsDown(NativeMethods.VK_CONTROL))
        {
            mods.Add("ctrl");
        }

        if (KeyIsDown(NativeMethods.VK_MENU))
        {
            mods.Add("alt");
        }

        if (KeyIsDown(NativeMethods.VK_SHIFT))
        {
            mods.Add("shift");
        }

        if (KeyIsDown(NativeMethods.VK_LWIN) || KeyIsDown(NativeMethods.VK_RWIN))
        {
            mods.Add("win");
        }

        return mods;
    }

    /// <summary>某个键当前是否被按住。</summary>
    internal static bool KeyIsDown(ushort vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// 模拟 Ctrl+V，返回**实际投递**的事件数（4 = 全部成功）。
    ///
    /// 用 SendInput 而不是 keybd_event：后者投递成功与否完全不可知，被 UIPI 拦掉时
    /// 也照样"成功"，表现出来就是"按了热键没反应"。
    ///
    /// 投递不全时**只补发抬起事件**，绝不重贴：重贴会造成重复文本。
    /// </summary>
    internal static int SendCtrlV()
    {
        NativeMethods.INPUT[] keys =
        {
            MakeKey(NativeMethods.VK_CONTROL, up: false),
            MakeKey(NativeMethods.VK_V, up: false),
            MakeKey(NativeMethods.VK_V, up: true),
            MakeKey(NativeMethods.VK_CONTROL, up: true),
        };

        int sent = (int)NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent < keys.Length)
        {
            // 只把**确实按下过**的键抬起来：索引 0 = Ctrl down，索引 1 = V down
            var release = new List<NativeMethods.INPUT>();
            if (sent >= 2)
            {
                release.Add(MakeKey(NativeMethods.VK_V, up: true));
            }

            if (sent >= 1)
            {
                release.Add(MakeKey(NativeMethods.VK_CONTROL, up: true));
            }

            if (release.Count > 0)
            {
                NativeMethods.SendInput((uint)release.Count, release.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
            }
        }

        return sent;
    }

    /// <summary>
    /// 只发 V↓/V↑（不带 Ctrl），返回实际投递数（2 = 全部成功）。
    ///
    /// 用在"用户自己按住 Ctrl、我们顺势补 V"的路径 —— Ctrl 是用户手指按着的，
    /// 状态真实存在；我们**绝不能替他发 Ctrl↓/Ctrl↑**：合成的 Ctrl↓ 会跟物理
    /// 按键状态打架，合成的 Ctrl↑ 会打断他继续按住的本意。
    /// </summary>
    internal static int SendV()
    {
        NativeMethods.INPUT[] keys =
        {
            MakeKey(NativeMethods.VK_V, up: false),
            MakeKey(NativeMethods.VK_V, up: true),
        };

        int sent = (int)NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent > 0 && sent < keys.Length)
        {
            // V↓ 投出去了但 V↑ 没有：补抬，别留一个"按住的 V"
            NativeMethods.INPUT[] ups = { MakeKey(NativeMethods.VK_V, up: true) };
            NativeMethods.SendInput((uint)ups.Length, ups, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        return sent;
    }

    /// <summary>造一个键盘事件（wScan=0，按虚拟键码走）。</summary>
    private static NativeMethods.INPUT MakeKey(ushort vk, bool up)
    {
        var ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
            time = 0,
            dwExtraInfo = UIntPtr.Zero,
        };

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION { ki = ki },
        };
    }
}
