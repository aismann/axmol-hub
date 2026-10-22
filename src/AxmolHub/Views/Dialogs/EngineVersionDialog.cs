using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The engine install picker: it chooses which version the 安装 button then downloads. Pure code-built,
/// same approach as <see cref="BuildTargetDialog"/> (both are one-off small windows, not worth a
/// dedicated .axaml).
///
/// It exists because: previously the engines-page button installed the latest LTS in the manifest
/// directly, leaving no entry point for older versions. And "each project locks its own engine
/// version" is a premise of Hub — project A pinned to 2.11.3 and project B to 2.11.5 is the normal
/// state, so **which version can be installed** must be the user's choice, not an implementation
/// side effect.
///
/// The list comes from <see cref="EngineReleases"/>: the remote index pulled at startup, or the
/// built-in manifest when that fails. Versions aren't enumerated in code — where versions come from
/// is a data question, not a code question.
/// Returns <c>null</c> = user cancelled, in which case not a single byte should be downloaded.
/// </summary>
public sealed class EngineVersionDialog : Window
{
    private readonly ListBox _versions = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16) };
    private readonly Button _install;
    private readonly string _downloadSource;

    /// <summary>Whether this version's download is already running. Asked of the queue by the caller,
    /// because the queue is what knows; the dialog only shows the answer.</summary>
    private readonly Func<EngineRelease, bool> _isInFlight;

    public EngineRelease? Selected => (_versions.SelectedItem as Row)?.Release;

    /// <summary>
    /// One list entry. The row is <see cref="EngineRelease.ToString"/> plus, when that version is already
    /// arriving, the in-flight word: a row that still offers 安装 for a version that is downloading is a
    /// button that does nothing, and "nothing happened" is the worst answer a click can give.
    /// </summary>
    private sealed class Row(EngineRelease release, Func<EngineRelease, bool> isInFlight)
    {
        public EngineRelease Release { get; } = release;

        private readonly Func<EngineRelease, bool> _isInFlight = isInFlight;

        public bool InFlight => _isInFlight(Release);

        public override string ToString() => InFlight
            ? Release + " · " + HubStrings.Get("TaskInFlightSuffix")
            : Release.ToString()!;
    }

    private EngineVersionDialog(
        IReadOnlyList<EngineRelease> releases,
        string? preferredVersion,
        bool fromRemoteIndex,
        string downloadSource,
        Func<EngineRelease, bool> isInFlight)
    {
        _downloadSource = downloadSource;
        _isInFlight = isInFlight;
        Title = HubStrings.Get("InstallEngineTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var chrome = new HubDialogChrome(this, "CloseWindow", () => Close(HubDialogResult.Cancel));

        // No title line in here on purpose: the caption bar says it, and a second copy at 18px inside the panel
        // is the same sentence twice.
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("EngineVersionHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0),
        });
        // Tell the user honestly where this list comes from. Falling back isn't a fault, but "where
        // the list comes from" must be visible — otherwise "there are 6 versions" and "only the
        // built-in few" look identical.
        body.Children.Add(new TextBlock
        {
            Text = HubStrings.Get(fromRemoteIndex ? "EngineIndexRemote" : "EngineIndexBuiltIn"),
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        body.Children.Add(_versions);
        body.Children.Add(_detail);
        body.Children.Add(_warning);

        var (footer, install, _) = HubDialogChrome.Footer(HubStrings.Get("InstallSelected"), HubStrings.Get("Cancel"),
            () => Close(HubDialogResult.Ok), () => Close(HubDialogResult.Cancel));
        _install = install;

        Content = chrome.Wrap(body, footer);

        var rows = releases.Select(release => new Row(release, isInFlight)).ToArray();
        _versions.ItemsSource = rows;
        // Pre-select: the version the caller specified, otherwise the newest in the manifest. Fall
        // back to the first item if not found — pre-selecting none would make the "continue" button
        // fail for no reason.
        var preferred = preferredVersion is null ? null : rows.FirstOrDefault(row => row.Release.Version == preferredVersion);
        _versions.SelectedItem = preferred ?? rows.FirstOrDefault();
        _versions.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (Selected is not { } release)
        {
            _detail.Text = HubStrings.Get("EngineVersionNone");
            _warning.Text = "";
            _warning.IsVisible = false;
            _install.IsEnabled = false;
            return;
        }

        var inFlight = _isInFlight(release);
        // A version that is already arriving cannot be installed again, and the button says so in its own
        // label rather than sitting there greyed and unexplained: a disabled control takes its tooltip with
        // it, so the reason would be unreadable exactly when it is needed.
        _install.IsEnabled = !inFlight;
        _install.Content = HubStrings.Get(inFlight ? "TaskInFlightSuffix" : "InstallSelected");
        _detail.Text = string.Join("\n", new[]
        {
            HubStrings.Get("Channel") + "：" + release.Channel,
            // Where the download will come from — stated before the download starts, because on a
            // slow network the difference between GitHub and a mirror is the whole experience.
            HubStrings.Get("DownloadSource") + "：" + HubStrings.Get(DownloadSources.TextKey(_downloadSource)),
            HubStrings.Get("DownloadSize") + "：" + Size(release.Package.DownloadBytes),
            // "Not installed" is true while it is arriving and useless: the fact worth printing is that
            // it is on its way.
            inFlight ? HubStrings.Get("TaskInFlightSuffix") : HubStrings.Get(release.Installed ? "Installed" : "Missing"),
        });

        // Versions with unverified recipes are still installable — the engine itself is
        // self-consistent, and the recipe only affects the Android packaging step. But it must be
        // stated here, otherwise the user only discovers after installing that packaging won't work.
        _warning.Text = HubStrings.Get("EngineRecipeUnverified");
        _warning.IsVisible = !release.RecipesVerified;
    }

    public static string Size(long? value) => value is null ? "—" : value >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{value / (1024d * 1024 * 1024):0.00} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{value / (1024d * 1024):0.00} MB");

    public static async Task<EngineRelease?> PickAsync(Window? owner, EngineReleases catalog, string? preferredVersion,
        string downloadSource = DownloadSources.GitHubId, Func<EngineRelease, bool>? isInFlight = null)
    {
        var releases = catalog.All();
        if (releases.Count == 0) return null;
        var dialog = new EngineVersionDialog(releases, preferredVersion, catalog.UsingRemoteIndex, downloadSource,
            isInFlight ?? (_ => false));
        var result = owner is null
            ? await dialog.ShowDialog<HubDialogResult>(null!)
            : await dialog.ShowDialog<HubDialogResult>(owner);

        return result == HubDialogResult.Ok ? dialog.Selected : null;
    }
}
