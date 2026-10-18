using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>What the chat says while Hub is compressing its own context.
///
/// <para>Kept in its own file for the reason <c>ShellCheckWindow.ModelSwitch.cs</c> is: the file these checks
/// would otherwise grow is already the largest in the repository.</para></summary>
public partial class ShellCheckWindow
{
    /// <summary>Three facts and a fourth, one per surface and one per ending. A compaction is the only stretch of a
    /// run where nothing at all arrives — no token, no tool call — so the run's own status line is what a person is
    /// staring at when it happens, and it has to say what is happening rather than go on claiming tokens are
    /// coming. The button path has no bubble, so the strip below the transcript carries the same sentence there;
    /// and both surfaces give the line back when the summary lands, because a line nobody takes down reads as a
    /// session that is stuck.
    ///
    /// <para>Both brackets are held open by the fixture rather than raced for: the summary answer waits on a gate
    /// the check holds, so "compacting" is a state it chose and can read, not a millisecond it happened to
    /// catch.</para></summary>
    private async Task CheckCompactingStatusAsync(MainWindow shell, ChatPanel panel, string providerId)
    {
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var beforeOpen = chat.ActiveConversation?.Id;
        var compacting = HubStrings.Get("ChatCompactingContext");

        // ── The button path: no run at all, so the strip below the transcript is the only surface there is ──
        var idleGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = chat.StartConversation(providerId);
        for (var turn = 0; turn < 12; turn++)
            chat.SeedTurnForCheck(idle.Id, "第 " + turn + " 条普通对话内容");
        try
        {
            chat.ClientOverride = (_, _) => new ScriptedChatClient(["## 压缩任务的正常摘要"], gate: idleGate.Task);
            chat.OpenConversation(idle.Id);
            Relayout(shell, panel);
            var idleCompaction = chat.CompressContextAsync(idle.Id);
            // Both raises are posted rather than invoked, so the row exists only once the dispatcher has run.
            Dispatcher.UIThread.RunJobs();
            var rowText = panel.LastNoticeTextForCheck;
            Check(chat.IsCompressingContext(idle.Id) && rowText == compacting,
                "没有运行的时候压缩，聊天里也留一行「上下文压缩中…」（实际「" + (rowText ?? "无") + "」）");

            idleGate.SetResult(true);
            var idleLanded = await idleCompaction;
            Relayout(shell, panel);
            Check(idleLanded && !chat.IsCompressingContext(idle.Id) && !panel.NoticeVisibleForCheck,
                "压缩落地之后那一行自己退场，不留在转录里（成功=" + idleLanded + "，行上「"
                + (panel.LastNoticeTextForCheck ?? "无") + "」）");

            // ── A run in flight: the one line the person is already looking at ──
            // The first request is the reply, held before its first token; the second is the summary this
            // compaction sends. Held in that order by the fixture's own call counter rather than by a delay.
            var runGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var summaryGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            var streaming = chat.StartConversation(providerId);
            for (var turn = 0; turn < 12; turn++)
                chat.SeedTurnForCheck(streaming.Id, "第 " + turn + " 条普通对话内容");
            try
            {
                chat.ClientOverride = (_, _) => Interlocked.Increment(ref requests) == 1
                    ? new ScriptedChatClient(["回答"], gate: runGate.Task)
                    : new ScriptedChatClient(["## 压缩任务的正常摘要"], gate: summaryGate.Task);
                chat.OpenConversation(streaming.Id);
                chat.TryEnqueueSend(streaming.Id, "还在回答的提问", null, out _);
                Relayout(shell, panel);
                Check(await WaitForConditionAsync(
                          () => chat.IsRunning(streaming.Id) && panel.ChatActivityVisibleForCheck),
                    "运行中的气泡在屏幕上，压缩之前的读数是「" + panel.ChatActivityTextForCheck + "」");

                var runCompaction = chat.CompressContextAsync(streaming.Id);
                Dispatcher.UIThread.RunJobs();
                var duringRun = panel.ChatActivityTextForCheck;
                Check(duringRun == compacting,
                    "运行的会话里压缩时，气泡的状态行走的是压缩而不是生成中（实际「" + duringRun + "」）");

                summaryGate.SetResult(true);
                var runLanded = await runCompaction;
                Dispatcher.UIThread.RunJobs();
                var afterRun = panel.ChatActivityTextForCheck;
                Check(runLanded && afterRun == HubStrings.Get("ChatPreparing"),
                    "压缩结束后状态行交还给准备中，而不是一直停在压缩上（成功=" + runLanded + "，实际「"
                    + afterRun + "」）");

                runGate.SetResult(true);
                await WaitForRunAsync(chat, streaming.Id);
            }
            finally
            {
                runGate.TrySetResult(true);
                summaryGate.TrySetResult(true);
                chat.DeleteConversation(streaming.Id);
            }
        }
        finally
        {
            idleGate.TrySetResult(true);
            chat.ClientOverride = savedOverride;
            chat.DeleteConversation(idle.Id);
            if (beforeOpen is { } back) chat.OpenConversation(back);
            Relayout(shell, panel);
        }
    }
}
