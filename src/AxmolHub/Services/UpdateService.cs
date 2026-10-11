using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxmolHub.Core;
using Velopack;
using Velopack.Sources;

namespace AxmolHub;

/// <summary>
/// The Velopack self-update bridge. <c>VelopackApp.Build().Run()</c> (called first in
/// <c>Program.Main</c>) only handles the <c>--veloapp-*</c> hook callbacks the updater passes in
/// when it re-launches the process to apply an update — it does **not** check for updates on its
/// own. Checking, downloading, and applying are all explicit calls into <see cref="UpdateManager"/>,
/// and this type is the single place Hub makes them.
///
/// There is deliberately **no modal update prompt**. An update is surfaced by the same thing that surfaces
/// every other download: a row in <see cref="TransferQueue"/>, whose slot in the status strip counts it and
/// whose popover carries the percentage. The settings page keeps what is not progress — the channel, the
/// auto-download switch, and the version this would move to.
///
/// Two drivers share this service: the silent startup check (<see cref="CheckAsync"/>) and the settings-page
/// button (also <see cref="CheckAsync"/>, then <see cref="DownloadAndApplyAsync"/>). With
/// <see cref="AutoDownload"/> on, the download starts on its own once a check finds an update; applying stays
/// an explicit restart (<see cref="ApplyAndRestart"/>), which the strip's slot offers as soon as the bytes
/// have landed.
///
/// Everything is deliberately fire-and-forget friendly: no call blocks <c>Main</c>, and failures are
/// swallowed into a result so the UI can decide what to say. A broken update check must never take
/// the whole app down with it.
///
/// **Threading**: call from the UI thread. These methods deliberately do **not** use
/// <c>ConfigureAwait(false)</c> — the download continuation, the progress callback, and
/// <see cref="Changed"/> are all meant to resume on the caller's (UI) thread. Touching Avalonia from
/// a thread-pool thread is exactly what throws "The calling thread cannot access this object because
/// a different thread owns it" (an earlier bug here did that and crashed the app).
/// </summary>
internal sealed class UpdateService
{
    /// <summary>
    /// The GitHub repository Hub ships its releases from. Hard-coded on purpose: the release feed
    /// location is part of the product's identity (same as the engine's official index), not a
    /// user preference — and it must stay correct even if the user changes the *engine* download
    /// source in Settings, which is a different host for a different file.
    /// </summary>
    private const string RepositoryUrl = "https://github.com/axmolengine/axmol-hub";

    private static readonly Lazy<UpdateService> Shared = new(() => new UpdateService());

    public static UpdateService Instance => Shared.Value;

    /// <summary>The outcome of a check, carried back to the UI so it can choose its copy.</summary>
    public enum CheckResult
    {
        /// <summary>An update is available.</summary>
        UpdateAvailable,
        /// <summary>Already on the latest version.</summary>
        UpToDate,
        /// <summary>Not a Velopack install (dev / portable) — self-update is not applicable.</summary>
        NotInstalled,
        /// <summary>Check failed (offline, rate-limited, feed error, …).</summary>
        Failed,
    }

    /// <summary>A check plus the update info it found (null when none is found).</summary>
    public sealed record CheckOutcome(CheckResult Result, UpdateInfo? Update);

    /// <summary>
    /// The most recent check result, or null before any check has run. Both the shell (dot badge) and
    /// the settings page read it, so a check started from either place updates both.
    /// </summary>
    public CheckOutcome? Last { get; private set; }

    /// <summary>
    /// The version of the pending update as text, or null while no update is known. This is the single
    /// place the version is read: the settings card's status line and the shell's update dot both spell
    /// it out, and two callers reaching into <see cref="Last"/> on their own is exactly how the copy
    /// they show would drift apart (or lose the version on one of the phases).
    /// </summary>
    public string? PendingVersion => Last?.Update is { } update ? update.TargetFullRelease.Version.ToString() : null;

    /// <summary>Raised whenever any observable state changes — the check result (<see cref="Last"/>)
    /// or the download phase. UI handlers must marshal to the UI thread themselves (the shell and the
    /// settings page post through the dispatcher).</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether an available update is downloaded in the background as soon as a check finds it.
    /// Mirrors the settings-page checkbox; seeded from <c>HubPreferences.AutoDownloadUpdates</c>.
    /// </summary>
    public bool AutoDownload { get; set; }

    /// <summary>The selected update channel. Preview builds always include GitHub pre-releases.</summary>
    public string UpdateChannel
    {
        get => _updateChannel;
        set
        {
            var normalized = UpdateChannels.Normalize(value);
            if (normalized == _updateChannel) return;

            CancelDownload();
            _updateChannel = normalized;
            Last = null;
            Download = DownloadState.None;
            DownloadPercent = 0;
            DownloadBytesPerSecond = 0;
            DownloadError = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Where the download of the pending update is (independent of the check result).</summary>
    public enum DownloadState
    {
        /// <summary>Nothing downloaded — either not started, or the last attempt was cancelled / failed.</summary>
        None,
        /// <summary>Downloading right now; <see cref="DownloadPercent"/> is meaningful.</summary>
        Downloading,
        /// <summary>Downloaded and waiting for a restart to apply.</summary>
        Ready,
    }

    /// <summary>The download phase of the update found by <see cref="Last"/>.</summary>
    public DownloadState Download { get; private set; }

    /// <summary>0-100 while <see cref="Download"/> is <see cref="DownloadState.Downloading"/>.</summary>
    public int DownloadPercent { get; private set; }

    /// <summary>
    /// Smoothed transfer rate in bytes/second for the progress tooltip; 0 while unknown, while nothing is
    /// downloading, and during the post-transfer phase of a delta update (where Velopack patches locally
    /// rather than transferring anything).
    /// </summary>
    public double DownloadBytesPerSecond { get; private set; }

    /// <summary>The last download failure message, or null. The caller that started the download owns
    /// showing it; a background download just leaves it set (and the user can retry).</summary>
    public string? DownloadError { get; private set; }

    /// <summary>True when the app was installed by Velopack — i.e. whether self-update applies at all.
    /// Drives whether the auto-download switch is offered.</summary>
    public bool IsInstalled => CanUpdate();

    /// <summary>
    /// The queue identity of the pending update's row, or null while none was ever started. Kept because
    /// <see cref="CancelDownload"/> has to name the transfer it is stopping, and the identity is built from
    /// the version — which the channel switch clears right after cancelling.
    /// </summary>
    private string? _transferIdentity;

    private string _updateChannel = UpdateChannels.DefaultChannel;

    /// <summary>
    /// The one <see cref="UpdateOptions"/> Hub updates with. Shared between <see cref="CreateManager"/>
    /// and <see cref="DescribeDownload"/> on purpose: the delta fallback limit below has to be the same
    /// value in both, or the estimate would disagree with what Velopack actually transfers.
    /// </summary>
    private static readonly UpdateOptions Options = new();

    /// <summary>
    /// What Velopack will actually transfer for <paramref name="update"/>, plus where the transfer phase
    /// ends on the 0-100 progress scale (the tooltip's byte rate is only meaningful up to that point).
    ///
    /// This mirrors <c>UpdateManager.DownloadUpdatesAsync</c> (Velopack 1.2.161): deltas are used only
    /// when a base release exists, at least one delta is available, the count is within
    /// <see cref="UpdateOptions.MaximumDeltasBeforeFallback"/> and their combined size does not exceed the
    /// full package; otherwise it downloads the full package. For deltas, progress 0-70 is the transfer
    /// and 70-100 is the **local** patch step (<c>DownloadAndApplyDeltaUpdates</c>) — no network at all.
    ///
    /// It is a read-only estimate for a tooltip: if a future Velopack changes the rule the number drifts,
    /// nothing breaks. Extracted as a pure function so the branch can be asserted (a wrong total is
    /// invisible — it just shows a plausible-looking rate).
    /// </summary>
    internal static (long TotalBytes, int TransferPercent) DescribeDownload(UpdateInfo update)
    {
        var target = update.TargetFullRelease;
        var deltas = update.DeltasToTarget ?? [];
        var deltaBytes = deltas.Sum(delta => delta.Size);

        var useDeltas = update.BaseRelease?.FileName is not null
                        && deltas.Length > 0
                        && deltas.Length <= (Options.MaximumDeltasBeforeFallback ?? 10)
                        && deltaBytes <= target.Size;

        return useDeltas ? (deltaBytes, 70) : (target.Size, 100);
    }

    /// <summary>
    /// Renders a byte rate for the progress tooltip ("2.4 MB/s" / "812 KB/s" / "—"). Pure and separate
    /// from the UI so its unit boundaries can be asserted — an off-by-1024 here would still look
    /// plausible on screen. Units stay language-neutral, so no text key is involved.
    /// </summary>
    internal static string FormatSpeed(double bytesPerSecond)
    {
        // Written as a negated comparison so NaN also lands here.
        if (!(bytesPerSecond > 0))
        {
            return "—";
        }

        const double kb = 1024d;
        const double mb = kb * 1024d;
        return bytesPerSecond switch
        {
            >= mb => (bytesPerSecond / mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s",
            >= kb => (bytesPerSecond / kb).ToString("0", CultureInfo.InvariantCulture) + " KB/s",
            _ => bytesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " B/s",
        };
    }

    /// <summary>
    /// Builds the manager against GitHub. Cheap; no network happens until a check runs. The
    /// <c>IsInstalled</c> property is an **instance** member, so "can we self-update" is answered
    /// by constructing a manager — that construction itself is side-effect free.
    /// </summary>
    private UpdateManager CreateManager()
        => new(new GithubSource(
            RepositoryUrl,
            null,
            UpdateChannels.IncludesPrereleases(UpdateChannel, HubReleaseInfo.IsPrereleaseBuild),
            null), Options, null);

    /// <summary>True when the app was installed by Velopack (not run from a dev/portable copy).</summary>
    private bool CanUpdate() => CreateManager().IsInstalled;

    /// <summary>
    /// Silent check. Never throws (a failure becomes <see cref="CheckResult.Failed"/>) and records the
    /// result in <see cref="Last"/> before raising <see cref="Changed"/>. Used by both the startup
    /// check and the settings-page button.
    /// </summary>
    public async Task<CheckOutcome> CheckAsync()
    {
        // A fresh check re-renders the card from scratch, so a stale "download failed" line must not
        // outlive it — when auto-download is on, the check itself is the retry.
        DownloadError = null;

        CheckOutcome outcome;
        if (!CanUpdate())
        {
            outcome = new CheckOutcome(CheckResult.NotInstalled, null);
        }
        else
        {
            try
            {
                var info = await CreateManager().CheckForUpdatesAsync();
                outcome = info is null
                    ? new CheckOutcome(CheckResult.UpToDate, null)
                    : new CheckOutcome(CheckResult.UpdateAvailable, info);
            }
            catch
            {
                outcome = new CheckOutcome(CheckResult.Failed, null);
            }
        }

        Last = outcome;
        Changed?.Invoke();

        // With auto-download on, merely finding an update is enough to start fetching it — the user
        // is then left with nothing but a restart.
        if (AutoDownload)
        {
            StartPendingDownload();
        }

        return outcome;
    }

    /// <summary>
    /// Verification hook (<c>--verify-shell</c>): installs a pending update **without** a network check,
    /// so the settings card's phases can be rendered and read back. Those phases are otherwise
    /// unreachable in a self-check — a real check needs GitHub, and a real download replaces the process
    /// the moment it finishes.
    /// </summary>
    internal void SetPendingForCheck(UpdateInfo update, DownloadState download = DownloadState.None, int percent = 0)
    {
        Last = new CheckOutcome(CheckResult.UpdateAvailable, update);
        Download = download;
        DownloadPercent = percent;
        DownloadError = null;
        Changed?.Invoke();
    }

    /// <summary>Undoes <see cref="SetPendingForCheck"/> so a scripted update can't leak into the
    /// render checks that follow (the badge is driven by <see cref="Last"/> too).</summary>
    internal void ClearForCheck()
    {
        Last = null;
        Download = DownloadState.None;
        DownloadPercent = 0;
        DownloadError = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Starts a background download if an update is known and nothing is in flight yet. Called after a
    /// check when <see cref="AutoDownload"/> is on, when the user switches the setting on with an update
    /// already pending, and from the strip's download action — which is the same request made on purpose.
    /// It never applies by itself: the transfer ends in <see cref="DownloadState.Ready"/> and the restart
    /// stays the user's press, because replacing the process is not something a download should decide.
    /// </summary>
    public void StartPendingDownload()
    {
        if (Download == DownloadState.None && Last is { Result: CheckResult.UpdateAvailable, Update: { } update })
        {
            StartDownload(update, applyWhenDone: false);
        }
    }

    /// <summary>Cancels a running download (manual or automatic). No-op when nothing is downloading.</summary>
    public void CancelDownload()
    {
        if (_transferIdentity is { } identity)
        {
            TransferQueue.Instance.Cancel(identity);
        }
    }

    /// <summary>
    /// Downloads the pending update and then applies it, restarting the app. Asked by the settings page's
    /// "Download &amp; restart" button. It returns as soon as the transfer is queued: the download now lives
    /// on the queue's row, and when it ends the process is replaced by the updater, so there is nothing
    /// left here to await.
    /// </summary>
    public Task DownloadAndApplyAsync()
    {
        if (Last is { Result: CheckResult.UpdateAvailable, Update: { } update })
        {
            StartDownload(update, applyWhenDone: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>Applies the already-downloaded update and restarts (the "Restart now" button, shown
    /// after a background download finishes). Never returns on success.</summary>
    public void ApplyAndRestart()
    {
        if (Download == DownloadState.Ready && Last?.Update is { } update)
        {
            // Verification hook: a self-check that really applied would take the process running the
            // assertions with it, so the suite installs an observer and reads the decision instead.
            if (ApplyOverrideForCheck is { } observer)
            {
                observer(update);
                return;
            }

            CreateManager().ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
    }

    /// <summary>Set by <c>--verify-shell</c> to observe a restart decision without making one.</summary>
    internal Action<UpdateInfo>? ApplyOverrideForCheck { get; set; }

    /// <summary>
    /// Puts this update's download on the shared <see cref="TransferQueue"/> rather than on a cancellation
    /// token of its own. An update downloading next to an engine install is two transfers on one pipe, and
    /// before this they did not know the other existed.
    ///
    /// <para><b>Velopack reports a percentage and nothing else</b> — <c>DownloadUpdatesAsync</c> hands back
    /// an <c>Action&lt;int&gt;</c>. So the row shows a percentage and an em-dash where an engine row shows
    /// MB and MB/s: see <see cref="TransferQueue.Item.ReportPercent(int)"/>. The byte estimate from
    /// <see cref="DescribeDownload"/> stays what it always was, the tooltip's, and never becomes the row's
    /// total.</para>
    ///
    /// <para>Progress arrives on the download thread (Velopack throttles it); every handler here and on the
    /// shell marshals itself, same as before.</para>
    /// </summary>
    private void StartDownload(UpdateInfo update, bool applyWhenDone)
    {
        var version = update.TargetFullRelease.Version.ToString();
        _transferIdentity = $"hub:update:{version}";
        TransferQueue.Instance.Enqueue(_transferIdentity, "TransferHubUpdate", version, async (item, token) =>
        {
            Download = DownloadState.Downloading;
            DownloadPercent = 0;
            DownloadBytesPerSecond = 0;
            DownloadError = null;
            Changed?.Invoke();

            var manager = CreateManager();

            // Velopack only ever hands back a 0-100 percentage, so the byte rate has to be derived: size of
            // whatever it is really transferring (see DescribeDownload) against our own clock. Sampling is
            // clamped to 500 ms — a finer window would only make the tooltip jitter.
            var (totalBytes, transferPercent) = DescribeDownload(update);
            var clock = Stopwatch.StartNew();
            long sampledBytes = 0;
            var sampledMs = 0L;
            const long sampleWindowMs = 500;
            const double smoothing = 0.4; // weight of the newest sample, so the number settles instead of flickering

            await manager.DownloadUpdatesAsync(update, percent =>
            {
                DownloadPercent = percent;
                item.ReportPercent(percent);

                var elapsedMs = clock.ElapsedMilliseconds;
                var window = elapsedMs - sampledMs;
                if (percent > transferPercent)
                {
                    // Past the transfer phase (delta updates patch locally from here): there is no
                    // download rate left to report, so don't keep showing a stale one. The row says the
                    // same thing — the work is local now, and unmeasurable.
                    DownloadBytesPerSecond = 0;
                    item.MarkApplying();
                }
                else if (totalBytes > 0 && window >= sampleWindowMs)
                {
                    var bytes = (long)(totalBytes * (percent / (double)transferPercent));
                    var instant = (bytes - sampledBytes) * 1000.0 / window;
                    DownloadBytesPerSecond = DownloadBytesPerSecond <= 0
                        ? instant
                        : DownloadBytesPerSecond * (1 - smoothing) + instant * smoothing;
                    sampledBytes = bytes;
                    sampledMs = elapsedMs;
                }

                Changed?.Invoke();
            }, token);

            DownloadPercent = 100;
            if (applyWhenDone)
            {
                // Terminates this process; the updater relaunches the app after installing. Nothing below
                // this line runs, and the row never gets to settle — which is the honest picture: the app
                // it was a row in is gone.
                manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
                return;
            }

            Download = DownloadState.Ready;
            Changed?.Invoke();
        }, onFinished: FinishDownload);
    }

    /// <summary>
    /// Brings this type's own state back in line with what the queue decided. The row keeps the failure and
    /// the cancel honest on its own, but the settings card and the strip's restart action both read
    /// <see cref="Download"/>, and a card still offering "restart" after a cancelled download would be a
    /// button that does nothing.
    /// </summary>
    private void FinishDownload(TransferQueue.Item item)
    {
        DownloadBytesPerSecond = 0;
        switch (item.State)
        {
            case TransferQueue.Phase.Failed:
                Download = DownloadState.None;
                DownloadPercent = 0;
                DownloadError = item.Error;
                break;
            case TransferQueue.Phase.Cancelled:
                Download = DownloadState.None;
                DownloadPercent = 0;
                break;
        }

        Changed?.Invoke();
    }
}
