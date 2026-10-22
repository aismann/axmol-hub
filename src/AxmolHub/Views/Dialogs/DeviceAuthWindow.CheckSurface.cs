using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace AxmolHub;
public sealed partial class DeviceAuthWindow : Window
{
    // ── Self-check surface ──
    //
    // Read the LOGICAL tree and read the TEXT — the two rules AuthDialog's own notes record, learned by
    // breaking them: a window that was never shown has no visual tree, and a control that lays out without
    // painting anything still counts.
    internal string CodeForCheck => _code.Text ?? "";
    internal string LinkForCheck => _link.Text ?? "";
    internal string StatusForCheck => _status.Text ?? "";
    /// <summary>The text the copy button would put on the clipboard, read without touching a clipboard: a check
    /// that drove the real one would be asserting on whatever the session's clipboard already held.</summary>
    internal string CopyTextForCheck => ClipboardText(CodeForCheck);
    internal int LogicalChildCountForCheck => this.GetLogicalDescendants().Count();
    internal bool CopyRevealedForCheck => _copy.Opacity > 0;
    /// <summary>
    /// Every piece of text on the window, joined. The check that matters here is a negative one — the polling
    /// secret must not be on screen — and a negative assertion written against three named properties only proves
    /// those three do not hold it. Reading all the text is what makes "nothing on this window says it" a fact.
    /// </summary>
    internal string TextsForCheck => string.Join("|", this.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? ""));

    /// <summary>
    /// Raises the close the way the window manager does, so a check can cancel a sign-in it never put on screen.
    ///
    /// <para><see cref = "Window.Close"/> is not usable here: it goes through the platform, and a window that was
    /// never shown has no platform side to ask. <c>OnClosed</c> is the method the platform itself calls, so
    /// routing the check through it fires the same <see cref = "Window.Closed"/> event the person closing the
    /// window fires — which is the wiring worth asserting. A seam that cancelled the token directly would prove
    /// the flow listens to a token, and the flow already does that in the Checks project.</para>
    /// </summary>
    internal void CloseForCheck() => OnClosed(EventArgs.Empty);
}
