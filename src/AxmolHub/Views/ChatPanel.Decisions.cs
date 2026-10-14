using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AxmolHub.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace AxmolHub;

/// <summary>
/// The three decisions a parked run can owe — an action to approve, a plan to review, a question to answer — and
/// the one confirmation a running reply can owe, all of them asked in a strip above the composer rather than in a
/// transcript row a person had scrolled away from.
///
/// They live in one partial because they are one idea: something the person must answer before the composer
/// means what it looks like it means. A pending call, a pending plan and a pending question share a host (one
/// slot, so two cards can never stack); a steer confirmation gets its own strip because it must show the draft
/// while leaving the box the draft goes back to. Both strips sit above the frame and neither takes the input box
/// away: a decision is a question, and a question has never been a reason to throw away what somebody is writing.
/// </summary>
public partial class ChatPanel
{
    /// <summary>The call the host is currently asking about, or null. Kept beside the host rather than read
    /// back out of it so a refresh can tell "same decision, repaint" from "a different decision arrived".</summary>
    private string? _decisionCallId;

    /// <summary>The turn index of the plan the host is reviewing, or -1.</summary>
    private int _decisionPlanIndex = -1;

    /// <summary>Which plan option is armed. The letter keys and the pointer both choose it and Continue acts on
    /// what it says, so there is always exactly one.</summary>
    private string _planSelectedOption = ChatPlanStates.Approve;

    /// <summary>Which option of the question card is armed, or null when the armed answer is the free-text line —
    /// or nothing yet. Its own field rather than a second life for the plan's, because the plan's default is a
    /// meaning ("A is pre-armed, and it is the answer that costs nothing to leave chosen") and a question's is
    /// not: pre-arming one of four options would let a stray Enter commit a choice nobody made.</summary>
    private string? _questionSelectedLetter;

    /// <summary>A draft waiting for its second tap, with the session it was typed in: switching conversations
    /// must not carry it along, and coming back must not have lost it.</summary>
    private sealed record SteerDraft(string ConversationId, string Text, string? Context, IReadOnlyList<byte[]>? Pictures);

    private SteerDraft? _steerDraft;

    /// <summary>Set by the shell: how the panel asks for the inspector column, on a tab ("plan" or "changes")
    /// and for one specific plan turn (-1 for the newest). The plan's body lives nowhere else in the UI once the
    /// transcript row is a card, so this is the entrance — which is why the card hands over the index of the plan
    /// it names rather than letting the pane show a newer one under an older title.</summary>
    internal Func<string, int, bool>? OpenInspector { get; set; }

    /// <summary>Set by the shell: how the panel asks the inspector column to repaint with the transcript it is
    /// already showing. The panel knows when the transcript moved; only the shell knows whether a column is up at
    /// all, on which page, and whether the pane is currently covering the window.</summary>
    internal Action? RefreshInspector { get; set; }

    /// <summary>Set by the shell: which tab the reader is actually standing on, every time the pane changes it.
    /// A repaint asks for a tab by name, so a header click that the shell never heard about is undone by the next
    /// streamed token — the column comes back on the plan. The handler is one assignment and nothing else: it
    /// fires from inside <c>Reload</c>, and a repaint that repainted would be a loop.</summary>
    internal Action<string>? InspectorTabFollowed { get; set; }

    /// <summary>The transcript shape the open column was last painted from: which conversation, how many turns.
    /// A turn count rather than a token count, because everything the pane shows is a turn — a plan body, one row
    /// per write — and rebuilding it for each streamed character would re-render markdown for a change nobody
    /// can see.</summary>
    private string _inspectorSignature = "";

    private void RefreshInspectorIfTranscriptMoved()
    {
        var conversation = _chat?.ActiveConversation;
        var signature = conversation is null ? "" : conversation.Id + ":" + conversation.Messages.Count;
        if (signature == _inspectorSignature) return;
        // Nothing is wired yet while the constructor is still running — the shell assigns the seam after this
        // panel comes back — so nothing has been painted, and priming the signature here would promise a repaint
        // that never happened. The first real paint then re-primes it honestly.
        if (RefreshInspector is null) return;
        _inspectorSignature = signature;
        RefreshInspector?.Invoke();
    }

    /// <summary>The one inspector pane, kept for the window's life so switching tabs or conversations does not
    /// rebuild it and lose which file a person had expanded.</summary>
    private InspectorPanel? _inspector;

    /// <summary>
    /// Builds (once) and refreshes the inspector's content for the conversation on screen, landing on
    /// <paramref name="tab"/> and showing the plan <paramref name="turnIndex"/> names, or the newest one at -1.
    /// The pane is fed a snapshot rather than reaching into the workspace: the plan text
    /// is a plan turn's own markdown, and each file's diff is resolved lazily through
    /// <see cref="ChatWorkspace.DiffForChange"/> so a list of twelve writes reads no file until one is opened.
    /// </summary>
    internal Control? BuildInspectorContent(string tab, int turnIndex, bool mayReadRepository = true)
    {
        var conversation = _chat?.ActiveConversation;
        if (_inspector is null)
        {
            _inspector = new InspectorPanel();
            // The pane draws no control of its own any more: the two it used to carry — shut the column, lift the
            // pane over the window — are the title bar's, wired straight to the shell's own methods. What is left
            // here is the pane's *content* contract.
            // The pane asks; this is where the ask turns into a git read, because the panel has no session, no
            // log and no thread to wait on — and a pane that did its own I/O could not be fed a fixture by a check.
            _inspector.RepoRefreshRequested += RequestRepositoryRead;
            // An empty scope pointing at the one that is not goes out through the same door a run's chip uses:
            // landing on a tab can mean opening a column that is shut, and only the shell knows the page it is on.
            _inspector.ScopeJumpRequested += target => OpenInspector?.Invoke(target, -1);
            // And whichever tab the pane ends up showing, the shell is told, because the next repaint asks for a
            // tab by name. Without this the reader's own click is undone by the next streamed token.
            _inspector.TabSelected += tab => InspectorTabFollowed?.Invoke(tab);
        }

        var planText = PlanTextFor(conversation, turnIndex);
        var changes = ChatChanges.Of(conversation);
        var conversationId = conversation?.Id ?? "";
        var repository = _chat?.RepositoryFor(conversation?.WorkspaceRoot);
        // Entering the tab with nothing read for this directory is the moment to read it. Not before, and not on
        // a repaint: a session that never goes looking for repository state never pays for a git run, and the
        // transcript path that ends up here fires for every turn a running reply adds.
        if (mayReadRepository && string.Equals(tab, "repo", StringComparison.Ordinal) && repository is null)
            RequestRepositoryRead();
        _inspector.Reload(
            planText,
            changes,
            change => _chat is null
                ? (null, UndoCopyState.Unreadable)
                : (_chat.DiffForChange(change, conversationId, out var state), state),
            repository?.State,
            tab);
        // Painting by any route leaves the pane in step with the transcript, so the repaint that follows a
        // click would be a second rebuild of the same thing.
        _inspectorSignature = conversation is null ? "" : conversation.Id + ":" + conversation.Messages.Count;
        return _inspector;
    }

    /// <summary>Starts a repository read for the conversation on screen, and lets it finish on its own. The
    /// result arrives through <see cref="OnRepositoryChanged"/>, which repaints the pane if it is still up — a
    /// click on a tab should not hold the pointer waiting on four git calls.</summary>
    private void RequestRepositoryRead()
    {
        var conversation = _chat?.ActiveConversation;
        if (_chat is null || conversation is null) return;
        _ = _chat.RefreshRepositoryAsync(conversation.Id);
    }

    /// <summary>A read that landed repaints the pane, but only while the repository tab is the one being looked
    /// at. A reader who has since gone back to the plan must not have the column rewritten under them, and the
    /// snapshot is already stored for the next time they ask.</summary>
    private void OnRepositoryChanged()
    {
        if (_inspector is not { } pane || !pane.RepoTabVisibleForCheck) return;
        // Repaint rather than re-open, because a repaint never starts another read — which is what keeps this
        // from turning into a loop when the snapshot cannot satisfy the question the pane is asking.
        RefreshInspector?.Invoke();
    }

    /// <summary>The markdown of one plan: the turn the caller pointed at when that turn is still a plan of this
    /// conversation, otherwise the newest plan it holds. The fallback is what a stale pointer degrades to — a
    /// message deleted or regenerated since the card was drawn should move the pane to the plan that is really
    /// there, not leave it showing nothing.</summary>
    private string? PlanTextFor(Conversation? conversation, int turnIndex)
    {
        if (conversation is null) return null;
        if (turnIndex >= 0 && turnIndex < conversation.Messages.Count
            && conversation.Messages[turnIndex] is { Role: ChatRoles.Assistant, PlanApprovalState: { Length: > 0 } } chosen)
        {
            return PlanMarkdown(chosen);
        }

        return LatestPlanText(conversation);
    }

    /// <summary>The markdown of the newest plan this conversation produced — the pending one if a plan is
    /// waiting, otherwise the last one it answered, so the card and 「查看计划」 still show something after
    /// approval.</summary>
    private string? LatestPlanText(Conversation? conversation)
    {
        if (conversation is null) return null;
        string? latest = null;
        foreach (var turn in conversation.Messages)
        {
            if (turn.Role != ChatRoles.Assistant) continue;
            if (turn.PlanApprovalState is not { Length: > 0 }) continue;
            if (PlanMarkdown(turn) is { Length: > 0 } markdown) latest = markdown;
        }
        return latest;
    }

    /// <summary>
    /// What the pane shows for one plan turn: its body, wherever the turn says it is.
    ///
    /// <para><see cref="ChatWorkspace.PlanBody"/> is the one reading of the reference rule, so a plan whose body
    /// was filed and a plan written before the store existed are drawn by the same three lines.</para>
    ///
    /// <para>A document that is not where the turn points gets a sentence saying so rather than <c>null</c>:
    /// <see cref="InspectorPanel.Reload"/> reads an empty input as 「还没有生成计划」, and telling somebody who has
    /// a plan on the transcript that none was generated is a different claim than the one that is true.</para>
    /// </summary>
    private string? PlanMarkdown(ChatTurn turn)
    {
        if (_chat is null) return turn.Text;
        var body = _chat.PlanBody(turn, out var state);
        if (state is PlanBodyState.Read or PlanBodyState.NoReference) return body;
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("InspectorPlanGoneFormat"),
            turn.Plan?.Chars ?? 0,
            turn.Plan?.File ?? "");
    }

    // ── the host ──

    /// <summary>
    /// Brings the host in step with the transcript. Reads <b>only</b> persisted state — approval states, the
    /// frozen preview, the arguments — never <c>RunFor</c> or the run's phase: a decision can outlive its run
    /// (a restart, another window answering it), and the host has to show it either way. The temptation to gate
    /// on the run appears the moment the card becomes "the composer's", which is exactly when it would be wrong.
    /// </summary>
    private void RefreshDecisionHost()
    {
        var conversation = _chat?.ActiveConversation;
        string? callId = null;
        var planIndex = -1;
        ChatTurn? pendingCall = null;
        List<ChatTurn>? pendingGroup = null;
        if (conversation is not null)
        {
            // A call with its result anywhere in the transcript is settled business, whichever slot the result
            // sits in; the group is about what still has to happen, not about what is out of order.
            var answered = conversation.Messages
                .Where(other => other.Role == ChatRoles.Tool && other.ToolCallId is { Length: > 0 })
                .Select(other => other.ToolCallId!)
                .ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < conversation.Messages.Count; index++)
            {
                var turn = conversation.Messages[index];
                // A plan outranks a call only in the sense that the two cannot coexist in this build; the
                // branch is a clamp on a state a newer session file could carry, not a rule with teeth here.
                if (turn.PlanApprovalState == PlanApprovalStates.Pending) { planIndex = index; break; }
                if (turn.ApprovalState != ChatApprovalStates.Pending || callId is not null) continue;
                callId = turn.ToolCallId;
                pendingCall = turn;
                // One card for the response, not one per call. The group is every call of it still owed an answer,
                // because that is what the click starts: a call the mode let through is held back only while its
                // sibling waits, and a card naming one of two would be a promise about half of what is about to
                // run. A question is not part of it — nobody authorizes somebody else's answer, so it keeps its own
                // card and its own words. A call recorded before batches existed is a group of one.
                var batchId = turn.ApprovalBatchId ?? turn.ToolCallId;
                pendingGroup = turn.ToolName == AskUserTool.ToolName
                    ? [turn]
                    : conversation.Messages
                        .Where(other => other.Role == ChatRoles.Assistant && other.ToolCallId is { Length: > 0 } id
                                        && !answered.Contains(id)
                                        && (other.ApprovalBatchId ?? other.ToolCallId) == batchId
                                        && other.ToolName != AskUserTool.ToolName)
                        .ToList();
            }
        }

        // Which of the two a pending call is asking for is read off the call, not kept beside it: the arguments on
        // the turn are the whole record, and a card rebuilt after a restart has to reach the same answer the gate
        // reached. A question and an approval share the slot and the shape of the decision; they differ in every
        // word on the card, so the branch has to happen before anything is built.
        var asks = pendingCall?.ToolName == AskUserTool.ToolName;
        var same = callId == _decisionCallId && planIndex == _decisionPlanIndex;
        _decisionCallId = callId;
        _decisionPlanIndex = planIndex;

        if (conversation is null || (callId is null && planIndex < 0))
        {
            DecisionHost.IsVisible = false;
            DecisionHost.Child = null;
            return;
        }

        if (!same)
        {
            DecisionHost.Child = planIndex >= 0
                ? BuildPlanReviewHost(conversation.Id, planIndex)
                : asks
                    ? BuildQuestionCard(conversation.Id, callId!, pendingCall!)
                    : BuildToolApprovalCard(conversation.Id, callId!, pendingCall!, pendingGroup!);
            _planSelectedOption = ChatPlanStates.Approve;
            _questionSelectedLetter = null;
            MarkPlanSelection();
            if (asks) MarkQuestionSelection();
        }

        DecisionHost.IsVisible = true;
        // The composer keeps working: the input box is not covered, its draft is not cleared, and Enter still
        // means what it meant a second ago. A decision is answered by clicking the card, so nothing here needs
        // the keyboard to make the point.
        // A decision that just arrived takes the keyboard only when the box is empty, or the letter keys on it are
        // only reachable after something has been clicked: a key the window itself holds never travels down this
        // panel's route. Never on top of a draft — taking that focus is the interruption this whole strip exists to
        // avoid. And never on a repaint, which must not move a caret somebody is using.
        if (!same && (planIndex >= 0 || asks) && (InputBox.Text ?? "").Length == 0) DecisionHost.Focus();
    }

    /// <summary>Escape on the host answers it the quiet way: a pending call is refused (one Denied result, the
    /// model is not asked again), a pending plan is rejected (recorded, plan mode kept), a pending question is
    /// <i>skipped</i> — which is not the same answer and does not get the same sentence, because a refusal tells
    /// the model to go back and ask. All three hand the keyboard back, which is the point of a keyboard exit.</summary>
    private void DismissDecisionByEscape()
    {
        var conversation = _chat.ActiveConversation;
        if (conversation is null) return;
        if (_decisionPlanIndex >= 0)
            ResolvePlanApproval(conversation.Id, _decisionPlanIndex, PlanApprovalStates.Rejected);
        else if (_decisionCallId is { } callId)
        {
            if (IsPendingQuestion(callId)) ResolveQuestion(conversation.Id, callId, null, null, skipped: true);
            else ResolveApproval(conversation.Id, callId, approved: false, alwaysAllow: false);
        }
        InputBox.Focus();
    }

    /// <summary>Whether the call the host is asking about is a question rather than an action wanting permission.
    /// Read from the stored turn so Escape, the letter keys and the card itself cannot disagree about which one
    /// they are acting on after a restart.</summary>
    private bool IsPendingQuestion(string callId)
        => _chat?.ActiveConversation?.Messages.FirstOrDefault(
            turn => turn.ToolCallId == callId)?.ToolName == AskUserTool.ToolName;

    // ── content: a pending tool call ──

    /// <summary>
    /// The question, its frozen evidence, and its answers. The title names the tool because "run file_write"
    /// is not the decision — what it writes is, and that is the body. The risk tier picks the glyph because the
    /// tier is what makes this call ask at all.
    ///
    /// <para><paramref name="group"/> is every call still waiting from the one model response that asked for the
    /// card, which is the unit the person is answering: a provider may ask for several tools in a single reply, and
    /// one click either runs that group or stops it. A group of one renders exactly the card this used to be.</para>
    /// </summary>
    private Control BuildToolApprovalCard(string conversationId, string callId, ChatTurn turn,
        IReadOnlyList<ChatTurn> group)
    {
        // Not a StackPanel: the host clips at its own MaxHeight and the detail block grows past that on expand, so
        // the middle row has to be the one that gives way. A star row takes whatever the fixed rows leave, which
        // keeps the answer buttons inside the clip at every height — the footer is the part that must be reachable.
        var card = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };

        var risks = group
            .Select(member => _chat.RiskFor(conversationId, member.ToolName ?? "", member.ToolArguments))
            .ToList();
        // The header carries the worst tier in the group, because that is what the answer agrees to: a card for two
        // writes and a screen capture has to read like a screen capture. Each row keeps its own evidence below.
        var risk = risks.Contains(ToolRisk.SystemCommand) ? ToolRisk.SystemCommand
            : risks.Contains(ToolRisk.WorkspaceWrite) ? ToolRisk.WorkspaceWrite
            : _chat.RiskFor(conversationId, turn.ToolName ?? "", turn.ToolArguments);
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var glyph = new Path
        {
            Classes = { "approval-risk" },
            // The tier picks the glyph, and the two extremes are the ones a person has to tell apart at a glance:
            // full-access geometry for something that reaches past the sandbox, the ask geometry for a write.
            // Everything below that — a command inside the session's own directory, a note, a peer session's
            // history — takes the auto glyph, which until this tier existed had nothing to draw.
            Data = ThemeGeometry(risk switch
            {
                ToolRisk.SystemCommand => "Hub.Icon.PermissionFull",
                ToolRisk.WorkspaceWrite => "Hub.Icon.PermissionAsk",
                _ => "Hub.Icon.PermissionAuto",
            }),
        };
        if (risk == ToolRisk.SystemCommand) glyph.Classes.Add("system-command");
        title.Children.Add(glyph);
        title.Children.Add(new TextBlock
        {
            Classes = { "approval-title" },
            // The tool's name when there is one thing to say, the count when the model asked for several: the
            // decision is about the group, and the rows beneath it name the parts.
            Text = group.Count == 1
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatApprovalWantsFormat"), turn.ToolName ?? "")
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatApprovalBatchWantsFormat"), group.Count),
        });
        Grid.SetColumn(title.Children[1], 1);
        Grid.SetRow(title, 0);
        card.Children.Add(title);

        // The evidence, bounded: a 400-line diff must not push the transcript out of the window, and the input box
        // under the card must not be pushed out either. Past twelve lines the block says so and offers the rest.
        var detail = new StackPanel { Spacing = 4 };
        var longestPreview = 0;
        foreach (var member in group)
        {
            // Named per member only when the card has more than one to tell apart; a single call says its own name
            // in the title already, and repeating it under the title is the second label the charter forbids.
            if (group.Count > 1)
                detail.Children.Add(new TextBlock
                {
                    // Two classes: the label's own look, and the hook's handle. A card that quietly dropped a
                    // member row would still answer the batch correctly, so the rows are counted, not read.
                    Classes = { "approval-label", "approval-member" },
                    Text = member.ToolName ?? "",
                });
            AddApprovalDetail(detail, "ChatApprovalArguments", member.ToolArguments);
            AddApprovalDetail(detail, "ChatApprovalChangePreview", member.ApprovalPreview);
            longestPreview = Math.Max(longestPreview, (member.ApprovalPreview ?? "").Split('\n').Length);
        }

        var scroll = new ScrollViewer
        {
            Name = "ApprovalDetailScroll",
            Content = detail,
            MaxHeight = 120,
            Margin = new Thickness(0, 8, 0, 0),
            ClipToBounds = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        card.Children.Add(scroll);

        // One number for the whole card, and the ceiling it measures against is the scroll's own: a group whose
        // evidence does not fit has to say so once, the same way one write's diff does.
        if (longestPreview > ApprovalExpandThreshold)
        {
            var expand = new ToggleButton
            {
                Name = "ApprovalExpandButton",
                Classes = { "approval-action", "approval-secondary" },
                Content = HubStrings.Get("ChatApprovalExpand"),
                Tag = "ChatApprovalExpand",
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            expand.Click += (_, _) =>
            {
                // Unbounded on expand, because the star row is what enforces the ceiling now: asking the scroll for
                // a number here is how the footer ended up under the clip when the number was larger than the host.
                scroll.MaxHeight = expand.IsChecked == true ? double.PositiveInfinity : 120;
                expand.Content = HubStrings.Get(
                    expand.IsChecked == true ? "ChatApprovalCollapse" : "ChatApprovalExpand");
            };
            Grid.SetRow(expand, 2);
            card.Children.Add(expand);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        // "Always allow" renamed to its consequence: the grant is app-wide now, and the bare word promised more
        // than it does. It is not offered on the tier that reaches past the sandbox, because a button that changed
        // nothing is a button that teaches the ladder is decoration — the gate ignores a grant on that tier. And it
        // is not offered on a group, because a grant is about one tool while the card is about several: clicking it
        // here would stop future questions for tools the person has not seen one at a time.
        var secondary = new StackPanel
        {
            Name = "ApprovalSecondaryActions",
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (group.Count == 1 && risk != ToolRisk.SystemCommand)
        {
            var always = new Button
            {
                Classes = { "approval-action", "approval-secondary" },
                Content = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ApprovalAllowAlwaysForTool"), turn.ToolName ?? ""),
                Tag = "ApprovalAllowAlways",
            };
            ToolTip.SetTip(always, HubStrings.Get("ApprovalAllowAlwaysTip"));
            always.Click += (_, _) => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: true);
            secondary.Children.Add(always);
        }
        footer.Children.Add(secondary);

        var primary = new StackPanel
        {
            Name = "ApprovalPrimaryActions",
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var deny = new Button
        {
            Classes = { "approval-action", "approval-deny" },
            Content = HubStrings.Get("ApprovalDeny"),
            Tag = "ApprovalDeny",
        };
        deny.Click += (_, _) => ResolveApproval(conversationId, callId, approved: false, alwaysAllow: false);
        var allow = new Button
        {
            Classes = { "approval-action", "approval-allow" },
            Content = HubStrings.Get("ApprovalAllow"),
            Tag = "ApprovalAllow",
        };
        allow.Click += (_, _) => ResolveApproval(conversationId, callId, approved: true, alwaysAllow: false);
        primary.Children.Add(deny);
        primary.Children.Add(allow);
        Grid.SetColumn(primary, 1);
        footer.Children.Add(primary);
        Grid.SetRow(footer, 3);
        card.Children.Add(footer);

        return card;
    }

    /// <summary>Twelve lines is where the bounded block stops being a glance. Below it an expand button would
    /// be an affordance for content that already fits, which is a lie with a border.</summary>
    private const int ApprovalExpandThreshold = 12;

    /// <summary>The transcript's signpost for a decision still owed: one muted line, pressable, pointing at the
    /// composer. Not a second card — the buttons live in one place or they live in none.</summary>
    private Control BuildApprovalPendingLine(ChatTurn turn)
    {
        var line = new Border { Classes = { "approval-pending-line" } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(new Path { Data = ThemeGeometry("Hub.Icon.Chevron") });
        row.Children.Add(new TextBlock
        {
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                // Two different waits, and the row has to say which. "等批准" over a question tells the person
                // their job is to permit something, when the run is actually stopped for information only they
                // have — and it is the only sentence about this decision a reader who scrolled away will see.
                HubStrings.Get(turn.ToolName == AskUserTool.ToolName
                    ? "ChatQuestionWaitingLine"
                    : "ChatApprovalWaitingLine"), turn.ToolName ?? ""),
        });
        Grid.SetColumn(row.Children[1], 1);
        line.Child = row;
        line.PointerPressed += (_, _) =>
        {
            DecisionHost.BringIntoView();
            DecisionHost.Focus();
        };
        return line;
    }

    // ── content: a question the model asked ──

    /// <summary>
    /// The question, its choices, and the two ways out of it. Same slot, same lettered list as the plan review,
    /// because it is the same kind of thing — a decision the run is stopped on — and a card that looked different
    /// each time the app asked would teach nobody anything.
    ///
    /// <para>Not a StackPanel: the host clips at its own MaxHeight, and four options under a wrapped question is
    /// already past it on a short window. The list is the row that gives way, so 「继续」 stays inside the clip at
    /// every height — the footer is the part that has to be reachable, and a card whose answer button was cut off
    /// is a run parked forever.</para>
    /// </summary>
    private Control BuildQuestionCard(string conversationId, string callId, ChatTurn turn)
    {
        // The gate only parks a question that parses, and the arguments on the turn are the whole record, so a
        // card rebuilt after a restart reaches the same answer here. If they no longer read as a question the
        // transcript has moved under us: fall back to the signpost line rather than draw an empty card.
        if (!AskUserQuestion.TryParse(turn.ToolArguments, out var question, out var choices, out _))
            return BuildApprovalPendingLine(turn);

        var card = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var title = new TextBlock
        {
            Classes = { "question-title" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatQuestionTitleFormat"), question),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(title, 0);
        card.Children.Add(title);

        var options = new StackPanel { Name = "QuestionOptions", Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var choice in choices) options.Children.Add(BuildQuestionOptionRow(conversationId, callId, choice));
        // The free-text line belongs to the list and not beside it: it is the fifth answer, and putting it outside
        // the scroll would make it the one thing that survives a clipped card — which reads as the only choice.
        options.Children.Add(BuildQuestionFreeRow(conversationId, callId));
        var scroll = new ScrollViewer
        {
            Name = "QuestionListScroll",
            Content = options,
            ClipToBounds = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        card.Children.Add(scroll);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var skip = new Button
        {
            Name = "QuestionSkipButton",
            Classes = { "plan-review-cancel" },
            Tag = "ChatQuestionSkip",
        };
        var skipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        skipRow.Children.Add(new TextBlock { Text = HubStrings.Get("ChatQuestionSkip"), VerticalAlignment = VerticalAlignment.Center });
        skipRow.Children.Add(new TextBlock { Classes = { "plan-review-kbd" }, Text = "Esc" });
        skip.Content = skipRow;
        skip.Click += (_, _) => ResolveQuestion(conversationId, callId, null, null, skipped: true);
        var continueButton = new Button
        {
            Name = "QuestionContinueButton",
            Classes = { "approval-action", "approval-allow" },
            Content = HubStrings.Get("Continue"),
            Tag = "ChatQuestionContinue",
            IsEnabled = false,
        };
        continueButton.Click += (_, _) => CommitQuestion(conversationId, callId);
        right.Children.Add(skip);
        right.Children.Add(continueButton);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        Grid.SetRow(footer, 2);
        card.Children.Add(footer);

        return card;
    }

    /// <summary>One lettered choice. The cap is the keyboard shortcut and the row is the click, and both land in
    /// <see cref="ChooseQuestionOption"/> so the two cannot drift apart.</summary>
    private Control BuildQuestionOptionRow(string conversationId, string callId, AskUserQuestion.Option choice)
    {
        var row = new Button
        {
            Classes = { "question-option" },
            // The letter, not the label: labels are model-authored text and a check that matched on one would
            // break the day a fixture reworded it.
            Tag = choice.Letter,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
        grid.Children.Add(new Border
        {
            Classes = { "question-key" },
            Child = new TextBlock { Text = choice.Letter },
        });
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Classes = { "question-label" }, Text = choice.Label });
        // A detail the model did not write is not a row to leave in — an empty line under every label would be
        // chrome, and the charter's answer to that is to remove it.
        if (choice.Detail.Length > 0)
            text.Children.Add(new TextBlock { Classes = { "question-detail" }, Text = choice.Detail });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        row.Content = grid;
        row.Click += (_, _) => ChooseQuestionOption(choice.Letter);
        return row;
    }

    /// <summary>The line that is not one of the model's options. It carries no letter cap: it is not a fifth choice
    /// the assistant authored, and giving it a key would let a keyboard press pick an answer nobody wrote.</summary>
    private Control BuildQuestionFreeRow(string conversationId, string callId)
    {
        var row = new Button
        {
            Classes = { "question-option" },
            // The same token <see cref="ChooseQuestionOption"/> arms with, because the row's Tag is what
            // MarkQuestionSelection compares the armed answer against. A second spelling here would leave the
            // free line never lighting up no matter what was chosen, and every other cell would still pass.
            Tag = FreeAnswer,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
        grid.Children.Add(new Border { Classes = { "question-key" }, Child = new TextBlock { Text = "✎" } });
        var box = new TextBox
        {
            Name = "QuestionAnswerBox",
            Classes = { "question-free" },
            PlaceholderText = HubStrings.Get("ChatQuestionFreeLabel"),
        };
        // Enter inside the line is "send this answer" and Shift+Enter is a newline, exactly as in the plan's
        // revision line. The AcceptsReturn box swallows Enter before any bubble handler sees it, so this has to
        // be a tunnel handler.
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            CommitQuestion(conversationId, callId);
        }, RoutingStrategies.Tunnel);
        box.PropertyChanged += (_, e) =>
        {
            // The caret is the choosing, as in the plan card: focus arms the line, typed text only re-reads
            // Continue's enabled state — text left from a previous draft must not outvote an explicit click.
            if (e.Property == InputElement.IsFocusedProperty) ChooseQuestionOption(free: true);
            else if (e.Property == TextBox.TextProperty) MarkQuestionSelection();
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        row.Content = grid;
        row.Click += (_, _) => ChooseQuestionOption(free: true, focusBox: box);
        return row;
    }

    /// <summary>Arms one answer. Every door — the pointer, the letter key, the caret landing in the free line —
    /// comes through here, so "the key does what the click does" is one implementation rather than two.</summary>
    private void ChooseQuestionOption(string? letter = null, bool free = false, TextBox? focusBox = null)
    {
        _questionSelectedLetter = free ? FreeAnswer : letter;
        MarkQuestionSelection();
        focusBox?.Focus();
    }

    /// <summary>The armed-answer marker for the free-text line, and the same string the row carries as its Tag.
    /// One constant because they are compared to each other. No model-authored letter collides with it: options
    /// are A to D.</summary>
    private const string FreeAnswer = "free";

    private TextBox? FindQuestionAnswerBox()
        => DecisionHost.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "QuestionAnswerBox");

    /// <summary>Paints the armed row and re-reads Continue. Nothing armed is a real state here, unlike the plan
    /// card: an empty answer would reach the model as a tool result with no content in it, which some bridges
    /// reject outright and the model reads as "no preference" at best.</summary>
    private void MarkQuestionSelection()
    {
        if (DecisionHost.Child is not Grid card) return;
        var options = card.Children.OfType<ScrollViewer>().FirstOrDefault(scroll => scroll.Name == "QuestionListScroll")
            ?.Content as StackPanel;
        if (options is null) return;
        foreach (var row in options.Children.OfType<Button>())
            row.Classes.Set("selected", Equals(row.Tag as string, _questionSelectedLetter));

        var continueButton = card.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "QuestionContinueButton");
        if (continueButton is null) return;
        var typed = (_questionSelectedLetter == FreeAnswer
            ? FindQuestionAnswerBox()?.Text ?? ""
            : "").Trim();
        continueButton.IsEnabled = _questionSelectedLetter is { Length: > 0 } armed
                                   && (armed != FreeAnswer || typed.Length > 0);
    }

    /// <summary>The letter keys arm a question's options. The same three gates as the plan card, and the third one
    /// is the one that matters more here: with the caret in the free-text line, A and B are letters.</summary>
    private bool TryChooseQuestionOptionByKey(KeyEventArgs e)
    {
        if (_decisionCallId is not { } callId || !DecisionHost.IsVisible) return false;
        if (!IsPendingQuestion(callId)) return false;
        if (e.KeyModifiers != KeyModifiers.None) return false;
        if (e.Source is TextBox) return false;
        var letter = e.Key switch
        {
            Key.A => "A",
            Key.B => "B",
            Key.C => "C",
            Key.D => "D",
            _ => null,
        };
        if (letter is null) return false;
        // Only a key that names an option on this card arms anything. Without the test, D on a two-option question
        // would silently select nothing and eat the keystroke.
        if (!QuestionOffers(callId, letter)) return false;
        ChooseQuestionOption(letter);
        e.Handled = true;
        return true;
    }

    /// <summary>Whether the parked question actually offers this letter.</summary>
    private bool QuestionOffers(string callId, string letter)
    {
        var turn = _chat?.ActiveConversation?.Messages.FirstOrDefault(candidate => candidate.ToolCallId == callId);
        return turn is not null
               && AskUserQuestion.TryParse(turn.ToolArguments, out _, out var choices, out _)
               && choices.Any(choice => choice.Letter == letter);
    }

    /// <summary>Hands the armed answer to the workspace, which words it and resumes the run. Nothing armed is
    /// already kept from reaching here by Continue's disabled state; the empty check is the same rule one more
    /// time, because Enter on the free line arrives by a route that never passes the button.</summary>
    private void CommitQuestion(string conversationId, string callId)
    {
        if (_questionSelectedLetter == FreeAnswer)
        {
            var words = (FindQuestionAnswerBox()?.Text ?? "").Trim();
            if (words.Length == 0) return;
            ResolveQuestion(conversationId, callId, null, words, skipped: false);
            return;
        }

        if (_questionSelectedLetter is { Length: > 0 } letter)
            ResolveQuestion(conversationId, callId, letter, null, skipped: false);
    }

    /// <summary>A refusal is spoken out loud rather than swallowed, as everywhere else in this strip: a card that
    /// did nothing after a click reads as a broken button, and here it also leaves a parked run looking alive.</summary>
    private void ResolveQuestion(
        string conversationId, string callId, string? letter, string? ownWords, bool skipped)
    {
        if (_chat.TryResolveQuestion(conversationId, callId, letter, ownWords, skipped, out var refusalKey)) return;
        AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
    }

    // ── content: a pending plan ──

    /// <summary>
    /// The review surface: a flat lettered list and a footer whose left half is deliberately empty — A and B
    /// already say everything, and a third label there would be the charter's second telling. B is the revision
    /// line itself rather than a box that opens under it, because nothing a person does to this card is allowed to
    /// move anything else on it. There is no "exit plan mode and I will prompt myself" row: the owner removed it,
    /// and <see cref="ChatWorkspace.TryResolvePlanApproval"/> has no such decision either.
    /// </summary>
    private Control BuildPlanReviewHost(string conversationId, int turnIndex)
    {
        var host = new StackPanel { Spacing = 6 };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Name = "PlanReviewTitle",
            Classes = { "plan-review-title" },
            Text = HubStrings.Get("ChatPlanReviewTitle"),
        });
        var open = new Button
        {
            Name = "PlanReviewOpenButton",
            Classes = { "plan-review-open" },
            Tag = "ChatPlanReviewOpen",
        };
        // No glyph on this one: the words already say where they go, and a hand-concatenated arrow is a string
        // no translation table owns.
        open.Content = HubStrings.Get("ChatPlanReviewOpen");
        // The one invitation to go and read it. The transcript's card opens the same pane, but this row is where
        // the decision is being asked and the card may already be scrolled out of sight — and it carries the
        // index of the plan under review, so the two entrances can never disagree about which plan they show.
        open.Click += (_, _) => OpenInspector?.Invoke("plan", turnIndex);
        Grid.SetColumn(open, 1);
        header.Children.Add(open);
        host.Children.Add(header);

        var options = new StackPanel { Name = "PlanReviewOptions", Spacing = 2 };
        options.Children.Add(BuildPlanOptionRow(conversationId, turnIndex, "A",
            ChatPlanStates.Approve, "ChatPlanOptionApprove"));
        options.Children.Add(BuildPlanOptionRow(conversationId, turnIndex, "B",
            ChatPlanStates.Revise, "ChatPlanOptionRevise"));
        host.Children.Add(options);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var cancel = new Button
        {
            Name = "PlanCancelButton",
            Classes = { "plan-review-cancel" },
            Tag = "ChatPlanCancel",
        };
        var cancelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        cancelRow.Children.Add(new TextBlock { Text = HubStrings.Get("Cancel"), VerticalAlignment = VerticalAlignment.Center });
        cancelRow.Children.Add(new TextBlock { Classes = { "plan-review-kbd" }, Text = "Esc" });
        cancel.Content = cancelRow;
        cancel.Click += (_, _) =>
            ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.Rejected);
        var continueButton = new Button
        {
            Name = "PlanContinueButton",
            Classes = { "approval-action", "approval-allow" },
            Content = HubStrings.Get("ChatPlanReviewContinue"),
            Tag = "ChatPlanContinue",
        };
        continueButton.Click += (_, _) => CommitPlanReview(conversationId, turnIndex);
        right.Children.Add(cancel);
        right.Children.Add(continueButton);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        host.Children.Add(footer);

        return host;
    }

    private Control BuildPlanOptionRow(string conversationId, int turnIndex, string letter, string option,
        string textKey)
    {
        var row = new Button
        {
            Classes = { "plan-review-option" },
            Tag = option == ChatPlanStates.Approve ? "ChatPlanApprove" : "ChatPlanRevise",
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
        grid.Children.Add(new Border { Classes = { "plan-review-key" }, Child = new TextBlock { Text = letter } });

        TextBox? box = null;
        if (option == ChatPlanStates.Approve)
        {
            grid.Children.Add(new TextBlock { Classes = { "plan-review-label" }, Text = HubStrings.Get(textKey) });
        }
        else
        {
            // The revision line is option B rather than a panel that opens under it: at rest it is one more line of
            // the list, and entering it shows a caret and nothing else. It carries the option's own words as its
            // placeholder, so being shown the choice and being told what to write are the same line.
            box = new TextBox
            {
                Name = "PlanFeedbackBox",
                Classes = { "plan-feedback" },
                PlaceholderText = HubStrings.Get(textKey),
            };
            ToolTip.SetTip(box, HubStrings.Get("ChatPlanFeedbackPlaceholder"));
            // Enter inside the feedback is "send this revision"; Shift+Enter is a newline. The AcceptsReturn
            // box swallows Enter before any bubble handler sees it, so this is a tunnel handler like the
            // composer's own.
            box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
                e.Handled = true;
                CommitPlanReview(conversationId, turnIndex);
            }, RoutingStrategies.Tunnel);
            box.PropertyChanged += (_, e) =>
            {
                // The caret is the choosing: a pointer or a tab that lands here has picked B. Typed text does not
                // pick it, because a line still holding last round's feedback must not outvote an explicit click
                // on A — but every text change re-reads Continue's enabled state, which nothing else repaints.
                if (e.Property == InputElement.IsFocusedProperty) ChoosePlanOption(ChatPlanStates.Revise);
                else if (e.Property == TextBox.TextProperty) MarkPlanSelection();
            };
            grid.Children.Add(box);
        }
        Grid.SetColumn(grid.Children[1], 1);

        row.Content = grid;
        row.Click += (_, _) => ChoosePlanOption(option, box);
        return row;
    }

    /// <summary>Arms one option and, for the revision line, puts the caret in it. The pointer, the letter key and
    /// the line's own focus all arrive here, so "the key does what the click does" is one implementation rather
    /// than two that drift.</summary>
    private void ChoosePlanOption(string option, TextBox? feedbackBox = null)
    {
        _planSelectedOption = option;
        MarkPlanSelection();
        feedbackBox?.Focus();
    }

    /// <summary>
    /// The letter keys choose a plan option, which is what makes the caps on the rows a shortcut rather than
    /// decoration. Three gates, each one load-bearing: the plan card has to be the thing that is up (the tool
    /// approval shares this host and has no options), no modifier may be down (Ctrl+A is select-all, Alt+B belongs
    /// to somebody else), and the key must not have come from a text box — while the caret is in the revision
    /// line, A and B are letters. Returns whether the key was taken, so a check can assert the negative half too.
    /// </summary>
    private bool TryChoosePlanOptionByKey(KeyEventArgs e)
    {
        if (_decisionPlanIndex < 0 || !DecisionHost.IsVisible) return false;
        if (e.KeyModifiers != KeyModifiers.None) return false;
        if (e.Source is TextBox) return false;
        var option = e.Key switch
        {
            Key.A => ChatPlanStates.Approve,
            Key.B => ChatPlanStates.Revise,
            _ => null,
        };
        if (option is null) return false;
        ChoosePlanOption(option, option == ChatPlanStates.Revise ? FindPlanFeedbackBox() : null);
        e.Handled = true;
        return true;
    }

    private TextBox? FindPlanFeedbackBox()
        => DecisionHost.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "PlanFeedbackBox");

    /// <summary>Applies the armed option's look and the continue button's enabled state. B with an empty revision
    /// line disables continue rather than sending nothing: a tap that looks like focusing a text box must not be
    /// the tap that spends a request.</summary>
    private void MarkPlanSelection()
    {
        if (DecisionHost.Child is not StackPanel host) return;
        var options = host.Children.OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Name == "PlanReviewOptions");
        if (options is null) return;
        foreach (var row in options.Children.OfType<Button>())
        {
            var isApprove = row.Tag as string == "ChatPlanApprove";
            row.Classes.Set("selected", (_planSelectedOption == ChatPlanStates.Approve) == isApprove);
        }

        var continueButton = host.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "PlanContinueButton");
        if (continueButton is null) return;
        var feedback = host.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(candidate => candidate.Name == "PlanFeedbackBox");
        continueButton.IsEnabled = _planSelectedOption == ChatPlanStates.Approve
                                   || (feedback?.Text ?? "").Trim().Length > 0;
    }

    /// <summary>
    /// Executes the selected row. Approve hands the plan to the workspace, which flips the session into Agent mode
    /// and starts the run; the composer's plan chip goes with that flip, because it reads the session's mode rather
    /// than keeping a copy of it here.
    ///
    /// Revise records the decision and then sends the feedback as an ordinary user message through
    /// <see cref="ChatWorkspace.TryEnqueueSend"/>: the pending-plan refusal inside it keys on
    /// <c>PlanApprovalState == Pending</c>, which the decision has just cleared, so the whole send path — busy,
    /// queue, parallel limit, picture admission — comes for free instead of being re-implemented here.
    /// </summary>
    private void CommitPlanReview(string conversationId, int turnIndex)
    {
        if (_planSelectedOption == ChatPlanStates.Approve)
        {
            ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.Approved);
            return;
        }

        var feedback = FindPlanFeedbackBox()?.Text ?? "";
        if (feedback.Trim().Length == 0) return;

        ResolvePlanApproval(conversationId, turnIndex, PlanApprovalStates.RevisionRequested);
        if (!_chat.TryEnqueueSend(conversationId, feedback.Trim(), null, null, out var refusalKey))
        {
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
            return;
        }
        RememberSentText(feedback.Trim());
        InputBox.Focus();
    }

    /// <summary>
    /// The transcript's whole presence for a plan: one bordered card naming the plan and where it stands, and
    /// none of its body. A plan runs to screens, and its row used to run with it — the conversation got pushed
    /// out of view to display a document that is read once, in one sitting, somewhere with room to scroll. The
    /// body is filed in <see cref="PlanStore"/> and the turn carries its name, so the card is drawn from the
    /// title frozen at that moment and painting a transcript opens no file; this is a decision about painting,
    /// not about what was said.
    ///
    /// One card carries the disposition instead of a card plus a record line, because after a decision the two
    /// would be the same sentence twice. It is a button rather than a border: it has to take Enter and be
    /// reachable by Tab, and a check has to be able to press it through its own
    /// <see cref="Button.ClickEvent"/>.
    /// </summary>
    private Control BuildPlanCard(ChatTurn turn, int turnIndex)
    {
        var stateKey = PlanCardStateKey(turn.PlanApprovalState ?? PlanApprovalStates.Pending);
        var title = PlanCardTitle(turn);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        row.Children.Add(new Path { Classes = { "plan-card-icon" }, Data = ThemeGeometry("Hub.Icon.ChatModePlan") });
        row.Children.Add(new TextBlock { Classes = { "plan-card-label" }, Text = HubStrings.Get(stateKey) });
        Grid.SetColumn(row.Children[1], 1);
        row.Children.Add(new TextBlock { Classes = { "plan-card-title" }, Text = title });
        Grid.SetColumn(row.Children[2], 2);

        var card = new Button
        {
            Name = "PlanCard",
            Classes = { "plan-card" },
            Tag = stateKey,
            Content = row,
        };
        // The title is trimmed on the card, so the tooltip is where the whole of it lives — and it says no more
        // than that. The card's affordance is a hand cursor and a brightening frame; a "click to open" line would
        // be the charter's second telling, since 「查看计划」 in the composer is the one place that invites.
        ToolTip.SetTip(card, title);
        card.Click += (_, _) => OpenInspector?.Invoke("plan", turnIndex);
        return card;
    }

    private static string PlanCardStateKey(string state) => state switch
    {
        PlanApprovalStates.Pending => "ChatPlanPending",
        PlanApprovalStates.Approved => "ChatPlanApproved",
        PlanApprovalStates.RevisionRequested => "ChatPlanRevisionRequested",
        _ => "ChatPlanRejected",
    };

    /// <summary>What the card calls this plan: the title frozen on the turn when its body was filed, and the same
    /// derivation run over the turn's own text for a plan whose body never left it. Neither branch opens a file,
    /// so painting a transcript of a hundred turns costs no disk read — which is why the title is stored rather
    /// than read back out of the document every time.</summary>
    private static string PlanCardTitle(ChatTurn turn)
    {
        var title = turn.Plan?.Title ?? PlanStore.TitleOf(turn.Text);
        return title.Length > 0 ? title : HubStrings.Get("ChatPlanCardUntitled");
    }

    // ── the steer-confirm strip ──

    /// <summary>
    /// Puts a draft on the strip instead of steering with it. A steer is the routing table's strongest signal and
    /// it joins a conversation already in progress, so the first Enter only shows what would be said and the
    /// second says it; the draft leaves the box so the composer reads as free again, exactly like the reference
    /// it was drawn from. Nothing is cancelled either way — the strip is a review, not a warning.
    /// </summary>
    private void ShowSteerConfirm(string text, string? context, IReadOnlyList<byte[]>? pictures)
    {
        var conversation = _chat.ActiveConversation;
        if (conversation is null) return;
        _steerDraft = new SteerDraft(conversation.Id, text, context, pictures);
        InputBox.Text = "";
        _pendingPictures.Clear();
        _contextAttachments.Clear();
        RenderContextAttachments();
        RefreshSteerConfirm();
        InputBox.Focus();
    }

    /// <summary>Rebuilds the strip from <see cref="_steerDraft"/>. Also the place where a draft whose reply
    /// finished while it waited stops reading as an interjection: nothing is being interrupted any more, so
    /// the primary action says send.</summary>
    private void RefreshSteerConfirm()
    {
        var draft = _steerDraft;
        if (draft is null || _chat?.ActiveConversation?.Id != draft.ConversationId)
        {
            SteerConfirmHost.IsVisible = false;
            SteerConfirmHost.Child = null;
            return;
        }

        var stillStreaming = _chat.RunFor(draft.ConversationId) is { IsStreaming: true };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Classes = { "steer-confirm-text" }, Text = draft.Text });
        if (draft.Pictures is { Count: > 0 } pictures)
            left.Children.Add(new TextBlock
            {
                Classes = { "steer-confirm-suffix" },
                Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatSteerPicturesSuffix"), pictures.Count),
            });
        row.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var commit = new Button
        {
            Name = "SteerCommitButton",
            Classes = { "steer-commit" },
            Tag = "SteerCommit",
        };
        var commitRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var keyGlyph = new TextBlock { Classes = { "steer-commit-key" }, Text = "↵" };
        keyGlyph.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("Hub.Font.Ui"));
        commitRow.Children.Add(keyGlyph);
        commitRow.Children.Add(new TextBlock
        {
            Text = HubStrings.Get(stillStreaming ? "ChatSteer" : "Send"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        commit.Content = commitRow;
        ToolTip.SetTip(commit, HubStrings.Get(stillStreaming ? "ChatSteerConfirmTip" : "Send"));
        commit.Click += (_, _) => CommitSteer();
        actions.Children.Add(commit);
        actions.Children.Add(IconActionButton("ChatSteerEdit", "Hub.Icon.Edit", EditSteerDraft));
        actions.Children.Add(IconActionButton("ChatSteerDiscard", "Hub.Icon.Trash", DiscardSteer));
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        SteerConfirmHost.Child = row;
        SteerConfirmHost.IsVisible = true;
    }

    /// <summary>The second tap. If the reply finished in the meantime this is an ordinary send — the strip has
    /// already relabelled itself for that case, and falling through to the send path is what keeps the label
    /// honest rather than decorative.</summary>
    private void CommitSteer()
    {
        var draft = _steerDraft;
        if (draft is null) return;
        _steerDraft = null;

        if (_chat.RunFor(draft.ConversationId) is not { IsStreaming: true })
        {
            SteerConfirmHost.IsVisible = false;
            SteerConfirmHost.Child = null;
            if (!_chat.TryEnqueueSend(draft.ConversationId, draft.Text, draft.Context, draft.Pictures,
                    out var refusalKey))
            {
                AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
                // The tap spent nothing, so the sentence goes back on the strip that held it: this is a review, not
                // a cheque. Reachable because the box stays live under a waiting plan, so a steer can be armed and
                // then refused by the very plan the strip now shares its space with.
                _steerDraft = draft;
                RefreshSteerConfirm();
                UpdateSendState();
                return;
            }
            RememberSentText(draft.Text);
            UpdateSendState();
            return;
        }

        if (!_chat.TrySteer(draft.ConversationId, draft.Text, draft.Context, draft.Pictures))
        {
            // Lost the race to a finished run: the draft goes back to the box rather than vanishing.
            _steerDraft = draft;
            RefreshSteerConfirm();
            UpdateSendState();
            return;
        }

        RememberSentText(draft.Text);
        SteerConfirmHost.IsVisible = false;
        SteerConfirmHost.Child = null;
        if (_live is { } live) live.Status.Text = HubStrings.Get("ChatSteering");
        UpdateSendState();
    }

    /// <summary>Puts the draft back where it was typed, caret at the end, and closes the strip. The only way a
    /// draft returns to the box; the only way it is destroyed is <see cref="DiscardSteer"/>.</summary>
    private void EditSteerDraft()
    {
        var draft = _steerDraft;
        if (draft is null) return;
        _steerDraft = null;
        InputBox.Text = draft.Text;
        InputBox.CaretIndex = draft.Text.Length;
        SteerConfirmHost.IsVisible = false;
        SteerConfirmHost.Child = null;
        InputBox.Focus();
        UpdateSendState();
    }

    /// <summary>The one action that destroys text, and the only one with no keyboard path: a key that deletes
    /// a sentence is a key pressed by accident.</summary>
    private void DiscardSteer()
    {
        _steerDraft = null;
        SteerConfirmHost.IsVisible = false;
        SteerConfirmHost.Child = null;
        UpdateSendState();
    }

    // ── the strip, as a check reads it ──

    internal bool SteerConfirmVisibleForCheck => SteerConfirmHost.IsVisible;

    /// <summary>The draft the strip is holding up for its second tap. Read off the strip rather than the box:
    /// showing the draft is the whole point of the confirmation, and the box has already been cleared.</summary>
    internal string SteerConfirmTextForCheck
        => SteerConfirmHost.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("steer-confirm-text"))?.Text ?? "";

    /// <summary>The commit button's own label: 「引导回复」 while a reply is running, 「发送」 once the reply has
    /// finished and the same tap is an ordinary send.</summary>
    internal string SteerConfirmCommitLabelForCheck
        => SteerConfirmHost.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "SteerCommitButton")
            ?.GetLogicalDescendants().OfType<TextBlock>()
            .LastOrDefault()?.Text ?? "";

    internal void ClickSteerCommitForCheck() => ClickSteerButtonForCheck("SteerCommitButton", null);
    internal void ClickSteerEditForCheck() => ClickSteerButtonForCheck(null, "ChatSteerEdit");
    internal void ClickSteerDiscardForCheck() => ClickSteerButtonForCheck(null, "ChatSteerDiscard");

    private void ClickSteerButtonForCheck(string? name, string? tag)
    {
        var button = SteerConfirmHost.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => (name is not null && candidate.Name == name)
                                       || (tag is not null && candidate.Tag as string == tag));
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Option tags for the plan review, in the order the rows read. Kept as constants so the
    /// transcript's decision strings and the composer's row tags cannot drift apart.</summary>
    private static class ChatPlanStates
    {
        public const string Approve = "approve";
        public const string Revise = "revise";
    }
}
