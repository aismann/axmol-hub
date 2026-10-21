using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The "choose build platform" dialog. In the WPF version this window was stacked up inline inside
/// <c>MainWindow.PickBuildTarget</c>; here it's its own file because both "generate / run" and the
/// verification programs use it.
///
/// Returning <c>null</c> means the user cancelled — same as the WPF version, and **cancelling must
/// not change project metadata**: choosing a platform itself writes <c>.axmol-hub.json</c>, so the
/// write is deferred until the build actually starts (see <c>BuildTargets.Select</c> inside
/// <see cref="HubWorkspace.BuildAsync"/>).
/// </summary>
public sealed class BuildTargetDialog : Window
{
    private readonly ComboBox _picker = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly ComboBox _configuration = new() { Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly ProjectEntry _project;

    private BuildTargetDialog(ProjectEntry project)
    {
        _project = project;
        Title = HubStrings.Get("ChooseBuildPlatform");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        var body = new StackPanel();
        body.Children.Add(_picker);
        body.Children.Add(new TextBlock { Text = HubStrings.Get("BuildConfiguration") });
        body.Children.Add(_configuration);
        body.Children.Add(_hint);
        body.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("BuildPlatformHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(chrome.Header, 0);
        Grid.SetRow(body, 1);
        var (footer, _, _) = HubDialogChrome.Footer(HubStrings.Get("Continue"), HubStrings.Get("Cancel"),
            () => Close(HubDialogResult.Ok), () => Close(HubDialogResult.Cancel));
        Grid.SetRow(footer, 2);
        layout.Children.Add(chrome.Header);
        layout.Children.Add(body);
        layout.Children.Add(footer);

        Content = new Border { Child = layout, Padding = new Thickness(24) };

        var available = BuildTargets.ForHost(project.Version);
        _picker.ItemsSource = available;
        // The project's current target may not be buildable on this host (e.g. picked on another machine
        // or via the CLI); fall back to the first buildable target so the combo box never shows a selection
        // that isn't in the list.
        var current = BuildTargets.Get(project.Platform);
        _picker.SelectedItem = available.Contains(current) ? current : available[0];
        _picker.SelectionChanged += (_, _) => RefreshConfigurations();
        RefreshConfigurations();
    }

    private void RefreshConfigurations()
    {
        var android = (_picker.SelectedItem as BuildTarget)?.Family == "android";
        var previous = _configuration.SelectedItem as string ?? _project.Configuration;
        _configuration.ItemsSource = BuildConfigurations.All;
        _configuration.SelectedItem = BuildConfigurations.All.Contains(previous) ? previous : "Debug";
        _hint.Text = HubStrings.Get(android ? "AndroidReleasePending" : "ConfigurationHint");
    }

    public static async Task<(BuildTarget Target, string Configuration)?> PickAsync(Window? owner, ProjectEntry project)
    {
        var dialog = new BuildTargetDialog(project);
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);

        return result == HubDialogResult.Ok && dialog._picker.SelectedItem is BuildTarget target
            && dialog._configuration.SelectedItem is string configuration
            ? (target, configuration)
            : null;
    }
}
