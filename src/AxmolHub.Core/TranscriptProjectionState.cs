using System;
using System.Collections.Generic;

namespace AxmolHub.Core;

/// <summary>
/// Runtime occurrence identity for one live conversation's turns.
///
/// <para>A turn is an immutable record, so the shell hands back a <b>different object</b> for the same occurrence
/// all the time: stamping <c>ApprovalSeen</c>, deciding an approval, spending an undo copy are all
/// <c>turn with { … }</c>. Keys are allocated here so a row can survive that — same content in the same slot is the
/// same occurrence, whatever object it arrives as.</para>
///
/// <para>Scope is deliberate. Keys live as long as this object, are never persisted, and a conversation reloaded
/// from disk starts again at 1: neither the session file nor a rendered control may depend on them.</para>
/// </summary>
public sealed class TranscriptProjectionState
{
    /// <summary>Below this share of content matches a rebuild is read as a wholesale rewrite rather than an edit.</summary>
    private const double MatchFloor = 0.5;

    private List<ChatTurn> _seen = [];
    private List<long> _keys = [];
    private long _next = 1;

    /// <summary>The conversation this state belongs to; a different one starts from scratch.</summary>
    public string ConversationId { get; private set; } = "";

    public TranscriptSnapshot Build(Conversation conversation, bool showThinking,
        int eagerMarkdownLimit = int.MaxValue)
    {
        if (ConversationId != conversation.Id)
        {
            Reset();
            ConversationId = conversation.Id;
        }

        var keys = Reconcile(conversation.Messages);
        return TranscriptProjection.Build(conversation, showThinking, eagerMarkdownLimit, keys);
    }

    private void Reset()
    {
        _seen = [];
        _keys = [];
        _next = 1;
    }

    /// <summary>Allocates or carries one key per stored turn, then remembers this list as the next comparison's
    /// starting point. Linear and deliberately conservative: the shell's mutations are append,
    /// insert-beside-a-call, replace and remove.</summary>
    private long[] Reconcile(List<ChatTurn> messages)
    {
        var keys = new long[messages.Count];
        var old = 0;
        var matched = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            if (old < _seen.Count && Same(_seen[old], messages[index]))
            {
                keys[index] = _keys[old];
                old++;
                matched++;
                continue;
            }

            // A result filed *beside* its call: everything already known is still where it was, on the far side of
            // the insertion, so the new turn gets a fresh key and the walk does not advance.
            if (old < _seen.Count && index + 1 < messages.Count && Same(_seen[old], messages[index + 1]))
            {
                keys[index] = _next++;
                continue;
            }

            // A removal in front of us: step over the occurrence that is gone, then carry on.
            if (old + 1 < _seen.Count && Same(_seen[old + 1], messages[index]))
            {
                old++;
                keys[index] = _keys[old];
                old++;
                matched++;
                continue;
            }

            // Same slot, different content: that occurrence was replaced — an edit, a regenerate, a rename. It keeps
            // its key, which is what lets a row be repainted rather than thrown away and re-made. Deliberately not
            // counted as a match: a rewrite that changes everything must end in the reset below, not keep keys.
            if (old < _seen.Count)
            {
                keys[index] = _keys[old];
                old++;
                continue;
            }

            keys[index] = _next++;
        }

        if (_seen.Count > 0 && messages.Count > 0
            && matched < (double)Math.Min(_seen.Count, messages.Count) * MatchFloor)
        {
            for (var index = 0; index < keys.Length; index++) keys[index] = _next++;
        }

        _seen = [.. messages];
        _keys = [.. keys];
        return keys;
    }

    /// <summary>Same occurrence: the same object, or the same content. The fields the shell rewrites in place —
    /// approvals, undo state, elapsed time — are deliberately not part of this test: a turn that was decided is
    /// still the same turn.</summary>
    private static bool Same(ChatTurn candidate, ChatTurn other)
        => ReferenceEquals(candidate, other)
           || (candidate.Role == other.Role
               && candidate.Text == other.Text
               && candidate.ToolCallId == other.ToolCallId
               && candidate.At == other.At);
}
