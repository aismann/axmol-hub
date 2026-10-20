using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// Everything one request's tools are drawn from, assembled on the UI thread when the request is built.
///
/// Assembled rather than read on demand because a tool call can be answered long after the request that offered
/// it — after a window closed, even. The values here are the ones the approval card was shown with, so approving
/// it later acts on the same directory, the same guards and the same secrets list.
///
/// <paramref name="AskUserEnabled"/> rides along for the same reason and decides one thing only: whether the
/// <c>ask_user</c> tool is declared at all. It is not a permission — a question that was asked is always shown —
/// so it belongs beside the session's own choice rather than beside the approval mode, and a call already parked
/// is answered regardless of what this says when the answer arrives.
/// </summary>
internal sealed record ChatToolScope(
    ChatWorkspace.HubReadOnlySnapshot? Snapshot,
    WorkspaceToolScope Workspace,
    bool AskUserEnabled = true,
    bool PlanTasksEnabled = false)
{
    public static ChatToolScope Empty { get; } = new(null, WorkspaceToolScope.Empty);
}
