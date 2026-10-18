using System;
using System.Collections.Generic;

namespace AxmolHub.Core;

/// <summary>What one row of the transcript is. The names follow the renderer's own vocabulary so a divergence
/// between this model and the painted flow can be read without a translation table.</summary>
public enum TranscriptRowKind
{
    Message,
    Notice,
    Thought,
    CallSurface,
    ToolExchange,
}

/// <summary>One row inside a piece, or on its own. <c>SourceIndex</c> is a lookup hint into the conversation, never
/// an identity: the index moves when a result is filed beside its call.</summary>
public sealed record TranscriptRow(TranscriptRowKind Kind, int SourceIndex, string? ToolName);

/// <summary>A run of machine turns: folded (one collapsible head) or flat (a narrated call with its results).</summary>
public sealed record TranscriptPiece(
    bool Folded,
    int FirstVisible,
    int EndVisible,
    int CallCount,
    IReadOnlyList<TranscriptRow> Children);

/// <summary>One top-level thing the transcript shows, in reading order.</summary>
public abstract record TranscriptEntry(int FirstVisible);

public sealed record TranscriptMessage(int FirstVisible, int SourceIndex, string Role) : TranscriptEntry(FirstVisible);

public sealed record TranscriptNotice(int FirstVisible, int SourceIndex) : TranscriptEntry(FirstVisible);

public sealed record TranscriptActivity(TranscriptPiece Piece) : TranscriptEntry(Piece.FirstVisible);

public sealed record TranscriptSnapshot(IReadOnlyList<TranscriptEntry> Entries, int VisibleTurns);

/// <summary>
/// The transcript as rows, derived from the conversation alone — no Avalonia types, no controls, no view state.
///
/// <para>This is the first half of P1: a model that can be compared against what <c>ChatPanel</c> paints today.
/// Every rule below is copied from the renderer <b>as it behaves</b>, not from what its comments claim. Three of
/// those differ, and each is pinned by a shadow check rather than by this file's prose:</para>
///
/// <list type="number">
/// <item>System turns are dropped <i>before</i> grouping, so they do not break a run even though the grouping
/// comment says prose and system turns do.</item>
/// <item>A tool result's name has two different sources: the conversation-wide lookup takes the <b>first</b>
/// assistant call carrying an id, while a piece's result pairing takes the <b>last</b> call inside that piece.
/// With duplicate ids those disagree, so neither is "the" answer and both are kept.</item>
/// <item>A <c>file_write</c> result draws no exchange row — its call's write record already speaks for it.</item>
/// </list>
/// </summary>
public static class TranscriptProjection
{
    /// <summary>Machine work rather than something said. A thinking turn that also carries prose is prose.</summary>
    public static bool IsActivityTurn(ChatTurn turn) => turn.Role switch
    {
        ChatRoles.Tool => true,
        ChatRoles.Assistant => turn.ToolCallId is { Length: > 0 }
                               || (turn.Reasoning is { Length: > 0 } && turn.Text.Length == 0
                                   && turn.PlanApprovalState is null),
        _ => false,
    };

    /// <summary>A call the model made while saying why, owning no surface of its own: the sentence is the row.</summary>
    public static bool IsNarratedCall(ChatTurn turn) => turn.Role == ChatRoles.Assistant
                                                        && turn.ToolCallId is { Length: > 0 }
                                                        && !string.IsNullOrWhiteSpace(turn.Text)
                                                        && turn.ApprovalState is null && turn.UndoName is null;

    /// <summary>The conversation-wide tool name a result prints, first named call wins. See the class remarks.</summary>
    public static Dictionary<string, string> CallNames(Conversation conversation)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var turn in conversation.Messages)
        {
            if (turn.Role != ChatRoles.Assistant || turn.ToolCallId is not { Length: > 0 } id) continue;
            if (turn.ToolName is { } named && !names.ContainsKey(id)) names[id] = named;
        }

        return names;
    }

    public static TranscriptSnapshot Build(Conversation conversation, bool showThinking)
    {
        var visible = new List<ChatTurn>();
        foreach (var turn in conversation.Messages)
            if (turn.Role != ChatRoles.System) visible.Add(turn);

        var entries = new List<TranscriptEntry>();
        var index = 0;
        while (index < visible.Count)
        {
            var turn = visible[index];
            if (!IsActivityTurn(turn))
            {
                // Prose closes the run: the next piece of machine work is a different exchange. A notice is a line
                // about the conversation rather than something said, and closes it for the same reason.
                if (turn.Role == ChatRoles.Notice) entries.Add(new TranscriptNotice(index, index));
                else entries.Add(new TranscriptMessage(index, index, turn.Role));
                index++;
                continue;
            }

            var carriesSentence = IsNarratedCall(turn);
            var open = entries.Count > 0 ? entries[^1] as TranscriptActivity : null;
            // The piece on the page continues when this turn belongs to it: a fold takes the silent turns behind
            // it, a flat exchange takes only the results filed beside its call. A narrated call always opens one.
            var attach = open is not null && !carriesSentence && open.Piece.EndVisible == index
                         && (open.Piece.Folded || turn.Role == ChatRoles.Tool);
            var flat = attach ? !open!.Piece.Folded : carriesSentence;
            if (!attach)
                entries.Add(new TranscriptActivity(new TranscriptPiece(!flat, index, index, 0, [])));

            index++;
            if (flat)
                while (index < visible.Count && visible[index].Role == ChatRoles.Tool) index++;
            else
                while (index < visible.Count && IsActivityTurn(visible[index]) && !IsNarratedCall(visible[index]))
                    index++;

            var piece = ((TranscriptActivity)entries[^1]).Piece;
            // Re-sliced from where the run began, not from the turns that just arrived: a row is drawn from a call
            // and the result that answered it, so handing it only the new half would draw orphans.
            entries[^1] = new TranscriptActivity(
                Describe(piece.Folded, piece.FirstVisible, index, visible, showThinking));
        }

        return new TranscriptSnapshot(entries, visible.Count);
    }

    /// <summary>The rows inside one piece, re-derived from its whole span.</summary>
    private static TranscriptPiece Describe(bool folded, int first, int end, List<ChatTurn> visible, bool showThinking)
    {
        // In-piece pairing takes the LAST call carrying an id; the conversation-wide lookup takes the first.
        var calls = new Dictionary<string, ChatTurn>(StringComparer.Ordinal);
        for (var at = first; at < end; at++)
            if (visible[at].Role == ChatRoles.Assistant && visible[at].ToolCallId is { Length: > 0 } id)
                calls[id] = visible[at];

        var rows = new List<TranscriptRow>();
        for (var at = first; at < end; at++)
        {
            var turn = visible[at];
            if (turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 })
            {
                if (turn.ApprovalState is { Length: > 0 } || turn.UndoName is { Length: > 0 } || IsNarratedCall(turn))
                    rows.Add(new TranscriptRow(TranscriptRowKind.CallSurface, at, turn.ToolName));
                else if (showThinking && turn.Reasoning is { Length: > 0 })
                    rows.Add(new TranscriptRow(TranscriptRowKind.Thought, at, null));
                continue;
            }

            if (turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } answered)
            {
                var paired = calls.TryGetValue(answered, out var call) ? call : null;
                // A write's record line already speaks for its exchange.
                if (paired?.ToolName == ChatChanges.FileWriteTool) continue;
                rows.Add(new TranscriptRow(TranscriptRowKind.ToolExchange, at, paired?.ToolName ?? turn.ToolName));
                continue;
            }

            if (showThinking && turn.Reasoning is { Length: > 0 })
                rows.Add(new TranscriptRow(TranscriptRowKind.Thought, at, null));
        }

        return new TranscriptPiece(folded, first, end, calls.Count, rows);
    }
}
