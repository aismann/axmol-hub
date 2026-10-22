using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ThemeProbe;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellPlatformChecks
{
    private readonly CheckReport _report;
    private readonly Action<bool, string> Check;
    private readonly ShellCheckFixture Fixture;
    internal ShellPlatformChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        _report = report;
        Check = check;
        Fixture = fixture;
    }

    internal void CheckStringDefinition()
    {
        Check(HubTexts.Get("Projects", "zh-CN") == "项目" && HubTexts.Get("Projects", "en-US") == "Projects",
            "同一 key 在中/英下取到不同文案（zh=项目, en=Projects）");

        Check(HubTexts.Get("NoSuchKey-" + Guid.NewGuid().ToString("N"), "zh-CN") is { Length: > 0 } missing
              && missing.StartsWith("NoSuchKey-", StringComparison.Ordinal),
            "缺失的 key 返回 key 本身而不是空白（缺失必须显式暴露）");

        Check(HubTexts.Normalize("zh-CN") == "zh-CN" && HubTexts.Normalize("en-US") == "en-US"
              && HubTexts.Normalize("fr-FR") == HubTexts.DefaultLanguage
              && HubTexts.Normalize(null) == HubTexts.DefaultLanguage && !HubTexts.IsSupported("fr-FR"),
            "两种受支持语言原样通过，未知/空值回落到默认语言（"
            + HubTexts.DefaultLanguage + "，语言来自设置文件，属用户数据，不能抛异常）");

        // The cold-start language (the language when there is no settings file). Deliberately hard-codes the literal rather than referencing HubTexts.DefaultLanguage:
        // referencing the constant would let a "change the default back to Chinese" regression drag the assertion along, i.e. no assertion at all.
        Check(new HubPreferences().Language == "en-US",
            "没有设置文件时冷启动语言是英文（没有 CJK 字体的 Linux 上中文起步会整片空白，实际 "
            + new HubPreferences().Language + "）");

        // Confirm one by one that none are empty — one empty string among hundreds is a blank label on the UI.
        var blankKeys = HubTexts.Keys
            .Where(key => string.IsNullOrWhiteSpace(HubTexts.Get(key, "zh-CN"))
                          || string.IsNullOrWhiteSpace(HubTexts.Get(key, "en-US")))
            .ToArray();
        Check(blankKeys.Length == 0,
            "全部 " + HubTexts.Keys.Count + " 条文案在中英两侧都非空"
            + (blankKeys.Length == 0 ? "" : "（空值: " + string.Join(", ", blankKeys) + "）"));

        // "value exactly equals key" is a missing-entry signal only on the **Chinese** side: Get returns the key itself for a missing key.
        // The English side can't be judged this way — many keys' English copy already equals the key (Projects / Version / Channel …),
        // and the first version of this assertion missed that, so 62 normal entries were reported as problems. That was caught by "running it", not by thinking it up.
        var missingChinese = HubTexts.Keys.Where(key => HubTexts.Get(key, "zh-CN") == key).ToArray();
        Check(missingChinese.Length == 0,
            "没有中文文案缺项（缺项会表现为返回值恰好等于键）"
            + (missingChinese.Length == 0 ? "" : "（缺项: " + string.Join(", ", missingChinese) + "）"));

        // Anti-regression: the two sides really differ. If someone pastes the whole English column as Chinese, the UI "displays but isn't localized",
        // and both assertions above would pass — so this one must exist separately.
        var localized = HubTexts.Keys.Count(key => HubTexts.Get(key, "zh-CN") != HubTexts.Get(key, "en-US"));
        Check(localized > HubTexts.Keys.Count / 2,
            "中英两侧确实存在不同文案（" + localized + "/" + HubTexts.Keys.Count + " 条不同，超过半数）");
    }

    internal void CheckWindowsTaskbarBadge()
    {
        Check(SystemAttentionService.WindowsTaskbarBadge.TaskbarInterface
                  == Guid.Parse("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")
              && SystemAttentionService.WindowsTaskbarBadge.TaskbarClass
                  == Guid.Parse("56FDF344-FD6D-11D0-958A-006097C9A090"),
            "任务栏 overlay 用的 IID_ITaskbarList3 与 CLSID_TaskbarList 和 Windows SDK 的 ShObjIdl_core.h 逐字一致（实际 "
            + SystemAttentionService.WindowsTaskbarBadge.TaskbarInterface + " / "
            + SystemAttentionService.WindowsTaskbarBadge.TaskbarClass + "）");

        if (!OperatingSystem.IsWindows())
        {
            _report.Skip("非 Windows 平台没有任务栏 overlay，跳过 ITaskbarList3 的真实探测");
            return;
        }

        // Really ask this host for the interface. Creating the object and running HrInit touches no window, so
        // this is safe on a live desktop, and it is the only assertion anywhere proving the shipped identifier is
        // one this machine's shell answers rather than one that merely matches a header. A thread that never
        // initialized COM is reported as a skip, not a failure — that is a fact about the session running the
        // suite. A SKIP here on Windows is itself the thing to notice: it would mean the probe never actually ran.
        var probed = SystemAttentionService.WindowsTaskbarBadge.TryProbe(out var hr, out var failure);
        if (!probed && hr == SystemAttentionService.WindowsTaskbarBadge.CO_E_NOTINITIALIZED)
        {
            _report.Skip("本线程未初始化 COM，跳过 ITaskbarList3 的真实探测（" + failure + "）");
            return;
        }

        Check(probed,
            "本机真的能按 IID_ITaskbarList3 建出任务栏对象并完成 HrInit（hr="
            + SystemAttentionService.WindowsTaskbarBadge.Code(hr)
            + (failure is null ? "" : "，" + failure) + "）");
    }

    internal void CheckWindowsToastInterop()
    {
        Check(WindowsToastInterop.XmlDocumentInterface == Guid.Parse("f7f3a506-1e87-42d6-bcfb-b8c809fa5494")
              && WindowsToastInterop.XmlDocumentIoInterface == Guid.Parse("6cd0e74e-ee65-4489-9ebf-ca43e87ba637")
              && WindowsToastInterop.ToastNotificationFactoryInterface == Guid.Parse("04124b20-82c6-4229-b109-fd9ed4662b53")
              && WindowsToastInterop.ToastNotificationManagerStaticsInterface == Guid.Parse("50ac103f-d235-4598-bbef-98fe4d1a3ad4")
              && WindowsToastInterop.ToastNotifierInterface == Guid.Parse("75927b93-03f3-41ec-91d3-6e5bac1b38e7"),
            "进程内 toast 用的五个 WinRT 接口 IID 与投影实测值逐字一致（实际 "
            + WindowsToastInterop.XmlDocumentInterface + " / " + WindowsToastInterop.XmlDocumentIoInterface + " / "
            + WindowsToastInterop.ToastNotificationFactoryInterface + " / "
            + WindowsToastInterop.ToastNotificationManagerStaticsInterface + " / "
            + WindowsToastInterop.ToastNotifierInterface + "）");

        if (!OperatingSystem.IsWindows())
        {
            _report.Skip("非 Windows 平台没有 WinRT toast，跳过进程内互操作的真实探测");
            return;
        }

        var probed = WindowsToastInterop.TryProbe(out var step, out var hr);
        Check(probed,
            "本机真的能按这些 IID 与槽位建出 toast 对象并拿到 notifier（停在 Show 之前，不画任何通知；"
            + "step=" + step + "，hr=" + WindowsToastInterop.Code(hr) + "）");
    }

    internal void CheckMacNotificationInterop()
    {
        Check(MacUserNotifications.PresentationBannerAndList == ((1 << 3) | (1 << 4))
              && MacUserNotifications.WillPresentTypeEncoding == "v@:@@@?"
              && MacUserNotifications.DidReceiveTypeEncoding == "v@:@@@?",
            "macOS 通知的呈现选项是 List|Banner（24），两个 delegate 方法注册为 v@:@@@?（与 UserNotifications 头文件一致）");

        if (!OperatingSystem.IsMacOS())
        {
            _report.Skip("非 macOS 平台没有 UserNotifications，跳过 delegate 与 global block 的真实探测");
            return;
        }

        var probed = MacUserNotifications.TryProbe(out var step, out var failure);
        Check(probed,
            "本机真的能建出 UNUserNotificationCenter delegate、按偏移取到 global block 的 invoke，并把 24 交回 completion"
            + "（step=" + step + (failure is null ? "" : "，" + failure) + "）");
        Check(MacUserNotifications.TryGetRequestConversationId(Guid.NewGuid().ToString("N"), out _)
              && !MacUserNotifications.TryGetRequestConversationId("not-a-conversation", out _),
            "macOS 通知请求的标识符只接受 32 位十六进制会话 id");
    }

    internal async Task CheckCjkFontNoticeAsync()
    {
        // ① Decision: only "genuinely no font + UI language is Chinese" bothers the user.
        Check(CjkFontNotice.ShouldWarn(CjkFontAvailability.Missing, HubTexts.ChineseLanguage)
              && !CjkFontNotice.ShouldWarn(CjkFontAvailability.Missing, HubTexts.EnglishLanguage)
              && !CjkFontNotice.ShouldWarn(CjkFontAvailability.Available, HubTexts.ChineseLanguage)
              && !CjkFontNotice.ShouldWarn(CjkFontAvailability.Unknown, HubTexts.ChineseLanguage),
            "缺字体只在「确实没有字体 + 界面语言是中文」时提示（英文界面不打扰；探测失败不误报）");

        // ② On Linux a directly paste-able command must be given — the whole weight of "prompt the user to install" is on this line.
        Check(CjkFontNotice.Message(linux: true).Contains(CjkFontNotice.DebianInstallCommand, StringComparison.Ordinal),
            "Linux 的提示里给出了可直接粘贴的安装命令（" + CjkFontNotice.DebianInstallCommand + "）");

        // ③ Reverse control: printing apt on a different OS is wrong. Without this, ② could pass with an always-true string.
        Check(!CjkFontNotice.Message(linux: false).Contains("apt", StringComparison.Ordinal),
            "非 Linux 的提示不给 apt 命令（照抄 apt 在别的系统上是错的）");

        // ④ The prompt itself must not contain CJK characters — the core of this group. The premise of the prompt is "Chinese can't render",
        //    so writing it in Chinese makes the people who most need to read it see a screen of boxes: the prompt becomes its own counterexample.
        var notice = string.Join("\n", CjkFontNotice.Title, CjkFontNotice.Message(true),
            CjkFontNotice.Message(false), CjkFontNotice.LogLine);
        var cjk = notice.Where(character => character >= 0x2E80).ToArray();
        Check(cjk.Length == 0,
            "缺字体提示的文案不含 CJK 字符（标题、两种正文、日志行都算）"
            + (cjk.Length == 0 ? "" : "（发现 " + new string(cjk) + "）"));

        // ⑤ The dialog buttons must also be English. HubDialog used to hard-code these four words in Chinese, and this prompt's only button
        //    would be a box on a machine missing the font — the prompt can't close the loop.
        var captions = new[] { "Ok", "Cancel", "Yes", "No" }
            .Select(key => HubTexts.Get(key, HubTexts.EnglishLanguage)).ToArray();
        Check(captions.All(caption => caption.All(character => character < 0x2E80)),
            "对话框按钮的英文文案不含 CJK 字符（实际 " + string.Join("/", captions) + "）");

        // ⑥ Really probe once. The previous five all passing while the probe isn't hooked up is easy: the conclusion is always Unknown (don't prompt),
        //    so the feature silently doesn't exist and no one notices.
        var availability = CjkFontProbe.Availability;
        var family = CjkFontProbe.MatchedFamily is { Length: > 0 } name ? name : "（未匹配到）";
        Check(availability != CjkFontAvailability.Unknown,
            "字体探测在本机得出了结论，而不是 Unknown（实际 " + availability + "，匹配到 " + family + "）");

        // ⑦ Windows / macOS ship Chinese fonts, so reporting Missing there can only mean the probe is wrong.
        //    Linux allows Missing — that's the whole point of this feature — so tighten per platform rather than requiring Available everywhere.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Check(availability == CjkFontAvailability.Available,
                "Windows/macOS 自带中文字体，探测结果应是 Available（实际 " + availability
                + "，匹配到 " + family + "）");
        }

        // ⑧ The dialog suppression switch: no one clicks the missing-font prompt in automation, so it would hang the self-check and must be blocked
        //    by App.Options.IsAutomation; the product path must **not** be blocked.
        //    Assert both directions — with only one, an always-true or always-false implementation could slip through.
        Check(App.Options.IsAutomation && !HubHostOptions.Parse([]).IsAutomation,
            "自动化模式（本次就是）关掉缺字体提示，裸启动不受影响（实际 "
            + App.Options.IsAutomation + " / " + HubHostOptions.Parse([]).IsAutomation + "）");
        var attentionTestOptions = HubHostOptions.Parse(["--test-system-attention"]);
        Check(attentionTestOptions.TestSystemAttention && !attentionTestOptions.IsAutomation
              && !HubHostOptions.Parse(["--verify-shell"]).TestSystemAttention,
            "系统通知手动诊断开关可单独启用真实桌面提示，且不会污染自动化自检");

        // ⑨ Really call the entry point once and confirm it **indeed doesn't pop** under automation. This isn't a repeat of ⑧: ⑧ reads the flag,
        //    ⑨ reads "did an extra window appear after the call" — when the gate is placed elsewhere (e.g. forgot the check, or the check is after an await),
        //    only ⑨ goes red. The missing-font prompt popping during a self-check means hanging, the costliest outcome.
        var windowsBefore = HubDialogWindows().Length;
        var notified = CjkFontNotifier.NotifyIfNeeded(Fixture.Host);
        Check(!notified && HubDialogWindows().Length == windowsBefore,
            "自动化模式下缺字体提示确实没有弹出来（返回 " + notified + "，弹窗数 "
            + windowsBefore + " → " + HubDialogWindows().Length + "）");

        // ⑩ Build the exact thing that would pop, and read back its title/body/buttons to confirm they match.
        //    Asserting string constants isn't the same as asserting what's really displayed: if HubDialog's parameter order
        //    (title, body) were reversed, the string assertions would still be all green.
        var pending = HubDialog.ShowAsync(null, CjkFontNotice.Title, CjkFontNotice.Message(linux: true));
        var prompt = HubDialogWindows().LastOrDefault();
        Dispatcher.UIThread.RunJobs();
        var promptTitle = prompt is null ? null : NamedDescendant<TextBlock>(prompt, "DialogTitle")?.Text;
        var promptBody = prompt is null ? null : NamedDescendant<TextBlock>(prompt, "DialogMessage")?.Text;
        Check(prompt is not null && promptTitle == CjkFontNotice.Title,
            "缺字体提示的标题写进了对话框（实际 " + promptTitle + "）");
        Check(promptBody == CjkFontNotice.Message(linux: true)
              && promptBody is not null && promptBody.Contains(CjkFontNotice.DebianInstallCommand, StringComparison.Ordinal),
            "提示正文写进了对话框，且里面带着那条安装命令（" + CjkFontNotice.DebianInstallCommand + "）");

        // The answers the box offers — deliberately not every Button in the window, because the drawn header
        // carries a plate too and a control that closes the box is not an answer to its question. The plate is
        // asserted with the dialog-chrome group.
        var promptButtons = prompt is null ? [] : ActionButtons(prompt);
        Check(promptButtons.Length == 1 && promptButtons[0].Content?.ToString() == HubTexts.Get("Ok", HubStrings.Language),
            "提示只有一个按钮，文案跟着界面语言走（实际 "
            + string.Join("/", promptButtons.Select(b => b.Content?.ToString())) + "）");

        // It closes — the "failure dialog must be closable" lesson: an unclosable notice locks the user in front of the UI.
        promptButtons.FirstOrDefault()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var promptResult = await pending;
        Check(promptResult == HubDialogResult.Ok && HubDialogWindows().Length == windowsBefore,
            "点掉提示后窗口真的关闭（结果 " + promptResult + "）");
    }

    internal void CheckEveryXamlResourceKeyResolves()
    {
        var source = FindSourceDirectory();
        if (source is null)
        {
            // The installed self-contained artifacts have no .axaml source. **Record it explicitly** rather than skipping silently:
            // the report must distinguish "verified" from "this didn't run".
            _report.Skip("源码目录不在，跳过 XAML 资源 key 扫描（仅开发/CI 环境可跑）");
            return;
        }

        var textKeys = new SortedSet<string>(StringComparer.Ordinal);
        var tokenKeys = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*.axaml", System.IO.SearchOption.AllDirectories))
        {
            // Strip XML comments first: comments mention {DynamicResource X} as explanatory text,
            // and those aren't real references; counting them would make this assertion a source of false reports.
            var xaml = Regex.Replace(System.IO.File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
            foreach (Match match in Regex.Matches(xaml, @"\{DynamicResource\s+([^}]+)\}"))
            {
                var key = match.Groups[1].Value.Trim();
                (key.StartsWith("Hub.", StringComparison.Ordinal) ? tokenKeys : textKeys).Add(key);
            }
        }

        Check(textKeys.Count > 0 && tokenKeys.Count > 0,
            "扫到 " + textKeys.Count + " 个文案 key 与 " + tokenKeys.Count + " 个主题 token 引用");

        var unresolvedTokens = tokenKeys.Where(key => !Resolves(key)).ToArray();
        Check(unresolvedTokens.Length == 0,
            "全部 " + tokenKeys.Count + " 个主题 token 都能解析"
            + (unresolvedTokens.Length == 0 ? "" : "（未解析: " + string.Join(", ", unresolvedTokens) + "）"));

        var unresolvedText = textKeys.Where(key => !HubTexts.Keys.Contains(key)).ToArray();
        Check(unresolvedText.Length == 0,
            "全部 " + textKeys.Count + " 个文案 key 都存在于 HubTexts"
            + (unresolvedText.Length == 0 ? "" : "（缺失: " + string.Join(", ", unresolvedText) + "）"));

        // "exists in the definition" isn't enough — what really decides whether the UI is blank is whether it's in the **runtime resource dictionary**.
        var notInDictionary = textKeys.Where(key => !Resolves(key)).ToArray();
        Check(notInDictionary.Length == 0,
            "全部文案 key 已在启动时灌进运行时资源字典（DynamicResource 才解析得出）"
            + (notInDictionary.Length == 0 ? "" : "（未灌入: " + string.Join(", ", notInDictionary) + "）"));
    }
}
