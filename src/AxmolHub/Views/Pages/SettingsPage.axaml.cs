using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
// GetLogicalDescendants (not GetVisualDescendants) on purpose: the model section is read for a group that may
// not be attached to a visual root yet, and the visual walk returns an empty collection in that case — which
// turns "the check found nothing" into "the check passed".
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The settings page (WPF's <c>SettingsPage</c>).
///
/// Beyond "porting the settings over", it also carries a verification duty: **does localization
/// actually take effect**. Switching language requires reloading the resource dictionary in place
/// and updating **already-existing controls**' text — this can't be judged by reading code (whether
/// Avalonia's <c>DynamicResource</c> re-resolves when the resource dictionary changes can only be
/// verified empirically), so <c>--verify-shell</c> really switches the language once and reads back
/// the controls' text.
///
/// Switching the data root is handled by the shell (<see cref="MainWindow.SwitchDataRoot"/>): it
/// only rebuilds the workspace and pages, **not the window** — so this doesn't depend on the shell
/// shape. The page here only "asks the user for a directory".
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly HubWorkspace _workspace;
    private readonly PreferencesStore _preferencesStore;
    private readonly HubPreferences _preferences;
    private readonly ChatWorkspace _chat;
    private readonly Action<string> _openFolder;

    /// <summary>After a language change the shell must recompute its own **imperative** copy (title, bottom-left version line).</summary>
    private readonly Action _languageChanged;

    /// <summary>Asks the shell to switch the data root. Returns whether it actually switched.</summary>
    private readonly Func<string, bool> _switchDataRoot;

    /// <summary>Asks the shell to switch the theme (persist → apply → revert on failure). Returns null on
    /// success, the error message on failure. The shell owns the sequence: the bottom menu's appearance
    /// items drive the same path, so the picker must not grow a second copy of it.</summary>
    private readonly Func<string, string?> _applyTheme;

    /// <summary>Suppresses <c>SelectionChanged</c> during initialization: the WPF version likewise had a <c>preferencesReady</c> gate.</summary>
    private bool _ready;

    /// <summary>True while a manual check is running: it owns the status line, so
    /// <see cref="RenderUpdateState"/> must not clobber it. The download needs no such flag — its phase
    /// lives in <see cref="UpdateService"/> and <see cref="RenderUpdateState"/> reads it from there.</summary>
    private bool _checking;

    /// <summary>Whether this page is currently subscribed to <see cref="UpdateService.Changed"/>
    /// (hooked while on screen only).</summary>
    private bool _updateHooked;

    // ── Provider draft state (v3) ──
    // A provider's edit view is a draft that lives *inside* its group, and an "add model" / "add custom
    // endpoint" draft is always the first child of its list. While any draft is open, ChatWorkspace.Changed
    // must not rebuild the groups — that would tear apart the very TextBox being typed in (the historical
    // inline-edit bug). The rebuild is deferred and applied when the draft closes.

    /// <summary>The id of the provider currently open for editing, or <c>null</c> when none.</summary>
    private string? _editingProviderId;

    /// <summary>Whether an "add custom endpoint" draft is open at the top of the provider list.</summary>
    private bool _addingProvider;

    /// <summary>Whether an "add model" draft row is open inside a provider.</summary>
    private bool _addingModel;

    /// <summary>The name of the model being edited (context-window draft), or <c>null</c> when none.</summary>
    private string? _editingModelName;

    /// <summary>True while any draft is open, which suspends the group rebuild (see the note above).</summary>
    private bool DraftOpen => _editingProviderId is not null || _addingProvider || _addingModel || _editingModelName is not null;

    /// <summary>Set when a rebuild was skipped while a draft was open; applied when the draft closes.</summary>
    private bool _pendingReload;

    /// <summary>Opens the first model's edit draft without a click — the seam --verify-shell drives to
    /// assert that the model-row pencil's draft builds (it once tore the tree on the first press).</summary>
    internal void OpenFirstModelEditForCheck()
    {
        var provider = _chat.Providers.FirstOrDefault(candidate => candidate.Models.Count > 0)
            ?? throw new InvalidOperationException("no provider with a configured model");
        OpenModelEditDraft(provider, provider.Models[0]);
    }

    /// <summary>How many model-draft rows are in the tree. More than one means the page-wide draft flags
    /// leaked into a group that is not the one being edited.</summary>
    internal int ModelDraftCountForCheck
        => this.GetLogicalDescendants().OfType<Control>()
            .Count(control => SectionTags.ModelDraft.Equals(control.Tag));

    /// <summary>Closes whatever draft is open through the same path cancel and save use.</summary>
    internal void CloseDraftForCheck() => CloseDraft();

    /// <summary>For the XAML loader and design-time preview (missing it raises AVLN3001).</summary>
    public SettingsPage()
    {
        _workspace = null!;
        _preferencesStore = null!;
        _preferences = new HubPreferences();
        _chat = null!;
        _openFolder = _ => { };
        _languageChanged = () => { };
        _switchDataRoot = _ => false;
        _applyTheme = _ => null;

        InitializeComponent();
    }

    public SettingsPage(
        HubWorkspace workspace,
        PreferencesStore preferencesStore,
        HubPreferences preferences,
        ChatWorkspace chat,
        Action<string> openFolder,
        Action languageChanged,
        Func<string, bool> switchDataRoot,
        Func<string, string?> applyTheme)
    {
        _workspace = workspace;
        _preferencesStore = preferencesStore;
        _preferences = preferences;
        _chat = chat;
        _openFolder = openFolder;
        _languageChanged = languageChanged;
        _switchDataRoot = switchDataRoot;
        _applyTheme = applyTheme;

        InitializeComponent();

        _workspace.Changed += Reload;

        LanguagePicker.SelectionChanged += (_, _) => OnLanguageChanged();
        ThemePicker.SelectionChanged += (_, _) => OnThemeChanged();
        // The custom URL is committed on focus loss rather than on every keystroke: each commit
        // writes the settings file, and "typed half a URL" is not a state worth persisting.
        DownloadSourcePicker.SelectionChanged += (_, _) => OnDownloadSourceChanged();
        ToolApprovalPicker.SelectionChanged += (_, _) => OnToolApprovalChanged();
        // The revoke-all button is the one escape from a grant the user cannot find the card for any more — the
        // session that made it may be long archived, while the trust it wrote is still answering for every session.
        RevokeAllTrustedToolsButton.Click += (_, _) => RevokeAllTrusted();
        UpdateChannelPicker.SelectionChanged += (_, _) => OnUpdateChannelChanged();
        CustomDownloadSourceBox.LostFocus += (_, _) => OnCustomDownloadSourceChanged();
        ChooseDataDirectoryButton.Click += async (_, _) => await ChooseDataDirectoryAsync();
        ChooseProjectDirectoryButton.Click += async (_, _) => await ChooseProjectDirectoryAsync();
        OpenDataFolderButton.Click += (_, _) => _openFolder(_workspace.Store.Root);
        SelectVisualStudioButton.Click += async (_, _) => await SelectEditorAsync(visualStudio: true);
        SelectCodeButton.Click += async (_, _) => await SelectEditorAsync(visualStudio: false);
        CheckForUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync();
        AutoDownloadCheck.IsCheckedChanged += (_, _) => OnAutoDownloadChanged();
        SpawnCheck.IsCheckedChanged += (_, _) => OnSpawnChanged();
        WebFetchCheck.IsCheckedChanged += (_, _) => OnWebFetchChanged();
        WireProviders();
        InitializeCategories();

        // The update card is a *view* of the shared UpdateService, so a check started elsewhere (the shell's
        // startup check) shows up here live. The download itself is no longer this page's to report — that is
        // the strip's queue slot — so what is rendered is the check result and nothing else. Hooked while the
        // page is on screen only: a cached page that has been navigated away shouldn't keep rendering.
        AttachedToVisualTree += (_, _) => HookUpdateService();
        DetachedFromVisualTree += (_, _) => UnhookUpdateService();

        // DataRootNote's copy goes through XAML's {DynamicResource}; **don't** assign it imperatively here:
        // an imperative value won't change on language switch, and the self-check reads this control's text to judge whether localization works.
        Reload();
    }

    // ───────────────────────── The category rail ─────────────────────────

    /// <summary>The row the card opens on when nobody asked for a specific one.</summary>
    internal const string DefaultCategory = "appearance";

    /// <summary>Every category the rail can land on, in rail order. The shell holds the deep-link string, so
    /// this is the one list both sides agree on — a key that is not here is not a category.</summary>
    internal static readonly string[] CategoryKeys =
        ["appearance", "providers", "permission", "engine", "storage", "updates", "editors"];

    internal static bool IsCategory(string? key)
        => key is { Length: > 0 } && Array.IndexOf(CategoryKeys, key) >= 0;

    /// <summary>The rail's rows and the pane each one shows, paired in code rather than by naming convention:
    /// a row that opens nothing, or a pane nobody can reach, is exactly the mistake a convention hides.</summary>
    private (string Key, RadioButton Row, Control Pane)[] _categories = [];

    private string _selectedCategory = DefaultCategory;

    private void InitializeCategories()
    {
        _categories =
        [
            ("appearance", CatAppearance, PaneAppearance),
            ("providers", CatProviders, PaneProviders),
            ("permission", CatPermission, PanePermission),
            ("engine", CatEngine, PaneEngine),
            ("storage", CatStorage, PaneStorage),
            ("updates", CatUpdates, PaneUpdates),
            ("editors", CatEditors, PaneEditors),
        ];

        foreach (var (key, row, _) in _categories)
        {
            row.Click += (_, _) => SelectCategory(key);
        }

        SelectCategory(_selectedCategory);
    }

    /// <summary>Shows one group and hides the other six. The rail's checked state is set here rather than left
    /// to the toggle: a raised <c>Click</c> does not flip a ToggleButton (measured, and the reason the shell's
    /// own checks drive rows through this method), and a row that reads checked while its pane is hidden is a
    /// worse answer than one that reads unchecked.</summary>
    internal void SelectCategory(string key)
    {
        if (!IsCategory(key))
        {
            key = DefaultCategory;
        }

        _selectedCategory = key;
        if (_categories.Length == 0)
        {
            // Constructed before the rail was paired — the constructor's own Reload lands here.
            return;
        }

        foreach (var (candidate, row, pane) in _categories)
        {
            var shown = candidate == key;
            row.IsChecked = shown;
            pane.IsVisible = shown;
        }

        // Every group shares the one detail scroll, so an offset left over from the bottom of a long provider
        // list would open the next group somewhere in its middle.
        DetailScroll.Offset = default;
    }

    internal string SelectedCategory => _selectedCategory;

    /// <summary>Whether any picker in the card has its dropdown up. The shell's Escape handler asks, because a
    /// dropdown is its own top-level: the key belongs to the list the reader is looking at, not to the card
    /// behind it.</summary>
    internal bool HasOpenDropDown
        => this.GetLogicalDescendants().OfType<ComboBox>().Any(box => box.IsDropDownOpen);

    /// <summary>
    /// The language tags declared in XAML. The language mapping lives on XAML's <c>Tag</c>; here we just read it out —
    /// assertions rely on it to confirm "the languages the dropdown items declare" and "the supported languages the code knows" haven't diverged.
    /// </summary>
    internal string[] DeclaredLanguages
        => LanguagePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    /// <summary>The currently selected language tag. Unknown/none fall back to the default language (same rule as <see cref="HubTexts.Normalize"/>).</summary>
    internal string SelectedLanguage
        => LanguagePicker.SelectedItem is ComboBoxItem { Tag: string tag } && HubTexts.IsSupported(tag)
            ? tag
            : HubTexts.DefaultLanguage;

    /// <summary>The theme tags declared in XAML, in the order they appear. Assertions compare it against <see cref="HubTheme.All"/>.</summary>
    internal string[] DeclaredThemes
        => ThemePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    /// <summary>The currently selected theme. Unknown/none fall back to following the system (same rule as <see cref="HubTheme.Normalize"/>).</summary>
    internal string SelectedTheme
        => ThemePicker.SelectedItem is ComboBoxItem { Tag: string tag } && HubTheme.IsSupported(tag)
            ? HubTheme.Normalize(tag)
            : HubTheme.DefaultTheme;

    /// <summary>
    /// The editor-executable validation rule (the WPF version wrote it once per branch).
    /// Extracted into a pure static function so it **can be asserted** — actually popping a file picker can't be automated,
    /// and "rejecting the wrong file" is exactly the branch most easily missed and hardest to spot in a few manual clicks.
    /// </summary>
    internal static bool MatchesEditorExecutable(bool visualStudio, string path)
        => System.IO.Path.GetFileName(path).Equals(visualStudio ? "devenv.exe" : "Code.exe", StringComparison.OrdinalIgnoreCase);

    internal void Reload()
    {
        if (_workspace is null)
        {
            return;
        }

        _ready = false;
        try
        {
            // A language switch discards any open draft rather than transplanting half-typed input across a
            // locale boundary. Clearing the flags first lets ReloadProviders below rebuild from scratch.
            CloseDraft();

            LocalizeThemeItems();
            LocalizeDownloadSourceItems();
            LocalizeToolApprovalItems();
            // The trust list is rebuilt rather than re-bound, for the same reason the provider groups are: every
            // row carries the tool name its revoke button has to write back.
            RebuildTrustedTools();
            LocalizeUpdateChannelItems();
            SelectLanguage(HubStrings.Language);
            SelectTheme(_preferences.Theme);
            SelectDownloadSource(_preferences.DownloadSource);
            SelectToolApproval(_preferences.ToolApprovalMode);
            SelectUpdateChannel(HubReleaseInfo.IsPrereleaseBuild ? UpdateChannels.Preview : _preferences.UpdateChannel);
            UpdateChannelPicker.IsEnabled = !HubReleaseInfo.IsPrereleaseBuild;
            CustomDownloadSourceBox.Text = _preferences.CustomDownloadSource ?? "";
            DataLocation.Text = _workspace.Store.Root;
            DefaultProjectLocation.Text = _preferences.ProjectDirectory ?? HubStrings.Get("NotSelected");

            var state = _workspace.State;
            var engine = state.Engines.FirstOrDefault(candidate => candidate.Path == state.DefaultEnginePath);
            DefaultEngine.Text = engine?.ToString() ?? HubStrings.Get("NoDefault");
            VisualStudioLocation.Text = Display(state.VisualStudioExecutable);
            CodeLocation.Text = Display(state.CodeExecutable);
            // The current-version line is read from the assembly; it is not localizable copy, so it
            // is safe to assign imperatively here and won't need a language-switch refresh.
            CurrentVersionLine.Text = HubStrings.Get("CurrentVersion") + "  v" +
                (typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            // Visual Studio (devenv.exe) is Windows-only; hide its row on other hosts so the user isn't
            // offered an editor that can never be installed there.
            VisualStudioRow.IsVisible = OperatingSystem.IsWindows();
            // The rule between the two editor rows divides one editor from two, so it follows the row that
            // decides the count rather than being painted unconditionally.
            EditorRule.IsVisible = VisualStudioRow.IsVisible;
            // The auto-download switch mirrors the preference. Assigning it here doesn't re-save:
            // _ready is false for the whole reload (the same gate the dropdowns rely on). It is
            // disabled on a dev/portable copy, where there is nothing to auto-update.
            AutoDownloadCheck.IsChecked = _preferences.AutoDownloadUpdates;
            AutoDownloadCheck.IsEnabled = UpdateService.Instance.IsInstalled;
            SpawnCheck.IsChecked = _preferences.AllowSpawnedSessions;
            WebFetchCheck.IsChecked = _preferences.AllowOutboundWebFetch;
            // Re-render the update card from the last known check: this re-localizes the status line
            // on a language switch (it is written imperatively) and reflects a check the shell already
            // ran at startup (the update dot and this card share UpdateService.Last).
            RenderUpdateState();
            ReloadProviders();
        }
        finally
        {
            _ready = true;
        }
    }

    /// <summary>
    /// Selects an item by the tag the dropdown item itself declares. If not found, selects the first item — never leaves an empty "nothing selected" state.
    /// Verification code calls it directly — same idea as navigation using <c>NavSettings.IsChecked = true</c>:
    /// go through the real event path rather than bypassing the event to mutate internal state.
    /// </summary>
    internal void SelectLanguage(string language)
    {
        var index = 0;
        foreach (var item in LanguagePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == HubTexts.Normalize(language))
            {
                LanguagePicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        LanguagePicker.SelectedIndex = 0;
    }

    /// <summary>
    /// Writes the three download-source labels from the text table. Imperative for the same reason as
    /// <see cref="LocalizeThemeItems"/>: <c>DynamicResource</c> on a ComboBoxItem inside <c>Items</c>
    /// resolves empty, and <see cref="Reload"/> runs on every language switch.
    /// </summary>
    private void LocalizeDownloadSourceItems()
    {
        foreach (var item in DownloadSourcePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } source && DownloadSources.IsKnown(tag))
            {
                source.Content = HubStrings.Get(DownloadSources.TextKey(tag));
            }
        }
    }

    /// <summary>The download-source tags declared in XAML, in order. Same contract as <see cref="DeclaredThemes"/>.</summary>
    internal string[] DeclaredDownloadSources
        => DownloadSourcePicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    /// <summary>The selected download source; unknown tags fall back to the official host (same rule as <see cref="DownloadSources.Normalize"/>).</summary>
    internal string SelectedDownloadSource
        => DownloadSourcePicker.SelectedItem is ComboBoxItem { Tag: string tag } && DownloadSources.IsKnown(tag)
            ? DownloadSources.Normalize(tag)
            : DownloadSources.GitHubId;

    /// <summary>Selects a source by the tag the item declares; not found → the first (GitHub).</summary>
    internal void SelectDownloadSource(string source)
    {
        var index = 0;
        foreach (var item in DownloadSourcePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == DownloadSources.Normalize(source))
            {
                DownloadSourcePicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        DownloadSourcePicker.SelectedIndex = 0;
    }

    /// <summary>
    /// Commits a download-source change. Invalid custom input is refused **before** the preference is
    /// written, and the UI is reverted: saving "custom" with a URL we can't use would make the next
    /// install fail with an error the user has no way to connect back to this page.
    /// </summary>
    private void OnDownloadSourceChanged()
    {
        if (!_ready) return;

        var source = SelectedDownloadSource;
        var custom = CustomText;
        // The value must really have changed: Reload re-selects on every refresh, and SelectionChanged
        // fires for that too (the WPF version's `preferencesReady` gate exists for the same reason).
        if (source == _preferences.DownloadSource && custom == (_preferences.CustomDownloadSource ?? "")) return;

        if (TrySaveDownloadSource(source, custom)) return;

        _ready = false;
        SelectDownloadSource(_preferences.DownloadSource);
        _ready = true;
    }

    private void OnCustomDownloadSourceChanged()
    {
        if (!_ready) return;

        var custom = CustomText;
        if (custom == (_preferences.CustomDownloadSource ?? "")) return;
        // While another source is selected the text is just a draft; committing it would save a
        // preference that isn't in effect yet.
        if (SelectedDownloadSource != DownloadSources.CustomId) return;

        if (TrySaveDownloadSource(SelectedDownloadSource, custom)) return;

        _ready = false;
        CustomDownloadSourceBox.Text = _preferences.CustomDownloadSource ?? "";
        _ready = true;
    }

    private string CustomText => (CustomDownloadSourceBox.Text ?? "").Trim();

    /// <summary>
    /// Writes the three permission-mode labels. Imperative for the same measured reason as
    /// <see cref="LocalizeThemeItems"/>: a ComboBoxItem inside <c>Items</c> is not attached to the logical tree
    /// until the dropdown opens, so <c>DynamicResource</c> resolves empty. Only <c>Content</c> is touched, so
    /// this cannot re-enter <see cref="OnToolApprovalChanged"/>.
    /// </summary>
    private void LocalizeToolApprovalItems()
    {
        foreach (var item in ToolApprovalPicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } mode && IsToolApprovalMode(tag))
            {
                mode.Content = HubStrings.Get(ChatPanel.ToolApprovalModeKey(tag));
            }
        }
    }

    private static bool IsToolApprovalMode(string tag)
        => tag is ToolApprovalModes.Ask or ToolApprovalModes.Auto or ToolApprovalModes.Full;

    /// <summary>The permission modes declared in XAML, in order. Same contract as <see cref="DeclaredThemes"/>.</summary>
    internal string[] DeclaredToolApprovalModesForCheck
        => ToolApprovalPicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    /// <summary>The selected mode, with an unrecognized value falling back to ask — the same rule the gate uses,
    /// so the picker cannot offer a setting the runtime would ignore in a different direction.</summary>
    internal string SelectedToolApprovalModeForCheck
        => ToolApprovalPicker.SelectedItem is ComboBoxItem { Tag: string tag } && IsToolApprovalMode(tag)
            ? tag
            : ToolApprovalModes.Ask;

    internal void SelectToolApproval(string mode)
    {
        var normalized = ToolApprovalModes.Normalize(mode);
        var index = 0;
        foreach (var item in ToolApprovalPicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == normalized)
            {
                ToolApprovalPicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        ToolApprovalPicker.SelectedIndex = 0;
    }

    /// <summary>The three labels really were written from the text table — a <c>ComboBoxItem</c> in
    /// <c>Items</c> is the one place a bound resource is known to resolve empty.</summary>
    internal bool ToolApprovalLabelsAreLocalizedForCheck
        => ToolApprovalPicker.Items.OfType<ComboBoxItem>()
            .All(item => item.Content is string text && text.Length > 0);

    private void LocalizeUpdateChannelItems()
    {
        foreach (var item in UpdateChannelPicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } channel && UpdateChannels.IsSupported(tag))
            {
                channel.Content = HubStrings.Get(tag == UpdateChannels.Preview
                    ? "UpdateChannelPreview"
                    : "UpdateChannelStable");
            }
        }
    }

    internal string[] DeclaredUpdateChannels
        => UpdateChannelPicker.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? "").ToArray();

    internal string SelectedUpdateChannel
        => UpdateChannelPicker.SelectedItem is ComboBoxItem { Tag: string tag } && UpdateChannels.IsSupported(tag)
            ? UpdateChannels.Normalize(tag)
            : UpdateChannels.DefaultChannel;

    internal bool UpdateChannelLabelsAreLocalizedForCheck
        => UpdateChannelPicker.Items.OfType<ComboBoxItem>()
            .All(item => item.Content is string text && text.Length > 0);

    internal bool UpdateChannelPickerEnabledForCheck => UpdateChannelPicker.IsEnabled;

    internal void SelectUpdateChannel(string channel)
    {
        var normalized = UpdateChannels.Normalize(channel);
        var index = 0;
        foreach (var item in UpdateChannelPicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == normalized)
            {
                UpdateChannelPicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        UpdateChannelPicker.SelectedIndex = 0;
    }

    private void OnUpdateChannelChanged()
    {
        if (!_ready || HubReleaseInfo.IsPrereleaseBuild) return;

        var channel = SelectedUpdateChannel;
        if (channel == _preferences.UpdateChannel) return;

        _preferences.UpdateChannel = channel;
        _preferencesStore.Save(_preferences);
        UpdateService.Instance.UpdateChannel = channel;
    }

    private void OnToolApprovalChanged()
    {
        if (!_ready) return;

        // One owner for the write. The assistant already reads this setting, offers the same change on the
        // composer's own menu row, and records the move in the activity log; a second path here would be a second
        // meaning for "the default moved", and the one that skips the audit line is the one that leaves a mystery
        // in the log the next time a call asks — or stops asking — for a reason nobody can read back.
        _chat.SetDefaultApprovalMode(SelectedToolApprovalModeForCheck);
    }

    /// <summary>
    /// The app-wide trust list, as rows. Built in code because each row's revoke button has to carry the tool name
    /// it belongs to — the same reason <see cref="RebuildProviderGroups"/> is not a bound template.
    ///
    /// <para>No per-row tier annotation: which card a grant came from is a fact about a session's sandbox, and this
    /// page has no session in front of it. What a grant does and does not cover is said once, in the hint above the
    /// list, where it is true for every row.</para>
    /// </summary>
    private void RebuildTrustedTools()
    {
        TrustedToolList.Children.Clear();
        if (_chat is null) return;
        var trusted = _chat.TrustedTools;
        if (trusted.Count == 0)
        {
            TrustedToolList.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("TrustedToolsNone"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                Tag = TrustedToolRowTag.None,
            });
            RevokeAllTrustedToolsButton.IsVisible = false;
            return;
        }

        RevokeAllTrustedToolsButton.IsVisible = true;
        foreach (var tool in trusted)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Tag = TrustedToolRowTag.Row };
            row.Children.Add(new TextBlock
            {
                Text = tool,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            var revoke = new Button
            {
                Classes = { "quiet" },
                Content = HubStrings.Get("RevokeTrustedTool"),
                Tag = tool,
            };
            revoke.Click += (_, _) => RevokeTrusted(tool);
            row.Children.Add(revoke);
            Grid.SetColumn(revoke, 1);
            TrustedToolList.Children.Add(row);
        }
    }

    private void RevokeTrusted(string tool)
    {
        if (!_chat.RevokeTrustedTool(tool)) return;
        RebuildTrustedTools();
    }

    private void RevokeAllTrusted()
    {
        if (!_chat.RevokeAllTrustedTools()) return;
        RebuildTrustedTools();
    }

    /// <summary>The trust list as stored, for the assertion that a grant written from a card lands here.</summary>
    internal string[] TrustedToolsForCheck => (_chat?.TrustedTools ?? []).ToArray();

    /// <summary>How many rows the page drew, counting the empty-state line as none.</summary>
    internal int TrustedToolRowsShownForCheck
        => TrustedToolList.Children.OfType<Control>().Count(child => TrustedToolRowTag.Row.Equals(child.Tag));

    internal bool TrustedToolsEmptyShownForCheck
        => TrustedToolList.Children.OfType<Control>().Any(child => TrustedToolRowTag.None.Equals(child.Tag));

    /// <summary>Grants a tool through the same workspace call the card's button makes, so the page is checked
    /// against the write path rather than against a copy of it.</summary>
    internal bool TrustToolForCheck(string tool)
    {
        var granted = _chat?.TrustTool(tool) == true;
        if (granted) RebuildTrustedTools();
        return granted;
    }

    internal bool RevokeTrustedToolForCheck(string tool)
    {
        var revoked = _chat?.RevokeTrustedTool(tool) == true;
        if (revoked) RebuildTrustedTools();
        return revoked;
    }

    internal bool RevokeAllTrustedToolsForCheck()
    {
        var revoked = _chat?.RevokeAllTrustedTools() == true;
        if (revoked) RebuildTrustedTools();
        return revoked;
    }

    private static class TrustedToolRowTag
    {
        internal const string Row = "trusted-tool-row";
        internal const string None = "trusted-tools-none";
    }

    private bool TrySaveDownloadSource(string source, string custom)
    {
        if (DownloadSources.Validate(source, custom) is { } problem)
        {
            _ = HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("OperationFailed"), HubStrings.Get(problem));
            return false;
        }

        _preferences.DownloadSource = source;
        _preferences.CustomDownloadSource = custom.Length == 0 ? null : custom;
        _preferencesStore.Save(_preferences);
        return true;
    }

    /// <summary>Copy key for a theme value. Kept next to the values so adding a theme can't leave its label behind.</summary>
    internal static string ThemeTextKey(string theme) => HubTheme.Normalize(theme) switch
    {
        HubTheme.Light => "ThemeLight",
        HubTheme.Dark => "ThemeDark",
        _ => "ThemeSystem",
    };

    /// <summary>What each theme value actually decides, for the one-line hint the bottom menu's row carries.
    /// Same reason as <see cref="ThemeTextKey"/>: a fourth theme must not be able to arrive without a
    /// sentence.</summary>
    internal static string ThemeHintKey(string theme) => HubTheme.Normalize(theme) switch
    {
        HubTheme.Light => "ThemeLightHint",
        HubTheme.Dark => "ThemeDarkHint",
        _ => "ThemeSystemHint",
    };

    /// <summary>The glyph for a theme value. Also total over the three, for the same reason.</summary>
    internal static string ThemeIconKey(string theme) => HubTheme.Normalize(theme) switch
    {
        HubTheme.Light => "Hub.Icon.Sun",
        HubTheme.Dark => "Hub.Icon.Moon",
        _ => "Hub.Icon.Monitor",
    };

    /// <summary>
    /// The languages the interface speaks and the name each one is called by its own speakers. The picker in
    /// XAML declares the same pairs and an assertion compares the two — the bottom menu cannot read the
    /// picker's items (the page may not exist yet), and a second copy of the list is exactly where they would
    /// drift. The display names are **not** localized: they are here for people who cannot read the current UI.
    /// </summary>
    internal static readonly (string Tag, string Display)[] LanguageChoices =
        [(HubTexts.ChineseLanguage, "简体中文"), (HubTexts.EnglishLanguage, "English")];

    /// <summary>
    /// Writes the three labels from the text table. It is imperative **on purpose**: a ComboBoxItem
    /// inside <c>Items</c> is not attached to the logical tree until the dropdown opens, and
    /// <c>DynamicResource</c> resolves through the resource host it is attached to — measured here,
    /// all three labels silently came back empty. Reload runs on every language switch
    /// (see <c>MainWindow.ApplyLanguage</c>), which is what keeps them in the current language.
    /// Only the <c>Content</c> is touched, never the selection, so this can't re-enter
    /// <see cref="OnThemeChanged"/>.
    /// </summary>
    private void LocalizeThemeItems()
    {
        foreach (var item in ThemePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } theme && HubTheme.IsSupported(tag))
            {
                theme.Content = HubStrings.Get(ThemeTextKey(tag));
            }
        }
    }

    /// <summary>
    /// Selects a theme item by the tag the item itself declares; not found → the first item (following
    /// the system), never an empty "nothing selected" state. Same contract as <see cref="SelectLanguage"/>.
    /// </summary>
    internal void SelectTheme(string theme)
    {
        var index = 0;
        foreach (var item in ThemePicker.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && tag == HubTheme.Normalize(theme))
            {
                ThemePicker.SelectedIndex = index;
                return;
            }

            index++;
        }

        ThemePicker.SelectedIndex = 0;
    }

    /// <summary>
    /// Switches language: write settings → reload the resource dictionary → existing controls follow with new text.
    ///
    /// The order can't be reversed: reload the dictionary before writing settings, otherwise a failed write leaves "UI switched language but settings not saved",
    /// reverting on the next launch and looking like a lost setting.
    ///
    /// Notify the shell after reloading: the shell and pages have **imperatively** written copy that DynamicResource doesn't cover.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (!_ready)
        {
            return;
        }

        var previous = HubStrings.Language;
        var language = SelectedLanguage;
        if (language == previous)
        {
            return;
        }

        try
        {
            _preferences.Language = language;
            _preferencesStore.Save(_preferences);

            // Reload in place: DynamicResource re-resolves when the resource dictionary changes, so
            // already-built controls (and other windows) also swap text — no window rebuild needed.
            HubStrings.Apply(language, Application.Current!);
            _languageChanged();
        }
        catch (Exception ex)
        {
            // On failure, revert the UI, otherwise it would show a language that disagrees with the settings file.
            _ready = false;
            _preferences.Language = previous;
            HubStrings.Apply(previous, Application.Current!);
            SelectLanguage(previous);
            _ready = true;
            _ = HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("OperationFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Switches theme through the shell (<see cref="MainWindow.UseTheme"/>), which owns the
    /// persist → apply → revert sequence — the bottom menu's appearance items share it.
    ///
    /// The page's own job is only the picker: on failure put the selection back, otherwise the
    /// dropdown would show a theme that isn't in effect.
    /// </summary>
    private void OnThemeChanged()
    {
        if (!_ready)
        {
            return;
        }

        var previous = ThemeService.Current;
        var theme = SelectedTheme;
        if (theme == previous)
        {
            return;
        }

        var error = _applyTheme(theme);
        if (error is null)
        {
            return;
        }

        _ready = false;
        SelectTheme(previous);
        _ready = true;
        _ = HubDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, HubStrings.Get("OperationFailed"), error);
    }

    private async Task ChooseDataDirectoryAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var picked = await Pickers.PickFolderAsync(top, HubStrings.Get("Select data directory"));
        if (picked.Outcome != PickOutcome.Picked)
        {
            if (picked.Outcome == PickOutcome.NotLocal)
            {
                await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
            }

            return;
        }

        try
        {
            // After a successful switch **this page instance is discarded** (the shell clears the page cache and rebuilds),
            // so here we must not touch any of our own controls: that would read an already-disposed workspace.
            // The success path ends here; only the failure path needs a dialog — and this page is still alive then.
            // The whole workspace is rebuilt on switch (page cache cleared), so confirm first: it is the one
            // "Browse" on this page that really re-homes Hub rather than just storing a path.
            var confirm = string.Format(HubStrings.Get("SwitchDataRootPrompt"), picked.Path);
            if (await HubDialog.ShowAsync(top as Window, HubStrings.Get("DataDirectory"), confirm, HubDialogButtons.OkCancel) != HubDialogResult.Ok)
            {
                return;
            }

            _switchDataRoot(picked.Path!);
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), ex.Message);
        }
    }

    private async Task ChooseProjectDirectoryAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var picked = await Pickers.PickFolderAsync(top, HubStrings.Get("Select default project directory"));
        if (picked.Outcome != PickOutcome.Picked)
        {
            if (picked.Outcome == PickOutcome.NotLocal)
            {
                await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
            }

            return;
        }

        try
        {
            // Unlike the data root, the project parent directory **only stores a path, no switch**: it carries no state, so switching needs no window rebuild.
            // Persistence and validation both happen in the workspace; on failure it rolls the preference back to the previous value.
            _workspace.SetProjectDirectory(picked.Path!);
            Reload();
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), ex.Message);
        }
    }

    private async Task SelectEditorAsync(bool visualStudio)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        // Both the title and the rejection copy name the concrete executable: when picked wrong, the user should see at a glance which one to choose, not just be told "wrong pick".
        var title = HubStrings.Get(visualStudio ? "Select devenv.exe" : "Select Code.exe");
        var picked = await Pickers.PickFileAsync(top, title,
            [new FilePickerFileType(visualStudio ? "devenv.exe" : "Code.exe") { Patterns = ["*.exe"] }]);

        if (picked.Outcome != PickOutcome.Picked)
        {
            if (picked.Outcome == PickOutcome.NotLocal)
            {
                await HubDialog.ShowAsync(top as Window, HubStrings.Get("OperationFailed"), HubStrings.Get("LocalPathRequired"));
            }

            return;
        }

        await _workspace.SelectEditorAsync(visualStudio, picked.Path);
        Reload();
    }

    private string Display(string? path) => path is { Length: > 0 } ? path : HubStrings.Get("NotSelected");

    /// <summary>
    /// Persists the "auto-download" choice and, when it is switched on with an update already known,
    /// starts the background download right away — otherwise the choice would not take effect until the
    /// next check, which reads as "the checkbox does nothing".
    /// </summary>
    /// <summary>Spawning is turned off by default and the tool says so when a model asks, so this switch is the
    /// only entrance it has — which is also why it lives beside the permission mode rather than hidden in a
    /// provider row: it is a decision about what the assistant may do on this machine, not about which model to use.</summary>
    private void OnSpawnChanged()
    {
        if (!_ready) return;
        var value = SpawnCheck.IsChecked == true;
        if (value == _preferences.AllowSpawnedSessions) return;
        _preferences.AllowSpawnedSessions = value;
        _preferencesStore.Save(_preferences);
    }

    /// <summary>Reads the switch the way the assistant does, for a self-check that has to prove the preference is
    /// the thing the tool consults rather than a checkbox that remembers nothing.</summary>
    internal bool AllowSpawnedSessionsForCheck => _preferences.AllowSpawnedSessions;

    /// <summary>Outbound fetching ships <b>on</b>, unlike spawning, so this checkbox is an exit rather than an
    /// entrance: its job is to let a person say "nothing this machine reads goes out without me deciding". The
    /// assistant's own refusal names this setting when it is off, which is the only reason the switch and the
    /// sentence can not drift apart.</summary>
    private void OnWebFetchChanged()
    {
        if (!_ready) return;
        var value = WebFetchCheck.IsChecked == true;
        if (value == _preferences.AllowOutboundWebFetch) return;
        _preferences.AllowOutboundWebFetch = value;
        _preferencesStore.Save(_preferences);
    }

    /// <summary>The same argument as the spawn hook: a self-check that turns fetching off has to read the
    /// preference the tool consults, not the checkbox that forgot it.</summary>
    internal bool AllowOutboundWebFetchForCheck => _preferences.AllowOutboundWebFetch;

    private void OnAutoDownloadChanged()
    {
        if (!_ready)
        {
            return;
        }

        var value = AutoDownloadCheck.IsChecked == true;
        if (value == _preferences.AutoDownloadUpdates)
        {
            return;
        }

        _preferences.AutoDownloadUpdates = value;
        _preferencesStore.Save(_preferences);
        UpdateService.Instance.AutoDownload = value;
        if (value)
        {
            UpdateService.Instance.StartPendingDownload();
        }
    }

    /// <summary>Live-renders the card whenever the shared update state changes (check result or download
    /// progress). The event can be raised from the download thread, so marshal before touching controls.</summary>
    private void OnUpdateChanged() => Dispatcher.UIThread.Post(RenderUpdateState);

    private void HookUpdateService()
    {
        if (_updateHooked)
        {
            return;
        }

        _updateHooked = true;
        UpdateService.Instance.Changed += OnUpdateChanged;
        RenderUpdateState();
    }

    private void UnhookUpdateService()
    {
        if (!_updateHooked)
        {
            return;
        }

        _updateHooked = false;
        UpdateService.Instance.Changed -= OnUpdateChanged;
    }

    /// <summary>
    /// The settings-page "Check for updates" entry point. Unlike the silent startup check, this one
    /// surfaces every outcome: the button is clicked *because* the user wants a visible answer.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        // The status line doubles as a "busy" indicator; the button is disabled while a check runs so
        // a second click can't stack a second network round-trip on top of the first.
        _checking = true;
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusLine.Text = HubStrings.Get("CheckingUpdate");
        try
        {
            await UpdateService.Instance.CheckAsync();
        }
        finally
        {
            _checking = false;
            CheckForUpdatesButton.IsEnabled = true;
            RenderUpdateState();
        }
    }

    /// <summary>
    /// Renders the card from <see cref="UpdateService"/>: the check result — the status line, and whether the
    /// check button is offered. Called when the page is shown, on <see cref="UpdateService.Changed"/>, and
    /// after a manual check, so a check started elsewhere (the shell's startup check) shows up here too and
    /// the line re-localizes on a language switch.
    ///
    /// <para>What is deliberately <b>not</b> on this card any more: the download's progress, its cancel, and
    /// the restart. Those live on one surface now — the strip's queue slot and its popover — because this page
    /// can be navigated away from while the bytes are moving, and an update whose end nobody can see is an
    /// update nobody can restart. The result line keeps naming the version for that same reason: it is the one
    /// thing this page owns, and it stays true while the transfer runs somewhere else.</para>
    /// </summary>
    private void RenderUpdateState()
    {
        // A manual check owns the status line while it runs; don't overwrite "checking…".
        if (_checking)
        {
            return;
        }

        var service = UpdateService.Instance;

        var last = service.Last;
        UpdateStatusLine.Text = last?.Result switch
        {
            UpdateService.CheckResult.UpdateAvailable => string.Format(HubStrings.Get("UpdateReady"), service.PendingVersion),
            UpdateService.CheckResult.UpToDate => HubStrings.Get("UpdateUpToDate"),
            UpdateService.CheckResult.NotInstalled => HubStrings.Get("UpdateNotInstalled"),
            UpdateService.CheckResult.Failed => HubStrings.Get("UpdateFailed"),
            _ => HubStrings.Get("UpdateCheckHint"),
        };

        // The check button steps aside when there is already something to act on: offering another 检查更新
        // under the line that says what the last check found would repeat the question it answered. Fetching
        // that version is the strip's press, reachable from whatever page is on screen.
        CheckForUpdatesButton.IsVisible = last?.Result is not UpdateService.CheckResult.UpdateAvailable;
    }

    /// <summary>
    /// The provider card's own status line. Separate from the update card's on purpose: they report on
    /// unrelated subsystems, and a page can only register one control per <c>x:Name</c>.
    ///
    /// <para>Text, ink and visibility are set together rather than by separate calls: the line is hidden while
    /// empty, so a caller that assigned the text after asking it to re-style would leave it invisible — a
    /// failure that looks exactly like "the operation did nothing".</para>
    /// </summary>
    private void SetProviderStatus(bool error, string text)
    {
        ProviderStatusLine.Text = text;
        ProviderStatusLine.Classes.Set("muted", !error);
        ProviderStatusLine.Classes.Set("danger", error);
        ProviderStatusLine.IsVisible = text.Length > 0;
    }

    // ───────────────────────── Model providers ─────────────────────────
    //
    // This block used to live in the assistant drawer and moved here when the assistant became a full page:
    // the conversation page keeps the model picker, and endpoint/model management — adding, enabling,
    // removing, keying — happens in Settings ▸ Models. GitHub Copilot draws the same separation.
    //
    // Adding a custom provider remains the only path to a local model, so the logic is unchanged; only its
    // host moved.

    private void WireProviders()
    {
        AddProviderButton.Click += async (_, _) => await AddProviderAsync();
    }

    /// <summary>
    /// Rebuilds the provider group list. Called from <see cref="Reload"/>, which is the only place that needs
    /// to: the rows carry no persistent state of their own — they are a rendering of <c>ChatWorkspace</c> — so
    /// a language switch and a data change are the same operation.
    /// </summary>
    private void ReloadProviders()
    {
        if (_chat is null) return;

        // A draft is live input; rebuilding under it destroys the control being typed in. Defer the rebuild
        // and apply it when the draft closes. Reload (a language switch) is the one caller that must not be
        // deferred: it calls CloseDraft() first, so by the time it reaches here no draft is open.
        if (DraftOpen)
        {
            _pendingReload = true;
            return;
        }

        AddProviderButton.Content = HubStrings.Get("AddProvider");
        RebuildProviderGroups();
    }

    /// <summary>
    /// Renders one group per configured provider. Large model lists start collapsed; smaller lists remain
    /// expanded so the short, useful ones still take one glance.
    ///
    /// <para>Built in code rather than bound: each group owns handlers that close over the provider they
    /// belong to (which credential to rotate, which model to mark), and a bound template would need a
    /// view-model per row for four controls. The cost of code-building is that a language switch has to
    /// rebuild rather than re-bind, which is why <see cref="Reload"/> calls straight in here.</para>
    /// </summary>
    private void RebuildProviderGroups()
    {
        ProviderGroupList.Children.Clear();
        if (_chat is null) return;

        // The add-custom-endpoint draft is always the first child, above every provider — the form is placed
        // where the eye already is, and it never scrolls out from under the user who just opened it.
        if (_addingProvider)
        {
            ProviderGroupList.Children.Add(BuildAddProviderDraft());
        }

        if (_chat.Providers.Count == 0 && !_addingProvider)
        {
            var empty = new StackPanel { Spacing = 6 };
            empty.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("NoProvider"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
            empty.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("NoProviderGuide"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
            ProviderGroupList.Children.Add(empty);
            return;
        }

        foreach (var provider in _chat.Providers)
        {
            ProviderGroupList.Children.Add(BuildProviderGroup(provider));
        }

        // One line for the whole card rather than one per provider: which backend holds the keys is a fact about
        // this machine, not about OrcaRouter. Windows says nothing, because DPAPI is the promise every install
        // already got; the file tier and an unreadable blob both speak up, because there "stored in the OS
        // credential store" would be an overstatement the user could act on.
        if (BuildSecretBackendLine(_chat.SecretBackend) is { } backend)
        {
            ProviderGroupList.Children.Add(backend);
        }
    }

    /// <summary>Whether the card currently carries the backend disclosure line. Read for the assertion.</summary>
    internal bool SecretBackendLineShown
        => ProviderGroupList.Children.OfType<Control>().Any(child => SectionTags.SecretBackend.Equals(child.Tag));

    /// <summary>
    /// The disclosure line as it would render for a given tier. Not a second copy of the wording: it calls the
    /// same builder the card calls, which is what lets the assertion cover the two tiers this host is not running.
    /// </summary>
    internal string? SecretBackendTextForCheck(SecretStoreKind kind, string? degradedReason)
        => (BuildSecretBackendLine(new SecretStoreDescriptor(kind, "", degradedReason)) as TextBlock)?.Text;

    /// <summary>
    /// The line that names where keys actually go, or <c>null</c> when the tier needs no explaining.
    /// Static and descriptor-driven on purpose: the same function renders the real card and the verification
    /// hook, so an assertion about the file tier's wording is an assertion about what the page would show.
    /// </summary>
    private static Control? BuildSecretBackendLine(SecretStoreDescriptor backend)
    {
        if (backend.DegradedReason is { Length: > 0 } degraded)
        {
            return new TextBlock
            {
                Text = HubStrings.Get("AuthSecretBackendDegraded") + degraded,
                Classes = { "danger" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Tag = SectionTags.SecretBackend,
            };
        }

        // Only the file tier explains itself. DPAPI is the promise every Windows install already got, a None
        // descriptor already surfaces through the auth dialog's own refusal, and repeating either here would be
        // a second label for information the screen carries once already.
        if (backend.Kind != SecretStoreKind.EncryptedFile) return null;

        return new TextBlock
        {
            Text = HubStrings.Get("AuthSecretBackendFile"),
            Classes = { "muted" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Tag = SectionTags.SecretBackend,
        };
    }

    /// <summary>
    /// One provider group: a collapsed one-line header by default, or the in-place edit draft when the pencil is
    /// pressed. The model section is shown in <b>both</b> states — collapsing hides the edit form, not the
    /// model list, because the list is what a person glances at to answer "what can I call here?".
    ///
    /// <para>There is deliberately no "browse but not edit" expanded state: the only reason to open a provider
    /// is to change it, so the pencil opens the draft directly and a stale read-only layout never has to be kept
    /// in sync with the draft.</para>
    /// </summary>
    private Control BuildProviderGroup(ModelProvider provider)
    {
        var editing = _editingProviderId == provider.Id;
        var content = new StackPanel { Spacing = 12 };

        content.Children.Add(editing ? BuildEditDraft(provider) : BuildCollapsedHeader(provider));

        // Identity row is separated from the model list by a hairline, so the card reads as two bands.
        content.Children.Add(new Border
        {
            Height = 1,
            Background = BrushOrNull("Hub.BorderSubtle"),
            Tag = SectionTags.ProviderHeaderDivider,
            Margin = new Thickness(0, 2),
        });

        // The model section is present in both states; only the edit form is gated on the draft.
        content.Children.Add(BuildModelSection(provider));

        if (!provider.CanAuthenticate)
        {
            content.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("ProviderNoCredential"),
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Tag = SectionTags.NoCredential,
            });
        }

        if (provider.Affiliate)
        {
            content.Children.Add(BuildAffiliateSection(provider));
        }

        // Outlined card: the list of providers has to read as a list. Tag stays the provider id so the
        // self-check can find the group by id.
        return new Border
        {
            Classes = { "provider-card" },
            Tag = provider.Id,
            Child = content,
        };
    }

    /// <summary>
    /// The collapsed one-line header: name (with source/current/disabled pills), the auth status mark, the
    /// state-dependent auth action, and — revealed on hover — the pencil and trash icons.
    /// </summary>
    private Control BuildCollapsedHeader(ModelProvider provider)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock
        {
            Text = provider.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        title.Children.Add(Pill(
            HubStrings.Get(provider.IsCustom ? "ProviderCustomTag" : "ProviderBuiltInTag"),
            brush: null));

        if (!provider.Enabled)
        {
            title.Children.Add(Pill(HubStrings.Get("ProviderDisabledTag"),
                BrushOrNull("Hub.DangerBorder"), BrushOrNull("Hub.DangerText")));
        }
        else if (_chat?.ActiveProvider?.Id == provider.Id)
        {
            title.Children.Add(Pill(HubStrings.Get("ProviderInUse"), BrushOrNull("Hub.AccentSoft"),
                BrushOrNull("Hub.TextOnAccent")));
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Status mark is always painted: green + words when callable, grey + words otherwise. Colour never
        // carries the state alone.
        actions.Children.Add(BuildLinkedStatusMark(provider));

        // The single auth control. Its label is the state: 鉴权 when not linked, 断开鉴权 when linked.
        // Method inputs never live on this page — the button opens the bound method's dialog.
        if (provider.CanAuthenticate)
        {
            var linked = IsLinked(provider);
            var auth = new Button
            {
                Content = HubStrings.Get(linked ? "DisconnectProvider" : "Authenticate"),
                Classes = { "quiet" },
                FontSize = 11,
                Padding = new Thickness(8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = SectionTags.AuthButton,
            };
            auth.Click += async (_, _) =>
            {
                if (linked) await DisconnectProviderAsync(provider);
                else await AuthenticateProviderAsync(provider);
            };
            actions.Children.Add(auth);

        }

        var reveal = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "reveal" },
        };

        // Changing the bound method is secondary: it first clears the credential. Hidden until hover.
        if (provider.CanAuthenticate && provider.EffectiveAuthMethods.Count > 1)
        {
            var change = new Button
            {
                Content = HubStrings.Get("ChangeAuthMethod"),
                Classes = { "quiet" },
                FontSize = 11,
                Padding = new Thickness(6, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = SectionTags.ChangeAuthMethod,
            };
            ToolTip.SetTip(change, HubStrings.Get("ChangeAuthMethodHint"));
            change.Click += async (_, _) => await ChangeAuthMethodAsync(provider);
            reveal.Children.Add(change);
        }

        // Custom providers own their identity fields; a built-in is pinned by the manifest and has no
        // configuration left to edit on this page.
        if (provider.IsCustom)
        {
            var edit = new Button
            {
                Content = BuildEditIcon(),
                Classes = { "quiet" },
                Padding = new Thickness(6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = SectionTags.EditProvider,
            };
            ToolTip.SetTip(edit, HubStrings.Get("EditProvider"));
            edit.Click += (_, _) => OpenEditDraft(provider);
            reveal.Children.Add(edit);
        }

        if (ChatWorkspace.CanRemoveProvider(provider))
        {
            // Always visible: removing a provider is a primary management action, not a hover secret.
            // The pencil and change-method stay on the reveal cluster.
            var remove = new Button
            {
                Content = BuildRemoveIcon(),
                Classes = { "quiet" },
                Padding = new Thickness(6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = SectionTags.RemoveProvider,
            };
            ToolTip.SetTip(remove, HubStrings.Get("RemoveProviderIcon"));
            remove.Click += async (_, _) => await RemoveProviderAsync(provider);
            actions.Children.Add(remove);
        }

        actions.Children.Add(reveal);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Classes = { "provider-row" } };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(title);
        grid.Children.Add(actions);
        return grid;
    }

    /// <summary>The brush key for a provider's status dot: green when callable, tertiary otherwise.</summary>
    private static string ProviderStatusBrushKey(ModelProvider provider)
        => provider.IsCallReady ? "Hub.Success" : "Hub.TextTertiary";

    /// <summary>
    /// The edit draft: configuration only. Name and base URL are read-only for a built-in provider (the
    /// manifest pins them). Authentication never appears here — no key box, no sign-in button — so the form
    /// cannot contradict the status mark on the header.
    /// </summary>
    private Control BuildEditDraft(ModelProvider provider)
    {
        var isBuiltIn = provider is { IsCustom: false };

        var stack = new StackPanel { Spacing = 8, Tag = SectionTags.ProviderDraft };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("EditProviderTitle"),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(Pill(
            HubStrings.Get(provider.IsCustom ? "ProviderCustomTag" : "ProviderBuiltInTag"),
            brush: null));
        titleRow.Children.Add(BuildLinkedStatusMark(provider));
        stack.Children.Add(titleRow);

        if (isBuiltIn)
        {
            stack.Children.Add(new TextBlock
            {
                Text = HubStrings.Get("ProviderBuiltInLockedHint"),
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,1*"),
            RowDefinitions = new RowDefinitions("Auto"),
        };

        var name = new TextBox { Text = provider.Name, IsReadOnly = isBuiltIn };
        if (isBuiltIn) name.Classes.Add("muted");
        fields.Children.Add(BuildField(HubStrings.Get("ProviderName"), name));
        Grid.SetColumn(fields.Children[^1], 0);
        Grid.SetRow(fields.Children[^1], 0);

        var baseUrl = new TextBox { Text = provider.BaseUrl, IsReadOnly = isBuiltIn };
        if (isBuiltIn) baseUrl.Classes.Add("muted");
        fields.Children.Add(BuildField(HubStrings.Get("ProviderBaseUrl"), baseUrl));
        Grid.SetColumn(fields.Children[^1], 1);
        Grid.SetRow(fields.Children[^1], 0);

        stack.Children.Add(fields);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), Classes = { "quiet" } };
        cancel.Click += (_, _) => CloseDraft();
        var save = new Button { Content = HubStrings.Get("Save"), Classes = { "primary" }, IsDefault = true };
        save.Click += (_, _) => CommitEditDraft(provider, name, baseUrl);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        return stack;
    }

    /// <summary>Wraps a labelled field for the draft's two-column grid.</summary>
    private static Control BuildField(string label, TextBox box, string? hint = null)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Classes = { "muted" },
            FontSize = 11,
        });
        stack.Children.Add(box);
        if (hint is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = hint,
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return stack;
    }

    /// <summary>The linked-state mark in words + glyph, the three states the dot compresses into colour.</summary>
    private Control BuildLinkedStatusMark(ModelProvider provider)
    {
        var linked = IsLinked(provider);
        var keyless = !provider.RequiresCredential;
        var ready = keyless || linked;

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        status.Children.Add(ready ? BuildCheckIcon() : BuildUnlinkedIcon());

        var label = new TextBlock
        {
            Text = HubStrings.Get(
                keyless ? "ProviderKeylessReady"
                    : linked ? "ProviderConnected"
                    : "ProviderNotConnected"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                ready ? "Hub.Success" : "Hub.TextTertiary"));
        status.Children.Add(label);
        return status;
    }

    /// <summary>The "add custom endpoint" draft, inserted first in the provider list. Four fields, cancel/save,
    /// and the same validation the save path re-runs.</summary>
    private Control BuildAddProviderDraft()
    {
        var stack = new StackPanel { Spacing = 8, Tag = SectionTags.AddProviderDraft };

        stack.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("AddCustomProvider"),
            FontWeight = FontWeight.SemiBold,
        });

        // Identity only. The key is not asked here: the list page never hosts a credential box. Saving this
        // draft creates the provider, then the auth dialog takes the key — the same one-job split as the
        // preset path.
        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,1*"),
            RowDefinitions = new RowDefinitions("Auto"),
        };

        var name = new TextBox();
        fields.Children.Add(BuildField(HubStrings.Get("ProviderName"), name));
        Grid.SetColumn(fields.Children[^1], 0);
        Grid.SetRow(fields.Children[^1], 0);

        var baseUrl = new TextBox();
        fields.Children.Add(BuildField(HubStrings.Get("ProviderBaseUrl"), baseUrl, HubStrings.Get("ProviderBaseUrlHint")));
        Grid.SetColumn(fields.Children[^1], 1);
        Grid.SetRow(fields.Children[^1], 0);

        var model = new TextBox();
        fields.Children.Add(BuildField(HubStrings.Get("ProviderModel"), model));
        Grid.SetColumn(fields.Children[^1], 0);
        Grid.SetRow(fields.Children[^1], 1);

        stack.Children.Add(fields);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), Classes = { "quiet" } };
        cancel.Click += (_, _) => CloseDraft();
        var save = new Button { Content = HubStrings.Get("Save"), Classes = { "primary" }, IsDefault = true };
        save.Click += async (_, _) => await CommitAddProviderDraft(name, baseUrl, model);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        return stack;
    }

    private async Task CommitAddProviderDraft(TextBox name, TextBox baseUrl, TextBox model)
    {
        if (_chat is null) return;

        var created = _chat.AddProvider(name.Text ?? "", baseUrl.Text ?? "", model.Text ?? "", null);
        if (created is null)
        {
            SetProviderStatus(true, _chat.CanStoreSecrets
                ? HubStrings.Get("ProviderBaseUrlInvalid")
                : HubStrings.Get("ApiKeyPlatformUnsupported"));
            return;
        }

        SetProviderStatus(false, HubStrings.Get("ProviderAdded"));
        CloseDraft();
        // Connect it now so the card does not land half-ready: a custom endpoint that wants a key gets the
        // auth dialog, a keyless one is done.
        if (created.CanAuthenticate)
        {
            await AuthenticateProviderAsync(created);
        }
    }

    /// <summary>
    /// Whether a provider counts as linked: it has a saved credential <b>whose secret is really there</b>.
    ///
    /// <para>Source-agnostic by design. The account model made a pasted key and a browser sign-in the same
    /// shape, so asking "how did this credential arrive?" here would undo that — and would show "not
    /// connected" for a provider that plainly works.</para>
    ///
    /// <para>The secret rather than the record is what the mark claims. A credential is a row in
    /// <c>credentials.json</c>; the key itself lives in the OS store and is read back at load, and when that read
    /// comes up empty the provider cannot be called. Showing 已鉴权 for that state made the settings page and the
    /// conversation disagree — the row said connected, the send answered "authenticate first".</para>
    /// </summary>
    private bool IsLinked(ModelProvider provider)
        => _chat?.CredentialFor(provider.Id)?.HasSecret == true;

    /// <summary>Opens the edit draft for a provider, replacing its collapsed header.</summary>
    private void OpenEditDraft(ModelProvider provider)
    {
        _editingProviderId = provider.Id;
        _addingProvider = false;
        _addingModel = false;
        RebuildProviderGroups();
    }

    /// <summary>
    /// Closes every open draft and applies any deferred rebuild. Called on cancel, save, Escape, and — from
    /// <see cref="Reload"/> — before a language-switch rebuild that must discard the draft.
    ///
    /// <para>A close is only observable through a rebuild: <see cref="BuildProviderGroup"/> swaps the draft
    /// for the collapsed header purely from the flags, so returning without one would leave the form on
    /// screen after cancel or save. The deferred reload rides the same pass.</para>
    /// </summary>
    private void CloseDraft()
    {
        var wasOpen = DraftOpen;
        _editingProviderId = null;
        _addingProvider = false;
        _addingModel = false;
        _editingModelName = null;
        if (wasOpen || _pendingReload)
        {
            _pendingReload = false;
            AddProviderButton.Content = HubStrings.Get("AddProvider");
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// Applies the edit draft. Built-in name/base URL are pinned by the manifest and already read-only. The
    /// blank key argument is a deliberate no-op: authentication is owned by the auth dialog, and this form
    /// must never touch the stored credential.
    /// </summary>
    private void CommitEditDraft(ModelProvider provider, TextBox name, TextBox baseUrl)
    {
        if (_chat is null) return;

        // Keep the existing default-model name; this page no longer edits it.
        if (_chat.UpdateProvider(provider.Id, name.Text ?? "", baseUrl.Text ?? "", provider.Model, ""))
        {
            SetProviderStatus(false, HubStrings.Get("ProviderEditSaved"));
        }
        else
        {
            SetProviderStatus(true, _chat.CanStoreSecrets
                ? HubStrings.Get("ProviderBaseUrlInvalid")
                : HubStrings.Get("ApiKeyPlatformUnsupported"));
        }
        CloseDraft();
    }

    private void SetProviderEnabled(string providerId, bool enabled)
    {
        if (_chat?.SetProviderEnabled(providerId, enabled) == true)
        {
            RebuildProviderGroups();
        }
    }


    /// <summary>
    /// The configured model sub-list. Always rendered: list membership is the activation set, and a provider
    /// that is not yet authenticated must still be able to hold local model names. Catalog fetch stays gated
    /// on a credential; manual add does not.
    ///
    /// <para>The header states only the model count. Which model a conversation runs is not this page's job.</para>
    /// </summary>
    private Control BuildModelSection(ModelProvider provider)
    {
        // Tagged so the self-check can find this section by role instead of by position: an index-based walk
        // silently reads the wrong node the moment the layout gains a row, and then reports a failure that
        // has nothing to do with what broke.
        var section = new StackPanel { Spacing = 6, Tag = SectionTags.Models };

        var expanded = provider.Models.Count <= 10;
        var listContent = new StackPanel { Spacing = 6, IsVisible = expanded, Tag = SectionTags.ModelList };
        var toggleContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var disclosure = new Avalonia.Controls.Shapes.Path
        {
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M6 9L12 15L18 9"),
            Stroke = BrushOrNull("Hub.TextSecondary"),
            StrokeThickness = 1.8,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(expanded ? 180 : 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        disclosure.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        toggleContent.Children.Add(disclosure);

        var toggleText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        toggleText.Children.Add(new TextBlock
        {
            Text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ModelsCount"),
                provider.Models.Count),
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
        });
        toggleContent.Children.Add(toggleText);

        // The dedicated collapse-header ControlTheme, not Fluent's default: Fluent paints a :checked
        // ToggleButton with a full accent slab from inside its own template, which no style setter or local
        // Background value could remove (observed on the screenshot). Replacing the theme removes the slab
        // at the source and keeps the header a quiet text row with a hover surface. TryFindResource, not the
        // Resources indexer: the indexer does not walk merged dictionaries, so the key lives in the
        // HubControls include and the indexer returns null — a silent fall-back to the accent slab.
        object? collapseHeaderThemeRaw = null;
        Application.Current?.TryFindResource("HubCollapseHeader", null, out collapseHeaderThemeRaw);
        var collapseHeaderTheme = collapseHeaderThemeRaw as ControlTheme;
        var toggle = new ToggleButton
        {
            Content = toggleContent,
            Theme = collapseHeaderTheme as ControlTheme,
            IsChecked = expanded,
            Padding = new Thickness(4, 2),
            Margin = new Thickness(-4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = SectionTags.ToggleModelList,
        };
        ToolTip.SetTip(toggle, HubStrings.Get("ToggleModelList"));
        toggle.IsCheckedChanged += (_, _) =>
        {
            expanded = toggle.IsChecked == true;
            listContent.IsVisible = expanded;
            disclosure.RenderTransform = new RotateTransform(expanded ? 180 : 0);
        };

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(toggle);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var catalog = new Button
        {
            Content = BuildSearchIcon(),
            Classes = { "quiet" },
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = SectionTags.ModelCatalog,
        };
        ToolTip.SetTip(catalog, HubStrings.Get("OpenModelCatalog"));
        catalog.Click += async (_, _) => await OpenModelCatalogAsync(provider);
        actions.Children.Add(catalog);

        var add = new Button
        {
            Content = BuildAddIcon(),
            Classes = { "quiet" },
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = SectionTags.AddModelButton,
        };
        ToolTip.SetTip(add, HubStrings.Get("AddModel"));
        add.Click += async (_, _) => await AddModelAsync(provider);
        actions.Children.Add(add);

        // Rightmost: the one action that empties the list. Disabled rather than hidden when there is
        // nothing to remove, so the toolbar keeps a stable shape across empty and populated states.
        var removeAll = new Button
        {
            Content = BuildClearListIcon(),
            Classes = { "quiet" },
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = provider.Models.Count > 0,
            Tag = SectionTags.RemoveAllModels,
        };
        ToolTip.SetTip(removeAll, HubStrings.Get("ModelsRemoveAll"));
        removeAll.Click += async (_, _) => await RemoveAllModelsAsync(provider);
        actions.Children.Add(removeAll);

        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        section.Children.Add(heading);

        // The add/edit-model draft is always the first row of ITS provider's list — the flags are page-wide,
        // so without the id gate every group would render a copy of the same draft (the pencil's own group
        // only). The provider being edited is exactly what _editingProviderId tracks.
        var modelDraftOpen = _editingProviderId == provider.Id
            && (_addingModel || _editingModelName is not null);
        if (provider.Models.Count == 0 && !modelDraftOpen)
        {
            // Empty state is one quiet line. The toolbar already carries ＋ 手动添加 and 🔍 浏览目录 —
            // a second 「添加模型」 here is the same action twice. The only extra is the auth CTA when the
            // provider cannot fetch a catalog yet.
            var empty = new StackPanel { Spacing = 8, Tag = SectionTags.ModelsEmpty };
            empty.Children.Add(new TextBlock
            {
                Text = IsLinked(provider) || !provider.RequiresCredential
                    ? HubStrings.Get("NoEnabledModels")
                    : HubStrings.Get("ModelsAuthHint"),
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            if (provider.RequiresCredential && !IsLinked(provider) && provider.CanAuthenticate)
            {
                var auth = new Button
                {
                    Content = HubStrings.Get("AuthenticateAndFetchModels"),
                    Classes = { "primary" },
                    FontSize = 11,
                    Padding = new Thickness(10, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                auth.Click += async (_, _) => await AuthenticateProviderAsync(provider);
                empty.Children.Add(auth);
            }

            listContent.Children.Add(empty);
            section.Children.Add(listContent);
            return section;
        }

        // The add/edit-model draft is always the first row, above every configured model.
        if (modelDraftOpen)
        {
            listContent.Children.Add(BuildModelDraftRow(provider));
        }

        foreach (var model in provider.Models)
        {
            listContent.Children.Add(BuildModelRow(provider, model));
        }

        section.Children.Add(listContent);
        return section;
    }

    /// <summary>The add-model draft row: an inline input with confirm/cancel, always first in the list. When a
    /// model is being edited (context-window override), the name is pre-filled and read-only, and a second
    /// field carries the token count.</summary>
    private Control BuildModelDraftRow(ModelProvider provider)
    {
        var stack = new StackPanel { Spacing = 4, Tag = SectionTags.ModelDraft };

        var editing = _editingModelName is not null;
        var name = editing ? _editingModelName! : "";

        var input = new TextBox
        {
            Text = name,
            IsReadOnly = editing,
            PlaceholderText = HubStrings.Get("ModelNamePlaceholder"),
        };
        if (editing) input.Classes.Add("muted");
        // The input joins the tree only through the inline row below — adding it to the stack here as well
        // gave it a second parent and threw "already has a visual parent" mid-rebuild, which is what froze
        // the settings card on the first pencil press.

        // The token field is built here but appended after the row, so the stack reads name-row first and
        // the context-window field second (the docstring's "second field").
        Control? tokensField = null;
        TextBox? tokens = null;
        if (editing)
        {
            var (current, _, _) = _chat!.ContextWindowFor(provider.Id, name);
            tokens = new TextBox
            {
                Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            tokensField = BuildField(HubStrings.Get("ModelContextEditHint"), tokens);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        var cancel = new Button { Content = HubStrings.Get("Cancel"), Classes = { "quiet" } };
        cancel.Click += (_, _) => CloseDraft();
        var confirm = new Button
        {
            Content = HubStrings.Get(editing ? "Save" : "Add"),
            Classes = { "primary" },
            IsDefault = true,
        };
        confirm.Click += (_, _) =>
        {
            if (editing) CommitModelEditDraft(provider, name, tokens!);
            else CommitModelDraft(provider, input);
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(input, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(input);
        row.Children.Add(buttons);

        stack.Children.Add(row);
        if (tokensField is not null) stack.Children.Add(tokensField);
        return stack;
    }

    private void CommitModelDraft(ModelProvider provider, TextBox input)
    {
        if (_chat is null) return;

        var name = input.Text ?? "";
        if (name.Trim().Length == 0)
        {
            SetProviderStatus(true, HubStrings.Get("ModelNameRequired"));
            return;
        }

        if (_chat.AddModel(provider.Id, name) is null)
        {
            SetProviderStatus(true, HubStrings.Get("ModelDuplicated"));
            return;
        }

        SetProviderStatus(false, HubStrings.Get("ModelAdded"));
        CloseDraft();
    }

    /// <summary>Opens the context-window draft for a model (the pencil on a model row).</summary>
    private void OpenModelEditDraft(ModelProvider provider, ProviderModel model)
    {
        _editingProviderId = provider.Id;
        _editingModelName = model.Name;
        _addingModel = false;
        RebuildProviderGroups();
    }

    private void CommitModelEditDraft(ModelProvider provider, string name, TextBox tokens)
    {
        if (_chat is null) return;

        int? parsed = null;
        if (int.TryParse((tokens.Text ?? "").Trim(), out var value) && value > 0)
        {
            parsed = value;
        }

        _chat.SetModelContextTokens(provider.Id, name, parsed);
        SetProviderStatus(false, HubStrings.Get("ProviderEditSaved"));
        CloseDraft();
    }

    /// <summary>
    /// Whether the provider's model catalog may be fetched.
    ///
    /// <para>True for a provider that needs no credential or one that holds a credential. False for a provider
    /// that needs one and does not have it — the request would come back 401.</para>
    ///
    /// <para>Lives here rather than in <see cref="ChatWorkspace"/> because it is a <b>presentation</b>
    /// decision: the fetch itself is legal without a key (the endpoint decides), and a future caller that
    /// wants to try anyway should not have to fight a UI-layer guard.</para>
    /// </summary>
    private bool CanListModels(ModelProvider provider)
        => !provider.RequiresCredential || IsLinked(provider);

    /// <summary>
    /// The window this model is treated as having, stated once as a number. Where that number came from lives in
    /// the tooltip, not in a suffix: the same source tag already rides the chat's usage line, so repeating it
    /// here would say one thing twice on a page that is only explaining a capability.
    ///
    /// <para>There is deliberately <b>no</b> box on this row. The one it replaces parsed on <c>TextChanged</c>
    /// and wrote through <see cref="ChatWorkspace.SetModelContextTokens"/>, whose <c>Changed</c> event rebuilds
    /// every provider group — so the first keystroke tore down the very text box being typed in. A number that
    /// is wrong is corrected in <c>ai/providers.json</c>, which is what the tooltip now names; the model
    /// property and its precedence in <see cref="ContextBudget"/> are untouched, so that file remains a real
    /// path rather than a documented-looking one.</para>
    /// </summary>
    private Control BuildModelContextRow(ModelProvider provider, ProviderModel model)
    {
        var (tokens, _, source) = _chat!.ContextWindowFor(provider.Id, model.Name);
        var line = new TextBlock
        {
            Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center,
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ModelContextFormat"), tokens.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)),
            Tag = SectionTags.ModelContext,
        };
        ToolTip.SetTip(line, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ModelContextSourceHint"), HubStrings.Get(ContextBudget.SourceLabelKey(source))));
        return line;
    }

    private Control BuildModelRow(ModelProvider provider, ProviderModel model)
    {
        // List membership is the activation set: every row here is selectable in a conversation. No enable
        // toggle and no "in use" marker — those answered a question this page does not ask.
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = model.Name,
            VerticalAlignment = VerticalAlignment.Center,
        });
        text.Children.Add(BuildModelContextRow(provider, model));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "reveal" },
        };

        // The pencil opens a context-window draft for this model. The one field is MaxContextTokens — the
        // zero-cost override the model already persists — so "edit model" is real without growing the model's
        // schema. Display name / wire override / output cap stay out until a later pass.
        var edit = new Button
        {
            Content = BuildEditIcon(),
            Classes = { "quiet" },
            Padding = new Thickness(6, 2),
        };
        ToolTip.SetTip(edit, HubStrings.Get("ModelEditTitle"));
        edit.Click += (_, _) => OpenModelEditDraft(provider, model);
        actions.Children.Add(edit);

        var remove = new Button
        {
            Content = BuildRemoveIcon(),
            Classes = { "quiet" },
            Padding = new Thickness(6, 2),
        };
        ToolTip.SetTip(remove, HubStrings.Get("ModelRemove"));
        remove.Click += async (_, _) => await RemoveModelAsync(provider, model);
        actions.Children.Add(remove);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(text);
        grid.Children.Add(actions);

        // Outlined card so the model list reads as a list; the fill is Hub.Surface (bound, not a captured
        // null brush). Hover only lifts the fill.
        var row = new Border
        {
            Padding = new Thickness(12, 8),
            Tag = SectionTags.ModelRow,
            Classes = { "model-row", "provider-row" },
            Child = grid,
        };
        return row;
    }

    private Control BuildAffiliateSection(ModelProvider provider)
    {
        var stack = new StackPanel { Spacing = 4 };
        var disclosure = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("AffiliateDisclosure"),
            provider.Name);
        _affiliateTextForCheck = disclosure;
        stack.Children.Add(new TextBlock
        {
            Text = disclosure,
            Classes = { "muted" },
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var referral = new Button
        {
            Content = HubStrings.Get("ViewReferral"),
            Classes = { "quiet" },
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 3),
            FontSize = 11,
        };
        referral.Click += (_, _) => OpenReferral(provider);
        stack.Children.Add(referral);

        var box = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Tag = SectionTags.Affiliate,
            Child = stack,
        };
        // Same reason as the model rows: a static BrushOrNull can capture null before attach.
        box.Bind(Border.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.Surface"));
        return box;
    }

    /// <summary>A small rounded label ("内置", "使用中"), used instead of colour alone to carry meaning.</summary>
    private static Control Pill(string text, IBrush? brush, IBrush? foreground = null)
        => new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = foreground,
            },
        };

    /// <summary>
    /// The remove affordance, drawn rather than typed: a trash outline on a 12×12 canvas. Generated instead of
    /// pulling in an icon font, so the button has no asset dependency and scales cleanly with the theme
    /// foreground.
    /// </summary>
    /// <summary>
    /// The remove affordance, drawn rather than typed: a trash outline on a 12×12 canvas. Generated instead of
    /// pulling in an icon font, so the button has no asset dependency.
    ///
    /// <para>The stroke is taken from the same theme brush the muted text uses: Avalonia's <c>Shape.Stroke</c>
    /// does <b>not</b> inherit <c>Foreground</c> (unlike a <c>Path</c> inside a control template), so leaving
    /// it unset renders nothing at all — a silent blank button, which is exactly the kind of failure this
    /// project's self-checks exist to catch.</para>
    /// </summary>
    private Control BuildRemoveIcon()
    {
        // A trash outline drawn as a single Path geometry. Generated instead of pulling in an icon font or an
        // asset, so the button has no external dependency.
        //
        // The stroke is bound with DynamicResource rather than read once through BrushOrNull: these rows are
        // built inside RebuildProviderGroups, which Reload calls while the page may not yet be attached to a
        // resource host — a one-off lookup returns null there and the icon renders as an invisible shape
        // inside a visible button. A binding re-resolves once the control is attached, and again on a theme
        // switch, which is what the rest of the page relies on.
        var path = new Avalonia.Controls.Shapes.Path
        {
            // Lid, handle, body, two slots.
            Data = Avalonia.Media.Geometry.Parse(
                "M1.5,2.5 H10.5 M4.5,1.2 H7.5 M2.5,2.5 H9.5 V11 H2.5 Z M5,4.8 V8.8 M7,4.8 V8.8"),
            StrokeThickness = 1.2,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));

        return new Viewbox { Width = 12, Height = 12, Child = path };
    }

    /// <summary>
    /// The edit affordance: a pencil outline. Same construction as the other icons — generated geometry, stroke
    /// bound through DynamicResource so it re-resolves on theme/language switch rather than going blank.
    /// </summary>
    private Control BuildEditIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(
                "M10.5,2.2 L11.8,3.5 L4.6,10.7 L2.9,11.1 L3.3,9.4 Z M9.4,3.3 L10.7,4.6"),
            StrokeThickness = 1.2,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        return new Viewbox { Width = 12, Height = 12, Child = path };
    }

    private Control BuildSearchIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(
                "M8,4.75 C8,6.55 6.55,8 4.75,8 C2.95,8 1.5,6.55 1.5,4.75 C1.5,2.95 2.95,1.5 4.75,1.5 C6.55,1.5 8,2.95 8,4.75 M7.25,7.25 L11,11"),
            StrokeThickness = 1.5,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        return new Viewbox { Width = 14, Height = 14, Child = path };
    }

    private Control BuildAddIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M6,1.5 V10.5 M1.5,6 H10.5"),
            StrokeThickness = 1.7,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        return new Viewbox { Width = 14, Height = 14, Child = path };
    }

    /// <summary>
    /// The remove-all icon: a list (three short strokes) crossed by a small ×. A plain trash would read as
    /// "remove this row" — the same glyph the per-row button already uses — so the list-plus-cross shape
    /// carries the "the whole list" meaning. Same construction rules as the other icons: generated geometry,
    /// stroke bound through DynamicResource.
    /// </summary>
    private Control BuildClearListIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(
                "M1.5,2.5 H6.5 M1.5,6 H6.5 M1.5,9.5 H6.5 M8,4 L11.5,7.5 M11.5,4 L8,7.5"),
            StrokeThickness = 1.4,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextSecondary"));
        return new Viewbox { Width = 14, Height = 14, Child = path };
    }

    private static Border BuildProviderHeaderDivider()
    {
        var divider = new Border
        {
            Height = 1,
            Tag = SectionTags.ProviderHeaderDivider,
        };
        divider.Bind(Border.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.BorderSubtle"));
        return divider;
    }

    /// <summary>
    /// The linked state mark: a tick in the success green. Same construction rules as the remove icon — a
    /// <c>Path</c> whose stroke is bound, because an unset <c>Shape.Stroke</c> draws nothing and the failure is
    /// silent.
    /// </summary>
    private Control BuildCheckIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M1.5,6.5 L4.6,9.6 L11,2.6"),
            StrokeThickness = 1.8,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.Success"));
        return new Viewbox { Width = 12, Height = 12, Child = path };
    }

    /// <summary>
    /// The not-linked mark: a broken link (a gap between two short strokes) in the tertiary ink.
    ///
    /// <para>Deliberately a different <i>shape</i>, not just a different colour: a grey tick would read as
    /// "disabled but connected", which is the opposite of what the state says.</para>
    /// </summary>
    private Control BuildUnlinkedIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M1.5,6 H4.6 M7.4,6 H10.5 M6.1,4.4 L5.9,7.6"),
            StrokeThickness = 1.6,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Hub.TextTertiary"));
        return new Viewbox { Width = 12, Height = 12, Child = path };
    }

    /// <summary>
    /// Looks a theme brush up by key, returning <c>null</c> when it is absent.
    ///
    /// <c>FindResource</c> returns <c>Avalonia.UnsetValueType</c> for a missing key, and a direct cast to
    /// <see cref="IBrush"/> then throws <c>InvalidCastException</c> at runtime — with no compiler warning,
    /// because the cast is legal for the declared type. A missing key would otherwise take down the whole
    /// settings page the first time a row was drawn.
    /// </summary>
    private IBrush? BrushOrNull(string key)
        => this.TryFindResource(key, out var value) ? value as IBrush : null;

    private void SetModelEnabled(string providerId, string modelName, bool enabled)
    {
        if (_chat?.SetModelEnabled(providerId, modelName, enabled) == true)
        {
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// Fetches the provider's model list and repaints.
    ///
    /// <para>A fetch that fails reports the reason and <b>leaves the existing list alone</b>, so the section
    /// does not collapse to empty on a network blip. A fetch that succeeds with zero models says so
    /// explicitly rather than letting the empty state speak for itself.</para>
    /// </summary>
    private async Task<ModelFetchResult> RefreshModelsAsync(ModelProvider provider)
    {
        if (_chat is null) return ModelFetchResult.Unreachable("The chat workspace is unavailable.");

        SetProviderStatus(false, HubStrings.Get("ModelsRefreshing"));

        try
        {
            var result = await _chat.RefreshModelsAsync(provider.Id);
            if (!result.Reachable)
            {
                SetProviderStatus(true, HubStrings.Get("ModelsFetchFailed") + " " + result.Problem);
                return result;
            }

            SetProviderStatus(false, result.Models.Count == 0
                ? HubStrings.Get("ModelsFetchedEmpty")
                : string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("ModelsFetched"),
                    result.Models.Count));
            return result;
        }
        finally
        {
            RebuildProviderGroups();
        }
    }

    private async Task OpenModelCatalogAsync(ModelProvider provider)
    {
        if (_chat is null) return;
        await CreateModelCatalogWindow(provider).ShowAsync(Owner());
        RebuildProviderGroups();
    }

    private ModelCatalogWindow CreateModelCatalogWindow(ModelProvider provider)
        => ModelCatalogWindow.Create(
            provider,
            () => _chat.CachedModels(provider.Id),
            name => provider.Models.Any(model =>
                string.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase) && model.Enabled),
            name => _chat.EnableCatalogModel(provider.Id, name),
            () => RefreshModelsAsync(provider),
            CanListModels(provider));

    private async Task AddModelAsync(ModelProvider provider)
    {
        if (_chat is null) return;

        // The draft row replaces the old PromptWindow modal: it stays where the list is, and it is always the
        // first row so the input never scrolls out from under the person who just clicked "add".
        _editingProviderId = provider.Id;
        _addingModel = true;
        RebuildProviderGroups();
        await Task.CompletedTask;
    }

    private async Task RemoveModelAsync(ModelProvider provider, ProviderModel model)
    {
        if (_chat is null) return;

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("ModelRemove"),
                model.Name + "\n\n" + HubStrings.Get("ModelRemoveConfirm"),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        if (_chat.RemoveModel(provider.Id, model.Name))
        {
            SetProviderStatus(false, HubStrings.Get("ModelRemoved"));
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// Empties one provider's configured-model list. Same confirm-first shape as the single-row removal:
    /// the click is one icon away from "add", and a misclick that costs the whole list is not recoverable
    /// by one more click.
    /// </summary>
    private async Task RemoveAllModelsAsync(ModelProvider provider)
    {
        if (_chat is null || provider.Models.Count == 0) return;

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("ModelsRemoveAll"),
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("ModelsRemoveAllConfirm"),
                    provider.Models.Count),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        var removed = _chat.RemoveAllModels(provider.Id);
        if (removed > 0)
        {
            SetProviderStatus(false, string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                HubStrings.Get("ModelsRemovedAll"),
                removed));
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// The browser sign-in for one provider group. Everything that can go wrong here is reported through the
    /// status line rather than a dialog: the flow is async and the user is looking at another window, so a
    /// modal box would be waiting in the wrong place.
    ///
    /// <para><b>There is deliberately no "paste the callback URL" dialog.</b> The redirect targets this machine's
    /// loopback port, so any browser on it finishes the sign-in without the user typing anything; where no browser
    /// could be launched the status line hands over the link instead.</para>
    ///
    /// <para>The button is passed in rather than looked up: with several groups on screen there is no single
    /// "the sign-in button", and disabling the wrong one would leave the clicked button live while a second
    /// flow started underneath.</para>
    /// </summary>
    /// <param name="button">
    /// The button that started the flow, disabled while the browser is open. <c>null</c> when the flow was
    /// started from the auth dialog instead of from a row: there is no row button to disable, and the dialog
    /// has already closed by the time the flow runs.
    /// </param>
    private async Task SignInAsync(ModelProvider provider, Button? button)
    {
        if (_chat is null || !provider.SupportsOAuth) return;

        SetProviderStatus(false, HubStrings.Get("AuthOAuthPending"));
        if (button is not null) button.IsEnabled = false;

        // A device-code sign-in needs a surface for the code and a way to be cancelled; the loopback flow needs
        // neither, because its redirect comes back to this machine by itself. The window is what the person
        // cancels *by* — closing it stops the polling, so there is no Cancel button repeating that.
        CancellationTokenSource? deviceCancellation = null;
        DeviceAuthWindow? device = null;
        IProgress<AxmolHub.Agent.DeviceCodeChallenge>? deviceProgress = null;
        if (provider.OAuth?.EffectiveFlow == ProviderOAuthFlows.DeviceCode)
        {
            deviceCancellation = new CancellationTokenSource();
            // Progress<T> posts to the synchronization context it was created on — the UI thread here. The flow
            // reports the challenge from a worker, and a window must not be built or shown from one.
            deviceProgress = new Progress<AxmolHub.Agent.DeviceCodeChallenge>(challenge =>
            {
                device = new DeviceAuthWindow(challenge);
                DeviceWindowForCheck = device;
                // Closing the window IS the cancel, which is why the subscription lives here and not inside the
                // window: the flow is already awaiting by the time a window exists, so only the caller holds a
                // token it can hand to it. Without this line the polling would keep running against a window the
                // person already threw away, and the sign-in would land after they said no.
                device.Closed += (_, _) =>
                {
                    // A close that arrives after the run has already finished lands on a source the finally block
                    // disposed. That is not a failure to report — the sign-in being over is exactly what the close
                    // wanted — so the no-op is written out instead of being left to throw inside an event handler,
                    // where there is nobody to see it.
                    try { deviceCancellation?.Cancel(); }
                    catch (ObjectDisposedException) { }
                };
                // Same tolerance AuthDialog shows: a design-time host has no top-level window to own this one,
                // and the window still opens standalone rather than swallowing the sign-in. A check host keeps it
                // off screen for the reason every detached window in this app exists: the assertions read the
                // object graph, and a second live top-level window would be measuring the harness instead.
                if (!_deviceWindowStaysOffScreen) device.Show(Owner()!);
            });
        }

        try
        {
            var result = await _chat.SignInWithOAuthAsync(provider.Id, url =>
            {
                // The manual URL has nowhere to live in a group (it would have to be rebuilt into the row that
                // is about to be rebuilt anyway), so it goes to the status line, which is already the channel
                // for every other outcome of this flow.
                SetProviderStatus(false, HubStrings.Get("AuthOAuthNoBrowser") + "\n" + url);
            }, deviceCancellation?.Token ?? default, deviceProgress);

            if (result is null)
            {
                SetProviderStatus(true, HubStrings.Get("AuthOAuthPlatformUnsupported"));
                return;
            }

            if (result.Cancelled)
            {
                SetProviderStatus(false, HubStrings.Get("AuthOAuthCancelled"));
                return;
            }

            if (result.ScopeRejected is { } granted)
            {
                SetProviderStatus(true, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    HubStrings.Get("AuthOAuthScopeRejected"),
                    granted));
                return;
            }

            if (result.Error is { Length: > 0 } error)
            {
                SetProviderStatus(true, HubStrings.Get("AuthOAuthFailed") + error);
                return;
            }

            SetProviderStatus(false, HubStrings.Get("AuthOAuthSucceeded"));

            _chat?.SetPreferredAuthMethod(provider.Id, ProviderAuthMethods.OAuth);

            // The credential is stored the moment the flow lands, so the linked mark repaints now — the
            // fetch below can take seconds and must not hold the state badge hostage.
            RebuildProviderGroups();

            // Same reasoning as the pasted-key path: a sign-in that leaves an empty model list is only half
            // finished, and the list is reachable now precisely because the browser just minted the key.
            await RefreshModelsAsync(provider);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
            // The outcome belongs to the settings status line — the same channel every other sign-in result
            // uses — so the code window has nothing left to say and goes away. IsVisible rather than a
            // try/catch: when the person closed it themselves (which is how they cancel), closing it again is
            // an error for a state that is already the one we wanted. An off-screen check window was never
            // opened, so there is nothing to close — and Window.Close on a window with no platform side behind
            // it is not a thing to discover at assertion time.
            if (!_deviceWindowStaysOffScreen && device is { IsVisible: true }) device.Close();
            deviceCancellation?.Dispose();
        }
    }

    /// <summary>
    /// The "Add provider" button. It opens the **preset picker** (a searchable list of known providers), not the
    /// empty form — the form is reachable only through the picker's "Custom endpoint…" row. That ordering is the
    /// whole point: a user who has never configured a provider gets a list they can read through, and the user
    /// with a private gateway still gets their form, one extra click in.
    /// </summary>
    private async Task AddProviderAsync()
    {
        var picker = await ProviderPickerWindow.ShowAsync(Owner(), _chat.AvailablePresets());
        if (picker is null) return;

        if (picker.WantsCustom)
        {
            await AddCustomProviderAsync();
            return;
        }

        if (picker.SelectedId is not { } presetId || _chat.AddPreset(presetId) is null)
        {
            SetProviderStatus(true, HubStrings.Get("ProviderAlreadyAdded"));
            return;
        }

        SetProviderStatus(false, "");
        RebuildProviderGroups();

        // Adding a provider and connecting it are one job. Dual-method presets choose the method inside the
        // auth dialog; a keyless preset has nothing to do. On success the catalog is fetched and the
        // manifest defaults are enabled — otherwise the card lands empty and the user has to remember a
        // second step.
        var provider = _chat.Providers.FirstOrDefault(candidate => candidate.Id == presetId);
        if (provider is { CanAuthenticate: true })
        {
            await AuthenticateProviderAsync(provider);
        }
    }

    /// <summary>The free-form path, shared by the picker's custom row. Instead of a modal dialog it inserts an
    /// "add custom endpoint" draft at the top of the provider list; the draft is always first, so the person
    /// never has to scroll to find the form they just opened.</summary>
    private async Task AddCustomProviderAsync()
    {
        _addingProvider = true;
        _editingProviderId = null;
        _addingModel = false;
        RebuildProviderGroups();
        await Task.CompletedTask;
    }

    private async Task RemoveProviderAsync(ModelProvider provider)
    {
        // The header only offers this for a provider that may be removed, so this is a guard rather than the
        // rule; the rule itself lives in ChatWorkspace so the two cannot drift.
        if (!ChatWorkspace.CanRemoveProvider(provider)) return;

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("RemoveProvider"),
                provider.Name + "\n\n" + HubStrings.Get("RemoveProviderConfirm"),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        if (_chat.RemoveProvider(provider.Id))
        {
            SetProviderStatus(false, HubStrings.Get("ProviderRemoved"));
            RebuildProviderGroups();
        }
    }

    /// <summary>
    /// Cuts a provider's link: its accounts and their secrets go, the provider stays in the list. Confirmed
    /// first — it destroys secrets that cannot be recovered, and the provider looking unchanged afterwards
    /// would otherwise make the action easy to trigger by accident and hard to notice.
    /// </summary>
    private async Task DisconnectProviderAsync(ModelProvider provider)
    {
        if (_chat is null) return;

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("DisconnectProvider"),
                provider.Name + "\n\n" + HubStrings.Get("DisconnectProviderConfirm"),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        SetProviderStatus(false, _chat.DisconnectProvider(provider.Id)
            ? HubStrings.Get("ProviderDisconnected")
            : HubStrings.Get("ProviderNothingToDisconnect"));
        RebuildProviderGroups();
    }

    /// <summary>
    /// Opens the authentication dialog and applies whatever it produces.
    ///
    /// <para>When the provider has already bound a method (preferred, or a credential source), the dialog
    /// skips its choice step and opens that method's surface directly — one provider never shows both.</para>
    ///
    /// <para>The two routes end differently on purpose. A pasted key is stored here and now. Browser sign-in
    /// closes this dialog immediately and hands off to <see cref="SignInAsync"/>, which owns the browser, the
    /// callback and the failure messages — the dialog's part was only to ask which route, and pretending
    /// otherwise would put two owners on one flow.</para>
    /// </summary>
    private async Task AuthenticateProviderAsync(ModelProvider provider, bool forceChooseMethod = false)
    {
        if (_chat is null) return;
        if (Owner() is not { } owner) return;

        if (!_chat.CanStoreSecrets)
        {
            SetProviderStatus(true, HubStrings.Get("AuthSecretsUnsupported"));
            return;
        }

        // Bound method: prefer an explicit choice, else the live credential's source, else the sole declared
        // method. Dual-method with no binding falls through to the dialog's choice step.
        var bound = forceChooseMethod
            ? null
            : BoundAuthMethod(provider);

        var outcome = await AuthDialog.ShowAsync(
            owner,
            provider,
            initialMethod: bound,
            checkKey: key => _chat.CheckApiKeyAsync(provider.Id, key));

        // Null is the user closing the dialog without choosing. Anything else is one of the two routes, and
        // the dialog says which — so there is no guessing from "was a key returned".
        if (outcome is null)
        {
            SetProviderStatus(false, HubStrings.Get("AuthDialogCancelled"));
            return;
        }

        if (outcome.Route == AuthRoute.Login)
        {
            // Hand off to the flow that already owns the browser and the callback. It reports on the status
            // line and rebuilds the groups itself when it lands.
            await SignInAsync(provider, null);
            return;
        }

        var stored = _chat.AddCredential(provider.Id, "", outcome.ApiKey, CredentialSources.ApiKey);
        if (stored is null)
        {
            SetProviderStatus(true, HubStrings.Get("AuthSecretsUnsupported"));
            return;
        }

        _chat.SetPreferredAuthMethod(provider.Id, ProviderAuthMethods.ApiKey);

        // The key is already stored, so "linked" is true now: repaint the mark before the fetch rather than
        // after it — the list can take seconds to arrive and the state must not wait on it.
        RebuildProviderGroups();

        // The model list needs the key, and the user has just handed one over — so it is fetched here rather
        // than left as the next thing to remember. It is the same call the refresh button makes; doing it
        // automatically is what turns "authenticated" into "ready to use" without a second visit.
        await RefreshModelsAsync(provider);
    }

    /// <summary>The method this provider is bound to for sign-in, or null when the user has not chosen yet.</summary>
    private static string? BoundAuthMethod(ModelProvider provider)
    {
        if (provider.Credential?.Source == CredentialSources.OAuth) return ProviderAuthMethods.OAuth;
        if (provider.Credential?.Source == CredentialSources.ApiKey) return ProviderAuthMethods.ApiKey;
        if (provider.PreferredAuthMethod is { Length: > 0 } preferred) return preferred;
        var methods = provider.EffectiveAuthMethods;
        return methods.Count == 1 ? methods[0] : null;
    }

    /// <summary>
    /// Clears the current credential and re-enters method choice. The entry is secondary (hover-revealed):
    /// switching methods is rare, and the button that does it must not sit next to 鉴权 looking equally
    /// primary.
    /// </summary>
    private async Task ChangeAuthMethodAsync(ModelProvider provider)
    {
        if (_chat is null) return;

        if (Owner() is { } owner)
        {
            var confirmed = await HubDialog.ShowAsync(
                owner,
                HubStrings.Get("ChangeAuthMethod"),
                provider.Name + "\n\n" + HubStrings.Get("ChangeAuthMethodConfirm"),
                HubDialogButtons.OkCancel);
            if (confirmed != HubDialogResult.Ok) return;
        }

        _chat.DisconnectProvider(provider.Id);
        _chat.SetPreferredAuthMethod(provider.Id, null);
        RebuildProviderGroups();
        await AuthenticateProviderAsync(provider, forceChooseMethod: true);
    }

    /// <summary>The top-level window, used as the dialog owner. <c>null</c> in a design-time host, in which
    /// case the dialog still opens (it degrades to a standalone window).</summary>
    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;

    /// <summary>
    /// Opens the affiliate/referral page. This is the same entrance the sign-in flow uses
    /// (<see cref="UrlLauncher"/>), because the disclosure link and the OAuth link are one job — telling the user
    /// where their key came from — and two launchers means one of them silently stops working on a platform the
    /// other added. A <c>directory</c> is deliberately not routed here: shell-opening a folder has to skip
    /// <c>$BROWSER</c>, and <c>MainWindow.OpenFolder</c> keeps its own path for that reason.
    /// </summary>
    private void OpenReferral(ModelProvider provider)
    {
        if (provider.ReferralUrl is not { Length: > 0 } url) return;
        if (UrlLauncher.TryOpen(url)) return;

        // No browser: report rather than throw, and hand over the link itself, which is what the sign-in
        // fallback does too. On a headless box the URL is still actionable — copy it and open it elsewhere.
        SetProviderStatus(true, HubStrings.Get("AuthNoBrowserLink") + url);
    }

    // ── Verification hooks for the update card (used by --verify-shell) ──

    /// <summary>The update card's status line **as rendered**. Read back rather than recomputed: what the
    /// user sees is this control's text, and the check result is only one of the strings it can hold.</summary>
    internal string UpdateStatusText => UpdateStatusLine.Text ?? "";

    /// <summary>Progress bars this page can still draw. The update card is meant to hold none: its download
    /// moved to the strip's queue, and a bar left here would be a second surface reporting one transfer —
    /// the shape of the bug this round was about.</summary>
    internal int ProgressBarCount => this.GetVisualDescendants().OfType<ProgressBar>().Count();

    // ── Verification hooks for the provider card (used by --verify-shell) ──

    /// <summary>Number of configured providers.</summary>
    internal int ProviderCount => _chat.Providers.Count;

    /// <summary>Number of built-in presets the "add provider" dialog would offer for this workspace — the
    /// catalog minus whatever is already configured.</summary>
    internal int AvailablePresetCount => _chat.AvailablePresets().Count;

    /// <summary>Runs the real "Add provider" button handler, so the self-check drives the same path a click
    /// does (the picker dialog is modal and cannot be clicked from the harness).</summary>
    internal Task ClickAddProviderForCheckAsync() => AddProviderAsync();

    /// <summary>Whether the "add provider" affordance is rendered.</summary>
    internal bool ProviderButtonsVisible => AddProviderButton.IsVisible;

    /// <summary>The provider card's status line text — provider operations and the sign-in flow report every
    /// outcome through it.</summary>
    internal string StatusLineText => ProviderStatusLine.Text ?? "";

    /// <summary>Whether the provider status line is on screen. It is hidden while empty, so a failed operation
    /// that wrote nothing would otherwise leave the page looking untouched.</summary>
    internal bool StatusLineVisible => ProviderStatusLine.IsVisible;

    // ── Verification hooks for the provider group list ──
    //
    // The groups are built in code, so the assertions read the rendered visual tree rather than the model:
    // what matters is that a group for the right provider exists and that its parts are actually on screen.
    // A missing group would otherwise pass a "provider exists in the model" check while the page showed
    // nothing at all.

    /// <summary>One rendered provider group, with the parts the self-check inspects.</summary>
    internal sealed record ProviderGroupView(
        string Name,
        bool HasRemoveButton,
        bool ShowsNoCredentialNote,
        bool HasAffiliateBox,
        bool IsLinked,
        string LinkedStatusText,
        bool StatusLabelIsPainted,
        bool StatusMarkIsPainted,
        /// <summary>The label on the single auth control; it is the state, so it is asserted, not just counted.</summary>
        string AuthButtonText,
        /// <summary>No credential input is rendered anywhere in the group. The whole point of the revision.</summary>
        bool HasCredentialField,
        string SummaryText,
        string[] ModelNames,
        bool[] ModelEnabled,
        string ActiveModelName,
        bool HasModelListToggle,
        bool ModelListExpanded,
        bool HasUseModelButton,
        bool HasModelCatalogButton,
        bool HasAddModelButton,
        bool ModelCatalogPrecedesAdd,
        bool HasRemoveAllModelsButton,
        /// <summary>Remove-all sits rightmost, after add: presence and order are different failures.</summary>
        bool RemoveAllFollowsAdd,
        bool HasProviderOutline,
        bool HasProviderSurface,
        bool HasModelRowsOutline,
        /// <summary>
        /// Every model row paints the settings-card surface, not the provider card's. A statically captured
        /// brush (<c>BrushOrNull</c> before attach) left the rows transparent once, and they silently took
        /// the card's darker colour — the rows must be bound, and this is what pins that.
        /// </summary>
        bool HasModelRowSurface,
        bool HasProviderHeaderDivider,
        /// <summary>
        /// Whether the model section is rendered at all. False for a provider that needs a credential and has
        /// none: the list is fetched with the user's key, so there is nothing to show and asking would be a
        /// request the user never authorized.
        /// </summary>
        bool ShowsModelSection,
        /// <summary>The section is present but the endpoint listed nothing (or has not been asked yet).</summary>
        bool ShowsModelEmptyState,
        /// <summary>True only if the legacy provider-list refresh control is present; it should stay false.</summary>
        bool HasRefreshButton,
        /// <summary>Legacy rule count; outlined model cards intentionally keep this at zero.</summary>
        int ModelDividerCount);

    /// <summary>The provider groups currently rendered, in display order.</summary>
    internal IReadOnlyList<ProviderGroupView> ProviderGroups
        => GroupCards.Select(ReadGroup).ToArray();

    /// <summary>Number of provider groups rendered — the count the page actually shows, not the model's.</summary>
    internal int ProviderGroupCount => GroupCards.Count();

    internal bool HasOnlyProviderCards
        => _chat is not null
           && ProviderGroupList.Children.Count == _chat.Providers.Count
           && GroupCards.All(card => _chat.Providers.Any(provider => provider.Id == card.Tag as string));

    /// <summary>The provider cards currently rendered.</summary>
    private IEnumerable<Border> GroupCards
        => ProviderGroupList.Children.OfType<Border>();

    /// <summary>
    /// Reads one group card.
    ///
    /// <para>An instance method so the card can be checked against the provider model while reading the rendered
    /// visual tree.</para>
    /// </summary>
    private ProviderGroupView ReadGroup(Border group)
    {
        var content = (StackPanel)group.Child!;

        // The header is a two-column Grid: name pills on the left, the status mark and available actions on the
        // right. Both columns are StackPanels, so the two are told apart by column rather than by shape — a
        // positional read would swap them the moment the title gained a pill.
        var header = (Grid)content.Children[0];
        var title = (StackPanel)header.Children[0];
        var name = title.Children.OfType<TextBlock>().First().Text ?? "";

        // The actions column packs [status stack] + [auth, catalog and remove actions as applicable]. The mark's
        // label is the source of truth for "linked": matching on the drawn tick would make the assertion pass
        // on any provider that happens to render a check-shaped Path, and the label is what the user reads.
        var actions = (StackPanel)header.Children[1];
        var statusStack = actions.Children.OfType<StackPanel>().FirstOrDefault();
        var statusLabel = statusStack?.Children.OfType<TextBlock>().FirstOrDefault();
        var statusText = statusLabel?.Text ?? "";
        var isLinked = statusText == HubStrings.Get("ProviderConnected");
        // Tags distinguish icon actions even though their content is a generated control rather than text.
        var hasRemove = actions.Children.OfType<Button>()
            .Concat(actions.GetLogicalDescendants().OfType<Button>())
            .Any(button => button.Tag as string == SectionTags.RemoveProvider);
        var hasHeaderCatalogButton = actions.Children.OfType<Button>()
            .Concat(actions.GetLogicalDescendants().OfType<Button>())
            .Any(button => button.Tag as string == SectionTags.ModelCatalog);

        // "The text is in the tree" is not "the user can see it". A brush that resolved to null while the page
        // was detached leaves a label with the right string and nothing to draw it in, and every check that
        // reads .Text still passes — which is exactly how the first version of this mark shipped a blank
        // status. So the assertion looks at the resolved paint, not at the string.
        var statusLabelIsPainted = statusLabel?.Foreground is not null;
        var statusMarkIsPainted = statusStack?.GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Shape>()
            .Any(shape => shape.Stroke is not null) ?? false;

        var sections = content.Children.OfType<StackPanel>()
            .Where(section => section.Tag is string)
            .ToArray();

        var modelSection = sections.FirstOrDefault(section => (string)section.Tag! == SectionTags.Models);
        var modelActions = modelSection?.Children.OfType<Grid>().FirstOrDefault()?.Children
            .OfType<StackPanel>().FirstOrDefault(stack => stack.Children.OfType<Button>().Any());
        var modelActionButtons = modelActions?.Children.OfType<Button>().ToArray() ?? [];
        var modelCatalogIndex = Array.FindIndex(modelActionButtons,
            button => button.Tag as string == SectionTags.ModelCatalog);
        var addModelIndex = Array.FindIndex(modelActionButtons,
            button => button.Tag as string == SectionTags.AddModelButton);
        var hasModelCatalogButton = modelCatalogIndex >= 0;
        var hasAddModelButton = addModelIndex >= 0;
        var modelCatalogPrecedesAdd = modelCatalogIndex >= 0 && addModelIndex > modelCatalogIndex;
        var removeAllIndex = Array.FindIndex(modelActionButtons,
            button => button.Tag as string == SectionTags.RemoveAllModels);
        var hasRemoveAllModelsButton = removeAllIndex >= 0;
        var removeAllFollowsAdd = addModelIndex >= 0 && removeAllIndex > addModelIndex;
        var hasProviderOutline = group.BorderThickness.Left > 0
                                 && group.BorderBrush is not null;
        var hasProviderSurface = ThemeProbe.IsToken(group.Background, "Hub.ProviderSurface");
        var hasProviderHeaderDivider = content.Children.OfType<Border>()
            .Any(border => border.Tag as string == SectionTags.ProviderHeaderDivider);

        // "Not rendered" is a *Border* placeholder rather than a missing section, so that "this group has no
        // model list" and "this group failed to build its model list" stay distinguishable — both would read as
        // an absent StackPanel otherwise.
        var modelsHidden = content.Children.OfType<Border>()
            .Any(border => border.Tag as string == SectionTags.ModelsHidden);

        var showsModelSection = modelSection is not null;

        // The empty-state line and model controls are looked up by tag rather than by position.
        var showsModelEmptyState = modelSection?.GetLogicalDescendants()
            .Any(node => (node as Control)?.Tag as string == SectionTags.ModelsEmpty) ?? false;
        var hasRefresh = modelSection?.GetLogicalDescendants()
            .OfType<Button>()
            .Any(button => button.Tag as string == SectionTags.RefreshModels) ?? false;
        var modelList = modelSection?.GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(list => list.Tag as string == SectionTags.ModelList);
        var hasModelListToggle = modelSection?.GetLogicalDescendants().OfType<ToggleButton>()
            .Any(button => button.Tag as string == SectionTags.ToggleModelList) ?? false;
        var hasUseModelButton = modelSection?.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Content?.ToString() == HubStrings.Get("ModelUse")) ?? false;

        // Each model row is [name (+ optional "in use" pill), optional description] in a text stack. The rows
        // live in a nested untagged StackPanel under the section's heading Grid.
        var modelRows = modelSection?.Children.OfType<Border>()
            .Where(row => row.Tag as string == SectionTags.ModelRow)
            .Concat(modelSection.Children.OfType<StackPanel>()
                .SelectMany(list => list.Children.OfType<Border>())
                .Where(row => row.Tag as string == SectionTags.ModelRow))
            .ToArray() ?? [];
        var hasModelRowsOutline = modelRows.Length > 0
                                  && modelRows.All(row => row.BorderThickness.Left > 0
                                                         && row.BorderBrush is not null);
        var hasModelRowSurface = modelRows.Length > 0
                                 && modelRows.All(row => ThemeProbe.IsToken(row.Background, "Hub.Surface"));

        var modelNames = new List<string>();
        var modelEnabled = new List<bool>();
        var activeModel = "";
        foreach (var row in modelRows)
        {
            var text = (StackPanel)((Grid)row.Child!).Children[0];
            // Name is the first TextBlock; there is no nameRow and no "in use" pill any more — every row in
            // the list is an active, selectable model.
            var modelName = text.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
            modelNames.Add(modelName);
            modelEnabled.Add(true);
        }

        // Model rows are individually outlined cards, so there should be no extra dividers between them.
        const int modelDividers = 0;

        // The single auth control, read by its tag. Its *label* is the state: the same button reads
        // "authenticate" or "disconnect", so a check that only counted buttons would pass for either.
        // It lives in the header actions stack (status + auth + reveal cluster).
        var authButton = actions.Children.OfType<Button>()
            .Concat(actions.GetLogicalDescendants().OfType<Button>())
            .FirstOrDefault(button => button.Tag as string == SectionTags.AuthButton);
        var authButtonText = authButton?.Content as string ?? "";

        // The whole point of this revision: no credential input exists anywhere in the list. A password-class
        // TextBox, or any TextBox with PasswordChar set, anywhere under the group is the regression this
        // check exists to catch — checked over descendants because a future layout could nest it deeper.
        var hasCredentialField = content.GetVisualDescendants().OfType<TextBox>()
            .Any(box => box.Classes.Contains("password") || box.PasswordChar != default(char));

        var summary = content.Children.OfType<TextBlock>()
            .FirstOrDefault(text => text.Tag as string == SectionTags.Summary)?.Text ?? "";
        var showsNoCredential = content.Children.OfType<TextBlock>()
            .Any(text => text.Tag as string == SectionTags.NoCredential);
        var hasAffiliate = content.Children.OfType<Border>()
            .Any(border => border.Tag as string == SectionTags.Affiliate);

        return new ProviderGroupView(
            name,
            hasRemove,
            showsNoCredential,
            hasAffiliate,
            isLinked,
            statusText,
            statusLabelIsPainted,
            statusMarkIsPainted,
            authButtonText,
            hasCredentialField,
            summary,
            modelNames.ToArray(),
            modelEnabled.ToArray(),
            activeModel,
            hasModelListToggle,
            modelList?.IsVisible == true,
            hasUseModelButton,
            hasModelCatalogButton && !hasHeaderCatalogButton,
            hasAddModelButton,
            modelCatalogPrecedesAdd,
            hasRemoveAllModelsButton,
            removeAllFollowsAdd,
            hasProviderOutline,
            hasProviderSurface,
            hasModelRowsOutline,
            hasModelRowSurface,
            hasProviderHeaderDivider,
            showsModelSection && !modelsHidden,
            showsModelEmptyState,
            hasRefresh,
            modelDividers);
    }

    /// <summary>Forces a repaint of the group list, so a self-check sees the tree a click would produce.</summary>
    internal void RefreshProviderGroupsForCheck()
    {
        RebuildProviderGroups();
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    internal bool SetModelListExpandedForCheck(string providerId, bool expanded)
    {
        var toggle = GroupCards
            .FirstOrDefault(group => group.Tag as string == providerId)?
            .GetLogicalDescendants().OfType<ToggleButton>()
            .FirstOrDefault(button => button.Tag as string == SectionTags.ToggleModelList);
        if (toggle is null) return false;

        toggle.IsChecked = expanded;
        UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return true;
    }

    internal bool SetModelEnabledForCheck(string providerId, string modelName, bool enabled)
    {
        var modelRow = GroupCards
            .Where(group => group.Tag as string == providerId)
            .SelectMany(group => group.GetVisualDescendants().OfType<Border>())
            .FirstOrDefault(row => row.Tag as string == SectionTags.ModelRow
                                   && row.GetVisualDescendants().OfType<TextBlock>()
                                       .Any(text => text.Text == modelName));
        var rowToggle = modelRow?.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
        if (rowToggle is null) return false;

        rowToggle.IsChecked = enabled;
        return true;
    }

    /// <summary>
    /// The first model row that rendered a context line: its text, the tooltip behind it, and whether any editor
    /// came with it. <c>HasEditor</c> is asserted <b>false</b> on purpose — this row used to carry a text box that
    /// rebuilt the page on every keystroke, and an assertion that only checked the box was present would have
    /// passed the day it was deleted and told nobody why it should never come back.
    ///
    /// <para>A generic "first row" rather than a named one because which models are configured is the user's data,
    /// and an assertion that depends on it would fail for reasons that have nothing to do with this feature.</para>
    /// </summary>
    internal (string Text, string Tip, bool HasEditor) FirstModelContextRowForCheck()
    {
        var text = GroupCards.SelectMany(group => group.GetVisualDescendants().OfType<TextBlock>())
            .FirstOrDefault(candidate => candidate.Tag as string == SectionTags.ModelContext);
        if (text is null) return ("", "", false);

        // Scoped to the row, not the page: a text box elsewhere on Settings is legitimate, one on this row is the
        // defect this line exists to keep out.
        var row = text.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(candidate => candidate.Tag as string == SectionTags.ModelRow);
        return (text.Text ?? "", ToolTip.GetTip(text) as string ?? "",
                row is not null && row.GetVisualDescendants().OfType<TextBox>().Any());
    }

    /// <summary>
    /// The group rendered for one provider id, or <c>null</c> when no group was rendered for it.
    ///
    /// Keyed on the id rather than the display name: a preset's name is copy ("Ollama (local)") and can change
    /// with the manifest or the language, so a name lookup would report a missing group as soon as the copy
    /// moved — a false failure that hides the real one.
    /// </summary>
    internal ProviderGroupView? ProviderGroupForId(string providerId)
    {
        var border = ProviderGroupList.Children.OfType<Border>()
            .FirstOrDefault(candidate => candidate.Tag as string == providerId);
        return border is null ? null : ReadGroup(border);
    }

    /// <summary>Whether the affiliate disclosure is rendered at all (inside the OrcaRouter group).</summary>
    internal bool AffiliateDisclosureVisible => ProviderGroups.Any(group => group.HasAffiliateBox);

    internal string AffiliateDisclosureText => _affiliateTextForCheck;

    /// <summary>The affiliate text of the last rendered group that showed one — kept so the assertion can read
    /// the disclosure wording, which lives inside a group rather than in a page-level block.</summary>
    private string _affiliateTextForCheck = "";

    /// <summary>Points the sign-in flow at an injected transport so the self-check never opens a socket.</summary>
    internal void UseOAuthHandlerForCheck(System.Net.Http.HttpMessageHandler handler)
    {
        _chat.OAuthHttp = new System.Net.Http.HttpClient(handler);
        // The real flow waits 45 seconds for a human and launches a browser; the harness must do neither, or the
        // shell check's own timeout fires first and reports the run as a hang rather than as a result. Reporting
        // the browser as unavailable is what drives the sign-in down its no-browser ending.
        _chat.OAuthTimeout = TimeSpan.FromSeconds(1);
        _chat.BrowserOpener = _ => false;
    }

    /// <summary>
    /// Runs the real sign-in flow against the first provider that offers it.
    ///
    /// <para>It no longer goes through a row button, because there is no longer a sign-in button on the page:
    /// the entry point is the auth <i>dialog</i>, and a modal cannot be driven from a self-check. What is still
    /// verified end to end is the flow itself — discovery, the exchange, and the status line it writes — which
    /// is the part that can actually break. The button's wiring is covered separately by asserting the rendered
    /// control exists and carries the right label for the state.</para>
    /// </summary>
    internal Task RunOAuthFlowForCheckAsync()
    {
        var provider = _chat?.Providers.FirstOrDefault(candidate => candidate.SupportsOAuth);
        if (provider is null) return Task.CompletedTask;

        return SignInAsync(provider, null);
    }

    /// <summary>The device-code window the last sign-in built, whether or not it went on screen.</summary>
    internal DeviceAuthWindow? DeviceWindowForCheck { get; private set; }

    private bool _deviceWindowStaysOffScreen;

    /// <summary>
    /// Runs the device-code sign-in for one named provider and hands back the task instead of awaiting it.
    ///
    /// <para>Returning rather than awaiting is the point: closing the code window only means something while the
    /// flow is still waiting for the approval, which is exactly the moment a check has to act in.</para>
    ///
    /// <para>The window is built but never opened — a shell check measures the settings page's own layout, and a
    /// second live top-level window would be measuring the harness instead of the product. One-way like the
    /// injected transport next to it: this page instance belongs to the check run, and a flag that quietly reset
    /// itself mid-suite would make the window assertions depend on where they sit in the file.</para>
    /// </summary>
    internal Task RunDeviceCodeFlowForCheck(string providerId)
    {
        var provider = _chat?.Providers.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return Task.CompletedTask;

        DeviceWindowForCheck = null;
        _deviceWindowStaysOffScreen = true;
        // Shorter than the five minutes a person walking to their phone needs, longer than the interval the
        // scripted poll answers with: a run that is cancelled or approved ends on its own, and only a broken
        // cancel reaches this ceiling. It is not zero, because "the wait ran out" has to stay a thing that can
        // happen *after* the check has finished acting.
        _chat!.DeviceCodeWait = TimeSpan.FromSeconds(10);
        return SignInAsync(provider, null);
    }

    /// <summary>
    /// Closes the device-code window the way the person does, and says whether there was one to close.
    ///
    /// <para>Routed through the window's own <see cref="Window.Closed"/> event rather than the cancellation
    /// source, because the subscription between the two is the thing under test: a check that cancelled the
    /// token itself would pass on a page that had forgotten to wire the close at all.</para>
    /// </summary>
    internal bool CloseDeviceWindowForCheck()
    {
        if (DeviceWindowForCheck is null) return false;
        DeviceWindowForCheck.CloseForCheck();
        return true;
    }

    /// <summary>
    /// Points the model-list fetch at an injected transport, so the self-check can drive the whole path —
    /// request, parse, adopt, cache, repaint — without a live provider.
    ///
    /// <para>A separate seam from <see cref="UseOAuthHandlerForCheck"/> on purpose: the two answer different
    /// questions, and a handler wired for one would happily answer the other. Sharing a client here would make
    /// a check pass on the wrong response.</para>
    /// </summary>
    internal void UseModelListHandlerForCheck(System.Net.Http.HttpMessageHandler handler)
    {
        if (_chat is not null) _chat.ModelListHttp = new System.Net.Http.HttpClient(handler);
    }

    /// <summary>
    /// Runs the model catalog's refresh handler through the same path as its dialog button.
    /// </summary>
    /// <param name="providerId">Provider to refresh; a missing group is a no-op rather than an exception.</param>
    internal async Task RefreshModelsForCheckAsync(string providerId)
    {
        var provider = _chat?.Providers.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is null) return;
        await CreateModelCatalogWindow(provider).RefreshForCheckAsync();
    }

    internal ModelCatalogWindow? ModelCatalogForCheck(string providerId)
        => _chat?.Providers.FirstOrDefault(candidate => candidate.Id == providerId) is { } provider
            ? CreateModelCatalogWindow(provider)
            : null;

    /// <summary>
    /// Scrolls to the second provider group, so a screenshot shows that the groups actually stack rather than
    /// only that one of them renders. A feature that puts every provider in the same single card would look
    /// identical to this one in a picture of the first group alone.
    /// </summary>
    internal void ScrollToSecondGroupForCheck()
    {
        Avalonia.Visual? node = ProviderGroupList;
        while (node is not null and not ScrollViewer) node = node.GetVisualParent();
        if (node is not ScrollViewer viewer || viewer.Content is not Control content) return;

        // Skipped through GroupCards rather than the raw children: the list now holds rules between groups, so a
        // positional Skip(1) would land on the divider above the second group and scroll to the boundary
        // between them — the one place the picture cannot show a second card.
        var second = GroupCards.Skip(1).FirstOrDefault();
        if (second is null) return;

        var offset = second.TranslatePoint(new Point(0, 0), content) ?? new Point(0, 0);
        viewer.Offset = new Vector(0, Math.Max(0, offset.Y - 40));
    }

    /// <summary>Drives the rail the way a click on it does, for the checks and the shot modes that need one
    /// specific group on screen. This replaced <c>ScrollToProviderCardForCheck</c>, which no longer means
    /// anything: the provider list is not below a fold to crawl past any more, it is a category the rail
    /// selects, and selecting it already lands at its top.</summary>
    internal void SelectCategoryForCheck(string key) => SelectCategory(key);

    /// <summary>
    /// Role markers attached to the nodes of a provider group with <c>Control.Tag</c>.
    ///
    /// <para>A group is a deep composite of StackPanels, Grids and Borders whose shape depends on the
    /// provider (a key-only provider has no sign-in row, a keyless one has no auth section at all). A self-check
    /// that walks it by position therefore reads the wrong node as soon as the layout moves — and reports a
    /// failure unrelated to whatever broke. Tagging each part with what it <i>is</i> keeps the walk honest and
    /// lets the assertions name a part instead of describing where it sits.</para>
    /// </summary>
    private static class SectionTags
    {
        internal const string Group = "provider-group";
        internal const string Summary = "provider-summary";
        internal const string Models = "provider-models";
        internal const string ModelList = "provider-model-list";
        internal const string ToggleModelList = "provider-toggle-model-list";

        /// <summary>
        /// Stands in for the model section when it is not rendered at all (an unauthenticated provider that
        /// needs a key). A separate tag from <see cref="Models"/> on purpose: "there is no section" and "the
        /// section is present but empty" are different states, and one check cannot stand for both.
        /// </summary>
        internal const string ModelsHidden = "provider-models-hidden";

        /// <summary>The empty-state line inside the model section: reachable, but the endpoint listed nothing.</summary>
        internal const string ModelsEmpty = "provider-models-empty";

        internal const string ModelRow = "provider-model-row";
        internal const string ModelEnabled = "provider-model-enabled";
        internal const string ModelContext = "provider-model-context";
        internal const string ModelCatalog = "provider-model-catalog";
        internal const string AddModelButton = "provider-add-model";
        internal const string RemoveAllModels = "provider-remove-all-models";
        internal const string RemoveProvider = "provider-remove";
        internal const string ProviderHeaderDivider = "provider-header-divider";

        /// <summary>The control that re-fetches the list from the endpoint.</summary>
        internal const string RefreshModels = "provider-refresh-models";
        // The single authenticate/disconnect control. It is tagged because the self-check has to tell "the
        // button is there" from "the button says the right thing for this state", and those are different
        // failures with the same shape in the tree.
        internal const string AuthButton = "provider-auth-button";
        internal const string ChangeAuthMethod = "provider-change-auth-method";
        internal const string NoCredential = "provider-no-credential";

        /// <summary>
        /// The one line under the provider cards that names where keys are actually stored. Tagged because it is
        /// the only place the honesty about the file tier is visible, and an assertion has to be able to tell
        /// "the line is missing" from "the line is there and empty".
        /// </summary>
        internal const string SecretBackend = "provider-secret-backend";
        internal const string Affiliate = "provider-affiliate";

        // ── Draft interaction (v3) ──
        // The edit form lives inside the group rather than in a modal window, and every draft is always the
        // first child of the list it belongs to. Tags let the self-check find each draft state without walking
        // the tree positionally, which a collapsible layout invalidates on every reshuffle.

        /// <summary>The in-group edit draft form (opened by the header's pencil icon).</summary>
        internal const string EditProvider = "provider-edit";

        /// <summary>The header enable/disable toggle for the whole provider.</summary>
        internal const string ProviderEnabled = "provider-enabled";

        /// <summary>The collapsed header's status dot (colour compresses the three link states).</summary>
        internal const string StatusDot = "provider-status-dot";

        /// <summary>The edit draft's form container, distinct from the collapsed header.</summary>
        internal const string ProviderDraft = "provider-draft";

        /// <summary>The draft row inserted at the top of a model list by "add model".</summary>
        internal const string ModelDraft = "provider-model-draft";

        /// <summary>The draft form inserted at the top of the provider list by "add custom endpoint".</summary>
        internal const string AddProviderDraft = "provider-add-draft";
    }
}
