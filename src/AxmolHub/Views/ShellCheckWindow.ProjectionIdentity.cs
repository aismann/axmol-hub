using System.Collections.Generic;
using System.Linq;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// What the occurrence keys have to survive. These are the shell's own mutations, replayed against
/// <see cref="TranscriptProjectionState"/> without any UI: a decision, a result filed beside its call, a deletion, a
/// wholesale rewrite. They are assertions about identity only — which row gets which cached height or scroll anchor
/// comes later, and depends on these holding.
/// </summary>
public partial class ShellCheckWindow
{
    private void CheckProjectionIdentity(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        var state = new TranscriptProjectionState();
        // Fold memory is kept for listed sessions only. Publish the fixture through the real write path,
        // then mutate the live list below; an unsaved empty session deliberately has no history-list entry.
        chat.SeedTurnForCheck(session.Id, "identity fixture");
        session.Messages.Clear();
        try
        {
            long[] Build() => KeysOf(state.Build(session, panel.ShowThinkingForCheck));

            session.Messages.Add(ChatTurn.User("第一问"));
            session.Messages.Add(ChatTurn.Assistant("第一答"));
            session.Messages.Add(ChatTurn.FunctionCall("i1", "run_command", "{}"));
            session.Messages.Add(ChatTurn.FunctionResult("i1", "ok"));
            var first = Build();
            Check(first.Length == 4 && first.All(key => key > 0) && first.Distinct().Count() == 4,
                "每一次出现都有自己的键（" + string.Join(",", first) + "）");

            Check(Build().SequenceEqual(first), "没有改动时键不变");

            // The shell rewrites a turn with `with { … }` all the time; the row must survive it.
            session.Messages[2] = session.Messages[2] with { ApprovalState = ChatApprovalStates.Pending };
            Check(Build().SequenceEqual(first), "审批状态变化（对象被换掉）不改变键");

            // A result filed beside its call is an insertion in the middle, not an append.
            session.Messages.Insert(3, ChatTurn.FunctionResult("i1", "again"));
            var inserted = Build();
            Check(inserted.Take(3).SequenceEqual(first.Take(3))
                  && inserted.Skip(4).SequenceEqual(first.Skip(3))
                  && !first.Contains(inserted[3]),
                "结果插在调用旁边：已知的键不动，新的一条拿到新键");

            // Removing a turn retires its key and leaves the rest alone.
            session.Messages.RemoveAt(1);
            var removed = Build();
            Check(removed.SequenceEqual(inserted.Where((_, at) => at != 1)),
                "删掉一条：其余键原样，被删的那条不再出现");

            // Equal-size replacements must not steal the occurrence in the same slot.
            var replacedKey = removed[0];
            session.Messages[0] = ChatTurn.User("replacement");
            var replaced = Build();
            Check(replaced[0] != replacedKey && replaced.Skip(1).SequenceEqual(removed.Skip(1))
                  && !state.TryResolve(replacedKey, out _), "替换一条不会继承原槽位的键，被删除的键无法解析");

            var duplicate = ChatTurn.Assistant("duplicate");
            session.Messages = [duplicate, duplicate];
            var duplicates = Build();
            Check(duplicates.Distinct().Count() == 2 && Build().SequenceEqual(duplicates),
                "同一对象重复出现也有独立键，未改动时保持不变");
            session.Messages = [duplicate with { ApprovalSeen = true }, duplicate with { ApprovalSeen = true }];
            var ambiguous = Build();
            Check(!ambiguous.Any(duplicates.Contains), "重复内容无法消歧时分配新键，不猜测折叠状态归属");

            // A real disk round-trip starts a fresh identity scope even though the session id is unchanged.
            var scratch = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(chat.PlansDirectoryForCheck!)!, "identity-store");
            var store = new ConversationStore(scratch);
            store.Save(session);
            var reloaded = store.Load(session.Id)!;
            var diskKeys = KeysOf(new TranscriptProjectionState().Build(reloaded, panel.ShowThinkingForCheck));
            Check(!diskKeys.Any(ambiguous.Contains), "磁盘重载分配新键，不复用旧进程状态");
            store.Delete(session.Id);

            panel.Reload();
            var panelKeys = panel.ProjectionKeysForCheck;
            var other = chat.StartConversation();
            chat.OpenConversation(session.Id);
            panel.Reload();
            Check(panel.ProjectionKeysForCheck.SequenceEqual(panelKeys), "切换会话返回后仍使用原来的出现键");
            chat.DeleteConversation(other.Id);

            // A wholesale rewrite is a reset, not an edit: no key may be handed to a different turn.
            session.Messages.Clear();
            session.Messages.Add(ChatTurn.User("全新的一问"));
            session.Messages.Add(ChatTurn.Assistant("全新的一答"));
            var rewritten = Build();
            Check(rewritten.Length == 2 && !rewritten.Any(ambiguous.Contains),
                "整段重写是重置，不会把某条 turn 的键交给另一条");

            session.Messages = [ChatTurn.User("tools"), ChatTurn.FunctionCall("fold", "read_file", "{}"),
                ChatTurn.FunctionResult("fold", "first")];
            panel.Reload();
            panel.SetActivityGroupExpandedForCheck(0, true);
            panel.SetActivityRowExpandedForCheck(0, true);
            session.Messages.Insert(0, ChatTurn.User("inserted before the group"));
            session.Messages[2] = session.Messages[2] with { ApprovalSeen = true };
            panel.Reload();
            Check(panel.ActivityGroupExpandedForCheck(0) && panel.ActivityRowExpandedForCheck(0),
                "前方插入和审批改写后，活动段和结果行仍按出现键保持展开");
            session.Messages.Add(ChatTurn.FunctionResult("fold", "second"));
            panel.Reload();
            Check(panel.ActivityRowExpandedForCheck(0) && !panel.ActivityRowExpandedForCheck(1),
                "重复 call id 的结果不会继承另一行的展开状态");
            session.Messages.RemoveAt(3);
            panel.Reload();
            Check(!panel.ActivityRowExpandedForCheck(0), "删除已展开结果后，同 call id 的剩余结果保持关闭");

            // Queue promotions, then replace and insert before the dispatcher runs. No old payload may return.
            panel.SetPaintWindowForCheck(100);
            session.Messages = Enumerable.Range(0, 40).Select(i => ChatTurn.Assistant($"old-{i} **body**")).ToList();
            panel.Reload();
            session.Messages[10] = ChatTurn.Assistant("replacement **current body**");
            session.Messages.Insert(0, ChatTurn.User("shift"));
            panel.Reload();
            Relayout(shell, panel, measure: true);
            Check(!panel.PlainTurnHoldsForCheck("old-10 **body**")
                  && panel.ProjectionKeysForCheck.Length == session.Messages.Count,
                "排队的 Markdown 补全在插入和替换后不恢复旧 turn");
            panel.SetPaintWindowForCheck(ChatPanel.FirstPaintTurnsForCheck);
        }
        finally
        {
            chat.DeleteConversation(session.Id);
            if (before is { Length: > 0 } && chat.Conversations.Any(summary => summary.Id == before))
                chat.OpenConversation(before);
            panel.Reload();
        }
    }

    /// <summary>One key per visible turn, in transcript order: the entries' own keys, and a piece's contributing
    /// turn keys in the order it stands for them.</summary>
    private static long[] KeysOf(TranscriptSnapshot snapshot)
    {
        var keys = new List<long>();
        foreach (var entry in snapshot.Entries)
        {
            switch (entry)
            {
                case TranscriptMessage message: keys.Add(message.TurnKey); break;
                case TranscriptNotice notice: keys.Add(notice.TurnKey); break;
                case TranscriptActivity activity: keys.AddRange(activity.Piece.TurnKeys ?? []); break;
            }
        }

        return [.. keys];
    }
}
