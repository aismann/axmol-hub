using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AxmolHub.Core;

/// <summary>
/// Runtime occurrence identity for one live conversation's turns.
///
/// <para>A turn is an immutable record, so the shell hands back a <b>different object</b> for the same occurrence
/// all the time: stamping <c>ApprovalSeen</c>, deciding an approval, spending an undo copy are all
/// <c>turn with { … }</c>. Keys preserve unambiguous occurrences across those rewrites without assigning a
/// replacement the identity of the old source slot.</para>
///
/// <para>Keys are process-unique and never persisted. A disk reload gets fresh identities. Unannounced edits
/// cannot be distinguished from replacement through the public Messages list: only unambiguous matches retain
/// identity. Consumers must tolerate a fresh key rather than inherit another occurrence's state.</para>
/// </summary>
public sealed class TranscriptProjectionState
{
    private List<ChatTurn> _seen = [];
    private long[] _keys = [];
    private static long _next;
    private readonly Dictionary<long, int> _indices = new();

    public long KeyAt(int sourceIndex) => _keys[sourceIndex];
    public bool TryResolve(long key, out int sourceIndex) => _indices.TryGetValue(key, out sourceIndex);

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
        _indices.Clear();
    }

    private readonly record struct Signature(string Role, string Text, string? CallId, DateTimeOffset At)
    {
        internal static Signature Of(ChatTurn turn) => new(turn.Role, turn.Text, turn.ToolCallId, turn.At);
    }

    /// <summary>Unchanged sequences retain even duplicate occurrences. After a structural change, first match
    /// unique object references, then unique content signatures (ignoring approval/undo/elapsed fields). Ambiguous
    /// duplicates and unknown replacements get fresh keys, never the key of the nearest slot.</summary>
    private long[] Reconcile(List<ChatTurn> messages)
    {
        if (messages.Count == _seen.Count && messages.Where((turn, at) => !ReferenceEquals(turn, _seen[at])).Any() == false)
            return _keys;

        var keys = new long[messages.Count];
        // Initial opens and the common append path need no content dictionaries. In particular, do not hash
        // every Markdown body merely because a streaming reply added one turn to a long transcript.
        if (messages.Count >= _seen.Count
            && !_seen.Where((turn, at) => !ReferenceEquals(turn, messages[at])).Any())
        {
            _keys.CopyTo(keys, 0);
            for (var at = _seen.Count; at < messages.Count; at++) keys[at] = Interlocked.Increment(ref _next);
            Remember(messages, keys);
            return keys;
        }
        var oldRefs = UniqueIndices<ChatTurn>(_seen, turn => turn, ReferenceEqualityComparer.Instance);
        var newRefs = UniqueIndices<ChatTurn>(messages, turn => turn, ReferenceEqualityComparer.Instance);
        var used = new HashSet<int>();
        for (var at = 0; at < messages.Count; at++)
        {
            if (newRefs[messages[at]] < 0 || !oldRefs.TryGetValue(messages[at], out var prior) || prior < 0) continue;
            keys[at] = _keys[prior];
            used.Add(prior);
        }

        var oldSignatures = UniqueIndices(_seen, Signature.Of, EqualityComparer<Signature>.Default);
        var newSignatures = UniqueIndices(messages, Signature.Of, EqualityComparer<Signature>.Default);
        for (var at = 0; at < messages.Count; at++)
        {
            if (keys[at] != 0) continue;
            var signature = Signature.Of(messages[at]);
            if (newSignatures[signature] >= 0 && oldSignatures.TryGetValue(signature, out var prior)
                && prior >= 0 && used.Add(prior)) keys[at] = _keys[prior];
            else keys[at] = Interlocked.Increment(ref _next);
        }

        Remember(messages, keys);
        return keys;
    }

    private void Remember(List<ChatTurn> messages, long[] keys)
    {
        _seen = [.. messages];
        _keys = keys;
        _indices.Clear();
        _indices.EnsureCapacity(keys.Length);
        for (var at = 0; at < keys.Length; at++) _indices.Add(keys[at], at);
    }

    private static Dictionary<T, int> UniqueIndices<T>(List<ChatTurn> turns, Func<ChatTurn, T> select,
        IEqualityComparer<T> comparer) where T : notnull
    {
        var result = new Dictionary<T, int>(comparer);
        for (var at = 0; at < turns.Count; at++)
        {
            var key = select(turns[at]);
            if (!result.TryAdd(key, at)) result[key] = -1;
        }
        return result;
    }
}
