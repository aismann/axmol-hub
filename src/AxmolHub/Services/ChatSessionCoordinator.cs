using static AxmolHub.ChatProviderCoordinator;
using static AxmolHub.ChatExecutionCoordinator;
using AxmolHub.Core;
using static AxmolHub.ChatWorkspace;

namespace AxmolHub;
/// <summary>Owns the durable session registry, workspace membership and current selection.</summary>
internal sealed partial class ChatSessionCoordinator
{
    internal ChatProviderCoordinator Providers { private get; set; } = null!;
    internal ChatExecutionCoordinator Execution { private get; set; } = null!;

    internal ChatSessionCoordinator(string dataRoot)
    {
        _dataRoot = dataRoot;
        _sessions = new ConversationRegistry(new ConversationStore(dataRoot));
        foreach (var summary in _sessions.List().Where(summary => summary.SpawnedState is SpawnedSessionStates.Queued or SpawnedSessionStates.Running))
            _sessions.TryUpdate(summary.Id, child => child.SpawnedState = SpawnedSessionStates.Interrupted);
        _workspaces = new WorkspaceStore(dataRoot);
        _workspaces.SeedFrom(_sessions.List().Select(summary => summary.WorkspaceRoot));
    }

    private readonly string _dataRoot;
    private readonly ConversationRegistry _sessions;
    internal ConversationRegistry Registry => _sessions;
    internal Conversation? Active => _active;
    internal string? SelectedApprovalMode { get => _selectedApprovalMode; set => _selectedApprovalMode = value; }
    private readonly WorkspaceStore _workspaces;
    private Conversation? _active;
    private string _selectedMode = ChatModes.Ask;
    private bool _selectedModeExplicit;
    private string _selectedReasoningEffort = ChatReasoningEfforts.Default;
    private string? _selectedApprovalMode;
    private string? _selectedWorkspaceRoot;
    internal Conversation? ActiveConversation => _active;
    internal string ActiveMode => _active is null ? _selectedMode : NormalizeMode(_active.Mode);
    internal string ActiveReasoningEffort
        => ChatReasoningEfforts.Normalize(_active?.ReasoningEffort ?? _selectedReasoningEffort);
    internal bool SupportsReasoningEffort
        => Providers.SelectedChatModel is { } choice && ModelCatalog.SupportsReasoningEffort(choice.Provider, choice.ModelName);
    internal IReadOnlyList<ConversationSummary> Conversations
        => [.. _sessions.List().Where(summary => IsListed(summary) && !summary.Archived)];
    internal IReadOnlyList<ConversationSummary> ArchivedConversations
        => [.. _sessions.List().Where(summary => summary.Archived)];

    internal bool SelectMode(string mode)
    {
        if (mode is not (ChatModes.Ask or ChatModes.Plan or ChatModes.Agent)) return false;
        _selectedMode = mode;
        _selectedModeExplicit = true;
        if (_active is not null) _sessions.TryUpdate(_active.Id, conversation => conversation.Mode = mode);
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool ActiveAskUserEnabled => Execution.PreferencesProvider?.Invoke().AllowAssistantQuestions != false;

    internal bool SelectAskUserEnabled(bool enabled)
    {
        var preferences = Execution.PreferencesProvider?.Invoke();
        if (preferences is null) return false;
        preferences.AllowAssistantQuestions = enabled;
        Execution.PreferencesPersist?.Invoke(preferences);
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool SelectReasoningEffort(string effort)
    {
        if (effort is not (ChatReasoningEfforts.Default or ChatReasoningEfforts.Low
            or ChatReasoningEfforts.Medium or ChatReasoningEfforts.High or ChatReasoningEfforts.XHigh
            or ChatReasoningEfforts.Max or ChatReasoningEfforts.Ultra)) return false;
        // Choosing a tier is allowed unless this model refused that one; "the manifest never mentioned it" is
        // not a refusal, and treating it as one is what made the menu unreachable on the default gateway.
        if (effort != ChatReasoningEfforts.Default
            && (Providers.SelectedChatModel is not { } choice
                || !ModelCatalog.MaySendEffort(choice.Provider, choice.ModelName, effort))) return false;
        _selectedReasoningEffort = effort;
        if (_active is not null) _sessions.TryUpdate(_active.Id, conversation =>
        {
            conversation.ReasoningEffort = effort;
            // Same rule as picking a model by hand: the moment someone names a tier, that session is theirs again.
            conversation.Routing = ChatRouting.Manual;
        });
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool SetPlanTasksExpanded(string conversationId, bool expanded)
        => _sessions.TryUpdate(conversationId, conversation => conversation.PlanTasksExpanded = expanded);

    internal bool SetRouting(string conversationId, string routing)
    {
        var normalized = ChatRouting.Normalize(routing);
        if (!_sessions.TryUpdate(conversationId, conversation => conversation.Routing = normalized))
            return false;
        // Turning routing off retires the route it last took. Leaving it would let a surface read a tier this
        // session no longer uses as if it were the current one.
        if (normalized == ChatRouting.Manual)
            Execution.ClearRoute(conversationId);
        Execution.Changed?.Invoke();
        return true;
    }

    internal string RoutingFor(string conversationId)
        => ChatRouting.Normalize((_sessions.Peek(conversationId) ?? _sessions.Load(conversationId))?.Routing);
    internal string ActiveRouting => ChatRouting.Normalize(_active?.Routing);

    internal string? WorkspaceRootFor(string conversationId)
        => (_sessions.Peek(conversationId) ?? _sessions.Load(conversationId))?.WorkspaceRoot;
    internal string? SessionTitleFor(string conversationId)
        => (_sessions.Peek(conversationId) ?? _sessions.Load(conversationId))?.Title;
    internal string? ActiveWorkspaceRoot => _active is { } session ? WorkspaceRootFor(session.Id) : _selectedWorkspaceRoot;
    internal IReadOnlyList<string> Workspaces => _workspaces.Roots;

    internal bool ForgetWorkspace(string root)
    {
        if (!_workspaces.Remove(root)) return false;
        Execution.Changed?.Invoke();
        return true;
    }

    private void RegisterWorkspace(string? root) => _workspaces.Add(root);
    internal WorkspacePathVerdict? SelectWorkspaceRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (_active is { } clearing) SetWorkspaceRoot(clearing.Id, null);
            else _selectedWorkspaceRoot = null;
            Execution.Changed?.Invoke();
            return null;
        }

        var (verdict, full) = ValidateWorkspacePath(path);
        if (verdict is not null) return verdict;

        if (_active is { } session) SetWorkspaceRoot(session.Id, full);
        else
        {
            // A folder picked in the chip while nothing is on screen is a designation, not just a pending value: it
            // names the workspace the next session will work in, and the group the user asked for is that directory.
            _selectedWorkspaceRoot = full;
            RegisterWorkspace(full);
            Execution.Changed?.Invoke();
        }

        return null;
    }

    private (WorkspacePathVerdict? Verdict, string Full) ValidateWorkspacePath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            return (WorkspacePathVerdict.EscapesWorkspace, "");
        }

        if (!Path.IsPathRooted(path)) return (WorkspacePathVerdict.EscapesWorkspace, "");
        if (!Directory.Exists(full)) return (WorkspacePathVerdict.MissingWorkspace, "");
        if (WorkspacePaths.IsProtected(full, new WorkspaceGuards(_dataRoot, Execution.EngineRootsProvider?.Invoke() ?? [])))
            return (WorkspacePathVerdict.ProtectedRoot, "");

        return (null, full);
    }

    internal bool SetWorkspaceRoot(string conversationId, string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        var switchDefaultAskToAgent = _active?.Id == conversationId && normalized is not null
            && _active.Mode == ChatModes.Ask && !_selectedModeExplicit;
        if (!_sessions.TryUpdate(conversationId, opened =>
            {
                opened.WorkspaceRoot = normalized;
                // A workspace attached to an Ask-default chat should make the newly available Agent mode effective.
                if (switchDefaultAskToAgent) opened.Mode = ChatModes.Agent;
            })) return false;
        if (switchDefaultAskToAgent) _selectedMode = ChatModes.Agent;
        // The session took the directory, so the directory is now a workspace. Clearing one (null) never takes a
        // workspace away: other sessions may still work there, and only ForgetWorkspace decides a place is finished.
        RegisterWorkspace(normalized);
        Execution.Changed?.Invoke();
        return true;
    }

    internal async Task<string> ApplyGrantedRootAsync(string conversationId, string path)
    {
        var added = false;
        await Execution.ApplyOnUiAsync(() =>
        {
            added = _sessions.TryUpdate(conversationId, opened =>
            {
                // Through the shape rules and with the guards in hand: this is one of the two doors into the list,
                // so a duplicate spelling, a directory that vanished, or a protected location cannot arrive here
                // either — the card was asked about a directory, the session keeps a directory.
                opened.GrantedRoots = WorkspacePaths.NormalizeRoots([.. opened.GrantedRoots, path],
                    new WorkspaceGuards(_dataRoot, Execution.EngineRootsProvider?.Invoke() ?? []));
            });
            if (added) Execution.Changed?.Invoke();
        }).ConfigureAwait(false);

        if (!added)
            return $"Refused: session {conversationId} is gone, so nothing could be granted. Do not retry this call.";
        Execution.Audit(conversationId, $"Granted directory: {path}");
        return $"Granted: {path} is readable and writable for this session, alongside the workspace. Name it by "
               + "absolute path in read_file, file_write, list_directory, search_text, find_files, workspace_map, "
               + "and in run_command's cwd.";
    }

    internal IReadOnlyList<string> ActiveGrantedRoots
        => _active is { } session ? (_sessions.Peek(session.Id) ?? session).GrantedRoots : [];

    internal bool RevokeGrantedRoot(string conversationId, string path)
    {
        if (WorkspacePaths.CanonicalRoot(path) is not { } key) return false;
        var revoked = false;
        _sessions.TryUpdate(conversationId, opened =>
        {
            var kept = opened.GrantedRoots
                .Where(granted => !string.Equals(WorkspacePaths.CanonicalRoot(granted), key, StringComparison.Ordinal))
                .ToList();
            if (kept.Count == opened.GrantedRoots.Count) return;
            opened.GrantedRoots = kept;
            revoked = true;
        });
        if (!revoked) return false;

        Execution.Audit(conversationId, $"Revoked directory: {path}");
        Execution.Changed?.Invoke();
        return true;
    }

    internal async Task<(GrantVerdict? Verdict, string Detail)> GrantDirectoryAsync(string conversationId,
        string? path)
    {
        var guards = new WorkspaceGuards(_dataRoot, Execution.EngineRootsProvider?.Invoke() ?? []);
        if (_sessions.Peek(conversationId) is not { } session) return (GrantVerdict.SessionGone, "");

        var sandbox = new WorkspaceSandbox(session.WorkspaceRoot,
            WorkspacePaths.NormalizeRoots(session.GrantedRoots, guards));
        var (verdict, full, detail) = WorkspacePaths.ClassifyGrant(sandbox, guards, path);
        if (verdict is not GrantVerdict.Granted) return (verdict, detail);

        // The record an approved card writes, guards and all: one grant, whichever door it came through. The
        // write is read back rather than assumed, because the session can be closed between the click and here.
        await ApplyGrantedRootAsync(conversationId, full).ConfigureAwait(false);
        var landed = (_sessions.Peek(conversationId) ?? session).GrantedRoots.Any(granted =>
            string.Equals(WorkspacePaths.CanonicalRoot(granted), WorkspacePaths.CanonicalRoot(full),
                StringComparison.Ordinal));
        return landed ? (null, "") : (GrantVerdict.SessionGone, "");
    }

    internal Conversation StartConversation(string? providerId = null)
    {
        // A permission mode picked while nothing was on screen was meant for the session started right then, so
        // it is taken only from that state: a session started from a live one follows the app default again.
        var approvalMode = _active is null ? _selectedApprovalMode : null;
        _selectedApprovalMode = null;
        // Same rule for the sandbox: a directory picked before the first message was meant for the session that
        // follows, and a session started from a live one gets its own.
        var workspaceRoot = _active is null ? _selectedWorkspaceRoot : null;
        _selectedWorkspaceRoot = null;
        if (_active is not null)
        {
            _selectedMode = ChatModes.Ask;
            _selectedModeExplicit = false;
            _selectedReasoningEffort = ChatReasoningEfforts.Default;
        }
        var mode = _selectedModeExplicit
            ? _selectedMode
            : workspaceRoot is null ? ChatModes.Ask : ChatModes.Agent;
        var choice = Providers.SelectedChatModel;
        var selectedProvider = providerId is null
            ? choice?.Provider
            : Providers.Providers.FirstOrDefault(provider => provider.Id == providerId);
        var modelName = selectedProvider is not null && choice is not null && selectedProvider.Id == choice.Provider.Id
            ? choice.ModelName
            : selectedProvider?.Model ?? "";
        var conversation = Conversation.Create(selectedProvider?.Id ?? "");
        conversation.ModelName = modelName;
        conversation.Mode = mode;
        _selectedMode = mode;
        _selectedModeExplicit = false;
        var defaultEffort = ModelCatalog.DefaultReasoningEffort(selectedProvider, modelName);
        conversation.ReasoningEffort = _selectedReasoningEffort != ChatReasoningEfforts.Default
                                       && ModelCatalog.MaySendEffort(selectedProvider, modelName, _selectedReasoningEffort)
            ? _selectedReasoningEffort
            : defaultEffort;
        _selectedReasoningEffort = conversation.ReasoningEffort;
        conversation.ApprovalMode = approvalMode;
        conversation.WorkspaceRoot = workspaceRoot;
        _sessions.Adopt(conversation);
        _active = conversation;
        Execution.Changed?.Invoke();
        return conversation;
    }

    private ConversationSummary? FindEmptyDraft(string? workspaceRoot)
    {
        var wanted = SessionGroupKey.Workspace(workspaceRoot);
        return _sessions.List()
            .Where(summary => summary.MessageCount == 0 && !summary.Archived)
            .Where(summary => SessionGroupKey.Workspace(summary.WorkspaceRoot) is not { } at || at == wanted)
            .OrderByDescending(summary => summary.UpdatedAt)
            .FirstOrDefault();
    }

    private Conversation OpenDraft(ConversationSummary draft)
    {
        var conversation = _sessions.Load(draft.Id)
            ?? throw new InvalidOperationException("Conversation index listed an id that no longer exists.");
        _active = conversation;
        _selectedMode = NormalizeMode(conversation.Mode);
        _selectedReasoningEffort = ChatReasoningEfforts.Normalize(conversation.ReasoningEffort);
        Execution.Changed?.Invoke();
        return conversation;
    }

    internal Conversation StartOrOpenEmptyConversation()
        => FindEmptyDraft(null) is { } draft ? OpenDraft(draft) : StartConversation();
    internal Conversation StartOrOpenEmptyConversation(string? workspaceRoot)
    {
        // The draft this group already owns if it has one, otherwise an unclaimed one, otherwise a new session:
        // two workspaces ＋ in a row get two drafts, because the second click may not move the first group.
        var draft = FindEmptyDraft(workspaceRoot);
        var conversation = draft is not null ? OpenDraft(draft) : StartConversation();
        if (conversation.Messages.Count > 0) return conversation;
        // SetWorkspaceRoot normalizes and persists through the registry, and treats blank as "no directory" — so
        // the group's own spelling is passed straight through rather than second-guessed here.
        SetWorkspaceRoot(conversation.Id, workspaceRoot);
        // StartConversation cannot see this target yet when invoked from a workspace group's ＋: it creates the
        // session before this method attaches the group root. Apply the workspace default after attachment, but
        // leave an existing empty draft's chosen mode alone.
        if (draft is null && !string.IsNullOrWhiteSpace(workspaceRoot))
        {
            conversation.Mode = ChatModes.Agent;
            _selectedMode = ChatModes.Agent;
            _selectedModeExplicit = false;
            _sessions.TryUpdate(conversation.Id, opened => opened.Mode = ChatModes.Agent);
            Execution.Changed?.Invoke();
        }
        return conversation;
    }

    internal (Conversation? Conversation, WorkspacePathVerdict? Verdict) StartOrOpenEmptyConversationInWorkspace(
        string workspaceRoot)
    {
        var (verdict, full) = ValidateWorkspacePath(workspaceRoot);
        if (verdict is not null) return (null, verdict);
        return (StartOrOpenEmptyConversation(full), null);
    }

    internal (double Milliseconds, bool CameFromCache) LastSessionLoad { get; private set; }
    internal SessionOpenMeasurement? CurrentSessionOpen { get; private set; }

    internal Conversation? OpenConversation(string id)
    {
        var measurement = new SessionOpenMeasurement(id, "OpenConversation");
        CurrentSessionOpen = measurement;
        // The registry hands back the one instance, so opening a session that is streaming does not swap in a
        // copy that a later write would silently replace.
        var cachedBefore = _sessions.Peek(id)is not null;
        var readClock = System.Diagnostics.Stopwatch.StartNew();
        _active = _sessions.Load(id);
        LastSessionLoad = (readClock.Elapsed.TotalMilliseconds, cachedBefore);
        measurement.LoadMs = LastSessionLoad.Milliseconds;
        measurement.Cached = cachedBefore;
        var approvalStarted = measurement.ElapsedMs;
        _selectedMode = _active is null ? ChatModes.Ask : NormalizeMode(_active.Mode);
        _selectedModeExplicit = false;
        _selectedReasoningEffort = ChatReasoningEfforts.Normalize(_active?.ReasoningEffort);
        if (_active is { } opened)
        {
            // Asked before writing, because the write is not free: it replaces the whole session file, and for a
            // long conversation that file is the largest thing an open touches — larger than the read that just
            // happened, and entirely inside the open's own path. A card whose flag is already set has nothing to
            // say, and re-stamping it would also hand every reader a turn that is no longer the object it was.
            var unseen = opened.Messages.Any(turn => (turn.ApprovalState == ChatApprovalStates.Pending || turn.PlanApprovalState == PlanApprovalStates.Pending) && !turn.ApprovalSeen);
            if (unseen)
            {
                _sessions.TryUpdate(opened.Id, conversation =>
                {
                    for (var index = 0; index < conversation.Messages.Count; index++)
                    {
                        var turn = conversation.Messages[index];
                        if (turn.ApprovalSeen)
                            continue;
                        if (turn.ApprovalState == ChatApprovalStates.Pending || turn.PlanApprovalState == PlanApprovalStates.Pending)
                            conversation.Messages[index] = turn with
                            {
                                ApprovalSeen = true
                            };
                    }
                });
            }
        }

        measurement.ApprovalMs = measurement.ElapsedMs - approvalStarted;
        var notifyStarted = measurement.ElapsedMs;
        Execution.Changed?.Invoke();
        measurement.NotifyMs = measurement.ElapsedMs - notifyStarted;
        if (_active is { GoalState: ChatGoalStates.Active } goalThread && NormalizeMode(goalThread.Mode) == ChatModes.Agent && Execution.RunFor(goalThread.Id) is null && Execution.RunningCount < MaxConcurrentRuns && !goalThread.Messages.Any(turn => turn.ApprovalState == ChatApprovalStates.Pending || turn.PlanApprovalState == PlanApprovalStates.Pending))
            Execution.StartRun(goalThread.Id);
        return _active;
    }

    internal bool TryResolvePlanApproval(string conversationId, int turnIndex, string decision, out string? refusalKey)
    {
        refusalKey = null;
        if (decision is not (PlanApprovalStates.Approved or PlanApprovalStates.RevisionRequested or PlanApprovalStates.Rejected))
            throw new ArgumentOutOfRangeException(nameof(decision));
        var conversation = _sessions.Peek(conversationId) ?? _sessions.Load(conversationId);
        if (conversation is null || turnIndex < 0 || turnIndex >= conversation.Messages.Count)
        {
            refusalKey = "ChatApprovalGone";
            return false;
        }

        var pending = conversation.Messages[turnIndex];
        if (pending.Role != ChatRoles.Assistant || pending.PlanApprovalState != PlanApprovalStates.Pending)
        {
            refusalKey = "ChatApprovalGone";
            return false;
        }

        if (decision == PlanApprovalStates.Approved)
        {
            // The body is read before anything is judged, because a filed plan's text is not on the turn: every
            // test below would pass on an empty string, and an empty string is what would then be sent.
            var body = PlanStore.BodyOf(pending, _dataRoot, out var bodyState);
            // The document is named and not on disk: approving would start an Agent run on an instruction with no
            // plan in it. Refusing says so, and the card stays where the person can still revise or reject it.
            if (bodyState is not (PlanBodyState.Read or PlanBodyState.NoReference) || string.IsNullOrEmpty(body))
            {
                refusalKey = "ChatPlanBodyGone";
                return false;
            }

            if (Providers.ResolvedModel(conversationId, out refusalKey)is null)
                return false;
            if (Execution.RunningCount >= MaxConcurrentRuns)
            {
                refusalKey = "ChatParallelLimit";
                return false;
            }
        }

        var updated = _sessions.TryUpdate(conversationId, opened =>
        {
            if (turnIndex >= opened.Messages.Count || opened.Messages[turnIndex].PlanApprovalState != PlanApprovalStates.Pending)
                return;
            opened.Messages[turnIndex] = opened.Messages[turnIndex] with
            {
                PlanApprovalState = decision
            };
            if (decision == PlanApprovalStates.Approved)
            {
                opened.Mode = ChatModes.Agent;
                opened.Append(ChatTurn.User(PlanApprovalInstruction)with { Plan = pending.Plan });
            }
            else if (decision == PlanApprovalStates.RevisionRequested)
            {
                opened.Mode = ChatModes.Plan;
            }
        });
        if (!updated)
        {
            refusalKey = "ChatApprovalGone";
            return false;
        }

        if (_active?.Id == conversationId)
            _selectedMode = decision == PlanApprovalStates.Approved ? ChatModes.Agent : ChatModes.Plan;
        Execution.Changed?.Invoke();
        if (decision == PlanApprovalStates.Approved)
            Execution.StartRun(conversationId);
        return true;
    }

    internal void DeleteConversation(string id)
    {
        Execution.StopRunFor(id);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var liveConversations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var summary in _sessions.List())
        {
            if (summary.Id == id) continue;
            liveConversations.Add(summary.Id);
            var conversation = _sessions.Peek(summary.Id) ?? _sessions.Load(summary.Id);
            if (conversation is null) continue;
            foreach (var turn in conversation.Messages)
            {
                if (turn.ChangeId is { Length: > 0 } changeId) referenced.Add(changeId);
                if (turn.UndoRevisionId is { Length: > 0 } undoId) referenced.Add(undoId);
            }
            foreach (var changeId in conversation.ChangeHistoryIds)
                if (changeId is { Length: > 0 }) referenced.Add(changeId);
        }
        _sessions.Delete(id);
        ChatUndoStore.DeleteConversationCopies(_dataRoot, id);
        ChatChangeStore.PruneSession(_dataRoot, liveConversations, referenced);
        CloseIfActive(id);
        Execution.Changed?.Invoke();
    }

    private void CloseIfActive(string id)
    {
        if (_active?.Id != id) return;
        _active = null;
        _selectedMode = ChatModes.Agent;
        _selectedReasoningEffort = ChatReasoningEfforts.Default;
    }

    internal bool RenameConversation(string conversationId, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return false;
        if (!_sessions.TryUpdate(conversationId, conversation => conversation.Title = trimmed)) return false;
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool SetPinned(string conversationId, bool pinned)
    {
        if (!_sessions.TryUpdate(conversationId, conversation => conversation.Pinned = pinned)) return false;
        Execution.Changed?.Invoke();
        return true;
    }

    internal bool SetArchived(string conversationId, bool archived)
    {
        // Before the write, not after: a run still streaming into a session the user just put away is the same
        // reply nobody will read, and stopping it is what makes the wake queue and the run registry agree with
        // the list on screen.
        if (archived) Execution.StopRunFor(conversationId);
        if (!_sessions.TryUpdate(conversationId, conversation => conversation.Archived = archived)) return false;
        if (archived) CloseIfActive(conversationId);
        Execution.Changed?.Invoke();
        return true;
    }

    internal int ArchiveWorkspace(string? workspaceRoot, bool archived)
    {
        var key = SessionGroupKey.Workspace(workspaceRoot);
        if (key is null) return 0;

        var changed = 0;
        foreach (var summary in _sessions.List())
        {
            if (summary.Archived == archived) continue;
            if (SessionGroupKey.Workspace(summary.WorkspaceRoot) != key) continue;
            if (archived) Execution.StopRunFor(summary.Id);
            if (!_sessions.TryUpdate(summary.Id, conversation => conversation.Archived = archived)) continue;
            if (archived) CloseIfActive(summary.Id);
            changed++;
        }

        if (changed > 0) Execution.Changed?.Invoke();
        return changed;
    }

    internal int RestoreArchivedSessions()
    {
        var restored = 0;
        foreach (var summary in _sessions.List())
        {
            if (!summary.Archived) continue;
            if (_sessions.TryUpdate(summary.Id, conversation => conversation.Archived = false)) restored++;
        }

        if (restored > 0) Execution.Changed?.Invoke();
        return restored;
    }

    internal (int Moved, WorkspacePathVerdict? Verdict) RepointWorkspace(string? fromRoot, string toPath)
    {
        var key = SessionGroupKey.Workspace(fromRoot);
        if (key is null) return (0, WorkspacePathVerdict.EscapesWorkspace);
        var (verdict, full) = ValidateWorkspacePath(toPath);
        if (verdict is not null) return (0, verdict);

        var moved = 0;
        foreach (var summary in _sessions.List())
        {
            if (SessionGroupKey.Workspace(summary.WorkspaceRoot) != key) continue;
            if (_sessions.TryUpdate(summary.Id, conversation => conversation.WorkspaceRoot = full)) moved++;
        }

        // A directory picked while nothing was on screen is the same place under its old spelling. Left alone,
        // the next session would start somewhere the user has just stopped pointing at.
        if (SessionGroupKey.Workspace(_selectedWorkspaceRoot) == key) _selectedWorkspaceRoot = full;

        // The registry entry moves with the group rather than being re-derived from the sessions: a workspace that
        // has no sessions left still has a path the person in front of the screen just corrected, and it has to
        // repaint for that.
        var renamed = _workspaces.Replace(fromRoot, full);
        if (moved > 0 || renamed) Execution.Changed?.Invoke();
        return (moved, null);
    }

    internal Conversation? BranchFrom(string conversationId, int index)
    {
        var source = _sessions.Peek(conversationId) ?? _sessions.Load(conversationId);
        if (source is null) return null;

        var title = NextBranchTitle(source.Title);
        // The cut point is decided with the source locked: the branch must not capture a turn that arrived
        // after the click, and the list a running session is growing must not be enumerated unlocked.
        var prefix = _sessions.Snapshot(source.Id, index + 1);
        if (prefix is null) return null;

        var branch = new Conversation
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            ProviderId = source.ProviderId,
            ModelName = source.ModelName,
            Mode = source.Mode,
            ReasoningEffort = source.ReasoningEffort,
            // A fork continues the same task, so it continues in the same directory. Copied rather than left to be
            // picked again: the person asked for the same tree, not for the same question a second time.
            WorkspaceRoot = source.WorkspaceRoot,
            // And the directories granted to it, for the same reason: a fork that kept the workspace but dropped
            // the grant would be a copy of the task that has to ask the same question again, and the copy is the
            // one the person already answered.
            GrantedRoots = [.. source.GrantedRoots],
            Messages = prefix,
            ChangeHistoryIds = [.. source.ChangeHistoryIds
                .Concat(prefix.SelectMany(turn => new[] { turn.ChangeId, turn.UndoRevisionId }))
                .Where(id => id is { Length: > 0 })
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)],
            ContextSummary = prefix.Count >= SummaryMessageCount(source) ? source.ContextSummary : "",
            ContextSummaryThroughMessageCount = prefix.Count >= SummaryMessageCount(source)
                ? SummaryMessageCount(source)
                : 0,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            // A fork is the person starting a conversation, so it sorts by the fork moment, not by the
            // inherited prefix's last turn.
            EngagedAt = DateTimeOffset.Now,
            BranchSourceId = source.Id,
            BranchSourceIndex = index,
        };

        _sessions.Adopt(branch);
        _active = branch;
        _selectedMode = NormalizeMode(branch.Mode);
        _selectedReasoningEffort = ChatReasoningEfforts.Normalize(branch.ReasoningEffort);
        Execution.Changed?.Invoke();
        return branch;
    }

    private string NextBranchTitle(string sourceTitle)
    {
        var cut = sourceTitle.LastIndexOf(" (", StringComparison.Ordinal);
        var baseTitle = cut > 0 && sourceTitle.EndsWith(')')
                        && int.TryParse(sourceTitle.AsSpan(cut + 2, sourceTitle.Length - cut - 3), out _)
            ? sourceTitle[..cut]
            : sourceTitle;
        if (baseTitle.Length == 0) return sourceTitle;

        var prefix = baseTitle + " (";
        var highest = 0;
        foreach (var title in _sessions.List().Select(summary => summary.Title))
        {
            if (!title.StartsWith(prefix, StringComparison.Ordinal) || !title.EndsWith(')')) continue;
            if (int.TryParse(title.AsSpan(prefix.Length, title.Length - prefix.Length - 1), out var number)
                && number > highest) highest = number;
        }

        return baseTitle + " (" + (highest + 1) + ")";
    }

    internal string SessionFilePath(string conversationId) => _sessions.Store.SessionFilePath(conversationId);
    internal string? StoredImagePath(string conversationId, string file)
        => _sessions.Store.ImagePath(conversationId, file);
    internal string? PlanBody(ChatTurn turn, out PlanBodyState state) => PlanStore.BodyOf(turn, _dataRoot, out state);
    internal string? ViewedConversationId => _active?.Id;

    private RepositorySnapshot? _repository;
    private Task<RepositorySnapshot>? _repositoryReading;
    private string? _repositoryReadingRoot;
    /// <summary>Raised when a read lands, so an open pane repaints. Nothing raises it on a timer.</summary>
    internal Action? RepositoryChanged;
    internal RepositorySnapshot? RepositoryFor(string? workspaceRoot)
        => _repository is { } snapshot
           && string.Equals(RepositoryKey(snapshot.WorkspaceRoot), RepositoryKey(workspaceRoot), StringComparison.Ordinal)
            ? snapshot
            : null;
    internal Task<RepositorySnapshot> RefreshRepositoryAsync(string conversationId)
    {
        var scope = Execution.ScopeFor(conversationId).Workspace;
        var root = scope.WorkspaceRoot ?? "";
        if (_repositoryReading is { } inFlight
            && string.Equals(RepositoryKey(_repositoryReadingRoot), RepositoryKey(root), StringComparison.Ordinal))
            return inFlight;
        var task = ReadRepositoryAsync(root, scope);
        _repositoryReading = task;
        _repositoryReadingRoot = root;
        return task;
    }

    private async Task<RepositorySnapshot> ReadRepositoryAsync(string workspaceRoot, WorkspaceToolScope scope)
    {
        // Counted where the read is really started, not where it is asked for: the coalesced path above never
        // gets here, so this number is the difference between a cache and a poll.
        RepositoryReadsForCheck++;
        try
        {
            var runner = new ProcessRunner(line => scope.Log?.Write(line));
            var state = await GitRepository.ReadAsync(workspaceRoot, runner, scope.SensitiveValues)
                .ConfigureAwait(false);
            var snapshot = new RepositorySnapshot(workspaceRoot, state);
            await Execution.ApplyOnUiAsync(() =>
            {
                _repository = snapshot;
                RepositoryChanged?.Invoke();
            }).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            // Cleared on the way out either way: a read that threw must not leave the pane waiting on a task
            // that will never answer, which is how one refused repository becomes a permanently stuck tab. The
            // root is compared first because a second read for a different directory may have taken the slot
            // while this one was running, and this one has no business clearing somebody else's.
            if (string.Equals(RepositoryKey(_repositoryReadingRoot), RepositoryKey(workspaceRoot),
                    StringComparison.Ordinal))
            {
                _repositoryReading = null;
                _repositoryReadingRoot = null;
            }
        }
    }


    private static bool IsListed(ConversationSummary summary) => summary.MessageCount > 0;
    /// <summary>What approving a plan writes as the user's instruction, <b>without</b> the plan under it. The body
    /// rides on the turn as a <see cref = "PlanReference"/> like the plan turn's own, and the request boundary puts
    /// the text back (<see cref = "MaterializePlans"/>), so the bytes the model is handed are the ones it is handed
    /// today while the session file names one document instead of holding two copies of it.</summary>


    private static string RepositoryKey(string? root) => WorkspacePaths.CanonicalRoot(root) ?? "";

    internal static string NormalizeMode(string? mode) => mode is ChatModes.Ask or ChatModes.Plan or ChatModes.Agent
        ? mode
        : ChatModes.Agent;

    internal int PruneEmptyConversations()
    {
        var emptyIds = _sessions.List().Where(summary => summary.MessageCount == 0 && Execution.RunFor(summary.Id)is null).Select(summary => summary.Id).ToList();
        foreach (var id in emptyIds)
            _sessions.Delete(id);
        if (_active is not null && emptyIds.Contains(_active.Id))
            _active = null;
        if (emptyIds.Count > 0)
            Execution.Changed?.Invoke();
        return emptyIds.Count;
    }

    internal string SessionImageDirectory(string conversationId) => _sessions.Store.SessionImageDirectory(conversationId);

}
