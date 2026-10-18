using System.Linq;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>What opening a long session costs, pinned as a shape rather than as a clock.
///
/// <para>Kept in its own file the way <c>ShellCheckWindow.ModelSwitch.cs</c> is, because the file this check
/// would otherwise grow is already the largest in the repository.</para>
///
/// <para>No stopwatch anywhere. "Renders in under 200 ms" is a check that fails on a slow machine, and this
/// suite has said so before (<c>ChatSidebar</c>'s layout-timing note). Every number below is a count of controls,
/// which comes out the same on a laptop and on a CI runner — and each of them is a claim the renderer can only
/// keep by doing the work proportionally, so a stopwatch would be measuring the same thing less reliably.</para></summary>
public partial class ShellCheckWindow
{
    /// <summary>
    /// Three claims about one twenty-answer session: it opens with a bounded number of Markdown documents, the
    /// plain rows near the reader are promoted once the dispatcher runs, and leaving it and coming back
    /// re-attaches the rows it had rather than drawing them again.
    ///
    /// <para>The first is read <b>before</b> anything pumps the dispatcher, because the promotion pass is posted
    /// rather than run inline: what this counts is exactly what the render laid down, which is the only moment
    /// the eager window is observable as a number.</para>
    /// </summary>
    private void CheckTranscriptCost(MainWindow shell, ChatPanel panel)
    {
        const int turns = 20;
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var longSession = chat.StartConversation();
        for (var i = 0; i < turns; i++)
            chat.SeedAssistantReplyForCheck(longSession.Id, $"第 {i + 1} 条回答，带 `代码` 与 **粗体**。");

        panel.Reload();
        var documents = panel.MarkdownDocumentCountForCheck;
        var plain = panel.PlainTurnRowCountForCheck;
        var limit = ChatPanel.EagerMarkdownLimitForCheck;
        Check(documents == limit && plain == turns - documents,
            $"长会话按屏幕开，不按会话长度开：{turns} 条回答里只画 {documents} 篇 Markdown"
            + $"（上限 {limit}），其余 {plain} 条先给可选纯文本");

        // The rows in reach are the ones that get their Markdown: a session nobody can read is not a saving, so
        // the held-back ones have to arrive as they are looked at.
        Relayout(shell, panel, measure: true);
        var promoted = panel.PlainTurnRowCountForCheck;
        var firstRow = panel.FirstTranscriptRowForCheck;
        Check(promoted < plain,
            $"滚到眼前的纯文本补成 Markdown：{plain} 条降到 {promoted} 条");

        // Leaving for another session and coming back: the same controls have to come back, and the promotion
        // state with them. A red cell here means the second visit paid for the first visit's work again.
        var other = chat.StartConversation();
        chat.SeedTurnForCheck(other.Id, "另一个会话");
        Relayout(shell, panel);
        chat.OpenConversation(longSession.Id);
        Relayout(shell, panel);
        Check(ReferenceEquals(panel.FirstTranscriptRowForCheck, firstRow)
              && panel.PlainTurnRowCountForCheck == promoted,
            "回到刚离开的会话是接回原来的行，不是重画一遍：首行仍是同一个控件，"
            + $"纯文本行数不变（{promoted} 条）");

        // ── A window edge that lands inside a machine exchange ──
        // The cut is chosen by turn count, and a count knows nothing about which turns belong together. This
        // fixture puts a tool result exactly at the edge, so a naive cut would paint a result whose call is above
        // the window: the answer has to move the edge back over the call instead.
        var edgeSession = chat.StartConversation();
        for (var i = 0; i < 3; i++)
            chat.SeedAssistantReplyForCheck(edgeSession.Id, $"前置回答 {i + 1}");
        // Two turns appended straight to the live conversation, the way the tool-flood fixture builds a call and
        // its result: there is no seed that writes a result, and what this check reads is how the window cuts a
        // stored exchange rather than how one was stored.
        var edgeTurns = chat.ActiveConversation!.Messages;
        edgeTurns.Add(ChatTurn.FunctionCall("call_edge", "run_command", "{\"command\":\"git status\"}"));
        edgeTurns.Add(ChatTurn.FunctionResult("call_edge", "ok"));
        for (var i = 0; i < 29; i++)
            chat.SeedAssistantReplyForCheck(edgeSession.Id, $"后续回答 {i + 1}");
        chat.OpenConversation(longSession.Id);
        chat.OpenConversation(edgeSession.Id);
        panel.Reload();
        var edgePainted = panel.PaintedTurnCountForCheck;
        Check(edgePainted == ChatPanel.FirstPaintTurnsForCheck + 1,
            $"窗口边落在工具调用中间时，边往回退一格把调用带进来：{ChatPanel.FirstPaintTurnsForCheck} 条变成 "
            + $"{edgePainted} 条");
        chat.DeleteConversation(edgeSession.Id);

        // ── A session carrying a pending decision, longer than one window ──
        // Opening a session used to write it unconditionally: a `turn with { … }` per pending card to flip
        // ApprovalSeen, plus a whole-file save whether or not anything needed stamping. That rewrite is also what
        // stopped the rows being re-attached, because the stamped turn comes back as a different object. This
        // pins all three halves: the flag is still stamped, a later open with nothing to stamp rewrites no turn,
        // and the rows come back either way.
        var decided = chat.StartConversation();
        for (var i = 0; i < 3; i++)
            chat.SeedAssistantReplyForCheck(decided.Id, $"待定的回答 {i + 1}");
        chat.ActiveConversation!.Messages.Add(ChatTurn.FunctionCall("call_wait", "run_command",
            "{\"command\":\"git status\"}") with { ApprovalState = ChatApprovalStates.Pending });
        for (var i = 0; i < 36; i++)
            chat.SeedAssistantReplyForCheck(decided.Id, $"后续回答 {i + 1}");
        var pendingBefore = chat.ActiveConversation!.Messages.Single(turn => turn.ToolCallId == "call_wait");

        chat.OpenConversation(longSession.Id);
        chat.OpenConversation(decided.Id);
        panel.Reload();
        var stamped = !ReferenceEquals(pendingBefore,
            chat.ActiveConversation!.Messages.Single(turn => turn.ToolCallId == "call_wait"));
        var rowBefore = panel.FirstTranscriptRowForCheck;
        var paintedBefore = panel.PaintedTurnCountForCheck;

        chat.OpenConversation(longSession.Id);
        Relayout(shell, panel);
        chat.OpenConversation(decided.Id);
        Relayout(shell, panel);
        Check(stamped && ReferenceEquals(panel.FirstTranscriptRowForCheck, rowBefore)
              && panel.PaintedTurnCountForCheck == paintedBefore,
            "带待决卡片的会话：首次打开写下 ApprovalSeen（这一条 turn 被换成新对象），"
            + "离开再回来仍然接回原来的行，窗口位置也一样（写入 " + stamped
            + "，行复用 " + ReferenceEquals(panel.FirstTranscriptRowForCheck, rowBefore)
            + "，窗口 " + paintedBefore + "→" + panel.PaintedTurnCountForCheck + "）");

        // Nothing left to stamp, so this open must not rewrite the conversation at all.
        var quietBefore = chat.ActiveConversation!.Messages.ToArray();
        chat.OpenConversation(decided.Id);
        var quietAfter = chat.ActiveConversation!.Messages;
        Check(quietBefore.Length == quietAfter.Count
              && quietBefore.Zip(quietAfter).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "没有待决卡片时再打开一次，一条 turn 都不会被重写（" + quietBefore.Length + " 条）");
        chat.DeleteConversation(decided.Id);

        // ── A session longer than one window ──
        // What the window claims: the first frame costs what a screen costs, not what the session costs. Counted
        // before anything pumps the dispatcher, because afterwards only the line at the top is left as evidence.
        var windowed = chat.StartConversation();
        for (var i = 0; i < 120; i++)
            chat.SeedAssistantReplyForCheck(windowed.Id, $"第 {i + 1} 条回答，带 `代码` 与 **粗体**。");
        chat.OpenConversation(longSession.Id);
        chat.OpenConversation(windowed.Id);
        panel.Reload();
        var firstPaint = panel.PaintedTurnCountForCheck;
        Check(firstPaint == ChatPanel.FirstPaintTurnsForCheck && panel.HasLoadEarlierRowForCheck,
            $"长会话先只画最新的一段：120 条回答里画 {firstPaint} 条"
            + $"（窗口 {ChatPanel.FirstPaintTurnsForCheck}），更早的用一行代替");

        // Laid out, not merely present: a row whose style resolved to nothing would still be counted above.
        Relayout(shell, panel, measure: true);
        Check(panel.LoadEarlierRowHeightForCheck > 0,
            "「加载更早的」真的画出来了，高 " + Fmt(panel.LoadEarlierRowHeightForCheck) + "px");

        // Each press widens the window by a step, and the line goes away once the whole history is in the flow.
        var presses = 0;
        while (panel.HasLoadEarlierRowForCheck && presses < 10 && panel.ClickLoadEarlierForCheck()) presses++;
        Relayout(shell, panel, measure: true);
        Check(!panel.HasLoadEarlierRowForCheck && panel.PaintedTurnCountForCheck == 120,
            $"点「加载更早的」一次给一批，点到底把整段都给出来：按了 {presses} 次，现在画 "
            + $"{panel.PaintedTurnCountForCheck} 条，头上那行{(panel.HasLoadEarlierRowForCheck ? "还在" : "没了")}");

        chat.DeleteConversation(windowed.Id);
        CheckSessionOpenMeasurements(shell, panel, longSession.Id, other.Id);
        // Last, and opt-in: the benchmark switches sessions hundreds of times, and the assertions above read the
        // view's own position and promotion state.
        RunTranscriptBenchmark(shell, panel);
        chat.DeleteConversation(other.Id);
        chat.DeleteConversation(longSession.Id);
        if (before is { Length: > 0 } && chat.Conversations.Any(summary => summary.Id == before))
            chat.OpenConversation(before);
        panel.Reload();
        Dispatcher.UIThread.RunJobs();
    }
}
