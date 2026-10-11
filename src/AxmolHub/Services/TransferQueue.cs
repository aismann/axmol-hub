using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The app's downloads, as a list rather than as one exclusive operation.
///
/// Before this there was no queue at all: <see cref="HubWorkspace.ExecuteAsync"/> held a single
/// <c>_operation</c> slot, so a second download was **dropped** instead of waiting, and the whole
/// content area was greyed out for the duration of the first one. A download is not that kind of work —
/// nothing on screen depends on the bytes arriving, and the transport already survives a quit
/// (<c>DownloadManager</c> keeps a <c>.partial</c> and resumes with a Range request). This service is
/// what makes that visible: several transfers, each with its own identity and progress, at most
/// <see cref="MaxConcurrent"/> moving at once, the rest waiting.
///
/// **What goes in**: work whose only job is moving bytes to disk — an engine package, the host shell's
/// MSI, Hub's own update. **What stays on <see cref="HubWorkspace.ExecuteAsync"/>**: work that mutates
/// state the user is looking at and must not interleave — builds, uninstall, project import. The test
/// is "can I keep using the app while this runs", and for a download the answer is yes.
///
/// **Progress is whatever the transport can actually measure**, never an invented number. A
/// <see cref="DownloadManager"/> transfer reports bytes and a total only when the server said so;
/// Velopack reports a percentage and no bytes at all. Both fit here, and a row renders <c>—</c> where a
/// transport has nothing to say rather than guessing a size from a tooltip estimate.
///
/// **Threading**: call from the UI thread, and do not <c>ConfigureAwait(false)</c> inside the work
/// delegate — completion runs the caller's <c>onFinished</c> (which saves state) and raises
/// <see cref="Changed"/> for the shell, both of which belong on the thread that owns the objects they
/// touch. Same discipline as <see cref="UpdateService"/>.
/// </summary>
internal sealed class TransferQueue
{
    /// <summary>
    /// Transfers allowed to move at once. Two, not more: a GB-scale engine package off a mirror is the
    /// common case, and past two the items only slow each other down while the list gets hard to read.
    /// Hub's own update goes through this same gate, which is the point of having one — it used to run
    /// on its own cancellation token and could race an engine install for bandwidth with neither side
    /// knowing the other existed.
    /// </summary>
    public const int MaxConcurrent = 2;

    private static readonly Lazy<TransferQueue> Shared = new(() => new TransferQueue());

    public static TransferQueue Instance => Shared.Value;

    private TransferQueue()
    {
    }

    /// <summary>
    /// Where one transfer is. Six phases, and each is something a transport can report truthfully:
    /// there is deliberately no "verifying" phase, because the SHA-256 pass runs inside
    /// <see cref="DownloadManager.DownloadAsync"/> and cannot be observed from here.
    /// </summary>
    public enum Phase
    {
        /// <summary>Waiting for a slot.</summary>
        Queued,
        /// <summary>Bytes are moving.</summary>
        Transferring,
        /// <summary>
        /// The bytes are on disk and the rest is local: unzipping an engine, or Velopack patching a
        /// delta. Unmeasurable, so the percentage is dropped rather than held at 99.
        /// </summary>
        Applying,
        Done,
        Failed,
        Cancelled,
    }

    /// <summary>What the transport reported last. Every field is nullable because different transports
    /// know different things.</summary>
    public sealed record Progress(long? Bytes, long? Total, double? BytesPerSecond, int? Percent)
    {
        public static readonly Progress Unknown = new(null, null, null, null);
    }

    /// <summary>One entry in the list. Created by <see cref="Enqueue"/>.</summary>
    public sealed class Item(string identity, string titleKey, string? subject)
    {
        /// <summary>
        /// Stable key saying *what* is being fetched, so the same engine can never download twice at
        /// once: <c>engine:install:{version}</c>, <c>engine:repair:{version}:{channel}</c>,
        /// <c>hostshell:msi</c>, <c>hub:update:{version}</c>.
        /// </summary>
        public string Identity { get; } = identity;

        /// <summary>UI-string key naming the kind of thing being fetched.</summary>
        public string TitleKey { get; } = titleKey;

        /// <summary>
        /// Which one: a version, a name. Appended to the title rather than formatted into it, because it
        /// is never a localized word — and a format key would leak its <c>{0}</c> on the one path
        /// (<see cref="HubWorkspace.InstallEngineAsync"/> with no version) where there is nothing to
        /// substitute yet.
        /// </summary>
        public string? Subject { get; } = subject;

        public Phase State { get; private set; } = Phase.Queued;

        public Progress Report { get; private set; } = Progress.Unknown;

        /// <summary>The failure message, or null. Shown inline on the row.</summary>
        public string? Error { get; private set; }

        /// <summary>
        /// The exception behind <see cref="Error"/>, kept whole. The row only needs the message, but the
        /// caller that owns the work needs the type: <see cref="HubWorkspace.ShowOperationErrorAsync"/>
        /// chooses its advice by exception, and the log keeps the stack trace.
        /// </summary>
        public Exception? Fault { get; private set; }

        /// <summary>
        /// Completes when the transfer settles — success, failure, or cancel — after the caller's
        /// completion work has run. A download no longer blocks the UI, but a caller that genuinely
        /// needs "is it installed yet" (the deep-link path, the version picker) still awaits this, so
        /// moving onto the queue does not quietly change what <c>await</c> means.
        /// </summary>
        public Task Completion => _completion.Task;

        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenSource? Cancellation { get; set; }
        internal Action? CancelAction { get; set; }
        internal Task? Running { get; set; }
        /// <summary>The transfer itself. Gets its own row so it can report what it is doing.</summary>
        internal Func<Item, CancellationToken, Task> Work { get; init; } = (_, _) => Task.CompletedTask;
        internal Action<Item>? OnFinished { get; init; }

        /// <summary>Wired when the transfer starts. A measurement is a state change as far as the strip is
        /// concerned: without this the row would only ever repaint on a phase change, which for a download
        /// is once at the start and once at the end.</summary>
        internal Action? OnProgress { get; set; }

        public string Title => Subject is { Length: > 0 } value
            ? HubStrings.Get(TitleKey) + " " + value
            : HubStrings.Get(TitleKey);

        public bool IsActive => State is Phase.Queued or Phase.Transferring or Phase.Applying;

        /// <summary>Whether this item occupies one of the <see cref="MaxConcurrent"/> slots right now.</summary>
        public bool IsMoving => State is Phase.Transferring or Phase.Applying;

        /// <summary>0-100 for the row's track: the measured percentage, full once done, and nothing while
        /// the remaining work is local and unmeasurable.</summary>
        public double? Fill => Report.Percent is { } percent
            ? Math.Clamp(percent, 0, 100)
            : State == Phase.Done ? 100 : null;

        /// <summary>Bytes-measuring transports. <see cref="DownloadProgress.Bytes"/> already counts the
        /// bytes a resumed transfer found on disk, which is what the row should show.</summary>
        public void ReportBytes(DownloadProgress progress)
        {
            Report = new Progress(
                progress.Bytes,
                progress.Total,
                progress.BytesPerSecond,
                progress.Total > 0 ? (int)Math.Clamp(progress.Bytes * 100.0 / progress.Total!.Value, 0, 100) : null);
            OnProgress?.Invoke();
        }

        /// <summary>Percentage-only transports (Velopack). Leaves the byte fields null on purpose.</summary>
        public void ReportPercent(int percent)
        {
            Report = Report with { Percent = percent };
            OnProgress?.Invoke();
        }

        /// <summary>The bytes are down and the rest is local work.</summary>
        public void MarkApplying()
        {
            State = Phase.Applying;
            OnProgress?.Invoke();
        }

        /// <summary>The phase as the row words it. Said here so every surface uses one word for one
        /// state, the same reason <see cref="UpdateService.PendingVersion"/> is read rather than rebuilt.</summary>
        public string PhaseText => HubStrings.Get(State switch
        {
            Phase.Queued => "TransferQueued",
            Phase.Transferring => "TransferTransferring",
            Phase.Applying => "TransferApplying",
            Phase.Done => "TransferDone",
            Phase.Cancelled => "TransferCancelled",
            _ => "TransferFailed",
        });

        /// <summary>
        /// What the transport can prove, in words. Bytes for a transport that counts them, a percentage
        /// for one that only has that (Velopack), and an em-dash for a phase that cannot be measured —
        /// never a zero, which would read as a download that stopped.
        /// </summary>
        public string MeasureText => Report switch
        {
            { Bytes: { } bytes } => FormatBytes(bytes) + " / " + FormatBytes(Report.Total)
                + (Report.BytesPerSecond is { } rate && rate > 0 ? " · " + UpdateService.FormatSpeed(rate) : ""),
            { Percent: { } percent } => percent + "%",
            _ => HubStrings.Get("TransferNoMeasure"),
        };

        internal void BeginTransferring() => State = Phase.Transferring;

        internal void Succeed() => State = Phase.Done;
        internal void Abort() => State = Phase.Cancelled;

        internal void Fail(Exception fault)
        {
            Fault = fault;
            Error = fault.Message;
            State = Phase.Failed;
        }

        internal void Fail(string? message)
        {
            Fault = null;
            Error = message;
            State = Phase.Failed;
        }

        internal void Complete() => _completion.TrySetResult();
    }

    private readonly List<Item> _items = [];

    /// <summary>The list as the UI should read it — a snapshot, so an item settling mid-render cannot
    /// shift the rows under the popover that is drawing them.</summary>
    public IReadOnlyList<Item> Items { get; private set; } = [];

    /// <summary>Raised whenever the list or any item's state changes. Handlers marshal to their own
    /// thread before touching a control, exactly like <see cref="UpdateService.Changed"/>.</summary>
    public event Action? Changed;

    public int ActiveCount => _items.Count(item => item.IsActive);

    public int FailedCount => _items.Count(item => item.State == Phase.Failed);

    /// <summary>Mebibytes to one decimal, the same reading the status line printed before there were rows.
    /// A null size stays an em-dash: a download whose total the server never said is not a zero-byte one.</summary>
    internal static string FormatBytes(long? bytes) => bytes is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value / 1048576.0:F1} MB")
        : HubStrings.Get("TransferNoMeasure");

    /// <summary>The item running or waiting under this identity, or null. This is what the engine
    /// version picker asks so a downloading version cannot be picked again.</summary>
    public Item? FindActive(string identity)
        => _items.FirstOrDefault(item => item.Identity == identity && item.IsActive);

    /// <summary>
    /// Adds a transfer and starts it when a slot is free. Returns null when that identity is already
    /// running: the caller's answer there is "nothing to do", not an exception, because the duplicate is
    /// a normal click once the picker is out of the way (a deep link, or two clicks in one frame).
    /// Finished rows are dropped first, so the list is about this batch rather than every download since
    /// launch — failures excepted, which <see cref="ClearFinished"/> keeps on purpose.
    /// </summary>
    public Item? Enqueue(
        string identity,
        string titleKey,
        string? subject,
        Func<Item, CancellationToken, Task> work,
        Action<Item>? onFinished = null)
    {
        if (FindActive(identity) is not null)
        {
            return null;
        }

        ClearFinished();
        var item = new Item(identity, titleKey, subject)
        {
            Work = work,
            OnFinished = onFinished,
        };
        _items.Add(item);
        Items = _items.ToArray();
        Pump();
        return item;
    }

    /// <summary>Asks one transfer to stop. For a running transfer this cancels the token its work runs on,
    /// and the work then lands in <see cref="OperationCanceledException"/>. A transfer still waiting has no
    /// token to cancel — and settling it here is what keeps "cancel" from meaning "start as soon as a slot
    /// frees", which is what dropping the request on the floor would turn into.</summary>
    public void Cancel(string identity)
    {
        if (FindActive(identity) is not { } item)
        {
            return;
        }

        if (item.CancelAction is { } cancel)
        {
            cancel();
            return;
        }

        item.Abort();
        Changed?.Invoke();
        Pump();
    }

    /// <summary>Forgets the transfers that ended quietly. A failure stays: it is still something the user
    /// has to see and act on.</summary>
    public void ClearFinished()
    {
        var done = _items.Where(item => item.State is Phase.Done or Phase.Cancelled).ToList();
        if (done.Count == 0)
        {
            return;
        }

        foreach (var item in done)
        {
            _items.Remove(item);
        }

        Items = _items.ToArray();
        Changed?.Invoke();
    }

    /// <summary>
    /// Verification hook (<c>--verify-shell</c>): a transfer that never finishes on its own, standing in
    /// for a download the suite cannot really run. Settle it with
    /// <see cref="FinishTransferForCheck"/>; <see cref="ClearForCheck"/> takes it away.
    /// </summary>
    internal Item? EnqueueForCheck(string identity, string titleKey, string? subject = null)
        => Enqueue(identity, titleKey, subject, (_, token) => Task.Delay(Timeout.Infinite, token));

    /// <summary>Verification hook: settles a scripted transfer without touching its cancellation.</summary>
    internal void FinishTransferForCheck(string identity, Phase state, string? error = null)
    {
        if (_items.FirstOrDefault(item => item.Identity == identity) is not { } item)
        {
            return;
        }

        switch (state)
        {
            case Phase.Done:
                item.Succeed();
                break;
            case Phase.Cancelled:
                item.Abort();
                break;
            default:
                item.Fail(error);
                break;
        }

        item.Complete();
        Changed?.Invoke();
    }

    /// <summary>Verification hook: drops the whole list without cancelling, so a scripted batch cannot
    /// leak into the render checks that follow.</summary>
    internal void ClearForCheck()
    {
        foreach (var item in _items)
        {
            // Stop the scripted work rather than orphaning it, and release anyone awaiting a transfer the
            // suite is throwing away — a stuck await is how a self-check turns into a timeout.
            item.CancelAction?.Invoke();
            item.CancelAction = null;
            item.OnProgress = null;
            item.Running = null;
            item.Complete();
        }

        _items.Clear();
        Items = [];
        Changed?.Invoke();
    }

    /// <summary>Starts as many waiting transfers as the gate allows. Every state change ends here, so a
    /// finished transfer hands its slot to the next one with no timer involved.</summary>
    private void Pump()
    {
        var free = MaxConcurrent - _items.Count(item => item.IsMoving);
        if (free <= 0)
        {
            return;
        }

        foreach (var item in _items.Where(item => item.State == Phase.Queued).Take(free))
        {
            Start(item);
        }
    }

    private void Start(Item item)
    {
        item.Cancellation = new CancellationTokenSource();
        item.CancelAction = () => item.Cancellation?.Cancel();
        item.OnProgress = () => Changed?.Invoke();
        item.BeginTransferring();
        item.Running = RunAsync(item, item.Cancellation.Token);
        Changed?.Invoke();
    }

    private async Task RunAsync(Item item, CancellationToken token)
    {
        try
        {
            await item.Work(item, token);
            item.Succeed();
        }
        catch (OperationCanceledException)
        {
            item.Abort();
        }
        catch (Exception ex)
        {
            item.Fail(ex);
        }
        finally
        {
            item.Cancellation?.Dispose();
            item.Cancellation = null;
            item.CancelAction = null;
            item.OnProgress = null;

            // The caller's completion work (save state, log, surface the failure) runs once the item has
            // settled, so a handler that throws cannot leave the list showing a transfer that is no
            // longer running: it turns a finished download into a failed one, which is the honest reading
            // of "the bytes arrived but the app could not record them". All of this stays on this thread
            // on purpose — see the threading note on this type.
            try
            {
                item.OnFinished?.Invoke(item);
            }
            catch (Exception ex)
            {
                if (item.State == Phase.Done)
                {
                    item.Fail(ex);
                }
            }

            item.Complete();
            Changed?.Invoke();
            Pump();
        }
    }
}
