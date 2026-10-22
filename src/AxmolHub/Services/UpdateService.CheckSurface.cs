using Velopack;

namespace AxmolHub;
internal sealed partial class UpdateService
{
    internal Func<UpdateManager>? ManagerOverrideForCheck { get; set; }

    /// <summary>
    /// Verification hook (<c>--verify-shell</c>): installs a pending update **without** a network check, at
    /// the download phase asked for, so the surfaces that read this service can be rendered and read back.
    /// Those phases are otherwise unreachable in a self-check — a real check needs GitHub, and a real
    /// download replaces the process the moment it finishes. A percentage is not part of it: that belongs to
    /// the queue's row, which the suite drives through <see cref = "TaskQueue"/> instead.
    /// </summary>
    internal void SetPendingForCheck(UpdateInfo update, DownloadState download = DownloadState.None)
    {
        Last = new CheckOutcome(CheckResult.UpdateAvailable, update);
        Download = download;
        // The arm belongs to the Ready phase alone, and this hook is the one writer that can install any
        // phase directly: a check that scripted a download phase after an armed restart would read back an
        // arm that no longer exists on screen.
        if (download != DownloadState.Ready)
        {
            DropRestartArm();
        }

        Changed?.Invoke();
    }

    /// <summary>Undoes <see cref = "SetPendingForCheck"/> so a scripted update can't leak into the
    /// render checks that follow (both the card's line and the strip's slot read <see cref = "Last"/>).</summary>
    internal void ClearForCheck()
    {
        Last = null;
        Download = DownloadState.None;
        DropRestartArm();
        Changed?.Invoke();
    }

    /// <summary>Set by <c>--verify-shell</c> to observe a restart decision without making one.</summary>
    internal Action<UpdateInfo>? ApplyOverrideForCheck { get; set; }
    /// <summary>
    /// Set by <c>--verify-shell</c> to observe a download request without making one. The same shape as
    /// <see cref = "ApplyOverrideForCheck"/> for the other half of the flow: a self-check that really asked
    /// Velopack would reach GitHub, and a delta would end with the updater taking the process.
    /// </summary>
    internal Action<UpdateInfo>? DownloadOverrideForCheck { get; set; }
}
