using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The add/edit form for a model provider, built in code like <see cref="EngineMirrorDialog"/>.
///
/// This is the **only** path to a custom provider (a user-supplied OpenAI-compatible endpoint — how a local
/// model is reached), so it also carries the validation: all three fields are required and the base URL must
/// be an absolute http/https URL. The check is live (the Save button stays disabled and the reason is shown),
/// and <see cref="ChatWorkspace.AddProvider"/>/<see cref="ChatWorkspace.UpdateProvider"/> re-run the same
/// validation, so a half-filled provider can never reach the file even if the UI is bypassed.
///
/// A built-in provider's base URL and name are shown **read-only**: they come from the shipped manifest, and
/// editing them would silently diverge from what OrcaRouter's partner review scans for. The user who wants a
/// different endpoint adds a custom provider — which is what the button is for.
/// </summary>
public sealed class ProviderEditWindow : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _baseUrl = new();
    private readonly TextBox _model = new();
    private readonly TextBox _apiKey = new() { PasswordChar = '•' };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Classes = { "danger" } };
    private readonly Button _save;

    // Deliberately not named "Name": Avalonia's StyledElement already has a Name property, and hiding it
    // would be both a compiler warning and a trap for anyone reading the dialog.
    public string ProviderName => _name.Text ?? "";
    public string BaseUrl => _baseUrl.Text ?? "";
    public string Model => _model.Text ?? "";
    public string ApiKey => _apiKey.Text ?? "";

    private ProviderEditWindow(ModelProvider? existing)
    {
        var isBuiltIn = existing is { IsCustom: false };
        Title = existing is null ? HubStrings.Get("AddCustomProvider") : HubStrings.Get("EditProviderTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        var panel = new StackPanel { Spacing = 4 };

        if (isBuiltIn)
        {
            panel.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("ProviderBuiltInLockedHint"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        AddField(panel, HubStrings.Get("ProviderName"), _name, null, isBuiltIn);
        AddField(panel, HubStrings.Get("ProviderBaseUrl"), _baseUrl, HubStrings.Get("ProviderBaseUrlHint"), isBuiltIn);
        AddField(panel, HubStrings.Get("ProviderModel"), _model, HubStrings.Get("ProviderModelHint"), false);
        // The key row is always editable: a custom endpoint may or may not want one, and a built-in provider
        // exists precisely so the user pastes a key. Blank means "leave the stored key as it is".
        AddField(panel, HubStrings.Get("ApiKey"), _apiKey, HubStrings.Get("ApiKeyHint"), false);
        panel.Children.Add(_problem);

        var (footer, confirm, _) = HubDialogChrome.Footer(HubStrings.Get("Save"), HubStrings.Get("Cancel"),
            () => Close(HubDialogResult.Ok), () => Close(HubDialogResult.Cancel));
        _save = confirm;
        Content = chrome.Wrap(panel, footer);

        if (existing is not null)
        {
            _name.Text = existing.Name;
            _baseUrl.Text = existing.BaseUrl;
            _model.Text = existing.Model;
        }

        _name.TextChanged += (_, _) => Revalidate();
        _baseUrl.TextChanged += (_, _) => Revalidate();
        _model.TextChanged += (_, _) => Revalidate();
        Revalidate();
    }

    private static void AddField(StackPanel panel, string label, TextBox box, string? hint, bool readOnly)
    {
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) };
        // Bound rather than read: Hub.TextSecondary lives inside the token dictionary's ThemeDictionaries, so a
        // one-shot FindResource returns Avalonia's UnsetValue (the cast threw, and this dialog could not open at
        // all), and a value read once would not follow a theme switch either.
        caption.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        panel.Children.Add(caption);
        box.IsReadOnly = readOnly;
        if (readOnly) box.Classes.Add("muted");
        panel.Children.Add(box);
        if (hint is not null)
        {
            panel.Children.Add(new TextBlock { Text = hint, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        }
    }

    /// <summary>
    /// Live validation using the same rule the save path enforces (<see cref="ChatWorkspace.Validate"/>), so
    /// the button's enabled state and the actual gate can never disagree.
    /// </summary>
    private void Revalidate()
    {
        var key = ChatWorkspace.Validate(Build());
        _problem.Text = key is null ? "" : HubStrings.Get(key);
        _save.IsEnabled = key is null;
    }

    private ModelProvider Build() => new()
    {
        Name = ProviderName,
        BaseUrl = BaseUrl,
        Model = Model,
    };

    /// <summary>Shows the form. Returns the edited values on Save, or <c>null</c> on cancel.</summary>
    public static async Task<ProviderEditWindow?> ShowAsync(Window? owner, ModelProvider? existing)
    {
        var dialog = new ProviderEditWindow(existing);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog : null;
    }
}
