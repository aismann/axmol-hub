using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The shell. Its shape matches the WPF version pixel for pixel (see the comment at the top of
/// MainWindow.axaml), and it has exactly three responsibilities: wiring navigation, placing pages
/// into <c>PageHost</c>, and translating <see cref="HubWorkspace"/> events into the status bar /
/// log panel / cancel button.
///
/// **Not a single line of business logic lives here**: engines, modules, build, run, and devices
/// are all in Core, orchestrated by <see cref="HubWorkspace"/> (a wholesale port of the non-visual
/// half of WPF <c>MainWindow.xaml.cs</c>).
/// </summary>
public partial class MainWindow : Window
{
    private HubWorkspace _workspace;
    private readonly PreferencesStore _preferencesStore;
    private readonly HubPreferences _preferences;
    private readonly SystemAttentionService _attention = new();
    private readonly Dictionary<string, Control> _pages = [];
    private bool? _attentionDiagnosticBadge;
    private string? _attentionDiagnosticConversationId;
    private bool _windowIsOpen;

    /// <summary>Whether this window has lost the foreground at least once since it was shown. A notification click
    /// arrives as a *return* to the foreground, and a window that never left cannot be returning: without this,
    /// the activation that finishes start-up consumes the pending conversation before any click exists — measured
    /// on GNOME 46, where it fired 13 ms after the notification was armed.</summary>
    private bool _foregroundHasLeftWindow;

    /// <summary>Forced window-visibility answer for the self-check, or <c>null</c> to read the platform. The
    /// suite drives a real <see cref="MainWindow"/> that is never the foreground window and can never truly be
    /// minimized, so without this the desktop half of the visibility composition could only ever be asserted in
    /// one of its two states.</summary>
    private bool? _windowVisibleOverride;

    /// <summary>
    /// The AI assistant page's state. **One instance for the window's whole life**, created before the page
    /// is: it persists providers/conversations under the data root, so unlike <see cref="HubWorkspace"/>
    /// it must survive a data-root switch's decision point — see <see cref="SwitchDataRoot"/>.
    /// </summary>
    private ChatWorkspace _chat;

    /// <summary>The assistant page, built lazily on first navigation (a user who never opens it pays nothing).</summary>
    private ChatPanel? _chatPanel;

    /// <summary>The conversation-list section hosted under the "AI 助手" nav item. Built eagerly because the
    /// sidebar is part of the shell; rebuilt together with <see cref="_chat"/> on a data-root switch.</summary>
    private ChatSidebar? _chatSidebar;

    /// <summary>Sidebar drag state: pointer x and width when the grip grab started.</summary>
    private double _grabStartX;
    private double _grabStartWidth;
    private bool _grabbing;

    /// <summary>Sidebar width bounds (also enforced in the XAML-facing logic).</summary>
    private const double SidebarMin = 240;
    private const double SidebarMax = 420;
    private const double SidebarCollapseThreshold = 200;

    /// <summary>
    /// The download slot's tooltip content. Installed once and rewritten **in place**: re-assigning
    /// <c>ToolTip.Tip</c> closes an open tooltip, and a new value assigned while it is closed does not
    /// re-open it (the update dot used this same holder, and the settings page's progress tip still does).
    /// The slot's tip moves on every progress tick, so a fresh string each time would make the tooltip
    /// impossible to read while a download runs.
    /// </summary>
    private readonly TextBlock _transferSlotTip = new();

    private string _currentKey = "";

    /// <summary>The press that is armed on the title bar, or null when none is. Holding the whole event rather
    /// than a point is what lets the move handler hand the platform the press it actually received; and a
    /// null/non-null test is the whole "is a drag armed" state, which is what a check reads — it cannot drive the
    /// arm that consumes this, because that one enters the platform's move loop.</summary>
    private PointerPressedEventArgs? _titleBarPress;

    /// <summary>Where that press landed, in the window's own coordinates — the origin the drag threshold is
    /// measured from.</summary>
    private Point _titleBarPressOrigin;

    /// <summary>
    /// For the XAML loader and design-time preview (without it Avalonia reports AVLN3001 "not
    /// available via the runtime loader"). Using the default data root is safe: **construction
    /// only reads, never writes** — nothing is persisted until the user clicks a button.
    /// </summary>
    public MainWindow() : this(HubHostOptions.DefaultDataRoot)
    {
    }

    /// <summary>
    /// Data root and settings file both use their default locations. **Verification programs must
    /// not use this overload**: switching language writes the settings file, and the default
    /// settings file is the user's own.
    /// </summary>
    public MainWindow(string dataRoot)
        : this(dataRoot, new PreferencesStore(HubHostOptions.DefaultPreferencesPath), new HubPreferences())
    {
    }

    public MainWindow(string dataRoot, PreferencesStore preferencesStore, HubPreferences preferences)
    {
        // Set in code, not in MainWindow.axaml: Avalonia 12's WindowIcon has no type converter, so
        // `Icon="avares://…"` would fail at XAML load time.
        Icon = HubAppIcon.CreateWindowIcon();
        _preferencesStore = preferencesStore;
        _preferences = preferences;
        _workspace = new HubWorkspace(dataRoot, preferences, preferencesStore);
        _workspace.Owner = this;
        _chat = new ChatWorkspace(dataRoot);
        _attention.Initialize(id => NotificationActivated(id));
        _attention.NotificationActivated += NotificationActivated;
        _attention.Diagnostic += message => Dispatcher.UIThread.Post(
            () => WriteLog("[System attention] " + message));
        // The route GNOME gives a click on a notification's body: the shell raises the already-running window
        // instead of re-running the desktop entry, so no deeplink and no second instance ever reach the broker.
        // That raise produces nothing but this event, and the conversation it belongs to is the one the
        // attention service remembered when it sent the notification. It has to be a *return*: the activation
        // that finishes start-up arrives on its own, ~13 ms after the first notification is armed in the
        // measured run, and consuming there would spend the click before the user ever had one.
        Activated += (_, _) => OnWindowActivated();
        Deactivated += (_, _) => _foregroundHasLeftWindow = true;
        WireChatSeams();
        WireChatRuns();

        InitializeComponent();
        InitializeSidebar();
        InitializeTitleBar();
        InitializeInspector();
        InitializePictureViewer();
        InitializeChrome();
        WireWorkspace();
        WireChrome();

        NavProjects.IsCheckedChanged += (_, _) => OnNavigated(NavProjects, "Projects");
        NavInstalls.IsCheckedChanged += (_, _) => OnNavigated(NavInstalls, "Installs");
        NavToolchains.IsCheckedChanged += (_, _) => OnNavigated(NavToolchains, "Toolchains");
        NavAssistant.IsCheckedChanged += (_, _) =>
        {
            var assistantSelected = NavAssistant.IsChecked == true;
            AssistantSearchButton.IsVisible = assistantSelected;
            AssistantNewButton.IsVisible = assistantSelected;
            OnNavigated(NavAssistant, "Assistant");
        };
        AssistantSearchButton.Click += (_, _) => _chatSidebar!.ToggleSearchFromNavigation();
        AssistantNewButton.Click += (_, _) => _chatSidebar!.StartNewConversationFromNavigation();
        ToolTip.SetTip(AssistantSearchButton, HubStrings.Get("SearchConversationsTip"));
        ToolTip.SetTip(AssistantNewButton, HubStrings.Get("NewConversationTip"));

        // Settings is no longer a nav item: it is the gear at the bottom of the rail. It still navigates like
        // one, so the page keeps its cached instance and ScrollViewer position.
        SettingsButton.Click += (_, _) => NavigateTo("Settings");

        // The brand/host text block beside the gear opens the popup menu (Settings + Appearance). The
        // menu is rebuilt on every open, so its labels and theme checkmarks are always current without
        // any sync machinery.
        BottomMenuButton.Click += (_, _) => ToggleBottomMenu();

        // Default to the "Projects" page, matching the WPF version (WPF uses NavProjects IsChecked="True").
        NavigateTo("Projects");

        // Font conclusions must be handled only after the window is on screen: a modal's owner
        // must be shown first.
        Opened += (_, _) => ReportFonts();
        Opened += (_, _) =>
        {
            _windowIsOpen = true;
            SyncApprovalBadge();
            // Microsoft's contract is that no ITaskbarList3 call may precede the shell's TaskbarButtonCreated
            // message, which explorer posts asynchronously after the HWND appears. Honouring that literally needs
            // a Win32 window-proc hook Avalonia only offers through platform-specific APIs, so this takes the
            // cheap route: one deferred retry. It is safe because SetApprovalBadge records the applied state only
            // on success, so repeating a state that already took is a no-op and a failed one is free to try again.
            // Without it, an approval restored at startup whose first attempt loses the race stays undotted until
            // some later transcript change raises Changed — which for an idle session may never come.
            DispatcherTimer.RunOnce(SyncApprovalBadge, TimeSpan.FromSeconds(1));
        };
    }

    internal void ActivateFromRequest()
    {
        if (ConsumeActivationTarget()) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>The window came back to the foreground — the only signal a desktop gives for a click on a
    /// notification when it raises the running window instead of naming a conversation. Gated on the window
    /// having genuinely left the foreground, see <see cref="_foregroundHasLeftWindow"/>.</summary>
    private void OnWindowActivated()
    {
        if (_foregroundHasLeftWindow) _ = ConsumeActivationTarget();
    }

    /// <summary>Opens the conversation a notification was about when the window is being raised for one, and
    /// reports whether it did. A raise with nothing pending — Alt-Tab, the taskbar, a second instance asked to
    /// show itself — stays the plain raise it always was.</summary>
    private bool ConsumeActivationTarget()
    {
        if (_attention.TryTakeActivationTarget(DateTimeOffset.UtcNow) is not { } conversationId) return false;
        WriteLog($"[System attention] The window was raised with a notification pending; "
                 + $"opening conversation {conversationId}.");
        OpenConversationFromAttention(conversationId);
        return true;
    }

    internal async Task HandleInstallLinkAsync(string value)
    {
        if (SystemAttentionService.TryGetConversationId(value, out var conversationId))
        {
            OpenConversationFromAttention(conversationId);
            return;
        }

        EngineInstallLink request;
        try
        {
            request = EngineInstallLink.Parse(value);
        }
        catch (FormatException ex)
        {
            _workspace.Log.Write("Rejected deeplink: " + ex.Message);
            await HubDialog.ShowAsync(this, HubStrings.Get("DeepLinkTitle"), HubStrings.Get("DeepLinkInvalid"));
            return;
        }

        if (_workspace.CanCancel)
        {
            _workspace.Log.Write($"Rejected deeplink for Axmol {request.Version}: another operation is running.");
            await HubDialog.ShowAsync(this, HubStrings.Get("DeepLinkTitle"), HubStrings.Get("DeepLinkBusy"));
            return;
        }

        WindowState = WindowState.Normal;
        Activate();
        NavigateTo("Installs");
        await _workspace.InstallEngineAsync(request.Version, request.Source);
    }

    internal void WriteLog(string message) => _workspace.Log.Write(message);

    /// <summary>
    /// Startup font self-check. Two things are done separately because they have different
    /// audiences:
    ///
    /// ① **Log**: write a line whenever a missing Chinese font is detected, regardless of the UI
    ///    language — users on the English UI aren't affected, but "why is Chinese rendering as
    ///    boxes" must be findable in the log.
    /// ② **Dialog**: only pop up when the UI language is Chinese (criterion in
    ///    <see cref="CjkFontNotice.ShouldWarn"/>) — at that moment the user sees nothing but boxes,
    ///    and not prompting means leaving them to guess at a broken UI.
    ///
    /// The probe runs in this frame rather than the constructor: <c>FontManager.Current</c> needs
    /// the platform font implementation to be in place.
    /// </summary>
    private void ReportFonts()
    {
        var availability = CjkFontProbe.Availability;
        if (availability == CjkFontAvailability.Available)
        {
            return;
        }

        // Log a line for Unknown too: here we can tell "no font" apart from "probe failed", and the
        // log should make the same distinction.
        _workspace.Log.Write(availability == CjkFontAvailability.Missing
            ? CjkFontNotice.LogLine
            : "CJK font probe returned no result; Chinese rendering is unverified.");

        if (availability == CjkFontAvailability.Missing)
        {
            CjkFontNotifier.NotifyIfNeeded(this);
        }
    }

    /// <summary>The assembled page keys. Verification programs rely on it to confirm every navigation
    /// destination has a real page. <c>Assistant</c> is included: it is a full page now, not a drawer.</summary>
    internal static string[] PageKeys => ["Projects", "Installs", "Toolchains", "Assistant", "Settings"];

    internal HubWorkspace Workspace => _workspace;

    /// <summary>
    /// Switches pages and returns the result to the caller for runtime assertions.
    /// Pages are **constructed on demand and cached**: rebuilding on every navigation would lose
    /// selection and scroll position.
    /// </summary>
    internal Control NavigateTo(string name)
    {
        if (!_pages.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "Projects" => new ProjectsPage(_workspace),
                "Installs" => new InstallsPage(_workspace),
                "Toolchains" => new ToolchainsPage(_workspace),
                "Assistant" => _chatPanel ??= CreateChatPanel(),
                "Settings" => new SettingsPage(_workspace, _preferencesStore, _preferences, _chat, OpenFolder, ApplyLanguage, SwitchDataRoot, UseTheme),
                _ => throw new ArgumentException("Unknown page: " + name, nameof(name)),
            };
            _pages[name] = page;
        }

        _currentKey = name;
        PageHost.Content = page;
        SyncNavigation(name);
        SyncApprovalBadge();

        // The conversation list is a child of the assistant nav item: hidden on every other page.
        ChatSidebarHost.IsVisible = name == "Assistant";
        // The inspector belongs to the assistant too, and a picture left open over a page you navigated away
        // from is a picture you can no longer reach the transcript of — so navigating closes it.
        SyncInspectorForPage();
        if (PictureViewerHost.IsVisible) ClosePictureViewer();
        UpdatePageTitle();
        return page;
    }

    /// <summary>Builds the assistant page once and keeps the shell's reference to it, so a later
    /// data-root switch can drop and rebuild it (<see cref="ReplaceWorkspace"/>).</summary>
    private ChatPanel CreateChatPanel()
    {
        var panel = new ChatPanel(_chat);
        // Sending a first message auto-titles the conversation; the workspace does not raise Changed for
        // streamed messages, so the panel tells the shell to refresh its sidebar and top-bar title.
        panel.ConversationStateChanged += () =>
        {
            _chatSidebar?.Reload();
            UpdatePageTitle();
        };
        // The panel owns the transcript; the window owns the surfaces that reach past it. A picture opens in the
        // window-level viewer (it fills the window, not the chat column), and the plan/diff/repository review
        // opens in the inspector column beside the page. Which of the column's two homes that pane is in, and
        // whether the column is up at all, is not asked of the panel: both now live on the title bar's own
        // buttons, which are wired straight to the two methods here (see InitializeInspector).
        panel.ShowPictureViewer = (set, index) => OpenPictureViewer(set, index);
        panel.OpenInspector = (tab, turnIndex) => OpenInspector(tab, turnIndex);
        panel.RefreshInspector = RefreshInspector;
        // The pane's header tabs are the reader's choice, and this column repaints on a schedule of its own —
        // every transcript move, every repository read that lands. The shell asks for a tab by name on each of
        // those, so it has to be told which one the reader is on, or their click is undone a token later.
        panel.InspectorTabFollowed = tab => _inspectorTab = tab;
        return panel;
    }

    /// <summary>
    /// What the assistant reads from the rest of the app, wired in one place because the workspace is rebuilt
    /// whenever the data root changes and a seam missed on the rebuild is a silent downgrade: no engine roots
    /// means engine installs stop being read-only, no log means command output is truncated with nowhere left.
    /// </summary>
    private void WireChatSeams()
    {
        _chat.HubSnapshotProvider = CreateReadOnlyChatSnapshot;
        _chat.PreferencesProvider = () => _preferences;
        // The tool trust list is the one app-wide setting the assistant itself writes, so it writes through the
        // same store the settings page uses rather than opening a second one on the same file.
        _chat.PreferencesPersist = _preferencesStore.Save;
        _chat.AuditWrite = _workspace.Log.Write;
        _chat.LogProvider = () => _workspace.Log;
        _chat.EngineRootsProvider = () => [.. _workspace.State.Engines.Select(engine => engine.Path)];
    }

    private ChatWorkspace.HubReadOnlySnapshot CreateReadOnlyChatSnapshot()
        => new(
            _workspace.State.Projects
                .Select(project => new ChatWorkspace.HubProjectSummary(
                    project.Name, project.Path, project.Version, project.Platform, project.Configuration, project.BuildStatus))
                .ToArray(),
            _workspace.State.Engines
                .Select(engine => new ChatWorkspace.HubEngineSummary(engine.Version, engine.Channel))
                .ToArray(),
            _workspace.Components
                .Select(component => new ChatWorkspace.HubToolchainSummary(
                    component.Name, component.Status.ToString(), component.Executable is not null))
                .ToArray());

    /// <summary>
    /// Keeps the left navigation highlight in sync with the current page. This is exactly what the
    /// WPF version does in <c>SelectPage</c>.
    /// Skipping it yields "showing page A but highlighting page B" — invisible at compile time and
    /// at binding time, only catchable by screenshot review.
    ///
    /// Settings is not a nav item, so it has no highlight to sync: landing on it simply clears the four
    /// radio buttons. That is why the loop below can omit it while <see cref="PageKeys"/> still lists it.
    /// </summary>
    private void SyncNavigation(string name)
    {
        foreach (var (key, button) in new[]
                 {
                     ("Projects", NavProjects), ("Installs", NavInstalls),
                     ("Toolchains", NavToolchains), ("Assistant", NavAssistant),
                 })
        {
            var expected = key == name;
            if (button.IsChecked != expected)
            {
                button.IsChecked = expected;
            }
        }
    }

    internal Control? CurrentPage => PageHost.Content as Control;

    internal void SetStatus(string text) => Status.Text = text;

    /// <summary>
    /// Puts how many sessions are answering right now on the strip. Called for every <see cref="ChatWorkspace"/>
    /// the shell builds, including the one a data-root switch replaces — an unresubscribed strip would quietly
    /// stop reporting replies, which is exactly the kind of failure nobody sees.
    /// </summary>
    private void WireChatRuns()
    {
        _chat.RunsChanged += _ => UpdateChatRunsStatus();
        _chat.Changed += SyncApprovalBadge;
        _chat.AttentionRequired += OnChatAttentionRequired;
        _chat.RunCompleted += OnChatRunCompleted;
        // The strip's repository slot is a render of what has already been read, so it follows the reads other
        // triggers started: one handler per landing, one per conversation switch, and a completion that takes the
        // stale number away. None of the three asks git anything — which is the whole reason a slot that lives
        // outside the chat page can be honest about a repository without paying for it.
        // A completion still lets it come back: the panel reads again on a finished run when the repository tab
        // is the one being looked at, and that landing repaints this.
        _chat.RepositoryChanged += UpdateRepoIndicator;
        _chat.Changed += UpdateRepoIndicator;
        _chat.RunCompleted += (_, _) => InvalidateRepoIndicator();
        SyncApprovalBadge();
    }

    private void OnChatAttentionRequired(string conversationId, ChatAttentionKind kind)
    {
        if (App.Options.IsAutomation) return;
        if (!SystemAttentionService.ShouldNotifyApproval(
                conversationId, _chat.ViewedConversationId, AttentionNow))
        {
            _attention.Report($"No system notification for approval in conversation {conversationId}: "
                              + "the user is already viewing it in the active Assistant window.");
            return;
        }
        var title = _chat.SessionTitleFor(conversationId);
        if (string.IsNullOrWhiteSpace(title)) title = HubStrings.Get("Conversation");
        var titleKey = kind == ChatAttentionKind.PlanApproval
            ? "ChatPlanNotificationTitle"
            : "ChatToolApprovalNotificationTitle";
        var bodyKey = kind == ChatAttentionKind.PlanApproval
            ? "ChatPlanNotificationBody"
            : "ChatToolApprovalNotificationBody";
        _attention.Show(HubStrings.Get(titleKey),
            string.Format(System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get(bodyKey), title),
            conversationId);
        SyncApprovalBadge();
    }

    private void OnChatRunCompleted(string conversationId, RunOutcome outcome)
    {
        if (App.Options.IsAutomation) return;
        if (!SystemAttentionService.ShouldNotifyRun(
                conversationId, _chat.ViewedConversationId, AttentionNow,
                _chat.HasPendingPlanApproval(conversationId), outcome.Result))
            return;

        var title = _chat.SessionTitleFor(conversationId);
        if (string.IsNullOrWhiteSpace(title)) title = HubStrings.Get("Conversation");
        var notificationTitle = outcome.Result switch
        {
            RunResult.Failed => "ChatRunFailedNotificationTitle",
            RunResult.TimedOut => "ChatRunTimedOutNotificationTitle",
            _ => "ChatRunCompletedNotificationTitle",
        };
        _attention.Show(HubStrings.Get(notificationTitle),
            string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatRunNotificationBody"), title),
            conversationId);
    }

    private void SyncApprovalBadge()
    {
        // This reads window state and asks the shell for a taskbar overlay, both of which belong to the UI
        // thread, and it is reached from ChatWorkspace.Changed — one of 54 raise sites, only some of which are
        // provably on the UI thread. So hop rather than assume. The hop is not about COM apartments: the overlay
        // interface is created and marshalled correctly from an MTA thread as well.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SyncApprovalBadge);
            return;
        }
        if (_windowIsOpen && !App.Options.IsAutomation)
            _attention.SetApprovalBadge(this, _attentionDiagnosticBadge == true
                ? 1
                : _chat.PendingApprovalConversationCount());
    }

    private void NotificationActivated(string conversationId)
        => Dispatcher.UIThread.Post(() => OpenConversationFromAttention(conversationId));

    private void OpenConversationFromAttention(string conversationId)
    {
        // Every route that lands here named its own conversation — an action key, a body link, a deeplink — so
        // the remembered target has been overtaken. Left standing, it would replay this jump on the next
        // ordinary raise, long after the notification it belonged to.
        _attention.ClearActivationTarget();
        if (string.Equals(conversationId, _attentionDiagnosticConversationId, StringComparison.Ordinal))
        {
            NavigateTo("Assistant");
            WindowState = WindowState.Normal;
            Activate();
            WriteLog("The test notification was activated; the app returned to the Assistant page.");
            return;
        }

        if (_chat.OpenConversation(conversationId) is null) return;
        NavigateTo("Assistant");
        WindowState = WindowState.Normal;
        Activate();
    }

    internal void RunSystemAttentionDiagnostic()
    {
        if (!App.Options.TestSystemAttention || App.Options.IsAutomation) return;

        _attentionDiagnosticConversationId = Guid.NewGuid().ToString("N");
        _attentionDiagnosticBadge = true;
        var badgeApplied = _attention.SetApprovalBadge(this, 1);
        _attention.Report(badgeApplied
            ? "The diagnostic numeric taskbar badge was requested successfully."
            : "The diagnostic numeric taskbar badge request failed or is unsupported; see the preceding diagnostic.");
        _attention.Show(
            HubStrings.Get("AttentionDiagnosticTitle"),
            HubStrings.Get("AttentionDiagnosticBody"),
            _attentionDiagnosticConversationId);
        _attention.Report("A test notification was requested. OS notification settings or focus mode may still suppress its display.");

        DispatcherTimer.RunOnce(() =>
        {
            _attentionDiagnosticBadge = null;
            _attentionDiagnosticConversationId = null;
            SyncApprovalBadge();
            WriteLog("System attention diagnostic ended; the badge returned to the real approval state.");
        }, TimeSpan.FromSeconds(30));
    }

    private void UpdateChatRunsStatus()
    {
        var running = _chat.RunningCount;
        ChatRuns.IsVisible = running > 0;
        ChatRuns.Text = running > 0 ? string.Format(HubStrings.Get("RunningSessionsFormat"), running) : "";
    }

    /// <summary>
    /// Renders the strip's repository slot from the last read this app already holds. It never asks for one: the
    /// only states worth 28 pixels of the strip are states some other trigger — entering the repository tab,
    /// pressing its ⟳, a run that finished while the tab was up — already verified by running git. That is what
    /// makes this element safe to subscribe to <see cref="ChatWorkspace.Changed"/>: it reads one slot and formats
    /// one string, and the ban on hanging work off that event is a ban on <em>reads</em>, not on repainting.
    /// </summary>
    private void UpdateRepoIndicator()
    {
        // Reached from the same event as <see cref="SyncApprovalBadge"/>, and that one's comment says why the hop
        // is not paranoia: of ChatWorkspace's raise sites, only some are provably on the UI thread. The null test
        // is the other half — <c>WireChatRuns</c> subscribes before <c>InitializeComponent</c> has built the
        // strip, so a raise that early would be a null dereference of a control that has nothing to say yet.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateRepoIndicator);
            return;
        }
        if (RepoIndicatorText is null) return;
        var (visible, text) = RepoIndicatorStateFor(
            _chat.RepositoryFor(_chat.ActiveConversation?.WorkspaceRoot)?.State);
        RepoIndicator.IsVisible = visible;
        RepoIndicatorText.Text = visible ? text : "";
        ToolTip.SetTip(RepoIndicator, HubStrings.Get("StatusRepoOpenHint"));
    }

    /// <summary>The whole decision, as a function of the snapshot — which is also the shape a check asserts on,
    /// because a strip that lit on every session from first launch would look identical to one that lit on the
    /// dirty one until something was actually read. Silence is the answer for a clean tree, for a directory that
    /// is not a repository, for a session with no directory, and for a directory nobody has read yet: none of
    /// those is something the person needs told twice a second. Only a tree with something uncommitted, and a
    /// read that honestly failed, earn the row.</summary>
    internal static (bool Visible, string Text) RepoIndicatorStateFor(GitRepositoryState? state)
    {
        if (state is null) return (false, "");
        if (!state.IsReadable)
        {
            // NoWorkspace and NotARepository are not failures — there is no repository here to be dirty in — and
            // a strip that reported them would be lit for every ordinary chat. The three that are honest failures
            // own one short sentence here; their five distinct reasons stay down in the tab, which is where a
            // person who asks gets to read them.
            return state.Outcome is GitReadOutcome.GitMissing or GitReadOutcome.UnsafeRepository
                or GitReadOutcome.Failed
                ? (true, HubStrings.Get("StatusRepoFailed"))
                : (false, "");
        }
        if (state.Entries.Count == 0) return (false, "");
        // The same dash the tab's own head line uses for a detached branch, so the two surfaces never disagree
        // about what a branch that is not named is called.
        var branch = string.IsNullOrEmpty(state.Branch) ? "—" : state.Branch;
        // A cut list says how many were listed, not how many are dirty, and the strip is not allowed to let the
        // two read as the same number.
        var key = state.Truncated ? "StatusRepoDirtyTruncatedFormat" : "StatusRepoDirtyFormat";
        return (true, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(key), state.Entries.Count, branch));
    }

    /// <summary>Takes the slot's words away without waiting for anything to answer. A run can commit, check out
    /// or build without Hub's file tool, and from the moment one ends the cached read is no longer a fact — and a
    /// persistent surface that keeps repeating a number the run just invalidated is worse than one that says
    /// nothing. The next read that lands brings it back.</summary>
    private void InvalidateRepoIndicator()
    {
        // Same construction-window as <see cref="UpdateRepoIndicator"/>: a run cannot complete before the strip
        // exists, but the subscription is older than the strip is, and this is the one handler that would find
        // out the hard way.
        if (RepoIndicatorText is null) return;
        RepoIndicator.IsVisible = false;
        RepoIndicatorText.Text = "";
    }

    internal bool RepoIndicatorVisibleForCheck => RepoIndicator.IsVisible;
    internal string RepoIndicatorTextForCheck => RepoIndicatorText.Text ?? "";

    // ───────────────────────── The download queue's slot ─────────────────────────

    /// <summary>
    /// Repaints the strip's download slot, and the popover's rows while that popover is open.
    ///
    /// The rows are only rebuilt when the *set* of transfers changes; a progress tick repainting the handful
    /// of numbers it moved. Re-creating the row controls on every 80 KB report is how a list that works in a
    /// unit test stutters on a real download.
    /// </summary>
    private void UpdateTransferSlot()
    {
        // Same construction-window as UpdateRepoIndicator: the queue can report before the strip exists.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateTransferSlot);
            return;
        }

        if (TransferSlotText is null) return;

        var (visible, label, isAction) = TransferSlotStateFor(
            TransferQueue.Instance.Items, UpdateService.Instance.Download, UpdateService.Instance.PendingVersion, _restartArmed);
        if (!isAction)
        {
            // Whatever the second press was standing by for is gone, so the word goes back before the next
            // click can find it still armed on a different state.
            _restartArmed = false;
        }

        TransferSlot.IsVisible = visible;
        TransferSlotText.Text = visible ? label : "";
        TransferSlot.Classes.Set("accent", isAction);
        _transferSlotTip.Text = isAction
            ? UpdateService.Instance.PendingVersion is { } version
                ? string.Format(HubStrings.Get("UpdateRestartActionTip"), version)
                : HubStrings.Get("TransferRestartAction")
            : HubStrings.Get("TransferSlotTip");
        if (!visible)
        {
            // Nothing left to act on and nothing left to read: the popover goes with its own anchor rather
            // than staying open over an empty list.
            QueuePopup.IsOpen = false;
        }
        else if (QueuePopup.IsOpen)
        {
            RenderTransferRows();
        }
    }

    /// <summary>
    /// What the slot says, as a function of the queue and the update's own phase — the shape a check
    /// asserts on, because every one of these branches otherwise needs a real download to reach.
    ///
    /// <para>Two modes, one slot. While transfers are moving (or one failed and went unread) the slot is
    /// the <b>queue</b>: a count, and a press that lifts the popover. When nothing is moving and the only
    /// thing left to decide is the update already on disk, the slot <b>is that decision</b> — restarting
    /// takes one press, not a trip through Settings. Which is the whole point of downloading in the
    /// background: the user should never have to go looking for the thing that finished.</para>
    ///
    /// <para>Silence is the answer while nothing is moving, nothing failed and nothing waits: a strip that
    /// lit for work already finished would be reporting the past. A failure keeps the slot lit because it
    /// is still a thing to act on.</para>
    /// </summary>
    internal static (bool Visible, string Label, bool IsAction) TransferSlotStateFor(
        IReadOnlyList<TransferQueue.Item> items, UpdateService.DownloadState download, string? pendingVersion, bool armed)
    {
        var active = items.Where(item => item.IsActive).ToList();
        if (active.Count > 0)
        {
            var moving = active.Count(item => item.IsMoving);
            // The percentage rides only when exactly one transfer is moving. Averaging two of them into one
            // number would produce a figure that describes neither download, and the row inside the popover
            // is where each one's own progress belongs.
            var single = moving == 1 ? active.Single(item => item.IsMoving) : null;
            return single?.Report.Percent is { } percent
                ? (true, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("TransferSlotPercentFormat"), moving, active.Count, percent), false)
                : (true, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("TransferSlotFormat"), moving, active.Count), false);
        }

        // The restart waits on the user, not the other way round: this is the state where the bytes have
        // landed and only a process swap is missing.
        if (download == UpdateService.DownloadState.Ready)
        {
            return (true, HubStrings.Get(armed ? "TransferRestartArmed" : "TransferRestartAction"), true);
        }

        var failed = items.Count(item => item.State == TransferQueue.Phase.Failed);
        return failed > 0
            ? (true, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("TransferSlotFailed"), failed), false)
            : (false, "", false);
    }

    /// <summary>
    /// The slot's one press. Queue mode lifts the popover; action mode restarts — unless something is
    /// still working, in which case the first press only arms the button and says so on its own label.
    ///
    /// <para>Restarting is the one thing in this app that cannot be undone from the inside: it replaces the
    /// process, so a reply mid-stream and a build mid-compile both stop, and the second tap is what stands
    /// between an accidental click and that. Same shape as a steer waiting for its second tap, for the same
    /// reason — the consequence is read once, on the button that carries it, not in a dialog.</para>
    /// </summary>
    private void OnTransferSlotClicked()
    {
        var (visible, _, isAction) = TransferSlotStateFor(
            TransferQueue.Instance.Items, UpdateService.Instance.Download, UpdateService.Instance.PendingVersion, _restartArmed);
        if (!visible)
        {
            return;
        }

        if (!isAction)
        {
            ToggleQueuePopup();
            return;
        }

        var busy = RestartInterruptsWork();
        if (busy && !_restartArmed)
        {
            _restartArmed = true;
            UpdateTransferSlot();
            // A button left armed is a button that restarts on the next stray click, minutes later, when
            // whatever made it busy has long since finished. The word goes away on its own.
            DispatcherTimer.RunOnce(() =>
            {
                if (!_restartArmed) return;
                _restartArmed = false;
                UpdateTransferSlot();
            }, TimeSpan.FromSeconds(10));
            return;
        }

        // The arm is spent by the press that used it, even though in the real case the process ends here:
        // a button left armed is a button that restarts on the next stray click.
        _restartArmed = false;
        UpdateService.Instance.ApplyAndRestart();
    }

    /// <summary>Set while the restart slot is waiting for its confirming second press.</summary>
    private bool _restartArmed;

    /// <summary>
    /// What a restart would interrupt. A probe rather than an inline read because the armed second tap is
    /// a rule worth asserting on its own, and parking a live stream behind it to reach one branch is not a
    /// fixture anyone could read. The window installs the real answer where it wires the slot.
    /// </summary>
    internal Func<bool> RestartInterruptsWork { get; set; } = () => false;

    /// <summary>Set once the user has answered "quit anyway" to running transfers, so the re-issued
    /// <see cref="Window.Close"/> is not met by the same question.</summary>
    private bool _quitConfirmed;

    /// <summary>
    /// Asks about quitting while transfers are moving, and closes on the answer.
    ///
    /// <para>The question cannot be awaited from <c>Closing</c> — that handler is synchronous, so this runs
    /// <c>e.Cancel</c> first and re-issues the close afterwards. <see cref="_quitConfirmed"/> is what keeps
    /// it a single question rather than a loop, and a dialog dismissed by its own X counts as "stay"
    /// (<see cref="HubDialogResult.Cancel"/> is deliberately the zero value).</para>
    ///
    /// <para>Worth the interruption because a transfer is the one work here that survives the answer either
    /// way: <c>DownloadManager</c> keeps a <c>.partial</c> and resumes with a Range request, so quitting
    /// loses the minutes, not the bytes.</para>
    /// </summary>
    private async Task ConfirmQuitAsync()
    {
        var running = TransferQueue.Instance.ActiveCount;
        var answer = await HubDialog.ShowAsync(this,
            HubStrings.Get("QuitDuringDownloadsTitle"),
            string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("QuitDuringDownloadsBody"), running),
            HubDialogButtons.OkCancel, danger: true);
        if (answer != HubDialogResult.Ok)
        {
            return;
        }

        _quitConfirmed = true;
        Close();
    }


    private void ToggleQueuePopup()
    {
        if (QueuePopup.IsOpen)
        {
            QueuePopup.IsOpen = false;
            return;
        }

        RenderTransferRows();
        QueuePopup.IsOpen = true;
    }

    /// <summary>The rows, reconciled against the queue. The list is short and changes rarely, so the
    /// reconciliation is "same shape → repaint in place, otherwise build it again".</summary>
    private void RenderTransferRows()
    {
        var items = TransferQueue.Instance.Items;
        if (_queueRowViews.Count != items.Count
            || items.Where((item, index) => !ReferenceEquals(_queueRowViews[index].Bound, item)).Any())
        {
            QueueRows.Children.Clear();
            _queueRowViews.Clear();
            foreach (var item in items)
            {
                var row = new QueueRowView(item, () => TransferQueue.Instance.Cancel(item.Identity));
                QueueRows.Children.Add(row.Control);
                _queueRowViews.Add(row);
            }

            return;
        }

        foreach (var view in _queueRowViews)
        {
            view.Update();
        }
    }

    private readonly List<QueueRowView> _queueRowViews = [];

    /// <summary>One row of the popover: what is being fetched, how far it has come, and the press that
    /// stops it. Held as controls rather than rebuilt, so a progress tick moves three strings.</summary>
    private sealed class QueueRowView
    {
        private readonly TransferQueue.Item _item;
        private readonly TextBlock _title = new() { Classes = { "queue-row-title" } };
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6 };
        private readonly TextBlock _measure = new() { Classes = { "queue-row-measure" } };
        private readonly TextBlock _error = new() { Classes = { "queue-row-error" }, IsVisible = false };
        private readonly Button _cancel = new()
        {
            Classes = { "strip" },
            Content = HubStrings.Get("Cancel"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        public QueueRowView(TransferQueue.Item item, Action cancel)
        {
            _item = item;
            _cancel.Click += (_, _) => cancel();
            Control = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel { Spacing = 4, Children = { _title, _bar, _measure, _error } },
                    _cancel,
                },
            };
            Grid.SetColumn(_cancel, 1);
            Update();
        }

        public Control Control { get; }

        public TransferQueue.Item Bound => _item;

        public void Update()
        {
            _title.Text = _item.Title + " · " + _item.PhaseText;
            // An unmeasurable phase gets the indeterminate bar rather than a number held at 99: the local
            // work after the bytes (unzipping, patching) is real work with no percentage in it.
            _bar.IsIndeterminate = _item.State == TransferQueue.Phase.Applying;
            _bar.Value = _item.Fill ?? 0;
            _measure.Text = _item.MeasureText;
            _error.Text = _item.Error ?? "";
            _error.IsVisible = _item.Error is { Length: > 0 };
            // A finished row keeps no cancel: there is nothing left to stop, and a button that answers to
            // nothing is the thing this shell already refused to do in the version picker.
            _cancel.IsVisible = _item.IsActive;
        }
    }

    internal bool TransferSlotVisibleForCheck => TransferSlot.IsVisible;
    internal string TransferSlotTextForCheck => TransferSlotText.Text ?? "";
    internal bool TransferSlotAccentForCheck => TransferSlot.Classes.Contains("accent");
    internal bool QueuePopupOpenForCheck => QueuePopup.IsOpen;
    internal int QueueRowCountForCheck => QueueRows.Children.Count;

    /// <summary>The slot and the popover as visuals, for a frame capture. The popover needs this rather
    /// than a region of the window's frame because a Popup presents in its own PopupRoot — it is a
    /// top-level visual, and not part of the window's pixels.</summary>
    internal Visual TransferSlotForCheck => TransferSlot;
    internal Visual? QueuePopoverForCheck => QueuePopup.Child;
    internal void OpenQueuePopupForCheck() => ToggleQueuePopup();
    internal void CloseQueuePopupForCheck() => QueuePopup.IsOpen = false;

    /// <summary>The restart slot's armed state. Setting it repaints, so a check reads what the user would
    /// see rather than what the field happens to hold. A test reaches the busy half through
    /// <see cref="RestartInterruptsWork"/>, not through this.</summary>
    internal bool RestartArmedForCheck
    {
        get => _restartArmed;
        set
        {
            _restartArmed = value;
            UpdateTransferSlot();
        }
    }

    /// <summary>Presses the slot the way the user does, so a check walks the same routing.</summary>
    internal void ClickTransferSlotForCheck() => TransferSlot.RaiseEvent(
        new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));


    /// <summary>Presses the strip's own button, so a check drives the wiring rather than the shell method the
    /// button happens to end up calling.</summary>
    internal void ClickRepoIndicatorForCheck()
        => RepoIndicator.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// Switches the data root. **Deliberately does not rebuild the window** — the WPF version news
    /// up a <c>MainWindow</c> and closes the old one because its state, pages, and log are all bound
    /// to the root. Here we only rebuild <see cref="HubWorkspace"/> and drop the page cache: pages
    /// hold workspace references, and after a root switch the old selections (selected project,
    /// device list, expanded new-project panel) should be invalid anyway.
    ///
    /// The payoff: switching the data root no longer depends on the shell shape, so we don't have
    /// to wait for the main window shape to be finalized.
    /// </summary>
    /// <returns>Whether it actually switched. Does nothing when the target equals the current
    /// root.</returns>
    internal bool SwitchDataRoot(string directory)
    {
        // Switching is forbidden while an operation is running: the operation holds the old root's
        // downloader, log, and toolchain directories, and switching mid-run would make it write into
        // a directory that no longer belongs to the current session.
        if (_workspace.IsBusy)
        {
            throw new InvalidOperationException(HubStrings.Get("WaitForOperation"));
        }

        var path = PreferencesStore.VerifyDirectory(directory);
        if (path.Equals(_workspace.Store.Root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Persist first, then switch. Once switched there's no going back to the old instance, so a
        // failed write must surface the error in place, on the old state — otherwise the UI would
        // point at the new root while the settings file still says the old root, silently reverting
        // on the next launch.
        var previous = _preferences.DataRoot;
        _preferences.DataRoot = path;
        try
        {
            _preferencesStore.Save(_preferences);
        }
        catch
        {
            _preferences.DataRoot = previous;
            throw;
        }

        ReplaceWorkspace(new HubWorkspace(path, _preferences, _preferencesStore));
        SetStatus(HubStrings.Get("DataChanged"));
        return true;
    }

    private void ReplaceWorkspace(HubWorkspace next)
    {
        _workspace.Dispose();
        _workspace = next;
        _workspace.Owner = this;

        // Events are wired on the new instance, so re-subscribing here won't append the log twice.
        WireWorkspace();

        // Old pages hold the old workspace and must be dropped wholesale; the next NavigateTo will
        // reconstruct them.
        _pages.Clear();

        // The assistant's providers and conversations also live under the data root, so it must be rebuilt
        // for the new one and the page (if it was ever built) dropped with it — otherwise the assistant would
        // keep listing the previous root's conversations and saving new ones into a directory that is no
        // longer current. Rebuilding rather than re-pointing mirrors how the pages are handled.
        _chat.Dispose();
        _chat = new ChatWorkspace(next.Store.Root);
        WireChatSeams();
        WireChatRuns();
        // A new workspace has read nothing yet, so the strip's repository slot has nothing true to say. Left
        // standing, it would report the previous root's dirty count against a directory nobody is looking at.
        InvalidateRepoIndicator();
        // The pane may be living in the window-wide overlay, and it belongs to the panel about to be dropped:
        // left there, a dead workspace's plan would keep covering a page that can no longer produce one.
        RestoreInspector();
        _chatPanel = null;
        // …and the same for the pane itself. `RestoreInspector` only acts on the overlay, so without this the
        // dropped panel's pane stays hosted in the column: the next `NavigateTo` repaints the column from the new
        // root, finds a pane already there, and shows the previous data root's plan under a conversation that has
        // no idea it exists. Which tab and which plan turn go back to the defaults for the same reason — they were
        // answers about a transcript that is gone.
        SetInspectorPane(null);
        _inspectorTab = "plan";
        _inspectorTurn = -1;

        // The conversation sidebar holds the old workspace; rebuild it for the new one.
        _chatSidebar = BuildChatSidebar();
        ChatSidebarHost.Content = _chatSidebar;

        // The log panel holds the previous root's content; leaving it would point people at a
        // directory no one is looking at anymore.
        ActivityLog.Text = "";
        ShowLog(false);

        InitializeChrome();
        NavigateTo(_currentKey);
    }

    /// <summary>The language in settings. <see cref="PageShots"/> relies on it to switch back after
    /// changing language.</summary>
    internal string PreferredLanguage => _preferences.Language;

    /// <summary>
    /// Switches language and repaints the UI immediately. This is the same path as picking a
    /// language in the settings page dropdown; only the caller changes from a person to
    /// <c>--smoke-pages</c>.
    ///
    /// **Writes the settings file**: language is a user preference, and not persisting it would
    /// diverge from the current selection in the settings page. Point <c>--preferences</c> at a
    /// temporary file to avoid touching the user's own.
    /// </summary>
    internal void UseLanguage(string language)
    {
        _preferences.Language = language;
        _preferencesStore.Save(_preferences);
        HubStrings.Apply(language, Application.Current!);
        ApplyLanguage();
    }

    /// <summary>
    /// Switches theme: write settings → apply the variant. The **single owner** of that sequence —
    /// the settings page's picker and the bottom menu's appearance items are two views of it. Same
    /// order as <see cref="UseLanguage"/> (persist first), for the same reason: a failed write must
    /// leave the UI untouched rather than showing a theme the settings file doesn't have — it would
    /// silently revert on the next launch.
    ///
    /// Unlike the language path there is **nothing to repaint afterwards**: every token is a
    /// DynamicResource and re-resolves on its own, including in windows built long before the switch.
    /// </summary>
    /// <returns>Null on success; the error message on failure (the caller decides how to show it).</returns>
    internal string? UseTheme(string theme)
    {
        theme = HubTheme.Normalize(theme);
        var previous = ThemeService.Current;
        if (theme == previous)
        {
            return null;
        }

        try
        {
            _preferences.Theme = theme;
            _preferencesStore.Save(_preferences);
            ThemeService.Apply(theme);
            return null;
        }
        catch (Exception ex)
        {
            // On failure, revert, otherwise the UI would show a theme that disagrees with the settings file.
            _preferences.Theme = previous;
            ThemeService.Apply(previous);
            return ex.Message;
        }
    }

    /// <summary>
    /// Shows/hides the bottom entry's popup. Built fresh on every open: labels follow the UI language
    /// and exactly one checkmark tracks <see cref="ThemeService.Current"/> with no sync machinery.
    /// </summary>
    private void ToggleBottomMenu()
    {
        if (BottomMenuForCheck is { IsOpen: true } open)
        {
            open.Hide();
            return;
        }

        FlyoutBase.SetAttachedFlyout(BottomMenuButton, BuildBottomMenu());
        FlyoutBase.ShowAttachedFlyout(BottomMenuButton);
    }

    /// <summary>
    /// The bottom entry's popup: Settings (navigates like the gear click used to) and an Appearance
    /// submenu holding the three theme values as radio items. <see cref="SettingsPage.ThemeTextKey"/>
    /// stays the theme→copy-key mapping so a new theme can't leave its label behind. (Avalonia 12 has
    /// no MenuFlyoutItem family: the flyout hosts <see cref="MenuItem"/>s directly, and a MenuItem with
    /// children is the submenu.)
    /// </summary>
    internal MenuFlyout BuildBottomMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };

        var settings = new MenuItem { Header = HubStrings.Get("Settings") };
        settings.Click += (_, _) => NavigateTo("Settings");
        menu.Items.Add(settings);

        var appearance = new MenuItem { Header = HubStrings.Get("Appearance") };
        foreach (var theme in HubTheme.All)
        {
            var item = new MenuItem
            {
                Header = HubStrings.Get(SettingsPage.ThemeTextKey(theme)),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = theme == ThemeService.Current,
                Tag = theme,
            };
            item.Click += (_, _) =>
            {
                var error = UseTheme(theme);
                if (error is not null)
                {
                    SetStatus(error);
                }

                // Re-sync every checkmark: a radio click checks itself but the group's behaviour is
                // left to us, so without this the user could end up with no theme checked (clicking
                // the already-checked one) while the theme itself didn't move.
                foreach (var other in appearance.Items.OfType<MenuItem>())
                {
                    other.IsChecked = other.Tag as string == ThemeService.Current;
                }
            };
            appearance.Items.Add(item);
        }

        menu.Items.Add(appearance);
        return menu;
    }

    /// <summary>Recomputes all **imperatively** written copy after a language change (DynamicResource
    /// doesn't cover them).</summary>
    internal void ApplyLanguage()
    {
        // InitializeChrome also rewrites the chrome tooltips (gear + bottom text block), so a language
        // switch reaches them.
        InitializeChrome();
        _chatSidebar?.Reload();
        UpdatePageTitle();
        UpdateChatRunsStatus();
        // The strip's repository slot is composed copy, not a DynamicResource, so a language switch has to be
        // told or the row keeps the previous tongue while everything around it changes.
        UpdateRepoIndicator();
        // Same for the download slot, and for the rows if the popover happens to be open: every one of those
        // strings is composed in code from a key.
        UpdateTransferSlot();

        foreach (var page in _pages.Values)
        {
            switch (page)
            {
                case SettingsPage settings:
                    settings.Reload();
                    break;
                // The assistant's copy is written in code (it composes values), so it only follows a language
                // switch if it is told to — the same reason the settings page reloads here.
                case ChatPanel chat:
                    chat.Reload();
                    break;
            }
        }

        // Copy computed in code inside pages (headers, empty-state hints, button labels) must all be
        // recomputed.
        _workspace.Refresh();

        // Switching language is the second moment to warn about fonts — and the more accurate one:
        // the user has actively chosen Chinese, and only now will they actually see boxes. Do it
        // last — recompute the UI copy first, so when the user dismisses the dialog they see the
        // fully switched UI rather than a half-new, half-old one.
        CjkFontNotifier.NotifyIfNeeded(this);
    }

    private void OnNavigated(RadioButton button, string name)
    {
        if (button.IsChecked == true)
        {
            NavigateTo(name);
        }
    }

    private void WireWorkspace()
    {
        _workspace.Logged += line =>
        {
            ActivityLog.Text += line + Environment.NewLine;
            if (ActivityLog.Text is { Length: > 120000 } text)
            {
                ActivityLog.Text = text[^80000..];
            }

            // Moving the caret to the end is how we "scroll to bottom". Avalonia's TextBox has no
            // ScrollToEnd.
            ActivityLog.CaretIndex = ActivityLog.Text?.Length ?? 0;
        };

        _workspace.StatusChanged += text => Status.Text = text;

        // During an operation pages are disabled and cancel is enabled — matching WPF's
        // Pages.IsEnabled / CancelButton.IsEnabled.
        _workspace.BusyChanged += busy =>
        {
            PageHost.IsEnabled = !busy;
            CancelButton.IsEnabled = busy && _workspace.CanCancel;
        };

        _workspace.Failed += () => ShowLog(true);

        // Dialogs (e.g. "prebuilt library settings") can't reference the main window directly, so
        // page navigation is forwarded here through the workspace.
        _workspace.NavigateRequested += key =>
        {
            if (PageKeys.Contains(key))
            {
                NavigateTo(key);
            }
        };
    }

    /// <summary>
    /// The assistant page, for the shell self-check. Navigates to it first if needed (which also proves the
    /// lazy construction path works).
    /// </summary>
    internal ChatPanel OpenAssistant()
    {
        NavigateTo("Assistant");
        return _chatPanel!;
    }

    /// <summary>The conversation sidebar section, for the shell self-check.</summary>
    internal ChatSidebar ChatSidebarSection => _chatSidebar!;

    internal Button AssistantSearchButtonForCheck => AssistantSearchButton;
    internal Button AssistantNewButtonForCheck => AssistantNewButton;
    internal bool AssistantActionsRightmostForCheck
        => NavAssistant.Parent is Grid grid
           && AssistantSearchButton.Parent == grid
           && AssistantNewButton.Parent == grid
           && Grid.GetColumnSpan(NavAssistant) == grid.ColumnDefinitions.Count
           && Grid.GetColumn(AssistantSearchButton) == grid.ColumnDefinitions.Count - 2
           && Grid.GetColumn(AssistantNewButton) == grid.ColumnDefinitions.Count - 1;
    internal void ClickAssistantSearchForCheck()
        => AssistantSearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal void ClickAssistantNewForCheck()
        => AssistantNewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The title bar's title text as shown, for the shell self-check.</summary>
    internal string PageTitleText => PageTitle.Text ?? "";

    // ── Title bar, for the shell self-check ──
    /// <summary>The app draws its own title bar, so a self-check has to be able to read back that the client
    /// area really was extended (a box that is 32px tall over a 32px native caption strip would look identical
    /// in a screenshot and be wrong by one row).</summary>
    internal bool ClientAreaExtendedForCheck => ExtendClientAreaToDecorationsHint;
    /// <summary>Whether the Snap Layouts hit test really subclassed this window. False on a non-Windows host and
    /// also false if the platform handle was not a Win32 one, so it distinguishes "not this platform" from "the
    /// hook silently declined" — which no screenshot can.</summary>
    internal bool SnapHookInstalledForCheck => TitleBarSnapHook.InstalledForCheck;
    /// <summary><c>BorderOnly</c>, not the <c>Full</c> default: only <c>Full</c> makes Avalonia draw a title bar
    /// over the client area (Window.ComputeDecorationParts gates the TitleBar part on it), and that title bar
    /// wrote the window title straight over this app's own bar. BorderOnly keeps the shadow, the border and the
    /// resize grips, which this app does not draw itself. Whether the drawn decorations are really gone can only
    /// be seen in a shown window (the self-check never shows this one), so this pin exists to keep the property
    /// that selects the behaviour from drifting back to the default.</summary>
    internal WindowDecorations WindowDecorationsForCheck => WindowDecorations;
    internal double TitleBarHeightForCheck => TitleBar.Bounds.Height;
    internal double TitleBarHintForCheck => ExtendClientAreaTitleBarHeightHint;
    /// <summary>The platform's own caption-strip height. On Windows the three plates are centred in it, so a bar
    /// that disagrees makes the glyphs sit off the bar's centre line.</summary>
    internal double WindowDecorationTopForCheck => WindowDecorationMargin.Top;
    internal Thickness WindowDecorationMarginForCheck => WindowDecorationMargin;
    internal Thickness OffScreenMarginForCheck => OffScreenMargin;
    internal Rect WindowControlsBoundsForCheck => InClient(WindowControls);
    internal double MinimizeButtonHeightForCheck => MinimizeButton.Bounds.Height;
    internal string MaximizeTipForCheck => ToolTip.GetTip(MaximizeButton) as string ?? "";
    internal string CloseTipForCheck => ToolTip.GetTip(CloseButton) as string ?? "";
    internal bool MaximizeGlyphIsRestoreForCheck => ReferenceEquals(MaximizeGlyph.Data, _restoreGlyph);

    /// <summary>Whether the maximize plate is painted as pressed. The class is written by the Win32 hook rather
    /// than by a pseudo-class, so reading it back is the only way to tell "the press was seen" from "the press was
    /// swallowed": the two look identical in the object graph and differ only in these pixels.</summary>
    internal bool MaximizePressedForCheck => MaximizeButton.Classes.Contains("ncpressed");

    /// <summary>Whether a press is armed on the bar and has not yet become a drag. Exposed because the arming is
    /// the half of the protocol that cannot be driven to its end in a check (see
    /// <see cref="TitleBarDragExceeded"/>), so its start is asserted instead.</summary>
    internal bool TitleBarDragArmedForCheck => _titleBarPress is not null;

    /// <summary>Whether the bar's page title takes pointer input. It must not: the title is a label in the middle
    /// of the one press surface the window has, and a label that answers hit tests punches a dead hole in it —
    /// exactly where a reader would double-click to maximize.</summary>
    internal bool PageTitleHitTestableForCheck => PageTitle.IsHitTestVisible;

    private Geometry? _maximizeGlyph;
    private Geometry? _restoreGlyph;

    /// <summary>One of the title bar's two drawer plates, measured in the bar. Read by name so the check can
    /// compare the pair without this file exporting a member per button.</summary>
    internal (double Width, double Height) TitlebarTogglePlateForCheck(string name)
        => this.FindControl<Button>(name) is { } button
            ? (button.Bounds.Width, button.Bounds.Height)
            : default;

    /// <summary>Writes the window state the way the platform would, so the check can prove the maximise plate
    /// follows it. A check window is never actually maximised by the user, so the property is the only lever —
    /// and it is the same one the platform sets, which is what <c>SyncWindowStateChrome</c> listens to.</summary>
    internal void SetWindowStateForCheck(WindowState state) => WindowState = state;

    /// <summary>The bar carries no product name: the title block in the sidebar and the window's Title property
    /// (which the taskbar reads) are where "Axmol Hub" lives. A second copy up here is the chrome this change
    /// removed, so it is asserted rather than merely deleted.</summary>
    internal string TitleBarTextForCheck => string.Join("|", TitleBar.GetVisualDescendants()
        .OfType<TextBlock>()
        .Select(text => text.Text ?? "")
        .Where(text => text.Length > 0));

    /// <summary>Whether the ☰ drawer is the leading control of the bar: nothing interactive may sit left of it,
    /// or "the first position in the title bar" is no longer true.</summary>
    internal bool SidebarToggleIsLeftmostForCheck
    {
        get
        {
            var toggle = InClient(SidebarToggle).X;
            return !TitleBar.GetVisualDescendants()
                .OfType<Button>()
                .Any(button => button != SidebarToggle && button.IsVisible && InClient(button).X < toggle - 0.5);
        }
    }

    /// <summary>Whether the ▯ drawer ends where the window controls begin: its right edge must sit to the left of
    /// the caption plates' left edge, and close enough that it reads as "just before minimize".</summary>
    internal (double Gap, bool Before) InspectorToggleMeetsWindowControlsForCheck
    {
        get
        {
            var toggle = InClient(InspectorToggle);
            var plates = InClient(WindowControls);
            return (plates.X - toggle.Right, toggle.Right <= plates.X + 0.5);
        }
    }

    // ── Sidebar collapse, for the shell self-check ──
    internal bool SidebarExpandedForCheck => IsSidebarExpanded;
    internal double SidebarWidthForCheck => Sidebar.Width;
    internal bool SidebarClipsForCheck => Sidebar.ClipToBounds;
    internal bool ResizeGripVisibleForCheck => ResizeGrip.IsVisible;
    internal void ToggleSidebarForCheck()
        => SidebarToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The title block (sidebar top), for the shell self-check. <c>BrandHeaderHasLogoForCheck</c> is what
    /// keeps the brand mark from quietly disappearing again: it reads the image's own source and geometry rather
    /// than trusting that the element exists, and it requires the image to sit *before* the title text, which is
    /// what "the logo precedes the name" means.</summary>
    internal bool BrandHeaderHasLogoForCheck
    {
        get
        {
            var logo = InClient(BrandLogo);
            var title = InClient(BrandTitle);
            return BrandLogo.Source is not null
                   && BrandLogo.Stretch == Stretch.Uniform
                   && BrandLogo.Bounds.Width > 1
                   && Math.Abs(BrandLogo.Bounds.Width - BrandLogo.Bounds.Height) <= 1
                   && logo.Right <= title.X + 0.5;
        }
    }

    /// <summary>Why <see cref="BrandHeaderHasLogoForCheck"/> said no: the resolved source (or a marker for the
    /// state it is in), and the mark's own box. A bare bool cannot tell "the resource key is missing" from "the
    /// image has no size", and those are two different bugs.</summary>
    internal string BrandLogoReportForCheck
    {
        get
        {
            var source = BrandLogo.Source switch
            {
                null => "null",
                Avalonia.Media.Imaging.Bitmap bitmap => "bitmap " + bitmap.PixelSize,
                DrawingImage drawing => "drawing image (" + drawing.Drawing + ")",
                var other => other.GetType().Name,
            };
            var appHas = Application.Current?.TryGetResource("Hub.Image.Brand", null, out var value) == true
                ? value?.GetType().Name ?? "null"
                : "missing";
            return $"source={source}, app-resource={appHas}, "
                   + $"size={BrandLogo.Bounds.Width:0.#}x{BrandLogo.Bounds.Height:0.#}, "
                   + $"stretch={BrandLogo.Stretch}, "
                   + $"left-of-title={InClient(BrandLogo).Right <= InClient(BrandTitle).X + 0.5}";
        }
    }

    internal int BrandHeaderLinesForCheck
        => new[] { BrandTitle.Text, BrandVersion.Text }.Count(text => text is { Length: > 0 });

    internal string BrandTitleTextForCheck => BrandHeader.GetVisualDescendants()
        .OfType<TextBlock>()
        .Select(text => text.Text ?? "")
        .FirstOrDefault(text => text == "Axmol Hub") ?? "";

    internal string BrandVersionTextForCheck => BrandVersion.Text ?? "";
    internal bool PreviewBadgeVisibleForCheck => PreviewBadge.IsVisible;
    internal string PreviewBadgeTextForCheck
        => PreviewBadge.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
    internal bool PreviewBadgeUsesThemeTokensForCheck
        => ThemeProbe.IsToken(PreviewBadge.Background, "Hub.PreviewBadgeBackground")
           && ThemeProbe.IsToken(PreviewBadge.BorderBrush, "Hub.PreviewBadgeBorder")
           && PreviewBadge.GetVisualDescendants().OfType<TextBlock>()
               .Any(text => ThemeProbe.IsToken(text.Foreground, "Hub.PreviewBadgeForeground"));

    internal void SetPreviewBadgeVisibleForCheck(bool visible) => PreviewBadge.IsVisible = visible;

    /// <summary>
    /// The assistant's state, for the shell self-check to assert against — and to install a scripted chat
    /// client so the page can be verified with no network and no API key.
    /// </summary>
    internal ChatWorkspace Chat => _chat;

    /// <summary>The OS attention channel, for the shell self-check: the pending notification target has to be
    /// armed on the same object the production notification path arms it on, or the check proves nothing.</summary>
    internal SystemAttentionService AttentionForCheck => _attention;

    /// <summary>Drives the foreground transition a notification click is made of, for the self-check: the window a
    /// check run shows never really leaves the foreground, so both the return that consumes a pending conversation
    /// and the start-up activation that must not consume one can only be asserted through this. The argument is
    /// the state being tested — whether this window had left the foreground before coming back.</summary>
    internal void ReturnToForegroundForCheck(bool hadLeftForeground)
    {
        _foregroundHasLeftWindow = hadLeftForeground;
        OnWindowActivated();
    }

    /// <summary>The strip's background-reply counter, read as the user sees it.</summary>
    internal bool ChatRunsVisibleForCheck => ChatRuns.IsVisible;
    internal string ChatRunsTextForCheck => ChatRuns.Text ?? "";

    /// <summary>The strip row and the log button's own plate, measured while the repository slot is lit. Adding a
    /// sixth column to a 28-pixel row is only safe if the row did not grow and the square glyph did not become a
    /// rectangle, and neither of those is visible in an assertion that only reads strings.</summary>
    internal double StatusStripHeightForCheck => StatusStrip.Bounds.Height;
    internal double RepoIndicatorWidthForCheck => RepoIndicator.Bounds.Width;
    internal (double Width, double Height) LogTogglePlateForCheck => (LogToggle.Bounds.Width, LogToggle.Bounds.Height);
    internal double StatusWidthForCheck => Status.Bounds.Width;

    /// <summary>Whether the assistant page is the one on screen.</summary>
    internal bool AssistantVisible => _currentKey == "Assistant";

    /// <summary>Whether the window is in front of the user: focused, and not minimized. Read live rather than
    /// cached, because a cached copy could become stale across a data-root switch or a recreated window.</summary>
    private bool WindowVisibleNow
        => _windowVisibleOverride ?? (IsActive && WindowState != WindowState.Minimized);

    /// <summary>The visibility answer used to suppress redundant system notifications while the user is already
    /// looking at the session that needs approval.</summary>
    internal AttentionVisibility AttentionNow => new(WindowVisibleNow, AssistantVisible);

    /// <summary>Forces the desktop half of <see cref="AttentionNow"/> for the self-check; <c>null</c> hands the
    /// answer back to the platform. The suite must reset it — an override left behind would stop the notification
    /// gate from reading the desktop.</summary>
    internal void SetWindowVisibleForCheck(bool? visible) => _windowVisibleOverride = visible;

    /// <summary>The settings gear's tooltip, for the shell self-check: the button is icon-only, so the tip is
    /// the only place its name is spelled out.</summary>
    internal string SettingsLabel => ToolTip.GetTip(SettingsButton) as string ?? "";

    /// <summary>Raises the gear's click path as a real click would — the self-check walks the same code the
    /// user's click does instead of calling <see cref="NavigateTo"/> directly.</summary>
    internal void ClickSettingsGearForCheck() => SettingsButton.RaiseEvent(
        new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    /// <summary>The bottom text block's tooltip, for the shell self-check: the block's visible text names
    /// the machine, so the tip is where "this opens settings and appearance" is spelled out.</summary>
    internal string BottomMenuLabel => ToolTip.GetTip(BottomMenuButton) as string ?? "";

    /// <summary>Opens the bottom text block's popup the way the user's click does — the self-check walks
    /// the same code instead of calling <see cref="BuildBottomMenu"/> directly.</summary>
    internal void ClickBottomMenuForCheck() => BottomMenuButton.RaiseEvent(
        new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    /// <summary>The popup the bottom text block last attached, for the shell self-check.</summary>
    internal MenuFlyout? BottomMenuForCheck => FlyoutBase.GetAttachedFlyout(BottomMenuButton) as MenuFlyout;

    private void WireChrome()
    {
        // The download slot mirrors the queue. Marshalled because a transfer reports from whatever thread
        // its transport reports on, and the strip is owned by this one. Subscribed once — WireChrome only
        // runs from the constructor.
        TransferQueue.Instance.Changed += () => Dispatcher.UIThread.Post(UpdateTransferSlot);
        // The update's own phase is part of what the slot says — "ready" is the restart — and it moves
        // without the queue moving: a check that finds nothing new, a cancel, a channel switch.
        UpdateService.Instance.Changed += () => Dispatcher.UIThread.Post(UpdateTransferSlot);
        QueuePopup.PlacementTarget = TransferSlot;
        QueuePopup.Placement = PlacementMode.TopEdgeAlignedLeft;
        // The holder is installed once, for the reason recorded on _transferSlotTip: only its text moves.
        ToolTip.SetTip(TransferSlot, _transferSlotTip);
        // What a restart would cut short: a Hub operation, or replies still being written in the
        // background. Both are the user's work, and neither gets destroyed by one stray click.
        RestartInterruptsWork = () => _workspace.IsBusy || _chat.RunningCount > 0;
        TransferSlot.Click += (_, _) => OnTransferSlotClicked();
        UpdateTransferSlot();

        CancelButton.Click += (_, _) => _workspace.Cancel();

        CopyErrorButton.Click += async (_, _) =>
        {
            var text = _workspace.LastError.Length == 0 ? ActivityLog.Text ?? "" : _workspace.LastError;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        };

        OpenLogsButton.Click += (_, _) => _workspace.Open(_workspace.Log.Folder);

        // Closing is forbidden while an operation is running (handled in WPF's Closing), otherwise
        // child processes would be left behind.
        Closing += (_, e) =>
        {
            if (_workspace.IsBusy)
            {
                // An exclusive operation is mid-write and must not be left half-done: the close becomes a
                // cancel and the window stays. Unchanged from before the queue existed.
                e.Cancel = true;
                _workspace.Cancel();
                SetStatus(HubStrings.Get("Cancelling"));
                return;
            }

            if (_quitConfirmed || TransferQueue.Instance.ActiveCount == 0 || App.Options.IsAutomation)
            {
                return;
            }

            e.Cancel = true;
            _ = ConfirmQuitAsync();
        };

        Closed += (_, _) => _workspace.Dispose();
        Closed += (_, _) => _chat.Dispose();
        Closed += (_, _) => _attention.Dispose();
    }

    /// <summary>
    /// The conversation sidebar, built against the current chat workspace. It takes the preference store because
    /// which groups are folded is state the user left and expects back after a restart, and the status strip
    /// because re-pointing a workspace can fail on a fact about the disk — a folder that moved again while the
    /// dialog was open — and that answer belongs in the strip rather than in a card.
    /// </summary>
    private ChatSidebar BuildChatSidebar()
        => new(_chat, _preferencesStore, _preferences) { StatusReporter = SetStatus };

    /// <summary>
    /// Builds the conversation sidebar section, applies persisted sidebar state, and wires the collapse
    /// toggle, the drag grip and the log chevron.
    /// </summary>
    private void InitializeSidebar()
    {
        _chatSidebar = BuildChatSidebar();
        ChatSidebarHost.Content = _chatSidebar;

        SidebarToggle.Click += (_, _) => SetSidebarExpanded(!IsSidebarExpanded);
        ToolTip.SetTip(SidebarToggle, HubStrings.Get("SidebarToggleTip"));

        ResizeGrip.PointerPressed += OnGripPressed;
        ResizeGrip.PointerMoved += OnGripMoved;
        ResizeGrip.PointerReleased += OnGripReleased;

        LogToggle.Click += (_, _) => ShowLog(!LogBody.IsVisible);

        // Apply persisted state: width first, then collapsed so a collapsed sidebar really starts at zero.
        Sidebar.Width = Math.Clamp(_preferences.SidebarWidth, SidebarMin, SidebarMax);
        SetSidebarExpanded(!_preferences.SidebarCollapsed);
    }

    private bool IsSidebarExpanded => Sidebar.Width > 1;

    /// <summary>
    /// Wires the app's own title bar: dragging the window by its background, and the three window controls.
    ///
    /// The bar is the app's because <c>ExtendClientAreaToDecorationsHint</c> hands the platform's chrome to the
    /// client area — see the comment in MainWindow.axaml. The three controls are therefore not decorative: each
    /// one has to ask the same object the platform asked, so nothing here goes around <see cref="Window"/>.
    /// Close goes through <see cref="Window.Close()"/> rather than tearing anything down directly, which is what
    /// keeps the running-operation guard in <c>Closing</c> the *only* way out.
    /// </summary>
    private void InitializeTitleBar()
    {
        // The whole bar moves the window; the buttons sit above this layer and take their own input first.
        // BeginMoveDrag is not called here any more, and that is what makes the bar's second click reachable at
        // all: it enters the platform's own modal move loop, so a press that started one would spend the rest of
        // the double-click moving the window (see TitleBarDragThreshold).
        TitleBarDragArea.PointerPressed += OnTitleBarPressed;
        TitleBarDragArea.PointerMoved += OnTitleBarMoved;
        TitleBarDragArea.PointerReleased += OnTitleBarReleased;

        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => Close();

        // Windows 11 Snap Layouts on hover. Ours is a drawn button, so the system has to be told where it is
        // (see TitleBarSnapHook). It cannot be installed here: this runs from the constructor, and a window that
        // has never been shown has no Win32 handle to subclass yet — the self-check builds exactly such a window,
        // which is how that was caught. Installing on Opened is the first moment a handle is guaranteed; the call
        // is idempotent, so a window shown twice cannot install twice.
        //
        // The same hook also runs the plate's click: answering HTMAXBUTTON turns that point into a non-client
        // press, which Avalonia never sees as a click, so the flip is handed to it as the one thing both callers
        // do (and the glyph and the tooltip follow it through the window's own state change).
        Opened += (_, _) => TitleBarSnapHook.Install(this, MaximizeButton, ToggleMaximized);

        // The system keeps a few pixels of resize frame between the client area and the window's outer edge, and
        // that strip is exactly the gap a caption button leaves at the right end. The platform's own close button
        // is centred on the outer edge, so the app's has to reach it: the control strip is extended right by the
        // measured frame width. A negative margin rather than a wider plate, because the plate's own size is what
        // the 46x32 platform proportion is about - only its origin moves.
        SetCaptionStripInset();

        // The maximize plate's glyph and tooltip both say what the *next* click does, so they are rewritten
        // from the real state rather than from anything remembered here. Both glyphs are resolved once, from
        // this window's own resources, because these are looked up on every window-state change - and they must
        // be the 12x12 caption glyphs, not the 0-24 grid icons of the same name, which are drawn scaled.
        _maximizeGlyph = this.FindResource("Hub.Icon.CaptionMaximize") as Geometry;
        _restoreGlyph = this.FindResource("Hub.Icon.CaptionRestore") as Geometry;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) SyncWindowStateChrome();
        };
        SyncWindowStateChrome();
    }

    /// <summary>
    /// Flips the window between maximized and normal. One method for all three of its callers — the plate's own
    /// click, the plate's non-client click (see <see cref="TitleBarSnapHook"/>) and the bar's double-click —
    /// because the window state is what the glyph and the tooltip follow, and a second copy of the flip would be a
    /// second place for them to fall out of step.
    /// </summary>
    private void ToggleMaximized()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Whether a press on the bar means "maximize or restore" instead of "start moving the window". Pure, and
    /// internal, so the self-check can drive every click count: a mouse cannot be scripted, and this arm is
    /// exactly the one that used to fall through to the drag.
    /// </summary>
    internal static bool TitleBarPressToggles(int clickCount) => clickCount >= 2;

    /// <summary>How far the pointer has to travel from the press before the bar starts moving the window, in DIPs:
    /// the system's own caption threshold (<c>SM_CXDRAG</c>/<c>SM_CYDRAG</c> default to 4). It is not a
    /// preference — a platform move loop is modal, so beginning one on the press would swallow the second click of
    /// a double-click before anything could recognise it.</summary>
    private const double TitleBarDragThreshold = 4;

    /// <summary>The same decision for the other half of a press: has the pointer moved far enough to be a drag
    /// rather than a click. Pure for the same reason, and a check must never drive the arm that acts on it — that
    /// one calls <c>BeginMoveDrag</c> and would sit in the platform's move loop until a mouse moved.</summary>
    internal static bool TitleBarDragExceeded(Point origin, Point current)
        => Math.Abs(current.X - origin.X) >= TitleBarDragThreshold
           || Math.Abs(current.Y - origin.Y) >= TitleBarDragThreshold;

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Properties.IsLeftButtonPressed) return;

        // The second click of a double-click maximizes or restores, and it is decided before anything reads the
        // pointer: a toggle has no business asking the input pipeline where the cursor is, and keeping it that way
        // is what lets the self-check raise this press with a pointer of its own.
        if (TitleBarPressToggles(e.ClickCount))
        {
            _titleBarPress = null;
            e.Pointer.Capture(null);
            ToggleMaximized();
            return;
        }

        _titleBarPressOrigin = e.GetPosition(this);
        _titleBarPress = e;
        e.Pointer.Capture(TitleBarDragArea);
    }

    private void OnTitleBarMoved(object? sender, PointerEventArgs e)
    {
        if (_titleBarPress is not { } press) return;
        if (!TitleBarDragExceeded(_titleBarPressOrigin, e.GetPosition(this))) return;

        // Let go of the press before handing the platform its move loop: that loop runs until the button comes up
        // and nothing after this line in the handler may be relied on, so the state has to be settled first.
        _titleBarPress = null;
        e.Pointer.Capture(null);
        BeginMoveDrag(press);
    }

    private void OnTitleBarReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_titleBarPress is null) return;
        _titleBarPress = null;
        e.Pointer.Capture(null);
    }

    /// <summary>Moves the caption strip out to the window's real right edge (see <c>InitializeTitleBar</c>). The
    /// frame width comes from Win32 in physical pixels, so it is converted through the render scaling; on a
    /// non-Windows host, or where the frame is not a Win32 one, it is zero and nothing moves.</summary>
    private void SetCaptionStripInset()
    {
        var frame = TitleBarSnapHook.ResizeFrameWidth;
        if (frame <= 0) return;

        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        WindowControls.Margin = new Thickness(0, 0, -(frame / scaling), 0);
    }

    private void SyncWindowStateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        if ((maximized ? _restoreGlyph : _maximizeGlyph) is { } glyph) MaximizeGlyph.Data = glyph;
        ToolTip.SetTip(MaximizeButton, HubStrings.Get(maximized ? "RestoreWindow" : "MaximizeWindow"));
    }

    private void SetSidebarExpanded(bool expanded)
    {
        if (expanded)
        {
            var width = Sidebar.Width < SidebarMin ? _preferences.SidebarWidth : Sidebar.Width;
            Sidebar.Width = Math.Clamp(width, SidebarMin, SidebarMax);
            ResizeGrip.IsVisible = true;
        }
        else
        {
            Sidebar.Width = 0;
            ResizeGrip.IsVisible = false;
        }

        if (_preferences.SidebarCollapsed != !expanded)
        {
            _preferences.SidebarCollapsed = !expanded;
            _preferencesStore.Save(_preferences);
        }
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _grabbing = true;
        _grabStartX = e.GetPosition(this).X;
        _grabStartWidth = Sidebar.Width;
        e.Pointer.Capture(ResizeGrip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_grabbing) return;
        var width = _grabStartWidth + (e.GetPosition(this).X - _grabStartX);
        if (width < SidebarCollapseThreshold)
        {
            _grabbing = false;
            e.Pointer.Capture(null);
            SetSidebarExpanded(false);
            return;
        }
        Sidebar.Width = Math.Clamp(width, SidebarMin, SidebarMax);
    }

    private void OnGripReleased(object? sender, PointerEventArgs e)
    {
        if (!_grabbing) return;
        _grabbing = false;
        e.Pointer.Capture(null);

        if (Sidebar.Width >= SidebarCollapseThreshold)
        {
            _preferences.SidebarWidth = Math.Clamp(Sidebar.Width, SidebarMin, SidebarMax);
            _preferencesStore.Save(_preferences);
        }
    }

    /// <summary>Shows/hides the log body and flips the strip chevron.</summary>
    internal void ShowLog(bool show)
    {
        LogBody.IsVisible = show;
        LogToggleChevron.RenderTransform = new RotateTransform(show ? 180 : 0);
    }

    /// <summary>Updates the top-bar title for the current page.</summary>
    private void UpdatePageTitle()
    {
        PageTitle.Text = _currentKey switch
        {
            "Assistant" => _chat.ActiveConversation is { Title.Length: > 0 } active
                ? active.Title
                : HubStrings.Get("Assistant"),
            "Projects" => HubStrings.Get("Projects"),
            "Installs" => HubStrings.Get("Installs"),
            "Toolchains" => HubStrings.Get("Toolchains"),
            "Settings" => HubStrings.Get("Settings"),
            _ => "",
        };
    }

    private void InitializeChrome()    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        BrandVersion.Text = "v" + version;
        PreviewBadge.IsVisible = HubReleaseInfo.IsPrereleaseBuild;
        Title = "Axmol Hub " + BrandVersion.Text;

        // The settings gear is icon-only, so its tooltip is the only place the name "Settings" appears for it.
        // Set here rather than in ApplyLanguage so it is also in place at construction time.
        ToolTip.SetTip(SettingsButton, HubStrings.Get("Settings"));
        // Same rule for the two buttons at the title bar's right end, and this one is in InitializeChrome rather
        // than InitializeInspector because ApplyLanguage calls this: an icon-only control whose name lives only in
        // a tooltip has to be rewritten when the language changes. The drawer's sentence depends on which surface
        // is up, so that half goes through the sync that owns it rather than being written here — a language
        // switch while the pane is lifted has to leave it saying "back to the column", not "show or hide".
        ToolTip.SetTip(InspectorExpandToggle, HubStrings.Get("InspectorExpand"));
        SyncInspectorDrawerChrome();

        // The bottom text block's visible text names the machine, not the menu; the tooltip is where
        // "this opens settings and appearance" is spelled out. Same place for the same reason.
        ToolTip.SetTip(BottomMenuButton, HubStrings.Get("BottomMenuTip"));

        // The WPF version hard-codes "AXMOL 2.11 LTS" in the bottom-left. The Avalonia version
        // computes it from the **default engine** at runtime: hard-coding the version number would
        // become wrong the day v3 ships — exactly the A1 (single-engine version binding) class of bug.
        var state = _workspace.State;
        var engine = state.Engines.FirstOrDefault(candidate => candidate.Path == state.DefaultEnginePath);
        BrandLine.Text = engine is null ? "AXMOL" : "AXMOL " + engine.Version;

        // The WPF version hard-codes "Windows x64". The Avalonia version runs on three platforms and
        // computes it from the real host.
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        HostLine.Text = os + " " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Hands a directory to the system shell to open. Avalonia has no wrapper for WPF's
    /// <c>Process.Start(UseShellExecute)</c>, so just start the process directly. Failures go to
    /// the status bar instead of being thrown: clicking "open directory" shouldn't blow up the
    /// whole program.
    /// </summary>
    private void OpenFolder(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }
}
