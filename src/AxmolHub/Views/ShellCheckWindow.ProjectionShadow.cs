using System;
using System.Collections.Generic;
using System.Linq;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Compares the complete independent projection with actual rows collected by scrolling each item
/// into view. Folded activity children are flattened only while expanded; offscreen controls need not exist.
/// Occurrence identity, disk reloads and approval rewrites are covered separately.</summary>
public partial class ShellCheckWindow
{
    private sealed record ProjectionCase(string Label, Action<Conversation> Build);

    private void CheckTranscriptProjection(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var cases = new ProjectionCase[]
        {
            new("markdown-subsets", conversation =>
            {
                conversation.Messages.Add(ChatTurn.Assistant("    return value;"));
                conversation.Messages.Add(ChatTurn.Assistant("&lt;value&gt;"));
                conversation.Messages.Add(ChatTurn.Assistant("two  spaces"));
                conversation.Messages.Add(ChatTurn.Assistant("https://example.com"));
            }),
            new("literal-prose", conversation => conversation.Messages.Add(ChatTurn.Assistant("Plain literal reply."))),
            new("plan", conversation => conversation.Messages.Add(ChatTurn.User("制定计划"))),
            new("prose", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("问题"));
                conversation.Messages.Add(ChatTurn.Assistant("**回答**"));
                conversation.Messages.Add(ChatTurn.User("再问一句"));
                conversation.Messages.Add(ChatTurn.Assistant("**再答一句**"));
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
                conversation.Messages.Add(ChatTurn.FunctionCall("n1", "run_command", "{}", "我先 **查一下** 状态。"));
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
                conversation.Messages.Add(ChatTurn.Assistant("结果丢了，我 **重试**。"));
            }),
            new("system-turn", conversation =>
            {
                conversation.Messages.Add(ChatTurn.User("问题"));
                conversation.Messages.Add(ChatTurn.Assistant("**答**"));
                conversation.Messages.Add(ChatTurn.System("这段是上下文"));
                conversation.Messages.Add(ChatTurn.FunctionCall("s1", "run_command", "{}"));
                conversation.Messages.Add(ChatTurn.FunctionResult("s1", "ok"));
            }),
            new("thought-and-notice", conversation =>
            {
                conversation.Messages.Add(ChatTurn.Assistant("**开场**"));
                conversation.Messages.Add(ChatTurn.Assistant("", "想一想这一步值不值得"));
                conversation.Messages.Add(ChatTurn.FunctionCall("m1", "run_command", "{}"));
                conversation.Messages.Add(ChatTurn.FunctionResult("m1", "ok"));
                conversation.Messages.Add(ChatTurn.Notice("换了模型"));
                conversation.Messages.Add(ChatTurn.Assistant("**收尾**"));
            }),
            new("duplicate-call-ids", conversation =>
            {
                // The trap the two lookup policies create: both calls carry the same id, so the piece-local pairing
                // takes the LAST one while the conversation-wide lookup keeps the first. What has to agree is the
                // name each painted row was actually drawn from.
                conversation.Messages.Add(ChatTurn.User("并行两条命令"));
                conversation.Messages.Add(ChatTurn.FunctionCall("dup", "run_command", "{}"));
                conversation.Messages.Add(ChatTurn.FunctionResult("dup", "ok"));
                conversation.Messages.Add(ChatTurn.FunctionCall("dup", "read_file", "{}"));
                conversation.Messages.Add(ChatTurn.FunctionResult("dup", "ok"));
            }),
        };

        foreach (var item in cases)
        {
            var session = chat.StartConversation();
            try
            {
                chat.SeedTurnForCheck(session.Id, "projection fixture");
                session.Messages.Clear();
                item.Build(session);
                if (item.Label == "plan") chat.SeedPlanReplyForCheck(session.Id, "## Implementation plan\n\n1. Inspect.\n2. Fix.\n3. Verify.");
                chat.OpenConversation(session.Id);
                panel.Reload();
                var snapshot = TranscriptProjection.Build(session, panel.ShowThinkingForCheck,
                    ChatPanel.EagerMarkdownLimitForCheck);
                var names = TranscriptProjection.CallNames(session);
                if (item.Label == "duplicate-call-ids")
                    Check(names["dup"] == "run_command", "全局工具名查找保留首个调用，与段内末次配对独立");
                panel.ExpandVirtualGroupsForCheck();
                Relayout(shell, panel, measure: true);
                var keys = panel.ProjectionKeysForCheck;
                var expected = new Dictionary<string, string>();
                foreach (var entry in snapshot.Entries)
                {
                    if (entry is TranscriptMessage message)
                        expected["message:" + keys[message.SourceIndex]] = "M:" + (message.IsPlan ? "plan" : message.Role == ChatRoles.User ? "user" : "assistant") + (message.Markdown && item.Label != "literal-prose" ? ":md" : "");
                    else if (entry is TranscriptNotice notice) expected["notice:" + keys[notice.SourceIndex]] = "N";
                    else if (entry is TranscriptActivity activity)
                    {
                        if (activity.Piece.Folded)
                        {
                            var firstVisible = session.Messages.Select((turn, at) => (turn, at)).Where(pair => pair.turn.Role != ChatRoles.System)
                                .ElementAt(activity.FirstVisible).at;
                            expected["group:" + keys[firstVisible]] = "G";
                        }
                        foreach (var row in activity.Piece.Children)
                        {
                            var kind = row.Kind == TranscriptRowKind.ToolExchange ? "exchange" : row.Kind == TranscriptRowKind.Thought ? "thought" : "message";
                            expected[kind + ":" + keys[row.SourceIndex]] = row.Kind switch
                            {
                                TranscriptRowKind.ToolExchange => "T:" + (row.ToolName ?? ""),
                                TranscriptRowKind.Thought => "Th",
                                _ => "C" + (row.Markdown ? ":md" : ""),
                            };
                        }
                    }
                }
                var painted = new Dictionary<string, string>();
                for (var index = 0; index < panel.VirtualItemCountForCheck; index++)
                {
                    panel.ScrollVirtualItemForCheck(index);
                    Relayout(shell, panel, measure: true);
                    foreach (var row in panel.RealizedStructureForCheck) painted[row.Key] = row.Value;
                }
                Check(expected.Count == panel.VirtualItemCountForCheck,
                    $"完整投影与展示项数量一致「{item.Label}」：{expected.Count} 项");
                var equal = expected.Count == painted.Count && expected.All(pair => painted.GetValueOrDefault(pair.Key) == pair.Value);
                Check(equal, $"逐行滚入视口后绘制与投影一致「{item.Label}」："
                    + string.Join(";", expected.Where(pair => painted.GetValueOrDefault(pair.Key) != pair.Value)
                        .Select(pair => pair.Key + "=" + pair.Value + "/" + painted.GetValueOrDefault(pair.Key))));

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

}
