using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The "build engine" confirmation window. Pure code-built, same approach as
/// <see cref="EngineVersionDialog"/>.
///
/// Why a confirmation is required first: this step compiles the **whole engine** (minutes to tens
/// of minutes, gigabytes of output), and when the toolchain is missing the engine triggers its own
/// setup on the way — which changes user-level PATH / AX_ROOT and may pop UAC. If these costs
/// aren't spelled out, the user just sees a button that does nothing when clicked.
///
/// It also lets the user pick a configuration: prebuilt libraries are **split into directories by
/// configuration** (<c>lib/&lt;Config&gt;</c>), and the project's build configuration must match the
/// engine's build configuration to link. Defaults to Release (matching the engine CI's <c>-O3</c>).
///
/// Returns the chosen configuration; <c>null</c> = user cancelled (in which case not a single byte
/// should be compiled).
/// </summary>
public sealed class EngineBuildWindow : Window
{
    private readonly ComboBox _configuration = new();
    private readonly string _release;
    private readonly string _debug;

    private string? Result => _configuration.SelectedItem as string;

    private EngineBuildWindow(EngineEntry engine, BuildTarget target)
    {
        _release = "Release";
        _debug = "Debug";

        Title = HubStrings.Get("BuildEngineConfirm");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        var panel = new StackPanel();

        var engineLabel = new TextBlock
        {
            Text = engine + "\n" + target.Name,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(engineLabel);

        // The cost must be stated explicitly — this is what really happens on this click.
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("EngineBuildCost"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        var configurationRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var configurationLabel = new TextBlock { Text = HubStrings.Get("EngineBuildConfig"), VerticalAlignment = VerticalAlignment.Center };
        configurationLabel.Margin = new Thickness(0, 0, 12, 0);
        configurationRow.Children.Add(configurationLabel);
        _configuration.ItemsSource = new[] { _release, _debug };
        _configuration.SelectedItem = _release;
        _configuration.Width = 200;
        configurationRow.Children.Add(_configuration);
        panel.Children.Add(configurationRow);

        var (footer, _, _) = HubDialogChrome.Footer(HubStrings.Get("Build"), HubStrings.Get("Cancel"),
            () => Close(HubDialogResult.Ok), () => Close(HubDialogResult.Cancel));
        Content = chrome.Wrap(panel, footer);
    }

    /// <summary>Returns the chosen configuration; <c>null</c> when the user cancels.</summary>
    public static async Task<string?> PickAsync(Window? owner, EngineEntry engine, BuildTarget target)
    {
        var dialog = new EngineBuildWindow(engine, target);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);
        return result == HubDialogResult.Ok ? dialog.Result : null;
    }
}
