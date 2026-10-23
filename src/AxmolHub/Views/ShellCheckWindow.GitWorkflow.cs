using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AxmolHub.Core;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;

internal sealed class ShellGitWorkflowChecks(CheckReport report, Action<bool, string> check)
{
    internal async Task VerifyAsync()
    {
        if (CommandShells.Resolve("git") is not { } git)
        {
            report.Skip("Git 不可用，跳过 Git 工作流重入检查");
            return;
        }

        var root = ScratchDirectory.Resolve("git-workflow-boundary", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "repository");
        Directory.CreateDirectory(workspace);
        var runner = new ProcessRunner(_ => { });
        async Task<ProcessResult> Git(params string[] arguments)
            => await runner.RunAsync(git, arguments, workspace);

        try
        {
            // Run logs intentionally write into the workspace. Ignore that owned fixture memory so the clean
            // repository case measures Git re-entry, not the logging feature leaving a new file behind.
            await File.WriteAllTextAsync(Path.Combine(workspace, ".gitignore"), ".agents/\n");
            foreach (var arguments in new[]
                     {
                         new[] { "init", "-b", "main" },
                         new[] { "config", "user.name", "Hub workflow check" },
                         new[] { "config", "user.email", "hub-check@example.invalid" },
                         new[] { "config", "commit.gpgsign", "false" },
                         new[] { "add", "--", ".gitignore" },
                         new[] { "-c", "core.hooksPath=", "commit", "-m", "misc: create fixture" },
                     })
            {
                var result = await Git(arguments);
                if (result.ExitCode != 0)
                {
                    check(false, "夹具：Git 工作流仓库无法初始化：" + result.Error);
                    return;
                }
            }

            using var chat = new ChatWorkspace(Path.Combine(root, "hub"))
            {
                HubSnapshotProvider = () => new ChatWorkspace.HubReadOnlySnapshot([], [], []),
            };
            var provider = chat.AddProvider("Git workflow check", "http://localhost:1234/v1", "offline-check-model", null);
            if (provider is null)
            {
                check(false, "夹具：Git 工作流离线模型无法创建");
                return;
            }

            var requests = 0;
            var completions = 0;
            chat.RunCompleted += (_, _) => completions++;
            var failures = new List<string>();
            var observations = new List<string>();
            const string title = "fix: repair workflow boundary";
            var fixtures = new[]
            {
                (Name: "普通回答", Request: "解释这个函数", Answer: "This is an explanation.", Summary: (string?)null),
                (Name: "无效摘要", Request: "完成修复", Answer: "Done.\nCommit summary: unknown: rejected", Summary: (string?)null),
                (Name: "提交结果回复", Request: "报告提交结果", Answer: "Committed the requested changes; the worktree is clean.", Summary: (string?)null),
                (Name: "有效摘要", Request: "完成修复", Answer: "Done.\nCommit summary: " + title, Summary: (string?)title),
            };
            var beforeHead = (await Git("rev-parse", "HEAD")).Output;
            await File.WriteAllTextAsync(Path.Combine(workspace, "sentinel.txt"), "Do not stage or commit without a click.\n");
            Conversation? suggestion = null;
            foreach (var fixture in fixtures)
            {
                var session = chat.StartConversation(provider.Id);
                chat.SetWorkspaceRoot(session.Id, workspace);
                chat.SelectMode(ChatModes.Agent);
                var beforeRequests = requests;
                var beforeCompletions = completions;
                // Stop a regressed loop at its second request so a failure does not exhaust the suite's ceiling.
                chat.ClientOverride = (_, _) => ++requests == beforeRequests + 1
                    ? new ScriptedChatClient([fixture.Answer])
                    : new ScriptedChatClient([], exception: new InvalidOperationException("Unexpected automatic Git fallback"));
                var sent = chat.TryEnqueueSend(session.Id, fixture.Request, null, out var refusal);
                await WaitForIdleAsync(chat);
                Dispatcher.UIThread.RunJobs();
                var stored = chat.StoredCopyForCheck(session.Id);
                var final = stored?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.Assistant);
                if (!sent || chat.RunningCount != 0 || requests != beforeRequests + 1
                    || completions != beforeCompletions + 1 || stored?.Messages.Count != 2
                    || final?.CommitSummary != fixture.Summary || final?.RunElapsed is null)
                    failures.Add(fixture.Name + ": sent=" + sent + ", refusal=" + refusal
                        + ", messages=" + stored?.Messages.Count + ", notice=" + stored?.LastNotice?.Key
                        + ", detail=" + stored?.LastNotice?.Detail + ", final=" + final?.Text);
                observations.Add(fixture.Name + "请求=" + (requests - beforeRequests));
                if (fixture.Summary is not null) suggestion = stored;
            }
            if ((await Git("rev-parse", "HEAD")).Output != beforeHead
                || !(await Git("status", "--porcelain")).Output.Contains("?? sentinel.txt", StringComparison.Ordinal)
                || (await Git("diff", "--cached", "--stat")).Output.Length != 0)
                failures.Add("回复结束自动修改了仓库或暂存区");

            if (suggestion?.Messages.LastOrDefault() is not { } suggestedReply)
            {
                check(false, "夹具：Git 工作流未生成可点击摘要");
                return;
            }

            // Exercise the real button handoff: no upstream makes local push fail, then normal Agent takes over.
            // The fallback deliberately returns another valid marker: it must remain terminal, with no new button.
            chat.SeedGoalForCheck(suggestion.Id, "完成这个目标");
            var beforeFallback = requests;
            chat.ClientOverride = (_, _) => ++requests == beforeFallback + 1
                ? new ScriptedChatClient(["Git operation finished.\nCommit summary: " + title])
                : new ScriptedChatClient([], exception: new InvalidOperationException("Recursive fallback"));
            chat.TryEnqueueSend(suggestion.Id, "push", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(suggestion.Id)?.Messages
                .LastOrDefault()?.Role == ChatRoles.Notice);
            await WaitForIdleAsync(chat);
            Dispatcher.UIThread.RunJobs();
            var fallbackCopy = chat.StoredCopyForCheck(suggestion.Id);
            if (requests != beforeFallback + 1 || chat.RunningCount != 0
                || fallbackCopy?.Messages.Count != 7 || fallbackCopy.GoalState != ChatGoalStates.Paused
                || fallbackCopy.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)?.IsGitOperationFallback != true
                || fallbackCopy.Messages.LastOrDefault()?.CommitSummary is not null)
                failures.Add("Agent 兜底完成后重新进入本地工作流");
            observations.Add("显式推送兜底请求=" + (requests - beforeFallback));

            File.Delete(Path.Combine(workspace, "sentinel.txt"));
            var remote = Path.Combine(root, "remote.git");
            await Git("init", "--bare", remote);
            await Git("remote", "add", "origin", remote);
            await Git("push", "-u", "origin", "main");
            var beforeNoOp = requests;
            foreach (var action in new[] { "commit", "commit+push", "push" })
            {
                var beforeCount = chat.StoredCopyForCheck(suggestion.Id)!.Messages.Count;
                chat.TryEnqueueSend(suggestion.Id, action, null, out _);
                await WaitForConditionAsync(() => chat.StoredCopyForCheck(suggestion.Id)?.Messages.Count >= beforeCount + 2);
                await WaitForIdleAsync(chat);
            }
            if (requests != beforeNoOp || chat.RunningCount != 0 || (await Git("rev-parse", "HEAD")).Output != beforeHead)
                failures.Add("干净仓库提交触发了 AI 重试");
            observations.Add("干净仓库后续请求=" + (requests - beforeNoOp));

            // A real run_command result, not a scripted claim in the final answer, proves a model commit.
            var agentSession = chat.StartConversation(provider.Id);
            chat.SetWorkspaceRoot(agentSession.Id, workspace);
            chat.SelectMode(ChatModes.Agent);
            chat.SetApprovalMode(agentSession.Id, ToolApprovalModes.Full);
            var agentCommit = new ApprovalChatClient
            {
                ToolName = "run_command",
                Arguments = new() { ["command"] = "git commit --allow-empty -m 'misc: record agent result'" },
            };
            chat.ClientOverride = (_, _) => agentCommit;
            chat.TryEnqueueSend(agentSession.Id, "执行本轮提交任务", null, out _);
            await WaitForIdleAsync(chat);
            var committedReply = chat.StoredCopyForCheck(agentSession.Id)?.Messages.LastOrDefault();
            var agentCommitActions = committedReply?.GitActions ?? GitActions.None;
            var agentPush = new ApprovalChatClient
            {
                ToolName = "run_command", Arguments = new() { ["command"] = "git push" },
            };
            chat.ClientOverride = (_, _) => agentPush;
            chat.TryEnqueueSend(agentSession.Id, "执行本轮推送任务", null, out _);
            await WaitForIdleAsync(chat);
            var pushedReply = chat.StoredCopyForCheck(agentSession.Id)?.Messages.LastOrDefault();
            check(agentCommitActions == GitActions.Push && pushedReply?.GitActions == GitActions.None,
                "模型工具成功提交后只保留推送，推送成功后不显示 Git 操作（提交后=" + agentCommitActions
                + "，推送后=" + pushedReply?.GitActions + "）");

            // Exact text commands share the click implementation and do not call the model with a usable title.
            var localSession = chat.StartConversation(provider.Id);
            chat.SetWorkspaceRoot(localSession.Id, workspace);
            chat.SelectMode(ChatModes.Agent);
            chat.ClientOverride = (_, _) => { requests++; return new ScriptedChatClient(["Done.\nCommit summary: " + title]); };
            chat.TryEnqueueSend(localSession.Id, "完成新的修复", null, out _);
            await WaitForIdleAsync(chat);
            await File.WriteAllTextAsync(Path.Combine(workspace, "local.txt"), "locally committed\n");
            var beforeLocalRequests = requests;
            var beforeLocalHead = (await Git("rev-parse", "HEAD")).Output;
            var localMessages = chat.StoredCopyForCheck(localSession.Id)!.Messages.Count;
            chat.TryEnqueueSend(localSession.Id, "commit", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(localSession.Id)?.Messages.Count >= localMessages + 2);
            var afterLocalHead = (await Git("rev-parse", "HEAD")).Output;
            localMessages = chat.StoredCopyForCheck(localSession.Id)!.Messages.Count;
            chat.TryEnqueueSend(localSession.Id, "push", null, out _);
            await WaitForConditionAsync(() => chat.StoredCopyForCheck(localSession.Id)?.Messages.Count >= localMessages + 2);
            var upstream = await Git("rev-parse", "@{u}");
            check(requests == beforeLocalRequests && beforeLocalHead != afterLocalHead
                  && upstream.Output == afterLocalHead && chat.RunningCount == 0,
                "独立 commit 和 push 文本通过本地入口完成，不增加模型请求（额外请求=" + (requests - beforeLocalRequests) + "）");

            await VerifyRestoredButtonsAsync(root, workspace, provider.Id);

            // Existing headless checks only parse strings and run Git; they cannot observe the dispatcher pump
            // enqueueing another billable request after an otherwise successful reply. This is that boundary.
            check(failures.Count == 0,
                "回复结束只保留摘要展示元数据；Git 仅由显式操作触发，兜底及无更改结果不重入（"
                + string.Join("；", observations) + (failures.Count > 0 ? "；失败：" + string.Join("；", failures) : "") + "）");
        }
        catch (Exception ex)
        {
            check(false, "Git 工作流重入检查未完成：" + ex.Message);
        }
    }

    private async Task VerifyRestoredButtonsAsync(string root, string workspace, string providerId)
    {
        // Metadata-only checks missed the reported restart regression. Persist records without the new flags,
        // recreate ChatWorkspace, and inspect actual rendered buttons on a hosted ChatPanel.
        var store = new ConversationStore(Path.Combine(root, "hub"));
        var oldSummary = Conversation.Create(providerId);
        oldSummary.Mode = ChatModes.Agent;
        oldSummary.WorkspaceRoot = workspace;
        oldSummary.ModelName = "offline-check-model";
        oldSummary.Append(ChatTurn.User("完成修复"));
        oldSummary.Append(ChatTurn.Assistant("Done.\nCommit summary: fix: restore actions on reopen"));
        store.Save(oldSummary);
        var noTitle = Conversation.Create(providerId);
        noTitle.Mode = ChatModes.Agent;
        noTitle.WorkspaceRoot = workspace;
        noTitle.ModelName = "offline-check-model";
        noTitle.Append(ChatTurn.User("检查修复"));
        noTitle.Append(ChatTurn.Assistant("Task finished without a commit title.") with { RunElapsed = TimeSpan.FromSeconds(1) });
        store.Save(noTitle);
        await File.WriteAllTextAsync(Path.Combine(workspace, "reopened.txt"), "dirty after restart\n");
        var runner = new ProcessRunner(_ => { });
        if (CommandShells.Resolve("git") is not { } git) return;
        await runner.RunAsync(git, ["commit", "--allow-empty", "-m", "misc: prepare pending fixture commit"], workspace);

        using var reopened = new ChatWorkspace(Path.Combine(root, "hub"))
        {
            HubSnapshotProvider = () => new ChatWorkspace.HubReadOnlySnapshot([], [], []),
        };
        var requests = 0;
        reopened.ClientOverride = (_, _) => { requests++; return new ScriptedChatClient(["No commit was attempted."]); };
        var panel = new ChatPanel(reopened);
        var host = new Window { Width = 1000, Height = 720, Content = panel, Title = "Git restart verification" };
        host.Show();
        try
        {
            bool HasButton(string key) => panel.GetLogicalDescendants().OfType<Button>()
                .Any(button => button.Tag as string == key);
            void Layout()
            {
                panel.Reload();
                host.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            var observations = new List<string>();
            var passed = true;
            foreach (var session in new[] { oldSummary, noTitle })
            {
                reopened.OpenConversation(session.Id);
                await WaitForConditionAsync(() => reopened.StoredCopyForCheck(session.Id)?.Messages.LastOrDefault()?.GitActions.HasFlag(GitActions.Commit) == true);
                Layout();
                await WaitForConditionAsync(() => HasButton("CommitSummaryAction"));
                var hasCommit = HasButton("CommitSummaryAction");
                var hasCombined = HasButton("CommitPushSummaryAction");
                var hasPush = HasButton("PushSummaryAction");
                passed &= hasCommit && hasCombined && hasPush;
                observations.Add((session == oldSummary ? "旧记录" : "无摘要") + "提交=" + hasCommit
                    + "，组合=" + hasCombined + "，推送=" + hasPush);
            }
            passed &= requests == 0;

            // Missing title stays a user-click boundary: visible controls are not permission for an AI request.
            var button = panel.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(control => control.Tag as string == "CommitSummaryAction");
            button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForConditionAsync(() => requests == 1);
            await WaitForIdleAsync(reopened);
            passed &= button is not null && requests == 1
                && reopened.StoredCopyForCheck(noTitle.Id)?.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)?.IsGitOperationFallback == true;
            observations.Add("点击后模型请求=" + requests);

            File.Delete(Path.Combine(workspace, "reopened.txt"));
            var state = await GitRepository.ReadAsync(workspace, runner, includeDiffs: false);
            await GitRepository.PushAsync(workspace, state.Root!, runner);
            await reopened.RefreshRepositoryAsync(noTitle.Id);
            Layout();
            passed &= !HasButton("CommitSummaryAction") && !HasButton("CommitPushSummaryAction") && !HasButton("PushSummaryAction");
            observations.Add("干净后按钮=" + HasButton("CommitSummaryAction"));
            check(passed,
                "重启恢复旧会话与无摘要回复的真实按钮；仅点击才兜底，仓库刷新移除无改动操作（"
                + string.Join("；", observations) + "）");
        }
        finally
        {
            host.Close();
        }
    }
}
