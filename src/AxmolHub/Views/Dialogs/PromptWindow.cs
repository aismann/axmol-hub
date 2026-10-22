using Avalonia.Controls;

namespace AxmolHub;

/// <summary>
/// A one-field prompt: a title, a single text box, and OK/Cancel. Built in code like the other dialogs.
///
/// It exists as a general dialog rather than an inline editor inside the account row because the row is
/// rebuilt from the credential list on every change — an inline <c>TextBox</c> would be thrown away mid-edit
/// the moment anything else repainted. A modal window is not rebuilt, so the text the user is typing survives.
/// </summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _input;

    public string Value => (_input.Text ?? "").Trim();

    private PromptWindow(string title, string initialValue, string? placeholder)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        _input = new TextBox { Text = initialValue };
        if (placeholder is { Length: > 0 }) _input.PlaceholderText = placeholder;

        // The title is the caption bar's now, so the body is the field alone: the dialog says its title once —
        // Window.Title — which is the same rule the header's caption follows.
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(_input);

        var (footer, _, _) = HubDialogChrome.Footer(HubStrings.Get("Save"), HubStrings.Get("Cancel"),
            () => Close(HubDialogResult.Ok), () => Close(HubDialogResult.Cancel));
        Content = chrome.Wrap(body, footer);

        Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    /// <summary>Shows the prompt. Returns the trimmed text on Save, or <c>null</c> on cancel. An empty string
    /// is returned as-is: whether blank is acceptable is the caller's rule, not the dialog's.</summary>
    public static async Task<string?> ShowAsync(Window? owner, string title, string initialValue = "", string? placeholder = null)
    {
        var dialog = new PromptWindow(title, initialValue, placeholder);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog.Value : null;
    }
}
