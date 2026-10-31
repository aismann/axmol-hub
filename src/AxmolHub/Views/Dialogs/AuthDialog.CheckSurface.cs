using Avalonia.Controls;
using Avalonia.LogicalTree;
using AxmolHub.Core;

namespace AxmolHub;
public sealed partial class AuthDialog : Window
{
    /// <summary>
    /// Opens the dialog the way the settings page opens it, with <paramref name="preferredMethod"/> as the route
    /// the provider is already bound to.
    ///
    /// <para>No step is passed in, and that is the point: what the dialog opens on has to be read off its own
    /// route list. A fixture that forced the step could only confirm what it had just forced.</para>
    /// </summary>
    internal static AuthDialog ForCheck(ModelProvider provider, string? preferredMethod = null)
        => new(provider, preferredMethod, null);

    /// <summary>The key surface, for the picture of step two — reached through <see cref="Choose"/> the way the
    /// dialog reaches it, not painted onto a fresh window.</summary>
    internal static AuthDialog ForCheckKeyStep(ModelProvider provider)
    {
        var dialog = new AuthDialog(provider, null, null);
        dialog.Choose(ProviderAuthMethods.ApiKey);
        return dialog;
    }

    /// <summary>Picks a method row without taking it, mirroring a click on the row itself.</summary>
    internal void SelectForCheck(string method)
    {
        var rows = LogicalControls.OfType<Button>().Where(button => button.Tag as string == method).ToList();
        foreach (var row in rows)
            Select(method, rows);
    }

    // ── Self-check surface ──
    //
    // A modal cannot be driven by --verify-shell, so these read the built state instead. Two rules make the
    // reads trustworthy, and both were learned by breaking them:
    //
    // 1. Read the LOGICAL tree, not the visual one. A window that was never shown has no visual tree —
    //    GetVisualDescendants() on it returns nothing, so "no blank text" passed on an empty collection and
    //    the option list read as "[]". An assertion that cannot fail is worse than no assertion, because it
    //    looks like evidence. StepCountForCheck exists to close that door: it fails loudly when the tree
    //    the other reads walk turns out to be empty.
    // 2. Read the TEXT, never the control's existence. Body(string) once took a parameter and never
    //    assigned it to Text — the block laid out, measured a line's height, painted nothing, and every
    //    check that counted controls or read Tag still passed. Counting is not reading.
    /// <summary>How many text-bearing controls the current step actually built. The non-empty witness.</summary>
    internal int StepControlCountForCheck => LogicalControls.Count();
    /// <summary>Every non-empty string currently painted in the dialog's content, in tree order.</summary>
    internal string[] VisibleTextForCheck => LogicalControls.Select(control => Read(control)).Where(text => !string.IsNullOrWhiteSpace(text)).Cast<string>().ToArray();
    /// <summary>
    /// How many <see cref = "TextBlock"/>s in the tree are blank <i>and showing</i>.
    ///
    /// <para>Visibility is part of the predicate on purpose. A status line that is built empty and filled
    /// in only when there is something to report is legitimate; what is not legitimate is a block the user
    /// can see with nothing in it. Counting the hidden ones would make the check cry wolf over the design
    /// rather than the defect.</para>
    /// </summary>
    internal int BlankTextCountForCheck => LogicalControls.OfType<TextBlock>().Count(block => string.IsNullOrWhiteSpace(block.Text) && block.IsVisible);
    /// <summary>Names of the blank, visible text blocks. Empty when there are none; diagnostic detail.</summary>
    internal string[] BlankTextClassesForCheck => LogicalControls.OfType<TextBlock>().Where(block => string.IsNullOrWhiteSpace(block.Text) && block.IsVisible).Select(block => string.Join("+", block.Classes)).ToArray();
    /// <summary>The label on the step-one confirm button, or <c>null</c> when this step has none.</summary>
    internal string? NextTextForCheck => (_next?.Content as string) ?? _next?.Content?.ToString();
    /// <summary>Whether the step-one confirm button would take the selection.</summary>
    internal bool NextEnabledForCheck => _next?.IsEnabled ?? false;
    /// <summary>The method rows on step one, in order. Empty on the key step.</summary>
    internal string[] MethodOptionsForCheck => LogicalControls.OfType<Button>().Select(button => button.Tag as string).Where(tag => tag is not null).Cast<string>().ToArray();
    /// <summary>The password boxes on the current step. Used to prove the key box exists at all.</summary>
    internal bool HasPasswordBoxForCheck => LogicalControls.OfType<TextBox>().Any(box => box.Classes.Contains("password"));
    private IEnumerable<Control> LogicalControls => this.GetLogicalDescendants().OfType<Control>();

    private static string? Read(Control control) => control switch
    {
        TextBlock block => block.Text,
        Button { Content: string text } => text,
        TextBox box => box.Text ?? box.PlaceholderText,
        _ => null,
    };
}
