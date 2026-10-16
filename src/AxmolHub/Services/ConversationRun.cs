using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Where a chat run is. <c>Completed</c> runs leave the workspace's registry, so anything reading a
/// phase is looking at work that is still happening.</summary>
internal enum RunPhase
{
    Streaming,

    /// <summary>A tool call is waiting for permission. The run keeps its slot, but no stream is open, so nothing
    /// re-arms the inactivity deadline — and if the one already armed fires while a person decides, the pending
    /// call and the decision both survive it. A new message does not wait for the decision — it supersedes it.</summary>
    AwaitingApproval,

    Completed,
}

/// <summary>How a run ended. Decided once, by the pump, from which cancellation source fired — the panel no
/// longer has to infer "the user pressed stop" from exception types.</summary>
internal enum RunResult
{
    Completed,
    Cancelled,
    TimedOut,
    Failed,

    /// <summary>Not an ending: the run parked on an approval and stays in the registry until that is decided.</summary>
    Parked,
}

/// <summary>Which of the three things a session can stop and wait on has just arrived. The kind chooses the words
/// of the notification and nothing else: the badge counts pending decisions without distinguishing them, and the
/// card that opens is chosen by the transcript state, not by this.</summary>
internal enum ChatAttentionKind
{
    ToolApproval,
    PlanApproval,

    /// <summary>The assistant asked a question mid-run. Its own kind because a person who has stepped away needs
    /// to know which of the two they are being called back for: one is a request to allow something, the other is
    /// a request for information only they have.</summary>
    UserQuestion,
}

/// <summary>
/// One in-flight assistant reply. The run is what lets a session keep working while the user looks at another:
/// it owns the cancellation, the deadline, the text arriving so far, and the steer waiting for it — none of
/// which can live in a view that gets thrown away on the next conversation switch.
///
/// Timers are two sources rather than one because the old single source had to serve two masters: a reply the
/// user asked to stop and a stream that stalled are different events with different messages, and a session
/// that runs long while it keeps producing is healthy, not overdue.
///
/// The one source that measures the stream carries two windows, because the silence before a round's first frame
/// and the silence after a chunk of it are not the same event: the first is a request the gateway has not begun
/// to answer, the second is a stream that stopped. Which one was armed is kept, so a timeout can name it.
///
/// The run also keeps how long this answer has been working, and that is the one number a view cannot own: the
/// bubble displaying it is thrown away the moment the user looks at another session, so a reply that restarted
/// its clock on the way back would be saying something untrue about the time it took.
/// </summary>
internal sealed class ConversationRun : IDisposable
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    // Where this segment's thinking stopped, on the run's clock, and whether that moment has been taken yet.
    // See <see cref="ReasoningElapsed"/> for why the freeze belongs at the thinking→answer boundary.
    private long _reasoningTicks;
    private bool _reasoningClosed;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _firstFrameTimeout;
    private CancellationTokenSource _idle = new();
    private CancellationTokenSource _suspend = new();
    private CancellationTokenSource _linked;
    // Which window is currently armed, kept because the audit line has to name the deadline that fired rather
    // than recite a constant, and which of the two it was is the difference between a stream that stopped and a
    // round that never got to speak.
    private TimeSpan _armed;

    // 1 while the round in flight has produced no frame yet. An int rather than a bool because the pipeline's
    // thread writes it and a view reads it while deciding what to say.
    private int _awaitingFirstFrame = 1;
    private int _retryWaiting;
    private int _retryWaits;
    private long _retryWaitedTicks;
    private long _retryDelayTicks;
    private string? _steerText;
    private string? _steerContext;
    private IReadOnlyList<byte[]>? _steerPictures;
    private readonly List<string> _toolOutcomes = [];
    private readonly List<ServerSearchNotice> _searches = [];
    private int _paintQueued;
    private int _compactionRequested;
    private int _wakesUsed;
    private int _spawnsUsed;
    private volatile bool _parked;
    private int _toolRunning;
    // The elapsed clock is banked and restarted rather than continuous, because a stretch spent waiting for a
    // person to approve a tool is not this answer working. The ticks are monotonic, never a time of day: a wall
    // clock stepped backwards by NTP or a timezone rule would hand the view a negative duration.
    private long _bankedTicks;
    private long _stretchStart = Stopwatch.GetTimestamp();
    private bool _stretchRunning = true;

    public string ConversationId { get; }
    public RunPhase Phase { get; private set; } = RunPhase.Streaming;
    public RunResult Result { get; private set; } = RunResult.Completed;

    internal CancellationToken Token => _linked.Token;
    internal bool StopRequested => _stop.IsCancellationRequested;

    public bool IsStreaming => Phase == RunPhase.Streaming;

    internal ConversationRun(string conversationId, TimeSpan idleTimeout, TimeSpan firstFrameTimeout)
    {
        ConversationId = conversationId;
        _idleTimeout = idleTimeout;
        _firstFrameTimeout = firstFrameTimeout;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _idle.Token, _suspend.Token);
        BeginRound();
    }

    /// <summary>How long this answer has been working, minus any stretch it spent parked on a tool approval. Read
    /// off the run rather than off a stopwatch the bubble winds itself, so a conversation switch shows the time
    /// really taken and not the time since the bubble happened to be rebuilt.</summary>
    public TimeSpan Elapsed
    {
        get { lock (_gate) return ElapsedLocked(); }
    }

    /// <summary>The same reading without the lock, for callers already inside the gate. Monitor is reentrant on
    /// one thread so this is a courtesy rather than a deadlock fix, but the reasoning clock below reads it from
    /// inside <see cref="AppendText"/> and a helper that says which contract it keeps is worth the extra line.</summary>
    private TimeSpan ElapsedLocked()
    {
        var running = _stretchRunning ? Stopwatch.GetElapsedTime(_stretchStart).Ticks : 0L;
        return TimeSpan.FromTicks(_bankedTicks + running);
    }

    /// <summary>The silence this run was owed when its deadline fired — the round's first frame or the next chunk
    /// of an answer already talking. Read where a failure gets named: an audit line saying a run timed out is
    /// worth nothing unless it also says what it was owed, and the two windows mean different things.</summary>
    internal TimeSpan IdleWindow => _armed;

    /// <summary>Whether the round now in flight has produced nothing at all yet. It is what separates "the stream
    /// stopped" from "the request never started", which is the one distinction a user can act on — the first is a
    /// provider, the second is usually a prompt the provider is still chewing on.</summary>
    internal bool AwaitingFirstFrame => Volatile.Read(ref _awaitingFirstFrame) == 1;

    /// <summary>Whether this run's own deadline is what cancelled the work. Read where a cancellation gets its
    /// blame, because the run's token is a <b>linked</b> source: the client pipeline cancels through the same
    /// token for reasons of its own, and a window measured in minutes makes that misattribution easy to land in a
    /// log and impossible to read back. Not true for a window that fired while a call waited for a person —
    /// <see cref="RearmAfterApproval"/> replaces the fired source, so a resumed request reads its own fresh
    /// deadline, which is the right answer since the parked cancellation left through a different branch.</summary>
    internal bool DeadlineFired => _idle.IsCancellationRequested;

    /// <summary>Banks the stretch just run and stops the clock. Called when the run parks on an approval and when
    /// it finishes, so the last number a viewer reads is the one the answer actually earned.</summary>
    private void FreezeClock()
    {
        lock (_gate)
        {
            if (!_stretchRunning) return;
            _bankedTicks += Stopwatch.GetElapsedTime(_stretchStart).Ticks;
            _stretchRunning = false;
        }
    }

    /// <summary>Opens the next stretch on a run that was frozen. Locking is not ceremony here: the freeze runs on
    /// the UI thread with the gate, while <see cref="RearmAfterApproval"/> is reached from a thread-pool
    /// continuation, and the view reads <see cref="Elapsed"/> three times a second on top of both.</summary>
    private void ResumeClock()
    {
        lock (_gate)
        {
            if (_stretchRunning) return;
            _stretchStart = Stopwatch.GetTimestamp();
            _stretchRunning = true;
        }
    }

    /// <summary>The text of the segment now streaming. A bubble rebuilt from this after a conversation switch
    /// shows exactly what arrived while the session was out of sight, so no view holds its own copy.</summary>
    public string LiveText
    {
        get { lock (_gate) return _text.ToString(); }
    }

    /// <summary>What the model thought on the way to <see cref="LiveText"/>, for the same segment. It is drawn
    /// while it is the only thing arriving and folds once the answer starts, and
    /// <see cref="ChatWorkspace.ShowThinking"/> decides even that much. What it never stops being is buffered: a
    /// thinking gateway insists on reading this back out of the transcript on the next request that carries tools,
    /// and withholds the answer entirely without it — so the switch hides the words, never the record.</summary>
    public string LiveReasoning
    {
        get { lock (_gate) return _reasoning.ToString(); }
    }

    public bool HasQueuedSteer
    {
        get { lock (_gate) return _steerText is not null; }
    }

    internal void AppendText(string chunk)
    {
        lock (_gate)
        {
            // The first prose of a segment that had been thinking is exactly where the thinking ended. Frozen
            // here rather than when the turn is filed, because by then the answer has typed for however long it
            // typed, and a clause thought for twelve seconds would be reported as forty-five. Frozen on the run
            // rather than read off the bubble's phase flag because a view is not a source of truth — and this one
            // is thrown away every time the user looks at another session.
            if (!_reasoningClosed && _reasoning.Length > 0 && _text.Length == 0)
            {
                _reasoningTicks = ElapsedLocked().Ticks;
                _reasoningClosed = true;
            }

            _text.Append(chunk);
        }
    }

    /// <summary>How long this segment thought, on the same clock that gives the bubble its elapsed label — which
    /// is the clock that does <i>not</i> charge a wait for an approval card to the model's reasoning.
    ///
    /// <para>A segment that said something is frozen at the thinking→answer boundary; one that never spoke has no
    /// such boundary, so it keeps accruing until it is filed, which is the right end for thinking that ran to the
    /// last token. Both readings come out of this one property so neither of the two write sites has to know
    /// which shape it is filing.</para>
    ///
    /// <para>Null means this segment never thought. That is a different answer from zero, and the copy depends on
    /// the difference: a session stored before this was recorded has to read 已思考, not 已思考 0s.</para></summary>
    public TimeSpan? ReasoningElapsed
    {
        get
        {
            lock (_gate)
            {
                if (_reasoningClosed) return TimeSpan.FromTicks(_reasoningTicks);
                return _reasoning.Length > 0 ? ElapsedLocked() : null;
            }
        }
    }

    /// <summary>Whether some <i>earlier</i> segment of this run already handed thinking to the transcript. That is
    /// the question the live bubble asks before drawing a thinking row: a tool loop thinks once per round, and
    /// showing the card every round would flash it above the group a hundred and twenty-seven times in a run that
    /// made that many calls.
    ///
    /// <para>Derived from the filed-reasoning tally rather than from a flag set at paint time, because "first
    /// round" and "first frame" are not the same question — a flag armed on the first paint would hide the row
    /// from the second paint of the very round it was meant to show.</para></summary>
    internal bool ThoughtFiledBefore
    {
        get { lock (_gate) return _filedReasoning > 0; }
    }

    internal void AppendReasoning(string chunk)
    {
        lock (_gate) _reasoning.Append(chunk);
    }

    /// <summary>Starts the next segment. The previous one was written to the transcript, so the buffer must not
    /// carry it into the bubble again — and the thinking must not be carried either, or the second assistant
    /// message of a tool loop would be charged with the first one's reasoning. The deadline is re-armed for the
    /// round this segment is about to ask for: between segments is the one stretch that owes a first frame and
    /// had no window of its own, which is how a steered turn inherited the leftover of the answer before it.</summary>
    internal void BeginSegment()
    {
        lock (_gate)
        {
            // Whatever this segment had said was handed over when it ended — as its own assistant turn, or as the
            // prose riding a tool call — so this is the one point both doors pass through, and the run's answer is
            // the sum rather than the last buffer. Kept here instead of at the two write sites because a count
            // that a caller has to remember to take is a count that a new write site forgets.
            _filedText += _text.Length;
            _filedReasoning += _reasoning.Length;
            _text.Clear();
            _reasoning.Clear();
            // The next segment thinks again, so the clock re-arms with it. <see cref="ThoughtShownLive"/> is
            // deliberately not reset here — it answers "has this run ever shown thinking", and a run is one
            // answer however many rounds of tool calls it took to get there.
            _reasoningClosed = false;
            _reasoningTicks = 0L;
            _spinStopped = false;
            // Each segment is answered by its own response, so the reason belongs to the one now starting.
            _finishReason = null;
        }

        BeginRound();
    }

    private int _filedText;
    private int _filedReasoning;

    /// <summary>Whether the model said anything at all across this run — text or thinking, still in the bubble or
    /// already handed to the transcript. A run that spoke over three tool calls and then stopped on an empty
    /// message did answer, and only the last segment's buffer would have disagreed; this is the reading that
    /// decides whether the person is told the model returned nothing.</summary>
    internal bool SaidAnything
    {
        get
        {
            lock (_gate)
                return _filedText > 0 || _filedReasoning > 0 || _text.Length > 0 || _reasoning.Length > 0;
        }
    }

    private string? _finishReason;

    /// <summary>Why the endpoint closed the response of the segment now running, in its own token.</summary>
    internal void NoteFinishReason(string reason) => Volatile.Write(ref _finishReason, reason);

    internal string? FinishReason => Volatile.Read(ref _finishReason);

    private bool _spinStopped;

    /// <summary>Set when the spin guard refused the same signature a second time and ended the loop with
    /// <c>Terminate</c>. The stream then ends without any text — the model was asking, not answering — and
    /// without this the person would be told the model "returned no content", which blames the wrong thing:
    /// the model answered every question it was asked, it was just asking the same one too many times.</summary>
    internal void NoteSpinStopped() => Volatile.Write(ref _spinStopped, true);

    internal bool SpinStopped => Volatile.Read(ref _spinStopped);

    /// <summary>Re-arms the inactivity deadline for the next frame of output, on the silence window: this round
    /// has already spoken, so a gap now means the stream stopped. A parked run has no stream, so nothing re-arms
    /// it — waiting for a human is not the same as stalling — and neither does a tool that owns the run, which is
    /// silent by nature; <see cref="EndTool"/> restarts the count once the tool comes back.</summary>
    internal void Touch()
    {
        if (_parked || Volatile.Read(ref _toolRunning) != 0) return;
        Volatile.Write(ref _awaitingFirstFrame, 0);
        Arm(_idleTimeout);
    }

    /// <summary>Arms the deadline for a round whose request has not been answered yet. This is a longer window
    /// than <see cref="Touch"/> uses, and the difference is not caution about a slow provider — it is that the
    /// stretch being measured is a different thing. After a tool result lands, the next silence is upload plus
    /// the gateway's own queue plus the prefill of a transcript that can run to two hundred thousand tokens, and
    /// none of it says the stream died. The first frame of the round hands the run back to
    /// <see cref="Touch"/>, and never back to this window.</summary>
    internal void BeginRound()
    {
        if (_parked || Volatile.Read(ref _toolRunning) != 0) return;
        Volatile.Write(ref _awaitingFirstFrame, 1);
        Arm(_firstFrameTimeout);
    }

    private void Arm(TimeSpan window)
    {
        _armed = window;
        // CancelAfter is a no-op once the source fired and throws once it is disposed; disposal happens only
        // after the pump is finished with this run.
        if (!_idle.IsCancellationRequested) _idle.CancelAfter(window);
    }

    /// <summary>Hands the run to a tool for the duration. No model output is owed while it runs, so the
    /// inactivity deadline stands down rather than mistaking a long build for a dead stream; the tool judges
    /// that stretch by its own silence bound, and answers with output the model can read instead of a network
    /// error. Reached only for a call the gate let run, so the deadline is never stopped while a person decides.
    /// The user's stop still cancels the tool, because it travels on the same token.
    ///
    /// <para>A count rather than a flag, because one response can hand over several calls and the whole batch is
    /// decided before any of it runs: every call of the batch opens its bracket at its own gate, so a flag would be
    /// cleared by whichever sibling came back first and start counting silence while the rest were still working.</para>
    /// </summary>
    internal void BeginTool()
    {
        Interlocked.Increment(ref _toolRunning);
        _idle.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    /// <summary>A tool came back, or was refused. The deadline starts counting again only when the <i>last</i>
    /// bracket closes, and what it counts from then is a first frame rather than another chunk — the round that
    /// follows a result has to be built, sent and read before it can say anything. A refused call never entered
    /// the bracket, so ending it for one simply re-arms.</summary>
    internal void EndTool()
    {
        if (Interlocked.Decrement(ref _toolRunning) != 0) return;
        BeginRound();
    }

    /// <summary>Hands the run to the retry policy's sleep for the duration. The tool bracket's reasoning applies
    /// one layer further down: while the pipeline is obeying a provider's <c>Retry-After</c> there is no stream
    /// to be silent about, and a deadline that fired through that wait reported a rate limit as a dead connection
    /// — which is the wrong diagnosis and sends a person to check the wrong thing. The user's stop still cancels
    /// the wait, because it travels on the same token.</summary>
    internal void BeginRetryWait(int attempt, TimeSpan delay)
    {
        if (_parked) return;
        Interlocked.Exchange(ref _retryWaiting, attempt);
        Interlocked.Exchange(ref _retryDelayTicks, delay.Ticks);
        Interlocked.Increment(ref _retryWaits);
        Interlocked.Add(ref _retryWaitedTicks, delay.Ticks);
        _idle.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    /// <summary>The wait is over and the next attempt is on its way: what that round owes is still a first frame,
    /// so the deadline comes back as the round's window rather than the silence one.</summary>
    internal void EndRetryWait()
    {
        Interlocked.Exchange(ref _retryWaiting, 0);
        BeginRound();
    }

    /// <summary>Whether a rate-limit wait is in progress, and what it was asked for: the attempt now pending and
    /// how long the provider said to wait. A bubble rebuilt after a conversation switch reads this rather than
    /// remembering it, because a wait that is still happening is still worth saying.</summary>
    internal bool RetryWaiting => Volatile.Read(ref _retryWaiting) != 0;

    internal (int Attempt, TimeSpan Delay) RetryWaitNotice()
        => (Volatile.Read(ref _retryWaiting), TimeSpan.FromTicks(Interlocked.Read(ref _retryDelayTicks)));

    /// <summary>What the retries cost this run, for the audit line that has to say whether a silence was the
    /// provider's doing.</summary>
    internal (int Waits, TimeSpan Waited) RetryCost()
        => (Volatile.Read(ref _retryWaits), TimeSpan.FromTicks(Interlocked.Read(ref _retryWaitedTicks)));

    internal void RequestStop() => _stop.Cancel();

    /// <summary>Ends the in-flight request without ending the run. This is the only cancellation a parked
    /// approval uses: the stream goes away, the decision stays, and the slot is still held.</summary>
    internal void SuspendForApproval() => _suspend.Cancel();

    /// <summary>Marks the run as waiting. Called from the gate, before the pipeline is told to park.</summary>
    internal void ParkForApproval()
    {
        _parked = true;
        Phase = RunPhase.AwaitingApproval;
        // A person reading a card is not the answer working, so the elapsed clock banks what it has and stands
        // down for the length of the decision.
        FreezeClock();
    }

    /// <summary>
    /// Takes the run out of the parked state with a fresh inactivity deadline and its elapsed clock running
    /// again. The old one may well have fired while a person was deciding, and an expired deadline reused for
    /// the resumed stream would cancel the first request it made; the old sources are dropped rather than reused
    /// for the same reason.
    /// </summary>
    internal void RearmAfterApproval()
    {
        var staleIdle = _idle;
        var staleSuspend = _suspend;
        var staleLinked = _linked;

        _idle = new CancellationTokenSource();
        _suspend = new CancellationTokenSource();
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _idle.Token, _suspend.Token);
        _parked = false;
        Phase = RunPhase.Streaming;
        ResumeClock();
        BeginRound();

        // Nothing holds the old token any more — the stream that used it ended when the run parked.
        staleLinked.Dispose();
        staleSuspend.Dispose();
        staleIdle.Dispose();
    }

    internal void QueueSteer(string text, string? context, IReadOnlyList<byte[]>? pictures = null)
    {
        lock (_gate)
        {
            _steerText = text;
            _steerContext = context;
            _steerPictures = pictures;
            SteerCount++;
        }
    }

    /// <summary>How many times the person reached into this reply to steer it. Nothing says "the answer was going
    /// wrong" more directly, which is why the router treats one steer as the strongest signal in its table — and
    /// why it is a count kept on the run rather than something re-derived from the transcript, where a steered
    /// turn looks exactly like an ordinary one.</summary>
    internal int SteerCount { get; private set; }

    internal bool TryTakeSteer(out string text, out string? context, out IReadOnlyList<byte[]>? pictures)
    {
        lock (_gate)
        {
            if (_steerText is null)
            {
                text = "";
                context = null;
                pictures = null;
                return false;
            }

            text = _steerText;
            context = _steerContext;
            pictures = _steerPictures;
            _steerText = null;
            _steerContext = null;
            _steerPictures = null;
            return true;
        }
    }

    /// <summary>Coalesces paints: the first chunk of a frame asks, the rest are folded into that one.</summary>
    internal bool TryRequestPaint() => Interlocked.Exchange(ref _paintQueued, 1) == 0;

    /// <summary>
    /// Asks the pump to consider compacting. A flag rather than a call because the only place that knows a tool
    /// result just landed is inside the model's tool loop, where nothing may be awaited — the pump picks it up
    /// between segments, which is the nearest point with no stream open.
    /// </summary>
    internal void RequestCompaction() => Interlocked.Exchange(ref _compactionRequested, 1);

    internal bool TryTakeCompactionRequest() => Interlocked.Exchange(ref _compactionRequested, 0) == 1;

    /// <summary>What each tool call did, in order, for the project's daily log. Recorded rather than derived from
    /// the transcript because the transcript does not say whether a call was allowed, approved or refused.</summary>
    internal void RecordToolOutcome(string outcome)
    {
        lock (_gate) _toolOutcomes.Add(outcome);
    }

    /// <summary>How many other sessions this run has started answering. Counted on the run, not on the pair of
    /// sessions, because the loop that has to stop is one assistant waking others — and a run that survives its
    /// own tool calls keeps the count it accumulated.</summary>
    internal int WakesUsed
    {
        get { lock (_gate) return _wakesUsed; }
    }

    internal void SpendWake()
    {
        lock (_gate) _wakesUsed++;
    }

    /// <summary>Children this answer has already started. Counted on the run like <see cref="WakesUsed"/>, and
    /// kept separate from it because the two limits answer different questions: a wake reuses a session that
    /// exists, a spawn creates one, so the spawn budget is one per answer even though wakes are two.</summary>
    internal int SpawnsUsed
    {
        get { lock (_gate) return _spawnsUsed; }
    }

    internal void SpendSpawn()
    {
        lock (_gate) _spawnsUsed++;
    }

    internal IReadOnlyList<string> ToolOutcomes
    {
        get { lock (_gate) return [.. _toolOutcomes]; }
    }

    /// <summary>An endpoint-side search this answer reported, folded into the one entry for its call id.
    ///
    /// <para>The bridge sends the same search twice — a call item that carries the queries and may arrive before
    /// the endpoint has any, then a result item that carries the addresses — and a transcript that drew two rows
    /// for one search would read like the model looked twice. Merging by <see cref="ServerSearchNotice.CallId"/>
    /// keeps the pair as one fact. An unnamed notice is never merged with another: two searches that both failed
    /// to say which they were are two searches, and collapsing them would invent a query for one of them.</para>
    ///
    /// <para>A start with nothing in it is still kept, because that is what lets the row say "searching" while
    /// the endpoint works; <see cref="StoredSearches"/> drops it again before the turn is saved, so a search that
    /// never produced a query or an address leaves no trace in the conversation file.</para></summary>
    internal void RecordSearch(ServerSearchNotice notice)
    {
        lock (_gate)
        {
            var index = notice.CallId.Length > 0
                ? _searches.FindIndex(existing => string.Equals(existing.CallId, notice.CallId, StringComparison.Ordinal))
                : -1;
            if (index < 0)
            {
                if (!notice.IsEmpty || notice.Started) _searches.Add(notice);
                return;
            }

            var existing = _searches[index];
            _searches[index] = existing with
            {
                Queries = Joined(existing.Queries, notice.Queries),
                Sources = Joined(existing.Sources, notice.Sources),
                Started = notice.Started || existing.Started && notice.Queries.Count == 0,
            };
        }
    }

    /// <summary>The searches as the run holds them, oldest first. Read on the UI thread while the answer streams
    /// (the row under the bubble) and once more when the reply is recorded.</summary>
    internal ServerSearchLog Searches
    {
        get { lock (_gate) return new ServerSearchLog([.. _searches]); }
    }

    /// <summary>The stored line for the turn this answer becomes, or null when the endpoint searched nothing worth
    /// recording. The empty case is null rather than an empty document because a reload has to keep "this provider
    /// searched and reported nothing" apart from "this reply never searched".</summary>
    internal string? StoredSearches()
    {
        lock (_gate) return new ServerSearchLog([.. _searches]).ToStored();
    }

    private static IReadOnlyList<string> Joined(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
    {
        var joined = new List<string>();
        foreach (var value in (first ?? []).Concat(second ?? []))
            if (!joined.Contains(value, StringComparer.Ordinal)) joined.Add(value);
        return joined;
    }

    /// <summary>How many calls this answer has already finished. The count lives here because a bubble rebuilt
    /// after a conversation switch has to be able to say the same thing the one it replaced was saying.</summary>
    internal int CompletedToolCalls
    {
        get { lock (_gate) return _toolOutcomes.Count; }
    }

    /// <summary>Whether a tool owns the run right now — the one phase the glyph cannot infer from the text.</summary>
    internal bool IsToolRunning => Volatile.Read(ref _toolRunning) != 0;

    internal void ClearPaintRequest() => Interlocked.Exchange(ref _paintQueued, 0);

    internal void Finish(RunResult result)
    {
        Phase = RunPhase.Completed;
        Result = result;
        // The last repaint reads this number after the run has already left the registry, so freezing it here is
        // what keeps the elapsed the viewer saw from drifting afterwards.
        FreezeClock();
    }

    public void Dispose()
    {
        _linked.Dispose();
        _suspend.Dispose();
        _idle.Dispose();
        _stop.Dispose();
    }
}

/// <summary>What the panel needs to say when a run ends. The workspace chooses the key — it is the only place
/// that knows which exception was thrown — and the panel resolves it, so localization stays in the view and
/// the strings never have to be matched against.</summary>
internal readonly record struct RunOutcome(
    RunResult Result,
    string? NoticeKey,
    bool NoticeDanger,
    string? Detail,
    // How many of the person's own messages the transcript held at that moment. The panel ages a notice out on
    // the next one, and a run that ended out of sight has no bubble of its own to read the count from — the
    // workspace is the only place still holding that conversation here.
    int UserTurns);
