using System;
using System.Collections.Generic;
using System.Linq;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The shadow comparison for P1: the new <see cref="TranscriptProjection"/> must produce the same top-level rows,
/// in the same order, with the same number of rows inside every activity piece, as the renderer paints today.
///
/// <para>The renderer is the oracle, not the projection: on a divergence the <i>projection</i> is wrong unless
/// somebody has decided the behaviour changes. That is deliberate — this file's job is to make "the new model
/// agrees with what people are looking at" an assertion rather than a hope.</para>
///
/// <para>What it compares: row kind and order, piece flat/folded, and the row count inside each piece. That last
/// number carries the awkward rules — a suppressed <c>file_write</c> result, a thinking row, a call surface that
/// keeps its prose. What it does <b>not</b> compare yet: which tool name a result prints (the two lookup
/// policies), plan cards, and Markdown flavour. Those need a finer signature and are the next step.</para>
/// </summary>
public partial class ShellCheckWindow
{
    private sealed record ProjectionCase(string Label, Action<Conversation> Build);

    private void CheckTranscriptProjection(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var cases = new ProjectionCase[]
        {
            new("prose", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("问题"));
                conversation.Messages.Add(ChatTurn.Assistant("回答"));
                conversation.Messages.Add(ChatTurn.User("再问一句"));
                conversation.Messages.Add(ChatTurn.Assistant("再答一句"));
            }),
            new("tools", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("看看仓库"));
                conversation.Messages.Add(ChatTurn.FunctionCall("c1", "run_command", "{}"));
                conversation.Messages.Add(ChatTurn.FunctionResult("c1", "ok"));
                conversation.Messages.Add(ChatTurn.FunctionCall("c2", "read_file", "{}"));
                conversation.Messages.Add(ChatTurn.FunctionResult("c2", "ok"));
            }),
            new("narrated-call", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("看看仓库"));
                conversation.Messages.Add(ChatTurn.FunctionCall("n1", "run_command", "{}", "我先查一下状态。"));
                conversation.Messages.Add(ChatTurn.FunctionResult("n1", "ok"));
            }),
            new("pending-decision", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("改一个文件"));
                conversation.Messages.Add(ChatTurn.FunctionCall("a1", "run_command", "{}")
                    with { ApprovalState = ChatApprovalStates.Pending });
            }),
            new("file-write", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("写一个文件"));
                conversation.Messages.Add(ChatTurn.FunctionCall("w1", ChatChanges.FileWriteTool, "{}")
                    with { UndoName = "copy-1" });
                conversation.Messages.Add(ChatTurn.FunctionResult("w1", "ok"));
            }),
            new("orphan-result", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("继续"));
                conversation.Messages.Add(ChatTurn.FunctionResult("ghost", "ok"));
                conversation.Messages.Add(ChatTurn.Assistant("结果丢了，我重试。"));
            }),
            new("system-turn", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("问题"));
                conversation.Messages.Add(ChatTurn.Assistant("答"));
                conversation.Messages.Add(ChatTurn.System("这段是上下文"));
                conversation.Messages.Add(ChatTurn.FunctionCall("s1", "run_command", "{}"));
                conversation.Messages.Add(ChatTurn.FunctionResult("s1", "ok"));
            }),
            new("thought-and-notice", conversation =>
            {
                conversation.Messages.Add(ChatTurn.Assistant("开场"));
                conversation.Messages.Add(ChatTurn.Assistant("", "想一想这一步值不值得"));
                conversation.Messages.Add(ChatTurn.FunctionCall("m1", "run_command", "{}"));
                conversation.Messages.Add(ChatTurn.FunctionResult("m1", "ok"));
                conversation.Messages.Add(ChatTurn.Notice("换了模型"));
                conversation.Messages.Add(ChatTurn.Assistant("收尾"));
            }),
        };

        foreach (var item in cases)
        {
            var session = chat.StartConversation();
            try
            {
                item.Build(session);
                chat.OpenConversation(session.Id);
                panel.Reload();
                var snapshot = TranscriptProjection.Build(session, panel.ShowThinkingForCheck);
                var painted = panel.PaintedStructureForCheck.ToArray();
                Check(snapshot.VisibleTurns <= ChatPanel.FirstPaintTurnsForCheck,
                    $"影子比较的夹具「{item.Label}」小于一屏，画的流才等于整份转录");
                var projected = Tokens(snapshot).ToArray();
                var equal = projected.SequenceEqual(painted);
                Check(equal, equal
                    ? $"影子模型与当前渲染一致「{item.Label}」：{painted.Length} 项"
                    : $"影子模型与当前渲染不一致「{item.Label}」：{Divergence(projected, painted)}");
            }
            finally
            {
                chat.DeleteConversation(session.Id);
            }
        }

        if (before is { Length: > 0 } && chat.Conversations.Any(summary => summary.Id == before))
            chat.OpenConversation(before);
        panel.Reload();
    }

    /// <summary>One token per top-level entry, matching what <c>PaintedStructureForCheck</c> produces.</summary>
    private static IEnumerable<string> Tokens(TranscriptSnapshot snapshot) => snapshot.Entries.Select(entry => entry switch
    {
        TranscriptMessage message => "message:" + (message.Role == ChatRoles.User ? "user" : "assistant"),
        TranscriptNotice => "notice",
        TranscriptActivity activity => "piece:" + (activity.Piece.Folded ? "folded:" : "flat:")
                                                + activity.Piece.Children.Count,
        _ => "unknown",
    });

    private static string Divergence(string[] projected, string[] painted)
    {
        var at = 0;
        while (at < projected.Length && at < painted.Length && projected[at] == painted[at]) at++;
        var model = at < projected.Length ? projected[at] : "<end>";
        var actual = at < painted.Length ? painted[at] : "<end>";
        return $"第 {at} 项起不同（模型 {model}，画面 {actual}）；模型 [{string.Join(",", projected)}]"
               + $" 画面 [{string.Join(",", painted)}]";
    }
}
