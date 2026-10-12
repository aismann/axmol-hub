using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The WPF version put "service wiring + all operations" in <c>MainWindow.xaml.cs</c> (823 lines)
/// — because the four pages were four <c>Grid</c>s in one window, fields were naturally shared, and
/// a single <c>Refresh()</c> could update both the projects-page counts and the toolchains-page
/// table at once.
///
/// The Avalonia version splits pages into <c>UserControl</c>s, so those shared fields lost their
/// home. Hence the **non-visual half** is moved here wholesale: service wiring, current selection,
/// <c>ExecuteAsync</c>, and the operation behind every button. The visual half stays in XAML;
/// pages only bind controls to the properties and events here.
///
/// Deliberately does **not** pull in an MVVM framework: the state here is "selected project /
/// running operation / detected components" and the like, so notifying via events is shorter than
/// building a binding infrastructure, and it stays closer to the WPF <c>Refresh()</c> semantics
/// (full repaint when an operation ends).
/// </summary>
public sealed class HubWorkspace : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ProcessRunner _runner;
    /// <summary>The single entry point for Hub → engine command lines (build/run/deploy all go through it).</summary>
    private readonly EngineCommandLine _commandLine;
    /// <summary>Environment prep: runs the engine's own <c>setup.ps1</c> (Hub no longer downloads or installs any tools).</summary>
    private readonly EngineSetupService _setup;
    private readonly ProjectService _projects;
    private readonly PlatformBuildService _platformBuilds;
    /// <summary>Read-only toolchain probe inside the engine tree (source of truth = the engine's bundled build.profiles + tools/external).</summary>
    private readonly EngineToolchain _engineToolchain;
    /// <summary>Prebuilt library records (stored in Hub's data root, hashed by engine identity).</summary>
    private readonly EnginePrebuiltState _prebuiltState;
    /// <summary>Compiles the engine into a prebuilt library (<c>axmol-sdk</c>).</summary>
    private readonly EngineBuildService _engineBuild;
    private readonly PackageInstaller _installer;
    /// <summary>Shared download path for every package Hub fetches (engine zips and the host shell alike).</summary>
    private readonly DownloadManager _downloads;
    /// <summary>Installs the host's own PowerShell 7 — the one thing Hub puts outside the engine tree, and only
    /// because <c>setup.ps1</c> cannot run without it. Detection lives in <see cref="HostPowerShell"/>.</summary>
    private readonly HostPowerShellInstaller _hostShellInstaller;

    /// <summary>
    /// The downloads and the changes, in one list. The transfers deliberately do not go through
    /// <see cref="ExecuteAsync"/>: a transfer must not grey out the page it was started from, and two of
    /// them must not crowd each other out. The changes do, because the exclusive gate is now a slot in that
    /// list rather than a field this class held beside it. See <see cref="TaskQueue"/> for where the line
    /// between the two is drawn.
    /// </summary>
    private readonly TaskQueue _tasks = TaskQueue.Instance;
    /// <summary>
    /// The wasm preview Hub is serving, if any. A named field rather than an awaited call because that server
    /// outlives the request that started it, and the 运行 button has to find it again as 停止.
    /// </summary>
    private PlatformBuildService.WebPreview? _webPreview;
    /// <summary>
    /// The pid of a game Hub launched, per project path — and the only thing kept about a process it does not
    /// own. It answers two questions and nothing else: did it die on contact, and is it still holding the
    /// output directory a rebuild wants to write into.
    /// </summary>
    private readonly Dictionary<string, int> _launchedGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _engineIndexReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The engine version catalog. **Must be the same instance**: after the remote index is pulled
    /// it has to be stored on it. Newing one up on every call would drop the adopted result right
    /// away, leaving the list stuck at the built-in manifest.
    /// </summary>
    private EngineReleases _releases;

    /// <summary>Signing passwords live only in memory, valid for one session — same as the WPF version, never persisted.</summary>
    private readonly Dictionary<string, AndroidSigningPasswords> _androidPasswords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The device list is invalidated only when the "project + platform" combination changes, to avoid clearing it on every project selection.</summary>
    private string _deviceProject = "";

    /// <summary>Reentrancy gate for the target dropdown. The WPF version calls it <c>targetReady</c>; it does exactly the same job.</summary>
    private bool _targetReady;

    public HubWorkspace(string root, HubPreferences preferences, PreferencesStore preferencesStore)
    {
        Preferences = preferences;
        PreferencesStore = preferencesStore;

        Store = new StateStore(root);
        Directory.CreateDirectory(Store.Root);
        ToolsRoot = Path.Combine(Store.Root, "tools");
        Directory.CreateDirectory(ToolsRoot);
        Manifests = Path.Combine(AppContext.BaseDirectory, "manifests");

        State = Store.Load();

        Log = new HubLog(Path.Combine(Store.Root, "logs"));
        // HubLog's callbacks come from a **worker thread** (ProcessRunner's output pump). The WPF
        // version switches back to the UI thread with Dispatcher.BeginInvoke; here is the same.
        // Without this step, appending text to a TextBox throws a cross-thread exception.
        // The drawer is the one surface that shows every line; a task's own row is fed by that task's
        // process (see OwnLines), because a row that moved on somebody else's output was reporting a
        // stage the build never printed.
        Log.Written += line => Dispatcher.UIThread.Post(() => Logged?.Invoke(line));

        _runner = new ProcessRunner(Log.Write);
        _commandLine = new EngineCommandLine(_runner, Path.Combine(AppContext.BaseDirectory, "Invoke-Axmol.ps1"));
        _setup = new EngineSetupService(_commandLine);
        _prebuiltState = new EnginePrebuiltState(Store.Root);
        _engineBuild = new EngineBuildService(_runner, _commandLine, _prebuiltState);
        _projects = new ProjectService(_runner, _commandLine, _prebuiltState);
        _platformBuilds = new PlatformBuildService(_runner);
        _engineToolchain = new EngineToolchain(_runner);
        _downloads = new DownloadManager(_http, Log.Write);
        _installer = new PackageInstaller(_downloads, Store.Root, Log.Write);
        _hostShellInstaller = new HostPowerShellInstaller(_http, _runner, _downloads, Path.Combine(Store.Root, "cache"), Path.Combine(Store.Root, "logs"));
        // The host shell is a fact about this machine, not about any engine, so it is probed once here next to
        // the engine index. It is a filesystem walk: no process, no network, nothing to await.
        HostShell = HostPowerShell.Probe();
        // …but off Windows a filesystem walk cannot read a version, only find the binary. Complete that one
        // unknown in the background so a Mac or Linux user does not open the page to "found, unconfirmed" and
        // have to press a button to learn what is already knowable.
        CompleteHostShellVersionAsync();

        _releases = new EngineReleases(Store.Root, Manifests);
        RefreshEngineIndexAsync();

        // Stall recorder: when the user reports "window not responding", the log must show how long
        // and when it stalled. Always on, and only writes when genuinely stalled (2-second threshold
        // — sub-second jitter isn't worth logging).
        new UiStallWatch(Log.Write, TimeSpan.FromSeconds(2)).Start();

        ProjectDirectory = preferences.ProjectDirectory ?? Path.Combine(Store.Root, "projects");
        _targetReady = true;
        ToolTarget = BuildTargets.All[0];
    }

    /// <summary>
    /// Pulls the remote version index once at startup; on failure it keeps using the built-in
    /// manifest.
    ///
    /// **Not awaited**: the main window must come up immediately. The index only affects "which
    /// versions the engines page lists", and making the user wait on the network for optional
    /// information is unacceptable (offline it would wait until timeout). So this is
    /// fire-and-forget, and once done it repaints the UI via <see cref="Changed"/>.
    ///
    /// Timeout is hard-capped: <see cref="_http"/> is deliberately
    /// <see cref="Timeout.InfiniteTimeSpan"/> (gigabyte-scale downloads must be able to crawl), but
    /// a small request like the index must wrap its own, otherwise a half-dead connection could
    /// hang the background task forever.
    ///
    /// **The whole request runs on the thread pool**
    /// (<see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>). This isn't for
    /// parallelism — it's to move the **synchronous part** of the "first HTTP request" off the UI
    /// thread: on its first request `HttpClient` resolves the proxy (on Windows it asks WinINET;
    /// on corporate networks WPAD/auto-detection can take seconds) and does DNS. That synchronous
    /// code runs on the caller thread, and the caller is the UI thread — hence the "not responding"
    /// window. Locally it measures tens of milliseconds, but in proxied environments it can blow up
    /// to seconds, and none of it shows in the log.
    /// </summary>
    private async void RefreshEngineIndexAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var url = new Uri(EngineIndex.DefaultUrl);
            var result = await Task.Run(() => EngineIndex.FetchAsync(_http, url, timeout.Token), timeout.Token);
            _releases.Adopt(result);
            foreach (var problem in _releases.Problems) Log.Write("Engine index: " + problem);
            Log.Write(result.Manifest is null
                ? "Engine index unavailable; using the built-in manifest."
                : $"Engine index applied: {_releases.All().Count} release(s).");
        }
        catch (Exception ex)
        {
            // By contract EngineIndex.FetchAsync doesn't throw; and if it does, it still shouldn't
            // fail startup.
            Log.Write("Engine index failed: " + ex.Message);
        }

        _engineIndexReady.TrySetResult();

        // Background thread → UI thread. Touching controls cross-thread throws, so go back via
        // Dispatcher.
        Dispatcher.UIThread.Post(() => Refresh());
    }

    // ───────────────────────── Services and state ─────────────────────────

    public StateStore Store { get; }
    public HubState State { get; }
    public HubLog Log { get; }
    public HubPreferences Preferences { get; }
    public PreferencesStore PreferencesStore { get; }
    /// <summary>
    /// Hub's own data directory (download cache, Android packaging staging).
    /// **Not the toolchain root** — toolchains live in the engine tree
    /// (<c>&lt;engine&gt;/tools/external</c>), prepared by the engine's setup.ps1.
    /// </summary>
    public string ToolsRoot { get; }
    public string Manifests { get; }

    /// <summary>The dialogs' host window. Filled in by the main window after construction; when null, dialogs degrade to non-modal.</summary>
    public Window? Owner { get; set; }

    /// <summary>
    /// Silences failure dialogs in verification mode (<c>--verify-ops</c>).
    ///
    /// On the product path an operation failure **must** pop up — the user has to see it. But in
    /// automation no one clicks confirm, and the Task returned by
    /// <see cref="HubDialog.ShowAsync"/> never completes, turning "really running" into "hanging".
    /// It only silences **presentation**: busy state, logging, persistence, and
    /// <see cref="LastError"/> all stay unchanged, so verification still observes product behavior
    /// — just without the window that needs a human.
    /// </summary>
    public bool SuppressDialogs { get; set; }

    /// <summary>A single log line (already on the UI thread). The main window appends it to the log panel.</summary>
    public event Action<string>? Logged;

    /// <summary>State/lists changed and pages should repaint. Equivalent to the WPF version's trailing <c>Refresh()</c>.</summary>
    public event Action? Changed;

    /// <summary>Status bar text.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>An operation failed: the main window expands the log panel in response.</summary>
    public event Action? Failed;

    /// <summary>Toolchain detection results updated.</summary>
    public event Action? ComponentsChanged;

    /// <summary>The host PowerShell probe produced a new verdict (page card repaints).</summary>
    public event Action? HostShellChanged;

    /// <summary>Android device list updated.</summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// Requests a switch to a page (page keys match <c>MainWindow.PageKeys</c>). Subscribed by the
    /// main window. Dialogs can't reference the main window directly, so jumps like "go to the
    /// engines page to build" are forwarded through it.
    /// </summary>
    public event Action<string>? NavigateRequested;

    // ───────────────────────── Shared selection ─────────────────────────

    public ProjectEntry? SelectedProject { get; set; }
    public EngineEntry? SelectedEngine { get; set; }
    public EngineEntry? ToolchainEngine { get; set; }
    public AndroidDevice? SelectedDevice { get; set; }

    /// <summary>The default location in the new-project panel. The WPF version is <c>ProjectLocation.Text</c>.</summary>
    public string ProjectDirectory { get; set; }

    private BuildTarget? _toolTarget;
    public BuildTarget? ToolTarget
    {
        get => _toolTarget;
        set
        {
            _toolTarget = value;
            ToolTargetHint = DescribeToolTarget(value);
        }
    }

    public List<ToolchainComponent> Components { get; private set; } = [];

    /// <summary>
    /// The host's PowerShell 7, as last probed. <b>Deliberately never persisted</b>: <c>hub-state.json</c> is the
    /// engine and project registry, while pwsh can be installed or removed outside Hub between two frames, so a
    /// stored verdict would be a lie the next run. The same reasoning makes
    /// <see cref="EngineSetupService.IsPrepared"/> re-read the disk every time.
    /// </summary>
    public HostShellStatus HostShell { get; private set; } = new(HostShellState.Unknown);

    /// <summary>True between "Hub handed the bootstrap to a terminal window" and "the user pressed Re-check".
    /// The button becomes a re-check for exactly this stretch: the install is out of Hub's hands, and the only
    /// thing that can end the wait is a probe the user decides to run.</summary>
    public bool HostShellAwaitingTerminal { get; private set; }

    /// <summary>The installer, exposed only so the page can state which road it is about to take <b>before</b>
    /// asking for confirmation — the three roads cost different things (UAC, a sudo password, nothing Hub can do).</summary>
    public HostShellMethod HostShellPlan => _hostShellInstaller.Plan();

    public string HostShellConfirmationKey => _hostShellInstaller.ConfirmationKey();

    public string HostShellMethodKey(HostShellMethod method) => _hostShellInstaller.MethodKey(method);

    public IReadOnlyList<AndroidDevice> Devices { get; private set; } = [];
    public string LastError { get; private set; } = "";
    public bool IsBusy => _tasks.ActiveMutating is not null;
    public string StatusText { get; private set; } = "";

    /// <summary>The second line of the "recent build platform" card at the top of the projects page.</summary>
    public string BuildHostHint { get; private set; } = "";

    /// <summary>The caption below the platform selection on the toolchains page. The WPF version calls it <c>ToolTargetHint</c>.</summary>
    public string ToolTargetHint { get; private set; } = "";

    // ───────────────────────── Common shell ─────────────────────────

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(text);
    }

    private IProgress<DownloadProgress> DownloadProgress() => new Progress<DownloadProgress>(p =>
        SetStatus(string.Create(CultureInfo.InvariantCulture,
            $"{HubStrings.Get("Download")} {p.Bytes / 1048576.0:F1} / {(p.Total.HasValue ? (p.Total.Value / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) : "?")} MB · {p.BytesPerSecond / 1048576.0:F1} MB/s")));

    /// <summary>
    /// The full lifecycle of one gated change: the exclusive slot, the busy flag, the status line, the log,
    /// the state save on success, and the typed error dialog on failure. It runs as a
    /// <see cref="TaskQueue.TaskKind.Mutating"/> <see cref="TaskQueue"/> item, so "is Hub busy" has exactly one
    /// answer — the scheduler — instead of a field here that the list of running work knew nothing about.
    ///
    /// <para>The signature is unchanged because twenty call sites use it and none of them should have to care
    /// about scheduling. Second-scale work holds the gate without taking a row: a task that finished before
    /// the popover could open is noise, not information, and the page already shows its own result.</para>
    /// </summary>
    public Task ExecuteAsync(string title, Func<CancellationToken, Task> action)
        => RunGated(title, title, null, (_, token) => action(token), listed: false);

    /// <summary>
    /// The same lifecycle with a row, for the minute-scale work the user is entitled to watch and to cancel
    /// from the status strip rather than from the page it happened to be started on: a project build, an
    /// engine host build, the engine's setup script.
    /// </summary>
    private Task RunTaskAsync(string identity, string titleKey, string? subject,
        Func<TaskQueue.Item, CancellationToken, Task> work)
        => RunGated(identity, titleKey, subject, work, listed: true);

    /// <summary>
    /// The question an entry point asks <b>before</b> it shows a dialog it would then have to take back: is
    /// the exclusive gate taken? If it is, the press is answered with one sentence on the strip and in the log
    /// — no modal, because an action that did nothing does not get to cover the composer, and no grey page,
    /// because this round's whole point is that the surface stays live while work runs.
    /// </summary>
    public bool RefuseIfBusy()
    {
        if (_tasks.ActiveMutating is null)
        {
            return false;
        }

        var sentence = HubStrings.Get("DeepLinkBusy");
        SetStatus(sentence);
        Log.Write(sentence);
        return true;
    }

    private async Task RunGated(string identity, string titleKey, string? subject,
        Func<TaskQueue.Item, CancellationToken, Task> work, bool listed)
    {
        // A change does not queue behind another change: two of them interleaving can leave the disk in a
        // state no sentence describes. It is refused — out loud, in the one place every entry point asks.
        if (RefuseIfBusy())
        {
            return;
        }

        LastError = "";
        SetStatus(HubStrings.Get(titleKey));
        Log.Write(titleKey);

        var item = _tasks.Enqueue(identity, titleKey, subject, work,
            onFinished: settled => FinishGated(settled, titleKey), kind: TaskQueue.TaskKind.Mutating, listed: listed);
        if (item is null)
        {
            // Already running under this identity: the row on screen is the answer, and the repaint the first
            // one owed must not be skipped because the second request was turned away.
            Refresh();
            return;
        }

        // Nothing is announced here. The shell reads the queue for its cancel button and the strip's label, so
        // the one fact — 「a change is running」 — has one owner, and a work delegate that settles on the spot
        // cannot leave a flag standing after its own finisher has run.
        await item.Completion;
    }

    /// <summary>What a settled change leaves behind. Mirrors the success / cancel / failure arms the WPF
    /// port carried sentence-for-sentence, including the reason a failure keeps its exception <b>type</b>:
    /// <see cref="ShowOperationErrorAsync"/> chooses the advice the user can act on from that type.</summary>
    private void FinishGated(TaskQueue.Item item, string titleKey)
    {
        var title = HubStrings.Get(titleKey);
        if (item.Fault is { } error)
        {
            _ = ReportFailureAsync(error);
        }
        else if (item.State == TaskQueue.Phase.Done)
        {
            Store.Save(State);
            SetStatus(title + " · " + HubStrings.Get("Done"));
        }
        else
        {
            SetStatus(title + " · " + HubStrings.Get("Cancelled"));
            Log.Write(StatusText);
        }

        Refresh();
    }

    /// <summary>
    /// The one way a failure reaches the user: the status line, the log, the typed advice. Shared by the
    /// gated operations and by the launch path, which runs outside the gate and so has no <c>catch</c> above
    /// it — a refusal must not be able to land quieter than an operation.
    /// </summary>
    private async Task ReportFailureAsync(Exception error)
    {
        LastError = error.ToString();
        Log.Write(LastError);
        SetStatus(HubStrings.Get("ErrorHint") + " " + HubStrings.Get(error.Message.Split('\n')[0]));
        Failed?.Invoke();
        // In verification mode don't pop up: ExpectedFailure is the norm for this group (e.g.
        // importing an incomplete engine), and a dialog would make the await never return (see
        // SuppressDialogs).
        if (!SuppressDialogs)
        {
            await ShowOperationErrorAsync(error);
        }
    }

    public void Cancel()
    {
        if (_tasks.ActiveMutating is { } item)
        {
            _tasks.Cancel(item.Identity);
        }
    }

    /// <summary>Cancellable while a change is running (previously had to avoid non-interruptible steps like the MSVC installer; that path has been handed back to the engine).</summary>
    public bool CanCancel => _tasks.ActiveMutating is not null;

    /// <summary>
    /// Runs one download on <see cref="TaskQueue"/> instead of under the exclusive lock, and gives it
    /// the parts of <see cref="ExecuteAsync"/>'s lifecycle that still apply: a log line for the title, a
    /// state save and a page refresh when it lands, and the typed error dialog when it does not.
    ///
    /// What it deliberately does **not** get is the busy flag and the status line. A download that
    /// disables the page it was started from is the exact thing this queue exists to stop, and its
    /// progress now belongs to its own row rather than to the one sentence the strip can say.
    /// </summary>
    /// <returns>A task that completes when the transfer settles — the same thing
    /// <see cref="ExecuteAsync"/> awaited, so a caller that needs "is it installed yet" (the deep link,
    /// the version picker) keeps meaning it. Null from the queue means "already running"; that is not an
    /// error, and the row already on screen is the answer.</returns>
    private Task EnqueueTask(
        string identity,
        string titleKey,
        string? subject,
        string logTitleKey,
        Func<TaskQueue.Item, CancellationToken, Task> work)
    {
        var item = _tasks.Enqueue(identity, titleKey, subject, work,
            onFinished: finished => FinishTask(finished, logTitleKey));
        if (item is null)
        {
            return Task.CompletedTask;
        }

        Log.Write(logTitleKey);
        return item.Completion;
    }

    /// <summary>What a settled transfer leaves behind. Mirrors the success / cancel / failure arms of
    /// <see cref="ExecuteAsync"/> one-for-one, including the reason a failure keeps its exception **type**:
    /// <see cref="ShowOperationErrorAsync"/> chooses the advice the user can act on from that type.</summary>
    private void FinishTask(TaskQueue.Item item, string logTitleKey)
    {
        var title = HubStrings.Get(logTitleKey);
        if (item.Fault is { } error)
        {
            LastError = error.ToString();
            Log.Write(LastError);
            Failed?.Invoke();
            if (!SuppressDialogs)
            {
                _ = ShowOperationErrorAsync(error);
            }
        }
        else if (item.State == TaskQueue.Phase.Done)
        {
            Store.Save(State);
            Log.Write(title + " · " + HubStrings.Get("Done"));
        }
        else
        {
            Log.Write(title + " · " + HubStrings.Get("Cancelled"));
        }

        Refresh();
    }

    /// <summary>Wraps a queue row as the byte reporter a Core transport expects. The row is what carries
    /// the identity that this used to throw away into one status sentence.</summary>
    private static IProgress<DownloadProgress> ByteProgress(TaskQueue.Item item)
        => new Progress<DownloadProgress>(item.ReportBytes);

    private async Task ShowOperationErrorAsync(Exception error)
    {
        // The dialog shows only the reason the user can act on; the full stack trace stays in the log.
        var message = error switch
        {
            PrebuiltUnavailableException prebuilt =>
                HubStrings.Get("PrebuiltUnavailable") + "\n\n"
                + string.Format(HubStrings.Get("PrebuiltUnavailableFormat"), prebuilt.TargetName, prebuilt.Configuration) + "\n"
                + HubStrings.Get(prebuilt.Availability.TextKey) + "\n\n"
                + HubStrings.Get("PrebuiltUnavailableAction"),
            ProjectDestinationExistsException exists =>
                HubStrings.Get("ProjectAlreadyExists") + "\n\n" + exists.Destination + "\n\n" + HubStrings.Get("ProjectAlreadyExistsAction"),
            HostShellInstallException shell => HostShellErrorText(shell),
            _ => HubStrings.Get(error.Message) + "\n\n" + HubStrings.Get("ErrorHint"),
        };

        await HubDialog.ShowAsync(Owner, HubStrings.Get("OperationFailed"), message);
    }

    /// <summary>
    /// The host-shell failure text, assembled from the verdict instead of a message string. Two of its branches
    /// are the reason it needs a type of its own: a declined elevation is retried by pressing the same button,
    /// while "no terminal on this host" has exactly one answer — the engine's command, printed so it can be
    /// copied. Neither is expressible through the generic <c>HubStrings.Get(ex.Message)</c> path, and showing the
    /// command is the difference between Hub admitting a limit and pretending an install failed.
    /// </summary>
    private static string HostShellErrorText(HostShellInstallException error)
    {
        var sentence = string.Format(HubStrings.Get(error.TextKey), HostPowerShellInstaller.BootstrapCommand);
        var technical = error.Result.Outcome == HostShellInstallOutcome.NoTerminal ? "" : error.Result.Detail;
        return sentence + "\n\n" + error.Status.Describe()
               + (technical.Length > 0 ? "\n" + technical : "")
               + "\n\n" + HubStrings.Get("ErrorHint");
    }

    /// <summary>
    /// A line sink for one task's <b>own</b> process. The pump that calls it is a worker thread and the row is
    /// data the shell reads on the UI thread, so the reading is posted rather than applied there — the same
    /// pairing the log above needs for its TextBox, and for the same reason.
    /// </summary>
    private Action<string> OwnLines(TaskQueue.Item item) => line => Dispatcher.UIThread.Post(() =>
        item.ApplyProgress(TaskQueue.TaskFeed.Parse(line)));

    /// <summary>
    /// The WPF version's <c>Refresh()</c>: after an operation ends, recompute all lists and derived
    /// copy. Deliberately keeps "full repaint" over incremental notification — these lists are
    /// tiny, and the correspondences incremental notification must maintain (who depends on whose
    /// selection) cost far more than the repaint it saves.
    /// </summary>
    public void Refresh()
    {
        var selectedProject = SelectedProject;
        var selectedEngine = SelectedEngine;
        var moduleEngine = ToolchainEngine;

        SelectedProject = State.Projects.FirstOrDefault(p => selectedProject is not null && p.Path == selectedProject.Path)
                          ?? State.Projects.FirstOrDefault();
        SelectedEngine = State.Engines.FirstOrDefault(e => selectedEngine is not null && e.Path == selectedEngine.Path)
                         ?? State.Engines.FirstOrDefault();
        ToolchainEngine = State.Engines.FirstOrDefault(e => moduleEngine is not null && e.Path == moduleEngine.Path)
                       ?? SelectedEngine;

        SyncProjectTarget();
        UpdateTargetHint();
        UpdateDevicePicker();
        Changed?.Invoke();
    }

    /// <summary>The "recent build platform" card at the top of the projects page. In WPF it's maintained by <c>SyncProjectTarget</c>.</summary>
    public BuildTarget? ProjectTarget =>
        SelectedProject is { } project ? BuildTargets.Get(project.Platform) : null;

    private void SyncProjectTarget()
    {
        if (!_targetReady)
        {
            return;
        }

        _targetReady = false;
        var project = SelectedProject;
        if (project is not null)
        {
            if (project.Path + "|" + project.Platform != _deviceProject)
            {
                _deviceProject = project.Path + "|" + project.Platform;
                Devices = [];
            }
        }

        _targetReady = true;
    }

    private void UpdateTargetHint()
    {
        if (ProjectTarget is { } target)
        {
            var project = SelectedProject;
            BuildHostHint = (project?.Configuration ?? "Debug") + " · "
                + (target.Family == "uwp" ? HubStrings.Get("UwpPending")
                   : target.CanBuildOn(BuildTargets.Host) ? HubStrings.Get("LocalHost")
                   : HubStrings.Get("RequiresHost") + " " + string.Join(" / ", target.Hosts));
        }
        else
        {
            BuildHostHint = "";
        }

        if (ToolTarget is { } toolsTarget)
        {
            ToolTargetHint = DescribeToolTarget(toolsTarget);
        }
    }

    private string DescribeToolTarget(BuildTarget? target) => target is null
        ? ""
        : HubStrings.Get("RequiresHost") + " " + string.Join(" / ", target.Hosts) + " · " + HubStrings.Get(
            target.Family == "android" ? "AndroidNativeNote"
            : target.Family == "uwp" ? "UwpPending"
            : "PlatformToolsNote");

    /// <summary>Equivalent of the WPF <c>SetHeaders</c>: header copy follows the language, so it is reset on every refresh.</summary>
    public static string[] GridHeaders(string grid) => grid switch
    {
        "projects" => ["Name", "Version", "Scripting", "BuildStatus", "LastOpened"],
        "engines" => ["Version", "Channel", "Path"],
        "tools" => ["Component", "Status", "Details"],
        _ => [],
    };

    // ───────────────────────── Engines ─────────────────────────

    public async Task ImportEngineAsync(string? path)
    {
        if (path is null)
        {
            return;
        }

        await ExecuteAsync("Import engine", _ =>
        {
            AddEngine(StateStore.ValidateEngine(path));
            return Task.CompletedTask;
        });
    }

    public async Task ChooseAndInstallEngineAsync()
    {
        // No busy guard here any more: an engine download is a transfer, and a transfer does not own the
        // app. The picker is the place that stops asking for a version that is already arriving.
        // Pre-select the currently selected engine version: when installing the same version twice,
        // the second time at least doesn't require hunting from scratch.
        var preferred = SelectedEngine?.Version;
        var release = await EngineVersionDialog.PickAsync(Owner, _releases, preferred, Preferences.DownloadSource,
            engine => EngineIsTransferring(engine.Version, engine.Channel));
        if (release is null)
        {
            return;
        }

        await InstallEngineAsync(release.Version);
    }

    /// <summary>The installable official engine versions. Remote index first; the built-in manifest when the pull fails.</summary>
    public EngineReleases Releases() => _releases;

    /// <summary>
    /// Installs the specified official engine version. When <paramref name="version"/> is null it
    /// picks the latest LTS in the manifest — the CLI and verification programs follow this default
    /// path, while the interactive UI lets the user pick first.
    ///
    /// This is a download, so it runs on <see cref="TaskQueue"/> rather than under the exclusive
    /// lock: the returned task still completes when the transfer settles (so <c>await</c> means what it
    /// meant), but the page stays live while it runs and a second engine can arrive alongside it.
    /// </summary>
    public Task InstallEngineAsync(string? version = null, string? downloadSourceOverride = null)
    {
        if (downloadSourceOverride is not null && !DownloadSources.IsKnown(downloadSourceOverride))
        {
            throw new InvalidOperationException("The requested engine download source is not supported.");
        }

        return EnqueueTask(
            EngineInstallIdentity(version),
            "TaskEnginePackage",
            version,
            "Install official engine",
            async (item, token) =>
            {
                // A website link names a version from the current online catalog. Wait until the
                // startup refresh has either adopted it or definitively fallen back to the built-in list.
                if (downloadSourceOverride is not null)
                {
                    await _engineIndexReady.Task;
                }

                var catalog = Releases();
                // A mistyped version must stop here: the manifest has id/url/sha256, and picking an
                // arbitrary "closest" one means downloading an engine the user didn't ask for — which
                // Hub wouldn't discover until the build fails.
                var release = version is null ? catalog.LatestLts()
                    : catalog.Find(version) ?? throw new InvalidOperationException(
                        HubStrings.Language == HubTexts.ChineseLanguage
                            ? $"清单里没有 Axmol {version} 这个可安装版本。"
                            : $"Axmol {version} is not an installable release in the manifest.");

                var package = WithDownloadSource(release.Package, downloadSourceOverride);
                var path = await _installer.InstallAsync(package, ByteProgress(item), token, item.MarkFinalizing);
                AddEngine(StateStore.ValidateEngine(path, package.Channel));
            });
    }

    /// <summary>
    /// The queue identity of an engine install, keyed on the version that was asked for so asking twice
    /// cannot fetch twice. The no-version default path shares one identity, which is the conservative
    /// reading: a second "get me the latest" is refused while the first is still arriving rather than
    /// racing it for the same bytes.
    /// </summary>
    internal static string EngineInstallIdentity(string? version) => $"engine:install:{version ?? "latest"}";

    /// <summary>
    /// Points a manifest package at the configured download source.
    ///
    /// The rewrite happens **here and only here**: the manifest (and the remote index) stays the
    /// description of *what* an engine is, while "where it comes from" is a user choice applied at
    /// download time. The digest is untouched — a mirror that serves different bytes must fail.
    /// </summary>
    private PackageEntry WithDownloadSource(PackageEntry package, string? sourceOverride = null)
    {
        var source = sourceOverride ?? Preferences.DownloadSource;
        var custom = Preferences.CustomDownloadSource;
        if (!DownloadSources.IsKnown(source)) throw new InvalidOperationException("The configured engine download source is not supported.");
        // Invalid custom input is rejected before a single byte is downloaded: silently falling back
        // to GitHub would look exactly like "the mirror worked".
        if (DownloadSources.Validate(source, custom) is { } problem) throw new InvalidOperationException(problem);

        var resolved = DownloadSources.Apply(package, source, custom);
        if (!ReferenceEquals(resolved, package)) Log.Write($"Download source '{source}': {resolved.Url}");
        return resolved;
    }

    private void AddEngine(EngineEntry engine)
    {
        if (!State.Engines.Any(e => e.Path.Equals(engine.Path, StringComparison.OrdinalIgnoreCase)))
        {
            State.Engines.Add(engine);
        }

        State.DefaultEnginePath ??= engine.Path;
    }

    public async Task SetDefaultEngineAsync() => await ExecuteAsync("Set default engine", _ =>
    {
        var engine = RequiredEngine();
        State.DefaultEnginePath = engine.Path;
        return Task.CompletedTask;
    });

    public async Task VerifyEngineAsync() => await ExecuteAsync("Verify engine", _ =>
    {
        var engine = RequiredEngine();
        if (StateStore.ValidateEngine(engine.Path).Version != engine.Version)
        {
            throw new InvalidDataException("Engine version changed.");
        }

        return Task.CompletedTask;
    });

    public async Task RemoveEngineAsync() => await ExecuteAsync("Remove engine from list", _ =>
    {
        var engine = RequiredEngine();
        State.Engines.Remove(engine);
        if (State.DefaultEnginePath == engine.Path)
        {
            State.DefaultEnginePath = State.Engines.FirstOrDefault()?.Path;
        }

        return Task.CompletedTask;
    });

    private PackageEntry ManagedEnginePackage(EngineEntry engine)
    {
        var package = PackageManifest.Read(Path.Combine(Manifests, "engine-manifest.json")).Packages
            .SingleOrDefault(p => p.Version == engine.Version && p.Channel == engine.Channel &&
                PackageInstaller.SafePath(Store.Root, p.Destination).Equals(engine.Path, StringComparison.OrdinalIgnoreCase));

        return package ?? throw new InvalidOperationException(HubStrings.Language == HubTexts.ChineseLanguage
            ? "导入的外部引擎保持原样，只支持修复或卸载 Hub 安装的引擎。"
            : "Only Hub-installed engines can be repaired or uninstalled. Imported folders are preserved.");
    }

    /// <summary>
    /// Re-downloads a Hub-installed engine over the current one, keeping the previous install as a
    /// backup.
    ///
    /// Both the engine lookup and the confirmation now happen **before** anything is queued. The worker
    /// has no window of its own to hold a dialog against, and a modal that surfaces after the user has
    /// moved on to another page is the thing this queue exists to stop.
    /// </summary>
    public async Task RepairEngineAsync()
    {
        EngineEntry engine;
        try
        {
            engine = RequiredEngine();
            RequireEngineAtRest(engine);
        }
        catch (Exception ex)
        {
            // The report the locked action used to give: logged, the drawer open, the typed dialog — not
            // an exception escaping into a button handler that has no catcher.
            LastError = ex.ToString();
            Log.Write(LastError);
            Failed?.Invoke();
            if (!SuppressDialogs)
            {
                await ShowOperationErrorAsync(ex);
            }

            return;
        }

        var prompt = string.Format(HubStrings.Get("RepairPrompt"), engine.Version);
        if (await HubDialog.ShowAsync(Owner, HubStrings.Get("Repair"), prompt, HubDialogButtons.OkCancel, danger: true) != HubDialogResult.Ok)
        {
            return;
        }

        await EnqueueTask(
            EngineRepairIdentity(engine.Version, engine.Channel),
            "TaskEnginePackage",
            engine.Version,
            "Repair engine",
            async (item, token) =>
            {
                await _installer.RepairAsync(WithDownloadSource(ManagedEnginePackage(engine)), ByteProgress(item), token, item.MarkFinalizing);
                StateStore.ValidateEngine(engine.Path, engine.Channel);
                foreach (var project in State.Projects.Where(p => p.Version == engine.Version && p.Channel == engine.Channel))
                {
                    project.BuildStatus = "Not built";
                }
            });
    }

    /// <summary>Repairs are keyed per engine, so a second click on the same engine cannot fetch it twice
    /// while the first is still arriving.</summary>
    internal static string EngineRepairIdentity(string version, string channel) => $"engine:repair:{version}:{channel}";

    /// <summary>
    /// Whether a transfer is writing this engine's own directory right now — an install of the same
    /// version, or a repair of it.
    ///
    /// The exclusive operation lock used to keep this from happening by accident: nothing else could run
    /// while a download did. Taking downloads off that lock was the point, so the rule has to be said on
    /// purpose instead — an engine is not a stable thing to uninstall, repair, or build against until the
    /// transfer that is replacing it has settled.
    /// </summary>
    private bool EngineIsTransferring(string version, string channel)
        => _tasks.FindActive(EngineInstallIdentity(version)) is not null
           || _tasks.FindActive(EngineRepairIdentity(version, channel)) is not null;

    /// <summary>The answer every engine operation gives when its own download has not landed yet.</summary>
    private void RequireEngineAtRest(EngineEntry engine)
    {
        if (EngineIsTransferring(engine.Version, engine.Channel))
        {
            throw new InvalidOperationException(HubStrings.Get("EngineBusyTransferring"));
        }
    }

    /// <summary>
    /// Delete a managed engine directory. The gate is asked and the question is put <b>before</b> the work is
    /// taken up: an uninstall is the one click that cannot be undone, so it must never be a dialog the user
    /// answers and then watches get refused — and it must keep <see cref="RequireEngineAtRest"/> as the guard
    /// against deleting a tree a transfer is still writing into.
    /// </summary>
    public async Task UninstallEngineAsync()
    {
        if (RefuseIfBusy())
        {
            return;
        }

        EngineEntry engine;
        PackageEntry package;
        try
        {
            engine = RequiredEngine();
            RequireEngineAtRest(engine);
            package = ManagedEnginePackage(engine);
            if (State.Projects.Any(p => p.Version == engine.Version && p.Channel == engine.Channel))
            {
                throw new InvalidOperationException(HubStrings.Language == HubTexts.ChineseLanguage
                    ? "仍有项目使用此引擎，请先移出项目列表。项目文件会保留。"
                    : "Projects still use this engine. Remove them from the list first; project files are preserved.");
            }

            var prompt = string.Format(HubStrings.Get("UninstallPrompt"), engine.Version);
            if (await HubDialog.ShowAsync(Owner, HubStrings.Get("Uninstall"), prompt, HubDialogButtons.OkCancel, danger: true) != HubDialogResult.Ok)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(ex);
            return;
        }

        // The directory goes and the bookkeeping follows, so the gated body needs nothing but the package it
        // was handed: every question that could change the answer has already been answered above.
        await ExecuteAsync("Uninstall engine", _ =>
        {
            _installer.Uninstall(package);
            State.Engines.Remove(engine);
            if (State.DefaultEnginePath == engine.Path)
            {
                State.DefaultEnginePath = State.Engines.FirstOrDefault()?.Path;
            }

            return Task.CompletedTask;
        });
    }

    private EngineEntry RequiredEngine() => SelectedEngine
        ?? throw new InvalidOperationException("Select an engine first.");

    // ───────────────────────── Engine mirror ─────────────────────────

    /// <summary>
    /// The mirror an engine currently uses, in the form the status line prints.
    ///
    /// Not a Hub setting: the value lives in the engine tree (<c>1k/.env</c> or <c>1k/.gitee</c>),
    /// so it travels with the engine and survives reinstalling Hub. An engine Hub can't map to
    /// either mechanism reports that instead of guessing.
    /// </summary>
    public string MirrorOf(EngineEntry? engine)
    {
        if (engine is null) return HubStrings.Get("MirrorNoEngine");
        return EngineMirror.IsSupported(engine) ? EngineMirror.Current(engine) : HubStrings.Get("MirrorUnknown");
    }

    /// <summary>The mirrors this engine offers; empty when the tree is neither v2 nor v3.</summary>
    public IReadOnlyList<MirrorOption> MirrorOptionsOf(EngineEntry? engine)
        => engine is null ? [] : EngineMirror.Options(engine);

    /// <summary>
    /// Writes the mirror into the engine tree. Nothing is re-downloaded here: the engine reads the
    /// setting the next time it fetches (setup / configure / build), which is exactly why the dialog
    /// says so.
    /// </summary>
    public async Task ApplyEngineMirrorAsync(string mirror) => await ExecuteAsync("SwitchEngineMirror", _ =>
    {
        var engine = RequiredEngine();
        EngineMirror.Apply(engine, mirror);
        Log.Write($"Engine mirror: {engine.Path} -> {mirror} (now {EngineMirror.Current(engine)})");
        return Task.CompletedTask;
    });

    // ───────────────────────── Projects ─────────────────────────

    public async Task CreateProjectAsync(string name, string parent, EngineEntry? engine, string projectType, bool usePrebuilt = false)
    {
        await ExecuteAsync("Create project", async token =>
        {
            var target = engine ?? throw new InvalidOperationException("Install or import an engine first.");
            var project = await _projects.CreateAsync(name.Trim(), parent.Trim(), target, token, projectType);
            if (usePrebuilt)
            {
                // Per-project options are written to a standalone file inside the project directory (same family as AndroidReleaseSettings).
                new PrebuiltSettings { Enabled = true }.Save(project);
            }

            State.Projects.Add(project);
            SelectedProject = project;
        });
    }

    public async Task OpenProjectAsync(string? path)
    {
        if (path is null)
        {
            return;
        }

        await ExecuteAsync("Open project", _ =>
        {
            var project = StateStore.ReadProject(path);
            if (!File.Exists(StateStore.MetadataPath(path)))
            {
                StateStore.LockProject(project);
            }

            if (!State.Projects.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                State.Projects.Add(project);
            }

            return Task.CompletedTask;
        });
    }

    public async Task RemoveProjectAsync() => await ExecuteAsync("Remove project from list", _ =>
    {
        var project = RequiredProject();
        State.Projects.Remove(project);
        return Task.CompletedTask;
    });

    private ProjectEntry RequiredProject() => SelectedProject
        ?? throw new InvalidOperationException("Select a project first.");

    private EngineEntry RequiredEngine(ProjectEntry project)
    {
        var engine = State.Engines
            .FirstOrDefault(e => e.Version == project.Version && e.Channel == project.Channel)
            ?? throw new InvalidOperationException(HubStrings.Get("EngineMissing"));
        // Every caller of this is about to read the engine tree — a build, a run, a packaging dialog — and
        // a repair swaps that tree underneath them while it lands. Same at-rest rule as the destructive
        // engine operations, stated here so no caller has to remember it.
        RequireEngineAtRest(engine);
        return engine;
    }

    /// <summary>
    /// Entry point for build/run. When <paramref name="selection"/> is null it first pops the
    /// "select build platform" dialog (corresponding to WPF's <c>PickBuildTarget</c>); when a
    /// selection already exists it runs directly.
    /// </summary>
    public async Task BuildAsync(bool configureOnly, (BuildTarget Target, string Configuration)? selection = null)
    {
        if (RefuseIfBusy())
        {
            return;
        }

        if (SelectedProject is not { } selected)
        {
            return;
        }

        // A running game holds its own output directory open, so a rebuild started now would fail in a minute
        // with a Windows sharing-conflict sentence from inside the compiler. Hub launched that process, so it
        // says which file is really the reason — and says it before asking for a target, not after the user
        // has answered a dialog for nothing.
        if (_launchedGames.TryGetValue(selected.Path, out var gamePid) && ProcessRunner.IsRunning(gamePid))
        {
            await ReportFailureAsync(new InvalidOperationException("ProjectStillRunning"));
            return;
        }

        var chosen = selection ?? await BuildTargetDialog.PickAsync(Owner, selected);
        if (chosen is null)
        {
            return;
        }

        var (target, configuration) = chosen.Value;

        // Android Release must first obtain the signing configuration, otherwise the output is an unsigned package.
        if (target.Family == "android" && configuration == "Release" && !await EditAndroidReleaseAsync(selected))
        {
            return;
        }

        // A build is the one thing the user waits on with the app open, so it takes a row: an identity that
        // says which project and which target, and a title that reads the same way in the popover as the
        // status line used to.
        await RunTaskAsync($"project:build:{selected.Name}:{target.Id}",
            configureOnly ? "Configure CMake" : "Build project",
            selected.Name + " · " + target.Name,
            async (task, token) =>
        {
            var project = RequiredProject();
            BuildTargets.Select(project, target.Id, configuration);
            var engine = RequiredEngine(project);

            if (project.Platform == "windows-x64")
            {
                UpdateTools(await DetectAsync(token));
            }

            project.BuildStatus = configureOnly ? "Configuring" : "Building";
            Store.Save(State);

            try
            {
                _androidPasswords.TryGetValue(project.Path, out var passwords);
                await _projects.BuildAsync(project, engine, configureOnly, token, passwords, OwnLines(task));
                if (!configureOnly)
                {
                    _projects.FindExecutable(project);
                }

                project.BuildStatus = configureOnly ? "Configured" : "Succeeded";
            }
            catch (OperationCanceledException)
            {
                project.BuildStatus = "Cancelled";
                throw;
            }
            catch
            {
                project.BuildStatus = "Failed";
                throw;
            }
            finally
            {
                Store.Save(State);
            }

            if (configureOnly)
            {
                return;
            }

            // No completion dialog: the row already says 完成 and the drawer holds the detail — a modal that
            // appears after minutes of waiting is the same shape this round deleted the progress window for.
            // Opening the output directory stays, because that is the next thing the user does with a build,
            // and it is written to the log so 看日志 leads somewhere with the path in it.
            var output = BuildOutputDirectory(project);
            Log.Write(output);
            _runner.Open(output);
        });
    }

    /// <summary>
    /// Launch the game. This is neither a task nor an operation: no row, no gate, nothing to cancel, no exit
    /// code to report. The ruling was 「Hub 不做调试器」 — Hub starts the artifact and lets go — so a game left
    /// running for an hour must not grey out a page, hold the exclusive slot, or turn "the user closed the
    /// window" into an error dialog. Two things stay Hub's own failures: the process that never came up, and
    /// the one that died on contact.
    /// </summary>
    public async Task RunAsync()
    {
        if (_webPreview is not null)
        {
            StopWebPreview();
            return;
        }

        try
        {
            var project = RequiredProject();
            if (project.BuildStatus != "Succeeded")
            {
                throw new InvalidOperationException("Build successfully before Run.");
            }

            var family = BuildTargets.Get(project.Platform).Family;
            if (family == "windows")
            {
                UpdateTools(await DetectAsync(CancellationToken.None));
            }
            else if (family == "android" && SelectedDevice?.State != "device")
            {
                throw new InvalidOperationException(HubStrings.Get("SelectAndroidDevice"));
            }

            project.LastOpened = DateTimeOffset.Now;
            Store.Save(State);
            var engine = RequiredEngine(project);

            if (family == "windows")
            {
                var pid = await _projects.LaunchAsync(project, engine);
                _launchedGames[project.Path] = pid;
                Log.Write($"{project.Name} launched (pid {pid}).");
                SetStatus(HubStrings.Get("Run project") + " · " + project.Name);
                WatchEarlyExit(project, pid);
                return;
            }

            if (family == "wasm")
            {
                await StartWebPreviewAsync(project, engine);
                return;
            }

            // Every other target goes through the engine's own command, and that one is awaited: for a deploy,
            // the command returning is the answer rather than the app exiting, so the exit code still means
            // something Hub can act on.
            await ExecuteAsync("Run project", async token =>
            {
                var result = await _projects.RunAsync(project, engine, token, SelectedDevice?.Serial);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Game exited with code {result.ExitCode}.");
                }
            });
        }
        catch (Exception ex)
        {
            // Not on the gate, so nothing upstream catches this: the launch path has to be as loud as the
            // operations that still run under it.
            await ReportFailureAsync(ex);
        }
    }

    /// <summary>
    /// A game that is gone after three seconds never got going; anything later is the user finishing play,
    /// which is not an event Hub reports. The pid is polled rather than waited on precisely because no handle
    /// is kept: Hub asked for a launch, not for a session.
    /// </summary>
    private async void WatchEarlyExit(ProjectEntry project, int pid)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (ProcessRunner.IsRunning(pid))
        {
            return;
        }

        _launchedGames.Remove(project.Path);
        var sentence = string.Format(HubStrings.Get("GameExitedEarly"), project.Name);
        Log.Write(sentence);
        SetStatus(sentence);
        if (!SuppressDialogs)
        {
            await HubDialog.ShowAsync(Owner, HubStrings.Get("OperationFailed"), sentence);
        }
    }

    /// <summary>Whether the wasm preview server is up — the answer the 运行 button needs in order to say
    /// 停止 instead, and the reason this is a property rather than a local in <see cref="RunAsync"/>.</summary>
    public bool WebPreviewRunning => _webPreview is not null;

    /// <summary>Verification hook (<c>--verify-shell</c>): records a pid as the game Hub launched for this
    /// workspace. The suite has no built artifact to start, and what the ruling is about is not the launch —
    /// it is that a running game leaves the gate empty, takes no row, and shows up again only as the reason a
    /// rebuild would fail.</summary>
    internal void RememberGameForCheck(string projectPath, int pid) => _launchedGames[projectPath] = pid;

    /// <summary>Verification hook: drops the remembered games, so a scripted "it is still running" cannot
    /// leave the next round's build press refused for a process that only ever existed in this suite.</summary>
    internal void ForgetGamesForCheck() => _launchedGames.Clear();

    public string WebPreviewUrl => _webPreview?.Url ?? "";

    private async Task StartWebPreviewAsync(ProjectEntry project, EngineEntry engine)
    {
        var preview = await _platformBuilds.StartWebPreviewAsync(project, engine, openBrowser: true, CancellationToken.None);
        _webPreview = preview;
        SetStatus(HubStrings.Get("Run project") + " · " + preview.Url);
        Changed?.Invoke();

        // The server going away — a crash, a Ctrl+C in its console, the browser tab ending the session — has to
        // put the button back by itself. Guarded on identity because stopping one preview must not erase a
        // newer one that already replaced it.
        _ = preview.Server.ContinueWith(_ =>
        {
            if (!ReferenceEquals(_webPreview, preview))
            {
                return;
            }

            _webPreview = null;
            preview.Stop.Dispose();
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
        }, TaskScheduler.Default);
    }

    /// <summary>Take the preview server down. The token source is disposed by the continuation that notices the
    /// server came down, so the repaint and the cleanup ride the same event whoever pressed the button.</summary>
    public void StopWebPreview()
    {
        if (_webPreview is not { } preview)
        {
            return;
        }

        PlatformBuildService.StopWebPreview(preview);
        SetStatus(HubStrings.Get("Stop") + " · " + preview.Url);
    }

    private string BuildOutputDirectory(ProjectEntry project)
    {
        if (BuildTargets.Get(project.Platform).Family == "android")
        {
            // The engine produces the APK via Gradle into the project's build directory (no longer Hub's staging directory).
            return EngineBuildLayout.FindBuildDirectory(project) ?? AndroidPackageService.StageDirectory(project);
        }

        return Path.GetDirectoryName(_projects.FindExecutable(project))!;
    }

    /// <summary>Opens the Android release settings. The projects page's "Android release settings" button calls it directly.</summary>
    public async Task<bool> EditAndroidReleaseAsync(ProjectEntry project)
    {
        try
        {
            _androidPasswords.TryGetValue(project.Path, out var previous);
            var dialog = new AndroidReleaseWindow(project, EngineTools(RequiredEngine(project)), _runner, previous);
            var result = Owner is null ? await dialog.ShowDialog<HubDialogResult>(null!) : await dialog.ShowDialog<HubDialogResult>(Owner);
            if (result != HubDialogResult.Ok)
            {
                return false;
            }

            _androidPasswords[project.Path] = dialog.Passwords!;
            project.BuildStatus = "Not built";
            Store.Save(State);
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(Owner, HubStrings.Get("AndroidReleaseSettings"), ex.Message);
            return false;
        }
    }

    // ───────────────────────── Open and editors ─────────────────────────

    public void Open(string path, params string[] arguments) => _runner.Open(path, arguments.Length == 0 ? null : arguments);

    public async Task OpenProjectFolderAsync() => await ExecuteAsync("Open project folder", _ =>
    {
        Open(RequiredProject().Path);
        return Task.CompletedTask;
    });

    public async Task OpenBuildOutputsAsync() => await ExecuteAsync("Open build outputs", _ =>
    {
        var project = RequiredProject();
        var directory = BuildTargets.Get(project.Platform).Family == "android"
            ? Path.Combine(AndroidPackageService.StageDirectory(project), "app/build/outputs")
            : BuildTargets.BuildDirectory(project);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        Open(directory);
        return Task.CompletedTask;
    });

    public async Task OpenEngineFolderAsync() => await ExecuteAsync("Open engine folder", _ =>
    {
        Open(RequiredEngine().Path);
        return Task.CompletedTask;
    });

    // ───────────────────────── Prebuilt engine libraries ─────────────────────────

    /// <summary>
    /// Whether the currently selected engine can build a prebuilt library on this machine; returns
    /// <c>null</c> when it can't (non-Windows host). The engine itself also allows Linux to consume
    /// prebuilt libraries, but this project only targets Windows.
    /// </summary>
    public BuildTarget? PrebuiltHostTarget => EnginePrebuilt.HostTarget();

    /// <summary>The selected engine's prebuilt-library status on the local target (for the engines page status readout).</summary>
    public PrebuiltAvailability? PrebuiltStatusOf(EngineEntry? engine)
    {
        if (engine is null || PrebuiltHostTarget is not { } host) return null;
        var configuration = _prebuiltState.Load(engine)?.Configuration ?? "Release";
        return EnginePrebuilt.Inspect(engine, host, configuration, _prebuiltState);
    }

    /// <summary>
    /// Compiles the engine into a prebuilt library reusable by projects.
    ///
    /// This is a **long task** (compiling the whole engine, minutes to tens of minutes, gigabytes of
    /// disk), and when the toolchain is missing the engine may trigger its own setup on the way
    /// (which changes the global environment), so a confirmation window first lets the user pick a
    /// configuration and be informed.
    /// </summary>
    public async Task BuildEngineAsync()
    {
        if (RefuseIfBusy())
        {
            return;
        }

        var engine = RequiredEngine();
        var host = PrebuiltHostTarget ?? throw new InvalidOperationException(HubStrings.Get("PrebuiltUnsupportedHost"));

        var configuration = await EngineBuildWindow.PickAsync(Owner, engine, host);
        if (configuration is null)
        {
            return;
        }

        // Compiling the engine is the longest thing Hub does and the one whose output the user cannot watch
        // any other way, so it is the change that earns a row — and an identity per engine and host, so
        // pressing build twice on two targets shows two rows rather than a second copy of one.
        await RunTaskAsync($"engine:hostbuild:{engine.Version}:{host.Id}", "Build engine",
            engine.Version + " · " + host.Name, async (task, token) =>
        {
            await _engineBuild.BuildAsync(engine, host, configuration, token, OwnLines(task));
        });

        Refresh();
    }

    /// <summary>
    /// Opens the project's "prebuilt library settings". Changing the link mode invalidates old
    /// artifacts, so the build status is reset after saving.
    /// </summary>
    public async Task<bool> EditPrebuiltAsync(ProjectEntry project)
    {
        try
        {
            var engine = RequiredEngine(project);
            var target = BuildTargets.Get(project.Platform);
            var availability = EnginePrebuilt.Supported(target)
                ? EnginePrebuilt.Inspect(engine, target, project.Configuration, _prebuiltState)
                : new PrebuiltAvailability(PrebuiltStatus.PlatformUnsupported,
                    $"Prebuilt engine libraries are only supported for Windows targets; {target.Id} is a '{target.Family}' target.");

            var enabled = PrebuiltSettings.Load(project)?.Enabled == true;
            var dialog = new PrebuiltWindow(project, engine, target, availability, enabled, NavigateTo);
            var result = Owner is null
                ? await dialog.ShowDialog<HubDialogResult>(null!)
                : await dialog.ShowDialog<HubDialogResult>(Owner);
            if (result != HubDialogResult.Ok)
            {
                return false;
            }

            new PrebuiltSettings { Enabled = dialog.Enabled }.Save(project);
            // The link mode changed: existing artifacts no longer represent the current configuration, so a rebuild is required.
            project.BuildStatus = "Not built";
            Store.Save(State);
            Log.Write(HubStrings.Get("PrebuiltSaved"));
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            await HubDialog.ShowAsync(Owner, HubStrings.Get("PrebuiltSettings"), ex.Message);
            return false;
        }
    }

    private void NavigateTo(string page) => NavigateRequested?.Invoke(page);

    public async Task OpenEditorAsync(bool visualStudio) => await ExecuteAsync(
        visualStudio ? "Open Visual Studio" : "Open VS Code", _ =>
    {
        var project = RequiredProject();
        var executable = visualStudio ? State.VisualStudioExecutable : State.CodeExecutable;
        if (executable is null || !File.Exists(executable))
        {
            throw new FileNotFoundException(visualStudio
                ? "Select Visual Studio devenv.exe in Settings first."
                : "Select VS Code executable in Settings first.");
        }

        Open(executable, EditorArguments(visualStudio, project.Path));
        project.LastOpened = DateTimeOffset.Now;
        return Task.CompletedTask;
    });

    /// <summary>
    /// Launch arguments that open <paramref name="path"/> in the selected editor.
    ///
    /// Both editors take the folder as a **positional** first argument: devenv's own usage line is
    /// "devenv [solutionfile | projectfile | folder | anyfile.ext]", and a CMake project folder is
    /// what makes Visual Studio pick up <c>CMakeLists.txt</c>. There is deliberately no switch here —
    /// devenv has no <c>/OpenFolder</c> switch, and passing one aborts with
    /// "Invalid Command Line. Unknown Switch : OpenFolder" **before** the IDE ever opens, which is a
    /// failure no compiler can catch. Kept as a pure function so the shell check can pin the exact list.
    ///
    /// <paramref name="visualStudio"/> is intentionally not consulted: the two editors accept the same
    /// positional form, and keeping the flag makes the call site read symmetrically on both branches so
    /// someone "restoring" a VS-only switch has to see this comment first.
    /// </summary>
    internal static string[] EditorArguments(bool visualStudio, string path) => [path];

    /// <summary>Selects an editor executable. WPF's <c>SelectEditor</c>, including filename validation.</summary>
    public async Task SelectEditorAsync(bool visualStudio, string? path)
    {
        if (path is null)
        {
            return;
        }

        var expected = visualStudio ? "devenv.exe" : "Code.exe";
        if (!Path.GetFileName(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"Select {expected}.");
            return;
        }

        if (visualStudio)
        {
            State.VisualStudioExecutable = path;
        }
        else
        {
            State.CodeExecutable = path;
        }

        Store.Save(State);
        Log.Write($"Selected editor: {path}");
        Refresh();
        await Task.CompletedTask;
    }

    // ───────────────────────── Toolchains ─────────────────────────

    private void UpdateTools(List<ToolchainComponent> values)
    {
        Components = values;
        ComponentsChanged?.Invoke();
    }

    /// <summary>
    /// The engine currently used for probing on the toolchains page: the page's engine selection
    /// first, then the default engine, then the first one.
    /// A toolchain belongs to a **specific engine tree** (one set per version), so it must bind to a
    /// concrete engine rather than being global.
    /// </summary>
    /// <summary>The tool root inside the engine tree (<c>&lt;engine&gt;/tools/external</c>) — where Hub gets the tools it still calls directly.</summary>
    private static string EngineTools(EngineEntry engine) => EngineToolchain.ToolRoot(engine);

    private EngineEntry? ToolEngine =>
        ToolchainEngine
        ?? State.Engines.FirstOrDefault(engine => engine.Path == State.DefaultEnginePath)
        ?? State.Engines.FirstOrDefault();

    /// <summary>
    /// Toolchain probing. The source of truth is the engine's bundled <c>1k/build.profiles</c>
    /// (expected versions) + the official install location <c>&lt;engine&gt;/tools/external</c>
    /// (what's actually installed) — Hub no longer keeps its own tool version manifest.
    /// </summary>
    public Task<List<ToolchainComponent>> DetectAsync(CancellationToken token = default)
    {
        var target = ToolTarget ?? BuildTargets.All[0];
        if (ToolEngine is not { } engine)
        {
            return Task.FromResult<List<ToolchainComponent>>(
                [new("Axmol engine", ComponentStatus.Missing, "Import an Axmol engine to inspect its toolchain.")]);
        }

        return _engineToolchain.InspectAsync(engine, target.Id, token);
    }

    public async Task VerifyToolchainsAsync() =>
        await ExecuteAsync("Verify toolchains", async token => UpdateTools(await DetectAsync(token)));

    public async Task ChangeToolTargetAsync(BuildTarget? target)
    {
        if (!_targetReady)
        {
            return;
        }

        ToolTarget = target;
        UpdateTargetHint();
        await ExecuteAsync("Verify toolchains", async token => UpdateTools(await DetectAsync(token)));
    }

    /// <summary>
    /// Environment prep: runs the engine's own <c>setup.ps1</c>.
    ///
    /// **This is not "Hub installing tools"** — the toolchain is installed by the engine's
    /// <c>1k/1kiss.ps1</c> into <c>&lt;engine&gt;/tools/external</c>. Hub passes <c>-hub</c> so
    /// <c>AX_ROOT</c> and <c>PATH</c> remain process-local; on Windows setup may still change the
    /// current user's PowerShell execution policy, so callers must **confirm with the user first**.
    /// </summary>
    public async Task RunEngineSetupAsync(string? platform = null)
    {
        // The engine's own setup.ps1 is minutes of installs, and Hub cannot say how long: it is the third
        // change that takes a row. The identity is per engine version because that is what the row is about,
        // and pressing it again for the same version must not stack a second row.
        await RunTaskAsync($"engine:setup:{ToolEngine?.Version ?? "none"}", "Run engine setup",
            ToolEngine?.Version, async (task, token) =>
        {
            var engine = ToolEngine ?? throw new InvalidOperationException("Import an Axmol engine first.");
            var target = ToolTarget ?? BuildTargets.All[0];
            if (platform is null && !target.CanBuildOn(BuildTargets.Host))
            {
                throw new PlatformNotSupportedException(HubStrings.Get("RequiresHost") + string.Join(" / ", target.Hosts));
            }

            var effective = platform ?? AxmolCommandMap.Target(target).Platform;
            var result = await _setup.RunAsync(engine, new SetupOptions(effective), token, OwnLines(task));
            Log.Write($"{effective}: {result.Describe()}");
            if (result.Outcome == SetupOutcome.NeedsPowerShell)
            {
                // The host-shell card directly above this button is the fix, so it must be showing the reason the
                // dialog just gave. Re-probing is a filesystem walk: cheap at the one moment it matters, and it
                // needs no chained confirmation dialog (the error dialog already named the cause).
                HostShell = HostPowerShell.Probe();
                HostShellChanged?.Invoke();
            }

            // When developer mode is off, setup.ps1 exits 0 but installs nothing — that fake success must be reported as failure.
            if (!result.Succeeded) throw new InvalidOperationException(result.Describe());
            UpdateTools(await DetectAsync(token));
        });
    }

    /// <summary>
    /// Fills in the one thing a filesystem walk cannot know off Windows: the version of the pwsh it found.
    ///
    /// Never awaited and hard-bounded, for the same reasons as <see cref="RefreshEngineIndexAsync"/>: the window
    /// must come up without waiting on a child process, and a pwsh that hangs must not hang the shell with it.
    /// The result is applied only if nothing else changed the verdict meanwhile — a user who pressed Re-check or
    /// installed in the meantime must not be overwritten by a stale answer, which is why the guard compares both
    /// the state and the path it asked about.
    /// </summary>
    private async void CompleteHostShellVersionAsync()
    {
        if (HostShell.State != HostShellState.Unknown || HostShell.Executable is not { } probed)
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var refined = await HostPowerShell.ProbeVersionAsync(_runner, HostShell, deadline.Token);
            Dispatcher.UIThread.Post(() =>
            {
                if (HostShell.State != HostShellState.Unknown || HostShell.Executable != probed) return;
                HostShell = refined;
                HostShellChanged?.Invoke();
                Log.Write(refined.Describe());
            });
        }
        catch (Exception ex)
        {
            // The card keeps saying "unconfirmed", which is the truth. A version probe that failed is not a
            // reason to interrupt anyone.
            Log.Write("Host PowerShell version probe: " + ex.Message);
        }
    }

    /// <summary>
    /// Re-probes the host PowerShell and resolves the version. Together with
    /// <see cref="CompleteHostShellVersionAsync"/> this is the only place a pwsh process is started (once,
    /// ~190 ms measured); the page constructor and <c>Refresh()</c> use the process-free
    /// <see cref="HostPowerShell.Probe"/>.
    /// </summary>
    public async Task RefreshHostShellAsync()
    {
        await ExecuteAsync("Check PowerShell 7", async token =>
        {
            var probed = HostPowerShell.Probe();
            Log.Write(probed.Describe());
            HostShell = await HostPowerShell.ProbeVersionAsync(_runner, probed, token);
            HostShellAwaitingTerminal = false;
            HostShellChanged?.Invoke();
            Log.Write(HostShell.Describe());
        });
    }

    /// <summary>
    /// Verification seam: puts a verdict on the card without touching the machine. On a host that already has
    /// pwsh — which is most development machines — the "missing" and "waiting on a terminal" layouts would
    /// otherwise never be reachable, and an unasserted layout is an unasserted UI (AGENTS.md). Installing for
    /// real is deliberately not offered here: it changes the whole machine, so its verdicts are covered by
    /// <c>Checks --check-host-shell</c> instead, which is host-independent and offline.
    /// </summary>
    internal void HostShellForCheck(HostShellStatus status, bool awaitingTerminal = false)
    {
        HostShell = status;
        HostShellAwaitingTerminal = awaitingTerminal;
    }

    /// <summary>
    /// Installs PowerShell 7 on this machine. The one operation in Hub that writes outside the engine tree and
    /// the only one whose confirmation has to name the road it will take (UAC prompt, sudo password in a
    /// terminal window, or "Hub cannot do this here").
    ///
    /// Success is decided by the probe afterwards, never by an installer's exit code — the same distrust
    /// <see cref="EngineSetupService"/> applies to <c>setup.ps1</c> exiting 0 having installed nothing.
    /// </summary>
    public async Task InstallHostShellAsync()
    {
        // Re-probe first: the card may be showing the state as of app start, and the user might have installed
        // pwsh in a terminal since then. This is the filesystem walk, not a spawn.
        HostShell = HostPowerShell.Probe();
        HostShellChanged?.Invoke();

        // Only the MSI road is a download, so only it goes on the queue. <see cref="HostShellPlan"/> answers
        // MSI exactly when nothing named winget is on PATH, and <c>InstallAsync</c>'s own rule then has no
        // other road to take — so this branch cannot flip after the decision. The other two stay under the
        // lock: winget is a subprocess Hub cannot meter, and the terminal bootstrap hands a script to a
        // window Hub can neither read nor close. A queue row for either would show numbers it does not have
        // and a cancel that stops nothing.
        if (HostShellPlan != HostShellMethod.GitHubMsi)
        {
            await ExecuteAsync("Install PowerShell 7", token => InstallHostShellAsync(token, DownloadProgress()));
            return;
        }

        await EnqueueTask("hostshell:msi", "TaskHostShell", null, "Install PowerShell 7",
            (item, token) => InstallHostShellAsync(token, ByteProgress(item)));
    }

    private async Task InstallHostShellAsync(CancellationToken token, IProgress<DownloadProgress> progress)
    {
        Log.Write($"Installing the host PowerShell; planned road: {HostShellPlan}.");
        var result = await _hostShellInstaller.InstallAsync(progress, token);
        Log.Write($"{result.Method}: {result.Detail}");
        HostShell = await HostPowerShell.ProbeVersionAsync(_runner, HostPowerShell.Probe(), token);
        HostShellChanged?.Invoke();
        Log.Write(HostShell.Describe());
        if (result.Outcome == HostShellInstallOutcome.LaunchedInTerminal)
        {
            // Hub handed the script to a terminal it cannot read. Reporting that as a completed install
            // would be a false success, and reporting it as a failure would be a false alarm: the status
            // line says where it went and what to press afterwards.
            SetStatus(HubStrings.Get("HostShellInTerminal"));
            HostShellAwaitingTerminal = true;
            HostShellChanged?.Invoke();
            return;
        }

        if (result.Outcome != HostShellInstallOutcome.Ready)
        {
            throw new HostShellInstallException(result, HostShell);
        }
    }

    // ───────────────────────── Android devices ─────────────────────────

    private void UpdateDevicePicker()
    {
        SelectedDevice = Devices.FirstOrDefault(device => SelectedDevice is not null && device.Serial == SelectedDevice.Serial)
                         ?? Devices.FirstOrDefault(device => device.State == "device");
        DevicesChanged?.Invoke();
    }

    public async Task QueryDevicesAsync()
    {
        await ExecuteAsync("Refresh Android devices", async token =>
        {
            var project = RequiredProject();
            var target = BuildTargets.Get(project.Platform);
            if (target.Family != "android")
            {
                throw new InvalidOperationException("Select an Android project.");
            }

            var deviceEngine = RequiredEngine(project);
            var environment = _platformBuilds.CreateEnvironment(deviceEngine, target);
            Devices = await new AndroidDeviceService(_runner, EngineTools(deviceEngine)).DevicesAsync(environment, token);
            _deviceProject = project.Path + "|" + project.Platform;
            UpdateDevicePicker();
        });
    }

    // ───────────────────────── Settings ─────────────────────────

    public void SetProjectDirectory(string directory)
    {
        var path = PreferencesStore.VerifyDirectory(directory);
        var previous = Preferences.ProjectDirectory;
        Preferences.ProjectDirectory = path;
        try
        {
            PreferencesStore.Save(Preferences);
        }
        catch
        {
            Preferences.ProjectDirectory = previous;
            throw;
        }

        ProjectDirectory = path;
        Refresh();
        SetStatus(HubStrings.Get("DefaultProjectsChanged"));
    }

    public void Dispose()
    {
        // A preview server outlives the page that started it, so shutdown has to take it with the rest of the
        // things this class leaves no orphans in.
        if (_webPreview is { } preview)
        {
            PlatformBuildService.StopWebPreview(preview);
        }

        _http.Dispose();
    }
}
