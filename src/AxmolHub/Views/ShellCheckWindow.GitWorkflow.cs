using System.IO;
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
                (Name: "模型已提交", Request: "commit", Answer: "Committed the requested changes; the worktree is clean.", Summary: (string?)null),
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
            var beforeFallback = requests;
            chat.ClientOverride = (_, _) => ++requests == beforeFallback + 1
                ? new ScriptedChatClient(["Git operation finished.\nCommit summary: " + title])
                : new ScriptedChatClient([], exception: new InvalidOperationException("Recursive fallback"));
            await chat.PushSuggestedAsync(suggestion.Id, workspace, title, suggestedReply.At, commitFirst: false);
            await WaitForIdleAsync(chat);
            Dispatcher.UIThread.RunJobs();
            var fallbackCopy = chat.StoredCopyForCheck(suggestion.Id);
            if (requests != beforeFallback + 1 || chat.RunningCount != 0
                || fallbackCopy?.Messages.Count != 4
                || fallbackCopy.Messages.LastOrDefault(turn => turn.Role == ChatRoles.User)?.IsGitOperationFallback != true
                || fallbackCopy.Messages.LastOrDefault()?.CommitSummary is not null)
                failures.Add("Agent 兜底完成后重新进入本地工作流");
            observations.Add("显式推送兜底请求=" + (requests - beforeFallback));

            File.Delete(Path.Combine(workspace, "sentinel.txt"));
            var beforeNoOp = requests;
            await chat.CommitSuggestedAsync(suggestion.Id, workspace, title, suggestedReply.At);
            await chat.PushSuggestedAsync(suggestion.Id, workspace, title, suggestedReply.At, commitFirst: true);
            await WaitForIdleAsync(chat);
            if (requests != beforeNoOp || chat.RunningCount != 0 || (await Git("rev-parse", "HEAD")).Output != beforeHead)
                failures.Add("干净仓库提交触发了 AI 重试");
            observations.Add("干净仓库后续请求=" + (requests - beforeNoOp));

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
}
