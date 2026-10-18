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

            // A wholesale rewrite is a reset, not an edit: no key may be handed to a different turn.
            session.Messages.Clear();
            session.Messages.Add(ChatTurn.User("全新的一问"));
            session.Messages.Add(ChatTurn.Assistant("全新的一答"));
            var rewritten = Build();
            Check(rewritten.Length == 2 && !rewritten.Any(removed.Contains),
                "整段重写是重置，不会把某条 turn 的键交给另一条");
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
