using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;
public partial class ToolchainsPage : UserControl
{
    /// <summary>Verification hook: paints the card from a status nobody has to install anything to produce.</summary>
    internal void SetHostShellForCheck(HostShellStatus status, bool awaitingTerminal = false)
    {
        _workspace.HostShellForCheck(status, awaitingTerminal);
        ReloadHostShell();
    }
}
