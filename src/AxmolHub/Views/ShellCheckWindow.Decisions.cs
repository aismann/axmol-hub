using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;
using static AxmolHub.ShellCheckFixture;

namespace AxmolHub;
internal sealed class ShellDecisionsChecks
{
    private readonly Action<bool, string> Check;
    private readonly ShellCheckFixture Fixture;
    internal ShellDecisionsChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
        Fixture = fixture;
    }

    internal async Task CheckActivityOrchestrationAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        var workspace = Path.Combine(ScratchDirectory.Resolve("activity-workspace"), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(workspace);
        // Held open by the last scene so a check can work on a run that is still calling tools; released on the
        // way out whatever an assertion did, because a closed gate parks the suite, not just the cell.
        var livePark = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerPark = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.SetWorkspaceRoot(session.Id, workspace);
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        chat.OpenConversation(session.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // ── the thinking glyph starts its clock, and stops it ──
            var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["想好了再答"], gate: hold.Task);
            panel.SetInputForCheck("先想想");
            _ = panel.SendComposerForCheck();
            await WaitUntilAsync(() => panel.IsStreamingForCheck);
            // Wait on the bubble's frame counter, not on the ticking itself: the 350ms tick drives both the
            // elapsed label and the glyph's phase, so a frame > 0 proves that tick has run at least once — and
            // waiting on the asserted predicate would turn the cell into a retry-until-pass.
            await WaitUntilAsync(() => panel.LiveFrameForCheck > 0);
            Check(panel.LiveGlyphTickingForCheck
                  && panel.LiveGlyphPhaseForCheck == ActivityPhase.WaitingFirstToken,
                "回复还没吐字时，思考指示器按「等第一个字」的相位走针（相位 "
                + panel.LiveGlyphPhaseForCheck + "，可见 " + panel.LiveGlyphVisibleForCheck
                + "，走针 " + panel.LiveGlyphTickingForCheck + "，帧 " + panel.LiveFrameForCheck + "）");
            hold.SetResult(true);
            await panel.WaitForRunToFinishForCheck();
            Check(!panel.LiveGlyphTickingForCheck,
                "回复结束后思考指示器停针：一个停不下来的动画会一直重绘整块画布");

            // ── switching sessions does not restart the reply's clock ──
            // The elapsed counter used to live on the bubble, and the bubble is destroyed the moment the user
            // looks at another session, so coming back rebuilt it from zero and the reply claimed a duration that
            // started on the way back in. The clock is the run's now, which is the only thing in this pairing that
            // was ever running while the session was out of sight.
            var timedSession = chat.StartConversation();
            var idleSession = chat.StartConversation();
            var laterPark = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, id) => id == timedSession.Id
                ? new ScriptedChatClient(["切走这段时间还在跑"], null, laterPark.Task, firstArrived)
                : new ScriptedChatClient(["另一路会话的回复"]);
            chat.OpenConversation(timedSession.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            panel.SetInputForCheck("别在切回来的时候重启计时");
            _ = panel.SendComposerForCheck();
            await WaitForSignalAsync(firstArrived.Task);
            // A precondition, not the assertion: until the answer has been running for a whole second there is no
            // number on the label for a rebuilt bubble to lose, and this scene would prove nothing.
            await WaitUntilAsync(() => panel.ChatActivityElapsedForCheck != "0s", 800);
            var elapsedBefore = panel.ChatActivityElapsedForCheck;
            chat.OpenConversation(idleSession.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.ChatActivityVisibleForCheck,
                "切到别的会话，这一页不再画那条回复的活动状态（它在自己那一路接着跑）");
            chat.OpenConversation(timedSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var elapsedAfter = panel.ChatActivityElapsedForCheck;
            // Frame 0 is what makes this a brand-new bubble rather than the one that never left, so the label it
            // shows can only have come off the run.
            Check(panel.LiveFrameForCheck == 0 && elapsedAfter != "0s"
                  && ElapsedSeconds(elapsedAfter) >= ElapsedSeconds(elapsedBefore),
                "切回来气泡是新建的（帧 " + panel.LiveFrameForCheck + "），耗时却从「" + elapsedBefore
                + "」继续走到「" + elapsedAfter + "」，而不是从 0s 重新开始");
            laterPark.SetResult(true);
            await panel.WaitForRunToFinishForCheck();
            chat.DeleteConversation(timedSession.Id);
            chat.DeleteConversation(idleSession.Id);

            // ── a run of tool calls folds into one collapsible group, counted rather than listed ──
            // Its own session: the waiting-glyph run above leaves a thinking-only turn behind, and a second
            // activity run in the same transcript would fold into a second group and miscount this one.
            var groupSession = chat.StartConversation();
            chat.SetWorkspaceRoot(groupSession.Id, workspace);
            chat.SetApprovalMode(groupSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(groupSession.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            var activity = new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "list_directory",
                Arguments = new Dictionary<string, object?> { ["path"] = "." },
            };
            chat.ClientOverride = (_, _) => activity;
            panel.SetInputForCheck("看看这仓库根目录");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Check(panel.ActivityGroupCountForCheck == 1,
                "一段带工具调用的活动折成一个可折叠组，而不是一路铺开（实际 "
                + panel.ActivityGroupCountForCheck + " 组：["
                + string.Join(" | ", Enumerable.Range(0, panel.ActivityGroupCountForCheck)
                    .Select(g => panel.ActivityGroupTitleForCheck(g)))
                + "]；转录 " + string.Join(",", groupSession.Messages.Select(m =>
                    m.Role + (m.ToolCallId is { Length: > 0 } ? "/call" : "")
                    + (m.Reasoning is { Length: > 0 } ? "/reason" : "")
                    + (m.Text.Length == 0 ? "/empty" : ""))) + "）");
            var listVerb = string.Format(CultureInfo.CurrentCulture, HubStrings.Get("ActivityRowList"), ".");
            Check(panel.ActivityGroupTitleForCheck(0).Contains(listVerb, StringComparison.Ordinal),
                "组头用满宽的一句话点名这次做了什么，而不是一个贴左的计数药丸（实际「"
                + panel.ActivityGroupTitleForCheck(0) + "」）");
            Check(!panel.ActivityGroupExpandedForCheck(0),
                "活动组默认收起：细节是点进去才看的，不该先糊一屏");
            panel.SetActivityGroupExpandedForCheck(0, true);
            Check(panel.ActivityGroupExpandedForCheck(0)
                  && panel.ActivityRowsForCheck.Count(row => row == listVerb) == 1,
                "点一下组头展开，每个工具是一行描述动作的行（实际行 "
                + string.Join(" / ", panel.ActivityRowsForCheck) + "）");
            panel.SetActivityGroupExpandedForCheck(0, false);
            Check(!panel.ActivityGroupExpandedForCheck(0), "再点一下收回去");
            chat.DeleteConversation(groupSession.Id);

            // ── one action row opens into its own concrete operation ──
            // The group opening was only half the story: inside, each row still folded its command to a 60-char chip
            // and hid the tool's answer behind a hover that a trackpad can't reach and a check can't read. A row now
            // carries its own expander, and it is the payload's only surface — so the seed uses a run_command whose
            // command is longer than that chip and a return long enough to overflow the opened box, neither of which
            // the collapsed row can show.
            var detailSession = chat.StartConversation();
            chat.SetWorkspaceRoot(detailSession.Id, workspace);
            chat.SetApprovalMode(detailSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(detailSession.Id);
            var longCommand =
                "cmake -S D:/dev/axmolengine/axmol-hub/samples/cpp/CMakeLists.txt -B build && cmake --build build";
            var longPayload = "shell: PowerShell 7 · exit: 0\n" + string.Join("\n",
                Enumerable.Range(1, 60).Select(step =>
                    $"[ 42%] Building CXX object CMakeFiles/app.dir/src/Scene{step}.cpp.o，这一行足够长以撑出输出块"));
            var detailTurns = chat.ActiveConversation!.Messages;
            detailTurns.Add(ChatTurn.User("把计算器编一下"));
            detailTurns.Add(ChatTurn.FunctionCall("call_det", "run_command", "{\"command\":\"" + longCommand + "\"}"));
            detailTurns.Add(ChatTurn.FunctionResult("call_det", longPayload));
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.SetActivityGroupExpandedForCheck(0, true);
            Relayout(shell, panel, measure: true);
            Check(panel.ActivityGroupCountForCheck == 1 && panel.ActivityRowToggleCountForCheck == 1,
                "每条工具行自带一个可点开的展开器（组 " + panel.ActivityGroupCountForCheck
                + "，展开器 " + panel.ActivityRowToggleCountForCheck + "）");
            panel.SetActivityGroupExpandedForCheck(0, true);
            Dispatcher.UIThread.RunJobs();
            Check(!panel.ActivityRowExpandedForCheck(0),
                "工具行默认也是收起的：先只看到一句动作，点开才看那次调用的细节");
            Check(panel.ActivityRowTooltipForCheck(0).Length == 0,
                "收起的工具行身上不再挂 payload tooltip，整段返回只属于展开面（实际挂了 "
                + panel.ActivityRowTooltipForCheck(0).Length + " 字）");
            panel.SetActivityRowExpandedForCheck(0, true);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var detailText = panel.ActivityRowDetailTextForCheck(0);
            // The report is one line per cell, and this payload is deliberately 60 lines long, so what it prints is
            // the head of the detail on one line — enough to see what actually rendered, not the whole box.
            var detailHead = detailText.Replace('\n', ' ').Replace('\r', ' ');
            if (detailHead.Length > 100) detailHead = detailHead[..100] + "…";
            Check(panel.ActivityRowExpandedForCheck(0)
                  && detailText.Contains(longCommand, StringComparison.Ordinal)
                  && detailText.Contains("shell: PowerShell 7 · exit: 0", StringComparison.Ordinal),
                "点开这一行看得到完整命令原文（未截断）与工具返回的 payload（实际详情「" + detailHead + "」）");
            Check(panel.ActivityRowDetailScrollsForCheck(0),
                "输出块是有界可滚动的：比界面装得下的还长时读得到底，而不是被省略号截掉");
            panel.SetActivityRowExpandedForCheck(0, false);
            Dispatcher.UIThread.RunJobs();
            Check(!panel.ActivityRowExpandedForCheck(0), "再点一下把这条行收回去");

            // ── the expander arrow lives on the left, in the status slot, and is the sidebar's own glyph ──
            // The arrow used to sit on the row's right edge, so the eye crossed the whole column to reach it, and a
            // checked group head wore the Fluent accent plate like a purple pill. The arrow is now the sidebar's
            // ChevronRight, sharing the status cell (it swaps in on hover), and the head borrows the Expander theme.
            Check(panel.ActivityRowChevronCountForCheck == panel.ActivityRowsForCheck.Length
                  && panel.ActivityRowChevronColumnForCheck(0) == 0,
                "每条工具行的展开箭头在左侧、与状态图标同一格（箭头 " + panel.ActivityRowChevronCountForCheck
                + " 颗 / 行 " + panel.ActivityRowsForCheck.Length + " 条，第 0 行箭头在第 "
                + panel.ActivityRowChevronColumnForCheck(0) + " 列）");
            Check(panel.ActivityRowChevronUsesSidebarGlyphForCheck,
                "展开箭头用的就是左栏工作区那颗 >／v，不是另画的相似物");
            Check(panel.ActivityGroupHeadUsesExpanderForCheck,
                "组头走左栏的 Expander 主题：拿它的左箭头，也不再被涂成紫色药丸");
            Check(panel.ActivityRowStatusSizeForCheck == 10,
                "执行状态的对勾/八叉缩到 10，安静地待在箭头旁边（实际 " + panel.ActivityRowStatusSizeForCheck + "）");
            chat.DeleteConversation(detailSession.Id);

            // ── a command's card: on the page while it prints, and what it ended with afterwards ──
            // A command used to leave a status line in the live bubble and nothing in the transcript until it came
            // back, so a ten-minute build was one row shown after the wait it should have filled. The call is
            // recorded before it runs, and this card is drawn from that call: the command, the directory it runs
            // in, the state, and the shell's own lines appended as they arrive. When it ends, the row the transcript
            // keeps carries the two numbers a person asks for — the exit code and how long it took.
            var verdictSession = chat.StartConversation();
            chat.SetWorkspaceRoot(verdictSession.Id, workspace);
            chat.SetApprovalMode(verdictSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(verdictSession.Id);
            var verdictTurns = chat.ActiveConversation!.Messages;
            verdictTurns.Add(ChatTurn.User("先编，再跑测试"));
            verdictTurns.Add(ChatTurn.FunctionCall("call_ok", "run_command", "{\"command\":\"dotnet build\"}"));
            verdictTurns.Add(ChatTurn.FunctionResult("call_ok", "shell: pwsh · exit: 0\nBuild succeeded",
                facts: new ToolRunFacts(0, TimeSpan.FromSeconds(5))));
            verdictTurns.Add(ChatTurn.FunctionCall("call_bad", "run_command", "{\"command\":\"dotnet test\"}"));
            verdictTurns.Add(ChatTurn.FunctionResult("call_bad", "shell: pwsh · exit: 1\n1 test failed",
                failed: true, facts: new ToolRunFacts(1, TimeSpan.FromSeconds(42))));
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.SetActivityGroupExpandedForCheck(0, true);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var exitChips = panel.ActivityRowExitChipsForCheck;
            var rowMetas = panel.ActivityRowMetaForCheck;
            Check(exitChips.Length == 2 && exitChips[0].Contains('0') && exitChips[1].Contains('1'),
                "命令结束时把退出码留在那一行上（实际 [" + string.Join(" / ", exitChips) + "]）");
            Check(rowMetas.Contains("5s") && rowMetas.Contains("42s"),
                "行尾还写着这条命令花了多久（实际 [" + string.Join(" / ", rowMetas) + "]）");
            chat.DeleteConversation(verdictSession.Id);

            // ── the card exists while the command is still printing ──
            // A real process this time, and a slow one on purpose: the whole claim is about the stretch *before*
            // the result exists, which no finished fixture can stand in for.
            var printingSession = chat.StartConversation();
            chat.SetWorkspaceRoot(printingSession.Id, workspace);
            chat.SetApprovalMode(printingSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(printingSession.Id);
            panel.Reload();
            var printingShell = CommandShells.ForCurrent();
            var slowCommand = printingShell.IsPowerShell
                ? "Write-Output 'first-line-of-build'; Start-Sleep -Seconds 3; Write-Output 'last-line-of-build'"
                : "echo first-line-of-build; sleep 3; echo last-line-of-build";
            var printing = new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "run_command",
                Arguments = new Dictionary<string, object?> { ["command"] = slowCommand },
            };
            chat.ClientOverride = (_, _) => printing;
            panel.SetInputForCheck("编一下这个工程");
            await panel.SendComposerForCheck();
            // The call is filed before the gate, so the first frame of this card may honestly say 「即将执行」 —
            // what is asserted here is that the card exists at all in that breath, and that it turns into
            // 「正在执行」 when the tool actually owns the run.
            await WaitUntilAsync(() => panel.RunningCommandRowShownForCheck, 2000);
            Check(panel.RunningCommandRowShownForCheck,
                "命令一开始跑，页面上就有一张卡片，而不是等它结束才有（行「"
                + panel.RunningCommandRowTextForCheck + "」）");
            Check(panel.RunningCommandRowTextForCheck.Contains("first-line-of-build", StringComparison.Ordinal),
                "卡片上带着要跑的那条命令（实际「" + panel.RunningCommandRowTextForCheck + "」）");
            await WaitUntilAsync(() => panel.RunningCommandRowTextForCheck.Contains(
                HubStrings.Get("ActivityRowCommandRunning"), StringComparison.Ordinal), 400);
            Check(panel.RunningCommandRowTextForCheck.Contains(
                      HubStrings.Get("ActivityRowCommandRunning"), StringComparison.Ordinal),
                "工具真正拿到这段运行时，卡片说的是「正在执行」（实际「"
                + panel.RunningCommandRowTextForCheck + "」）");
            // A command in flight uses a terminal prompt rather than a verdict or the regenerate button's arrow.
            // Check the glyph identity so a visually similar but misleading icon cannot silently replace it.
            Check(panel.ActivityRowRunningGlyphForCheck,
                "运行中的命令行用终端提示符 >_，不是可点击的重试箭头");
            Check(!panel.RunningCommandExpandedForCheck,
                "卡片默认收着：一行说清在跑什么，输出不替人把整列顶开");
            // The clock is what tells a silent command from a dead one, so it has to be there before the first line
            // is: this is the same number the finished row will keep.
            Check(panel.ActivityRowMetaForCheck.Any(meta => meta.EndsWith('s')),
                "运行中的卡片带一个计时（实际 [" + string.Join(" / ", panel.ActivityRowMetaForCheck) + "]）");
            await WaitUntilAsync(() =>
                panel.RunningCommandOutputForCheck.Contains("first-line-of-build", StringComparison.Ordinal), 600);
            Check(panel.RunningCommandOutputForCheck.Contains("first-line-of-build", StringComparison.Ordinal),
                "命令的输出一边跑一边追加进这张卡片（实际「" + panel.RunningCommandOutputForCheck + "」）");
            panel.SetRunningCommandExpandedForCheck(true);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.RunningCommandExpandedForCheck,
                "点一下展开：已经打出来的内容就在那里，不用等命令结束");
            panel.SetRunningCommandExpandedForCheck(false);
            Dispatcher.UIThread.RunJobs();
            Check(!panel.RunningCommandExpandedForCheck, "再点一下收回去");
            // A command that prints and sleeps is longer than the run helper's own one-second budget, so the wait
            // is the harness's poll loop rather than that helper.
            await WaitUntilAsync(() => !panel.IsStreamingForCheck, 2000);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.RunningCommandRowShownForCheck,
                "命令结束后临时卡片让位给正式那一行，页面上不会同时有两条");
            panel.SetActivityGroupExpandedForCheck(0, true);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.ActivityRowExitChipsForCheck is [var printedExit] && printedExit.Contains('0'),
                "结束的那一行带着退出码（实际 [" + string.Join(" / ", panel.ActivityRowExitChipsForCheck) + "]）");
            chat.DeleteConversation(printingSession.Id);

            // ── a call waiting for a decision is not "running" ──
            // The same command, in a mode that has to ask: the card that owns this stretch is the approval one, and
            // a row claiming the command had started would say a person's click had already happened.
            var askingSession = chat.StartConversation();
            chat.SetWorkspaceRoot(askingSession.Id, workspace);
            chat.SetApprovalMode(askingSession.Id, ToolApprovalModes.Ask);
            chat.OpenConversation(askingSession.Id);
            panel.Reload();
            var asking = new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "run_command",
                Arguments = new Dictionary<string, object?> { ["command"] = "echo hello" },
            };
            chat.ClientOverride = (_, _) => asking;
            panel.SetInputForCheck("跑个 echo");
            await panel.SendComposerForCheck();
            await WaitUntilAsync(() => !panel.IsStreamingForCheck);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var parkedCall = chat.ActiveConversation!.Messages.LastOrDefault(turn =>
                turn.Role == ChatRoles.Assistant && turn.ApprovalState == ChatApprovalStates.Pending);
            Check(parkedCall is not null && !panel.RunningCommandRowShownForCheck,
                "等审批的命令不画成「正在执行」：那一格里站着的是提问的卡片（运行中卡片 "
                + panel.RunningCommandRowShownForCheck + "）");
            chat.DeleteConversation(askingSession.Id);
            chat.ClientOverride = savedOverride;

            // ── the command preview reads beside its verb, not across the column ──
            // The row laid its label into a star column and the preview after it, so the label ate the slack and the
            // preview landed on the row's right edge — the same second-column problem the expander arrow was moved
            // off, one line further down. A short command on purpose: a preview long enough to fill the column sits
            // beside its verb for the wrong reason, because there was nowhere else to be, and that is the reading
            // this pair of numbers exists to rule out.
            var alignSession = chat.StartConversation();
            chat.SetWorkspaceRoot(alignSession.Id, workspace);
            chat.SetApprovalMode(alignSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(alignSession.Id);
            var alignTurns = chat.ActiveConversation!.Messages;
            alignTurns.Add(ChatTurn.User("跑个短命令"));
            alignTurns.Add(ChatTurn.FunctionCall("call_align", "run_command",
                "{\"command\":\"git status --short\"}"));
            alignTurns.Add(ChatTurn.FunctionResult("call_align", "ok"));
            panel.Reload();
            panel.SetActivityGroupExpandedForCheck(0, true);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var (rowInline, rowGapText) = panel.ActivityRowTrailingGapForCheck(0) is { } measured
                ? (measured.Gap <= 12 && measured.RightSlack > 40,
                   "间隙 " + measured.Gap.ToString("0.#") + "px，右余 "
                   + measured.RightSlack.ToString("0.#") + "px")
                : (false, "量不出来");
            Check(rowInline,
                "工具行的命令预览紧跟在动作名后面，不再隔着整列钉到右端（实际 " + rowGapText + "）");
            Check(panel.ActivityRowChipColumnForCheck(0) == 2 && panel.ActivityRowChevronColumnForCheck(0) == 0,
                "命令预览排在动作名之后那一列，展开箭头仍在第 0 列（实际 预览第 "
                + panel.ActivityRowChipColumnForCheck(0) + " 列，箭头第 "
                + panel.ActivityRowChevronColumnForCheck(0) + " 列）");
            chat.DeleteConversation(alignSession.Id);

            // ── a group the person opened stays open while the model keeps calling tools ──
            // Every cell above reads a finished transcript, which is how this slipped through: each new call used
            // to tear the whole flow down and lay it back out, so the head somebody had opened to read came up
            // collapsed again — once per call, for as long as the model kept working. The window to see it is
            // between two calls, so the fixture parks in front of its follow-up request.
            var liveSession = chat.StartConversation();
            chat.SetWorkspaceRoot(liveSession.Id, workspace);
            chat.SetApprovalMode(liveSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(liveSession.Id);
            panel.Reload();
            // Reading position only means something when there is somewhere else to be, so the run is put at the
            // end of a transcript that already overflows the column.
            var filler = string.Join("\n\n", Enumerable.Range(1, 40)
                .Select(line => $"第 {line} 段：这一段的长度足够把消息列撑出一屏之外，剩下的要滚动才读得完。"));
            chat.ClientOverride = (_, _) => new ScriptedChatClient([filler]);
            panel.SetInputForCheck("先写满一屏");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();

            var live = new ApprovalChatClient
            {
                CallsRemaining = 2,
                ToolName = "list_directory",
                Arguments = new Dictionary<string, object?> { ["path"] = "." },
                ParkBeforeFollowUp = livePark.Task,
                FollowUpReached = liveArrived,
                ParkBeforeAnswer = answerPark.Task,
                AnswerReached = answerReached,
            };
            chat.ClientOverride = (_, _) => live;
            panel.SetInputForCheck("接着列两次目录");
            _ = panel.SendComposerForCheck();
            await WaitForSignalAsync(liveArrived.Task);
            // One call answered is one row inside one group, and the stream is now held at the park.
            panel.SetActivityGroupExpandedForCheck(0, true);
            Relayout(shell, panel, measure: true);
            await WaitUntilAsync(() => panel.ActivityRowsForCheck.Length == 1);
            panel.SetActivityRowExpandedForCheck(0, true);
            Relayout(shell, panel, measure: true);
            // Leave the bottom while keeping the expanded group visible; an absolute offset near zero reads
            // the earlier filler document, whose offscreen group correctly has no realized controls.
            panel.ScrollTranscriptForCheck(Math.Max(0, panel.FlowOffsetForCheck - 40));
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var openedToken = panel.ActivityGroupInstanceTokenForCheck(0);
            var openedDetail = panel.ActivityRowDetailTextForCheck(0);
            Check(panel.ActivityGroupExpandedForCheck(0) && openedToken != 0
                  && panel.ActivityRowExpandedForCheck(0) && openedDetail.Length > 0
                  && !panel.ViewAtBottomForCheck && panel.ScrollToBottomVisible,
                "夹具：组与其第一行都展开了、转录读到中间，回底板的按钮升起来了（组 "
                + panel.ActivityGroupCountForCheck + "，行 " + panel.ActivityRowsForCheck.Length + "）");

            var liveAnchor = panel.VirtualAnchorForCheck;
            livePark.SetResult(true);
            // Signalled from the fixture rather than polled off a row count. Counting rows stopped meaning "the
            // second call landed" once a thinking row was one of them, and polling for the second verb row raced
            // the answer: by the time it came true the run had finished, and a finished run scrolling to the newest
            // line is correct behaviour that would have read here as the bug this check exists to catch.
            await WaitForSignalAsync(answerReached.Task);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.ActivityGroupCountForCheck == 1,
                "第二个工具折进同一个组里，而不是又开一折（实际 " + panel.ActivityGroupCountForCheck + " 组：["
                + string.Join(" | ", Enumerable.Range(0, panel.ActivityGroupCountForCheck)
                    .Select(g => panel.ActivityGroupTitleForCheck(g))) + "]）");
            // The count side of the head's rule, on a run with two actions: naming whichever one happened to come
            // first reads as a claim about what the whole run was for. The single-call group further up still
            // names its verb, and that half of the boundary is what the assertion there guards.
            Check(panel.ActivityGroupTitleForCheck(0) == string.Format(CultureInfo.CurrentCulture,
                    HubStrings.Get("ActivityGroupToolCountFormat"), 2),
                "两个以上调用的组头只报数，不再点名第一个动作（实际「" + panel.ActivityGroupTitleForCheck(0) + "」）");
            Check(panel.ActivityGroupExpandedForCheck(0),
                "模型又调了一个工具，用户展开的根节点不会当场折叠回去");
            Check(panel.ActivityGroupInstanceTokenForCheck(0) == openedToken,
                "组是就地长出来的同一个控件，而不是整段重建后再恢复成展开（展开前 " + openedToken
                + "，现在 " + panel.ActivityGroupInstanceTokenForCheck(0) + "）");
            Check(panel.VirtualExchangeIdsForCheck.Length == 2
                  && panel.VirtualExchangeIdsForCheck.Distinct().Count() == 2
                  && panel.ActivityRowsForCheck.Count(row => row == listVerb) <= 2,
                "两次调用各有一个稳定结果项，离屏行无需保留控件（当前已实现行 "
                + string.Join(" / ", panel.ActivityRowsForCheck) + "）");
            Check(!panel.ViewAtBottomForCheck && panel.ScrollToBottomVisible
                  && panel.VirtualAnchorForCheck.Key == liveAnchor.Key
                  && Math.Abs(panel.VirtualAnchorForCheck.Top - liveAnchor.Top) <= 2,
                "跑着的工具调用不再把读数位置拽回底部，也没有因为整段清空而被夹到 0（offset 现在是 "
                + panel.FlowOffsetForCheck + "，在底部 " + panel.ViewAtBottomForCheck + "，回底板 "
                + panel.ScrollToBottomVisible + "；此刻仍在运行 "
                + (chat.IsRunning(liveSession.Id) ? "是" : "否") + "，流式气泡 "
                + (panel.IsStreamingForCheck ? "在" : "没") + "，行 ["
                + string.Join(" / ", panel.ActivityRowsForCheck) + "]）");
            // The group re-derives its rows off the transcript as each call lands, so a row the person opened only
            // stays open if that choice was remembered by the call it reports rather than left on a control that
            // is being replaced.
            Check(panel.ActivityRowExpandedForCheck(0) && panel.ActivityRowDetailTextForCheck(0) == openedDetail,
                "组重新推导过行之后，人点开的那条工具行还开着，详情也还是同一份（详情「"
                + panel.ActivityRowDetailTextForCheck(0) + "」）");
            Check(!panel.ActivityRowExpandedForCheck(1),
                "新长出来的那条工具行默认收起：记住人开过的折叠，不等于替他每一个都打开");
            answerPark.SetResult(true);
            await panel.WaitForRunToFinishForCheck();

            // The same page then gets a second run, which has to open its own fold and default it to collapsed:
            // keeping the person's choice is one thing, inventing a second one for a group they never touched is
            // another. It also proves the run's prose closed the first group rather than the tail folding in.
            chat.ClientOverride = (_, _) => new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "list_directory",
                Arguments = new Dictionary<string, object?> { ["path"] = "." },
            };
            panel.SetInputForCheck("再列一次目录");
            await panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.ActivityGroupCountForCheck == 2
                  && panel.ActivityGroupExpandedForCheck(0) && !panel.ActivityGroupExpandedForCheck(1),
                "新的一轮机器活开自己的第二折，默认收起；人开过的那一折不受牵连（组 "
                + panel.ActivityGroupCountForCheck + "，第一折展开 " + panel.ActivityGroupExpandedForCheck(0)
                + "，第二折展开 " + panel.ActivityGroupExpandedForCheck(1) + "）");

            // The one rebuild no running stream can avoid: reading another session and coming back lays this one
            // out from nothing, so the fold the person left open has to return from a remembered choice rather
            // than from a control that no longer exists.
            var readBack = chat.StartConversation();
            chat.OpenConversation(readBack.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            chat.OpenConversation(liveSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.ActivityGroupCountForCheck == 2
                  && panel.ActivityGroupExpandedForCheck(0) && !panel.ActivityGroupExpandedForCheck(1)
                  && panel.ActivityRowExpandedForCheck(0) && !panel.ActivityRowExpandedForCheck(1)
                  && panel.ActivityRowDetailTextForCheck(0) == openedDetail,
                "切到别的会话再切回来（整段重建），开过的组与行按人留下的选择恢复，没开过的照旧收起（第一折 "
                + panel.ActivityGroupExpandedForCheck(0) + "，第二折 " + panel.ActivityGroupExpandedForCheck(1)
                + "，行 0 " + panel.ActivityRowExpandedForCheck(0) + "，行 1 "
                + panel.ActivityRowExpandedForCheck(1) + "，行 " + panel.ActivityRowsForCheck.Length
                + " 条，详情「" + panel.ActivityRowDetailTextForCheck(0) + "」对照「" + openedDetail + "」）");
            // ── the run's aggregate count is the way into the pane ──
            // The chip only exists when a write parked and froze a preview, so this fixture is the ask tier: the
            // number on the group head is the same diff the approval card just asked about, and pressing it has to
            // land the reader on that file rather than on a plan they did not ask for.
            var chipSession = chat.StartConversation();
            chat.SetWorkspaceRoot(chipSession.Id, workspace);
            chat.SetApprovalMode(chipSession.Id, ToolApprovalModes.Ask);
            chat.OpenConversation(chipSession.Id);
            System.IO.File.WriteAllText(System.IO.Path.Combine(workspace, "chip-note.txt"), "第一行\n第二行\n");
            var chipClient = new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "file_write",
                Arguments = new Dictionary<string, object?>
                {
                    ["path"] = "chip-note.txt",
                    ["old_string"] = "第二行",
                    ["new_string"] = "第二行（改过）",
                },
            };
            chat.ClientOverride = (_, _) => chipClient;
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            panel.SetInputForCheck("改一行，然后我要看改动");
            _ = panel.SendComposerForCheck();
            var chipCallId = await WaitForPendingCallAsync(chipSession);
            Check(chipCallId is not null, "夹具：那次写入先挂起来等批准，所以它才有一段被冻结的预览");
            chat.TryResolveApproval(chipSession.Id, chipCallId ?? "", approved: true, alwaysAllow: false, out _);
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.RunDiffChipCountForCheck == 1 && panel.RunDiffChipsInsideGroupHeadsForCheck
                  && panel.RunDiffChipTextForCheck(0).Contains("+1", StringComparison.Ordinal),
                "这轮运行在组头上带出一个可点的 +N −M，数字正是刚批准的那段 diff（实际「"
                + panel.RunDiffChipTextForCheck(0) + "」）");
            // The count is read as the tail of the sentence that produced it. Pinned to the head's right edge it
            // became a column of its own, and a column whose left edge moves with every label width is not
            // something the eye learns to scan in a transcript.
            var (headInline, headGapText) = panel.ActivityGroupHeadTrailingGapForCheck(0) is { } headMeasured
                ? (headMeasured.Gap <= 12 && headMeasured.RightSlack > 40,
                   "间隙 " + headMeasured.Gap.ToString("0.#") + "px，右余 "
                   + headMeasured.RightSlack.ToString("0.#") + "px")
                : (false, "量不出来");
            Check(headInline,
                "组头的 +N −M 跟着那句话走，右端留给空白（实际 " + headGapText + "）");
            var chipTokenBefore = panel.ActivityGroupInstanceTokenForCheck(0);
            var chipPressed = panel.ClickRunDiffChipForCheck(0);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            // Bound once, non-null: if the press did not produce a pane, the cells below fail on their own terms
            // rather than on a null dereference, and none of them is a claim about a pane that is not there.
            var chipPane = shell.InspectorPaneForCheck ?? new InspectorPanel();
            Check(chipPressed && shell.InspectorVisibleForCheck
                  && shell.InspectorPaneForCheck is not null
                  && chipPane.ChangesTabVisibleForCheck && !chipPane.RepoTabVisibleForCheck
                  && !chipPane.PlanTabVisibleForCheck
                  && chipPane.ChangeRowCountForCheck == 1
                  && chipPane.FirstChangePathForCheck == "chip-note.txt"
                  && panel.ActivityGroupInstanceTokenForCheck(0) == chipTokenBefore,
                "点组头上那个数字，右边那一列就开到改动页并列出刚动过的文件，而那一折还是同一个控件、没有被这次点击重建"
                + $"（点击 {chipPressed}，显示 {shell.InspectorVisibleForCheck}，行 {chipPane.ChangeRowCountForCheck}，"
                + $"路径 {chipPane.FirstChangePathForCheck}，组 {chipTokenBefore}/{panel.ActivityGroupInstanceTokenForCheck(0)}）");
            // A repaint that folds everything the reader had opened is the same teardown seen from the other side:
            // the row is opened here, before the second run, and has to come back open out of it.
            chipPane.ClickChangeRowForCheck(0);
            Check(chipPane.ChangeRowExpandedForCheck(0)
                  && chipPane.ChangeRowTextForCheck(0).Contains("第二行（改过）", StringComparison.Ordinal),
                "点开改动行才取它的 diff，取到的是批准时冻结的那一段（实际「"
                + chipPane.ChangeRowTextForCheck(0) + "」）");
            // The pane is open while the assistant keeps working, which is exactly when it used to stop telling the
            // truth: it was painted once, on opening, and every write after that landed in a list nobody repainted.
            var chipTwoClient = new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "file_write",
                Arguments = new Dictionary<string, object?>
                {
                    ["path"] = "chip-two.txt",
                    ["old_string"] = "",
                    ["new_string"] = "新建的一行\n",
                },
            };
            chat.ClientOverride = (_, _) => chipTwoClient;
            panel.SetInputForCheck("再新建一个文件");
            _ = panel.SendComposerForCheck();
            var secondCallId = await WaitForPendingCallAsync(chipSession);
            chat.TryResolveApproval(chipSession.Id, secondCallId ?? "", approved: true, alwaysAllow: false, out _);
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(chipPane.ChangeRowCountForCheck == 2
                  && chipPane.FirstChangePathForCheck == "chip-note.txt",
                "开着的改动页跟着这一轮多出来的那次写入一起长，不用重新点开（实际 "
                + chipPane.ChangeRowCountForCheck + " 行）");
            Check(chipPane.ChangeRowExpandedForCheck(0) && chipPane.OpenChangeCountForCheck == 1,
                "重绘之后读者开着的那一行还开着：刷新不该把他翻开的文件折回去");
            // ── the same number in the tier that never asked ──
            // This is the premise the recorded tally exists for. A write only freezes a preview when it parks for a
            // card, so in 自动审批 there is no diff text in the transcript at all — and a count read off previews
            // therefore reports nothing for exactly the runs that never interrupted anybody. The number has to come
            // from the write itself, which is the one moment both sides of it are in memory.
            var silentSession = chat.StartConversation();
            chat.SetWorkspaceRoot(silentSession.Id, workspace);
            chat.SetApprovalMode(silentSession.Id, ToolApprovalModes.Auto);
            chat.OpenConversation(silentSession.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            // Seeded rather than created: an empty anchor against a file that is already there is a refusal by
            // design, and a scratch workspace left over from the last run would silently turn this cell into a
            // fixture about nothing. An edit from three lines to five is the same claim with no such dependency.
            System.IO.File.WriteAllText(System.IO.Path.Combine(workspace, "silent-write.txt"), "甲\n乙\n丙\n");
            chat.ClientOverride = (_, _) => new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "file_write",
                Arguments = new Dictionary<string, object?>
                {
                    ["path"] = "silent-write.txt",
                    ["old_string"] = "甲\n乙\n丙\n",
                    ["new_string"] = "一\n二\n三\n四\n五\n",
                },
            };
            panel.SetInputForCheck("自动审批档改三行为五行，不会有任何卡片");
            _ = panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var silentTurn = silentSession.Messages.Find(turn => turn.ToolName == "file_write");
            // The turn has to be named, not merely "not carrying a preview": a null turn satisfies a null check on
            // its own and would let a fixture that wrote nothing pass as a tier that never asked.
            Check(silentTurn is not null && panel.RunDiffChipCountForCheck == 1
                  && panel.RunDiffChipTextForCheck(0).Contains("+5", StringComparison.Ordinal)
                  && silentTurn.ApprovalPreview is null
                  && silentTurn.ChangeLines is { Added: 5, Removed: 3 },
                "自动审批档那次写入没有卡片、也冻不出预览，组头上照样报出 +5 −3（实际「"
                + panel.RunDiffChipTextForCheck(0) + "」，调用轮 "
                + (silentTurn is null ? "不存在" : "在") + "，预览 "
                + (silentTurn?.ApprovalPreview is null ? "没有" : "有") + "）");
            // ── the band above the composer: this session's own tally, and the way into the list behind it ──
            // One landed write and no question outstanding is the state the plate is meant to be read in, and it is
            // also the state where it sits directly on top of the input box: with nothing owed an answer, the band
            // is the row nearest the composer.
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var pillLine = string.Format(CultureInfo.CurrentCulture, HubStrings.Get("ChatChangesPillFormat"), 1);
            Check(panel.ChangesPillVisibleForCheck
                  && panel.ChangesPillTextForCheck.Contains(pillLine, StringComparison.Ordinal)
                  && panel.ChangesPillTextForCheck.Contains("+5", StringComparison.Ordinal)
                  && panel.ChangesPillTextForCheck.Contains("−3", StringComparison.Ordinal),
                "输入框上方那条胶囊说出这一个文件和那五行增删（实际「" + panel.ChangesPillTextForCheck + "」）");
            Check(panel.ChangesPillBottomForCheck is { } pillBottom && pillBottom <= 0.5,
                "没有待批准问题时，胶囊就是贴在输入框上方的那一条，没有盖到输入区（下沿相对输入框上边 "
                + (panel.ChangesPillBottomForCheck ?? double.NaN).ToString("F1", CultureInfo.InvariantCulture) + "）");
            var pressedPill = panel.ClickChangesPillForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var bandPane = shell.InspectorPaneForCheck ?? new InspectorPanel();
            Check(pressedPill && shell.InspectorVisibleForCheck && bandPane.ChangesTabVisibleForCheck
                  && bandPane.ChangeRowCountForCheck == 1,
                "点胶囊把右边那一列开到改动页，页上的行数就是胶囊说的那个文件数（页 "
                + bandPane.ChangeRowCountForCheck + " 行）");
            // The plate is a thing the eye is sent to on purpose, so it has to be drawn, not merely laid out, and
            // framed with the token that separates from the raised surface — Hub.BorderSubtle is that same grey in
            // the dark variant and would draw no edge at all.
            var pillBorder = panel.ChangesPillBorderForCheck;
            Check(pillBorder is not null
                  && ThemeProbe.IsToken(pillBorder.BorderBrush, "Hub.Border")
                  && pillBorder.CornerRadius.TopLeft > 0,
                "胶囊描的是 Hub.Border 的圆角框，不是与抬起面同色的 BorderSubtle（圆角 "
                + (pillBorder?.CornerRadius.TopLeft ?? 0) + "）");
            // ── one response asking for six writes: each number belongs to its own call ──
            // The tally is handed from the tool to the turn through a queue keyed by conversation, so a batch that
            // lands several writes in one response is exactly the shape that could pair them wrongly. Each fixture
            // edit moves a different number of lines, which is what makes a mispairing observable at all.
            var bandSession = chat.StartConversation();
            chat.SetWorkspaceRoot(bandSession.Id, workspace);
            chat.SetApprovalMode(bandSession.Id, ToolApprovalModes.Full);
            chat.OpenConversation(bandSession.Id);
            var bandClient = new ApprovalChatClient { CallsRemaining = 0 };
            var bandBatch = new List<(string, Dictionary<string, object?>)>();
            for (var file = 0; file < 6; file++)
            {
                var name = $"band-{file + 1}.txt";
                System.IO.File.WriteAllText(System.IO.Path.Combine(workspace, name), "旧一\n旧二\n旧三\n");
                bandBatch.Add(("file_write", new Dictionary<string, object?>
                {
                    ["path"] = name,
                    ["old_string"] = "旧一\n旧二\n旧三\n",
                    ["new_string"] = string.Concat(Enumerable.Range(0, file + 2)
                        .Select(line => $"新{line + 1}\n")),
                }));
            }
            bandClient.Batch = bandBatch;
            chat.ClientOverride = (_, _) => bandClient;
            panel.SetInputForCheck("一次响应里改六个文件，每个改的行数都不一样");
            _ = panel.SendComposerForCheck();
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var bandPairs = bandSession.Messages
                .Where(turn => turn.ToolName == "file_write" && turn.ChangeLines is not null)
                .Select(turn => (turn.ChangeLines!.Added, turn.ChangeLines.Removed))
                .ToList();
            Check(bandPairs.Count == 6
                  && bandPairs.Select(pair => pair.Added).SequenceEqual(Enumerable.Range(2, 6))
                  && bandPairs.All(pair => pair.Removed == 3),
                "六个写入各归各的调用，增删行数按各自那次编辑落下（实际 "
                + string.Join(" / ", bandPairs.Select(pair => "+" + pair.Added + " −" + pair.Removed)) + "）");
            Check(panel.ChangesPillTextForCheck.Contains(
                      string.Format(CultureInfo.CurrentCulture, HubStrings.Get("ChatChangesPillFormat"), 6),
                      StringComparison.Ordinal)
                  && panel.ChangesPillTextForCheck.Contains("+27", StringComparison.Ordinal)
                  && panel.ChangesPillTextForCheck.Contains("−18", StringComparison.Ordinal),
                "胶囊说的是这六个文件的总数（实际「" + panel.ChangesPillTextForCheck + "」）");
            bandPane = shell.InspectorPaneForCheck ?? bandPane;
            Check(bandPane.ChangeRowCountForCheck == 6,
                "胶囊报的文件数就是点开后改动页的行数（页 " + bandPane.ChangeRowCountForCheck + " 行）");
            // ── the two plates share one row, and scrolling does not move the box ──
            // The band's height is reserved by whether the transcript <i>can</i> scroll rather than by where the
            // reading position stopped. Without that, the way back down would lift the composer every time the
            // bottom edge was crossed — the jump being the reason the plate does not float over the transcript any
            // more, so the check has to measure the box, not the plate.
            panel.SetActivityGroupExpandedForCheck(0, true);
            for (var row = 0; row < panel.ActivityRowsForCheck.Length; row++) panel.SetActivityRowExpandedForCheck(row, true);
            // Six edits are not reliably one screen of transcript, and a fixture that quietly did not overflow would
            // make every claim below about the plate vacuous — it would pass on a transcript that never needed one.
            // Notice rows will not do it either: they live below the flow, outside the scroller, so they never grow
            // its extent. Bubbles are the only thing that does.
            //
            // It is the *range* that has to be big enough, not merely non-zero. A reading position is set in pixels
            // and the scroller clamps it to the range, so on a host whose paragraphs happen to be shorter the whole
            // travel was 37.5 px: the scroll below asked for more than that, landed back at the bottom, and the
            // plate that is the subject of this cell never rose — reported as a layout fault when it was the
            // fixture that had nothing to scroll through. So the pad loop runs until the travel clears the scroll
            // below several times over, and the scroll itself asks for the middle of that travel.
            for (var pad = 0; pad < 40 && panel.TranscriptScrollRangeForCheck < 160; pad++)
            {
                bandSession.Messages.Add(new ChatTurn(ChatRoles.Assistant,
                    "第 " + (pad + 1) + " 条回答。这一段只为把转录推过一屏而存在：回底板的板子中不中，要看的是"
                    + "转录到底超出没超出视口，而不是这个夹具碰巧长到了多高。", DateTimeOffset.UnixEpoch.AddMinutes(pad)));
                panel.Reload();
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            // ── the reading column stops where the reader's eye does, not where the scroller does ──
            // The transcript's own bar is opaque, 10px, and painted over the content it belongs to, so a column
            // laid out to the scroller's whole width keeps its right edge behind the track — the user pill's tail
            // (the row asserted above to hug the column's right edge) and a tool row's trailing chip are what the
            // reader loses. This fixture is the one that can see it: the inspector is open, which is what presses
            // the column down to a width the viewport clamps, and the padded transcript is what puts a bar on
            // screen at all. Both halves are asserted, because either one missing makes the claim vacuous.
            var transcriptBarWidth = panel.TranscriptScrollBarWidthForCheck;
            var transcriptViewport = panel.MessageScrollerViewportWidthForCheck;
            var columnRight = panel.MessageFlowRightInScrollerForCheck;
            var pillRight = panel.UserPillRightInScrollerForCheck(0);
            Check(transcriptBarWidth > 0
                  && panel.MessageFlowWidthForCheck < HubMetrics.ColumnMaxWidth
                  && columnRight > 0 && columnRight <= transcriptViewport - transcriptBarWidth + 0.5
                  && pillRight > 0 && pillRight <= transcriptViewport - transcriptBarWidth + 0.5,
                "转录的滚动条压的是留白而不是消息列：列右沿与用户气泡右沿都停在滚动条左侧（视口 "
                + Fmt(transcriptViewport) + "，条宽 " + Fmt(transcriptBarWidth)
                + "，列宽 " + Fmt(panel.MessageFlowWidthForCheck)
                + "，列右沿 " + Fmt(columnRight) + "，气泡右沿 " + Fmt(pillRight) + "）");
            // The composer is the same column, so the same gutter has to sit under both: drop it from one side and
            // the box ends one bar-width right of the text it sits under.
            Check(panel.ComposerFrameRightForCheck > 0
                  && Math.Abs(panel.ComposerFrameRightForCheck - panel.MessageColumnRightForCheck) <= 1,
                "作曲栏与转录共用同一条留白：输入框右沿与消息列右沿是同一条线（输入框 "
                + Fmt(panel.ComposerFrameRightForCheck) + "，消息列 "
                + Fmt(panel.MessageColumnRightForCheck) + "）");
            panel.ClickScrollToBottomForCheck();
            Relayout(shell, panel, measure: true);
            var frameAtBottom = panel.ComposerFrameTopForCheck;
            var bandAtBottom = panel.ChangesPillRowHeightForCheck;
            Check(panel.TranscriptScrollableForCheck && panel.ViewAtBottomForCheck
                  && !panel.ScrollToBottomVisible && panel.ChangesPillVisibleForCheck,
                "夹具：这六个文件摊开之后转录确实超出一屏，停在最底时横排里只有胶囊（组 "
                + panel.ActivityGroupCountForCheck + "，行 " + panel.ActivityRowsForCheck.Length
                + "，可滚 " + panel.TranscriptScrollableForCheck + "）");
            panel.ScrollTranscriptForCheck(panel.TranscriptScrollRangeForCheck / 2);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var plates = panel.BandPlatesForCheck;
            Check(!panel.ViewAtBottomForCheck && panel.ScrollToBottomVisible && plates is not null,
                "往上读到中间，回底板升起来了，两个板子同时在这条横排里（offset "
                + Fmt(panel.FlowOffsetForCheck) + " / 可滚 " + Fmt(panel.TranscriptScrollRangeForCheck) + "，"
                + (plates is null ? "只有一个板子" : "两个都在") + "）");
            Check(Math.Abs(panel.ComposerFrameTopForCheck - frameAtBottom) <= 0.5
                  && Math.Abs(panel.ChangesPillRowHeightForCheck - bandAtBottom) <= 0.5,
                "回底板升起来没有把输入框推走，也没有把横排撑高：滚动位置变了，输入框上沿还在 "
                + frameAtBottom.ToString("F1", CultureInfo.InvariantCulture) + "（现在 "
                + panel.ComposerFrameTopForCheck.ToString("F1", CultureInfo.InvariantCulture) + "），行高还在 "
                + bandAtBottom.ToString("F1", CultureInfo.InvariantCulture) + "（现在 "
                + panel.ChangesPillRowHeightForCheck.ToString("F1", CultureInfo.InvariantCulture) + "）");
            Check(plates is { } pair2 && Math.Abs(pair2.PillCenterY - pair2.PlateCenterY) <= 0.5
                  && pair2.Gap >= 6 && pair2.Gap <= 12
                  && Math.Abs(pair2.BandCenterX - pair2.FrameCenterX) <= 1.5,
                "两个板子中心线一致、中间隔着那 8 个像素，整组与输入框同轴居中（中心线差 "
                + (plates is { } a ? (a.PillCenterY - a.PlateCenterY).ToString("F1", CultureInfo.InvariantCulture) : "?")
                + "，间隙 " + (plates is { } b ? b.Gap.ToString("F1", CultureInfo.InvariantCulture) : "?")
                + "，整组中心与列中心差 "
                + (plates is { } c ? (c.BandCenterX - c.FrameCenterX).ToString("F1", CultureInfo.InvariantCulture) : "?")
                + "）");
            // ── a question and the tally up at the same time: which one is nearest the box ──
            // The band was put above the decision host rather than below it for this one cell, and the existing
            // placement assertion cannot see the difference on its own: with the band hidden the card is flush with
            // the frame, and with it shown the card is still the nearest thing — a check that only measured the
            // card would stay green while the band sat underneath it, covering nothing but outranking a question.
            var rankedSession = chat.StartConversation();
            chat.SetWorkspaceRoot(rankedSession.Id, workspace);
            chat.SetApprovalMode(rankedSession.Id, ToolApprovalModes.Ask);
            chat.OpenConversation(rankedSession.Id);
            System.IO.File.WriteAllText(System.IO.Path.Combine(workspace, "ranked.txt"), "甲\n乙\n");
            var rankedFirst = new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "file_write",
                Arguments = new Dictionary<string, object?>
                {
                    ["path"] = "ranked.txt", ["old_string"] = "乙", ["new_string"] = "乙\n丙",
                },
            };
            chat.ClientOverride = (_, _) => rankedFirst;
            panel.SetInputForCheck("先改一行，这条会挂起来等批准");
            _ = panel.SendComposerForCheck();
            var rankedCall = await WaitForPendingCallAsync(rankedSession);
            chat.TryResolveApproval(rankedSession.Id, rankedCall ?? "", approved: true, alwaysAllow: false, out _);
            await panel.WaitForRunToFinishForCheck();
            // A second write, left unanswered: the card is up and the first file has already landed.
            var rankedSecond = new ApprovalChatClient
            {
                CallsRemaining = 1,
                ToolName = "file_write",
                Arguments = new Dictionary<string, object?>
                {
                    ["path"] = "ranked.txt", ["old_string"] = "乙\n丙", ["new_string"] = "乙\n丙\n丁",
                },
            };
            chat.ClientOverride = (_, _) => rankedSecond;
            panel.SetInputForCheck("再改一行，这条先挂着");
            _ = panel.SendComposerForCheck();
            var rankedPending = await WaitForPendingCallAsync(rankedSession);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var rankedPillBottom = panel.ChangesPillBottomForCheck;
            var rankedCardBottom = panel.DecisionHostPlacementForCheck;
            Check(panel.ChangesPillVisibleForCheck && rankedPending is not null
                  && rankedCardBottom is { } card && card.BottomRelativeToFrame <= 0.5
                  && rankedPillBottom is { } pill && pill < card.BottomRelativeToFrame - 1,
                "有待批准问题时，问题卡才是贴着输入框的那一行，胶囊排在它上面（卡片下沿 "
                + (rankedCardBottom?.BottomRelativeToFrame.ToString("F1", CultureInfo.InvariantCulture) ?? "?")
                + "，胶囊下沿 " + (rankedPillBottom ?? double.NaN).ToString("F1", CultureInfo.InvariantCulture)
                + $"，显示 {panel.ChangesPillVisibleForCheck}，等待 {rankedPending}，文件 {ChatChanges.Of(rankedSession).Count}）");
            chat.DeleteConversation(rankedSession.Id);
            // A row that fits is the other half of the same claim: nothing is owed a row that holds nothing.
            var bareSession = chat.StartConversation();
            chat.OpenConversation(bareSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.ChangesPillVisibleForCheck && !panel.ScrollToBottomVisible
                  && panel.ChangesPillRowHeightForCheck <= 0.5,
                "一个还没改过文件、也装得下的会话不占输入框上方的一行（行高 "
                + panel.ChangesPillRowHeightForCheck.ToString("F1", CultureInfo.InvariantCulture) + "）");
            chat.DeleteConversation(bareSession.Id);
            chat.DeleteConversation(bandSession.Id);
            chat.DeleteConversation(silentSession.Id);
            chat.DeleteConversation(chipSession.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            chat.DeleteConversation(readBack.Id);
            chat.DeleteConversation(liveSession.Id);
        }
        finally
        {
            livePark.TrySetResult(true);
            answerPark.TrySetResult(true);
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckInspectorAndSteerAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;

        // ── the inspector column: a real third column that belongs to the assistant page ──
        shell.OpenAssistant();
        shell.NavigateTo("Assistant");
        Dispatcher.UIThread.RunJobs();
        Check(shell.OpenInspector("plan", -1) && shell.InspectorVisibleForCheck
              && shell.InspectorWidthForCheck is >= 300 and <= 520,
            "打开审阅面板：它是聊天右边真实的一列，宽度落在可读区间里（实际 "
            + shell.InspectorWidthForCheck + "）");
        shell.SetInspectorWidthForCheck(360);
        shell.DragInspectorForCheck(-120);
        Check(shell.InspectorVisibleForCheck && shell.InspectorWidthForCheck == 480
              && shell.InspectorPreferenceWidthForCheck == 480,
            "向左拖动右栏手柄正常放宽，并保存宽度（实际 " + shell.InspectorWidthForCheck + "）");
        shell.OpenInspector("changes", -1);
        shell.DragInspectorForCheck(281);
        Check(!shell.InspectorVisibleForCheck && !shell.InspectorGripVisibleForCheck
              && !shell.InspectorPreferenceOpenForCheck
              && shell.InspectorPreferenceWidthForCheck == 480,
            "继续向右拖过收起阈值：右栏关闭、宽度偏好仍保留（实际 "
            + shell.InspectorWidthForCheck + "）");
        shell.ClickInspectorToggleForCheck();
        Check(shell.InspectorVisibleForCheck && shell.InspectorWidthForCheck == 480
              && shell.InspectorTabForCheck == "changes",
            "标题栏重新打开右栏时恢复原宽度和当前页签（宽度 "
            + shell.InspectorWidthForCheck + "，页签「" + shell.InspectorTabForCheck + "」）");
        shell.NavigateTo("Projects");
        Check(!shell.InspectorVisibleForCheck, "离开助手页，审阅面板跟着收起：它是助手的，不是每页的");
        shell.NavigateTo("Assistant");
        Check(shell.InspectorVisibleForCheck, "回到助手页，之前打开的面板还在");
        shell.CloseInspector();
        Check(!shell.InspectorVisibleForCheck, "关掉之后那一列真的让位给聊天，而不是留一条空白");

        // ── a column that came back from the last run ──
        // The open flag is persisted and the pane is not, so a restart is the one moment the column is on screen
        // before anybody asked it to be — and every way out of it (the header, the three tabs, the ×) lives
        // inside the pane. Built the way the app builds it: the settings file a previous run left behind, read
        // back through the same store, handed to a fresh shell on its own scratch root so nothing here can
        // disturb the language/theme the settings group reads back.
        var restoreRoot = ScratchDirectory.Resolve("inspector-restore");
        System.IO.Directory.CreateDirectory(restoreRoot);
        var restoreStore = new PreferencesStore(PreferencesPathFor(restoreRoot));
        restoreStore.Save(new HubPreferences { InspectorOpen = true, InspectorWidth = 420 });
        var restored = new MainWindow(restoreRoot, restoreStore, restoreStore.Load());
        restored.Show();
        restored.NavigateTo("Assistant");
        restored.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var restoredPane = restored.InspectorPaneForCheck;
        Check(restored.InspectorVisibleForCheck && restored.InspectorWidthForCheck == 420
              && restoredPane is not null
              && restoredPane.PlanTabVisibleForCheck && !restoredPane.ChangesTabVisibleForCheck
              && !restoredPane.RepoTabVisibleForCheck,
            "重启后那一列自己把面板建了回来：三页页签都在，宽度还是存的那个，不是一列没法关的空白");
        Check(restoredPane?.PlanNoticeForCheck == HubStrings.Get("InspectorPlanEmpty"),
            "这一会话没有计划时那一列说「还没有生成计划」，而不是什么都不说（实际「"
            + (restoredPane?.PlanNoticeForCheck ?? "(没有面板)") + "」）");
        var workspaceContent = new TextBlock { Text = "source fixture" };
        restored.ShowCodeWorkspace("src/main.cpp", workspaceContent);
        restored.UpdateLayout();
        Check(restored.CodeWorkspaceVisibleForCheck
              && restored.CodeWorkspaceTitleForCheck == "src/main.cpp"
              && ReferenceEquals(restored.CodeWorkspaceContentForCheck, workspaceContent),
            "文件与 Diff 共用 shell 的代码工作区图层，而不是另开窗口");
        restored.CloseCodeWorkspace(discard: true);
        Check(!restored.CodeWorkspaceVisibleForCheck, "关闭代码工作区只收起该图层，不关闭 shell");
        restored.ClickInspectorToggleForCheck();
        restored.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!restored.InspectorVisibleForCheck
              && new PreferencesStore(PreferencesPathFor(restoreRoot)).Load().InspectorOpen == false,
            "点标题栏那枚抽屉按钮：列真的让位给聊天，设置里也记成关着，而不是下次启动又回来");
        restored.Close();

        // ── the header tabs, pressed by a reader rather than by the shell ──
        // Every repository test used to walk in through OpenInspector, which is precisely how "the column shows
        // the plan when you clicked 仓库" stayed green: the shell already knew the tab it had just been asked
        // for. These drive the pane's own header instead.
        var tabSession = chat.StartConversation();
        chat.OpenConversation(tabSession.Id);
        panel.Reload();
        shell.OpenInspector("plan", -1);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var tabPane = shell.InspectorPaneForCheck;
        tabPane?.ClickChangesTabForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.InspectorTabForCheck == "changes" && tabPane is { } afterChanges
              && afterChanges.ChangesTabVisibleForCheck && !afterChanges.PlanTabVisibleForCheck
              && afterChanges.SelectedTabKeyForCheck == "changes",
            "点页眉的「改动」：外壳跟着改到改动页，页眉的下划线也移过去了（外壳记的是「"
            + shell.InspectorTabForCheck + "」，页眉标的是「"
            + (shell.InspectorPaneForCheck?.SelectedTabKeyForCheck ?? "") + "」）");
        // The transcript moves underneath them, which is the whole reason the column repaints at all.
        chat.SeedTurnForCheck(tabSession.Id, "又一句");
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.InspectorPaneForCheck is { } afterPaint && afterPaint.ChangesTabVisibleForCheck
              && !afterPaint.PlanTabVisibleForCheck && shell.InspectorTabForCheck == "changes",
            "转录又长一截之后重画，那一列还停在改动页，不会把人弹回他两分钟前就走开的计划页");
        // The reader's actual complaint: 仓库 showed the plan, because the read that landed repainted the column
        // on the tab the shell still remembered. Entering it by click must ask for its one read *and* still be
        // on it when the answer comes back.
        var repoTabReads = chat.RepositoryReadsForCheck;
        shell.InspectorPaneForCheck?.ClickRepoTabForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var repoTabLanded = false;
        await WaitUntilAsync(() =>
        {
            repoTabLanded = shell.InspectorPaneForCheck?.RepoNoticeForCheck
                .Contains(HubStrings.Get("InspectorRepoNoWorkspace"), StringComparison.Ordinal) == true;
            return repoTabLanded;
        });
        Check(chat.RepositoryReadsForCheck == repoTabReads + 1 && repoTabLanded
              && shell.InspectorPaneForCheck is { } afterRepo && afterRepo.RepoTabVisibleForCheck
              && !afterRepo.PlanTabVisibleForCheck && shell.InspectorTabForCheck == "repo",
            "页眉点「仓库」递出那一次读取，读落回面板时人仍站在仓库页（两次之间读了 "
            + (chat.RepositoryReadsForCheck - repoTabReads) + " 次）");
        var quietReads = chat.RepositoryReadsForCheck;
        shell.InspectorPaneForCheck?.ClickPlanTabForCheck();
        shell.InspectorPaneForCheck?.ClickChangesTabForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(chat.RepositoryReadsForCheck == quietReads && shell.InspectorTabForCheck == "changes"
              && ReferenceEquals(shell.InspectorPaneForCheck, tabPane),
            "计划与改动两页一次 git 都不问，换页也不重造面板（外壳记的是「"
            + shell.InspectorTabForCheck + "」）");
        // The plan tab is the renderer's audience outside ChatPanel, and it is where a style written only in
        // ChatPanel.axaml went unnoticed: the copy button carries no Content — its icon is entirely the
        // template's — so without the style it fell back to the base Button theme and painted an empty plate
        // behind a working tooltip. Drive the pane through its own repaint path, install the toolbar the way
        // the renderer does, then read the icon back off the template.
        var iconPane = shell.InspectorPaneForCheck;
        iconPane?.Reload(
            "# Plan\n\n```cpp\nint main() {}\n```",
            [],
            static _ => ("", default),
            null,
            "plan",
            // The pane is fed the same 引用 target production feeds it, because what the pane's own right-click
            // menu offers is decided here rather than inside the pane: a check that left this out would be asking
            // the pane for a menu it was never given a quote target for.
            text => panel.QuoteIntoComposer(text));
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        iconPane?.ApplyPlanSyntaxHighlightingForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(iconPane is { } paintedPlan && paintedPlan.PlanCopyIconForCheck,
            "计划页代码块的复制按钮画出图标：复制图标跟渲染器走，不是只写在聊天面板作用域里的样式");
        Check(iconPane is { } copyPlacement && copyPlacement.PlanCopyButtonInHeaderForCheck
              && copyPlacement.PlanCopyButtonVisibleForCheck,
            "计划 Markdown 复制按钮位于标签栏右侧，计划页选中时可见");
        iconPane?.ClickChangesTabForCheck();
        Check(iconPane is { } hiddenCopy && !hiddenCopy.PlanCopyButtonVisibleForCheck,
            "切换到改动页时隐藏计划 Markdown 复制按钮");
        iconPane?.ClickPlanTabForCheck();
        Check(iconPane is { } menuPlan && menuPlan.PlanSelectionMenuForCheck,
            "计划页的选区右键菜单同样可用（复制/引用）：两个受众都要有");
        shell.CloseInspector();
        chat.DeleteConversation(tabSession.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();

        // ── the title bar's two right-hand buttons ──
        // The column's way in and out has to exist whether or not a pane does, which is the difference between a
        // drawer and the dead end a restored column used to be. It belongs to the assistant page rather than to
        // the column: on another page there is nothing for it to show — and nothing for the arrow beside it to
        // lift, which is the other half of that rule.
        shell.OpenInspector("plan", -1);
        shell.NavigateTo("Projects");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.InspectorToggleVisibleForCheck && !shell.InspectorExpandToggleVisibleForCheck,
            "审阅面板的两枚按钮都只在助手页出现：别的页上没有可开可关的东西，也没有可撑开的东西");
        shell.NavigateTo("Assistant");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.InspectorToggleVisibleForCheck && shell.InspectorExpandToggleVisibleForCheck
              && shell.InspectorVisibleForCheck
              && shell.InspectorToggleTipForCheck == HubTexts.Get("InspectorToggleTip", HubStrings.Language)
              && shell.InspectorToggleTipForCheck.Length > 0,
            "回到助手页两枚又在了，而抽屉那枚只有图标，确实带着文案表里那句提示（实际「"
            + shell.InspectorToggleTipForCheck + "」）");

        var barHeight = shell.TitleBarBoundsForCheck.Height;

        shell.InspectorPaneForCheck?.ClickChangesTabForCheck();
        shell.ClickInspectorToggleForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.InspectorVisibleForCheck && !shell.InspectorGripVisibleForCheck
              && !shell.InspectorExpandedForCheck && !shell.InspectorOverlayVisibleForCheck
              && !shell.InspectorExpandToggleVisibleForCheck,
            "一按收起：那一列连同它自己的拖拽手柄一起让位给聊天，靠它撑开的那枚也跟着收掉（列 "
            + shell.InspectorVisibleForCheck + "、手柄 " + shell.InspectorGripVisibleForCheck
            + "、撑开 " + shell.InspectorExpandedForCheck + "、覆盖层 "
            + shell.InspectorOverlayVisibleForCheck + "、撑开按钮 "
            + shell.InspectorExpandToggleVisibleForCheck + "）");

        shell.ClickInspectorToggleForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.InspectorVisibleForCheck && shell.InspectorTabForCheck == "changes"
              && shell.InspectorPaneForCheck is { } reopened
              && reopened.ChangesTabVisibleForCheck && !reopened.PlanTabVisibleForCheck,
            "再按展开：回到读者上次停的那一页，而不是从头塞给他一个计划（外壳记的是「"
            + shell.InspectorTabForCheck + "」）");
        var barBounds = shell.TitleBarBoundsForCheck;
        Check(Math.Abs(barBounds.Height - 32) <= 0.5,
            "抽屉控制仍位于 32 DIP 高的标题栏中");
        shell.CloseInspector();
        Dispatcher.UIThread.RunJobs();

        // ── the same pane, lifted out of its column ──
        // A width that is not the default, so the round trip below measures something: a restore that reset the
        // column would otherwise pass on two copies of 360.
        shell.OpenInspector("plan", -1);
        shell.SetInspectorWidthForCheck(480);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        shell.ClickInspectorExpandToggleForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var overlay = shell.InspectorOverlayBoundsForCheck;
        var client = shell.ClientBoundsForCheck;
        Check(shell.InspectorExpandedForCheck
              && shell.InspectorOverlayVisibleForCheck
              && shell.InspectorOverlayOrderForCheck
              && !shell.InspectorVisibleForCheck
              && overlay.Contains(shell.SidebarBoundsForCheck)
              && overlay.Contains(shell.PageBoundsForCheck)
              && !overlay.Intersects(shell.TitleBarBoundsForCheck)
              && overlay.Width >= client.Width - 1
              && overlay.Height >= client.Height - barHeight - 1,
            "撑开审阅面板：侧栏和聊天列都在它下面，标题栏却一点没被盖上——面板从栏下面开始，回程那枚按钮就长在那儿（覆盖 "
            + Fmt(overlay.Width) + "×" + Fmt(overlay.Height) + " @ y=" + Fmt(overlay.Y) + "，客户区 "
            + Fmt(client.Width) + "×" + Fmt(client.Height) + "）");
        var expandedPane = shell.InspectorPaneForCheck;
        Check(expandedPane is not null
              && expandedPane.ExpandedForCheck
              && expandedPane.HeaderButtonCountForCheck == 4
              && expandedPane.PlanHostMaxWidthForCheck == 820,
            "撑开后计划页收到聊天列的行长而不是铺满整屏，面板头上只有三个页签和计划页复制按钮（正文列宽上限 "
            + Fmt(expandedPane?.PlanHostMaxWidthForCheck ?? 0) + "，头上按钮 "
            + (expandedPane?.HeaderButtonCountForCheck ?? -1) + " 枚）");
        Check(!shell.InspectorExpandToggleVisibleForCheck
              && shell.InspectorToggleVisibleForCheck
              && shell.InspectorDrawerGlyphIsForCheck("Hub.Icon.Restore")
              && shell.InspectorToggleTipForCheck == HubTexts.Get("InspectorRestore", HubStrings.Language),
            "撑开后抽屉按钮切换为还原操作并保持可见（提示「"
            + shell.InspectorToggleTipForCheck + "」）");
        shell.PressInspectorKeyForCheck(Key.Escape);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.InspectorExpandedForCheck
              && !shell.InspectorOverlayVisibleForCheck
              && shell.InspectorVisibleForCheck
              && shell.InspectorWidthForCheck == 480
              && shell.InspectorExpandToggleVisibleForCheck
              && shell.InspectorDrawerGlyphIsForCheck("Hub.Icon.PanelRight")
              && shell.InspectorToggleTipForCheck == HubTexts.Get("InspectorToggleTip", HubStrings.Language)
              && shell.InspectorPaneForCheck?.PlanTabVisibleForCheck == true,
            "Esc 把面板收回右边那一列：抽屉换回面板图标、话也说「显示或隐藏」，撑开那枚又回来了，列宽还是撑开前拖到的 480，计划页也还在（实际 "
            + Fmt(shell.InspectorWidthForCheck) + "）");

        // The drawer's restore face, driven through the button's own Click rather than through the method behind
        // it: what broke here was the wiring — the lifted pane keeps _inspectorOpen set, so an open-first branch
        // read "press the restore face" as "shut the column". Esc covered the restore and stayed green while the
        // click closed everything, so the button itself is what this cell has to press. End state = column open,
        // not lifted, same as before this block, so the sequence below still starts from a liftable pane.
        shell.ClickInspectorExpandToggleForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        shell.ClickInspectorToggleForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.InspectorExpandedForCheck
              && !shell.InspectorOverlayVisibleForCheck
              && shell.InspectorVisibleForCheck
              && shell.InspectorWidthForCheck == 480
              && shell.InspectorExpandToggleVisibleForCheck
              && shell.InspectorDrawerGlyphIsForCheck("Hub.Icon.PanelRight")
              && shell.InspectorToggleTipForCheck == HubTexts.Get("InspectorToggleTip", HubStrings.Language),
            "点抽屉那枚「还原」把面板收回右边那一列：列宽还是撑开前拖到的 480、撑开按钮又回来了，而不是把整列关掉（实际 "
            + "撑开 " + shell.InspectorExpandedForCheck
            + "、覆盖层 " + shell.InspectorOverlayVisibleForCheck
            + "、列 " + shell.InspectorVisibleForCheck
            + "、列宽 " + Fmt(shell.InspectorWidthForCheck)
            + "、撑开按钮 " + shell.InspectorExpandToggleVisibleForCheck
            + "、提示「" + shell.InspectorToggleTipForCheck + "」）");

        // The two ways out that are not the arrow: leaving the page it belongs to, and closing it. Either one
        // leaving a lifted pane behind would cover a page that cannot produce one.
        shell.ClickInspectorExpandToggleForCheck();
        shell.NavigateTo("Projects");
        Check(!shell.InspectorExpandedForCheck && !shell.InspectorOverlayVisibleForCheck,
            "离开助手页时撑开状态一起没有：不会留下一整片盖住别的页面");
        shell.NavigateTo("Assistant");
        shell.OpenInspector("plan", -1);
        shell.ClickInspectorExpandToggleForCheck();
        shell.CloseInspector();
        Check(!shell.InspectorExpandedForCheck && !shell.InspectorOverlayVisibleForCheck
              && !shell.InspectorVisibleForCheck,
            "关掉审阅面板时撑开状态一起没有，而不是留下一整片没有列可回的面板");

        // ── the inspector content, fed a fixture snapshot directly ──
        var inspector = new InspectorPanel();
        var changes = new[]
        {
            new ChangedFile("note.txt", ChangeVerdict.Edited, "call-1", 3,
                "@@ -1,2 +1,2 @@\n 第一行\n-第二行\n+第二行（已改）", null, null, "Edited note.txt", true)
            {
                ChangeId = "revision-2",
                History =
                [
                    new ChatFileRevision("revision-1", "session", "/workspace/note.txt", "/workspace",
                        DateTimeOffset.UnixEpoch, "before-1", "after-1", ChatChangeStore.Complete),
                    new ChatFileRevision("revision-2", "session", "/workspace/note.txt", "/workspace",
                        DateTimeOffset.UnixEpoch.AddSeconds(1), "before-2", "after-2", ChatChangeStore.Complete),
                ],
            },
        };
        inspector.Reload("# 计划\n\n- 先看实现\n- 再动手", changes,
            change => (change.SelectedRevisionId == "revision-1"
                ? "@@ -1,2 +1,2 @@\n 第一行\n-第二行\n+第二行（旧版）"
                : "@@ -1,2 +1,2 @@\n 第一行\n-第二行\n+第二行（已改）", UndoCopyState.Read), null, "changes");
        Check(inspector.ChangeRowCountForCheck == 1 && inspector.FirstChangePathForCheck == "note.txt"
              && inspector.ChangesTabVisibleForCheck && !inspector.PlanTabVisibleForCheck,
            "改动页把这次会话动过的文件列出来，并停在被点开的哪一页（实际 "
            + inspector.ChangeRowCountForCheck + " 行，首行「" + inspector.FirstChangePathForCheck + "」）");
        Check(inspector.ChangeFileMenuItemsForCheck.SequenceEqual([
                HubStrings.Get("InspectorViewFile"), HubStrings.Get("InspectorEditFile")]),
            "会话改动文件行提供明确的 View file / Edit file 菜单入口");
        inspector.ClickChangeRowForCheck(0);
        Check(inspector.DurableDiffControlsForCheck,
            "改动详情提供可切换的历史修订和可滚动的差异文本区域");
        Check(inspector.ChangeRowDiffGutterCountForCheck(0) >= 3
              && inspector.ChangeRowDiffMarkersForCheck(0).Contains("+", StringComparison.Ordinal)
              && inspector.ChangeRowDiffMarkersForCheck(0).Contains("−", StringComparison.Ordinal),
            "unified diff 为上下文与改动行提供行号槽和明确的增删标记（实际 "
            + inspector.ChangeRowDiffGutterCountForCheck(0) + " 行号、标记「"
            + inspector.ChangeRowDiffMarkersForCheck(0) + "」）");
        // The gear is the page's only setting surface now: the diff layout and the change scope are the same kind
        // of question, so they are four rows in one menu, and the tick — not a label in the header — says which of
        // each is up. Read in order, so a row that moved into the wrong group is caught as well as a missing one.
        inspector.PressViewSettingsForCheck();
        Check(inspector.ViewMenuRowsForCheck.SequenceEqual([
                "DiffViewSection", "InspectorMenuSplit", "InspectorMenuUnified ✓",
                "InspectorScopeSection", "InspectorMenuSession ✓", "InspectorMenuRepository"]),
            "改动页齿轮菜单按「视图、改动范围」两组排出并排/统一与会话/仓库，勾落在统一视图与会话改动上（实际 "
            + string.Join("、", inspector.ViewMenuRowsForCheck) + "）");
        inspector.ClickViewMenuRowForCheck("InspectorMenuSplit");
        Check(inspector.SplitDiffForCheck
              && inspector.ChangeRowDiffMarkersForCheck(0).Contains("+", StringComparison.Ordinal)
              && inspector.ChangeRowDiffMarkersForCheck(0).Contains("−", StringComparison.Ordinal)
              && inspector.ChangeRowDiffGutterCountForCheck(0) > 0,
            "选并排视图后，已经打开的那份 diff 当场改成左右两栏（实际 "
            + inspector.ChangeRowDiffGutterCountForCheck(0) + " 行号）");
        inspector.ClickViewMenuRowForCheck("InspectorMenuUnified");
        Check(!inspector.SplitDiffForCheck && inspector.ChangeRowDiffGutterCountForCheck(0) >= 3,
            "选统一视图后回到单栏，行号槽与标记照旧");
        inspector.SelectHistoryRevisionForCheck(0);
        Check(inspector.SelectedRevisionForCheck("note.txt") == "revision-1",
            "切换历史修订会将详情切到选中的那次写入");
        inspector.Reload("# 计划\n\n- 先看实现", changes,
            _ => ((string?)null, UndoCopyState.Read), null, "plan");
        Check(inspector.PlanTabVisibleForCheck && !inspector.ChangesTabVisibleForCheck
              && !inspector.RepoTabVisibleForCheck,
            "三页各显示各的，切到计划页不会把改动页或仓库页也带出来");

        // ── the repository tab, fed a snapshot the same shape the reader produces ──
        // The three states this tab can be in are three different claims, so each is asserted on its own words:
        // the caveat that the list is the repository's rather than the session's, the per-file row with git's own
        // two letters, and the mark that says a path is in <i>both</i> lists. Fixture-driven first — the pane must
        // be readable without a git anywhere near the check — and the live read that proves the snapshot shape is
        // asserted headlessly in --check-ai-workspace, where the real repository lives.
        var repoState = new GitRepositoryState(GitReadOutcome.Ok, @"D:\dev\probe", "master", 2, 1,
            [
                new GitStatusEntry("src/main.cpp", "M", " ", false),
                new GitStatusEntry("note.txt", " ", "M", false),
                new GitStatusEntry("assets/logo.png", "?", "?", true),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/main.cpp"] = "diff --git a/src/main.cpp b/src/main.cpp\n@@ -1 +1 @@\n-old\n+new",
            }, false, null);
        var repoReads = 0;
        inspector.RepoRefreshRequested += () => repoReads++;
        inspector.Reload(null, changes, _ => ((string?)null, UndoCopyState.Read), repoState, "repo");
        Check(inspector.RepoTabVisibleForCheck && !inspector.ChangesTabVisibleForCheck
              && !inspector.PlanTabVisibleForCheck
              && inspector.RepoRowCountForCheck == 3
              && inspector.RepoHeadlineForCheck.Contains("master", StringComparison.Ordinal)
              && inspector.RepoHeadlineForCheck.Contains(HubStrings.Get("InspectorRepoAheadBehindFormat")
                  .Replace("{0}", "2").Replace("{1}", "1"), StringComparison.Ordinal),
            "仓库页停在被点开的那一页，列出仓库相对 HEAD 的三个脏路径，并把分支与领先落后写在同一行（实际「"
            + inspector.RepoHeadlineForCheck + "」）");
        Check(inspector.RepoFileMenuItemsForCheck(0).SequenceEqual([
                HubStrings.Get("InspectorViewFile"), HubStrings.Get("InspectorEditFile")]),
            "仓库文件行同样提供明确的 View file / Edit file 菜单入口");
        Check(inspector.RepoCommitComparisonControlsAbsentForCheck,
            "仓库文件行不再显示按提交引用比较的输入框或按钮");
        Check(inspector.RepoCaveatForCheck == HubStrings.Get("InspectorRepoCaveat"),
            "仓库页第一句就讲清这是仓库的全部改动，不只是本会话做的（实际「" + inspector.RepoCaveatForCheck + "」）");
        // The shared-path mark is the one place the two lists are allowed to speak to each other: note.txt is in
        // the session's changes and in the repository's status, and saying so adds no claim to either.
        Check(inspector.RepoRowTextForCheck(1).Contains(HubStrings.Get("InspectorRepoSessionAlsoTouched"),
                  StringComparison.Ordinal)
              && inspector.RepoRowTextForCheck(1).Contains("note.txt", StringComparison.Ordinal)
              && !inspector.RepoRowTextForCheck(0).Contains(HubStrings.Get("InspectorRepoSessionAlsoTouched"),
                  StringComparison.Ordinal),
            "本会话也动过的那个路径被标出来，没动过的不被顺手标（实际「" + inspector.RepoRowTextForCheck(1) + "」）");
        // A row's hunks come from the click, not from the list paint: the snapshot carries a diff for src/main.cpp
        // and none for the untracked png, and each row has to answer differently rather than the tab deciding for
        // all of them.
        inspector.ClickRepoRowForCheck(0);
        Check(inspector.RepoRowExpandedForCheck(0)
              && inspector.RepoRowTextForCheck(0).Contains(" ~ + ~ new", StringComparison.Ordinal),
            "点开仓库行才取它的 diff，取到的是这个文件自己的那段（实际「" + inspector.RepoRowTextForCheck(0) + "」）");
        inspector.ClickRepoRowForCheck(2);
        Check(inspector.RepoRowTextForCheck(2).Contains(HubStrings.Get("InspectorRepoNoDiff"), StringComparison.Ordinal),
            "读不到改动内容的行讲清读不到，而不是画一片空白（实际「" + inspector.RepoRowTextForCheck(2) + "」）");
        // The ⟳ is the panel asking, not doing: it raises the event and the caller decides whether to run a git.
        inspector.ClickRepoRefreshForCheck();
        Check(repoReads == 1, "仓库页那个刷新按钮只把请求递出去，自己不动 git（实际收到 " + repoReads + " 次）");
        // And each way the read can stop owns its own sentence — five outcomes, five strings, none of them the
        // empty list, because "no changes" and "I could not ask" answer different questions.
        var whyKeys = new (GitReadOutcome Outcome, string Key)[]
        {
            (GitReadOutcome.NoWorkspace, "InspectorRepoNoWorkspace"),
            (GitReadOutcome.NotARepository, "InspectorRepoNotGit"),
            (GitReadOutcome.GitMissing, "InspectorRepoGitMissing"),
            (GitReadOutcome.UnsafeRepository, "InspectorRepoUnsafe"),
            (GitReadOutcome.Failed, "InspectorRepoFailed"),
        };
        var whyTexts = new List<string>();
        var whyWrong = "";
        foreach (var (outcome, key) in whyKeys)
        {
            inspector.Reload(null, [], _ => ((string?)null, UndoCopyState.Read),
                GitRepositoryState.Stopped(outcome, "probe"), "repo");
            var said = inspector.RepoNoticeForCheck;
            if (!said.Contains(HubStrings.Get(key), StringComparison.Ordinal) && whyWrong.Length == 0)
                whyWrong = $"{outcome} 说成了「{said}」，而不是 {key} 的那句";
            whyTexts.Add(said);
        }
        Check(whyWrong.Length == 0 && whyTexts.Distinct(StringComparer.Ordinal).Count() == whyKeys.Length,
            "五种读不到仓库的原因各说各的一句话，而不是同一个空列表换了个标题（" + whyWrong + "）");
        // No snapshot at all is the reading state, and it is the only state that may ask for a git run.
        inspector.Reload(null, [], _ => ((string?)null, UndoCopyState.Read), null, "repo");
        Check(inspector.RepoNoticeForCheck.Contains(HubStrings.Get("InspectorRepoReading"), StringComparison.Ordinal),
            "还没读到仓库时面板说在读，而不是先说没有");

        // ── the empty-scope jump: an empty list that knows the other one is not ──
        // Both directions are one rule seen from the two tabs, and the rule is that a jump only appears over a
        // list that really is worth opening. A jump to an empty list is a button that goes nowhere, and a jump
        // that names a count the app never read would be a claim this surface cannot make — so every state that
        // has no read behind it stays silent, and that is asserted as hard as the jump itself.
        var dirtyThree = new GitRepositoryState(GitReadOutcome.Ok, @"D:\dev\probe", "master", 0, 0,
            [
                new GitStatusEntry("note.txt", " ", "M", false),
                new GitStatusEntry("src/main.cpp", "M", " ", false),
                new GitStatusEntry("assets/图标.png", "?", "?", true),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal), false, null);
        var cleanTree = new GitRepositoryState(GitReadOutcome.Ok, @"D:\dev\probe", "master", 0, 0, [],
            new Dictionary<string, string>(StringComparer.Ordinal), false, null);
        inspector.Reload(null, [], _ => ((string?)null, UndoCopyState.Read), dirtyThree, "changes");
        Check(inspector.ChangeRowCountForCheck == 0 && inspector.ChangeJumpPresentForCheck
              && inspector.ChangeJumpTextForCheck == string.Format(System.Globalization.CultureInfo.CurrentCulture,
                  HubStrings.Get("InspectorChangesJumpRepoFormat"), 3),
            "改动页空着而仓库有未提交时，那句空状态本身是个能点的跳转，报的正是读到的那个数（实际「"
            + inspector.ChangeJumpTextForCheck + "」）");
        inspector.Reload(null, [], _ => ((string?)null, UndoCopyState.Read), null, "changes");
        Check(!inspector.ChangeJumpPresentForCheck,
            "还没读到仓库时改动页不跳转：没验证过的事实不该写成一句邀请");
        inspector.Reload(null, [], _ => ((string?)null, UndoCopyState.Read), cleanTree, "changes");
        Check(!inspector.ChangeJumpPresentForCheck,
            "读到的是干净工作树时也不跳转：那边也没有可看的东西，跳过去只是多一次空手");
        // The other tab, the same rule. This is the screen right after the assistant ran `git commit`: nothing is
        // uncommitted any more, and the reader is looking at a list that can no longer answer what they came for.
        inspector.Reload(null, changes, _ => ((string?)null, UndoCopyState.Read), cleanTree, "repo");
        Check(inspector.RepoRowCountForCheck == 0 && inspector.RepoJumpPresentForCheck
              && inspector.RepoJumpTextForCheck == string.Format(System.Globalization.CultureInfo.CurrentCulture,
                  HubStrings.Get("InspectorRepoJumpSessionFormat"), changes.Length)
              && inspector.RepoNoticeForCheck == HubStrings.Get("InspectorRepoEmpty"),
            "工作树干净而本会话改过文件时，仓库页把话递回改动页，而那一句「工作树与 HEAD 一致」还是它自己的一句，"
            + "跳转没有混进去（实际「" + inspector.RepoJumpTextForCheck + "」/「" + inspector.RepoNoticeForCheck + "」）");
        var jumpedWrong = "";
        foreach (var (outcome, _) in whyKeys)
        {
            inspector.Reload(null, changes, _ => ((string?)null, UndoCopyState.Read),
                GitRepositoryState.Stopped(outcome, "probe"), "repo");
            if (inspector.RepoJumpPresentForCheck) jumpedWrong += (jumpedWrong.Length == 0 ? "" : "、") + outcome;
        }
        inspector.Reload(null, changes, _ => ((string?)null, UndoCopyState.Read), null, "repo");
        if (inspector.RepoJumpPresentForCheck) jumpedWrong += "还没读";
        Check(jumpedWrong.Length == 0,
            "读不到仓库的每一种原因都不跳转，因为那种时候这一边自己就是答案（多余的跳转：" + jumpedWrong + "）");
        // The panel asks and the shell lands it. A jump must not switch this pane's own visibility, because the ask
        // can arrive while the column is shut, and only the shell knows the page it is on.
        var jumpedTo = "";
        inspector.ScopeJumpRequested += target => jumpedTo = target;
        inspector.Reload(null, [], _ => ((string?)null, UndoCopyState.Read), dirtyThree, "changes");
        inspector.ClickChangeJumpForCheck();
        Check(jumpedTo == "repo" && inspector.ChangesTabVisibleForCheck && !inspector.RepoTabVisibleForCheck,
            "点跳转：面板只把「要去哪一页」递出去，自己不换页（实际请求「" + jumpedTo + "」，仍停在改动页 "
            + inspector.ChangesTabVisibleForCheck + "）");

        // The repository tab's own chain, end to end: entering the tab hands a read request to the workspace, the
        // read answers away from the UI thread, and the answer has to land back on the pane. No git is needed to
        // drive this path — a session without a workspace is a read that answers at once — and if the
        // repaint-on-arrival wiring were missing, this is exactly where the tab would sit forever on 「正在读仓库…」.
        var repoSession = shell.Chat.StartConversation();
        shell.Chat.OpenConversation(repoSession.Id);
        // The column only belongs to the assistant page, and a read that lands while the page is elsewhere never
        // repaints — so the page is put where a person would be standing when they ask this.
        shell.NavigateTo("Assistant");
        panel.Reload();
        shell.OpenInspector("repo", -1);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var repoReadLanded = false;
        await WaitUntilAsync(() =>
        {
            repoReadLanded = shell.InspectorPaneForCheck?.RepoNoticeForCheck
                .Contains(HubStrings.Get("InspectorRepoNoWorkspace"), StringComparison.Ordinal) == true;
            return repoReadLanded;
        });
        Check(repoReadLanded && shell.InspectorVisibleForCheck
              && shell.InspectorPaneForCheck is { } repoPane && repoPane.RepoTabVisibleForCheck
              && repoPane.RepoRowCountForCheck == 0
              && repoPane.RepoNoticeForCheck.Contains(HubStrings.Get("InspectorRepoNoWorkspace"),
                  StringComparison.Ordinal),
            "仓库页把读取请求递出去、读完自己落回面板，并说清这个会话还没有工作目录（实际「"
            + (shell.InspectorPaneForCheck?.RepoNoticeForCheck ?? "") + "」）");
        // And one read for one question: the same tab opened twice must not start two gits, because the pane is
        // repaintable from the transcript and a repository read is the expensive thing on this surface.
        var readsAfterFirst = shell.Chat.RepositoryReadsForCheck;
        shell.CloseInspector();
        shell.OpenInspector("repo", -1);
        Dispatcher.UIThread.RunJobs();
        Check(shell.Chat.RepositoryReadsForCheck == readsAfterFirst
              && shell.InspectorPaneForCheck is { } repoAgain
              && repoAgain.RepoNoticeForCheck.Contains(HubStrings.Get("InspectorRepoNoWorkspace"),
                  StringComparison.Ordinal),
            "同一目录再开一次仓库页用的是已经读到的那一份，不是又跑一遍 git（两次之间读了 "
            + (shell.Chat.RepositoryReadsForCheck - readsAfterFirst) + " 次）");
        shell.CloseInspector();
        shell.Chat.DeleteConversation(repoSession.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();

        // The jump on a live pane is the part a fixture pane cannot prove: the ask goes out to the shell, and the
        // shell lands it on the other tab. The repository answer is seeded rather than read, because a check
        // machine has no dirty repository to point a session at — and the seeding is what lets the same cell prove
        // the jump costs no git, since the snapshot it was drawn from is the one the target tab is served from.
        var jumpWorkspace = ScratchDirectory.Resolve("review-jump-workspace");
        System.IO.Directory.CreateDirectory(jumpWorkspace);
        var jumpSession = chat.StartConversation();
        chat.OpenConversation(jumpSession.Id);
        chat.SetWorkspaceRoot(jumpSession.Id, jumpWorkspace);
        panel.Reload();
        chat.SeedRepositoryForCheck(chat.WorkspaceRootFor(jumpSession.Id) ?? jumpWorkspace, dirtyThree);
        var readsBeforeJump = chat.RepositoryReadsForCheck;
        shell.OpenInspector("changes", -1);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.InspectorPaneForCheck is { } jumpPane && jumpPane.ChangesTabVisibleForCheck
              && jumpPane.ChangeJumpPresentForCheck,
            "会话一个文件都没写过时，活的改动页上就摆着那个跳转（实际「"
            + (shell.InspectorPaneForCheck?.ChangeJumpTextForCheck ?? "") + "」）");
        shell.InspectorPaneForCheck?.ClickChangeJumpForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.InspectorVisibleForCheck
              && shell.InspectorTabForCheck == "repo"
              && shell.InspectorPaneForCheck is { } afterJump
              && afterJump.RepoTabVisibleForCheck && !afterJump.ChangesTabVisibleForCheck
              && chat.RepositoryReadsForCheck == readsBeforeJump,
            "点这个跳转换的是外壳那一页（外壳记的是「" + shell.InspectorTabForCheck + "」），不是面板自己换个可见性，"
            + "而且它一次 git 都不多跑（两次之间读了 " + (chat.RepositoryReadsForCheck - readsBeforeJump) + " 次）");
        chat.ClearRepositoryForCheck();
        shell.CloseInspector();
        chat.DeleteConversation(jumpSession.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();

        // ── the status strip's repository slot ──
        // The mapping is asserted as a table before anything live, because four of these states have to stay
        // silent — and a strip that lit on every session from first launch would be indistinguishable from a
        // correct one in a live cell, since a live cell only ever sees one state at a time.
        var silentNames = "";
        foreach (var (name, state) in new (string, GitRepositoryState?)[]
        {
            ("还没读过", null),
            ("干净工作树", cleanTree),
            ("没有工作目录", GitRepositoryState.Stopped(GitReadOutcome.NoWorkspace, "probe")),
            ("这个目录不是仓库", GitRepositoryState.Stopped(GitReadOutcome.NotARepository, "probe")),
        })
        {
            var (visible, text) = MainWindow.RepoIndicatorStateFor(state);
            if (visible || text.Length > 0) silentNames += (silentNames.Length == 0 ? "" : "、") + name;
        }
        Check(silentNames.Length == 0,
            "状态条的仓库指示只在有事可说时开口：还没读、干净、没有工作目录、不是仓库这四种都闭嘴（多嘴的："
            + silentNames + "）");
        var (_, dirtyLine) = MainWindow.RepoIndicatorStateFor(dirtyThree);
        var noBranch = new GitRepositoryState(GitReadOutcome.Ok, @"D:\dev\probe", "", 0, 0,
            [new GitStatusEntry("note.txt", " ", "M", false)],
            new Dictionary<string, string>(StringComparer.Ordinal), false, null);
        var (_, detachedLine) = MainWindow.RepoIndicatorStateFor(noBranch);
        var cutShort = new GitRepositoryState(GitReadOutcome.Ok, @"D:\dev\probe", "master", 0, 0,
            [
                new GitStatusEntry("note.txt", " ", "M", false),
                new GitStatusEntry("src/main.cpp", "M", " ", false),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal), true, null);
        var (_, cutLine) = MainWindow.RepoIndicatorStateFor(cutShort);
        Check(dirtyLine == string.Format(CultureInfo.CurrentCulture,
                  HubStrings.Get("StatusRepoDirtyFormat"), dirtyThree.Entries.Count, "master")
              && detachedLine.Contains("—", StringComparison.Ordinal)
              && cutLine == string.Format(CultureInfo.CurrentCulture,
                  HubStrings.Get("StatusRepoDirtyTruncatedFormat"), cutShort.Entries.Count, "master"),
            "脏工作树那一行报的是读到的个数和分支名，没有分支名时用仓库页同一个破折号，被截断的说「2+」而不是把上限"
            + "报成确数（实际「" + dirtyLine + "」/「" + detachedLine + "」/「" + cutLine + "」）");
        var wrongFailure = "";
        foreach (var outcome in new[] { GitReadOutcome.GitMissing, GitReadOutcome.UnsafeRepository,
                GitReadOutcome.Failed })
        {
            var (visible, text) = MainWindow.RepoIndicatorStateFor(GitRepositoryState.Stopped(outcome, "probe"));
            if (!visible || text != HubStrings.Get("StatusRepoFailed"))
                wrongFailure += (wrongFailure.Length == 0 ? "" : "、") + outcome;
        }
        Check(wrongFailure.Length == 0,
            "真读不到的那三种在状态条合成一句短话——五种理由各说各的归仓库页，那才是读理由的地方（不合规的："
            + wrongFailure + "）");
        // Live. The snapshot is seeded rather than read, because a check machine has no dirty repository to point
        // a session at; seeding goes through the same tail a landing uses, so what the cells below actually measure
        // is the wiring from that landing to the strip — and that no git ran on the way.
        var stripWorkspace = ScratchDirectory.Resolve("review-strip-workspace");
        Directory.CreateDirectory(stripWorkspace);
        var stripSession = chat.StartConversation();
        chat.OpenConversation(stripSession.Id);
        chat.SetWorkspaceRoot(stripSession.Id, stripWorkspace);
        panel.Reload();
        shell.NavigateTo("Assistant");
        Dispatcher.UIThread.RunJobs();
        Check(!shell.RepoIndicatorVisibleForCheck,
            "这个目录一次都没读过时状态条那颗指示是灭的，而不是先猜一个数");
        var readsBeforeLight = chat.RepositoryReadsForCheck;
        chat.SeedRepositoryForCheck(chat.WorkspaceRootFor(stripSession.Id) ?? stripWorkspace, dirtyThree);
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.RepoIndicatorVisibleForCheck
              && shell.RepoIndicatorTextForCheck.Contains("master", StringComparison.Ordinal)
              && chat.RepositoryReadsForCheck == readsBeforeLight,
            "一次读落地就把分支和未提交数写进状态条，而这一趟自己没跑 git（实际「"
            + shell.RepoIndicatorTextForCheck + "」，读了 "
            + (chat.RepositoryReadsForCheck - readsBeforeLight) + " 次）");
        // A session pointed somewhere else must not keep reporting the directory it left, and the slot belongs to
        // the window rather than to the assistant page — it stays while a person is on another page, because that
        // is when they need the reminder.
        var awaySession = chat.StartConversation();
        chat.OpenConversation(awaySession.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.RepoIndicatorVisibleForCheck,
            "换到一个没有工作目录的会话时指示跟着闭嘴，而不是继续报上一个目录的数");
        chat.OpenConversation(stripSession.Id);
        panel.Reload();
        shell.NavigateTo("Projects");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.RepoIndicatorVisibleForCheck && !shell.AssistantVisible,
            "指示条是窗口自己的东西，离开助手页它还在（当前页 " + shell.AssistantVisible + "）");
        // Pressing it is a way into the tab, not a second read: the only way it was reachable at all is a read
        // that already landed, so the tab has to open onto what is already known.
        var readsBeforePress = chat.RepositoryReadsForCheck;
        shell.ClickRepoIndicatorForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(shell.AssistantVisible && shell.InspectorVisibleForCheck
              && shell.InspectorTabForCheck == "repo"
              && shell.InspectorPaneForCheck?.RepoTabVisibleForCheck == true,
            "从项目页点状态条的仓库指示：先回到助手页，再把右列开到仓库页，而不是把列开在看不见的地方");
        Check(chat.RepositoryReadsForCheck == readsBeforePress,
            "从指示条进仓库页一次 git 都不多跑，用的是已经读到的那一份（两次之间读了 "
            + (chat.RepositoryReadsForCheck - readsBeforePress) + " 次）");
        // And the row itself: a sixth column in a 28-pixel strip may not grow the strip, square the log glyph
        // into a rectangle, or squeeze the operation text out.
        var (plateWidth, plateHeight) = shell.LogTogglePlateForCheck;
        Check(shell.StatusStripHeightForCheck <= 28.5
              && Math.Abs(plateWidth - plateHeight) <= 1.5
              && shell.StatusWidthForCheck > 40
              && shell.RepoIndicatorWidthForCheck > 0,
            "指示条亮着的时候状态栏还是那 28 高、日志底板还是正方形、左边的状态文字没被挤没（列高 "
            + Fmt(shell.StatusStripHeightForCheck) + "，底板 " + Fmt(plateWidth) + "×" + Fmt(plateHeight)
            + "，状态宽 " + Fmt(shell.StatusWidthForCheck) + "）");
        chat.ClearRepositoryForCheck();
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        Check(!shell.RepoIndicatorVisibleForCheck,
            "把那份读回收走之后指示跟着灭，而不是留着一个已经没有对象的数字");
        shell.CloseInspector();
        chat.DeleteConversation(stripSession.Id);
        chat.DeleteConversation(awaySession.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();

        // ── the steer strip: a second tap before an interjection spends a request ──
        var session = chat.StartConversation();
        chat.OpenConversation(session.Id);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["这条会被引导打断"], gate: hold.Task);
            panel.SetInputForCheck("正在答的这句");
            _ = panel.SendComposerForCheck();
            await WaitUntilAsync(() => panel.IsStreamingForCheck);

            panel.SetInputForCheck("半路改的主意");
            await panel.SendComposerForCheck();
            Dispatcher.UIThread.RunJobs();
            Check(panel.SteerConfirmVisibleForCheck
                  && panel.SteerConfirmTextForCheck == "半路改的主意"
                  && panel.InputTextForCheck.Length == 0,
                "回复进行中第一次回车只是把草稿摆上确认条、输入框随之清空，还没有真的引导出去（条上「"
                + panel.SteerConfirmTextForCheck + "」，框里「" + panel.InputTextForCheck + "」）");

            panel.ClickSteerEditForCheck();
            Check(!panel.SteerConfirmVisibleForCheck && panel.InputTextForCheck == "半路改的主意",
                "「编辑」把草稿原样放回输入框，确认条收起：这是草稿回到框里的唯一一条路");

            await panel.SendComposerForCheck();
            Dispatcher.UIThread.RunJobs();
            Check(panel.SteerConfirmVisibleForCheck, "夹具：再按一次发送，确认条又出现了");
            panel.PressComposerKeyForCheck(Key.Escape);
            Check(!panel.SteerConfirmVisibleForCheck && panel.InputTextForCheck == "半路改的主意",
                "Esc 等于「编辑」而不是「丢弃」：一个会删掉整句的键不能是随手按下的那个");

            await panel.SendComposerForCheck();
            Dispatcher.UIThread.RunJobs();
            panel.ClickSteerDiscardForCheck();
            Check(!panel.SteerConfirmVisibleForCheck && panel.InputTextForCheck.Length == 0,
                "「丢弃」是唯一会毁掉草稿的动作，只有图标、没有键盘路径");

            // The line gives way rather than running on. The commit button has no plate of its own, so a draft that
            // overflowed its column read as two sentences printed on top of each other — which a short draft
            // satisfies the first assertion of without asking anything. Hence a draft longer than the strip, and the
            // second half: the line really did have to give something up.
            var longDraft = new string('1', 200);
            panel.SetInputForCheck(longDraft);
            await panel.SendComposerForCheck();
            Dispatcher.UIThread.RunJobs();
            shell.UpdateLayout();
            var clip = panel.SteerConfirmClipForCheck;
            var textWidth = clip?.TextWidth ?? double.NaN;
            var naturalWidth = clip?.NaturalWidth ?? double.NaN;
            var textRight = clip?.TextRight ?? double.NaN;
            var actionsLeft = clip?.ActionsLeft ?? double.NaN;
            Check(clip is { } strip && strip.TextRight <= strip.ActionsLeft + 1
                  && strip.TextWidth < strip.NaturalWidth - 20,
                "长草稿在确认条上占一行、到发送键前就收住，不进到那个透明底板的字底下，而且确实是被裁过的（草稿 "
                + longDraft.Length + " 字符，行里量到 " + Fmt(textWidth) + "，不设限是 " + Fmt(naturalWidth)
                + "，行右端 " + Fmt(textRight) + "，动作列左端 " + Fmt(actionsLeft) + "）");
            Check(panel.SteerConfirmTextTipForCheck == longDraft,
                "裁掉的部分没有丢：整句挂在确认条的提示里（提示 " + panel.SteerConfirmTextTipForCheck.Length
                + " 字符，草稿 " + longDraft.Length + " 字符）");
            // The fixture puts the strip back the way it found it, so no later scene inherits an armed draft.
            panel.ClickSteerDiscardForCheck();

            hold.SetResult(true);
            await panel.WaitForRunToFinishForCheck();
        }
        finally
        {
            chat.ClientOverride = savedOverride;
            if (chat.Conversations.Any(summary => summary.Id == session.Id)) chat.DeleteConversation(session.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal async Task CheckPlanApprovalAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedIdleTimeout = chat.IdleTimeout;
        var savedFirstFrameTimeout = chat.FirstFrameTimeout;
        var observer = chat.StartConversation();
        var approvedSession = chat.StartConversation();
        var rejectedSession = chat.StartConversation();
        var twoPlanSession = chat.StartConversation();
        const string plan = "# Reviewed plan\n\n- First, inspect the implementation.\n- Then, make the change.";
        var attentionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        void CountAttention(string conversationId, ChatAttentionKind _)
            => attentionCounts[conversationId] = attentionCounts.GetValueOrDefault(conversationId) + 1;
        chat.AttentionRequired += CountAttention;
        chat.IdleTimeout = TimeSpan.FromSeconds(5);
        // Both windows, for the same reason as the ask-user cell: a plan card parks, and what is armed
        // while it waits is the round's window rather than the silence one.
        chat.FirstFrameTimeout = TimeSpan.FromSeconds(5);

        async Task RunPlanInBackgroundAsync(Conversation session)
        {
            chat.OpenConversation(session.Id);
            chat.SelectMode(ChatModes.Plan);
            var sent = chat.TryEnqueueSend(session.Id, "请先给出计划", null, out var refusal);
            chat.OpenConversation(observer.Id);
            await WaitForIdleAsync(chat);
            // A finished plan-mode answer becomes a review item, and its body leaves the transcript for a document
            // of its own. The halves are asserted together because each alone passes on a bug: a turn still
            // holding the text is the old shape, a session file still holding it is the shape this change exists
            // to remove, and a JSON naming a file nobody wrote is worse than either.
            var stamped = session.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant
                                                                 && turn.PlanApprovalState == PlanApprovalStates.Pending);
            var name = stamped?.Plan?.File ?? "";
            var body = chat.PlansDirectoryForCheck is { } plans && System.IO.File.Exists(System.IO.Path.Combine(plans, name))
                ? System.IO.File.ReadAllText(System.IO.Path.Combine(plans, name))
                : null;
            var json = chat.StoredSessionTextForCheck(session.Id) ?? "";
            Check(sent && refusal is null
                  && stamped is not null
                  && stamped.Text.Length == 0
                  && name.Length == 35 && name.EndsWith(".md", StringComparison.Ordinal)
                  && name[..32].All(Uri.IsHexDigit)
                  && stamped.Plan!.Title == "Reviewed plan"
                  && stamped.Plan.Chars == plan.Length
                  && stamped.Plan.Tokens > 0
                  && body == plan
                  && json.Contains(name, StringComparison.Ordinal)
                  && !json.Contains("First, inspect the implementation", StringComparison.Ordinal),
                "计划模式结束后把正文写进 ai/plans 下的独立文件、会话 JSON 只留它的文件名，并把这一轮标成待确认项"
                + "（会话 " + session.Id + "，盘上正文 " + (body?.Length ?? -1) + "/" + plan.Length
                + " 字，文件 " + name + "，拒绝原因 " + (refusal ?? "无") + "）");
        }

        try
        {
            ScriptedChatClient? planClient = null;
            chat.ClientOverride = (_, _) => planClient = new ScriptedChatClient([plan]);

            await RunPlanInBackgroundAsync(approvedSession);
            Check(chat.PendingBackgroundApprovalCount(observer.Id) == 1
                  && chat.PendingApprovalConversationCount() == 1
                  && approvedSession.Messages.Last().ApprovalSeen == false,
                "后台会话中的待确认计划计入任务栏审批标记");
            Check(attentionCounts.GetValueOrDefault(approvedSession.Id) == 1,
                "一次计划确认只发出一次待处理关注事件（实际 "
                + attentionCounts.GetValueOrDefault(approvedSession.Id) + " 次）");
            await RunPlanInBackgroundAsync(rejectedSession);
            Check(chat.PendingApprovalConversationCount() == 2
                  && chat.PendingBackgroundApprovalCount(observer.Id) == 2,
                "任务栏数字按待审批会话计数而非操作数，并能反映多个待处理会话（实际 "
                + chat.PendingApprovalConversationCount() + "）");
            Check(SystemAttentionService.FormatBadgeCount(0) == "0"
                  && SystemAttentionService.FormatBadgeCount(1) == "1"
                  && SystemAttentionService.FormatBadgeCount(42) == "42"
                  && SystemAttentionService.FormatBadgeCount(999) == "999"
                  && SystemAttentionService.FormatBadgeCount(1000) == "999+",
                "原生徽标计数格式覆盖零、单/多位及极大值（实际 "
                + string.Join(", ", new[] { 0, 1, 42, 999, 1000 }
                    .Select(SystemAttentionService.FormatBadgeCount)) + "）");
            // The host is `sessions`: that is what the notification writes and what the sidebar's menu copies.
            // `conversation` is the older spelling, pinned as rejected in --check-linux-integration.
            var deepLink = $"axmolhub://sessions/{approvedSession.Id}";
            Check(SystemAttentionService.TryGetConversationId(deepLink, out var activatedId)
                  && activatedId == approvedSession.Id
                  && !SystemAttentionService.TryGetConversationId("axmolhub://sessions/not-a-guid", out _),
                "系统通知激活链接只解析有效的会话深链（实际 " + activatedId + "）");
            // The gate is a pure predicate, so its whole truth table needs no window: three inputs (window in
            // front, assistant page on screen, this very conversation viewed) decide one output. The eight rows
            // are written out rather than derived from the implementation — a table generated from the predicate
            // would be the predicate restating itself.
            var viewed = approvedSession.Id;
            var background = observer.Id;
            Check(!SystemAttentionService.ShouldNotifyApproval(viewed, viewed, new AttentionVisibility(true, true))
                  && SystemAttentionService.ShouldNotifyApproval(viewed, background, new AttentionVisibility(true, true))
                  && SystemAttentionService.ShouldNotifyApproval(viewed, viewed, new AttentionVisibility(true, false))
                  && SystemAttentionService.ShouldNotifyApproval(viewed, background, new AttentionVisibility(true, false))
                  && SystemAttentionService.ShouldNotifyApproval(viewed, viewed, new AttentionVisibility(false, true))
                  && SystemAttentionService.ShouldNotifyApproval(viewed, background, new AttentionVisibility(false, true))
                  && SystemAttentionService.ShouldNotifyApproval(viewed, viewed, new AttentionVisibility(false, false))
                  && SystemAttentionService.ShouldNotifyApproval(viewed, background, new AttentionVisibility(false, false)),
                "系统通知闸门：只有「窗口在前台 + 停在助手页 + 正是该会话」三条同时成立才不打扰；窗口失焦或最小化时，即便正停在该会话也照样提醒（八种组合全覆盖）");
            Check(Enum.GetValues<RunResult>()
                      .Where(result => result is RunResult.Completed or RunResult.Incomplete
                          or RunResult.Failed or RunResult.TimedOut)
                      .All(result => SystemAttentionService.ShouldNotifyRun(
                          viewed, background, new AttentionVisibility(true, true),
                          hasPendingPlan: false, result))
                  && SystemAttentionService.ShouldNotifyRun(
                      viewed, viewed, new AttentionVisibility(false, true),
                      hasPendingPlan: false, RunResult.Completed)
                  && !SystemAttentionService.ShouldNotifyRun(
                      viewed, viewed, new AttentionVisibility(true, true),
                      hasPendingPlan: false, RunResult.Completed)
                  && !SystemAttentionService.ShouldNotifyRun(
                      viewed, background, new AttentionVisibility(false, true),
                      hasPendingPlan: true, RunResult.Completed)
                  && !SystemAttentionService.ShouldNotifyRun(
                      viewed, background, new AttentionVisibility(false, true),
                      hasPendingPlan: false, RunResult.Cancelled)
                  && !SystemAttentionService.ShouldNotifyRun(
                      viewed, background, new AttentionVisibility(false, true),
                      hasPendingPlan: false, RunResult.Parked),
                "运行结束闸门：完成/失败/超时提醒而取消、暂停不报完成，仍有待确认计划时让位给审批提醒；失焦与最小化下同样成立");
            // Visibility suppresses only redundant system notifications. The native badge is a count of every
            // pending session, so switching pages or minimizing must not change it.
            shell.NavigateTo("Assistant");
            shell.SetWindowVisibleForCheck(true);
            var forcedVisible = shell.AttentionNow;
            var badgeWhileVisible = chat.PendingApprovalConversationCount();
            shell.SetWindowVisibleForCheck(false);
            var forcedHidden = shell.AttentionNow;
            var badgeWhileHidden = chat.PendingApprovalConversationCount();
            shell.SetWindowVisibleForCheck(null);
            Dispatcher.UIThread.RunJobs();
            Check(forcedVisible is { WindowVisible: true, AssistantPageVisible: true }
                  && forcedHidden is { WindowVisible: false, AssistantPageVisible: true }
                  && badgeWhileVisible == 2
                  && badgeWhileHidden == badgeWhileVisible
                  && shell.AttentionNow.WindowVisible == (shell.IsActive && shell.WindowState != WindowState.Minimized),
                "系统通知闸门仍区分窗口可见性，而任务栏待审批会话数不随当前页面或窗口状态变化（可见/隐藏 "
                + badgeWhileVisible + "/" + badgeWhileHidden + "）");
            // The click that carries no identifier: a desktop that only raises the running window gives the shell
            // one signal — the window came back — so the conversation has to be the one the notification
            // remembered. Measured on GNOME 46 first, where the activation that finishes start-up arrived 13 ms
            // after the notification was armed and spent it before any click existed; ActivateFromRequest is the
            // entry point a second instance's bare activation reaches.
            chat.OpenConversation(background);
            shell.AttentionForCheck.RememberActivationTarget(viewed, DateTimeOffset.UtcNow);
            shell.ReturnToForegroundForCheck(false);
            Dispatcher.UIThread.RunJobs();
            Check(chat.ViewedConversationId == background,
                "还没离开过前台的激活不消费待处理会话：没人点过通知，视图不该被挪走（实际 " + chat.ViewedConversationId + "）");
            shell.ReturnToForegroundForCheck(true);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(chat.ViewedConversationId == viewed && shell.AssistantVisible
                  && shell.WindowState != WindowState.Minimized,
                "走开再回来后，回到助手页并选中最近一次系统通知的那个会话（实际 " + chat.ViewedConversationId + "）");
            chat.OpenConversation(background);
            shell.ReturnToForegroundForCheck(true);
            Dispatcher.UIThread.RunJobs();
            Check(chat.ViewedConversationId == background,
                "一次通知只被消费一次，稍后的普通回到前台不再跳回那个会话（实际 " + chat.ViewedConversationId + "）");
            chat.OpenConversation(background);
            shell.AttentionForCheck.RememberActivationTarget(viewed, DateTimeOffset.UtcNow);
            shell.ActivateFromRequest();
            Dispatcher.UIThread.RunJobs();
            Check(chat.ViewedConversationId == viewed && shell.AssistantVisible,
                "裸唤起同样消费待处理会话：窗口回到助手页并选中该会话（实际 " + chat.ViewedConversationId + "）");
            shell.AttentionForCheck.RememberActivationTarget(background, DateTimeOffset.UtcNow);
            await shell.HandleInstallLinkAsync(deepLink);
            Dispatcher.UIThread.RunJobs();
            shell.ActivateFromRequest();
            Dispatcher.UIThread.RunJobs();
            Check(chat.ViewedConversationId == viewed,
                "带 id 的深链到达时清掉待消费的会话，避免稍后普通唤起跳到旧会话（实际 " + chat.ViewedConversationId + "）");

            await shell.HandleInstallLinkAsync(deepLink);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var planCardVisible = panel.PlanApprovalCardOnScreenForCheck;
            var planCardsPainted = panel.PlanCardCountForCheck;
            var planCardText = panel.PlanCardTextsForCheck.FirstOrDefault() ?? "";
            var planBodiesSuppressed = panel.PlanBodiesSuppressedForCheck;
            // The two hooks that used to be this assertion's positive half are now its negative half: a plan body
            // that still reaches the transcript — rendered, or as the plain block rendering replaces — is the
            // document back in the flow. That the document itself survives is pinned elsewhere, on the stored turn
            // text above and on the instruction the approval sends below.
            var planBodyInk = panel.PlanApprovalMarkdownOnScreenForCheck
                              || panel.PlanApprovalMessageTextForCheck.Length > 0;
            Check(chat.PendingBackgroundApprovalCount(approvedSession.Id) == 1
                  && chat.PendingApprovalConversationCount() == 2
                  && approvedSession.Messages.Last().ApprovalSeen
                  && chat.ViewedConversationId == approvedSession.Id
                  && panel.PendingPlanApprovalCardsForCheck == 1
                  && planCardVisible
                  && planCardsPainted == 1
                  && panel.PlanCardOnScreenForCheck(0)
                  && planCardText.Contains("Reviewed plan", StringComparison.Ordinal)
                  && planBodiesSuppressed
                  && !planBodyInk,
                "打开会话后仍计入待审批会话数字；计划以一张紧凑卡片真实显示、标题上卡，正文不再铺进转录，决定摆在输入框上方（待后台 "
                + chat.PendingBackgroundApprovalCount(approvedSession.Id) + "，卡面「" + planCardText + "」，"
                + "转录里的正文 " + planBodyInk + "）");
            shell.CloseInspector();
            panel.ClickPlanCardForCheck(0);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(shell.InspectorVisibleForCheck
                  && shell.InspectorPlanMarkdownForCheck.Contains("Reviewed plan", StringComparison.Ordinal),
                "点计划卡即打开右栏，全文只在那里：卡片是正文的入口而不是正文的替代品（面板 "
                + shell.InspectorVisibleForCheck + "，面板里 " + shell.InspectorPlanMarkdownForCheck.Length + " 字）");
            shell.NavigateTo("Projects");
            Check(chat.PendingBackgroundApprovalCount(null) == 2
                  && chat.PendingApprovalConversationCount() == 2,
                "离开助手页不会改变所有未解决计划审批的任务栏数字");
            shell.NavigateTo("Assistant");
            Check(chat.PendingBackgroundApprovalCount(approvedSession.Id) == 1
                  && chat.PendingApprovalConversationCount() == 2,
                "返回一个待审批会话时，其他后台会话仍计入数字且当前会话也不被漏计");
            Check(panel.PlanReviewActionsForCheck.SequenceEqual(
                      ["ChatPlanReviewOpen", "ChatPlanCancel", "ChatPlanRevise", "ChatPlanApprove"], StringComparer.Ordinal)
                  && !panel.PlanReviewHasInputForCheck,
                "计划审阅提供查看、取消、聚焦聊天和直接批准，没有第二个意见框或继续步骤（实际 "
                + string.Join(", ", panel.PlanReviewActionsForCheck) + "）");

            const string revision = "先把测试补上，再改实现，可以开始了";
            panel.SetInputForCheck(revision);
            var beforeFocus = approvedSession.Messages.Count;
            panel.ClickPlanActionForCheck("ChatPlanRevise");
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.InputTextForCheck == revision && !panel.DecisionHostFocusedForCheck
                  && approvedSession.Messages.Count == beforeFocus && panel.PendingPlanApprovalCardsForCheck == 1,
                "在聊天中修改只聚焦原输入区，保留草稿、不消费待审决定、不启动请求");

            // Refusals must not consume a review. Existing image checks cover formats, and UI stale-draft
            // checks cover the pre-await guard; neither sees the backend gate after an attachment read yields.
            var refusals = new List<string>();
            var refusalsKeptReview = true;
            foreach (var stale in new[] { false, true })
            {
                var accepted = chat.TryEnqueueSend(approvedSession.Id, revision, null,
                    stale ? [PictureFixture()] : [new byte[] { 1, 2, 3 }], false, out var refusal,
                    stale ? ChatWorkspace.PendingPlanToken(approvedSession) + "-stale" : null);
                refusals.Add(refusal ?? "none");
                refusalsKeptReview &= !accepted && refusal is not null && (!stale || refusal == "ChatPlanChanged")
                    && approvedSession.Messages.Count == beforeFocus
                    && approvedSession.Messages.Any(turn => turn.PlanApprovalState == PlanApprovalStates.Pending)
                    && chat.RunFor(approvedSession.Id) is null;
            }
            Check(refusalsKeptReview,
                "发送遇到无效附件或过期计划引用时，原计划仍待审、没有追加半条消息（拒绝 " + string.Join(" / ", refusals) + "）");

            // Even an explicitly chosen Agent mode and words saying to start cannot turn feedback into approval.
            chat.SelectMode(ChatModes.Agent);
            panel.AddImageBytesForCheck(PictureFixture(), "plan-reference.png");
            await panel.SendForCheckAsync(revision);
            await WaitForIdleAsync(chat);
            Dispatcher.UIThread.RunJobs();
            var revisionTurn = approvedSession.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User);
            var revisionWire = planClient?.LastMessages?.LastOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User);
            Check(approvedSession.Messages.Any(turn => turn.Role == ChatRoles.Assistant
                      && turn.PlanApprovalState == PlanApprovalStates.RevisionRequested)
                  && chat.ActiveMode == ChatModes.Plan
                  && revisionTurn is { Images.Count: 1, Plan.File.Length: > 0 }
                  && revisionTurn.Text == revision
                  && revisionTurn.AttachedContext?.Contains(ChatWorkspace.PlanRevisionInstruction, StringComparison.Ordinal) == true
                  && revisionWire?.Text.Contains("First, inspect the implementation", StringComparison.Ordinal) == true
                  && revisionWire.Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count() == 1
                  && panel.PendingPictureCountForCheck == 0 && panel.InputTextForCheck.Length == 0,
                "聊天修改将文字和图片与原计划一起发出，只记录修改、不批准，成功后才清空草稿（图片 "
                + revisionTurn?.Images.Count + "，模式 " + chat.ActiveMode + "）");
            Check(approvedSession.Messages.Count(turn =>
                      turn.Role == ChatRoles.Assistant && turn.PlanApprovalState == PlanApprovalStates.Pending) == 1,
                "修改请求可以再次发送，新的计划重新进入待确认状态");
            Check(attentionCounts.GetValueOrDefault(approvedSession.Id) == 2,
                "修改后再次生成计划只新增一次待审事件（实际 "
                + attentionCounts.GetValueOrDefault(approvedSession.Id) + " 次）");

            // The card is the one surface for every disposition, so its wording has to change with the state
            // rather than a second record line appear under it. Two cards now: the older one already answered.
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.PlanCardCountForCheck == 2
                  && panel.PlanCardStateKeysForCheck.SequenceEqual(
                      ["ChatPlanRevisionRequested", "ChatPlanPending"], StringComparer.Ordinal),
                "同一张计划卡就地换掉措辞：要求修改后旧卡说「计划已要求修改」、新卡说「计划待你审阅」，不再另起一行决定记录（实际 "
                + string.Join(" / ", panel.PlanCardStateKeysForCheck) + "）");

            // The approved run's scripted answer is deliberately not the plan's own text. The assertion further
            // down reads the session file for "no copy of the plan body anywhere", and an agent reply that happened
            // to repeat the fixture string would fail it for a reason that has nothing to do with plans.
            var approvalGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.ClientOverride = (_, _) => planClient = new ScriptedChatClient(["开始按计划改第一个文件。"], gate: approvalGate.Task);
            panel.SetInputForCheck("批准前还没发出的意见");
            panel.AddImageBytesForCheck(PictureFixture(), "before-approval.png");
            panel.ClickPlanActionForCheck("ChatPlanApprove");
            try
            {
                // Unlike cancellation, approval starts a live run. Old feedback must be refused before the
                // normal running-composer path can offer to inject it as a steer into implementation.
                await panel.SendComposerForCheck();
                Check(panel.InputTextForCheck == "批准前还没发出的意见" && panel.PendingPictureCountForCheck == 1
                      && panel.LastNoticeTextForCheck == HubStrings.Get("ChatPlanChanged")
                      && chat.RunFor(approvedSession.Id) is { HasQueuedSteer: false }
                      && !panel.SteerConfirmVisibleForCheck,
                    "计划已批准并开始运行时，旧意见不会变成实施中的插话，文字图片保留并要求重新查看");
            }
            finally
            {
                approvalGate.TrySetResult(true);
                panel.RemovePictureChipForCheck(0);
                panel.SetInputForCheck("");
            }
            await WaitForIdleAsync(chat);
            var approvedCopy = chat.StoredCopyForCheck(approvedSession.Id);
            var approvedInstruction = "Implement the following plan, which I have reviewed and approved. "
                                      + "Follow this plan only; ask before taking actions outside its scope.\n\n" + plan;
            var approvedState = approvedCopy?.Messages
                .LastOrDefault(turn => turn.Role == ChatRoles.Assistant)?.PlanApprovalState;
            var approvedUserTurn = approvedCopy?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User);
            var approvedUserText = approvedUserTurn?.Text;
            // The split this change is built on, read from both ends: what the session file keeps is the sentence,
            // and what the model is handed is the sentence with the approved plan under it — byte for byte what
            // approval has always sent. Asserting only the first half would let the body go missing on the wire;
            // asserting only the second would let it stay pasted into the transcript. Both turns naming one
            // document is the third half: the instruction must point at the plan it approves, not at a copy.
            var wireInstruction = planClient?.LastMessages?
                .LastOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)?.Text;
            var approvedRun = chat.RunFor(approvedSession.Id);
            Check(approvedCopy?.Mode == ChatModes.Agent
                  && approvedCopy.Messages.Any(turn => turn.Role == ChatRoles.Assistant
                      && turn.PlanApprovalState == PlanApprovalStates.Approved)
                  && approvedUserText == ChatWorkspace.PlanApprovalInstruction
                  && approvedUserTurn?.Plan?.File is { Length: > 0 } approvedName
                  && approvedName == approvedCopy.Messages
                      .LastOrDefault(turn => turn.Role == ChatRoles.Assistant
                                             && turn.PlanApprovalState == PlanApprovalStates.Approved)?.Plan?.File
                  && wireInstruction == approvedInstruction
                  && approvedRun is null
                  && chat.PendingApprovalConversationCount() == 1,
                "批准后切换 Agent 并以用户指令附带原样审阅计划继续执行：存的是引用，线上是全文，完成后无残留运行（模式 "
                + approvedCopy?.Mode + "，末条计划状态 " + approvedState + "，存下的指令「"
                + approvedUserText?.Replace('\n', '|') + "」，引用 "
                + (approvedUserTurn?.Plan?.File ?? "无") + "，线上末条用户消息 "
                + (wireInstruction?.Length ?? -1) + "/" + approvedInstruction.Length + " 字，运行 "
                + approvedRun?.Phase + "，其他待审批会话数 " + chat.PendingApprovalConversationCount() + "）");
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            Check(panel.PendingPlanApprovalCardsForCheck == 0,
                "计划决定后卡片退出待确认状态（卡片 "
                + panel.PendingPlanApprovalCardsForCheck + "，会话状态 "
                + string.Join(",", approvedCopy?.Messages
                    .Where(turn => turn.PlanApprovalState is not null)
                    .Select(turn => turn.PlanApprovalState) ?? []) + "）");

            chat.OpenConversation(rejectedSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.SetInputForCheck("取消前写好的修改意见");
            panel.AddImageBytesForCheck(PictureFixture(), "unsent-feedback.png");
            var rejectedCount = rejectedSession.Messages.Count;
            panel.ClickPlanCancelForCheck();
            await panel.SendForCheckAsync("取消前写好的修改意见");
            Check(panel.InputTextForCheck == "取消前写好的修改意见" && panel.PendingPictureCountForCheck == 1
                  && panel.LastNoticeTextForCheck == HubStrings.Get("ChatPlanChanged")
                  && rejectedSession.Messages.Count == rejectedCount && chat.RunFor(rejectedSession.Id) is null,
                "取消计划后旧意见不会悄悄变成普通消息，文字和图片保留并提示重新查看");
            panel.RemovePictureChipForCheck(0);
            panel.SetInputForCheck("");
            var rejectedCopy = chat.StoredCopyForCheck(rejectedSession.Id);
            Check(rejectedCopy?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant)
                      ?.PlanApprovalState == PlanApprovalStates.Rejected
                  && chat.ActiveMode == ChatModes.Plan
                  && chat.RunFor(rejectedSession.Id) is null
                  && chat.PendingApprovalConversationCount() == 0,
                "拒绝计划会留下拒绝记录、不启动 Agent、清空会话徽标计数，并保持计划模式");
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.PlanCardStateKeysForCheck.SequenceEqual(["ChatPlanRejected"], StringComparer.Ordinal)
                  && panel.PlanBodiesSuppressedForCheck,
                "被拒绝的计划也只剩一张说「计划已拒绝」的卡片，正文从头到尾没有进过转录（实际 "
                + string.Join(" / ", panel.PlanCardTextsForCheck) + "）");

            // Two plans that differ, so a click on the older card can be told apart from a click on the newer
            // one. The pane must show the document the card names, not simply the last plan that was written —
            // which is the whole reason the card carries its turn index into the shell.
            const string firstPlan = "# First draft\n\n## Implementation tasks\n- [ ] Read the existing code\n- [ ] Make and verify the change\n\n## Notes\n- The older body.";
            const string secondPlan = "# Second draft\n\n## Implementation tasks\n- [ ] Inspect the new request\n\n## Notes\n- The newer body.";
            chat.ClientOverride = (_, _) => new ScriptedChatClient(
                [twoPlanSession.Messages.Count(turn => turn.PlanApprovalState is not null) == 0
                    ? firstPlan
                    : secondPlan]);
            chat.OpenConversation(twoPlanSession.Id);
            chat.SelectMode(ChatModes.Plan);
            chat.TryEnqueueSend(twoPlanSession.Id, "先给出计划", null, out _);
            await WaitForIdleAsync(chat);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            // The card takes the keyboard when nothing is being written, which is the case where the letters on it
            // are worth more than a caret. The arrival after this one has a draft in the box and must not.
            //
            // Split in two, and the focus half waits for the request to land. The panel asks for the focus on the
            // rebuild that builds the card, but the window this check drives is never shown, so the focus manager
            // is entitled to hand the element its focus a beat later than the rebuild that asked — which is what
            // made a single read flaky on X11 (a handful of runs in a hundred, never a real regression). A bounded
            // wait keeps the claim and drops the race; the card count is read first so a card that never arrived
            // cannot be reported as a focus that never landed.
            Check(panel.PendingPlanApprovalCardsForCheck == 1,
                "空着输入框时计划卡到达（实际 " + panel.PendingPlanApprovalCardsForCheck + " 张待批准计划卡）");
            var initialTaskSummary = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatPlanTaskSummaryFormat"), 2, 0, 0, 2);
            var legacyPlanTaskState = System.Text.Json.JsonSerializer.Deserialize<Conversation>("{\"Id\":\"legacy-plan-task-state\"}");
            Check(!panel.PlanTasksExpandedForCheck && panel.PlanTaskSummaryForCheck == initialTaskSummary
                  && legacyPlanTaskState?.PlanTasksExpanded == false,
                "新计划及缺少展开字段的旧会话默认折叠，摘要显示任务状态计数（实际「" + panel.PlanTaskSummaryForCheck + "」）");
            panel.TogglePlanTasksForCheck();
            Dispatcher.UIThread.RunJobs();
            Check(panel.PlanTaskTitlesForCheck.SequenceEqual(
                      ["Read the existing code", "Make and verify the change"], StringComparer.Ordinal)
                  && panel.PendingPlanTaskGlyphsForCheck == 2
                  && panel.PlanTasksExpandedForCheck
                  && panel.PlanTasksPaintedForCheck
                  && panel.PlanTasksAboveComposerForCheck,
                "展开后计划任务出现在 composer 上方，文字与空心方框都有可见画刷（实际 "
                + string.Join(" / ", panel.PlanTaskTitlesForCheck) + "，空心图标 "
                + panel.PendingPlanTaskGlyphsForCheck + " 个）");
            chat.OpenConversation(rejectedSession.Id);
            panel.Reload();
            chat.OpenConversation(twoPlanSession.Id);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            Check(panel.PlanTasksExpandedForCheck
                  && chat.StoredCopyForCheck(twoPlanSession.Id)?.PlanTasksExpanded == true,
                "切换会话再返回恢复计划任务卡展开状态，session JSON 持久化该选择");
            var taskPlanIndex = twoPlanSession.Messages.FindLastIndex(turn => turn.Plan?.Tasks is { Count: > 0 });
            var taskPlanTurn = twoPlanSession.Messages[taskPlanIndex];
            twoPlanSession.Messages[taskPlanIndex] = taskPlanTurn with
            {
                Plan = taskPlanTurn.Plan! with
                {
                    Tasks = taskPlanTurn.Plan.Tasks!.Select((task, index) => task with
                    {
                        State = index == 0 ? PlanTaskStates.InProgress : PlanTaskStates.Completed,
                    }).ToArray(),
                },
            };
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            var updatedTaskSummary = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatPlanTaskSummaryFormat"), 2, 1, 1, 0);
            Check(panel.PlanTasksExpandedForCheck && panel.PlanTaskSummaryForCheck == updatedTaskSummary
                  && panel.PlanTaskTitlesForCheck.Count == 2 && panel.PlanTasksPaintedForCheck,
                "同一计划更新进度后保留展开状态，摘要和状态图标同步更新（实际「"
                + panel.PlanTaskSummaryForCheck + "」）");
            // A plan longer than the eight-row cap is the only shape where a rebuild would be visible: the list
            // has somewhere to be scrolled to, and a status change through this same repaint must not put it back
            // at the first row under whoever scrolled down to the step being worked on.
            var scrollablePlanTurn = twoPlanSession.Messages[taskPlanIndex];
            twoPlanSession.Messages[taskPlanIndex] = scrollablePlanTurn with
            {
                Plan = scrollablePlanTurn.Plan! with
                {
                    Tasks = Enumerable.Range(1, 10)
                        .Select(number => new PlanTask(number, "Step " + number + " of the long plan")).ToArray(),
                },
            };
            panel.Reload();
            var (taskExtent, taskViewport) = panel.PlanTaskListMeasureForCheck;
            // The cap is applied from the list's own LayoutUpdated, so the pass that builds the rows measures
            // without it. Keep laying out until the viewport is genuinely smaller than the extent.
            for (var pass = 0; pass < 4 && taskExtent <= taskViewport + 1; pass++)
            {
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                (taskExtent, taskViewport) = panel.PlanTaskListMeasureForCheck;
            }
            panel.ScrollPlanTasksForCheck(24);
            Dispatcher.UIThread.RunJobs();
            var scrolledTaskOffset = panel.PlanTaskScrollForCheck;
            var scrolledPlanTurn = twoPlanSession.Messages[taskPlanIndex];
            twoPlanSession.Messages[taskPlanIndex] = scrolledPlanTurn with
            {
                Plan = scrolledPlanTurn.Plan! with
                {
                    Tasks = scrolledPlanTurn.Plan!.Tasks!.Select(task => task with
                    {
                        State = task.Number == 9 ? PlanTaskStates.InProgress : PlanTaskStates.Pending,
                    }).ToArray(),
                },
            };
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(taskExtent > taskViewport + 1 && scrolledTaskOffset > 0
                  && Math.Abs(panel.PlanTaskScrollForCheck - scrolledTaskOffset) <= 1,
                "计划步骤改状态后任务清单停在原处，不把滚到第几步的人甩回第一条（偏移 "
                + Fmt(scrolledTaskOffset) + "→" + Fmt(panel.PlanTaskScrollForCheck) + "，extent "
                + Fmt(taskExtent) + " / viewport " + Fmt(taskViewport) + "）");
            panel.TogglePlanTasksForCheck();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.PlanTasksExpandedForCheck && panel.PlanTasksVisibleForCheck,
                "再次点击任务摘要行后收起清单且保留摘要卡");
            var updatedTaskPlanTurn = twoPlanSession.Messages[taskPlanIndex];
            var newTaskPlanTurn = updatedTaskPlanTurn with
            {
                Plan = updatedTaskPlanTurn.Plan! with
                {
                    Id = Guid.NewGuid().ToString("N"),
                },
            };
            twoPlanSession.Messages[taskPlanIndex] = newTaskPlanTurn;
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.PlanTasksExpandedForCheck && panel.PlanTasksVisibleForCheck,
                "同一会话中的新计划默认折叠");
            twoPlanSession.Messages[taskPlanIndex] = newTaskPlanTurn with
            {
                Plan = newTaskPlanTurn.Plan! with
                {
                    Tasks = newTaskPlanTurn.Plan!.Tasks!
                        .Select(task => task with { State = PlanTaskStates.Completed }).ToArray(),
                },
            };
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.PlanTasksVisibleForCheck,
                "计划任务全部完成后收起任务卡，避免完成的清单继续占用 composer 上方空间");
            twoPlanSession.Messages[taskPlanIndex] = taskPlanTurn with
            {
                PlanApprovalState = PlanApprovalStates.Approved,
            };
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(!panel.PlanTasksVisibleForCheck && panel.ComposerBandGapForCheck <= 18,
                "运行结束后旧计划即使保留未完成条目也不常驻输入区，横排与输入框之间没有空卡片占位");
            twoPlanSession.Messages[taskPlanIndex] = taskPlanTurn;
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            await WaitUntilAsync(() => panel.DecisionHostFocusedForCheck);
            Check(panel.DecisionHostFocusedForCheck,
                "空着输入框时计划卡到达就拿到键盘，直接操作不需要先选择再继续");
            // No need to click the revision affordance: a picture alone goes through the ordinary send path.
            panel.AddImageBytesForCheck(PictureFixture(), "another-direction.png");
            await panel.BeginSendForCheckAsync("");
            // A draft typed while the revision is being thought about: the second plan lands on top of it, and this
            // is the state the report was about — a card arriving must not clear the box or move the caret.
            const string draftUnderPlan = "计划还等着，我先把这句写完";
            panel.SetInputForCheck(draftUnderPlan);
            await WaitForIdleAsync(chat);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.PlanCardCountForCheck == 2
                  && panel.PlanCardTextsForCheck[0].Contains("First draft", StringComparison.Ordinal)
                  && panel.PlanCardTextsForCheck[1].Contains("Second draft", StringComparison.Ordinal),
                "两份计划是两张卡，各自的标题就是自己那份的标题（实际 "
                + string.Join(" / ", panel.PlanCardTextsForCheck) + "）");
            // The other half of the arrival above: a plan that turns up while a sentence is being written leaves the
            // keyboard where it was. Nothing here needs the caret to answer — the card is a row above the box, not a
            // cover over it.
            Check(panel.InputTextForCheck == draftUnderPlan && !panel.DecisionHostFocusedForCheck,
                "框里正写着字时新计划到达，不抢键盘也不清空那半句（框里「" + panel.InputTextForCheck + "」）");
            var pictureOnlyRevision = twoPlanSession.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User);
            Check(pictureOnlyRevision is { Text.Length: 0, Images.Count: 1, Plan.File.Length: > 0 }
                  && chat.ActiveMode == ChatModes.Plan && panel.PendingPlanApprovalCardsForCheck == 1,
                "只发图片也关联原计划，新回复仍然等待批准，没有切到实施模式");
            // Simulate a replacement while the person is writing. Reusing the same turn index must not let
            // old feedback bind to the new document; this is distinct from cancellation removing the card.
            var replacedIndex = twoPlanSession.Messages.FindIndex(turn => turn.PlanApprovalState == PlanApprovalStates.Pending);
            var replaced = twoPlanSession.Messages[replacedIndex];
            twoPlanSession.Messages[replacedIndex] = replaced with { Plan = replaced.Plan! with { Id = Guid.NewGuid().ToString("N") } };
            panel.PressComposerKeyForCheck(Key.Enter);
            Dispatcher.UIThread.RunJobs();
            Check(panel.InputTextForCheck == draftUnderPlan && panel.NoticeVisibleForCheck
                  && panel.LastNoticeTextForCheck == HubStrings.Get("ChatPlanChanged")
                  && panel.PendingPlanApprovalCardsForCheck == 1 && chat.RunFor(twoPlanSession.Id) is null,
                "同一位置换了计划后 Enter 拒绝旧草稿，不误关联新文档、不丢文字（提示「"
                + panel.LastNoticeTextForCheck + "」）");
            panel.SetInputForCheck("");
            panel.ClickPlanCardForCheck(0);
            Check(shell.InspectorPlanMarkdownForCheck.Contains("The older body", StringComparison.Ordinal),
                "点旧的那张计划卡，右栏显示的是那一份计划而不是最新的一份（面板里 "
                + shell.InspectorPlanMarkdownForCheck.Replace('\n', '|') + "）");
            panel.ClickPlanCardForCheck(1);
            Check(shell.InspectorPlanMarkdownForCheck.Contains("The newer body", StringComparison.Ordinal),
                "点新的那张就换到新的一份：卡片与面板说的始终是同一份文档");

            // A thinking model's plan is the shape this change creates: reasoning on the turn, and no text once
            // the body is filed. That is exactly the silhouette the transcript folds away as machine work, and a
            // review card buried inside a collapsed 「Thought for Ns」 is a decision nobody is ever asked to make.
            var thinkingPlan = chat.StartConversation();
            chat.OpenConversation(thinkingPlan.Id);
            chat.SeedPlanReplyForCheck(thinkingPlan.Id, plan, "先想清楚要动哪几个文件");
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(chat.StoredCopyForCheck(thinkingPlan.Id)?.Messages
                      .LastOrDefault(turn => turn.PlanApprovalState == PlanApprovalStates.Pending)
                  is { Text.Length: 0, Reasoning.Length: > 0, Plan.File.Length: > 0 } thoughtPlan
                  && panel.PlanCardCountForCheck == 1
                  && panel.PlanCardOnScreenForCheck(0),
                "正文进了文件、带着思考的计划仍然是转录里一张看得见、点得开的卡，没有被折进「Thought for Ns」"
                + "（卡面「" + string.Join(" / ", panel.PlanCardTextsForCheck) + "」）");
            chat.DeleteConversation(thinkingPlan.Id);
            // Deleting the session that was on screen leaves the panel with no transcript, and every card pressed
            // below would press nothing — the assertion would read as a missing plan rather than as a missing view.
            chat.OpenConversation(twoPlanSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // The store is bounded, so an older plan's document can be gone while its turn stays in the
            // transcript. Neither surface gets to pretend: the column names the file that is missing instead of
            // falling back to 「还没有生成计划」, and approving is refused rather than answered with an
            // instruction that has nothing under it.
            var newestPlanIndex = -1;
            for (var index = twoPlanSession.Messages.Count - 1; index >= 0; index--)
                if (twoPlanSession.Messages[index].PlanApprovalState == PlanApprovalStates.Pending)
                {
                    newestPlanIndex = index;
                    break;
                }
            var goneTurn = newestPlanIndex >= 0 ? twoPlanSession.Messages[newestPlanIndex] : null;
            var missingName = goneTurn?.Plan?.File ?? "";
            var missingChars = goneTurn?.Plan?.Chars ?? 0;
            if (chat.PlansDirectoryForCheck is { } plansDir && missingName.Length > 0)
            {
                var missingPath = System.IO.Path.Combine(plansDir, missingName);
                if (System.IO.File.Exists(missingPath)) System.IO.File.Delete(missingPath);
            }
            shell.CloseInspector();
            // Closing the column changes transcript width and realizes its cards on the next layout pass.
            // Do not click a not-yet-realized card: that presses nothing and leaves the old plan in the pane.
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            panel.ClickPlanCardForCheck(1);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(missingName.Length > 0
                  && shell.InspectorPlanMarkdownForCheck.Contains(missingName, StringComparison.Ordinal)
                  && shell.InspectorPlanMarkdownForCheck != HubStrings.Get("InspectorPlanEmpty")
                  && !shell.InspectorPlanMarkdownForCheck.Contains("The newer body", StringComparison.Ordinal)
                  && !shell.InspectorPlanMarkdownForCheck.Contains("The older body", StringComparison.Ordinal),
                "正文文件被淘汰之后，右栏点名是哪一个文件不在了，既不谎称「还没有生成计划」也不去念另一份（面板里 "
                + shell.InspectorPlanMarkdownForCheck.Replace('\n', '|') + "；缺失文件 " + missingName
                + "，面板打开 " + shell.InspectorVisibleForCheck + "，卡片数 " + panel.PlanCardCountForCheck + "）");
            var turnsBeforeGone = twoPlanSession.Messages.Count;
            var refusedGone = !chat.TryResolvePlanApproval(twoPlanSession.Id, newestPlanIndex,
                PlanApprovalStates.Approved, out var goneRefusal);
            Check(refusedGone && goneRefusal == "ChatPlanBodyGone"
                  && HubStrings.Get(goneRefusal!) != goneRefusal
                  && twoPlanSession.Messages.Count == turnsBeforeGone
                  && goneTurn?.PlanApprovalState == PlanApprovalStates.Pending,
                "读不到正文的计划：批准被拒绝并给出理由（" + missingChars + " 字的 " + missingName
                + "），不追加任何用户指令，卡片留在原处（拒绝键 " + (goneRefusal ?? "无") + "）");

            // A plan carried over from a previous run: the session file names a document, and those two files are
            // all a later shell gets. Copied the way a data root moves — the JSON and the .md, nothing else — and
            // read back by a window that never saw a model produce either.
            var carriedName = approvedSession.Messages
                .LastOrDefault(turn => turn.PlanApprovalState is not null)?.Plan?.File ?? "";
            var carriedJson = chat.StoredSessionTextForCheck(approvedSession.Id) ?? "";
            var carriedRoot = ScratchDirectory.Resolve("plan-carried");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(carriedRoot, "ai", "sessions"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(carriedRoot, "ai", "plans"));
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(carriedRoot, "ai", "sessions", approvedSession.Id + ".json"), carriedJson);
            var carriedSource = System.IO.Path.Combine(chat.PlansDirectoryForCheck ?? "", carriedName);
            if (System.IO.File.Exists(carriedSource))
                System.IO.File.Copy(carriedSource, System.IO.Path.Combine(carriedRoot, "ai", "plans", carriedName));
            var carriedPrefs = new PreferencesStore(PreferencesPathFor(carriedRoot));
            var carriedShell = new MainWindow(carriedRoot, carriedPrefs, carriedPrefs.Load());
            carriedShell.Show();
            carriedShell.NavigateTo("Assistant");
            carriedShell.Chat.OpenConversation(approvedSession.Id);
            carriedShell.OpenInspector("plan", -1);
            carriedShell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(carriedName.Length > 0
                  && carriedShell.InspectorPlanMarkdownForCheck
                      .Contains("First, inspect the implementation", StringComparison.Ordinal)
                  && carriedJson.Contains(carriedName, StringComparison.Ordinal)
                  && !carriedJson.Contains("First, inspect the implementation", StringComparison.Ordinal),
                "换一根数据根重开之后，只凭会话 JSON 里的那个文件名就把计划读回屏幕上，而 JSON 自始至终没有正文（面板 "
                + carriedShell.InspectorPlanMarkdownForCheck.Length + " 字，文件 " + carriedName + "）");
            carriedShell.Close();
        }
        finally
        {
            chat.ClientOverride = null;
            chat.IdleTimeout = savedIdleTimeout;
            chat.FirstFrameTimeout = savedFirstFrameTimeout;
            chat.AttentionRequired -= CountAttention;
            foreach (var session in new[] { observer, approvedSession, rejectedSession, twoPlanSession })
                chat.DeleteConversation(session.Id);
            await WaitForIdleAsync(chat);
            panel.Reload();
            shell.NavigateTo("Assistant");
            Dispatcher.UIThread.RunJobs();
            Check(chat.RunningCount == 0,
                "自检清理：计划审批夹具没有留下运行（实际 " + chat.RunningCount + " 路）");
        }
    }

    internal async Task CheckAskUserAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var savedIdleTimeout = chat.IdleTimeout;
        var savedFirstFrameTimeout = chat.FirstFrameTimeout;
        var savedPreferencesProvider = chat.PreferencesProvider;
        var observer = StartAgentSession(chat);
        var cardSession = StartAgentSession(chat);
        var skipSession = StartAgentSession(chat);
        var owedSession = StartAgentSession(chat);
        var longSession = StartAgentSession(chat);
        var questionArguments = new Dictionary<string, object?>
        {
            ["question"] = "重试逻辑放在哪一层？",
            ["options"] = new object?[]
            {
                new Dictionary<string, object?> { ["label"] = "放在调用方 (Recommended)" },
                new Dictionary<string, object?> { ["label"] = "放在传输层", ["detail"] = "覆盖所有调用方，但会改公共签名。" },
                new Dictionary<string, object?> { ["label"] = "抽一个独立的 helper" },
                new Dictionary<string, object?> { ["label"] = "这一轮先不做" },
            },
        };
        var cardClient = new ApprovalChatClient
        {
            ToolName = AskUserTool.ToolName,
            Arguments = questionArguments,
        };
        var skipClient = new ApprovalChatClient
        {
            ToolName = AskUserTool.ToolName,
            Arguments = questionArguments,
        };
        // One response, two questions. A model is asked for one call per reply and does not always agree, and the
        // only honest way to show that the second one is not lost is to answer the first and watch the run park
        // again on the card that is still owed.
        var owedClient = new ApprovalChatClient
        {
            // The batch answers once and is then cleared, and the fixture's own default would have the second
            // request ask a third question — which would leave the run parked and read as a resume that never
            // happened. Zero here is what makes "the reply went on" a claim rather than an artifact.
            CallsRemaining = 0,
            Batch =
            [
                (AskUserTool.ToolName, new Dictionary<string, object?>(questionArguments)),
                (AskUserTool.ToolName, new Dictionary<string, object?>
                 {
                     ["question"] = "那超时呢？",
                     ["options"] = new object?[]
                     {
                         new Dictionary<string, object?> { ["label"] = "跟重试放在一起" },
                         new Dictionary<string, object?> { ["label"] = "单独配" },
                     },
                 }),
            ],
        };
        // The card draws a bounded head of everything it did not author and keeps the whole of it in a tip. Both
        // halves are asserted below, because either one alone is a lie: text thrown away before the card saw it
        // leaves a tip with nothing to show, and a cut with no tip presents a truncated sentence as the sentence.
        var longQuestion = new string('问', AskUserQuestion.QuestionLimit + 20);
        var longLabel = new string('长', AskUserQuestion.LabelLimit + 30);
        // The two halves of the detail's bargain. `longDetail` is under the limit, so the row owes the whole of it
        // on screen — wrapping onto a second line is the only way it fits, and a row that drew one line would be
        // clipping it. `overlongDetail` is past the limit, where the row draws the head and the tip carries the rest.
        var longDetail = new string('描', 120);
        var overlongDetail = new string('细', AskUserQuestion.DetailLimit + 20);
        var longClient = new ApprovalChatClient
        {
            ToolName = AskUserTool.ToolName,
            Arguments = new Dictionary<string, object?>
            {
                ["question"] = longQuestion,
                ["options"] = new object?[]
                {
                    new Dictionary<string, object?> { ["label"] = longLabel, ["detail"] = longDetail },
                    new Dictionary<string, object?> { ["label"] = "短的那一项", ["detail"] = overlongDetail },
                },
            },
        };
        chat.IdleTimeout = TimeSpan.FromSeconds(5);
        // Both windows: a card that parks is armed with the round's window now, and a fixture that lets
        // only the silence one expire would sit on the default until the suite's own ceiling.
        chat.FirstFrameTimeout = TimeSpan.FromSeconds(5);
        try
        {
            chat.ClientOverride = (_, conversationId) => conversationId switch
            {
                var id when id == cardSession.Id => cardClient,
                var id when id == skipSession.Id => skipClient,
                var id when id == owedSession.Id => owedClient,
                var id when id == longSession.Id => longClient,
                _ => new ScriptedChatClient(["不该被使用"]),
            };

            // The axis this whole change turns on: a session with the loosest permission setting still stops for
            // a question. Asserted through the real gate rather than the policy table, because the table cell
            // cannot tell "the tier is read before the mode" from "this call never reached the tier".
            chat.OpenConversation(cardSession.Id);
            Check(chat.SetApprovalMode(cardSession.Id, ToolApprovalModes.Full)
                  && chat.ApprovalModeFor(cardSession.Id) == ToolApprovalModes.Full,
                "夹具会话确实跑在完全访问下（实际「" + chat.ApprovalModeFor(cardSession.Id) + "」）");
            chat.TryEnqueueSend(cardSession.Id, "重试这块我拿不准", null, out _);
            chat.OpenConversation(observer.Id);
            await WaitForIdleAsync(chat);
            var parkedCallId = cardSession.Messages.LastOrDefault(
                turn => turn.ToolName == AskUserTool.ToolName)?.ToolCallId;
            Check(parkedCallId is not null
                  && cardSession.Messages.Any(turn => turn.ToolCallId == parkedCallId
                      && turn.ApprovalState == ChatApprovalStates.Pending)
                  && cardSession.Messages.Count(turn => turn.Role == ChatRoles.Tool) == 0,
                "完全访问模式下模型的提问照样停住，且没有替它写出结果（停在 " + cardSession.Messages.Count + " 条）");

            chat.OpenConversation(cardSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var placement = panel.DecisionHostPlacementForCheck;
            Check(panel.QuestionChoicesForCheck.SequenceEqual(["A", "B", "C", "D", "free"])
                  && panel.QuestionHasSkipForCheck && panel.QuestionTitleForCheck.Contains("重试逻辑放在哪一层", StringComparison.Ordinal)
                  && placement is not null && placement.Value.BottomRelativeToFrame <= 0,
                "提问卡排在输入框上方、与输入框同列：四个字母选项加一行自己写的，页脚是跳过与继续（实际 "
                + string.Join("/", panel.QuestionChoicesForCheck) + "，卡底相对输入框 "
                + (placement?.BottomRelativeToFrame ?? double.NaN) + "）");
            // The host clips at its own ceiling, and four options under a wrapped question is the far end of the
            // range. A card that pushed 「继续」 outside would leave the run parked forever with no way out but
            // another message, so the footer's own bounds are what gets read, not the class names on it.
            Check(panel.QuestionCardHeightForCheck > 0 && panel.QuestionContinueInsideHostForCheck
                  && panel.ComposerInputEnabledForCheck,
                "四个选项时继续按钮仍在卡片内、没被裁掉，输入框也照旧可用（卡高 "
                + panel.QuestionCardHeightForCheck + "）");
            Check(!panel.QuestionContinueEnabledForCheck,
                "什么都没选、也没写字时继续是禁用的：一次看起来只是点了个框的操作不该花掉一次请求");
            // The cap is a shortcut, and a shortcut that fires while somebody is typing in the line beside it is
            // an interruption of exactly the kind the charter forbids above the composer.
            panel.ClickQuestionChoiceForCheck("free");
            panel.SetQuestionFreeTextForCheck("都行，你定");
            Check(!panel.PressQuestionCardKeyForCheck(Key.A, inFreeLine: true)
                  && panel.QuestionSelectedChoiceForCheck == "free"
                  && panel.QuestionFreeTextForCheck == "都行，你定"
                  && panel.QuestionContinueEnabledForCheck,
                "光标在自己的答案行里时 A 是字母：不换选中项、不吞已写的字，而写进内容之后继续活了");
            // The other half of the same rule: the line is armed by the caret, but it is the text that makes the
            // answer worth sending. An armed-and-empty line must not be able to spend a request.
            panel.SetQuestionFreeTextForCheck("");
            Check(!panel.QuestionContinueEnabledForCheck,
                "把答案行清空之后继续又禁用了，选中项还停在这一行");
            Check(panel.QuestionOffersAlwaysAllowForCheck == false,
                "提问卡上没有「以后都不问」：一次点击不该把后来所有的问题都消音");
            panel.ExpandVirtualGroupsForCheck();
            Relayout(shell, panel, measure: true);
            Check(panel.TranscriptWaitingLinesForCheck.Any(
                      line => line.Contains("等你回答", StringComparison.Ordinal))
                  && !panel.TranscriptWaitingLinesForCheck.Any(
                      line => line.Contains("等你批准", StringComparison.Ordinal)),
                "转录里的待回答问题写的是等你回答，不是等你批准（实际 "
                + string.Join(" / ", panel.TranscriptWaitingLinesForCheck) + "）");

            panel.SetQuestionFreeTextForCheck("");
            panel.ClickQuestionChoiceForCheck("B");
            Check(panel.QuestionSelectedChoiceForCheck == "B" && panel.QuestionContinueEnabledForCheck,
                "点 B 就是选中 B，继续跟着活了（实际停在 " + panel.QuestionSelectedChoiceForCheck + "）");
            panel.ClickQuestionContinueForCheck();
            await WaitForIdleAsync(chat);
            var answered = chat.StoredCopyForCheck(cardSession.Id);
            var answerAt = answered?.Messages.FindIndex(turn => turn.Role == ChatRoles.Tool
                && turn.ToolCallId == parkedCallId) ?? -1;
            Check(answerAt > 0
                  && answered!.Messages[answerAt - 1].ToolCallId == parkedCallId
                  && answered.Messages[answerAt].Text
                      == "The user answered \"B: 放在传输层\" to \"重试逻辑放在哪一层？\"."
                  && answered.Messages[answerAt].Role == ChatRoles.Tool
                  && answered.Messages.Any(turn => turn.Role == ChatRoles.Assistant
                      && turn.Text == ApprovalChatClient.Answer),
                "选中一项再点继续，回答原样写回这一次调用旁边，回复接着往下说（结果落在第 " + answerAt + " 条）");
            Check(chat.RunFor(cardSession.Id) is null,
                "答完之后这条会话不再有挂着的运行");

            // Escape is the quiet no. It has to land as "you decide", not as the refusal sentence — that one ends
            // by telling the model to go ask the user what to do, which is the question that was just skipped.
            chat.OpenConversation(skipSession.Id);
            chat.TryEnqueueSend(skipSession.Id, "再问一次", null, out _);
            await WaitForIdleAsync(chat);
            chat.OpenConversation(skipSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var skipCallId = skipSession.Messages.LastOrDefault(
                turn => turn.ToolName == AskUserTool.ToolName)?.ToolCallId;
            // Pressed on the card, not in the composer: a caret in the input box outranks the card by the
            // charter's own rule, so Escape there is not an answer to the question and must stay that way.
            panel.PressQuestionCardKeyForCheck(Key.Escape);
            await WaitForIdleAsync(chat);
            var skipped = chat.StoredCopyForCheck(skipSession.Id);
            Check(skipCallId is not null
                  && skipped?.Messages.Any(turn => turn.Role == ChatRoles.Tool
                      && turn.ToolCallId == skipCallId
                      && turn.Text == ToolApprovalResults.QuestionSkipped) == true
                  && skipped.Messages.All(turn => turn.Text != ToolApprovalResults.Denied),
                "Esc 是跳过不是拒绝：模型收到的是「你自己定」，不是那句「去问用户该怎么办」（那会变成一个重问循环）");

            // Two questions owed by one response. The second is invisible until the first is answered — that is
            // the one-slot host — so what proves it is not lost is the run parking again on it.
            chat.OpenConversation(owedSession.Id);
            chat.TryEnqueueSend(owedSession.Id, "两件都要问", null, out _);
            await WaitForIdleAsync(chat);
            chat.OpenConversation(owedSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var owedCalls = owedSession.Messages
                .Where(turn => turn.ToolName == AskUserTool.ToolName && turn.ToolCallId is not null)
                .Select(turn => turn.ToolCallId!).ToArray();
            panel.ClickQuestionChoiceForCheck("A");
            panel.ClickQuestionContinueForCheck();
            await WaitForIdleAsync(chat);
            Dispatcher.UIThread.RunJobs();
            Check(owedCalls.Length == 2
                  && owedSession.Messages.Any(turn => turn.ToolCallId == owedCalls[1]
                      && turn.ApprovalState == ChatApprovalStates.Pending)
                  && panel.QuestionTitleForCheck.Contains("那超时呢", StringComparison.Ordinal)
                  && chat.RunFor(owedSession.Id) is { } stillParked && !chat.IsRunning(owedSession.Id),
                "两个问题挂在一起时答完一个，另一个接着从卡上起来，运行仍然停在那张欠着的卡上（实际 "
                + panel.QuestionTitleForCheck + "）");
            panel.ClickQuestionChoiceForCheck("A");
            panel.ClickQuestionContinueForCheck();
            await WaitForIdleAsync(chat);
            Check(owedSession.Messages.Count(turn => turn.Role == ChatRoles.Tool) == 2
                  && chat.RunFor(owedSession.Id) is null,
                "第二个答完之后两条结果都在，运行收尾（实际 "
                + owedSession.Messages.Count(turn => turn.Role == ChatRoles.Tool) + " 条）");

            // Long text. The question and the label each had to be drawn short to keep the card at the height its
            // layout was built on, and the whole of each is one hover away — the row is the head, the tip is the
            // sentence. The short label is the negative half: a tip that repeated the line under the pointer would
            // be chrome, and it is what proves the tip is a fallback rather than something every row wears.
            // The detail is where the two halves meet the paint: below the limit nothing may be thrown away, so A's
            // detail has to be the whole string *and* arrive on more than one line, while B's past-the-limit one
            // draws the head and hands the rest to its tip. Read as geometry as well as text, because a row that was
            // never given a width draws one line and lets the card clip it — which reads as a short sentence on
            // screen and as a green assertion everywhere that only looks at the string.
            chat.OpenConversation(longSession.Id);
            chat.TryEnqueueSend(longSession.Id, "问个长的", null, out _);
            await WaitForIdleAsync(chat);
            chat.OpenConversation(longSession.Id);
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(panel.QuestionLabelForCheck("A").EndsWith('…')
                  && panel.QuestionLabelTipForCheck("A") == longLabel
                  && panel.QuestionLabelTipForCheck("B").Length == 0
                  && panel.QuestionTitleForCheck.EndsWith('…')
                  && panel.QuestionTitleTipForCheck == longQuestion
                  && panel.QuestionDetailForCheck("A") == longDetail
                  && panel.QuestionDetailLinesForCheck("A") > 1
                  && panel.QuestionDetailTipForCheck("A").Length == 0
                  && panel.QuestionDetailTipForCheck("B") == overlongDetail
                  && panel.QuestionDetailLinesForCheck("B") > 1
                  && panel.QuestionRowsInsideCardForCheck,
                "长的问题与选项画得下多少画多少、画不下的整段放进提示，短的那一项不挂提示，预算以内的描述整段换行画出来（卡上 "
                + panel.QuestionLabelForCheck("A").Length + " 字，提示 "
                + panel.QuestionLabelTipForCheck("A").Length + " 字；描述 "
                + panel.QuestionDetailForCheck("A").Length + " 字，"
                + panel.QuestionDetailLayoutForCheck("A") + "；" + panel.QuestionRowsGeometryForCheck + "）");
            // The card may grow to fit the wrapped rows — the list is the row that gives way at the host's ceiling —
            // but it may not grow past it: the footer is the only way out of a parked run, so its bounds are read
            // again here, at the far end of the range, and not only on the four short options above.
            Check(panel.QuestionCardHeightForCheck > 0 && panel.QuestionContinueInsideHostForCheck,
                "长提问换行撑高之后卡片仍收在宿主的上限里，「继续」没有被裁到卡外（卡高 "
                + panel.QuestionCardHeightForCheck + "）");
            // The frame itself, for whoever reads the report: the numbers above say the detail wrapped, and this is
            // the picture of the row growing instead of the card cutting the sentence. No assertion rides on it —
            // it is the same artifact the code-block and diff groups leave in tmp/.
            if (panel.QuestionCardForCheck is { } longCard)
                SmokeCapture.Capture(longCard, System.IO.Path.Combine(ScratchDirectory.Resolve("ask-user-card"), "long-detail.png"));
            panel.ClickQuestionChoiceForCheck("B");
            panel.ClickQuestionContinueForCheck();
            await WaitForIdleAsync(chat);
            Check(chat.RunFor(longSession.Id) is null,
                "长提问答完之后这条会话也不再挂着运行");

            // The other axis: switched off means the model is never told the capability exists. Asserted on the
            // declaration list rather than on a parked call, because a tool that is declared and then ignored
            // would still let the model ask into a void — and that is the failure this cell exists to catch.
            var offScope = new ChatToolScope(chat.HubSnapshotProvider?.Invoke(),
                new WorkspaceToolScope(null, new WorkspaceGuards(null, []), null, "ask-off", [], null, null),
                AskUserEnabled: false);
            var onScope = new ChatToolScope(chat.HubSnapshotProvider?.Invoke(),
                new WorkspaceToolScope(null, new WorkspaceGuards(null, []), null, "ask-on", [], null, null));
            Check(offScope.AskUserEnabled == false && onScope.AskUserEnabled
                  && new[] { ChatModes.Plan, ChatModes.Agent }.All(mode =>
                      !ChatTools.CreateFor(mode, offScope).Any(tool => tool.Name == AskUserTool.ToolName)
                      && ChatTools.CreateFor(mode, onScope).Any(tool => tool.Name == AskUserTool.ToolName))
                  && !ChatTools.CreateFor(ChatModes.Ask, onScope).Any(tool => tool.Name == AskUserTool.ToolName),
                "「遇到选择时问我」关掉以后，计划与 Agent 的工具列表里根本没有 ask_user —— 不是问了没人答（询问档本来就不给工具）");
        }
        finally
        {
            chat.ClientOverride = null;
            chat.IdleTimeout = savedIdleTimeout;
            chat.FirstFrameTimeout = savedFirstFrameTimeout;
            chat.PreferencesProvider = savedPreferencesProvider;
            foreach (var session in new[] { observer, cardSession, skipSession, owedSession, longSession })
                chat.DeleteConversation(session.Id);
            await WaitForIdleAsync(chat);
            panel.Reload();
            shell.NavigateTo("Assistant");
            Dispatcher.UIThread.RunJobs();
            Check(chat.RunningCount == 0,
                "自检清理：提问夹具没有留下运行（实际 " + chat.RunningCount + " 路）");
        }
    }
}
