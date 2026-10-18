using Avalonia.Controls;
using Avalonia.Media.Imaging;
using AxmolHub.Core;

namespace AxmolHub;

public enum HubDialogButtons
{
    Ok,
    OkCancel,
    YesNo,
}

/// <summary>
/// Counterpart of WPF's MessageBoxResult. **Cancel must come first**: Avalonia's
/// <c>ShowDialog&lt;TResult&gt;</c> returns <c>default(T)</c> when the window is closed "without a
/// result" (clicking the title bar X), so if Ok were 0, closing the window would be read as
/// "clicked OK" — a real bug on the uninstall confirmation box.
/// </summary>
public enum HubDialogResult
{
    Cancel,
    Ok,
    Yes,
    No,
}

/// <summary>
/// The Avalonia replacement for WPF's <c>MessageBox.Show</c>. See Views/HubDialog.axaml for details.
/// </summary>
public partial class HubDialog : Window
{
    private TaskCompletionSource<HubDialogResult>? _standalone;
    private bool _finished;

    public HubDialogResult Result { get; private set; } = HubDialogResult.Cancel;

    /// <summary>
    /// Where a link in this box is handed to. Replacing it is how <c>--verify-shell</c> proves the About box's
    /// repository link is wired without opening a browser on the machine running the check — the same shape as
    /// <see cref="ChatWorkspace.BrowserOpener"/>, and <see cref="UrlLauncher"/> itself is covered on its own (the
    /// Checks project, <c>--check-secrets</c>).
    /// </summary>
    internal static Func<string, bool> LinkOpener { get; set; } = UrlLauncher.TryOpen;

    public HubDialog()
    {
        InitializeComponent();

        // The bar this app draws, with one plate: closing a confirmation with the × is its Cancel answer, the
        // same answer Esc gives through the IsCancel button. That is the invariant the enum above is ordered for,
        // so the plate goes through Finish rather than Window.Close — a bare Close would still *read* as Cancel
        // today, and would stop reading as Cancel the moment a caller's default result changed.
        ChromeHost.Content = new HubDialogChrome(this, "CloseWindow", () => Finish(HubDialogResult.Cancel)).Header;
    }

    /// <summary>Replaces MessageBox.Show. Shows even when owner is null (startup-failure path).</summary>
    public static Task<HubDialogResult> ShowAsync(
        Window? owner,
        string title,
        string message,
        HubDialogButtons buttons = HubDialogButtons.Ok,
        bool danger = false,
        Bitmap? picture = null,
        string? linkUrl = null)
    {
        var dialog = new HubDialog();

        // One write, two readers: Window.Title is what the OS's window list reads, and the drawn header's caption
        // is bound to it (see HubDialogChrome), so the title cannot be said twice and cannot go out of step.
        dialog.Title = title;
        dialog.DialogMessage.Text = message;
        dialog.SetPicture(picture);
        dialog.AddLink(linkUrl);
        dialog.Build(buttons, danger);

        if (owner is not null)
        {
            return dialog.ShowDialog<HubDialogResult>(owner);
        }

        // ShowDialog needs an owner, but on the "startup failed" path the main window doesn't exist yet.
        // Degrade to a non-modal Show() and collect the result ourselves: closed without a result is Cancel.
        var completion = new TaskCompletionSource<HubDialogResult>();
        dialog._standalone = completion;
        dialog.Closed += (_, _) => completion.TrySetResult(dialog.Result);
        dialog.Show();
        return completion.Task;
    }

    /// <summary>
    /// Button copy goes through <see cref="HubStrings"/>. **These four words used to be hard-coded Chinese** —
    /// back then the UI was Chinese-first so the problem was invisible; on an English UI any dialog's buttons were still "取消/确定".
    /// The worse spot is `CjkFontNotice`'s "missing Chinese font" prompt: its only button, hard-coded in Chinese,
    /// would itself be a box on such a system, so the prompt can't close the loop.
    /// Order and semantics (which is primary / cancel) are **unchanged**: those are guarded by self-check assertions.
    /// </summary>
    /// <summary>
    /// Shows <paramref name="picture"/> above the text. A null picture leaves the row collapsed rather than
    /// blank: this box is also every confirmation and notice in the shell, and none of those has a logo.
    /// </summary>
    private void SetPicture(Bitmap? picture)
    {
        DialogPicture.Source = picture;
        DialogPicture.IsVisible = picture is not null;
    }

    /// <summary>
    /// Adds the destination the box points at, as a row of its own above the answers. A Button rather than a
    /// TextBlock with a pointer handler, because focus, Enter/Space and the hand cursor come with it; the caption
    /// is the address itself, so a reader who will not click still sees where it goes.
    /// </summary>
    private void AddLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var link = new Button { Name = "DialogLink", Content = url, Classes = { "link" } };
        link.Click += (_, _) => _ = LinkOpener(url);
        DialogLinks.Children.Add(link);
        DialogLinks.IsVisible = true;
    }

    private void Build(HubDialogButtons buttons, bool danger)
    {
        switch (buttons)
        {
            case HubDialogButtons.OkCancel:
                Add(HubDialogResult.Cancel, HubStrings.Get("Cancel"), primary: false, cancel: true, danger: false);
                Add(HubDialogResult.Ok, HubStrings.Get("Ok"), primary: true, cancel: false, danger: danger);
                break;

            case HubDialogButtons.YesNo:
                Add(HubDialogResult.No, HubStrings.Get("No"), primary: false, cancel: true, danger: false);
                Add(HubDialogResult.Yes, HubStrings.Get("Yes"), primary: true, cancel: false, danger: danger);
                break;

            default:
                Add(HubDialogResult.Ok, HubStrings.Get("Ok"), primary: true, cancel: false, danger: danger);
                break;
        }
    }

    private void Add(HubDialogResult result, string caption, bool primary, bool cancel, bool danger)
    {
        // IsDefault / IsCancel handle Enter and Esc, equivalent to WPF MessageBox's default-button and Esc behavior.
        var button = new Button { Content = caption, IsDefault = primary, IsCancel = cancel };
        if (primary)
        {
            button.Classes.Add("primary");
        }

        if (danger)
        {
            button.Classes.Add("danger");
        }

        button.Click += (_, _) => Finish(result);
        DialogButtons.Children.Add(button);
    }

    private void Finish(HubDialogResult result)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Result = result;

        if (_standalone is not null)
        {
            _standalone.TrySetResult(result);
            Close();
            return;
        }

        Close(result);
    }
}
