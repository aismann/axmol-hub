using AxmolHub.Core;

namespace AxmolHub;
public sealed partial class HubWorkspace : IDisposable
{
    /// <summary>Verification hook (<c>--verify-shell</c>): records a pid as the game Hub launched for this
    /// workspace. The suite has no built artifact to start, and what the ruling is about is not the launch —
    /// it is that a running game leaves the gate empty, takes no row, and shows up again only as the reason a
    /// rebuild would fail.</summary>
    internal void RememberGameForCheck(string projectPath, int pid) => _launchedGames[projectPath] = pid;
    /// <summary>Verification hook: drops the remembered games, so a scripted "it is still running" cannot
    /// leave the next round's build press refused for a process that only ever existed in this suite.</summary>
    internal void ForgetGamesForCheck() => _launchedGames.Clear();
    /// <summary>
    /// Verification seam: puts a verdict on the card without touching the machine. On a host that already has
    /// pwsh — which is most development machines — the "missing" and "waiting on a terminal" layouts would
    /// otherwise never be reachable, and an unasserted layout is an unasserted UI (AGENTS.md). Installing for
    /// real is deliberately not offered here: it changes the whole machine, so its verdicts are covered by
    /// <c>Checks --check-host-shell</c> instead, which is host-independent and offline.
    /// </summary>
    internal void HostShellForCheck(HostShellStatus status, bool awaitingTerminal = false)
    {
        HostShell = status;
        HostShellAwaitingTerminal = awaitingTerminal;
    }
}
