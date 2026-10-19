using System;
using System.Diagnostics;
using System.Threading;

namespace AxmolHub;

/// <summary>One open operation, retained until its dispatcher observation completes. All durations are cumulative
/// or explicitly named stages; Loaded is a dispatcher proxy, not a compositor presentation timestamp.</summary>
internal sealed class SessionOpenMeasurement(string conversationId, string origin)
{
    private static long _nextId;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _allocated = GC.GetTotalAllocatedBytes(false);
    private readonly int[] _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    internal long Id { get; } = Interlocked.Increment(ref _nextId);
    internal string ConversationId { get; } = conversationId;
    internal string Origin { get; } = origin;
    internal double ElapsedMs => _clock.Elapsed.TotalMilliseconds;
    internal double LoadMs { get; set; }
    internal double ApprovalMs { get; set; }
    internal double NotifyMs { get; set; }
    internal double SyntaxMs { get; set; }
    internal double MarkdownMs { get; set; }
    internal double HighlightMs { get; set; }

    [ThreadStatic] internal static SessionOpenMeasurement? Rendering;
    internal bool Cached { get; set; }
    internal int Reloads { get; set; }
    internal double RenderMs { get; set; }
    internal double ReloadMs { get; set; }
    internal bool Reused { get; set; }
    internal int TotalTurns { get; set; }
    internal int PaintedTurns { get; set; }
    internal int Rows { get; set; }
    /// <summary>Tool and thinking rows, which carry <c>activity-row</c> rather than <c>message-row</c>: a window
    /// of nothing but machine turns has no message rows at all, and a metric that reported zero would be lying
    /// about how much was materialized.</summary>
    internal int ActivityRows { get; set; }
    internal int FlowChildren { get; set; }
    internal int MarkdownDocuments { get; set; }
    internal int Editors { get; set; }
    /// <summary>How many coverage repairs this open needed before its observation completed. Zero is the healthy
    /// case: the first follow landed and no host had to be reset.</summary>
    internal int Repairs { get; set; }
    internal string LastRepairTrigger { get; set; } = "";
    internal bool ObservationQueued { get; set; }
    internal bool Completed { get; set; }
    internal double ObservedMs { get; private set; }

    private string? _completedLine;

    internal string Complete(bool stillViewed)
    {
        if (_completedLine is not null) return _completedLine;
        ObservedMs = ElapsedMs;
        Completed = true;
        return _completedLine = FormattableString.Invariant($"Session open: {ConversationId} · operation={Id} origin={Origin}")
            + FormattableString.Invariant($" · total={TotalTurns} painted={PaintedTurns} messageRows={Rows} activityRows={ActivityRows} flowChildren={FlowChildren} markdown={MarkdownDocuments} editors={Editors}")
            + FormattableString.Invariant($" · read={LoadMs:0.0}ms cache={(Cached ? "hit" : "miss")} approval={ApprovalMs:0.0}ms notify={NotifyMs:0.0}ms")
            + FormattableString.Invariant($" · render={RenderMs:0.0}ms reload={ReloadMs:0.0}ms rows={(Reused ? "reused" : "drawn")}")
            + FormattableString.Invariant($" · syntax={SyntaxMs:0.0}ms markdown={MarkdownMs:0.0}ms highlightObserved={HighlightMs:0.0}ms")
            + FormattableString.Invariant($" · openToLoaded={ElapsedMs:0.0}ms reloads={Reloads} stillViewed={stillViewed} presentation=unavailable")
            + FormattableString.Invariant($" · repairs={Repairs}{(LastRepairTrigger.Length > 0 ? " last=" + LastRepairTrigger : "")}")
            + FormattableString.Invariant($" · allocatedProcessBytes={GC.GetTotalAllocatedBytes(false) - _allocated}")
            + FormattableString.Invariant($" gc={GC.CollectionCount(0) - _collections[0]}/{GC.CollectionCount(1) - _collections[1]}/{GC.CollectionCount(2) - _collections[2]}");
    }
}
