using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using AvaloniaEdit;
using Markdown.Avalonia;
using ColorTextBlock.Avalonia;

namespace AxmolHub;

public partial class ChatPanel : UserControl
{
    /// <summary>How many of the newest assistant turns are rendered as full Markdown when a transcript is laid
    /// down. Older turns fall back to plain selectable text and are promoted the moment a reader scrolls one
    /// into reach (<see cref="PromoteMarkdownInView"/>). Every Markdown turn carries a MarkdownScrollViewer, a
    /// Markdig pass and an AvaloniaEdit highlighter, so the count that matters is the one on screen rather than
    /// the one in the session; bounding it keeps the heavy controls proportional to what a person actually reads
    /// rather than to the session's total length. The limit is high enough that short sessions (all of the
    /// self-check fixtures) render everything without a promotion pass.</summary>
    private const int EagerMarkdownLimit = 12;

    /// <summary>How many plain rows one promotion pass may upgrade. A scroll arrives as a burst and a pass that
    /// swallowed the whole burst would stall the very frame it exists to keep smooth, so the rest wait for the
    /// next pass — which is queued from the one that filled its quota.</summary>
    private const int MarkdownPromotionsPerPass = 3;

    /// <summary>How many transcripts that were left behind keep their rows. Two, because coming back is a single
    /// step either way (the session just left, and the one before it) — a longer chain would keep Markdown
    /// viewers and their editors alive for sessions nobody is going back to.</summary>
    private const int ParkedTranscriptDepth = 2;

    /// <summary>The class a row wears while its Markdown is still held back. It is also what makes such a row
    /// findable without a side table, so a check can count what a session opened with from outside.</summary>
    private const string PlainTurnClass = "plain-turn";

    /// <summary>
    /// How many of the newest turns a transcript paints when it is laid down.
    ///
    /// <para>The rest of a long session is reachable through the line at the top of the flow instead of being
    /// paid for up front: building and measuring a row is nearly all of what an open costs, and a session of four
    /// hundred turns painted in full holds the window for about three quarters of a second before the first thing
    /// can be read. The number is above every self-check fixture — the longest is 18 turns — so the suite keeps
    /// exercising the ordinary full paint, and only <c>CheckTranscriptCost</c> meets the window.</para>
    /// </summary>
    private const int FirstPaintTurns = 30;

    /// <summary>How many rows a transcript may park when it is left. A row holds its whole Markdown document, its
    /// editors and its decoded pictures, so a session somebody expanded all the way to the top is a session whose
    /// rows are all there — past this, drawing them again is cheaper than keeping them.</summary>
    private const int ParkedRowLimit = 200;

    /// <summary>How close to the bottom counts as "the user is at the bottom". A few pixels of slack stop
    /// sub-pixel scroll rounding from flipping the state on every scroll event.</summary>
    private const double StickEpsilon = 6;
    private readonly ChatWorkspace _chat;
    private readonly List<ContextAttachment> _contextAttachments = [];

    /// <summary>
    /// The pictures this composer is holding, in the order they were added, still in memory. A draft that is
    /// abandoned leaves nothing on disk, and the conversation the pictures would be stored into may not exist
    /// yet — Hub creates it when the message is sent, which is also when these bytes get a file name.
    /// </summary>
    private readonly List<PendingPicture> _pendingPictures = [];

    /// <summary>The shell's window-level picture viewer, handed over as a delegate the same way the inspector
    /// is: the viewer covers the whole window, so it cannot live in this page, but the sets it pages through
    /// are this page's business (the transcript's pictures, or this composer's draft).</summary>
    internal Action<IReadOnlyList<PictureRef>, int>? ShowPictureViewer { get; set; }

    /// <summary>
    /// What this composer has already sent, oldest first, for the Up arrow to walk back through — along with the
    /// steer that a running reply swallowed. Panel-wide rather than per conversation on purpose: Hub creates the
    /// conversation at the moment the first message is sent, so a per-conversation history would lose exactly the
    /// draft a person reaches Up for.
    /// </summary>
    private readonly List<string> _sentTexts = [];

    private int _recallIndex;
    private bool _recalling;
    private const int MaxRecalledTexts = 20;

    private bool _stickToBottom = true;
    // True while a hand holds the scrollbar's thumb: the window in which automatic scrolling and streamed
    // re-measuring both stand down. Wired in the constructor off Thumb.DragStarted/DragCompleted; see ScrollToEnd
    // for what it guards and RefreshLive for what it holds back.
    private bool _thumbDragging;
    private Task? _contextCompressionTask;

    /// <summary>
    /// The bubble showing the reply arriving in the session on screen, or null while none is. Everything it
    /// displays is read off the run, so leaving and coming back rebuilds it from what actually arrived — the text,
    /// the tools it has been through, and the time it has been taking — instead of from a copy this view kept,
    /// which is also what lets a session keep streaming while it is hidden.
    /// </summary>
    private LiveBubble? _live;

    /// <summary>The build count of the bubble that was last on screen, kept because the bubble itself is
    /// discarded the moment the answer lands — and the number is only interesting once the stream is over.</summary>
    private int _lastLiveBuilds;

    /// <summary>Set when a live repaint was skipped because the composer was being typed into on Linux; the next
    /// repaint or focus loss clears it. Keeps the message column from re-arranging — and the in-progress ibus
    /// composition from jumping — while the user is composing a sentence during a streamed reply.</summary>
    private bool _liveRepaintPending;

    /// <summary>Where an open IME composition cannot survive a sibling re-arrange (Avalonia's X11 backend owns the
    /// preedit in the control's own text presenter), so the streamed reply must not repaint the message column
    /// while a draft is being composed. Gated on non-empty text, not focus alone: the composer keeps focus after
    /// a send, and an empty box means no composition is open — so a just-sent reply must still stream normally.
    /// Windows/macOS hand composition to the OS IME and need no such deferral.</summary>
    private bool DeferringLiveRepaint =>
        OperatingSystem.IsLinux() && InputBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(InputBox.Text);

    /// <summary>One attached run's worth of chrome: the row, the pieces of it that change while text arrives, and
    /// the timer that animates them. Created on attach, thrown away on detach; the run outlives both and carries
    /// every fact the chrome displays, so nothing here survives a detach and nothing here needs to.</summary>
    private sealed class LiveBubble
    {
        public required ConversationRun Run { get; init; }
        public required Control Row { get; init; }
        /// <summary>The arriving answer, laid down as Markdown the whole way: finished blocks stacked above the
        /// one still being typed. There is no plain-text copy of it any more — showing the source while the model
        /// spoke was the thing this replaced.</summary>
        public required StackPanel Prose { get; init; }

        /// <summary>How much of <see cref="ConversationRun.LiveText"/> is already a finished block, the source
        /// the open block was last rendered from, and when that happened. Together they let a frame that changed
        /// nothing inside the current block do no building at all.</summary>
        public int Settled { get; set; }
        public string SettledSource { get; set; } = "";
        public string OpenBlockSource { get; set; } = "";

        /// <summary>How many documents this bubble has built. Not a diagnostic curiosity: rebuilding the whole
        /// reply on every arriving chunk is the one way this path could be silently as expensive as the plain
        /// text block it replaced, and a count is the only part of it a check can assert on without a stopwatch.</summary>
        public int Builds { get; set; }
        public long OpenBlockPaintedAt { get; set; }

        /// <summary>The text the bubble last drew, kept only so a deferred frame can tell whether anything
        /// arrived while the repaint was held back.</summary>
        public string PaintedText { get; set; } = "";

        /// <summary>The thinking, as the very same row the stored turn will use. Drawn while reasoning is the only
        /// thing arriving and folded when the answer starts — never hidden, because a reader who watched forty
        /// seconds of it did not ask for it to vanish the moment the result turned up.</summary>
        public required ThoughtRowView Thought { get; init; }

        /// <summary>Whether the thinking body is following the newest line. Its own copy of the transcript's
        /// stick-to-bottom rule, because a box that always follows is a box nobody can read the top of.</summary>
        public bool ThoughtStick { get; set; } = true;

        public required TextBlock Status { get; init; }
        public required TextBlock Elapsed { get; init; }
        public required ActivityGlyph Glyph { get; init; }
        public DispatcherTimer? Timer { get; set; }
        public int Frame { get; set; }

        /// <summary>Whether a tool owns the run right now. Seeded off the run when a switch rebuilds the bubble
        /// and moved by <see cref="ChatWorkspace.ToolActivityChanged"/>, the only place the view learns that a
        /// call started or came back; it is what tells the two waiting shapes apart.</summary>
        public bool ToolRunning { get; set; }

        /// <summary>Whether this segment has produced any text yet — the status line says "preparing" until it
        /// has, and a steer starts a new segment, so the flag has to be able to go back.</summary>
        public bool ShowedText { get; set; }
    }

    /// <summary>The conversation whose turns <see cref="MessageFlow"/> currently shows, and how many of its
    /// visible turns have already been laid down. Together they let <see cref="RenderMessages"/> append only what
    /// is new instead of tearing down and rebuilding the whole flow on every change. The prefix is counted in
    /// <i>turns</i>, not in rows: a group of machine turns paints fewer rows than it consumes — a plain call
    /// draws none, its result draws one between them — and a row count cannot point back into the transcript.</summary>
    private string? _renderedConversationId;
    private int _renderedCount;

    /// <summary>The per-turn states as they were when the flow was laid down. An approval decision — and a spent
    /// undo copy — rewrite what a turn that is already on screen looks like without changing how many turns there
    /// are, which is precisely what counting cannot see.</summary>
    private string _renderedApprovalStamp = "";

    /// <summary>The last answer to "is the thinking drawn", held beside the stamp above because it invalidates the
    /// flow for the same reason. Seeded to the shipped default so opening a session never tears down a transcript
    /// that has not been painted yet.</summary>
    private bool _renderedShowThinking = true;

    /// <summary>Which turn was sitting in the last painted slot. Counting can see a tail append and can see the
    /// history shrink, but it cannot see a turn <i>inserted</i> before the end — and a tool result is filed beside
    /// the call it answers, which is an insert whenever other calls from the same response ran first. That shift
    /// used to paint the shifted tail twice and skip the inserted row entirely. The slot's own identity is enough:
    /// an insert anywhere before it moves a different turn into it.</summary>
    private long _renderedTailKey;
    private List<long> _renderedKeys = [];

    /// <summary>The activity group whose run is still going, when one is: the machine turns that arrive next
    /// belong to it, so it grows where it stands instead of the flow being rebuilt around it. See
    /// <see cref="DeriveActivityGroup"/>.</summary>
    private ActivityGroupView? _openGroup;

    /// <summary>The visible turns the flow was laid down from. Held so that leaving a session keeps a way to ask
    /// whether its rows still say what was drawn (see <see cref="RenderedTranscript"/>); the comparison itself
    /// lives in <see cref="TakeParked"/>, which accepts a turn that came back as an equal object — stamping
    /// <c>ApprovalSeen</c> at open does exactly that.</summary>
    private List<ChatTurn> _renderedTurns = [];

    /// <summary>The rows that were drawn as plain text although their turn holds Markdown, waiting for a reader
    /// to scroll them into reach. Panel-wide rather than per control for the same reason the fold memory is: a
    /// rebuild can put a row somewhere else, and the record has to survive that or follow it.</summary>
    private List<PendingMarkdownRow> _pendingMarkdownRows = [];

    /// <summary>How many turns a full paint may lay down, newest first. Doubles when a person asks for the ones
    /// above, so each click's pause has a bound; panel-wide rather than per conversation, because somebody who
    /// once wanted the earlier messages does not want to be asked again in every session they open.</summary>
    private int _paintWindow = FirstPaintTurns;

    /// <summary>The first visible turn the flow holds, or 0 when it holds the whole transcript. Rows exist only
    /// for turns from here to the end, which makes this the one number that says where the window starts.</summary>
    private int _paintedFrom;

    // Weak ownership follows the registry's live Conversation objects. Switching away does not reset identities;
    // deleting or evicting a conversation does not leave a second strong copy of its history in this panel.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Conversation, TranscriptProjectionState>
        _projectionStates = new();
    private TranscriptProjectionState? _projection;

    private string RowKey(int sourceIndex, string facet)
        => facet + ":" + _projection!.KeyAt(sourceIndex).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Set by <see cref="ExpandEarlierTurns"/> to ask for the next paint to lay the flow down again from
    /// a wider window. A flag rather than a sentinel in the fields above, because the teardown it requests must
    /// <b>not</b> be read as a switch of session: nothing is parked, and the reading position does not move.</summary>
    private bool _paintFromScratch;

    /// <summary>Transcripts that were on screen before and whose rows are still worth keeping — the answer to
    /// "the person is going back and forth between two sessions". Reusing the rows is what makes the second
    /// visit cheap: the Markdig pass, the AvaloniaEdit editors and the syntax highlighting behind them are
    /// exactly the work a rebuild would repeat, and a control that was only detached does not need it again.
    /// Ordered most recently left first, bounded by <see cref="ParkedTranscriptDepth"/>.</summary>
    private readonly List<RenderedTranscript> _parkedTranscripts = [];

    /// <summary>The current operation stays alive until its own Loaded observation, even if another session
    /// replaces it in this panel. Callback closures own their measurements, never the next session's fields.</summary>
    private SessionOpenMeasurement? _panelOpen;
    private bool _openReusedRows;

    /// <summary>One transcript's rows, detached but alive, with everything needed to tell whether they still
    /// describe the conversation they were drawn from.</summary>
    private sealed class RenderedTranscript
    {
        public required string ConversationId { get; init; }
        public List<Control> Rows { get; } = [];
        public List<ChatTurn> Turns { get; } = [];
        public List<long> Keys { get; } = [];
        public List<PendingMarkdownRow> PendingMarkdown { get; } = [];
        public ActivityGroupView? OpenGroup { get; set; }
        public string ApprovalStamp { get; set; } = "";
        public bool ShowThinking { get; set; }

        /// <summary>Where this transcript's window started, so that coming back to it restores the same edge
        /// rather than displaying rows as if the whole history were in the flow.</summary>
        public int PaintedFrom { get; set; }
    }

    /// <summary>A row drawn as plain text although its turn holds Markdown, with everything needed to draw it
    /// properly in place: the panel it sits in and the control itself, because a transcript that grew a group
    /// since it was painted did not move this row's parent even though its position in the flow changed.</summary>
    private sealed class PendingMarkdownRow
    {
        public required Panel Parent { get; init; }
        public required Control Row { get; init; }
        public required string ConversationId { get; init; }
        public required long TurnKey { get; init; }
        public string? ToolName { get; init; }
        public bool IsLast { get; init; }
        public bool InGroup { get; init; }
    }

    /// <summary>Which folds the person opened, held as data rather than as a control's state. Extending a group
    /// in place keeps its head alive, but not every rebuild can be avoided: a decision rewrites what an
    /// already-painted turn looks like, and a result approved late is filed beside its call rather than at the
    /// end. A group is keyed by the turn it starts on and a row by the call it reports — both survive a teardown,
    /// so the fold comes back where the person left it. Nothing is in these sets on its own: a fold nobody opened
    /// stays collapsed, which is what the charter asks of secondary detail.</summary>
    private readonly Dictionary<string, HashSet<string>> _expandedGroups = new();
    private readonly Dictionary<string, HashSet<string>> _expandedRows = new();

    private static bool IsFoldOpen(Dictionary<string, HashSet<string>> memory, string conversationId, string key)
        => memory.TryGetValue(conversationId, out var open) && open.Contains(key);

    private static void RememberFold(Dictionary<string, HashSet<string>> memory, string conversationId,
        string key, bool opened)
    {
        if (!memory.TryGetValue(conversationId, out var set))
        {
            if (!opened) return;
            memory[conversationId] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        if (opened) set.Add(key);
        else set.Remove(key);
    }

    /// <summary>Drops the fold choices — and the parked rows — of sessions that no longer exist. Deleting a
    /// conversation raises <see cref="ChatWorkspace.Changed"/>, so this runs on the very repaint that follows it
    /// and none of them outlives the conversations they name.</summary>
    private void PruneFoldMemory()
    {
        if (_expandedGroups.Count == 0 && _expandedRows.Count == 0 && _parkedTranscripts.Count == 0) return;
        var alive = _chat!.Conversations.Select(summary => summary.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _expandedGroups.Keys.Where(id => !alive.Contains(id)).ToArray()) _expandedGroups.Remove(gone);
        foreach (var gone in _expandedRows.Keys.Where(id => !alive.Contains(id)).ToArray()) _expandedRows.Remove(gone);
        // Parked rows are the same kind of debris and a heavier one: a transcript of a deleted session keeps its
        // Markdown viewers, their editors and their decoded pictures alive for a session nobody can open again.
        _parkedTranscripts.RemoveAll(parked => !alive.Contains(parked.ConversationId));
    }

    private bool RenderedPrefixMatches(List<(int Index, ChatTurn Turn, string? ToolName)> visible)
    {
        if (_renderedKeys.Count != _renderedCount) return false;
        for (var i = 0; i < _renderedCount; i++)
            if (_renderedKeys[i] != _projection!.KeyAt(visible[i].Index)
                || !_renderedTurns[i].Equals(visible[i].Turn)) return false;
        return true;
    }

    private static string ApprovalStamp(Conversation? conversation)
    {
        if (conversation is null) return "";
        var stamp = new StringBuilder();
        foreach (var turn in conversation.Messages)
        {
            if (turn.ApprovalState is null && turn.UndoName is null && turn.PlanApprovalState is null) continue;
            stamp.Append(turn.ToolCallId).Append(':').Append(turn.ApprovalState ?? "")
                .Append('/').Append(turn.UndoName ?? "")
                .Append('/').Append(turn.PlanApprovalState ?? "").Append(';');
        }

        return stamp.ToString();
    }

    /// <summary>Raised when the active conversation or its title may have changed, so the shell can update
    /// its top-bar title (the panel no longer owns a title of its own).</summary>
    internal event Action? ConversationStateChanged;

    public ChatPanel()
    {
        _chat = null!;
        InitializeComponent();
    }

    public ChatPanel(ChatWorkspace chat)
    {
        _chat = chat;
        InitializeComponent();

        _chat.Changed += Reload;
        // A command that went quiet raises its own question, and it is raised from inside the tool body — nowhere on
        // the token stream, so nothing else would ever tell this panel the card exists. The conversation id rides
        // along: a question in a session that is not on screen must not repaint the one that is.
        _chat.StallChanged += conversationId =>
        {
            if (conversationId != _chat?.ActiveConversation?.Id) return;
            RefreshDecisionHost();
        };
        // A repository read lands whenever git finishes, which is nowhere on the token stream. The pane decides
        // whether it still wants the answer; nothing here polls, and nothing here runs a git.
        _chat.RepositoryChanged += OnRepositoryChanged;
        // Tool activity already arrives on the UI thread, and it carries the session it happened in: a reply
        // running out of sight must not rewrite the status line of the one on screen.
        _chat.ToolActivityChanged += (conversationId, name, completed) =>
        {
            if (_live is not { } live || live.Run.ConversationId != conversationId) return;
            live.Status.Text = ToolActivityText(name, completed);
            // Which of the two waiting shapes to wear is the whole thing this event decides; how many calls have
            // come back is counted in the group row the transcript already draws, not on the glyph.
            live.ToolRunning = !completed;
            RefreshGlyphPhase(live);
        };
        _chat.RunTextChanged += conversationId =>
        {
            if (_live is { } live && live.Run.ConversationId == conversationId) RefreshLive(live);
        };
        // A command that printed another line. Its card is not a turn and not a token: the transcript grows nothing
        // until the command ends, so what changes is the text inside one row that is already on screen. Refreshed
        // from the run rather than rebuilt, and guarded by the session for the reason the two events above are — a
        // build running in a window nobody is looking at must not paint into this one.
        _chat.ToolOutputChanged += conversationId =>
        {
            if (_runningCommand is not { } card || card.ConversationId != conversationId) return;
            RefreshRunningCommand(card, _chat.RunFor(conversationId));
        };
        // A search the endpoint ran for itself: no card, no call row, nothing the person can act on — so the one
        // status line the bubble already has is where the fact belongs. Same session guard as the tool row above,
        // for the same reason: a reply running out of sight must not rewrite the line of the one on screen.
        _chat.ServerSearchChanged += (conversationId, notice) =>
        {
            if (_live is not { } live || live.Run.ConversationId != conversationId) return;
            live.Status.Text = SearchActivityText(notice);
        };
        // A retry wait is the provider saying "not yet" and the pipeline obeying, which is work rather than a
        // stall — so it takes the same one line the tool and the search use, and gives it back when the attempt
        // goes out. Out of sight it is still the run that holds the fact, for the bubble rebuilt later.
        _chat.RetryWaitChanged += (conversationId, waiting) =>
        {
            if (_live is not { } live || live.Run.ConversationId != conversationId) return;
            live.Status.Text = waiting ? RetryActivityText(live.Run.RetryWaitNotice())
                : live.ShowedText ? HubStrings.Get("ChatGenerating") : HubStrings.Get("ChatPreparing");
        };
        // A compaction is the one stretch of a run where no token arrives and no tool runs, so the line that exists
        // to say the turn is alive would go on claiming tokens are coming. Both brackets write the same line the
        // retry wait uses, and give it back the same way. The strip below the flow is repainted as well, because
        // that is the surface the button path has: compressing a session nobody has sent to leaves no bubble to
        // correct, and RenderNotice is where the two surfaces take turns.
        _chat.CompactionChanged += (conversationId, compacting) =>
        {
            if (_live is { } live && live.Run.ConversationId == conversationId)
            {
                live.Status.Text = compacting ? HubStrings.Get("ChatCompactingContext")
                    : live.ShowedText ? HubStrings.Get("ChatGenerating") : HubStrings.Get("ChatPreparing");
            }
            RenderNotice();
        };
        _chat.RunsChanged += conversationId =>
        {
            if (conversationId == _chat.ViewedConversationId) RenderMessages();
        };
        _chat.RunCompleted += (conversationId, outcome) =>
        {
            var viewed = conversationId == _chat.ViewedConversationId;
            if (viewed) RenderMessages();
            // Filed for whichever session ended, ahead of the guard below: a run that finished while the person
            // was reading another conversation still owes them the reason when they come back.
            AppendRunNotice(conversationId, outcome);
            if (!viewed) return;
            UpdateSendState();
            ConversationStateChanged?.Invoke();
            // A run that just finished can have moved the repository without Hub's file tool ever being part of
            // it — a build that regenerated sources, a code format, a checkout the assistant asked to approve.
            // So the repository tab asks for a fresh read here if it is the one being looked at. Nothing else is
            // asked to read: the other two tabs are the transcript, which this event already repaints above.
            if (_chat.ActiveConversation?.Id == conversationId
                && _inspector is { } pane && pane.RepoTabVisibleForCheck) RequestRepositoryRead();
        };
        ModelPicker.Click += (_, _) => ShowModelMenu();
        ForkNotice.Click += (_, _) =>
        {
            if (ForkNotice.Tag is string sourceId) _chat.OpenConversation(sourceId);
        };
        ModeIndicatorButton.Click += (_, _) => _chat.SelectMode(ChatModes.Agent);
        ContextButton.Click += (_, _) => ShowContextMenu();
        ContextPopup.PlacementTarget = ContextButton;
        ContextPopup.Placement = PlacementMode.TopEdgeAlignedRight;
        ContextProgressTrack.SizeChanged += (_, _) => UpdateContextProgressFill();
        CompressContextButton.Click += (_, _) => _contextCompressionTask = CompressContextAsync();
        ToolTip.SetTip(AddContextButton, HubStrings.Get("ChatAddContext"));
        AddContextButton.Click += (_, _) => ShowAddContextMenu();
        PermissionChip.Click += (_, _) => ShowPermissionMenu();
        WorkspaceChip.Click += (_, _) => ShowWorkspaceMenu();
        // The round button is the thing being pressed, and a press moves the keyboard to it. The reply is what
        // the person is waiting on, and the composer is where the next sentence goes, so focus comes back here.
        SendButton.Click += (_, _) =>
        {
            _ = SendAsync();
            InputBox.Focus();
        };
        ScrollToBottomButton.Click += (_, _) => ScrollToEnd();
        MessageScroller.SizeChanged += (_, _) => UpdateMessageColumnWidth();
        MessageScroller.ScrollChanged += (_, _) =>
        {
            UpdateMessageColumnWidth();
            UpdateScrollAffordance();
            // The transcript of a long session is largely plain until this runs, and a scroll is exactly the
            // news that some of it is now in front of someone.
            ScheduleMarkdownPromotion();
        };
        // A hand on the thumb owns the reading position. While it is held, no automatic scroll may move what it
        // holds and the streamed reply must not re-measure the column under it; the moment it lets go, the
        // held-back paint flushes. Both drag events bubble out of the ScrollBar's template (Thumb's drag events
        // are Bubble routed events), so the wiring survives a template re-apply and stays out of the template.
        MessageScroller.AddHandler(Thumb.DragStartedEvent,
            (_, _) => { _thumbDragging = true; _stickToBottom = false; });
        MessageScroller.AddHandler(Thumb.DragCompletedEvent,
            (_, _) => { _thumbDragging = false; FlushLiveRepaintIfPending(); });
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                // Typing ends the walk: the next Up starts again from the newest thing sent, rather than
                // continuing to page through history under a sentence the person has begun writing themselves.
                if (!_recalling) _recallIndex = 0;
                UpdateSendState();
                UpdateContextRing();
            }
        };
        InputBox.GotFocus += (_, _) => SetComposerFocus(true);
        InputBox.LostFocus += (_, _) => SetComposerFocus(false);
        // The clipboard verbs a text box is expected to have on right-click. Avalonia's TextBox has none of its
        // own, so without this the box a person writes in has no right-click at all — while the transcript beside
        // it does.
        SelectionMenu.AttachTextBox(InputBox);

        // KeyDown is registered for both tunnel and bubble. Subscribe on the tunnel: it runs before the
        // TextBox's own Enter handling, which (with AcceptsReturn=true) inserts a newline and marks the
        // bubble handled, silently swallowing a normal KeyDown subscriber.
        InputBox.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            // While an IME composition is open, Enter arrives as Key.ImeProcessed (confirm candidate), so it
            // falls through; only a bare Enter sends. Shift+Enter is left alone so it still inserts a newline.
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            // A steer waiting for its second tap owns Enter: the box is empty by then (the draft moved to the
            // strip), so without this the key would read as an ordinary send of nothing.
            if (SteerConfirmHost.IsVisible) CommitSteer();
            else _ = SendAsync();
        }, RoutingStrategies.Tunnel);

        // Ctrl+V is claimed here rather than left to the box, and the text paste is then asked for by name: a
        // clipboard holding a screenshot has to become a chip, and a clipboard holding text has to keep pasting
        // text. Reading the clipboard is async, so the alternative — peek and decide — would either drop the
        // picture or paste the file path a copied image also carries.
        InputBox.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.V) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            e.Handled = true;
            if (!await TryPastePictureAsync()) InputBox.Paste();
        }, RoutingStrategies.Tunnel);

        // Escape stops the reply this composer is watching, and Up walks back through what has already been sent.
        // Both are claimed only while the text box holds the keyboard: the in-place editor on a bubble keeps its
        // own Escape, and a caret in a multi-line draft keeps its own Up.
        InputBox.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape when ViewedRun is { IsStreaming: true } streaming:
                    e.Handled = true;
                    _chat.RequestStop(streaming.ConversationId);
                    break;
                case Key.Up when e.KeyModifiers == KeyModifiers.None && CanRecall():
                    e.Handled = true;
                    RecallText(backwards: true);
                    break;
                case Key.Down when _recallIndex > 0:
                    e.Handled = true;
                    RecallText(backwards: false);
                    break;
            }
        }, RoutingStrategies.Tunnel);

        // Dropping a capture onto the composer is how a picture arrives while the answer is still being written.
        // The frame is the target rather than the whole page, so a drop beside it does nothing at all.
        DragDrop.SetAllowDrop(ComposerFrame, true);
        DragDrop.AddDragOverHandler(ComposerFrame, (_, e) =>
        {
            // Both halves matter. `None` is what stops the shell from offering a drop this page cannot use, and
            // the ring is what tells the person *here* before they let go — a target that looks identical to the
            // rest of the window is a target nobody finds.
            var carries = DragCarriesFiles(e);
            e.DragEffects = carries ? DragDropEffects.Copy : DragDropEffects.None;
            ComposerFrame.Classes.Set("drag-over", carries);
        });
        DragDrop.AddDragLeaveHandler(ComposerFrame, (_, _) => ComposerFrame.Classes.Remove("drag-over"));
        DragDrop.AddDropHandler(ComposerFrame, OnComposerDrop);

        // Escape answers the thing on top, and the order is written in exactly one place per layer: the window
        // closes its picture viewer first, then puts an expanded inspector back in its column (both of those
        // handlers live in MainWindow, above this one), then a decision on the card above the composer is answered,
        // then a steer waiting for its second tap goes back to being a draft, and only then does Escape mean "stop
        // the reply". The plan card's letter keys ride the same handler for the same reason. Tunnel, so this runs
        // before the composer's own key handling gets a turn.
        AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (TryChoosePlanOptionByKey(e)) return;
            if (TryChooseQuestionOptionByKey(e)) return;
            if (e.Key != Key.Escape) return;
            if (DecisionHost.IsVisible)
            {
                if (e.Source is TextBox box)
                {
                    // A caret in a text box outranks the card. The card sits above the draft rather than in front
                    // of it precisely so a decision cannot interrupt what is being written, and a key that answers
                    // the question while the person is mid-sentence is that interruption again. Only the card's own
                    // revision line turns Escape into "leave this line"; the composer's box falls through to the
                    // layers under this one, where Escape still stops the reply.
                    if (box.Name is "PlanFeedbackBox" or "QuestionAnswerBox")
                    {
                        DecisionHost.Focus();
                        e.Handled = true;
                    }
                    return;
                }
                DismissDecisionByEscape();
                e.Handled = true;
                return;
            }
            if (SteerConfirmHost.IsVisible)
            {
                // Escape on a draft is the way back to editing it, never the way to destroy it.
                EditSteerDraft();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        Reload();
    }

    public void Reload()
    {
        if (_chat is null) return;
        var viewed = _chat.ViewedConversationId;
        var requested = _chat.CurrentSessionOpen;
        if (requested is { Completed: false } && requested.ConversationId == viewed)
            _panelOpen = requested;
        else if (viewed is not null && viewed != _renderedConversationId)
            _panelOpen = new SessionOpenMeasurement(viewed, "panel-switch");
        var measurement = _panelOpen is { Completed: false } pending && pending.ConversationId == viewed
            ? pending : null;
        if (measurement is not null) measurement.Reloads++;
        var reloadStarted = measurement?.ElapsedMs ?? 0;
        _openReusedRows = false;

        InputBox.PlaceholderText = HubStrings.Get("InputPlaceholder");
        GreetingLabel.Text = HubStrings.Get("AssistantGreeting");
        GreetingSubtitle.Text = HubStrings.Get("AssistantGreetingSubtitle");
        RenderSuggestions();
        UpdateSendState();

        RefreshModelPicker();
        RefreshComposerChoices();
        UpdateForkNotice();
        var renderStarted = measurement?.ElapsedMs ?? 0;
        var previousMeasurement = SessionOpenMeasurement.Rendering;
        SessionOpenMeasurement.Rendering = measurement;
        try { RenderMessages(); }
        finally { SessionOpenMeasurement.Rendering = previousMeasurement; }
        if (measurement is not null)
        {
            measurement.RenderMs += measurement.ElapsedMs - renderStarted;
            measurement.Reused = _openReusedRows;
        }
        UpdateContextRing();
        // The two composer-anchored surfaces read the transcript, not the run registry, so a repaint is the one
        // place both can be brought in step with whatever just changed.
        RefreshDecisionHost();
        RefreshSteerConfirm();
        // And the band above them, which outlives the round that filled it: a tally somebody can click has to keep
        // telling the truth after the run ends and after a restart, so it reads the transcript here too rather than
        // being driven from a run event that stops firing.
        RefreshChangesPill();
        // The right-hand column is the third surface that reads the transcript rather than the run registry. It
        // repaints only when the transcript grew, so a reply streaming text costs no pane rebuild.
        RefreshInspectorIfTranscriptMoved();
        if (measurement is not null)
        {
            measurement.ReloadMs += measurement.ElapsedMs - reloadStarted;
            NoteSessionOpen(measurement);
        }
        // Rows that came up already in front of the reader are promoted without waiting for a scroll: a session
        // short enough to fit a screen is a session whose whole transcript can be upgraded at once.
        ScheduleMarkdownPromotion();
    }

    /// <summary>Observes dispatcher latency, not presentation. Snapshot counts synchronously so a rapid switch
    /// cannot attribute the next conversation's controls to this operation. Subsequent reloads update the same
    /// operation until this callback completes it; superseded opens are labeled, not mistaken for visible frames.</summary>
    private void NoteSessionOpen(SessionOpenMeasurement measurement)
    {
        measurement.TotalTurns = _chat.ActiveConversation?.Messages.Count ?? 0;
        measurement.PaintedTurns = _renderedCount - _paintedFrom;
        measurement.Rows = MessageRows.Count();
        measurement.ActivityRows = MessageFlow.GetLogicalDescendants().OfType<Control>()
            .Count(control => control.Classes.Contains("activity-row"));
        measurement.MarkdownDocuments = MarkdownDocumentCountForCheck;
        measurement.Editors = MessageFlow.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Count();
        measurement.FlowChildren = MessageFlow.Children.Count;
        if (measurement.ObservationQueued) return;
        measurement.ObservationQueued = true;
        var chat = _chat;
        Dispatcher.UIThread.Post(() =>
        {
            var line = measurement.Complete(chat.ViewedConversationId == measurement.ConversationId);
            chat.LogProvider?.Invoke()?.Write(line);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The four starting points the empty state offers, in the order they read best.</summary>
    private static readonly string[] SuggestionKeys =
    [
        "AssistantSuggestReadCodebase",
        "AssistantSuggestFixBug",
        "AssistantSuggestWriteScript",
        "AssistantSuggestRefactor",
    ];

    /// <summary>
    /// Rebuilds the suggestion chips from the language table. Rebuilt rather than declared in the markup because
    /// a language switch must not leave English prompts under a Chinese greeting, and because a chip whose key
    /// vanished from the table disappears with it — the alternative is a pill reading "AssistantSuggestFixBug".
    /// </summary>
    private void RenderSuggestions()
    {
        SuggestionPanel.Children.Clear();
        foreach (var key in SuggestionKeys)
        {
            var prompt = HubStrings.Get(key);
            if (string.IsNullOrWhiteSpace(prompt) || string.Equals(prompt, key, StringComparison.Ordinal)) continue;

            var chip = new Button { Classes = { "suggestion-chip" }, Content = prompt, Tag = key };
            // Fill, do not send. A chip is an offer the person may want to narrow ("…this function"), and sending
            // it would start a run that spends their quota on a tap that looked like focusing a text box.
            chip.Click += (_, _) =>
            {
                InputBox.Text = prompt;
                InputBox.CaretIndex = prompt.Length;
                InputBox.Focus();
                UpdateSendState();
            };
            SuggestionPanel.Children.Add(chip);
        }
        SuggestionPanel.IsVisible = SuggestionPanel.Children.Count > 0;
    }

    /// <summary>
    /// Names the session the current one was forked from, above the transcript. Hidden for a normal session
    /// and for a fork whose source has since been deleted — a link to nothing would be worse than no link.
    /// </summary>
    private void UpdateForkNotice()
    {
        var source = _chat.ActiveConversation?.BranchSourceId is { Length: > 0 } sourceId
            ? _chat.Conversations.FirstOrDefault(summary => summary.Id == sourceId)
            : null;

        ForkNotice.IsVisible = source is not null;
        if (source is null) return;

        ForkNotice.Tag = source.Id;
        ForkNotice.Content = string.Format(
            System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get("ForkedFrom"), source.Title);
    }

    private void RefreshModelPicker()
    {
        var choices = _chat.AvailableChatModels.ToArray();
        var selected = _chat.SelectedChatModel is { } active
            ? choices.FirstOrDefault(choice =>
                choice.Provider.Id == active.Provider.Id
                && string.Equals(choice.ModelName, active.ModelName, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedModelLabel.Text = selected?.ModelName ?? HubStrings.Get("NoAvailableChatModels");
        var supportsReasoning = selected is not null
            && ModelCatalog.SupportsReasoningEffort(selected.Provider, selected.ModelName);
        SelectedReasoningLabel.IsVisible = supportsReasoning;
        // With routing on there is no single tier to name — Hub picks one per request — so the chip says who is
        // picking, and the tooltip carries the route this session last actually went out on. A setting that
        // spends money has to be readable without opening a log file.
        var routed = supportsReasoning && _chat.ActiveRouting == ChatRouting.Auto;
        SelectedReasoningLabel.Text = supportsReasoning
            ? routed ? HubStrings.Get("ChatRoutingAutoChip") : ReasoningChoiceLabel(_chat.ActiveReasoningEffort)
            : "";
        ToolTip.SetTip(SelectedReasoningLabel, routed && _chat.ActiveConversation is { } session
            && _chat.LastRouteFor(session.Id) is { } route
            ? $"{route.Model} · {route.Effort} — {route.Reason}"
            : null);
        ModelPicker.IsEnabled = choices.Length > 0;
        ToolTip.SetTip(ModelPicker, selected is null
            ? HubStrings.Get("NoAvailableChatModels")
            : selected.Provider.Name + " · " + selected.ModelName);
    }

    private void RefreshComposerChoices()
    {
        RefreshModelPicker();
        // The marker is read off the session on screen, never kept here: a field on this view is what let one
        // session's 计划 paint over the next one's, and the mode it named was then not the mode the message ran
        // under. Its content follows the session's own mode even while hidden, so a chip cannot disagree with the
        // session it belongs to; only 计划 wears one, because an unticked menu pair already reads as the default
        // 目标, and 提问 is a chat with no tools rather than something to advertise.
        var mode = _chat.ActiveMode;
        ModeIndicatorButton.IsVisible = mode == ChatModes.Plan;
        ModeIndicatorIcon.Data = ThemeGeometry(ComposerModeIconKey(mode));
        ModeIndicatorLabel.Text = HubStrings.Get(mode switch
        {
            ChatModes.Ask => "ChatModeAsk",
            ChatModes.Plan => "ChatModePlan",
            _ => "ChatModeGoal",
        });
        ToolTip.SetTip(ModeIndicatorButton, HubStrings.Get("ChatModeResetHint"));
        // The chip names the *effective* mode, not the one someone clicked: a session that merely follows a
        // permissive default is in it, and reading back the override would call that mode safe. With no session
        // on screen yet it names the mode the first one starts under, because before the first message is the
        // moment to decide how much to allow.
        var permission = _chat.ActiveApprovalMode;
        PermissionChipIcon.Data = ThemeGeometry(ToolApprovalIconKey(permission));
        PermissionChipLabel.Text = HubStrings.Get(ToolApprovalModeKey(permission));
        PermissionChip.Classes.Set("danger", permission == ToolApprovalModes.Full);
        ToolTip.SetTip(PermissionChip, HubStrings.Get(ToolApprovalModeHintKey(permission)));
        // The picker reads out the directory itself rather than a label for it: "which folder may this assistant
        // write to" has one answer and the folder name is it. When nothing is bound it says so, because that is
        // the state where every file tool refuses and the reason is still one click away.
        var root = _chat.ActiveWorkspaceRoot;
        WorkspaceChipLabel.Text = root is { Length: > 0 } ? FolderNameOf(root) : HubStrings.Get("ChatWorkspaceNone");
        ToolTip.SetTip(WorkspaceChip, root is { Length: > 0 }
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatWorkspaceChipHintFormat"), root)
            : HubStrings.Get("ChatWorkspaceChipHint"));
        // And it is on screen only while the choice is still open: no session, or one that has never received a
        // message. The directory is what a session is born into, so after the first turn it is history —
        // swapping it mid-conversation would leave every earlier reply about a different tree — and a control
        // that can no longer act is a claim that it still can.
        WorkspaceChip.IsVisible = _chat.ActiveConversation is not { Messages.Count: > 0 };
    }

    private static string FolderNameOf(string path)
        => System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    private void UpdateContextRing()
    {
        if (_chat is null) return;
        var usage = CurrentContextUsage();
        // The arc is a picture and a picture cannot draw past its own circle, so the arc clamps. The reading
        // beside it does not: a conversation that no longer fits is at more than 100%, and reporting that as a
        // full ring turns "this session has to be compressed" into the same look as a normal long one.
        ContextRing.Usage = Math.Clamp(usage.Fill, 0, 1);
        var percent = (int)Math.Round(usage.Fill * 100);
        ContextPopoverPercent.Text = percent.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
        UpdateContextProgressFill(usage);
        RefreshContextCompressionButton();
        RenderCacheHit();
        RenderContextBreakdown(usage);
        RenderContextBurn();
        // Two different claims, so two different sentences. "预计" over a number the model itself reported is a
        // lie, and the measured reading is the one thing on this screen a person can hold the gateway to.
        ToolTip.SetTip(ContextButton, string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(usage.IsMeasured ? "ChatContextReportedFormat" : "ChatContextEstimateFormat"),
            usage.Used.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            usage.Room.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            percent));
    }

    private ContextUsage CurrentContextUsage() => _chat.EstimateContextUsage(InputBox.Text ?? "");

    /// <summary>
    /// The session's input cache hit rate, drawn as the panel's first figure: bolder than every row under it, and
    /// never larger than the title's own percentage, so the card keeps one headline number and this one is a second
    /// reading rather than a second headline.
    ///
    /// <para>A ratio over the session's own bill, not a slice of the window: the rows below add up to the occupancy
    /// reading, and this one divides two numbers an endpoint reported. That is why it is a block of its own above
    /// them rather than one more row — a column that no longer adds up is worse than a figure nobody can find.</para>
    ///
    /// <para>The session total, not the last request's: one request's cache count is a sample, and the question this
    /// figure answers — did this conversation's prefix survive — is about the whole conversation. While a run is
    /// still streaming the session total does not include it yet, so the live reading stands in; it is the number
    /// the reader is watching climb.</para>
    ///
    /// <para>Hidden when nothing reported a cache split. A gateway that never sends one is not an endpoint with a
    /// 0.000% hit rate, and a headline is the last place to blur that.</para>
    /// </summary>
    private void RenderCacheHit()
    {
        if (_chat is null) return;
        var (send, session) = _chat.ActiveConversationBurn();
        var burned = session ?? send;
        if (burned is null || burned.Input <= 0 || burned.CachedInput <= 0)
        {
            // Cleared, not just hidden: a hidden block keeping the last session's reading would be one edit away
            // from being shown again on a gateway that never reported anything.
            CacheHitValue.Text = "";
            CacheHitDetail.Text = "";
            CacheHitBlock.IsVisible = false;
            return;
        }
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        // Same three decimals and same clamp as the burn line below, because the two must never disagree about the
        // same reading — a vendor that reports more cached than input is shown as 100.000%, not as 104%.
        var ratio = Math.Clamp(burned.CachedInput * 100d / burned.Input, 0d, 100d);
        CacheHitValue.Text = ratio.ToString("F3", culture) + "%";
        CacheHitDetail.Text = string.Format(culture, HubStrings.Get("ChatContextCacheHitDetailFormat"),
            burned.Input.ToString("N0", culture), burned.CachedInput.ToString("N0", culture));
        CacheHitBlock.IsVisible = true;
    }

    /// <summary>
    /// The measured spend, in the endpoint's own tokens: what this reply cost, and what the session has cost.
    ///
    /// <para>Its own block rather than more rows in the breakdown above, because those rows are one estimate that
    /// adds up to the occupancy figure while these are readings of money spent — dropping a measurement into the
    /// column would stop the column adding up, and the two answers legitimately differ (this session's rows can
    /// total more than the transcript the model actually read, or less).</para>
    ///
    /// <para>Nothing is drawn when the gateway never reported. A zero would read as "this answer cost nothing",
    /// which is a lie in the direction that makes people keep talking; the estimate-vs-measured distinction the
    /// button's own tooltip already carries is the only place that story needs telling.</para>
    /// </summary>
    private void RenderContextBurn()
    {
        if (_chat is null) return;
        var (send, session) = _chat.ActiveConversationBurn();
        var lines = new List<string>();
        if (send is { } thisSend)
            lines.Add(HubStrings.Get("ChatContextBurnSendLabel") + DescribeBurn(thisSend, countRequests: true));
        // The second line earns its row only by saying something the first did not: a session with one answer in
        // it does not need the same figure twice.
        if (session is { } total && !Equals(total, send))
            lines.Add(HubStrings.Get("ChatContextBurnSessionLabel") + DescribeBurn(total, countRequests: false));
        ContextBurnNote.Text = string.Join('\n', lines);
        ContextBurnNote.IsVisible = lines.Count > 0;
    }

    /// <summary>One line of the spend, in the order the bill arrives: what was read, what was written, of what the
    /// thinking was, how much of the read came from cache, what share of the read that was, and over how many
    /// requests.</summary>
    private static string DescribeBurn(ContextBurn burned, bool countRequests)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var parts = new List<string>
        {
            string.Format(culture, HubStrings.Get("ChatContextBurnInFormat"),
                burned.Input.ToString("N0", culture)),
        };
        if (burned.Output > 0)
        {
            // Reasoning is a clause inside the output, never a term beside it: the wire bills the chain of thought
            // as part of the reply, and two numbers side by side invite a reader to add them.
            parts.Add(string.Format(culture, HubStrings.Get("ChatContextBurnOutFormat"),
                burned.Output.ToString("N0", culture))
                + (burned.Reasoning > 0
                    ? string.Format(culture, HubStrings.Get("ChatContextBurnReasoningFormat"),
                        burned.Reasoning.ToString("N0", culture))
                    : ""));
        }
        // Left out entirely when the endpoint reports no cache split — silence is not a claim that nothing was
        // cached, and a 0 here would say it was.
        if (burned.CachedInput > 0)
            parts.Add(string.Format(culture, HubStrings.Get("ChatContextBurnCachedFormat"),
                burned.CachedInput.ToString("N0", culture)));
        // The share the two numbers above already carry, said out loud: cached over summed input is the figure a
        // reader would otherwise do in their head, and it is the one that says whether the prefix survived. Three
        // decimals because the interesting differences live below one percent. Nothing to divide when either side
        // is silent, and no ratio at all when the endpoint reported no cache split — for the same reason the
        // cached figure itself is left out.
        if (burned.CachedInput > 0 && burned.Input > 0)
            parts.Add(string.Format(culture, HubStrings.Get("ChatContextBurnCacheHitFormat"),
                Math.Clamp(burned.CachedInput * 100d / burned.Input, 0d, 100d).ToString("F3", culture)));
        // The request count is what makes a summed input readable against the window's single-request figure:
        // five requests over the same transcript is five charges, and the line says so.
        if (countRequests && burned.Requests > 0)
            parts.Add(string.Format(culture, HubStrings.Get("ChatContextBurnRequestsFormat"),
                burned.Requests.ToString(culture)));
        return string.Join(" · ", parts);
    }

    private void UpdateContextProgressFill(ContextUsage? usage = null)
    {
        if (ContextProgressTrack is null || ContextProgressFill is null) return;
        var reading = usage ?? CurrentContextUsage();
        // A bar has an end, so it fills up to it; the percentage next to it is allowed to say 118%.
        ContextProgressFill.Width = ContextProgressTrack.Bounds.Width
                                     * Math.Clamp((double)reading.Used / Math.Max(1, reading.Room), 0, 1);
    }

    /// <summary>
    /// The popover's breakdown: every heading that holds something, then the room that is left, then the share of
    /// the window held back for the answer. The rows and the ring's number come out of one reading, so the column
    /// adds up to what the ring claims — the identity that has been missing since the meter existed, and the one
    /// that says a category was left out of the total rather than quietly showing a smaller session.
    /// </summary>
    private void RenderContextBreakdown(ContextUsage usage)
    {
        ContextCategoryList.Children.Clear();
        foreach (var category in usage.Categories)
            AddContextCategoryRow(HubStrings.Get(ContextCategoryKey(category.Kind)), category.Tokens);
        AddContextCategoryRow(HubStrings.Get("ChatContextCatFree"), usage.Free);
        AddContextCategoryRow(HubStrings.Get("ChatContextCatReserve"), usage.OutputReserve);

        ContextPopoverSource.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatContextSourceFormat"),
            HubStrings.Get(ContextBudget.SourceLabelKey(usage.Source)),
            usage.EffectiveWindow.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

        // What the reading cannot say by itself: that its floor came from a response rather than a guess, that
        // turns this session still holds never went out, that the window was corrected down from what the
        // provider published, and that the conversation is past the room it has.
        var notes = new List<string>();
        if (usage.IsMeasured)
            notes.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatContextMeasuredNote"),
                usage.MeasuredInputTokens.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        if (usage.DriftPermille > ContextReport.UncalibratedPermille
            && usage.EffectiveWindow != usage.RawWindow)
            notes.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatContextDriftNote"),
                usage.RawWindow.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                usage.EffectiveWindow.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        if (usage.DroppedTurns > 0)
            notes.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatContextDroppedFormat"), usage.DroppedTurns));
        var overfull = usage.Free == 0 && usage.Used > usage.Room;
        if (overfull) notes.Add(HubStrings.Get("ChatContextOverfull"));
        ContextPopoverNote.Text = string.Join('\n', notes);
        ContextPopoverNote.IsVisible = notes.Count > 0;
        // Bound rather than set, and cleared when it no longer applies: a brush read by value would keep the
        // danger colour of whatever theme the popover happened to be built in.
        if (overfull)
            ContextPopoverNote.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
        else ContextPopoverNote.ClearValue(TextBlock.ForegroundProperty);
    }

    private void AddContextCategoryRow(string label, int tokens)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            Classes = { "context-category-label" },
            Text = label,
        });
        var value = new TextBlock
        {
            Classes = { "context-category-value" },
            Text = tokens.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        ContextCategoryList.Children.Add(row);
    }

    /// <summary>The heading a cost row is filed under. The keys live with the rest of the wording, because a row
    /// named in code rather than in the string table is a row that stays Chinese when the app turns English.</summary>
    private static string ContextCategoryKey(ContextCostKind kind) => kind switch
    {
        ContextCostKind.SystemPrompt => "ChatContextCatSystem",
        ContextCostKind.ProjectCharter => "ChatContextCatCharter",
        ContextCostKind.Summary => "ChatContextCatSummary",
        ContextCostKind.MemoryIndex => "ChatContextCatMemory",
        ContextCostKind.ToolSchema => "ChatContextCatTools",
        ContextCostKind.Messages => "ChatContextCatMessages",
        ContextCostKind.Attachments => "ChatContextCatAttachments",
        _ => "ChatContextCatImages",
    };

    private void RefreshContextCompressionButton()
    {
        if (_chat is null) return;
        var conversation = _chat.ActiveConversation;
        var compressing = conversation is not null && _chat.IsCompressingContext(conversation.Id);
        CompressContextButton.IsEnabled = conversation is not null
                                          && !compressing
                                          && _chat.CanCompressContext(conversation.Id);
        CompressContextButton.Content = HubStrings.Get(
            compressing ? "ChatCompressingContext" : "ChatCompressContext");
    }

    private void ShowContextMenu()
    {
        UpdateContextRing();
        ContextPopup.IsOpen = true;
    }

    private async Task CompressContextAsync()
    {
        if (_chat.ActiveConversation is not { } conversation) return;
        CompressContextButton.IsEnabled = false;
        CompressContextButton.Content = HubStrings.Get("ChatCompressingContext");
        try
        {
            if (!await _chat.CompressContextAsync(conversation.Id))
                AppendNotice(HubStrings.Get("ChatContextCompressUnavailable"), danger: false);
        }
        catch (Exception ex)
        {
            AppendNotice(HubStrings.Get("ChatContextCompressFailed") + ex.Message, danger: true);
        }
        finally
        {
            UpdateContextRing();
        }
    }

    private void ShowAddContextMenu() => ShowMenu(BuildComposerMenu(), AddContextButton);

    private void ShowPermissionMenu() => ShowMenu(BuildPermissionMenu(), PermissionChip);

    private void ShowWorkspaceMenu() => ShowMenu(BuildWorkspaceMenu(), WorkspaceChip);

    /// <summary>
    /// Pick a directory, and — only while one is bound — clear it. The bound path is not repeated as a row: the
    /// chip reads it out and the tooltip gives it in full, so a third copy would be chrome saying a thing twice.
    /// </summary>
    private MenuFlyout BuildWorkspaceMenu()
    {
        var menu = new MenuFlyout();
        var choose = new MenuItem { Header = HubStrings.Get("ChatWorkspaceMenuChoose") };
        choose.Click += async (_, _) =>
        {
            menu.Hide();
            await PickWorkspaceFolderAsync();
        };
        menu.Items.Add(choose);

        if (_chat.ActiveWorkspaceRoot is { Length: > 0 })
        {
            var clear = new MenuItem { Header = HubStrings.Get("ChatWorkspaceMenuClear") };
            clear.Click += (_, _) =>
            {
                _chat.SelectWorkspaceRoot(null);
                menu.Hide();
            };
            menu.Items.Add(clear);
        }

        return menu;
    }

    /// <summary>Refusals come back as a verdict, not as text: the sentences in <c>WorkspacePaths</c> are written
    /// for the model, and the panel has its own words for the same fact.</summary>
    private async Task PickWorkspaceFolderAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFolderAsync(owner, HubStrings.Get("ChatWorkspacePickTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFolderNotLocal"), danger: true);
            return;
        }

        switch (_chat.SelectWorkspaceRoot(result.Path))
        {
            case null:
                return;
            case WorkspacePathVerdict.ProtectedRoot:
                AppendNotice(HubStrings.Get("ChatWorkspaceProtected"), danger: true);
                return;
            default:
                AppendNotice(HubStrings.Get("ChatWorkspaceRejected") + result.Path, danger: true);
                return;
        }
    }

    /// <summary>
    /// The flyout this panel last opened. A popup is its own top-level, so a screenshot of one has to start
    /// from an item inside it — which is the only reason the instance is kept.
    /// </summary>
    private MenuFlyout? _openMenu;

    private void ShowMenu(MenuFlyout menu, Control anchor)
    {
        _openMenu = menu;
        menu.ShowAt(anchor);
    }

    /// <summary>
    /// The permission menu, hung off its own chip: three tiers, each stating what it lets through unasked, with
    /// the effective one ticked. A fourth row appears only when something overrides the app-wide default —
    /// otherwise it would be a second line on screen saying what the first already says. It works with no
    /// session on screen, where the pick lands on the one about to be started.
    /// </summary>
    private MenuFlyout BuildPermissionMenu()
    {
        var menu = new MenuFlyout();
        var effective = _chat.ActiveApprovalMode;
        foreach (var mode in new[] { ToolApprovalModes.Ask, ToolApprovalModes.Auto, ToolApprovalModes.Full })
        {
            var checkedItem = effective == mode;
            var item = new MenuItem
            {
                // The tick is drawn inside the row rather than by a toggle column: the column would sit to the
                // left of the tier's own icon and the two would read as one glyph doing a job twice. IsChecked
                // is still set, so the state stays on the item and not only in its pixels.
                Header = BuildPermissionHeader(mode, checkedItem),
                IsChecked = checkedItem,
            };
            item.Click += (_, _) =>
            {
                _chat.SelectApprovalMode(mode);
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        if ((_chat.ActiveConversation?.ApprovalMode ?? _chat.SelectedApprovalMode) is { Length: > 0 })
        {
            var follow = new MenuItem
            {
                Header = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatToolPermissionFollowFormat"),
                    HubStrings.Get(ToolApprovalModeKey(_chat.DefaultApprovalMode))),
                FontSize = 12,
            };
            follow.Click += (_, _) =>
            {
                _chat.SelectApprovalMode(null);
                menu.Hide();
            };
            menu.Items.Add(follow);
        }

        // A pick that differs from the app default is a per-session answer to what may be a standing preference,
        // and the setting that would make it stand lives on another page. One row here writes it: the same store,
        // the same repaint, the same rule the settings page's picker uses.
        if (effective != _chat.DefaultApprovalMode)
        {
            var makeDefault = new MenuItem
            {
                Header = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatToolPermissionSetDefaultFormat"),
                    HubStrings.Get(ToolApprovalModeKey(effective))),
                FontSize = 12,
                Tag = "ApprovalSetDefault",
            };
            makeDefault.Click += (_, _) =>
            {
                _chat.SetDefaultApprovalMode(effective);
                menu.Hide();
            };
            menu.Items.Add(makeDefault);
        }

        return menu;
    }

    /// <summary>Whether the menu would offer to make the current pick the app default. Built from the same menu
    /// the click builds, so the assertion is about the row a person can actually reach.</summary>
    internal bool ApprovalSetDefaultRowShownForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Any(item => "ApprovalSetDefault".Equals(item.Tag));

    private static Control BuildPermissionHeader(string mode, bool checkedItem)
    {
        var danger = mode == ToolApprovalModes.Full;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Data = ThemeGeometry(ToolApprovalIconKey(mode)),
        };
        // Bound rather than read once: a brush taken by value here does not follow a theme switch.
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension(danger ? "Hub.DangerText" : "Hub.TextSecondary"));

        var title = new TextBlock
        {
            Text = HubStrings.Get(ToolApprovalModeKey(mode)),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
        };
        var hint = new TextBlock
        {
            Text = HubStrings.Get(ToolApprovalModeHintKey(mode)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        if (danger)
        {
            title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
            hint.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.DangerText"));
        }
        else
        {
            hint.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextTertiary"));
        }

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(hint);

        var tick = new Avalonia.Controls.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry("Hub.Icon.Tick"),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsVisible = checkedItem,
        };
        tick.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        // One width for every row, so the ticks land in a column rather than wherever each description happens
        // to end.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinWidth = 300 };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(tick, 2);
        header.Children.Add(icon);
        header.Children.Add(text);
        header.Children.Add(tick);
        return header;
    }

    internal static string ToolApprovalIconKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "Hub.Icon.PermissionAuto",
        ToolApprovalModes.Full => "Hub.Icon.PermissionFull",
        _ => "Hub.Icon.PermissionAsk",
    };

    internal static string ToolApprovalModeKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "ToolApprovalAuto",
        ToolApprovalModes.Full => "ToolApprovalFull",
        _ => "ToolApprovalAsk",
    };

    internal static string ToolApprovalModeHintKey(string mode) => mode switch
    {
        ToolApprovalModes.Auto => "ToolApprovalAutoHint",
        ToolApprovalModes.Full => "ToolApprovalFullHint",
        _ => "ToolApprovalAskHint",
    };

    private MenuFlyout BuildComposerMenu()
    {
        var menu = new MenuFlyout();
        // Two modes rather than three. "提问" was the one with nothing behind it that the composer already
        // says: an assistant with no tools offered is what answering a question without acting on it means, and
        // a person who wants that simply does not pick 计划 or 目标. The mode itself stays — sessions that
        // recorded "ask" keep running without tools — it just has no menu item any more.
        foreach (var (mode, key) in new[]
                 {
                     (ChatModes.Plan, "ChatModePlan"),
                     (ChatModes.Agent, "ChatModeGoal"),
                 })
        {
            // Only 计划 wears a tick: the session's mode is a fact about it, and 目标 is what an unticked pair
            // already means, so that item is the way back to the default rather than a state to hold.
            var ticks = mode == ChatModes.Plan && _chat.ActiveMode == ChatModes.Plan;
            var item = new MenuItem
            {
                Header = BuildComposerModeHeader(mode, key),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = ticks,
                Tag = mode,
            };
            item.Click += (_, _) =>
            {
                _chat.SelectMode(ticks ? ChatModes.Agent : mode);
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        // Whether the model may stop and ask is neither of the two choices above it, and the row says so by where
        // it sits and what it is ticked by: 计划 and 目标 pick what this session can reach, this picks whether a
        // person is willing to be interrupted to settle something the repository cannot answer. It reads the
        // session on the way in and writes the session on the way out, so the tick cannot disagree with the
        // conversation it belongs to — which is the failure this menu's mode rows used to have.
        var askUser = new MenuItem
        {
            Header = HubStrings.Get("ChatAskUserMenuItem"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _chat.ActiveAskUserEnabled,
            Tag = "ask-user",
        };
        ToolTip.SetTip(askUser, HubStrings.Get("ChatAskUserMenuTip"));
        askUser.Click += (_, _) =>
        {
            // Derived from the session, not from the row's own new tick: the two agree today, and the day they
            // do not it is the control that is wrong.
            _chat.SelectAskUserEnabled(!_chat.ActiveAskUserEnabled);
            menu.Hide();
        };
        menu.Items.Add(askUser);

        var addFolder = new MenuItem { Header = HubStrings.Get("ChatAddLocalFolder") };
        addFolder.Click += async (_, _) => await AddLocalFolderAsync();
        menu.Items.Add(addFolder);

        var addFile = new MenuItem { Header = HubStrings.Get("ChatAddLocalFile") };
        addFile.Click += async (_, _) => await AddLocalFileAsync();
        menu.Items.Add(addFile);

        var addPicture = new MenuItem { Header = HubStrings.Get("ChatAddImage") };
        addPicture.Click += async (_, _) => await AddLocalPictureAsync();
        menu.Items.Add(addPicture);

        var projects = _chat.HubSnapshotProvider?.Invoke()?.Projects ?? [];
        var addProject = new MenuItem
        {
            Header = HubStrings.Get("ChatAddHubProject"),
            IsEnabled = projects.Count > 0,
        };
        foreach (var project in projects)
        {
            var projectItem = new MenuItem { Header = project.Name, Tag = project };
            projectItem.Click += (_, _) =>
            {
                if (projectItem.Tag is ChatWorkspace.HubProjectSummary selected)
                    AddProjectAttachment(selected);
            };
            addProject.Items.Add(projectItem);
        }

        if (projects.Count == 0)
        {
            addProject.Items.Add(new MenuItem
            {
                Header = HubStrings.Get("ChatNoHubProjects"),
                IsEnabled = false,
            });
        }
        menu.Items.Add(addProject);
        return menu;
    }

    private static Control BuildComposerModeHeader(string mode, string labelKey)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(BuildModeIcon(mode, 14));
        header.Children.Add(new TextBlock
        {
            Text = HubStrings.Get(labelKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return header;
    }

    private static Avalonia.Controls.Shapes.Path BuildModeIcon(string mode, double size)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry(ComposerModeIconKey(mode)),
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(
            Avalonia.Controls.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension("Hub.TextSecondary"));
        return icon;
    }

    private static string ComposerModeIconKey(string mode) => mode switch
    {
        ChatModes.Ask => "Hub.Icon.ChatModeAsk",
        ChatModes.Plan => "Hub.Icon.ChatModePlan",
        _ => "Hub.Icon.ChatModeGoal",
    };

    private void ShowModelMenu()
    {
        BuildModelMenu().ShowAt(ModelPicker);
    }

    private MenuFlyout BuildModelMenu()
    {
        var menu = new MenuFlyout();
        var choices = _chat.AvailableChatModels;
        var selected = _chat.SelectedChatModel;
        foreach (var choice in choices)
        {
            var isSelected = selected is not null
                && choice.Provider.Id == selected.Provider.Id
                && string.Equals(choice.ModelName, selected.ModelName, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = choice.Provider.Name + " · " + choice.ModelName,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = isSelected,
                Tag = choice,
            };
            item.Click += (_, _) => _chat.SelectChatModel(choice.Provider.Id, choice.ModelName);

            var knownEfforts = ModelCatalog.EffortsFor(choice.Provider, choice.ModelName);
            if (knownEfforts.Count > 0 || ModelCatalog.MayTryUnreportedEfforts(choice.Provider, choice.ModelName))
            {
                // A model that never reported a tier is offered the whole ladder — the first refusal trims it,
                // and the first thinking reply confirms it. Unknown must not read as impossible, or the only way
                // to find out is to ship a manifest entry for every model someone might connect to.
                var currentEffort = isSelected ? _chat.ActiveReasoningEffort : ChatReasoningEfforts.Default;
                foreach (var (effort, labelKey) in ReasoningChoices.Where(candidate =>
                             candidate.Value == ChatReasoningEfforts.Default
                             || knownEfforts.Count == 0
                             || knownEfforts.Contains(candidate.Value, StringComparer.OrdinalIgnoreCase)))
                {
                    var effortItem = new MenuItem
                    {
                        Header = HubStrings.Get(labelKey),
                        ToggleType = MenuItemToggleType.Radio,
                        IsChecked = currentEffort == effort,
                        Tag = effort,
                    };
                    effortItem.Click += (_, _) =>
                    {
                        if (!isSelected) _chat.SelectChatModel(choice.Provider.Id, choice.ModelName);
                        _chat.SelectReasoningEffort(effort);
                    };
                    item.Items.Add(effortItem);
                }
            }

            menu.Items.Add(item);
        }

        menu.Items.Add(BuildRoutingMenuItem());
        return menu;
    }

    /// <summary>
    /// Who picks the model and the tier for this session. It is a switch under the model list rather than another
    /// row in it, because "自动" as a menu entry would read exactly like the gateway's model id
    /// <c>orcarouter/auto</c> — the same word, three meanings, and this is the one that changes what gets spent.
    /// </summary>
    private MenuItem BuildRoutingMenuItem()
    {
        var conversationId = _chat.ActiveConversation?.Id ?? "";
        var auto = conversationId.Length > 0 && _chat.RoutingFor(conversationId) == ChatRouting.Auto;
        var item = new MenuItem { Header = HubStrings.Get("ChatRoutingMenu") };
        foreach (var (value, labelKey, isAuto) in new (string, string, bool)[]
                 {
                     (ChatRouting.Manual, "ChatRoutingManual", false),
                     (ChatRouting.Auto, "ChatRoutingAuto", true),
                 })
        {
            var choice = new MenuItem
            {
                Header = HubStrings.Get(labelKey),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = auto == isAuto,
            };
            choice.Click += (_, _) =>
            {
                if (conversationId.Length > 0) _chat.SetRouting(conversationId, value);
                RefreshComposerChoices();
            };
            item.Items.Add(choice);
        }
        return item;
    }

    private static readonly (string Value, string LabelKey)[] ReasoningChoices =
    [
        (ChatReasoningEfforts.Default, "ChatReasoningDefault"),
        (ChatReasoningEfforts.Low, "ChatReasoningLow"),
        (ChatReasoningEfforts.Medium, "ChatReasoningMedium"),
        (ChatReasoningEfforts.High, "ChatReasoningHigh"),
        (ChatReasoningEfforts.XHigh, "ChatReasoningXHigh"),
        (ChatReasoningEfforts.Max, "ChatReasoningMax"),
        (ChatReasoningEfforts.Ultra, "ChatReasoningUltra"),
    ];

    private static string ReasoningChoiceLabel(string effort)
    {
        var labelKey = ReasoningChoices.FirstOrDefault(choice => choice.Value == effort).LabelKey
                       ?? ReasoningChoices[0].LabelKey;
        var label = HubStrings.Get(labelKey);
        var separator = label.IndexOfAny(['：', ':']);
        return separator >= 0 ? label[(separator + 1)..].Trim() : label;
    }

    private async Task AddLocalFolderAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFolderAsync(owner, HubStrings.Get("ChatPickFolderTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFolderNotLocal"), danger: true);
            return;
        }

        var path = result.Path!;
        AddContextAttachment(new ContextAttachment(
            "folder",
            System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
            path));
    }

    /// <summary>One file as context — the same channel a folder goes down, for the case where the person knows
    /// exactly which file they mean. This is where a vector document enters: its markup is what a model can be
    /// handed, and the picture admission refuses it by its header.</summary>
    private async Task AddLocalFileAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFileAsync(owner, HubStrings.Get("ChatPickFileTitle"));
        if (result.Outcome == PickOutcome.Cancelled) return;
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatFileNotLocal"), danger: true);
            return;
        }

        AddFileAttachment(result.Path!);
    }

    /// <summary>Offers one file to the text channel, and says which rule refused it when one does: "that is not
    /// text" and "that is too large" are different fixes, and a notice that only says no gets the same file
    /// offered again. The answer is also what lets the drop path fall through to the picture refusal.</summary>
    private bool AddFileAttachment(string path)
    {
        var verdict = ChatContextReader.AdmitFile(path);
        if (verdict != ChatContextReader.FileAttachmentVerdict.Accepted)
        {
            AppendNotice(FileNotice(verdict), danger: true);
            return false;
        }

        AddContextAttachment(new ContextAttachment("file", System.IO.Path.GetFileName(path), path));
        return true;
    }

    private static string FileNotice(ChatContextReader.FileAttachmentVerdict verdict) => verdict switch
    {
        ChatContextReader.FileAttachmentVerdict.TooLarge => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatFileTooLarge"), ChatContextReader.MaxAttachedFileBytes / (1024 * 1024)),
        _ => HubStrings.Get("ChatFileNotText"),
    };

    private void AddProjectAttachment(ChatWorkspace.HubProjectSummary project)
    {
        var details = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatProjectContextFormat"),
            project.EngineVersion,
            project.Platform,
            project.Configuration,
            project.BuildStatus);
        AddContextAttachment(new ContextAttachment("project", project.Name, project.Path, details));
    }

    private void AddContextAttachment(ContextAttachment attachment)
    {
        if (_contextAttachments.Any(existing =>
                string.Equals(existing.Path, attachment.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            return;
        _contextAttachments.Add(attachment);
        RenderContextAttachments();
        UpdateContextRing();
    }

    private void RenderContextAttachments()
    {
        ContextAttachmentPanel.Children.Clear();
        foreach (var attachment in _contextAttachments)
        {
            var label = new TextBlock
            {
                Text = ContextAttachmentPrefix(attachment) + attachment.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 240,
            };
            label.Classes.Add("context-attachment-label");
            var remove = new Button { Content = "×", Tag = attachment };
            remove.Classes.Add("context-attachment-remove");
            ToolTip.SetTip(label, attachment.Path);
            remove.Click += (_, _) =>
            {
                if (remove.Tag is ContextAttachment selected)
                {
                    _contextAttachments.Remove(selected);
                    RenderContextAttachments();
                    UpdateContextRing();
                }
            };
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (VectorCover(attachment) is { } cover) chip.Children.Add(cover);
            chip.Children.Add(label);
            chip.Children.Add(remove);
            var border = new Border { Child = chip };
            border.Classes.Add("context-attachment");
            ContextAttachmentPanel.Children.Add(border);
        }

        foreach (var picture in _pendingPictures)
        {
            var remove = new Button { Content = "×", Tag = picture };
            remove.Classes.Add("context-attachment-remove");
            remove.Click += (_, _) =>
            {
                if (remove.Tag is PendingPicture selected)
                {
                    _pendingPictures.Remove(selected);
                    RenderContextAttachments();
                    UpdateSendState();
                }
            };

            // The thumbnail is the label: a picture's file name says nothing about it, and a person deciding
            // whether this is the capture they meant has to see it. Bytes rather than a path because nothing of
            // this draft has reached the disk yet.
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var thumb = TryOpenBitmap(picture.Bytes);
            Control face = thumb is null
                ? new TextBlock { Classes = { "muted" }, Text = picture.Name, MaxWidth = 120 }
                : new Button
                {
                    Classes = { "picture-face" },
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Content = new Image
                    {
                        Source = thumb,
                        Width = 34,
                        Height = 34,
                        Stretch = Stretch.Uniform,
                    },
                };
            if (face is Button opener)
            {
                // Thirty-four pixels of a screen capture is enough to recognise it and not enough to read it, and
                // this is the moment before the message goes out — the only one where "that is the wrong window"
                // is still cheap to act on. The draft's pictures are their own set: they have not been sent, so
                // paging from one into the transcript's would page into pictures this message does not have.
                var index = _pendingPictures.IndexOf(picture);
                opener.Click += (_, _) => ShowPictureViewer?.Invoke(DraftPictureSet(), index < 0 ? 0 : index);
            }
            chip.Children.Add(face);
            chip.Children.Add(remove);
            var frame = new Border { Child = chip };
            frame.Classes.Add("context-attachment");
            ToolTip.SetTip(frame, $"{picture.Name} · {picture.Bytes.LongLength} bytes");
            ContextAttachmentPanel.Children.Add(frame);
        }

        ContextAttachmentPanel.IsVisible = _contextAttachments.Count > 0 || _pendingPictures.Count > 0;
    }

    private static string ContextAttachmentPrefix(ContextAttachment attachment) => attachment.Kind switch
    {
        "project" => HubStrings.Get("ChatProjectPrefix"),
        "file" => HubStrings.Get("ChatFilePrefix"),
        _ => HubStrings.Get("ChatFolderPrefix"),
    };

    /// <summary>
    /// What an .svg attachment looks like, or null when there is nothing to draw with. The attachment is still
    /// text — the markup is what the model receives — and this is only the chip's face: a vector document drawn
    /// at chip size by the package the markdown renderer already brought in, so no raster is produced, stored or
    /// sent. Anything that fails to parse falls back to the name, because a chip that cannot say which file it is
    /// has stopped being a chip.
    /// </summary>
    private static Control? VectorCover(ContextAttachment attachment)
    {
        if (attachment.Kind != "file"
            || !System.IO.Path.GetExtension(attachment.Path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(attachment.Path)) return null;
        try
        {
            // A null base URI on purpose: the cover is a 34-pixel face, not a browser, and a document that names
            // a sibling file or a URL must not be able to make the composer read or fetch anything.
            return new Image
            {
                Source = new Avalonia.Svg.SvgImage
                {
                    Source = Avalonia.Svg.SvgSource.Load(attachment.Path, null, null),
                },
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
            };
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException
                                   or System.Xml.XmlException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private Task<string?> ReadAttachmentContextAsync()
    {
        if (_contextAttachments.Count == 0) return Task.FromResult<string?>(null);
        var attachments = _contextAttachments.ToArray();
        // The window is read here rather than inside the task: the attachment is priced against what the model
        // can actually hold, and a pool thread must not reach the workspace.
        var windowTokens = _chat?.SelectedChatModel is { } selected
            ? ContextBudget.For(selected.Provider, selected.ModelName).Tokens
            : ContextBudget.FallbackTokens;
        return Task.Run<string?>(() =>
        {
            var sections = attachments.Select(attachment =>
            {
                var contents = ChatContextReader.Read(attachment.Path, attachment.Name, windowTokens);
                return attachment.Details is { Length: > 0 }
                    ? attachment.Details + "\n\n" + contents
                    : contents;
            });
            return string.Join("\n\n", sections);
        });
    }

    // ───────────────────────── Pictures ─────────────────────────
    //
    // A picture is a second channel next to the text attachments: a folder becomes prompt text, a picture stays
    // bytes on the wire. They share the chip row and nothing else — reading one through the other would either
    // send a PNG as source code or quietly drop it.

    /// <summary>Offers one picture to the composer. The rules are Core's, the same four a captured frame is
    /// admitted by, and the notice names which one fired: "capture a smaller region" and "attach fewer at once"
    /// are different fixes, and a notice that only says "no" gets the same file picked again.</summary>
    private void AddPendingPicture(byte[] bytes, string name)
    {
        var verdict = ChatImageFormat.Admit(bytes, _pendingPictures.Count);
        if (verdict != ChatImageVerdict.Accepted)
        {
            AppendNotice(ImageNotice(verdict), danger: true);
            return;
        }

        _pendingPictures.Add(new PendingPicture(bytes, name));
        RenderContextAttachments();
        UpdateSendState();
    }

    /// <summary>Same admission for a picture that arrives as a file — picked, dropped, or copied from Explorer.
    /// The size is checked before the bytes are read, so dropping a folder of raw captures cannot freeze the
    /// window on a gigabyte Hub was going to refuse anyway.</summary>
    private void AddPictureFromPath(string path)
    {
        byte[]? bytes;
        ChatImageVerdict verdict;
        try
        {
            bytes = ConversationStore.ReadCandidateFile(path, out verdict);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
            return;
        }

        if (bytes is null)
        {
            // A file that is not a picture is not automatically nothing: an .svg, a .md, a source file dragged in
            // belongs to the text channel, which is the only one that can carry it. The text channel is asked
            // only when it could take the file — a .png that happens to hold text is a picture someone
            // mis-exported, and answering it with "not a text file" would name the wrong problem — and whatever
            // it answers is the last word, because two notices for one gesture read as two problems.
            if (verdict == ChatImageVerdict.Unrecognized
                && ChatContextReader.AdmitFile(path) != ChatContextReader.FileAttachmentVerdict.Unreadable)
            {
                AddFileAttachment(path);
                return;
            }

            AppendNotice(ImageNotice(verdict), danger: true);
            return;
        }

        AddPendingPicture(bytes, System.IO.Path.GetFileName(path));
    }

    private static string ImageNotice(ChatImageVerdict verdict) => verdict switch
    {
        ChatImageVerdict.Empty => HubStrings.Get("ChatImageEmpty"),
        ChatImageVerdict.Unrecognized => HubStrings.Get("ChatImageUnrecognized"),
        ChatImageVerdict.TooLarge => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatImageTooLarge"), ChatImageFormat.MaxImageBytes / (1024 * 1024)),
        _ => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatImageTooMany"), ChatImageFormat.MaxImagesPerMessage),
    };

    /// <summary>What a refused send should say. The picture refusals are worded with the limit in them, so they
    /// go through the same formatter the composer used when it refused the picture on the way in — a key shown
    /// raw would print the brace.
    ///
    /// <para><c>NoAvailableChatModels</c> splits in two, because one sentence cannot describe both states: the
    /// picker genuinely empty, which is a settings problem, and this session naming a model its provider stopped
    /// offering while other models sit in the list right there. The second is fixable in the chip — provided the
    /// line says which name went missing, rather than claiming nothing is available when plenty is.</para></summary>
    private string NoticeFor(string? refusalKey, string? conversationId) => refusalKey switch
    {
        "ChatImageEmpty" => ImageNotice(ChatImageVerdict.Empty),
        "ChatImageUnrecognized" => ImageNotice(ChatImageVerdict.Unrecognized),
        "ChatImageTooLarge" => ImageNotice(ChatImageVerdict.TooLarge),
        "ChatImageTooMany" => ImageNotice(ChatImageVerdict.TooMany),
        "NoAvailableChatModels" when _chat.UnresolvableSessionModel(conversationId ?? "") is { } gone
            => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatSessionModelGoneFormat"), gone),
        _ => HubStrings.Get(refusalKey ?? "ChatFailed"),
    };

    private async Task AddLocalPictureAsync()
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null) return;
        var result = await Pickers.PickFileAsync(owner, HubStrings.Get("ChatPickImageTitle"), PictureFileTypes);
        if (result.Outcome == PickOutcome.NotLocal)
        {
            AppendNotice(HubStrings.Get("ChatImageNotLocal"), danger: true);
            return;
        }

        if (result.Outcome != PickOutcome.Picked || result.Path is not { } path) return;
        AddPictureFromPath(path);
    }

    /// <summary>The picker's filter is a courtesy, not a rule: what a picture *is* stays a decision about the
    /// bytes' header, because a pasted capture has no file name to match.</summary>
    private static IReadOnlyList<FilePickerFileType> PictureFileTypes { get; } =
    [
        new("PNG") { Patterns = ["*.png"] },
        new("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
        new("GIF") { Patterns = ["*.gif"] },
        new("WebP") { Patterns = ["*.webp"] },
    ];

    /// <summary>Ctrl+V in the composer: a picture on the clipboard becomes a chip, and everything else keeps
    /// being the text paste the box already does. Claiming the keystroke only once there is a picture to take is
    /// what keeps an ordinary paste working — a paste handler that eats text is a worse bug than no paste.</summary>
    private async Task<bool> TryPastePictureAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        IAsyncDataTransfer? transfer;
        try
        {
            transfer = await clipboard.TryGetDataAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return await TakeClipboardAsync(transfer);
    }

    /// <summary>The paste's one decision: did a picture get taken, or should the box paste text as it always
    /// has? Split out because it is the half a check can be handed a payload for.</summary>
    private async Task<bool> TakeClipboardAsync(IAsyncDataTransfer? transfer)
        => transfer is not null && await TakePicturesAsync(transfer) > 0;

    /// <summary>
    /// The paste's actual decision, separated from the clipboard so it can be handed a transfer of a known shape.
    /// A screenshot arrives as a bitmap with no file name, a copied file arrives as a storage item, and a copy
    /// from some applications arrives as both — the loop takes what is a picture and leaves the rest alone.
    /// </summary>
    private async Task<int> TakePicturesAsync(IAsyncDataTransfer transfer)
    {
        var taken = 0;
        foreach (var item in transfer.Items)
        {
            if (item.Formats.Contains(DataFormat.Bitmap)
                && await item.TryGetRawAsync(DataFormat.Bitmap) is Bitmap bitmap)
            {
                AddPendingPicture(EncodePng(bitmap), HubStrings.Get("ChatPastedImageName"));
                taken++;
            }
            else if (item.Formats.Contains(DataFormat.File)
                     && await item.TryGetRawAsync(DataFormat.File) is { } raw)
            {
                foreach (var path in LocalPathsOf(raw)) AddPictureFromPath(path);
                taken++;
            }
        }

        return taken;
    }

    /// <summary>A clipboard or drop payload hands back one item, a list of them, or an array — the shape is the
    /// platform's business, so the view accepts all three rather than betting on one.</summary>
    private static IEnumerable<string> LocalPathsOf(object raw)
    {
        IReadOnlyList<IStorageItem> items = raw switch
        {
            IStorageItem one => [one],
            IEnumerable<IStorageItem> many => many.ToArray(),
            _ => [],
        };
        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }

    /// <summary>A bitmap has to become the bytes Hub keeps, and PNG is the only honest answer: a capture loses
    /// nothing to it, and the header is what says so on the wire.</summary>
    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new System.IO.MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static bool DragCarriesFiles(DragEventArgs e)
        => e.DataTransfer.Items.Any(item => item.Formats.Contains(DataFormat.File));

    private void OnComposerDrop(object? sender, DragEventArgs e)
    {
        // The ring is cleared whatever the drop carried: a leave does not necessarily follow a drop, so a ring
        // that only comes off on DragLeave would stay lit for the rest of the session.
        ComposerFrame.Classes.Remove("drag-over");
        if (!DragCarriesFiles(e)) return;
        e.Handled = true;
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(DataFormat.File) is { } raw)
                foreach (var path in LocalPathsOf(raw)) AddPictureFromPath(path);
    }

    /// <summary>The bytes of every picture waiting in the composer, in chip order, or null when there are none —
    /// the shape the send and steer paths take so an empty draft stays exactly the request it was before.</summary>
    private IReadOnlyList<byte[]>? PendingPictureBytes()
        => _pendingPictures.Count == 0 ? null : _pendingPictures.Select(picture => picture.Bytes).ToArray();

    /// <summary>
    /// The pictures one sent message carries, painted from the file Hub kept. Bounded so a 5K capture cannot push
    /// the chat column open, and a file that is no longer there says so rather than drawing nothing at all — the
    /// transcript claims a picture was sent, and that claim has to stay checkable.
    ///
    /// <para>Every cell is built at once but its pixels are not: the decode runs off the UI thread and to the
    /// width the column shows (see <see cref="DecodeTurnPictureAsync"/>), because a session with screenshots in
    /// it would otherwise pay several megapixels of decoding inside the frame that opens it.</para>
    /// </summary>
    private Control BuildTurnPictures(string conversationId, IReadOnlyList<ChatImage> images)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Classes.Add("turn-pictures");
        foreach (var image in images)
        {
            var path = _chat.StoredImagePath(conversationId, image.File);
            // A missing file is answered from the name alone. That is a fact about the disk rather than about
            // the pixels, and waiting for a decode to fail would say it one frame later.
            if (path is null)
            {
                row.Children.Add(GonePictureCell(image));
                continue;
            }

            var picture = new Image { Stretch = Stretch.Uniform, MaxWidth = 260, MaxHeight = 200 };
            ToolTip.SetTip(picture, $"{image.File} · {image.MediaType} · {image.Bytes} bytes");
            // The spacing moves onto the button so the gap between two thumbnails is not itself a hit target.
            var frame = new Border { Child = picture, Classes = { "turn-picture" }, Margin = new Thickness(0) };
            var opener = new Button
            {
                Classes = { "picture-face" },
                Content = frame,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            // The set is the conversation's pictures in transcript order and this one's place in it; the
            // bytes stay unread until the viewer lands on them (see PictureRef).
            var key = $"{conversationId}#{image.File}";
            opener.Click += (_, _) =>
            {
                var set = TranscriptPictureSet(conversationId);
                var at = 0;
                while (at < set.Count && set[at].Key != key) at++;
                ShowPictureViewer?.Invoke(set, at >= set.Count ? 0 : at);
            };
            row.Children.Add(opener);
            _ = DecodeTurnPictureAsync(path, picture, opener, image);
        }

        return row;
    }

    /// <summary>One frame saying the picture is gone, in the shape a transcript row gives it. Shared by the two
    /// ways a picture can be missing — no file, and a file no decoder will take.</summary>
    private static Border GonePictureCell(ChatImage image)
    {
        var gone = new TextBlock { Classes = { "muted" }, Text = HubStrings.Get("ChatImageGone") };
        ToolTip.SetTip(gone, $"{image.File} · {image.MediaType} · {image.Bytes} bytes");
        return new Border { Child = gone, Classes = { "turn-picture" } };
    }

    /// <summary>
    /// Fills one transcript thumbnail, off the UI thread and at the size the column draws.
    ///
    /// <para>Width rather than height because the row is a horizontal wrap of pictures capped at
    /// <c>260</c> logical pixels: a screenshot keeps everything a 260-wide slot can show and nothing above it, so
    /// decoding the full 4K frame would be twelve megapixels to make a two-hundred-pixel image. The scale factor
    /// is the window's, so a 2x display still gets 2x of pixels.</para>
    /// </summary>
    private async Task DecodeTurnPictureAsync(string path, Image target, ContentControl holder, ChatImage image)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var width = (int)Math.Clamp(Math.Ceiling(260 * scale), 64, 1560);
        Bitmap? bitmap;
        try
        {
            bitmap = await Task.Run(() => TryDecodeBitmap(path, width)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Off the UI thread there is no frame to fail: a decoder that throws something the narrow list in
            // TryDecodeBitmap did not name still ends as a cell that says the picture is gone, which is the same
            // answer the renderer gives a file it cannot read at all.
            bitmap = null;
        }

        if (bitmap is not null)
        {
            target.Source = bitmap;
            return;
        }

        // A file the header promised and the decoder refused. It says so where the picture would have been,
        // rather than leaving an empty frame that reads as a picture Hub has not finished loading.
        holder.Content = GonePictureCell(image);
    }

    /// <summary>
    /// The conversation's pictures in transcript order, which is the set the window-level viewer pages through.
    /// A picture whose stored file is gone still gets an entry: the transcript claims it was sent, and a viewer
    /// that says "the picture is gone" keeps that claim checkable, where dropping the entry would silently
    /// renumber every picture after it.
    /// </summary>
    private IReadOnlyList<PictureRef> TranscriptPictureSet(string conversationId)
    {
        var list = new List<PictureRef>();
        if (_chat.ActiveConversation is not { } conversation || conversation.Id != conversationId) return list;
        for (var index = 0; index < conversation.Messages.Count; index++)
        {
            var turn = conversation.Messages[index];
            if (turn.Role != ChatRoles.User) continue;
            foreach (var image in turn.Images)
            {
                var file = image.File;
                list.Add(new PictureRef(
                    Key: $"{conversationId}#{file}",
                    Caption: $"{image.File} · {image.MediaType} · {image.Bytes} bytes",
                    Bytes: image.Bytes,
                    Load: () => ReadPictureBytes(_chat.StoredImagePath(conversationId, file))));
            }
        }
        return list;
    }

    /// <summary>The composer's unsent pictures as their own set: they have not reached the disk, and paging
    /// from a draft into the transcript would page into pictures this message does not carry.</summary>
    private IReadOnlyList<PictureRef> DraftPictureSet()
        => _pendingPictures.Select((picture, index) => new PictureRef(
            Key: $"draft#{index}",
            Caption: $"{picture.Name} · {picture.Bytes.LongLength} bytes",
            Bytes: picture.Bytes.LongLength,
            Load: () => picture.Bytes)).ToList();

    /// <summary>The stored bytes of one attached picture, or null when they are no longer readable — which the
    /// viewer answers by saying so in the caption row rather than drawing an empty frame.</summary>
    private static byte[]? ReadPictureBytes(string? path)
    {
        if (path is not { Length: > 0 }) return null;
        try
        {
            return System.IO.File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>One transcript thumbnail, decoded to the width the column can show. Read from a stream rather
    /// than from a file name because that is the form of the call that takes a width — and the width is the whole
    /// point: the reader is shown 260 logical pixels of a capture that may be 4000 wide.</summary>
    private static Bitmap? TryDecodeBitmap(string path, int width)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The same preview from bytes, because a draft has not reached the disk. A picture whose header
    /// Hub recognizes but whose pixels it cannot decode stays in the queue under its file name rather than
    /// vanishing — what the model is going to receive is the bytes, not this preview.</summary>
    private static Bitmap? TryOpenBitmap(byte[] bytes)
    {
        try
        {
            return new Bitmap(new System.IO.MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static string ToolActivityText(string name, bool completed)
    {
        var tool = name switch
        {
            "get_projects" => HubStrings.Get("ChatToolProjects"),
            "get_engines" => HubStrings.Get("ChatToolEngines"),
            "get_toolchain_status" => HubStrings.Get("ChatToolchains"),
            _ => name,
        };
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get(completed ? "ChatToolCompletedFormat" : "ChatToolRunningFormat"),
            tool);
    }

    /// <summary>The bubble's status line while the endpoint is searching on its own. A start event arrives with no
    /// query in it — that is the whole meaning of "in progress" on this wire — so the line says only that it is
    /// looking, and the first query replaces it when the service reports one.</summary>
    private static string SearchActivityText(ServerSearchNotice notice)
    {
        var queries = notice.Queries;
        if (queries.Count == 0) return HubStrings.Get("ActivityRowWebSearching");
        var first = queries[0];
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ActivityRowWebSearched"),
            first.Length <= 48 ? first : first[..48] + "…");
    }

    /// <summary>The bubble's status line while the pipeline sits out a provider's retry delay. The denominator is
    /// the policy's own number rather than a guess, so the line cannot drift from how many attempts exist.</summary>
    private static string RetryActivityText((int Attempt, TimeSpan Delay) wait)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatRetryWaitFormat"), wait.Attempt, Agent.ChatClientFactory.MaxRetries + 1,
            wait.Delay.TotalSeconds < 1 ? wait.Delay.TotalSeconds.ToString("0.#")
                : Math.Round(wait.Delay.TotalSeconds).ToString("0"));

    /// <summary>
    /// Where the answer came from, under the reply that used it. One quiet line: 「来源」 and as many hosts as the
    /// line can afford, each opening in the browser, the rest folded into a +N whose tooltip carries the whole list.
    ///
    /// <para>It sits with the answer rather than inside the collapsed action group because a citation is not an
    /// action: the person reads the sentence, then decides whether to check it, and making that require opening a
    /// fold they had to notice first is how a client teaches people not to read sources. The queries go in the
    /// line's tooltip — the interesting half of "what did it do" is the address it ended up trusting.</para>
    ///
    /// <para><b>Why a character budget rather than "four and done":</b> this line shares the chat column with the
    /// answer above it, and the column is as narrow as the 560px floor an open inspector leaves. Four chips of long
    /// hostnames is a row wider than that, and the charter is that a surface clips — a clipped source is a source
    /// nobody can click. So the line spends <see cref="SourceChipBudget"/> characters of label, always at least one
    /// chip, never more than four, and everything it cannot afford folds into the +N. Nothing is lost: the fold's
    /// tooltip lists the remaining addresses in the order the endpoint reported them.</para>
    /// </summary>
    private Control BuildSearchSources(ServerSearchLog searches)
    {
        const int SourceChipBudget = 64;
        const int SourceChipLimit = 4;
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        line.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("MessageSources"),
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var sources = searches.Sources();
        var shown = 0;
        var used = 0;
        while (shown < sources.Count && shown < SourceChipLimit
               && (shown == 0 || used + HostOf(sources[shown]).Length + 2 <= SourceChipBudget))
        {
            var address = sources[shown];
            var host = HostOf(address);
            var chip = new Border
            {
                // file-chip for the look, source-chip so a check can tell these from the other chips in the same
                // panel — the run_command preview and the diff counters share the styling and nothing else.
                Classes = { "file-chip", "source-chip" },
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = host, FontSize = 12 },
            };
            ToolTip.SetTip(chip, address);
            // Through the workspace's opener rather than UrlLauncher directly: the same seam the rest of the
            // browser-bound traffic uses, so a self-check can count what a click would have opened without
            // opening anything.
            var target = address;
            chip.PointerPressed += (_, _) => _chat.BrowserOpener(target);
            line.Children.Add(chip);
            used += host.Length + 2;
            shown++;
        }

        if (sources.Count > shown)
        {
            var rest = new TextBlock
            {
                Text = "+" + (sources.Count - shown),
                Classes = { "muted", "source-fold" },
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(rest, string.Join('\n', sources.Skip(shown)));
            line.Children.Add(rest);
        }

        var queries = searches.Queries();
        if (queries.Count > 0)
            ToolTip.SetTip(line, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("MessageSearchedFor"), string.Join(" · ", queries)));
        return line;
    }

    /// <summary>The label a source chip gets: the host, plus its path when the path is short enough to be worth
    /// the room. It is a label, not a URL — the full address lives in the tooltip and in what a click opens — so
    /// an over-long one is cut rather than allowed to push the rest of the line off the column.</summary>
    private static string HostOf(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var target)) return Shorten(address);
        var path = target.AbsolutePath.Trim('/');
        return Shorten(path.Length > 0 && path.Length <= 24 ? $"{target.Host}/{path}" : target.Host);
    }

    /// <summary>The one place a chip's text is measured, so the line's budget and the chip's own cap cannot drift
    /// into two different notions of how wide a source is allowed to be.</summary>
    private static string Shorten(string label) => label.Length <= 34 ? label : label[..33] + "…";

    // ───────────────────────── Messages ─────────────────────────

    private void RenderMessages()
    {
        if (_chat is null) return;
        PruneFoldMemory();
        var conversation = _chat.ActiveConversation;
        _projection = conversation is null ? null : _projectionStates.GetValue(conversation, _ => new());
        var snapshot = conversation is null ? null
            : _projection!.Build(conversation, _chat.ShowThinking, EagerMarkdownLimit);
        if (conversation is not null)
        {
            foreach (var memory in new[] { _expandedGroups, _expandedRows })
                if (memory.TryGetValue(conversation.Id, out var keys))
                    keys.RemoveWhere(key => !long.TryParse(key.AsSpan(key.IndexOf(':') + 1),
                        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                        out var occurrence) || !_projection!.TryResolve(occurrence, out _));
        }
        var run = conversation is null ? null : _chat.RunFor(conversation.Id);

        // The live bubble is not a stored turn. It comes off the flow before the stored rows are laid down and
        // goes back on the end afterwards, so a repaint in the middle of a reply cannot bury it mid-transcript.
        // A bubble belonging to another session is closed for good: its run keeps going, and coming back to it
        // rebuilds the bubble from what arrived while it was out of sight — text, tools and elapsed time alike,
        // because all three are read off the run and none of them is a number this view was keeping.
        // A bubble that outlives its run would keep animating a reply that has already been written to the
        // transcript, where it now belongs — so the absence of a streaming run for this session closes it,
        // whoever happened to ask for a repaint.
        if (_live is { } attached && (run is not { IsStreaming: true } || attached.Run != run))
            CloseLive();
        if (_live is { } held) MessageFlow.Children.Remove(held.Row);
        // The command card comes off for the same reason and goes back on at the foot, after the stored rows: a
        // command printing while the transcript is rebuilt must not leave its card stranded above the rows that
        // explain it. The object survives the removal, so an opened panel and a scroll position survive it too.
        if (_runningCommand is { } printing) MessageFlow.Children.Remove(printing.Shell);

        var visible = new List<(int Index, ChatTurn Turn, string? ToolName)>();
        if (conversation is not null)
        {
            // A tool result prints the name its call carried, and the result turn does not keep it. Gathered in
            // one pass rather than looked up per result: `IndexOfToolCall` is a linear scan, so asking it once
            // per result is quadratic in the session's length — and an agent session is exactly where both the
            // length and the number of results are large. First call wins, which is the scan's own rule, so a
            // transcript with a duplicated id reads the same as it always did.
            var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var earlier in conversation.Messages)
            {
                if (earlier.Role != ChatRoles.Assistant || earlier.ToolCallId is not { Length: > 0 } id) continue;
                if (earlier.ToolName is { } named && !callNames.ContainsKey(id)) callNames[id] = named;
            }

            for (var i = 0; i < conversation.Messages.Count; i++)
            {
                var turn = conversation.Messages[i];
                // A system turn is context the model was given, not something anybody said. A tool result stays
                // in the flow but not as a bubble — see <see cref="ToolResultLine"/> — and the name it prints is
                // the one its call carried, because the result turn itself does not keep it.
                if (turn.Role == ChatRoles.System) continue;
                visible.Add((i, turn, turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } answered
                    ? callNames.GetValueOrDefault(answered)
                    : null));
            }
        }

        // A decision changes what an already-rendered turn looks like without changing how many turns there
        // are, which is precisely the case incremental rendering cannot see: the stamp is what lets a card
        // collapse into its one-line record instead of keeping buttons that no longer mean anything.
        //
        // Captured before the two stamps move forward: parking the session being left files its rows under *its*
        // decisions, and reading the fields afterwards filed them under the incoming session's — which is why a
        // switch between two sessions whose cards differed threw the rows away on the way back.
        var priorApprovalStamp = _renderedApprovalStamp;
        var priorShowThinking = _renderedShowThinking;
        var approvalStamp = ApprovalStamp(conversation);
        var approvalChanged = approvalStamp != _renderedApprovalStamp;
        _renderedApprovalStamp = approvalStamp;

        // Whether the thinking is drawn is a fact about how a turn looks, not about how many turns there are, so
        // an append cannot carry it: every turn already on screen would keep wearing the old answer. The same
        // shape as the approval stamp just above, and for the same reason.
        var showThinkingChanged = _renderedShowThinking != _chat.ShowThinking;
        _renderedShowThinking = _chat.ShowThinking;

        // Coming back to a session whose rows are still parked re-attaches them instead of drawing them again.
        // None of the conditions above can see this case — it is another session by definition, which is exactly
        // what a teardown fires on — so it is answered here, before the teardown below pays for Markdig and
        // AvaloniaEdit a second time over the same turns.
        if (conversation is { } returning
            && !string.Equals(returning.Id, _renderedConversationId, StringComparison.Ordinal)
            && TakeParked(returning.Id, visible, approvalStamp) is { } parked)
        {
            ParkCurrentTranscript(priorApprovalStamp, priorShowThinking);
            MessageFlow.Children.Clear();
            foreach (var row in parked.Rows) MessageFlow.Children.Add(row);
            _renderedConversationId = returning.Id;
            _renderedCount = visible.Count;
            _paintedFrom = parked.PaintedFrom;
            _renderedTailKey = _projection!.KeyAt(visible[^1].Index);
            _renderedTurns = parked.Turns;
            _renderedKeys = parked.Keys;
            _pendingMarkdownRows = parked.PendingMarkdown;
            _openGroup = parked.OpenGroup;
            _parkedTranscripts.Remove(parked);
            // The rows came back, not the reading position: a session opens on its newest message exactly as it
            // does the first time, which is what a person who clicked it in the sidebar asked for.
            _stickToBottom = true;
            _openReusedRows = true;
            EmptyState.IsVisible = false;
            SyncRunningCommandRow(conversation.Id, run);
            if (run is { IsStreaming: true }) MessageFlow.Children.Add(AttachLive(run).Row);
            RenderNotice();
            ScrollToEndIfSticky();
            UpdateScrollAffordance();
            // The rows that came back carry the promotions they never got: which of them are within reach is a
            // question about where the view lands, so it is asked after the landing has been queued.
            ScheduleMarkdownPromotion();
            return;
        }

        if (visible.Count == 0)
        {
            // Whatever was on screen is still worth keeping: leaving a session for the empty state is the same
            // errand as leaving it for another one.
            ParkCurrentTranscript(priorApprovalStamp, priorShowThinking);
            MessageFlow.Children.Clear();
            _renderedConversationId = conversation?.Id;
            _renderedCount = 0;
            _paintedFrom = 0;
            _renderedTailKey = 0;
            _renderedTurns = [];
            _renderedKeys = [];
            _pendingMarkdownRows = [];
            _openGroup = null;
            EmptyState.IsVisible = run is not { IsStreaming: true };
            SyncRunningCommandRow(conversation?.Id, run);
            if (run is { IsStreaming: true }) MessageFlow.Children.Add(AttachLive(run).Row);
            RenderNotice();
            UpdateScrollAffordance();
            return;
        }

        // A teardown is for the cases an append cannot express: another session's turns, a history that shrank
        // under an edit or a regenerate, a decision that rewrote what an already-painted turn looks like, a display
        // switch that did the same, a turn that has moved into a slot it did not occupy when the flow was laid
        // down, and a window the person just widened. A growing activity run is none of those: its new turns extend
        // the group that is already the last row, which is what keeps a head the person had opened from being
        // swapped out for a fresh collapsed one.
        var aDifferentSession = _renderedConversationId != conversation!.Id;
        var historyShrank = visible.Count < _renderedCount;
        if (aDifferentSession || historyShrank || approvalChanged || showThinkingChanged || _paintFromScratch
            || (_renderedCount > 0 && _projection!.KeyAt(visible[_renderedCount - 1].Index) != _renderedTailKey
                || !RenderedPrefixMatches(visible)))
        {
            // The session being left keeps its rows, and this is the call that makes coming back cheap: the next
            // visit to it is usually the very next thing the person does. Only on a switch — a teardown for a
            // decision or a shrunken history is this same session being redrawn, and its own rows are exactly
            // what is about to be wrong.
            if (aDifferentSession) ParkCurrentTranscript(priorApprovalStamp, priorShowThinking);
            _paintFromScratch = false;
            MessageFlow.Children.Clear();
            _renderedConversationId = conversation.Id;
            _renderedCount = 0;
            _paintedFrom = 0;
            _renderedTurns = [];
            _renderedKeys = [];
            // The records die with the rows they pointed at: everything below lays the transcript down again,
            // and a record left over from the flow that was just thrown away would name a control nobody can
            // reach, which is a promotion that silently paints into a detached panel.
            _pendingMarkdownRows = [];
            _openGroup = null;
            // A session opens on its newest message. The scroll offset belongs to the viewer rather than to the
            // conversation, so without this a switch inherits wherever the previous one had been read up to.
            // Revalidating the same transcript is not a switch, though: whoever had scrolled up to read something
            // stays where they put it — and neither is a window the person just widened while reading the top.
            if (aDifferentSession || historyShrank) _stickToBottom = true;
        }

        var lastIndex = conversation.Messages.Count - 1;
        // The transcript as rows, computed once and then painted. The loop below no longer decides where a piece
        // begins or ends: it reads that from the model, which is the point of having one — the boundaries are one
        // answer computed in one place instead of a predicate soup evaluated while drawing.
        // The snapshot was reconciled before cache lookup so parked rows and deferred work see current keys.
        // The window. A fresh flow starts at a *piece boundary* near the newest FirstPaintTurns turns, never inside
        // one: a cut between a call and its result paints a result whose call is above it. Only a fresh flow is
        // windowed — an append is a row at the end, and taking rows away to fit a window would be a teardown
        // wearing a different hat.
        if (_renderedCount == 0 && visible.Count > _paintWindow)
        {
            var first = 0;
            while (first < snapshot!.Entries.Count
                   && visible.Count - snapshot!.Entries[first].FirstVisible > _paintWindow) first++;
            _paintedFrom = first < snapshot!.Entries.Count ? snapshot!.Entries[first].FirstVisible : visible.Count;
            _renderedCount = _paintedFrom;
            MessageFlow.Children.Add(BuildLoadEarlierRow(conversation.Id, _paintedFrom));
        }
        else if (_renderedCount == 0)
        {
            _paintedFrom = 0;
        }

        foreach (var entry in snapshot!.Entries)
        {
            if (entry.FirstVisible >= visible.Count) break;
            switch (entry)
            {
                case TranscriptActivity activity:
                {
                    var piece = activity.Piece;
                    // Everything up to _renderedCount is already on the page: the window's prefix, or the piece
                    // that has not grown since the last paint.
                    if (piece.EndVisible <= _renderedCount) continue;
                    // Re-sliced from where the run began, not the turns that just arrived: a row is drawn from a
                    // call <i>and</i> the result that answered it, so handing the group only the new second half
                    // would draw every result as a call nobody had asked for. A piece that grew is the open group
                    // still last in the flow, which is what keeps a head somebody expanded from being replaced.
                    var slice = visible.GetRange(piece.FirstVisible, piece.EndVisible - piece.FirstVisible);
                    if (piece.FirstVisible < _renderedCount && _openGroup is { } prior
                        && ReferenceEquals(MessageFlow.Children[^1], prior.Group))
                    {
                        prior.Turns = slice;
                        prior.EndVisible = piece.EndVisible;
                        DeriveActivityGroup(prior, conversation.Id, lastIndex, visible.Count);
                    }
                    else
                    {
                        _openGroup = CreateActivityGroup(conversation.Id, piece.FirstVisible, slice, piece.Folded);
                        DeriveActivityGroup(_openGroup, conversation.Id, lastIndex, visible.Count);
                    }

                    _renderedCount = piece.EndVisible;
                    continue;
                }

                case TranscriptMessage message:
                {
                    if (message.FirstVisible < _renderedCount) continue;
                    // Prose closes the piece: the next piece of machine work is a different exchange.
                    _openGroup = null;
                    var (index, turn, toolName) = visible[message.FirstVisible];
                    AppendRenderedTurn(conversation.Id, index, turn, toolName, isLast: index == lastIndex,
                        markdown: message.Markdown, target: MessageFlow, inGroup: false);
                    _renderedCount = message.FirstVisible + 1;
                    continue;
                }

                case TranscriptNotice notice:
                {
                    if (notice.FirstVisible < _renderedCount) continue;
                    // A notice is a line about the conversation rather than something anybody said, so it gets the
                    // notice row this same flow already uses at its bottom — centered in the column, which is what
                    // makes a model switch read as a divider rather than as a reply that happened to be short.
                    _openGroup = null;
                    MessageFlow.Children.Add(BuildNoticeRow(visible[notice.FirstVisible].Turn.Text, danger: false,
                        onRetry: null));
                    _renderedCount = notice.FirstVisible + 1;
                    continue;
                }
            }
        }

        // Where the painted prefix ends, in the conversation's own terms. The next render compares against this
        // rather than against the count, which is what lets a result filed beside its call — an insert, not an
        // append — rebuild the flow instead of drawing the shifted tail twice.
        _renderedTailKey = _projection!.KeyAt(visible[^1].Index);

        // The turns these rows stand for, held by reference so that leaving the session keeps a way to ask
        // whether they still say what was drawn (see <see cref="RenderedTranscript"/>).
        _renderedTurns = [.. visible.Select(slot => slot.Turn)];
        _renderedKeys = [.. visible.Select(slot => _projection!.KeyAt(slot.Index))];

        EmptyState.IsVisible = false;
        SyncRunningCommandRow(conversation.Id, run);
        if (run is { IsStreaming: true }) MessageFlow.Children.Add(AttachLive(run).Row);
        RenderNotice();
        // The rows that just went in have not been measured yet, so this is posted: a view that was following
        // the reply keeps following it, and one the person had scrolled away from is left where they put it.
        ScrollToEndIfSticky();
        UpdateScrollAffordance();
        ScheduleMarkdownPromotion();
    }

    /// <summary>Forces a full rebuild on the next <see cref="RenderMessages"/> — used after an edit or a
    /// regenerate shortened the history, where the visible prefix no longer matches the stored turns.</summary>
    private void ForceRebuildMessages()
    {
        _renderedConversationId = null;
        _renderedCount = 0;
        RenderMessages();
    }

    /// <summary>
    /// The line a windowed transcript opens with, standing for every turn above it.
    ///
    /// <para>Not a message row and not a machine panel — it carries neither class, so nothing in the transcript's
    /// row accounting, its fold memory or its checks can mistake it for something somebody said. What it does
    /// carry is the count, because "earlier messages" alone gives a reader no way to tell whether the click buys
    /// forty rows or four thousand.</para>
    /// </summary>
    private Control BuildLoadEarlierRow(string conversationId, int hidden)
    {
        var button = new Button
        {
            Classes = { "load-earlier" },
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatLoadEarlier"), hidden),
        };
        button.Click += (_, _) => ExpandEarlierTurns(conversationId);
        return new Border { Classes = { "load-earlier-row" }, Child = button };
    }

    /// <summary>
    /// Widens the window and lays the transcript down again from further up.
    ///
    /// <para>The click is a person saying they want the earlier messages, so the work is paid once, on their word,
    /// rather than guessed at while they read: a background pass that kept laying the flow down again would tear
    /// the transcript apart under the eye for content nobody had asked for yet. Doubling rather than "everything"
    /// keeps each click's pause bounded — 30, 60, 120 — and on any real session the last click lands on the whole
    /// history, because the window has passed the transcript's length.</para>
    ///
    /// <para>Nothing scrolls. The person is at the top by definition, since that is where the line they clicked
    /// was, and the content that arrives is the content they asked for.</para>
    /// </summary>
    private void ExpandEarlierTurns(string conversationId)
    {
        if (!string.Equals(_chat.ActiveConversation?.Id, conversationId, StringComparison.Ordinal)) return;
        _paintWindow = _paintWindow > int.MaxValue / 2 ? int.MaxValue : _paintWindow * 2;
        _paintFromScratch = true;
        RenderMessages();
    }

    /// <summary>
    /// The parked transcript for <paramref name="conversationId"/>, if its rows still describe the conversation
    /// about to be drawn. One that does not is dropped here rather than left to be re-examined on every later
    /// visit: the test is one reference comparison per turn, and the rows behind it are the expensive thing to
    /// keep alive.
    /// </summary>
    private RenderedTranscript? TakeParked(string conversationId,
        List<(int Index, ChatTurn Turn, string? ToolName)> visible, string approvalStamp)
    {
        var parked = _parkedTranscripts.Find(candidate => candidate.ConversationId == conversationId);
        if (parked is null) return null;
        // Parked controls are reusable only when occurrence keys and rendered values still agree in order,
        // under the same decisions and thinking visibility. A disk reload must never inherit these controls.
        //
        // Identity first, value second, and the second half is not politeness: opening a session stamps
        // `ApprovalSeen` onto every pending card with `turn with { … }`, so a turn whose *content* is unchanged
        // comes back as a different object. Asking by identity alone threw away the rows of every session that
        // had ever raised a card — which in practice was every long one — and the log said so plainly: opens of a
        // 483-turn session read `drawn`, never `re-attached rows`.
        var same = parked.Turns.Count == visible.Count
                   && parked.ApprovalStamp == approvalStamp
                   && parked.ShowThinking == _chat.ShowThinking;
        for (var i = 0; same && i < parked.Turns.Count; i++)
        {
            var drawn = parked.Turns[i];
            var now = visible[i].Turn;
            same = parked.Keys[i] == _projection!.KeyAt(visible[i].Index)
                   && (ReferenceEquals(drawn, now) || drawn.Equals(now));
        }

        if (same) return parked;
        _parkedTranscripts.Remove(parked);
        return null;
    }

    /// <summary>
    /// Puts the transcript on screen aside — rows and all — so that coming back to it re-attaches instead of
    /// drawing again. Called on the way out of a session, before the flow is cleared for the next one; the rows
    /// stay alive detached, which is what a control that was only removed already is.
    ///
    /// <para>Nothing is parked while the flow holds no turns: rows are the whole of what this saves, and a
    /// transcript with none costs nothing to draw again.</para>
    ///
    /// <para>The two stamps arrive as arguments rather than being read here, and that is the whole of a bug this
    /// had: by the time this runs, <see cref="RenderMessages"/> has already moved both fields forward to the
    /// <i>incoming</i> session's values, so reading them filed the outgoing rows under the wrong decisions and
    /// every switch between two sessions whose cards differed lost its rows on the way back.</para>
    /// </summary>
    private void ParkCurrentTranscript(string approvalStamp, bool showThinking)
    {
        if (_renderedConversationId is not { Length: > 0 } id) return;
        if (_renderedTurns.Count == 0 || MessageFlow.Children.Count == 0) return;
        // A transcript somebody widened all the way to the top is a transcript with every row in it, and a row
        // holds its whole Markdown document, its editors and its decoded pictures. Past this, drawing it again is
        // cheaper than keeping it — and the window means the ordinary case never comes near the limit.
        if (MessageFlow.Children.Count > ParkedRowLimit) return;
        _parkedTranscripts.RemoveAll(parked => parked.ConversationId == id);
        var parked = new RenderedTranscript
        {
            ConversationId = id,
            ApprovalStamp = approvalStamp,
            ShowThinking = showThinking,
            OpenGroup = _openGroup,
            PaintedFrom = _paintedFrom,
        };
        parked.Rows.AddRange(MessageFlow.Children);
        parked.Turns.AddRange(_renderedTurns);
        parked.Keys.AddRange(_renderedKeys);
        parked.PendingMarkdown.AddRange(_pendingMarkdownRows);
        _parkedTranscripts.Insert(0, parked);
        while (_parkedTranscripts.Count > ParkedTranscriptDepth)
            _parkedTranscripts.RemoveAt(_parkedTranscripts.Count - 1);
    }

    /// <summary>Set while a promotion pass is queued for the end of the frame.</summary>
    private bool _markdownPassQueued;

    /// <summary>Queues one promotion pass for the end of the frame. Queued rather than run inline because both
    /// callers — a render, and a scroll — are already inside a frame that has to stay responsive.</summary>
    private void ScheduleMarkdownPromotion()
    {
        if (_markdownPassQueued || _pendingMarkdownRows.Count == 0) return;
        _markdownPassQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _markdownPassQueued = false;
                PromoteMarkdownInView();
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Renders the Markdown of the plain rows the reader has scrolled within reach of, and queues the next pass
    /// when it spent its quota.
    ///
    /// <para>Which rows: those whose bottom is at or below the top edge of the viewport and whose top is inside
    /// two screens below it. Rows <b>above</b> the top edge are deliberately left alone — drawing one properly
    /// changes its height, and a height that changes above the reading position pushes everything under it down,
    /// which is a transcript jumping under a hand that is scrolling it. Below the top edge the same growth only
    /// moves what has not been read yet. A row that is above the edge today enters the window the moment it is
    /// scrolled back to, so nothing is left permanently plain.</para>
    /// </summary>
    private void PromoteMarkdownInView()
    {
        if (_pendingMarkdownRows.Count == 0) return;
        var offset = MessageScroller.Offset.Y;
        var reach = MessageScroller.Viewport.Height;
        var ceiling = offset + (2 * reach);
        var promoted = 0;
        for (var i = 0; i < _pendingMarkdownRows.Count && promoted < MarkdownPromotionsPerPass;)
        {
            var pending = _pendingMarkdownRows[i];
            var position = pending.Parent.Children.IndexOf(pending.Row);
            // The row is gone: the piece it lived in was drawn again, or the flow was rebuilt around it. The
            // record goes with the control it named, which is what keeps this list from filling up with rows
            // nobody can reach.
            if (position < 0 || _chat.ActiveConversation is not { } current
                || current.Id != pending.ConversationId || _projection is null
                || !_projection.TryResolve(pending.TurnKey, out var sourceIndex))
            {
                _pendingMarkdownRows.RemoveAt(i);
                continue;
            }

            var top = pending.Row.TranslatePoint(new Point(0, 0), MessageFlow)?.Y;
            if (top is not { } y || !pending.Row.IsEffectivelyVisible
                || y + pending.Row.Bounds.Height < offset || y > ceiling)
            {
                i++;
                continue;
            }

            _pendingMarkdownRows.RemoveAt(i);
            // Drawn the ordinary way into a panel of its own and then moved into place: a turn has exactly one
            // way of being drawn, and a promotion that assembled a second one would be a second thing to keep
            // in step with the first.
            var staging = new StackPanel { Spacing = 8 };
            AppendRenderedTurn(pending.ConversationId, sourceIndex, current.Messages[sourceIndex], pending.ToolName,
                sourceIndex == current.Messages.Count - 1, markdown: true, target: staging, inGroup: pending.InGroup);
            if (staging.Children.Count != 1) continue;
            var promotedRow = staging.Children[0];
            staging.Children.RemoveAt(0);
            promotedRow.Classes.Remove(PlainTurnClass);
            pending.Parent.Children[position] = promotedRow;
            promoted++;
        }

        // A pass that spent its whole quota has more rows waiting in the same window; one that did not has
        // nothing left in reach and will be asked again by the next scroll.
        if (promoted == MarkdownPromotionsPerPass) ScheduleMarkdownPromotion();
    }

    /// <summary>
    /// Draws one turn into <paramref name="target"/> and answers with the row it built.
    ///
    /// <para>The row comes back because the caller that held its Markdown back has to be able to find it again:
    /// a plain row carries <see cref="PlainTurnClass"/> and is filed in <see cref="_pendingMarkdownRows"/>, and
    /// <see cref="PromoteMarkdownInView"/> replaces it in place — which needs the control, not just the turn.
    /// A Markdown row is drawn here or nowhere: the promotion pass re-enters this method with
    /// <c>markdown: true</c> rather than assembling a second, different way of drawing a turn.</para>
    /// </summary>
    private Control AppendRenderedTurn(
        string conversationId, int index, ChatTurn turn, string? toolName, bool isLast, bool markdown,
        Panel target, bool inGroup)
    {
        var fromUser = turn.Role == ChatRoles.User;
        var body = new StackPanel { Spacing = 8 };

        // Whose words these are is not optional information: a peer session's message would otherwise read as
        // something the user typed. The stored turn carries the source's id, so the title is looked up here — and
        // a session deleted since still gets a line rather than no label at all.
        if (turn.InjectedFrom is { Length: > 0 } peer)
        {
            var name = _chat.SessionTitleFor(peer);
            var origin = new TextBlock
            {
                Classes = { "muted", "peer-origin" },
                Text = name is { Length: > 0 } titled
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get("ChatPeerOriginFormat"), titled)
                    : HubStrings.Get("ChatPeerOriginGone"),
            };
            ToolTip.SetTip(origin, peer);
            body.Children.Add(origin);
        }

        // The machine half of a tool exchange is not prose. What came back is a JSON array, a directory listing,
        // or a compiler's stderr, and painting it in the assistant's own face makes the transcript read as though
        // the model had recited it — which is exactly what a person cannot un-see. One quiet line says what
        // arrived, trimmed to what a glance holds.
        // A plan is the other case where a row's text is a document rather than a reply, and it is suppressed the
        // same way the tool payload is. The card built below stands in for it and names it. Nothing was taken away
        // from the provider either: the body is filed in its own document and put back on the wire where the
        // request is assembled, and this row's copy action resolves it the same way — a decision about painting,
        // not about what was said.
        var isPlanTurn = turn.PlanApprovalState is { Length: > 0 };
        // A turn that only thought has no prose to paint, and an empty block is not an empty line: it carries the
        // body's own line height, so it used to leave a gap under the 「已思考 Ns」 head with nothing in it. The
        // thinking itself is what belongs there, and it is added below once the answer has been rendered.
        if (turn.Role == ChatRoles.Tool) body.Children.Add(ToolResultLine(toolName, turn));
        else if (!isPlanTurn && turn.Text.Length > 0)
            // Selectable, not a bare TextBlock: this is the person's own words, and words somebody wrote are words
            // they copy — a TextBlock cannot be selected at all, which made the one message they certainly care
            // about the only one on screen that could not be. It carries the same `turn-text` class, so its size,
            // ink and leading stay the ones the style already states for this row, and it is given the same
            // right-click menu a rendered answer carries, so selecting one's own message is as useful as
            // selecting the reply to it.
            body.Children.Add(OwnWordsBlock(turn.Text));

        // What the person attached is shown from the file Hub kept, not from anything this view remembers: the
        // transcript is the only copy of "this message had a picture in it", and a row rebuilt after a restart
        // has to look the same as the one that was sent. A frame the assistant captured itself stays out of the
        // flow — its approval card already says which window was grabbed, and this row is about what the user said.
        if (fromUser && turn.Images.Count > 0) body.Children.Add(BuildTurnPictures(conversationId, turn.Images));

        Control? approvalSurface = null;

        // A call that needed permission carries its own record in the transcript: one quiet line while the
        // decision is owed (the buttons live in the composer's decision host, not here), one quiet line once it
        // is not. A call that never needed asking gets nothing drawn here, which is why the approval state —
        // not the presence of a tool call — is what decides. A write is the exception: it changed a file
        // whether or not anybody was asked, and the line is where its undo lives.
        // The pending line must not disappear altogether: the incremental renderer invalidates on a stamp of
        // these states, and a scrolled-back reader still needs to see that a decision was owed at this turn.
        if (turn.ToolCallId is { Length: > 0 } callId)
        {
            if (turn.ApprovalState == ChatApprovalStates.Pending)
                approvalSurface = BuildApprovalPendingLine(turn);
            else if (turn.ApprovalState is { Length: > 0 } || turn.UndoName is { Length: > 0 })
                approvalSurface = BuildCallRecord(conversationId, callId, turn);
        }
        if (isPlanTurn) approvalSurface = BuildPlanCard(turn, index);

        // A function-call or tool-result turn gets no action bar: it is not a readable message, and acting on
        // half of a call/result pair orphans the other half (the pipeline sends them to the provider as one
        // exchange). `index` is the only thing that gates the bar, so nulling it here leaves stored indexes
        // untouched for every other turn.
        var actionableIndex = turn.ToolCallId is { Length: > 0 } ? (int?)null : index;
        // 「复制」 hands over what was said, and for a plan what was said is the document the card stands in for.
        // Resolved per row rather than per keystroke: a session holds a handful of plans, and this runs once for
        // each turn the transcript paints. A body no longer on disk copies as nothing, which beats copying the
        // card's title back as though it were the plan.
        var copyText = isPlanTurn ? _chat.PlanBody(turn, out _) ?? "" : turn.Text;
        var row = BuildMessageRow(fromUser, body, actionableIndex, turn.Role, copyText, isLast, turn.At);
        target.Children.Add(row);

        // User text is plain by nature, and a tool payload is now a quiet line rather than a bubble; only the
        // assistant's own words carry Markdown worth rendering — and a plan's words do not render here at all.
        // A prose turn whose Markdown was held back keeps the plain block added above, wears the mark that says
        // so, and is filed with what a later pass needs to draw it properly once a reader scrolls to it.
        if (!fromUser && !isPlanTurn && turn.Role != ChatRoles.Tool && turn.Text.Length > 0)
        {
            if (markdown)
            {
                MarkdownMessageRenderer.RenderInto(body, turn.Text, QuoteIntoComposer);
            }
            else
            {
                row.Classes.Add(PlainTurnClass);
                _pendingMarkdownRows.Add(new PendingMarkdownRow
                {
                    Parent = target,
                    Row = row,
                    ConversationId = conversationId,
                    TurnKey = _projection!.KeyAt(index),
                    ToolName = toolName,
                    IsLast = isLast,
                    InGroup = inGroup,
                });
            }
        }

        // The thinking sits above the answer, which is the order it happened in and the order every client of
        // this shape puts it — but it has to be <i>inserted</i>, not added, because the pass just above clears
        // the panel it renders into. Laid down before that call it would not sit under the answer, it would be
        // gone with everything else the panel held.
        if (!fromUser && !isPlanTurn && turn.Role != ChatRoles.Tool
            && BuildThoughtRowForTurn(conversationId, index, turn, inGroup) is { } reasoning)
            body.Children.Insert(0, reasoning);

        // The run's total, said once on the answer it belongs to. Always visible rather than hover-revealed: the
        // reader watched this number climb while the reply arrived, and making it hard to reach the moment it
        // stops would be exactly backwards. A plan's turn gets it too — the plan finishing IS the run finishing.
        if (!fromUser && turn.Role != ChatRoles.Tool && turn.RunElapsed is { } took)
            body.Children.Add(new TextBlock
            {
                Classes = { "run-elapsed" },
                Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("ChatRunElapsedFormat"), FormatDuration(took)),
            });

        // Added after the Markdown pass rather than before it, because <see cref="MarkdownMessageRenderer.RenderInto"/>
        // clears the panel it renders into: a line laid down first is not underneath the answer, it is gone. This is
        // also the order the person reads — the reply, then where it came from, then any record of a decision.
        if (!fromUser && ServerSearchLog.Read(turn.WebSearch) is { IsEmpty: false } searches)
            body.Children.Add(BuildSearchSources(searches));

        if (approvalSurface is not null) body.Children.Add(approvalSurface);
        return row;
    }

    /// <summary>The person's own words, as something they can select, copy and quote.</summary>
    private SelectableTextBlock OwnWordsBlock(string text)
    {
        var block = new SelectableTextBlock { Classes = { "turn-text" }, Text = text };
        // The same menu a rendered answer carries, read off the block instead of off a document: a plain turn is
        // one string, so its selection needs no extractor.
        SelectionMenu.Attach(block, () => block.SelectedText ?? "", QuoteIntoComposer);
        return block;
    }

    /// <summary>Puts a quoted block in the composer — where the right-click menu's 引用 hands a selection.
    ///
    /// <para>The quote is Markdown, one <c>&gt; </c> per line, because that is what the box is for: what arrives is
    /// something the person can answer underneath, and the fence is what tells the model those words are a quote
    /// rather than the start of their own sentence. It is inserted at the caret and takes nothing over — the draft
    /// around it stays where it was, and the caret lands after the quote, which is where the reply goes.</para>
    /// </summary>
    internal void QuoteIntoComposer(string text)
    {
        if (text.Length == 0) return;

        var quoted = string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => "> " + line));
        var draft = InputBox.Text ?? "";
        var caret = Math.Clamp(InputBox.CaretIndex, 0, draft.Length);
        var before = draft[..caret];
        var after = draft[caret..];
        // A quote is its own block: it opens on a fresh line, and what follows it gets a line of its own to be
        // written on rather than continuing the quoted paragraph.
        var inserted = (before.Length > 0 && !before.EndsWith('\n') ? "\n" : "")
                       + quoted
                       + (after.StartsWith('\n') ? "\n" : "\n\n");
        InputBox.Text = before + inserted + after;
        InputBox.CaretIndex = before.Length + inserted.Length;
        InputBox.Focus();
    }

    /// <summary>Whether a turn is machine work rather than something said: a tool result, a call, or a
    /// thinking-only answer. These are the turns a run folds into one group; prose and user turns break it.
    /// A thinking turn that also carries prose is prose — the answer is what was asked for, and folding it
    /// away would hide the reply.
    ///
    /// <para>A plan is prose whatever its text says, so it is excepted by name. Its body lives in its own document
    /// and its turn's text is legitimately empty, which is the shape this test would otherwise read as
    /// "thought and said nothing" — and the review card would go inside the collapsed 「Thought for Ns」 fold, on a
    /// model whose answers always carry thinking.</para></summary>
    private static bool IsActivityTurn(ChatTurn turn) => turn.Role switch
    {
        ChatRoles.Tool => true,
        ChatRoles.Assistant => turn.ToolCallId is { Length: > 0 }
                               || (turn.Reasoning is { Length: > 0 } && turn.Text.Length == 0
                                   && turn.PlanApprovalState is null),
        _ => false,
    };

    /// <summary>Whether this turn is a call the model asked for <i>while saying why</i>, and owns no surface of
    /// its own: an auto-allowed read or command, whose prose would otherwise be filed on the call turn and drawn by
    /// nobody. A call that owed a decision, or a write that left a copy, is excluded — it already paints its prose
    /// through its own row, inside the fold, and asserting that is <c>d7c3f9f</c>'s job. Whitespace does not count:
    /// a blank sentence must not buy a blank row, which is the gap this whole branch used to be argued on.
    ///
    /// <para>These are what the flat pieces of the transcript are made of — the sentence goes in the flow and its
    /// one action sits under it, because the words are the thread the person is reading and hiding them behind a
    /// collapsed head is indistinguishable from the model having said nothing.</para></summary>
    private static bool IsNarratedCall(ChatTurn turn) => turn.Role == ChatRoles.Assistant
                                                         && turn.ToolCallId is { Length: > 0 }
                                                         && !string.IsNullOrWhiteSpace(turn.Text)
                                                         && turn.ApprovalState is null && turn.UndoName is null;

    /// <summary>
    /// <summary>
    /// The shell of one collapsible activity group: its head, the panel its rows live in, and the stretch of the
    /// transcript it stands for. Keeping those together is what lets a machine run that is still arriving grow the
    /// group it opened instead of replacing it — the head is the control a person may well have expanded to read,
    /// and a rebuilt group answers them with a collapsed one.
    /// </summary>
    private sealed class ActivityGroupView
    {
        public required string ConversationId { get; init; }

        /// <summary>Where the run begins in the visible turns, and where it has reached so far. A next turn whose
        /// index is exactly <see cref="EndVisible"/> continues this run; anything else is a different piece of
        /// work and opens its own fold.</summary>
        public required int FirstVisible { get; init; }

        public int EndVisible { get; set; }

        public required List<(int Index, ChatTurn Turn, string? ToolName)> Turns { get; set; }

        /// <summary>Whether this piece is a collapsible fold. A flat piece (<see cref="IsNarratedCall"/> opened
        /// it) has no head and nothing to expand: it is the sentence and the one action under it.</summary>
        public required bool Folded { get; init; }

        public required Control Group { get; init; }

        /// <summary>The expandable head, or null for a flat piece. Only a fold has a sentence to keep current.</summary>
        public ToggleButton? Head { get; init; }

        public required StackPanel Inner { get; init; }
    }

    /// <summary>Lays down an empty piece over the first stretch of a machine run and puts it at the end of the
    /// flow. Its rows and its sentence come from <see cref="DeriveActivityGroup"/>. A folded piece is a head over a
    /// body the person opens; a flat one is a bare column of rows — the same drawing target, minus the chrome —
    /// because a call that came with its own sentence has nothing here worth hiding.</summary>
    private ActivityGroupView CreateActivityGroup(string conversationId, int firstVisible,
        List<(int Index, ChatTurn Turn, string? ToolName)> turns, bool folded)
    {
        if (!folded)
        {
            // Hugging its own rows the way the fold's body does, and taking the flow's spacing only on the
            // outside: the sentence and the action under it are one exchange, not two entries in the transcript.
            var plain = new StackPanel
            {
                Classes = { "activity-plain" },
                Spacing = 2,
            };
            MessageFlow.Children.Add(plain);
            return new ActivityGroupView
            {
                ConversationId = conversationId,
                FirstVisible = firstVisible,
                EndVisible = firstVisible + turns.Count,
                Turns = turns,
                Folded = false,
                Group = plain,
                Inner = plain,
            };
        }

        var head = new ToggleButton { Classes = { "activity-group-head" } };
        var inner = new StackPanel { Spacing = 2 };
        var body = new StackPanel { Name = "ActivityGroupBody", Spacing = 0, IsVisible = false, Margin = new Thickness(0, 2, 0, 2) };
        body.Children.Add(inner);
        // The turn a run starts on is the group's identity: it does not move while the run grows, and a rebuild
        // slices the same run out of the same turn — so keying the fold to it is what lets an opened head come
        // back open.
        var foldKey = RowKey(turns[0].Index, "group");
        // Driven off IsCheckedChanged rather than Click: a real click flips IsChecked (which fires this), and so
        // does a check that sets IsChecked directly — a raised Click never reaches a ToggleButton's OnClick, so
        // wiring the fold to Click would leave it untestable and half-broken. Wired before the remembered state is
        // applied, because the panel's visibility has to keep exactly one source.
        head.IsCheckedChanged += (_, _) =>
        {
            var expanded = head.IsChecked == true;
            body.IsVisible = expanded;
            RememberFold(_expandedGroups, conversationId, foldKey, expanded);
            // A fold the person just opened is the one place where a plain row becomes readable without any
            // scrolling at all, and the rows inside it were drawn plain while it was shut.
            if (expanded) ScheduleMarkdownPromotion();
        };
        head.IsChecked = IsFoldOpen(_expandedGroups, conversationId, foldKey);

        var group = new Border { Classes = { "activity-group" }, Child = new StackPanel { Spacing = 2, Children = { head, body } } };
        MessageFlow.Children.Add(group);
        return new ActivityGroupView
        {
            ConversationId = conversationId,
            FirstVisible = firstVisible,
            EndVisible = firstVisible + turns.Count,
            Turns = turns,
            Folded = true,
            Group = group,
            Head = head,
            Inner = inner,
        };
    }

    /// <summary>
    /// Redraws one group from the turns it holds: every row inside it, then the full-width sentence over them.
    /// The head names the work (the first action, and how many followed) rather than a bare count, so a tools-only
    /// turn is a line of content and not a lonely pill. Collapsed by default: the charter hides secondary things.
    ///
    /// Inside, each tool exchange is folded into a single row. A read/search/list/find/command becomes one
    /// descriptive <c>activity-row</c> built from its call and result (the raw payload moves to the tooltip), so
    /// the AI lane reads as actions rather than a stack of result fragments. A write keeps its existing record
    /// line with the undo — that line is the write's one exit and its approval trace, and it is asserted
    /// elsewhere — so the group is a container that routes each turn to the row shape that fits it, not a
    /// re-implementation of the per-turn surfaces.
    ///
    /// The rows are re-derived rather than appended because a result is filed <i>beside</i> the call it answers
    /// (<see cref="Conversation.AppendFunctionResult"/>): the turn that just arrived is not always the new thing at
    /// the end, and rebuilding the slice from the transcript as it now stands is the only way the rows come out in
    /// the order the conversation holds. Only this group's own children move, so an opened head and the reading
    /// position are left alone — which is the whole point of extending rather than rebuilding.
    /// </summary>
    private void DeriveActivityGroup(ActivityGroupView view, string conversationId, int lastIndex,
        int visibleCount)
    {
        var turns = view.Turns;
        // Pair each call with the result that answered it, so one row can say what was done and how it went.
        var calls = new Dictionary<string, ChatTurn>(StringComparer.Ordinal);
        foreach (var (_, turn, _) in turns)
        {
            if (turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 } id) calls[id] = turn;
        }

        var inner = view.Inner;
        // The rows inside this piece are about to be thrown away and drawn again, so the promotions that named
        // them go first: a record pointing at a control nobody can reach is a promotion that paints into a panel
        // that is no longer in the flow.
        _pendingMarkdownRows.RemoveAll(pending => ReferenceEquals(pending.Parent, inner));
        inner.Children.Clear();
        foreach (var (index, turn, toolName) in turns)
        {
            // A call that owed a decision, or a write that left a copy, keeps its own surface (signpost / record
            // with undo) — those are functional and asserted, so route them through the ordinary turn renderer.
            // The prose this call rode in on belongs to that surface too, so it is painted through the Markdown
            // layer rather than left as raw source text — and a call that only asked for a read or a command gets
            // its sentence painted the same way, because there it was, until now, drawn by nobody.
            if (turn.Role == ChatRoles.Assistant && turn.ToolCallId is { Length: > 0 } callId)
            {
                if (turn.ApprovalState is { Length: > 0 } || turn.UndoName is { Length: > 0 } || IsNarratedCall(turn))
                    AppendRenderedTurn(conversationId, index, turn, toolName, isLast: index == lastIndex,
                        markdown: index >= visibleCount - EagerMarkdownLimit, target: inner, inGroup: true);
                else if (BuildThoughtRowForTurn(conversationId, index, turn, inGroup: true) is { } silentThought)
                    // A silent call renders no row of its own: its action row is drawn from the result below, so
                    // emitting one here would only be a gap. Thinking is the exception — it is content, and until
                    // now it was filed on a turn that painted nothing at all, which left a whole round of
                    // reasoning on the record and nowhere on screen.
                    inner.Children.Add(silentThought);
                continue;
            }

            // A tool result folds into its call's row — except a write, whose record line above already speaks
            // for the exchange.
            if (turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } answered)
            {
                var call = calls.TryGetValue(answered, out var paired) ? paired : null;
                if (call?.ToolName == ChatChanges.FileWriteTool) continue;
                var exchange = BuildActivityRowShell(conversationId, index, call, turn);
                // The pairing this row was drawn from, kept as data: a result's name has two possible sources
                // (the conversation-wide first call, the piece-local last call) and only the row knows which one
                // it used. The shadow comparison reads it instead of guessing from a localised label.
                exchange.Tag = call?.ToolName ?? "";
                inner.Children.Add(exchange);
                continue;
            }

            // A thinking-only turn <i>is</i> the row, not a row holding a fold. Routing it through the ordinary
            // turn renderer painted an empty prose block and then tucked the thinking inside a second collapsed
            // toggle — two clicks to reach one thing, beneath a group head that had already said 已思考 once.
            if (BuildThoughtRowForTurn(conversationId, index, turn, inGroup: true) is { } thoughtRow)
                inner.Children.Add(thoughtRow);
        }

        // A flat piece has no head to caption: its sentence is the prose row already painted above it.
        if (view.Head is { } head) head.Content = BuildActivityGroupHead(turns, calls.Count);
    }

    /// <summary>
    /// The head's row: a chevron, a sentence naming the first action (and how many followed), and an aggregate
    /// <c>+N −M</c> that trails that sentence. Naming the work rather than counting it is what keeps a tools-only
    /// turn from reading as a lonely pill stranded at the left edge; trailing the count rather than pinning it to
    /// the right edge is what keeps the head one line the eye finishes in order, rather than a second column whose
    /// left edge moves with every label width.
    /// </summary>
    private Control BuildActivityGroupHead(List<(int Index, ChatTurn Turn, string? ToolName)> turns, int callCount)
    {
        // The head toggle carries the HubExpanderHeader theme, which supplies the same left `>`/`v` arrow the
        // sidebar's workspace groups use — so no chevron is built here, and the grid only lays out the sentence and
        // the aggregate diff chip. The slack goes to the last column, after the chip.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var (text, added, removed) = GroupSummary(turns, callCount);
        // The sentence embeds the first call's description, so its length is not bounded — and an Auto column is
        // measured against the whole row, where trimming could never bite. The cap is what makes the ellipsis
        // reachable, the same way tool-result-line carries one.
        var label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        if (added + removed > 0)
        {
            var chip = BuildDiffChip(added, removed);
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
        }

        return grid;
    }

    private static (string Text, int Added, int Removed) GroupSummary(
        List<(int Index, ChatTurn Turn, string? ToolName)> turns, int callCount)
    {
        if (callCount == 0)
        {
            var first = turns[0].Turn.At;
            var last = turns[^1].Turn.At;
            return (string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityGroupThoughtFormat"), FormatDuration(last - first)), 0, 0);
        }

        var calls = turns
            .Where(t => t.Turn.Role == ChatRoles.Assistant && t.Turn.ToolCallId is { Length: > 0 })
            .ToList();
        // One action gets named, because with one action the name is the whole answer and a count would hide it
        // behind a click. Two or more get counted, because the first of a hundred and twenty-seven is a sample
        // rather than a summary — naming it tells the reader this run was about <c>workspace_map</c> when it was
        // about something else entirely. The names are not lost: they are the rows, and the row order is the
        // order the model asked for them.
        var text = calls.Count > 1
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityGroupToolCountFormat"), calls.Count)
            : DescribeToolCall(calls[0].Turn);
        var (added, removed) = AggregateDiff(calls);
        return (text, added, removed);
    }

    /// <summary>The aggregate line count of the run's edits. It reads <see cref="ChatChanges.LinesOf"/> per call
    /// rather than the frozen previews alone, because a write the mode allowed without asking froze nothing and yet
    /// still moved the file — totalling only the previews made this head go silent for exactly the runs that never
    /// interrupted anybody. Nothing is opened to work it out: reading the disk to total a header would be the whole
    /// list paying for one line. When no call moved anything, the header simply carries no chip.</summary>
    private static (int Added, int Removed) AggregateDiff(
        List<(int Index, ChatTurn Turn, string? ToolName)> calls)
    {
        var added = 0;
        var removed = 0;
        foreach (var call in calls)
        {
            var lines = ChatChanges.LinesOf(call.Turn);
            added += lines.Added;
            removed += lines.Removed;
        }
        return (added, removed);
    }

    /// <summary>
    /// The run's aggregate line count, and the way into the pane that lists what produced it.
    ///
    /// It stays the same <c>Border.file-chip</c> wrapped in a bare transparent Button rather than becoming a
    /// visible button: the count is the thing being read, and a plate on the head of a folded group is exactly
    /// the chrome the charter has no room for. Wrapping it in a button is also what keeps the group open — the
    /// button takes the pointer press, so the head's ToggleButton never sees it and the fold the reader chose
    /// does not collapse under the click that was meant to open a diff.
    /// </summary>
    private Control BuildDiffChip(int added, int removed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (added > 0) row.Children.Add(new TextBlock
        {
            Classes = { "file-chip-add" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorAddedFormat"), added),
        });
        if (removed > 0) row.Children.Add(new TextBlock
        {
            Classes = { "file-chip-del" },
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorRemovedFormat"), removed),
        });
        var chip = new Button
        {
            Classes = { "file-chip-action" },
            Tag = "InspectorOpenChanges",
            Content = new Border { Classes = { "file-chip", "inline-chip" }, Child = row },
            VerticalAlignment = VerticalAlignment.Center,
            // The head's slack now sits after this column, so the button has to hold still at its own width: the
            // expander template centers its content, and a stretched button would float the count into the middle
            // of the empty row.
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(chip, HubStrings.Get("InspectorOpenChanges"));
        chip.Click += (_, _) => OpenInspector?.Invoke("changes", -1);
        return chip;
    }

    /// <summary>How tall a thinking body may get before it scrolls. The bound is the existing detail panel's, not
    /// a new number: nothing in the pipeline caps a chain of thought, and an activity group re-derives every row
    /// of itself on each arriving turn, so an unbounded block would be re-measured once per tool call for the
    /// length of a run.</summary>
    private const double ThoughtBodyMaxHeight = 200;

    /// <summary>
    /// The model's thinking as one row: a head naming it, a bounded body holding the words.
    ///
    /// <para>One builder serves the streaming bubble and the stored turn, because they are the same thing seen at
    /// two moments. Two controls is what let a reader watch forty seconds of reasoning and then land on a
    /// different widget that had forgotten it — and a card that hides when the answer starts is not the same thing
    /// as a row that folds.</para>
    ///
    /// <para>It is a row rather than a card because the transcript already has a vocabulary for machine work you
    /// can peek at: the same chevron, the same hover, the same remembered fold. Inventing a second idiom for the
    /// second kind of machine work is how you end up with a fold inside a fold.</para>
    /// </summary>
    private sealed class ThoughtRowView
    {
        internal required Border Shell { get; init; }
        internal required ToggleButton Head { get; init; }
        internal required Border Body { get; init; }
        internal required TextBlock Text { get; init; }
        internal required ScrollViewer Scroll { get; init; }
        internal required TextBlock Label { get; init; }

        /// <summary>Open or closed. The head's checkbox mirrors this rather than owning it, because a press and
        /// the answer's arrival both arrive as the same property change on that checkbox.</summary>
        internal bool Expanded { get; set; }

        /// <summary>Set only around a fold the code performs. From the first press onward nothing automatic
        /// touches the row: a reader who chose a position was using it, and the answer turning up is not a reason
        /// to take it back.</summary>
        internal bool TouchedByUser { get; set; }

        internal bool Syncing { get; set; }

        internal void SetExpanded(bool expanded)
        {
            if (Expanded == expanded) return;
            Expanded = expanded;
            Body.IsVisible = expanded;
            Syncing = true;
            try { Head.IsChecked = expanded; }
            finally { Syncing = false; }
        }
    }

    /// <summary>Lays one thinking row out. <paramref name="onUserToggle"/> fires only for a fold the reader made,
    /// which is what lets a stored row remember its fold across the group's rebuilds while the live row — which
    /// has nothing to remember it — stays out of that memory entirely.</summary>
    private ThoughtRowView BuildThoughtRow(string label, Action<bool>? onUserToggle)
    {
        var text = new TextBlock { Classes = { "turn-reasoning" } };
        var scroll = BoundedDetailScroll(text, ThoughtBodyMaxHeight);
        var body = new Border { Classes = { "activity-detail" }, IsVisible = false, Child = scroll };

        var grid = new Grid
        {
            Classes = { "activity-row", "thought" },
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-status" },
            Data = ThemeGeometry("Hub.Icon.Assistant"),
        });
        // The same cell as the mark, so the hover swap never slides the label sideways. The tool rows are built
        // this way; matching them is the point of the exercise, or the column reads as two different lists.
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-row-chevron" },
            Data = ThemeGeometry("Hub.Icon.ChevronRight"),
        });
        var labelText = new TextBlock { Classes = { "activity-row-text" }, Text = label };
        Grid.SetColumn(labelText, 1);
        grid.Children.Add(labelText);

        var head = new ToggleButton { Classes = { "activity-row-toggle" }, Content = grid };
        var shell = new Border { Classes = { "activity-row-shell" } };
        var row = new ThoughtRowView
        {
            Text = text, Scroll = scroll, Body = body, Head = head, Label = labelText, Shell = shell,
        };
        shell.Child = new StackPanel { Spacing = 0, Children = { head, body } };

        // IsChecked, never Click — a raised Click does not flip a ToggleButton, so wiring it would leave the fold
        // both half-broken and unreadable to a check. Same reason the row shell and the group head are wired so.
        head.IsCheckedChanged += (_, _) =>
        {
            if (row.Syncing) return;
            row.TouchedByUser = true;
            var want = head.IsChecked == true;
            if (want == row.Expanded) return;
            row.Expanded = want;
            body.IsVisible = want;
            onUserToggle?.Invoke(want);
        };
        return row;
    }

    /// <summary>The head's sentence. The duration is said only when nothing above the row has already said it:
    /// inside an activity group whose own head reads 已思考 3s, a row repeating the number is one fact told twice.
    /// Out of a group — and in the live bubble, where there is no group yet — this row is the only place the
    /// number exists, so it carries it.</summary>
    private static string ThoughtLabel(TimeSpan? elapsed, bool inGroup)
        => !inGroup && elapsed is { } span
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityGroupThoughtFormat"), FormatDuration(span))
            : HubStrings.Get("ActivityRowThought");

    /// <summary>The stored form of the row: this turn's thinking, folded as the reader left it.</summary>
    private Control? BuildThoughtRowForTurn(string conversationId, int index, ChatTurn turn, bool inGroup)
    {
        if (!_chat.ShowThinking || turn.Reasoning is not { Length: > 0 } thought) return null;

        // The thought facet belongs to this occurrence, so a rebuild or an inserted result cannot hand its
        // fold state to the turn that happens to occupy the old source index.
        var foldKey = RowKey(index, "thought");
        var row = BuildThoughtRow(
            ThoughtLabel(turn.ReasoningElapsed, inGroup),
            expanded => RememberFold(_expandedRows, conversationId, foldKey, expanded));
        row.Text.Text = thought;
        row.SetExpanded(IsFoldOpen(_expandedRows, conversationId, foldKey));
        return row.Shell;
    }

    /// <summary>One folded tool exchange wrapped in its own expander. The collapsed line names the action; clicking
    /// it reveals the whole exchange — the arguments the call was made with and the payload that came back — in a
    /// detail panel that grows downward in place. That panel is the operation's only readable surface: a hover is
    /// not one, because a trackpad cannot reach it, a check cannot read it, and a session transcript the model was
    /// handed is far too long to fit in one anyway.</summary>
    private Control BuildActivityRowShell(string conversationId, int index, ChatTurn? call, ChatTurn result)
    {
        var toggle = new ToggleButton { Classes = { "activity-row-toggle" }, Content = BuildActivityRow(call, result) };

        var detail = BuildActivityRowDetail(call, result);
        // Each landed result owns its fold, including orphan results and repeated results with the same call id.
        var foldKey = RowKey(index, "exchange");
        // Transfer an opened live command to its first landed result, once. Other results own their own folds.
        if (call is not null && _chat.ActiveConversation is { } conversation)
        {
            var callIndex = conversation.Messages.FindIndex(turn => ReferenceEquals(turn, call));
            if (callIndex >= 0)
            {
                var runningKey = RowKey(callIndex, "command");
                if (IsFoldOpen(_expandedRows, conversationId, runningKey))
                {
                    RememberFold(_expandedRows, conversationId, foldKey, true);
                    RememberFold(_expandedRows, conversationId, runningKey, false);
                }
            }
        }

        // The fold is driven off IsChecked, never Click: a raised Click does not flip a ToggleButton, so wiring
        // Click would leave this both untestable and half-broken — the same reason the group head is wired this way.
        toggle.IsCheckedChanged += (_, _) =>
        {
            var expanded = toggle.IsChecked == true;
            detail.IsVisible = expanded;
            RememberFold(_expandedRows, conversationId, foldKey, expanded);
        };
        toggle.IsChecked = IsFoldOpen(_expandedRows, conversationId, foldKey);

        return new Border
        {
            Classes = { "activity-row-shell" },
            Child = new StackPanel { Spacing = 0, Children = { toggle, detail } },
        };
    }

    // ── the command card: the call that is working right now ──
    // A transcript used to say nothing at all until a call came back: the row it grows is drawn from a result, so
    // the whole of a ten-minute build was a status line in the live bubble and nothing a person could read. This
    // card is that missing stretch — the call itself, the directory it runs in, the state, and the shell's own
    // lines — and it is an ephemeral tail like the live bubble beside it, not a transcript row: it exists only
    // while a command is in flight and it is drawn in the same shape and classes as the row the finished exchange
    // gets, so what the person watched and what the transcript keeps are one surface in two tenses.

    /// <summary>The command card at the foot of the reply, while one is printing. One at a time because that is what
    /// the tier table allows: a command changes state, so a second one waits for the first.
    ///
    /// <para>Its panel is shut until somebody opens it, and the <c>_expandedRows</c> memory it reads and writes is
    /// transferred to the first finished result's occurrence key. That is what makes the two
    /// tenses one surface — a person who opened the output while the build ran finds it open in the transcript
    /// afterwards, and a person who left it alone gets the same one line the row would have drawn anyway.</para>
    /// </summary>
    private RunningCommandRow? _runningCommand;

    /// <summary>One command card, and the pieces a repaint has to reach without rebuilding it.</summary>
    private sealed class RunningCommandRow
    {
        public required string ConversationId { get; init; }
        public required string CallId { get; init; }
        public required Border Shell { get; init; }
        public required ToggleButton Toggle { get; init; }
        public required Border Detail { get; init; }
        public required TextBlock State { get; init; }
        public required TextBlock Elapsed { get; init; }
        public required TextBlock Output { get; init; }
        public required ScrollViewer OutputScroll { get; init; }
        public required TextBlock Dropped { get; init; }

        /// <summary>The output text last written, so a refresh that finds the same lines does not reassign a
        /// TextBlock — which would re-measure a box the person may be reading.</summary>
        public string Painted { get; set; } = "";

        /// <summary>Whether the output box follows its newest line. Its own copy of the transcript's stick rule,
        /// for the same reason the thinking box has one. Nothing is written into a shut panel, so this only starts
        /// mattering the moment somebody opens it — and from then on it keeps the box where they left it.</summary>
        public bool Stick { get; set; } = true;
    }

    /// <summary>Puts the command card at the foot of the reply, refreshes the one already there, or takes it away.
    /// Called wherever the stored rows are laid down, because they are what it has to sit under.</summary>
    private void SyncRunningCommandRow(string? conversationId, ConversationRun? run)
    {
        if (PendingCommandCall(conversationId, run) is not { } call)
        {
            ForgetRunningCommand();
            return;
        }

        var callId = call.ToolCallId!;
        if (_runningCommand is not { } card || card.ConversationId != conversationId
            || !string.Equals(card.CallId, callId, StringComparison.Ordinal))
        {
            ForgetRunningCommand();
            card = BuildRunningCommandRow(conversationId!, call);
            _runningCommand = card;
        }

        RefreshRunningCommand(card, run);
        MessageFlow.Children.Add(card.Shell);
    }

    /// <summary>Drops the card. What the person chose about its panel needs no work here: that decision lives in the
    /// fold memory transferred to the finished result, so an output opened while the command printed is
    /// already open in the transcript afterwards — and one left shut stays the one line it always was.</summary>
    private void ForgetRunningCommand()
    {
        if (_runningCommand is not { } card) return;
        _runningCommand = null;
        MessageFlow.Children.Remove(card.Shell);
    }

    /// <summary>The command this conversation is running right now, or null.
    ///
    /// <para>Three facts have to agree before a card is drawn, and each of them is a lie avoided. The call must have
    /// no result yet, or the finished row is the surface and this one would duplicate it. It must not be waiting for
    /// a decision: an approval card is already asking, and "running" over it would say a person's click had already
    /// happened. And the run must actually own it — a stopped run or a restart leaves unanswered calls behind, and a
    /// card claiming a command is printing for a run that is gone would outlive the process it named.</para>
    /// </summary>
    private ChatTurn? PendingCommandCall(string? conversationId, ConversationRun? run)
    {
        if (conversationId is null || run is null) return null;
        var conversation = _chat.ActiveConversation;
        if (conversation is null || !string.Equals(conversation.Id, conversationId, StringComparison.Ordinal))
            return null;

        var messages = conversation.Messages;
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in messages)
        {
            if (turn.Role == ChatRoles.Tool && turn.ToolCallId is { Length: > 0 } id) answered.Add(id);
        }

        // Newest first, and an unanswered call the run is actually inside wins over a newer one that is only
        // recorded: a response can ask for two commands, both brackets open at the same gate, and the card has to
        // follow the one printing rather than the one the model happened to name last.
        ChatTurn? newest = null;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var turn = messages[index];
            if (turn.Role != ChatRoles.Assistant || turn.ToolCallId is not { Length: > 0 } callId) continue;
            if (!string.Equals(turn.ToolName, WorkspaceTools.RunCommandTool, StringComparison.Ordinal)) continue;
            if (answered.Contains(callId)) continue;
            if (turn.ApprovalState == ChatApprovalStates.Pending) return null;
            if (run.IsCallRunning(callId)) return turn;
            newest ??= turn;
        }

        // Nothing has started yet: the card is still owed, because the call is filed and the run is between a
        // request and its first tool. Outside a run there is nothing to say, and that is the answer that keeps a
        // stopped session's unanswered call from claiming a command is printing.
        return newest is not null && run.IsStreaming ? newest : null;
    }

    /// <summary>The card itself: the same row a finished exchange gets, with the state and the clock where that row
    /// keeps its chips, and the output underneath in the box the finished row's detail uses.
    /// </summary>
    private RunningCommandRow BuildRunningCommandRow(string conversationId, ChatTurn call)
    {
        var command = ArgField(call.ToolArguments, "command", "cmd") ?? "";
        var grid = new Grid
        {
            Classes = { "activity-row", "running" },
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
        };
        // The status slot holds the mark, and the arrow trades places with it on hover — the same two glyphs in one
        // cell the finished row uses, so nothing moves sideways when this row becomes that one. The mark is a
        // gap-topped ring rather than a tick or a cross: a call in flight has no verdict yet, and either of the two
        // would be a claim about how it ends. It is Hub.Icon.InProgress and not Hub.Icon.Refresh for the same
        // reason: the arrowed arc is the regenerate button's face in this same panel, and a row nobody can press
        // must not wear it.
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-status" },
            Data = ThemeGeometry("Hub.Icon.InProgress"),
        });
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-row-chevron" },
            Data = ThemeGeometry("Hub.Icon.ChevronRight"),
        });

        var action = new TextBlock { Classes = { "activity-row-text" }, Text = DescribeToolCall(call) };
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        if (!string.IsNullOrWhiteSpace(command))
        {
            var oneLine = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var chip = new Border
            {
                Classes = { "file-chip", "inline-chip" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = oneLine.Length > CommandPreviewCharacters
                        ? oneLine[..CommandPreviewCharacters] + "…"
                        : oneLine,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);
        }

        var state = new TextBlock { Classes = { "activity-row-meta" } };
        Grid.SetColumn(state, 3);
        grid.Children.Add(state);

        var elapsed = new TextBlock { Classes = { "activity-row-meta" } };
        Grid.SetColumn(elapsed, 4);
        grid.Children.Add(elapsed);

        // The detail: where the command runs, what it was asked with, and what it has said so far. The workspace is
        // the session's own root — the tool runs in it, and naming it here is what makes a command's effect on a
        // directory legible before it prints anything.
        var body = new StackPanel { Orientation = Orientation.Vertical };
        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailWorkspace"),
        });
        body.Children.Add(BoundedDetailScroll(
            new TextBlock { Classes = { "activity-detail-body" }, Text = _chat.ActiveConversation?.WorkspaceRoot ?? "" },
            60));
        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailInput"),
        });
        body.Children.Add(BoundedDetailScroll(FormatToolArguments(call), 120));
        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailCommandOutput"),
        });
        var output = new TextBlock { Classes = { "activity-detail-body" } };
        var outputScroll = BoundedDetailScroll(output, 180);
        body.Children.Add(outputScroll);
        var dropped = new TextBlock
        {
            Classes = { "activity-detail-label" },
            IsVisible = false,
        };
        body.Children.Add(dropped);

        var detail = new Border
        {
            Classes = { "activity-detail" },
            Child = body,
        };

        var toggle = new ToggleButton { Classes = { "activity-row-toggle" }, Content = grid };
        var shell = new Border
        {
            Classes = { "activity-row-shell" },
            Child = new StackPanel { Spacing = 0, Children = { toggle, detail } },
        };

        var callId = call.ToolCallId!;
        var foldKey = RowKey(_chat.ActiveConversation!.Messages.FindIndex(turn => ReferenceEquals(turn, call)), "command");
        // Shut by default, like every other activity row: the head says what is running and where, and the output is
        // there for whoever wants it. Opening it is the person's call, not the card's — a build that pushed its log
        // under the conversation as soon as it started was the card deciding for them.
        var expanded = IsFoldOpen(_expandedRows, conversationId, foldKey);
        detail.IsVisible = expanded;
        toggle.IsChecked = expanded;
        // Driven off IsChecked, never Click, exactly as the finished row is: a raised Click does not flip a
        // ToggleButton, so a check could not drive it and half the wiring would be dead.
        toggle.IsCheckedChanged += (_, _) =>
        {
            var open = toggle.IsChecked == true;
            detail.IsVisible = open;
            // The first landed result consumes this command occurrence's choice when its row is built.
            RememberFold(_expandedRows, conversationId, foldKey, open);
        };

        var card = new RunningCommandRow
        {
            ConversationId = conversationId,
            CallId = callId,
            Shell = shell,
            Toggle = toggle,
            Detail = detail,
            State = state,
            Elapsed = elapsed,
            Output = output,
            OutputScroll = outputScroll,
            Dropped = dropped,
        };
        // Wired after the card exists, because what it records is a field of the card: a command that keeps printing
        // must not pull the box away from a person who scrolled up to read what went past.
        outputScroll.ScrollChanged += (_, _) => card.Stick =
            outputScroll.Offset.Y + outputScroll.Viewport.Height >= outputScroll.Extent.Height - StickEpsilon;
        return card;
    }

    /// <summary>Redraws one card from the run: the state, the clock, and the lines printed so far.
    ///
    /// <para>Refreshed in place rather than rebuilt because a build prints faster than a transcript can be laid
    /// down: the row, the open panel and the reading position all survive a line, and only two TextBlocks change.
    /// </para></summary>
    private void RefreshRunningCommand(RunningCommandRow card, ConversationRun? run)
    {
        var running = run?.IsCallRunning(card.CallId) == true;
        card.State.Text = HubStrings.Get(running ? "ActivityRowCommandRunning" : "ActivityRowCommandQueued");
        card.Elapsed.Text = running && run!.ToolElapsed(card.CallId) is { } spent ? FormatDuration(spent) : "";
        card.Elapsed.IsVisible = card.Elapsed.Text.Length > 0;

        IReadOnlyList<string> lines = [];
        var dropped = 0;
        if (run is not null) (lines, dropped) = run.CommandOutputOf(card.CallId);
        var text = lines.Count == 0
            ? HubStrings.Get("ActivityDetailCommandSilent")
            : string.Join("\n", lines);
        if (!string.Equals(text, card.Painted, StringComparison.Ordinal))
        {
            card.Painted = text;
            card.Output.Text = text;
            // The box follows the newest line only while the reader has left it at the bottom — a command whose
            // output pushed itself up under somebody reading the top is the one thing this box must not do. A shut
            // panel has nothing to follow and queues no work: the text still lands, so the first click shows
            // everything printed so far in one frame.
            if (card.Stick && card.Detail.IsVisible) FollowNewestLine(card.OutputScroll);
        }

        card.Dropped.Text = dropped > 0
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ActivityDetailCommandDroppedFormat"), dropped)
            : "";
        card.Dropped.IsVisible = dropped > 0;
    }

    private static Border BuildActivityRowDetail(ChatTurn? call, ChatTurn result)
    {
        var body = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailInput"),
        });
        body.Children.Add(BoundedDetailScroll(FormatToolArguments(call), 160));

        body.Children.Add(new TextBlock
        {
            Classes = { "activity-detail-label" },
            Text = HubStrings.Get("ActivityDetailOutput"),
        });
        body.Children.Add(BoundedDetailScroll(FormatToolResult(result), 200));

        return new Border
        {
            Classes = { "activity-detail" },
            IsVisible = false,
            Child = body,
        };
    }

    /// <summary>One half of the exchange in a scroll window of its own. The bound is the point: a compiler's whole
    /// stderr or a peer session's transcript must not stretch the transcript, and clipping it silently would strand
    /// the part that matters — so the box holds still and its content scrolls inside it. Horizontal scrolling stays
    /// off, because the column is where the wrap happens.</summary>
    private static ScrollViewer BoundedDetailScroll(string text, double maxHeight)
        => BoundedDetailScroll(new TextBlock { Classes = { "activity-detail-body" }, Text = text }, maxHeight);

    /// <summary>The same window for content that is not a tool payload — a model's thinking, which wants the
    /// body's own face rather than the monospace the exchange is drawn in.</summary>
    private static ScrollViewer BoundedDetailScroll(Control content, double maxHeight) => new()
    {
        Content = content,
        MaxHeight = maxHeight,
        ClipToBounds = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>The call's arguments as a small readable block: each top-level JSON field on its own
    /// <c>key: value</c> line, strings unquoted and everything else verbatim. This is where a long command shows in
    /// full — the collapsed row's chip truncates for a glance, but the detail does not. Unparseable arguments are
    /// shown as their raw text rather than dropped.</summary>
    private static string FormatToolArguments(ChatTurn? call)
    {
        var args = call?.ToolArguments;
        if (string.IsNullOrWhiteSpace(args)) return HubStrings.Get("ActivityDetailEmpty");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(args);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return args;
            var lines = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.GetRawText();
                lines.Add($"{property.Name}: {value}");
            }
            // An empty object parses fine but says nothing; show the raw text so the reader sees what was sent.
            return lines.Count > 0 ? string.Join("\n", lines) : args;
        }
        catch (System.Text.Json.JsonException)
        {
            return args;
        }
    }

    /// <summary>The payload the tool returned, normalized to one line ending style and capped so a compiler's whole
    /// stderr can't stretch the transcript. The cap is what the row's detail scrolls inside; nothing else in the
    /// shell shows a tool payload, so past this bound the tail is said to be missing rather than cut silently.</summary>
    private static string FormatToolResult(ChatTurn result)
    {
        const int cap = 4096;
        var text = result.Text?.Replace("\r\n", "\n").Replace("\r", "\n") ?? "";
        if (text.Length == 0) return HubStrings.Get("ActivityDetailEmpty");
        return text.Length > cap ? text[..cap] + HubStrings.Get("ActivityDetailTruncated") : text;
    }

    /// <summary>One folded tool exchange as a single descriptive row: a status mark, the action named from its
    /// call, and — for a command — the command itself trailing that verb in the same reading order. This is the
    /// collapsed line inside <see cref="BuildActivityRowShell"/>; the full arguments and payload live in the detail
    /// it reveals. The left slot carries two glyphs in one cell — the execution status at rest, and the same
    /// `>`/`v` arrow the sidebar uses, which the row's styles swap in on hover and while expanded — so the
    /// affordance sits where the eye already is rather than stranded on the right edge.</summary>
    private Control BuildActivityRow(ChatTurn? call, ChatTurn result)
    {
        var command = call?.ToolName == "run_command" ? ArgField(call.ToolArguments, "command", "cmd") : null;
        var showsCommand = !string.IsNullOrWhiteSpace(command);
        // What a command ended with, when anything recorded it: the exit code and how long it ran. They trail the
        // preview rather than replacing it — one line still reads in order — and they are absent for every other
        // tool, because a read has no exit code and a duration for one answers nothing anybody asked.
        var facts = result.ToolFacts;
        var marks = facts is null ? 0 : (facts.ExitCode is null ? 0 : 1) + (facts.Elapsed is null ? 0 : 1);
        // The preview owns the star column, so it reads immediately after the verb. A star on the label is what
        // used to eat all the slack and pin the preview to the row's right edge, which made the eye cross the
        // whole column to finish the sentence. Without a preview the label keeps the star, because that is where
        // a long 「读取 <path>」 gets trimmed.
        var columns = showsCommand ? "Auto,Auto,*" : "Auto,*";
        for (var trailing = 0; trailing < marks; trailing++) columns += ",Auto";
        var grid = new Grid
        {
            Classes = { "activity-row" },
            ColumnDefinitions = new ColumnDefinitions(columns),
        };
        if (result.ToolFailed) grid.Classes.Add("failed");

        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-status" },
            Data = ThemeGeometry(result.ToolFailed ? "Hub.Icon.Close" : "Hub.Icon.CopySuccess"),
        });

        // Same cell as the status, so the hover swap never shifts the row's text sideways; both glyphs are 10×10.
        grid.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "activity-row-chevron" },
            Data = ThemeGeometry("Hub.Icon.ChevronRight"),
        });

        var text = new TextBlock { Classes = { "activity-row-text" } };
        text.Text = call is not null
            ? DescribeToolCall(call)
            : FirstLine(result.Text);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (showsCommand)
        {
            var oneLine = command!.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var chip = new Border
            {
                // file-chip for the look, source-chip so a check can tell these from the other chips in the same
                // panel — the run_command preview and the diff counters share the styling and nothing else.
                Classes = { "file-chip", "source-chip", "inline-chip" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = oneLine.Length > CommandPreviewCharacters
                        ? oneLine[..CommandPreviewCharacters] + "…"
                        : oneLine,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);
        }

        // The verdict of a command, in the two numbers a person would ask for. The code is drawn as a chip like the
        // command it follows, and takes the danger ink when it is not zero: the row already carries a red mark for a
        // failed call, and a `0` in green beside one would be the row disagreeing with itself.
        var trailingColumn = showsCommand ? 3 : 2;
        if (facts?.ExitCode is { } exitCode && call is not null
            && string.Equals(call.ToolName, WorkspaceTools.RunCommandTool, StringComparison.Ordinal))
        {
            var exit = new Border
            {
                // inline-chip for the layout it shares with the command preview, exit-chip so a check can tell the
                // two apart in the same row.
                Classes = { "file-chip", "inline-chip", "exit-chip" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        HubStrings.Get("ActivityRowExitCodeFormat"), exitCode),
                },
            };
            if (exitCode != 0) exit.Classes.Add("failed");
            Grid.SetColumn(exit, trailingColumn++);
            grid.Children.Add(exit);
        }

        if (facts?.Elapsed is { } elapsed)
        {
            var spent = new TextBlock
            {
                Classes = { "activity-row-meta" },
                Text = FormatDuration(elapsed),
            };
            Grid.SetColumn(spent, trailingColumn);
            grid.Children.Add(spent);
        }

        return grid;
    }

    /// <summary>How much of a command the collapsed row pre-truncates. The visual cut is the trimming's, at whatever
    /// width the column has left; this only keeps a megabyte-long one-liner out of the measure. Sized so the row
    /// still reads as one line at the chat column's 560px floor.</summary>
    private const int CommandPreviewCharacters = 96;

    private static string FirstLine(string text)
    {
        var at = text.IndexOf('\n');
        var line = (at < 0 ? text : text[..at]).Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    /// <summary>One tool call, said the way the reference says it: a verb naming the work plus the file or
    /// pattern it touched, so the row is about the action rather than the payload that came back.</summary>
    private static string DescribeToolCall(ChatTurn call)
    {
        var name = call.ToolName ?? "";
        var args = call.ToolArguments;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var path = ChatUndoStore.WritePathOf(args);
        if (path.Length == 0) path = ArgField(args, "path", "file_path", "dir") ?? "";

        return name switch
        {
            "read_file" when path.Length > 0
                => string.Format(culture, HubStrings.Get("ActivityRowRead"), path),
            "file_write" when path.Length > 0
                => string.Format(culture, HubStrings.Get("ActivityRowEditFile"), path),
            "search_text"
                => string.Format(culture, HubStrings.Get("ActivityRowSearch"),
                    ArgField(args, "pattern", "query") ?? ""),
            "find_files"
                => string.Format(culture, HubStrings.Get("ActivityRowFind"),
                    ArgField(args, "pattern", "query", "glob") ?? ""),
            "list_directory"
                => string.Format(culture, HubStrings.Get("ActivityRowList"), path.Length > 0 ? path : "."),
            "run_command" => HubStrings.Get("ActivityRowRunCommand"),
            // Host and path only: the query is where a token hides, and this line is stored in the transcript.
            "web_fetch" => string.Format(culture, HubStrings.Get("ActivityRowFetch"),
                WebFetch.Shown(ArgField(args, "url") ?? "")),
            _ => string.Format(culture, HubStrings.Get("ActivityRowGeneric"), name),
        };
    }

    /// <summary>The first string-valued field among <paramref name="keys"/> from a tool's JSON arguments, or null.
    /// A malformed payload is treated as absent rather than thrown: a description is not worth failing a paint.</summary>
    private static string? ArgField(string? json, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var key in keys)
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return value.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        return null;
    }

    /// <summary>The one shape every duration in this panel is written in, shared by the live bubble's counter and a
    /// folded group's title. The two spans are measured differently on purpose: the bubble reads the run's clock,
    /// which banks and stops while a person is deciding on an approval card, and the group title spans the stored
    /// turns, so it counts that decision too. A group that agreed to the second with the bubble it replaced would
    /// mean the reply suddenly got shorter the moment it finished.</summary>
    internal static string FormatDuration(TimeSpan span)
        => span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds:D2}s"
            : $"{Math.Max(0, (int)span.TotalSeconds)}s";

    /// <summary>What came back from one tool call, as a single muted line: the tool that answered, then the first
    /// line of its payload, trimmed and said to be trimmed. A result that failed is said so, because the line is
    /// otherwise the only place a refused or crashed call is visible at all.</summary>
    private static TextBlock ToolResultLine(string? toolName, ChatTurn turn)
    {
        var firstLine = turn.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lineBreak = firstLine.IndexOf('\n');
        if (lineBreak >= 0) firstLine = firstLine[..lineBreak];
        firstLine = firstLine.Trim();
        if (firstLine.Length > ResultLineCharacters) firstLine = firstLine[..ResultLineCharacters] + "…";

        var line = new StringBuilder();
        // Most of Hub's read-only tools already say which tool answered, because their result text is written for
        // a reader ("list_directory · '.agents/' · depth 2"). Prefixing that with the name again breaks the line
        // the transcript is read by.
        if (toolName is { Length: > 0 } named && !firstLine.StartsWith(named + " ", StringComparison.Ordinal))
            line.Append(toolName).Append(" → ");
        line.Append(firstLine.Length > 0 ? firstLine : "—");

        // A refused or crashed call has no other trace once the card is gone, and the danger ink is the one the
        // shell already uses for it — same metrics as muted, so the line never re-wraps.
        var block = new TextBlock
        {
            Classes = { turn.ToolFailed ? "danger" : "muted", "tool-result-line" },
            Text = line.ToString(),
        };
        return block;
    }

    /// <summary>How much of a result's first line is worth a glance. Long enough to read a path or a status,
    /// short enough that the line stays one line at the chat column's width.</summary>
    private const int ResultLineCharacters = 120;

    private static void AddApprovalDetail(StackPanel card, string labelKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        card.Children.Add(new TextBlock { Classes = { "approval-label" }, Text = HubStrings.Get(labelKey) });
        card.Children.Add(new TextBlock
        {
            Classes = { "approval-detail" },
            Text = value,
            MaxHeight = 180,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private static Button CloseActionButton(Action onClick)
    {
        var glyph = new TextBlock
        {
            Text = "×",
            FontSize = 15,
            Width = 15,
            Height = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The app's own UI stack rather than a bare "Segoe UI": this glyph has to come from a font that is there
        // on every host, and the stack is the one place that decision is written down.
        glyph.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("Hub.Font.Ui"));
        glyph.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        var button = new Button { Content = glyph, Tag = "Cancel" };
        button.Classes.Add("message-action");
        button.Classes.Add("message-action-icon");
        ToolTip.SetTip(button, HubStrings.Get("Cancel"));
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>The decided call, still in the record and no longer actionable: what became of it is the only
    /// thing worth the space — and, for a write, the one exit it has while a copy is left behind it.</summary>
    private Control BuildCallRecord(string conversationId, string callId, ChatTurn turn)
    {
        // A write that never had to ask still changed the file, so it gets a line of its own naming the file
        // rather than the tool: who was asked is a permission detail, what moved is the record.
        var asked = turn.ToolName == AskUserTool.ToolName;
        var line = turn.ApprovalState is { Length: > 0 } state
            ? $"{HubStrings.Get(state switch
                {
                    ChatApprovalStates.Approved => "ChatApprovalResolvedApproved",
                    ChatApprovalStates.Denied => "ChatApprovalResolvedDenied",
                    ChatApprovalStates.Answered => "ChatQuestionResolved",
                    // A question nobody answered was still asked, so "not executed" is not a fact about it — the
                    // thing that did not happen was the reply.
                    ChatApprovalStates.Superseded when asked => "ChatQuestionResolvedSuperseded",
                    _ => "ChatApprovalResolvedSuperseded",
                })} · {turn.ToolName ?? ""}"
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("ChatWriteRecordFormat"), ChatUndoStore.WritePathOf(turn.ToolArguments));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new TextBlock
        {
            Classes = { "approval-record-text" },
            Text = line,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (turn.UndoName is { Length: > 0 })
        {
            row.Children.Add(BuildUndoButton(conversationId, callId,
                ChatUndoStore.WritePathOf(turn.ToolArguments)));
        }

        return new Border { Classes = { "approval-record" }, Child = row };
    }

    private Button BuildUndoButton(string conversationId, string callId, string path)
    {
        var button = new Button
        {
            Classes = { "undo-action" },
            Content = HubStrings.Get("ChatUndoButton"),
            Tag = "ChatUndoButton",
        };
        ToolTip.SetTip(button, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("ChatUndoTip"), path));
        button.Click += (_, _) => RevertWrite(conversationId, callId, button);
        return button;
    }

    /// <summary>One click, one file back. A refusal is a line in the notice row rather than a dialog: the reason
    /// nothing moved has to be readable while the transcript stays exactly where it was.</summary>
    private async void RevertWrite(string conversationId, string callId, Button source)
    {
        source.IsEnabled = false;
        var outcome = await _chat.RevertWriteAsync(conversationId, callId);
        // A revert that worked raises Changed on its way out, and the rebuild that drops the spent button comes
        // with it — so only a refusal has anything left to re-enable.
        if (outcome.Succeeded) return;
        source.IsEnabled = true;
        var key = outcome.RefusalKey ?? outcome.Verdict switch
        {
            UndoVerdict.CopyMissing => "ChatUndoCopyMissing",
            UndoVerdict.ChangedSince => "ChatUndoChangedSince",
            UndoVerdict.TargetMissing => "ChatUndoTargetMissing",
            UndoVerdict.NotText => "ChatUndoNotText",
            UndoVerdict.RefusedPath => "ChatUndoRefusedPath",
            _ => "ChatUndoWriteFailed",
        };
        AppendNotice(HubStrings.Get(key), danger: key is "ChatUndoWriteFailed" or "ChatUndoRefusedPath");
    }

    /// <summary>Hands one decision to the workspace. A refusal is spoken out loud rather than swallowed: the card
    /// would otherwise sit there having done nothing, which reads as a broken button. <paramref name="alwaysAllowVerb"/>
    /// is the third answer, the one that grants a single git verb instead of the whole tool.</summary>
    private void ResolveApproval(string conversationId, string callId, bool approved, bool alwaysAllow,
        bool alwaysAllowVerb = false)
    {
        if (_chat.TryResolveApproval(conversationId, callId, approved, alwaysAllow, out var refusalKey,
                alwaysAllowVerb)) return;
        AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
    }

    private void ResolvePlanApproval(string conversationId, int turnIndex, string decision)
    {
        if (!_chat.TryResolvePlanApproval(conversationId, turnIndex, decision, out var refusalKey))
        {
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatApprovalGone"), danger: true);
            return;
        }

        // The revision's own text is sent by the review surface (CommitPlanReview) through the ordinary send
        // path; prefilling the composer here would put a prompt in front of a sentence the person already wrote.
        ConversationStateChanged?.Invoke();
    }

    /// <summary>
    /// Builds one message row. Both row kinds are a single Border: the user row's child column right-aligns
    /// its pill, the assistant row's carries borderless plain text. One container type keeps the hover-reveal
    /// rules in the markup to two instead of four, and both carry the message-row class and, when
    /// <paramref name="index"/> is non-null, a hover-revealed action row.
    /// A null <paramref name="index"/> (the live streaming bubble / just-sent user pill) carries no
    /// action bar: there is nothing stable to act on until the turn is persisted.
    /// </summary>
    private Control BuildMessageRow(
        bool fromUser, StackPanel body, int? index, string role, string text, bool isLast, DateTimeOffset? at = null)
    {
        var column = new StackPanel { Spacing = 6 };

        if (fromUser)
        {
            column.Children.Add(new Border
            {
                Classes = { "user-pill" },
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = body,
            });
        }
        else
        {
            column.Children.Add(body);
        }

        if (index is { } messageIndex)
        {
            var actions = BuildActionBar(messageIndex, role, text, isLast, at, body);
            actions.HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            column.Children.Add(actions);
        }

        // One container type for both row kinds. The hover-reveal rules in the markup key on
        // Border.message-row, and having two container types meant writing every reveal twice; a user row is
        // now a Border whose child column right-aligns the pill, which is all the Grid ever added.
        // MessageRows selects by the message-row class rather than by type, so nothing downstream notices.
        var row = new Border { ClipToBounds = true, Child = column };
        row.Classes.Add("message-row");
        row.Classes.Add(fromUser ? "user-row" : "assistant-msg");
        return row;
    }

    private Control BuildActionBar(int index, string role, string text, bool isLast, DateTimeOffset? at, StackPanel body)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Classes.Add("message-actions");

        if (role == ChatRoles.User && at is { } timestamp)
        {
            var localTimestamp = timestamp.ToLocalTime();
            var relative = new TextBlock { Text = FormatRelativeTime(DateTimeOffset.Now - localTimestamp) };
            relative.Classes.Add("message-timestamp");
            ToolTip.SetTip(relative, localTimestamp.ToString(
                "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture));
            bar.Children.Add(relative);
        }

        if (role == ChatRoles.User)
        {
            bar.Children.Add(IconActionButton("CopyMessage", "Hub.Icon.Copy", () => CopyToClipboard(text)));
            bar.Children.Add(IconActionButton("EditMessage", "Hub.Icon.Edit", () => BeginInlineEdit(index, body, bar)));
            // A question that never got an answer is otherwise stuck: the notice that explained the failure is
            // not written to the transcript, and resubmitting the same text is refused as an unchanged edit. The
            // same ↻ that re-answers a reply re-asks this one, because Regenerate drops nothing when the
            // trailing turn is the question. It stops being the trailing turn the moment an answer lands, so
            // the button leaves on its own.
            if (isLast && ViewedRun is null)
                bar.Children.Add(IconActionButton("RetryMessage", "Hub.Icon.Refresh", RegenerateAsync));
        }
        else
        {
            bar.Children.Add(IconActionButton("CopyMessage", "Hub.Icon.Copy", () => CopyToClipboard(text)));
            if (isLast)
                bar.Children.Add(IconActionButton("RegenerateMessage", "Hub.Icon.Refresh", RegenerateAsync));
            bar.Children.Add(IconActionButton("BranchFromHere", "Hub.Icon.Branch", () => BranchFromHere(index)));
        }

        return bar;
    }

    private static string FormatRelativeTime(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        if (seconds < 60) return HubStrings.Get("MessageTimeJustNow");
        if (seconds < 3600)
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("MessageTimeMinutesAgo"),
                (int)elapsed.TotalMinutes);
        if (seconds < 86400)
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("MessageTimeHoursAgo"),
                (int)elapsed.TotalHours);

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            HubStrings.Get("MessageTimeDaysAgo"),
            (int)elapsed.TotalDays);
    }

    /// <summary>One icon in a message's action row. The size and pen are per-glyph because a cross is two
    /// strokes meeting in the middle: at the weight an outline icon carries it, the crossing fills its plate
    /// and reads as one solid mark.</summary>
    private static Button IconActionButton(
        string textKey, string geometryKey, Action onClick, double size = 13, double thickness = 1.7)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension(geometryKey));
        icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));

        var button = new Button { Content = icon, Tag = textKey };
        button.Classes.Add("message-action");
        button.Classes.Add("message-action-icon");
        ToolTip.SetTip(button, HubStrings.Get(textKey));
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// The last thing the app had to say about each conversation, the run it was said about, and how many of the
    /// person's own messages had been sent when it was said. A notice is the tail of the transcript until either of
    /// those two facts stops holding — which is what keeps it surviving a rebuild of that transcript without
    /// outliving the news it carried.
    ///
    /// <para>It is keyed by conversation rather than a single slot because a run does not end in step with what is
    /// on screen: the reason a session stopped while the person read another one is still news when they come
    /// back, and holding it in one shared field would either lose it or paint it onto the wrong transcript.</para>
    ///
    /// <para>The run is the half the message count cannot stand in for. A continuation — an approved call, an
    /// answered question, a ↻ — starts a fresh run and appends no message of the person's, so a stop pressed on the
    /// round before it would otherwise go on answering a round that is already arriving. That is a rule about what
    /// a run said; a folder verdict or an attachment refusal is news about the conversation rather than about the
    /// round that ended, and keeps the older lifetime.</para>
    ///
    /// <para><c>Retryable</c> marks the endings where the same round is worth asking for again — a timeout, a
    /// stalled stream, a refused connection. Those carry the ↻ on the line itself, because the alternative was a
    /// sentence telling the person to retry and the only way to do it hidden on the last message row, revealed on
    /// hover, three pixels from a notice that is not part of the transcript.</para>
    /// </summary>
    private readonly Dictionary<string, (string Text, bool Danger, int OwnTurns, bool Retryable, bool FromRun,
        ConversationRun? Run)> _notices = new(StringComparer.Ordinal);

    private void AppendNotice(string text, bool danger)
    {
        var viewed = _chat.ActiveConversation;
        RememberNotice(viewed?.Id ?? "", text, danger,
            viewed?.Messages.Count(turn => turn.Role == ChatRoles.User) ?? 0);
        RenderNotice();
        ScrollToEnd();
    }

    /// <summary>Files what a run or an action had to say for one conversation. The line reaches the screen through
    /// <see cref="RenderNotice"/>, which paints only the conversation being looked at.</summary>
    private void RememberNotice(string conversationId, string text, bool danger, int ownTurns,
        bool retryable = false, bool fromRun = false, ConversationRun? run = null)
        => _notices[conversationId] = (text, danger, ownTurns, retryable, fromRun, run);

    /// <summary>Which endings are worth asking for again. Everything on this list is a round that never got an
    /// answer through no fault of the question: a window ran out, the stream stopped, the connection or the limit
    /// got in the way, the model closed the response with nothing in it. A refusal the person made (ChatCancelled,
    /// an approval that went away) is not on the list — pressing ↻ after saying "no" would ask for the same thing
    /// they just declined. One rule, read both by the ending as it arrives and by the stored line as it is painted
    /// back, so a retry button cannot appear at one door and be missing at the other.</summary>
    private static bool NoticeIsRetryable(string key)
        => key is "ChatNoFirstFrame" or "ChatTimedOut" or "ChatTimedOutAfterRetries"
            or "ChatStreamInterrupted" or "ChatConnectionFailed" or "ChatRateLimited"
            or "ChatFailed" or "ChatNoResponse";

    /// <summary>What the strip below the transcript says. Two painters rather than one, because the stored notice
    /// can decline to speak — it ages out — while a compaction is only ever happening or over.</summary>
    private void RenderNotice()
    {
        RenderStoredNotice();
        RenderCompactingRow();
    }

    /// <summary>Adds the line a compaction owns, while one runs and no bubble is on screen. Read from the workspace
    /// rather than remembered here, so a rebuild of the transcript paints it again for as long as it is true — the
    /// reason this strip lives outside the flow at all. A run gets the same fact on its own status line instead:
    /// saying it in both places would be one thing said twice.</summary>
    private void RenderCompactingRow()
    {
        var conversationId = _chat.ActiveConversation?.Id;
        if (conversationId is null || !_chat.IsCompressingContext(conversationId)) return;
        // A run on this same session already says it on its own status line; a bubble belonging to another
        // session is not this session's news, and must not silence the strip.
        if (_live?.Run.ConversationId == conversationId) return;
        NoticeHost.Children.Add(BuildNoticeRow(HubStrings.Get("ChatCompactingContext"), danger: false,
            onRetry: null));
    }

    /// <summary>Paints the stored notice, if it still is news: same conversation, nothing of the person's said
    /// since, and — for the news a run brought — no newer run begun. Called from the render path, so a rebuild
    /// cannot erase it and a new message cannot be answered by an old warning. With no conversation at all — the
    /// model list just emptied, say — there is nothing that can age it out, so the line stands until the next one
    /// replaces it.</summary>
    private void RenderStoredNotice()
    {
        NoticeHost.Children.Clear();
        var viewed = _chat.ActiveConversation;
        var conversationId = viewed?.Id ?? "";
        // A notice is news about a conversation; when that conversation is gone the line has no subject left, so
        // it leaves with it rather than keeping a key nothing will ever view again. The sidebar list is not the
        // test for "gone" — a session nobody has spoken in holds no row there, and that is exactly the session an
        // attachment refusal gets filed on.
        foreach (var stale in _notices.Keys
                     .Where(id => id.Length > 0 && id != conversationId
                                  && !_chat.Conversations.Any(session => session.Id == id))
                     .ToList())
            _notices.Remove(stale);
        var ownTurns = viewed?.Messages.Count(turn => turn.Role == ChatRoles.User) ?? 0;
        if (!_notices.TryGetValue(conversationId, out var notice))
        {
            // Nothing in memory for the session on screen, so read back how its last run ended. This is the half
            // that makes the difference observable: a line that lives only in a dictionary is a line that
            // disappears on a conversation switch, and "the model returned no content" is news somebody may still
            // want to act on after they go and come back. Never while a round is in flight — the newest ending
            // owns the strip, and a stored one must not speak over a reply that is arriving.
            if (viewed?.LastNotice is not { } stored || _chat.RunFor(conversationId) is not null
                || ownTurns != stored.UserTurns) return;
            notice = (HubStrings.Get(stored.Key) + (stored.Detail ?? ""), stored.Danger, stored.UserTurns,
                NoticeIsRetryable(stored.Key), false, null);
        }
        else if (notice.FromRun && _chat.RunFor(conversationId) is { } current
                 && !ReferenceEquals(current, notice.Run))
        {
            // The registry is the authority on which round this conversation is in. A finished run leaves it
            // before the notice is filed, so a stored null and a live instance both mean "a newer round has
            // begun" — and a continuation that begins one without adding a message of the person's is exactly the
            // case the count below cannot see. Only a run's own news is measured this way; what an action said
            // about the conversation outlives the round it was said in.
            _notices.Remove(conversationId);
            return;
        }

        if (ownTurns != notice.OwnTurns)
        {
            _notices.Remove(conversationId);
            return;
        }

        NoticeHost.Children.Add(BuildNoticeRow(notice.Text, notice.Danger,
            notice.Retryable ? RegenerateAsync : null));
    }

    /// <summary>The notice row on screen, whichever check needs it. It lives below the flow rather than at the end
    /// of it, so it is never mistaken for a turn and never shifts an index.</summary>
    private Grid? CurrentNoticeRow
        => NoticeHost.Children.LastOrDefault() is Grid row && row.Classes.Contains("notice") ? row : null;

    /// <summary>The notice rows drawn <i>inside</i> the flow — the ones a model switch leaves behind — for
    /// whichever check needs to measure one. The turn is what the conversation stores; this is what the flow drew
    /// from it, which is the thing with a size and a position.</summary>
    internal IReadOnlyList<Grid> NoticeTurnRowsForCheck
        => MessageFlow.Children.OfType<Grid>().Where(row => row.Classes.Contains("notice")).ToList();

    /// <summary>Where the newest in-flow notice row sits inside the flow, in the flow's own coordinates —
    /// (left, width, flow width) — which is what turns "it is centered" into numbers a check can hold.</summary>
    internal (double Left, double Width, double FlowWidth)? NoticeTurnRowGeometryForCheck
    {
        get
        {
            if (NoticeTurnRowsForCheck.LastOrDefault() is not { } row) return null;
            var left = row.TranslatePoint(new Point(0, 0), MessageFlow)?.X ?? double.NaN;
            return (left, row.Bounds.Width, MessageFlow.Bounds.Width);
        }
    }

    /// <summary>Tears the flow down and paints it again, which is the path a conversation switch takes — and the
    /// cheapest way for a check to ask whether a row is redrawn from the transcript rather than kept alive by the
    /// view that happened to build it.</summary>
    internal void RebuildMessagesForCheck() => ForceRebuildMessages();

    private static Grid BuildNoticeRow(string text, bool danger, Action? onRetry)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*" + (onRetry is null ? "" : ",Auto")) };
        row.Classes.Add("notice");
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.MaxWidth = 820;
        if (danger) row.Classes.Add("danger");

        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Data = ThemeGeometry("Hub.Icon.Notice"),
        };
        icon.Classes.Add("notice-icon");
        // The row declares "Auto,*", and a child without an explicit column lands in column 0 — both of
        // them there meant the text sat on top of the icon, which in a real run read like "the tip is
        // smudged over something". Icon owns column 0, text column 1.
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var label = new TextBlock { Text = text };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        // The same button the last message row uses, built by the same helper: the tooltip, the hover reveal and
        // the glyph's offset inside its own plate are already settled there, and a second copy of an action button
        // is how the notice ends up looking like somebody else's widget.
        if (onRetry is not null)
        {
            var retry = IconActionButton("RetryMessage", "Hub.Icon.Refresh", onRetry);
            Grid.SetColumn(retry, 2);
            row.Children.Add(retry);
        }

        return row;
    }

    // ───────────────────────── Actions ─────────────────────────

    private void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var data = new DataTransfer();
        data.Add(item);
        _ = clipboard.SetDataAsync(data);
    }

    /// <summary>
    /// Keeps the round button in step with the composer: the same button sends or stops, and it stays
    /// greyed out while the box is empty so "there is nothing to send" is visible before the click rather than
    /// after it.
    /// </summary>
    private void UpdateSendState()
    {
        SendStateUpdates++;
        // A decision on the card above does not own this button any more: the run it belongs to is parked, so
        // IsViewedStreaming is false and the button reads as an ordinary send of whatever is in the box — which is
        // exactly what pressing it does.
        var streaming = IsViewedStreaming;
        var hasText = (InputBox.Text ?? "").Trim().Length > 0;
        var hasPicture = _pendingPictures.Count > 0;
        // A picture in the chip row is as much "there is something to send" as text is, and it says so in the
        // tooltip too: while a reply is streaming, sending this means steering it rather than stopping it.
        var saysSomething = hasText || hasPicture;
        // A steer already waiting is spent: the button must not offer a second one before the first is taken.
        SendButton.IsEnabled = ViewedRun?.HasQueuedSteer != true && (streaming || saysSomething);
        SendButton.Content = BuildSendIcon(streaming && !saysSomething);
        ToolTip.SetTip(SendButton, HubStrings.Get(streaming
            ? saysSomething ? "ChatSteer" : "Stop"
            : "Send"));
        UpdateContextRing();
    }

    /// <summary>Builds the arrow (send) or square (stop) glyph. Colours are bound as DynamicResource rather
    /// than looked up once because this runs from the constructor, before the control is attached — a brush
    /// captured there comes back null and the glyph would be invisible.</summary>
    private static Control BuildSendIcon(bool streaming)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
        };
        path.Data = ThemeGeometry(streaming ? "Hub.Icon.Stop" : "Hub.Icon.Send");

        if (streaming)
        {
            // The stop square is solid; the fill is what makes it read differently from the arrow.
            path.Bind(Avalonia.Controls.Shapes.Path.FillProperty, new DynamicResourceExtension("Hub.TextOnAccent"));
            return path;
        }

        path.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextOnAccent"));
        path.StrokeThickness = 2;
        path.StrokeLineCap = PenLineCap.Round;
        path.StrokeJoin = PenLineJoin.Round;
        return path;
    }

    private static Geometry? ThemeGeometry(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var value) ? value as Geometry : null;

    /// <summary>Highlights the composer frame while the input has focus so the whole rounded box reads as the
    /// thing being typed into.</summary>
    private void SetComposerFocus(bool focused)
    {
        ComposerFrame.Classes.Set("focused", focused);
        // Leaving the composer is the point where re-arranging the column is safe again, so the reply the
        // deferred ticks held back paints here (and the timer resumes on its own once focus is gone).
        if (!focused) FlushLiveRepaintIfPending();
    }

    /// <summary>Whether Up is free to take the key: the box is empty, or the person is already part-way through a
    /// recall. Anything else is a caret moving inside a draft, which is the text box's business and not this one.</summary>
    private bool CanRecall() => _recallIndex > 0 || (InputBox.Text ?? "").Length == 0;

    /// <summary>Steps through what has been sent. Down at the newest end lands back on an empty box, which is the
    /// only way out of the walk that does not throw away the sentence being looked at.</summary>
    private void RecallText(bool backwards)
    {
        _recallIndex = backwards
            ? Math.Min(_recallIndex + 1, _sentTexts.Count)
            : Math.Max(_recallIndex - 1, 0);
        if (_sentTexts.Count == 0) return;

        var picked = _recallIndex == 0 ? "" : _sentTexts[^_recallIndex];
        _recalling = true;
        InputBox.Text = picked;
        _recalling = false;
        InputBox.CaretIndex = picked.Length;
        UpdateSendState();
    }

    /// <summary>Records a message that really left the composer. A send that was refused, and a steer that lost
    /// the race to a finished run, both keep the draft out of here — history is what was said, not what was typed.</summary>
    private void RememberSentText(string text)
    {
        if (text.Length == 0) return;
        _sentTexts.Add(text);
        if (_sentTexts.Count > MaxRecalledTexts) _sentTexts.RemoveAt(0);
        _recallIndex = 0;
    }

    private async Task SendAsync()
    {
        if (ViewedRun is { IsStreaming: true } run)
        {
            if (run.HasQueuedSteer) return;
            var steerText = (InputBox.Text ?? "").Trim();
            var steerPictures = PendingPictureBytes();
            if (steerText.Length > 0 || steerPictures is { Count: > 0 })
            {
                string? steerContext;
                try
                {
                    steerContext = await ReadAttachmentContextAsync();
                }
                catch (Exception ex)
                {
                    AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
                    return;
                }

                // A steer is the routing table's strongest signal and it joins a reply already on its way, yet
                // until now it looked exactly like an ordinary send. The first Enter therefore only shows what
                // would be injected; the second one (or the strip's button) is the steer itself. It waits for the
                // step in flight rather than cancelling it.
                ShowSteerConfirm(steerText, steerContext, steerPictures);
                return;
            }

            _chat.RequestStop(run.ConversationId);
            return;
        }

        var text = (InputBox.Text ?? "").Trim();
        var pictures = PendingPictureBytes();
        // A picture with no question under it is a message: "look at this" is what people actually send, and a
        // send button that stays grey because the box is empty would say otherwise.
        if (text.Length == 0 && pictures is null) return;
        if (_chat.SelectedChatModel is null)
        {
            // One sentence, one door: the pre-check and the refusal behind <c>TryEnqueueSend</c> both come through
            // <see cref="NoticeFor"/>, so a session missing its own model says its name here and there alike.
            AppendNotice(NoticeFor("NoAvailableChatModels", _chat.ActiveConversation?.Id), danger: true);
            return;
        }

        string? context;
        try
        {
            context = await ReadAttachmentContextAsync();
        }
        catch (Exception ex)
        {
            AppendNotice(HubStrings.Get("ChatAttachmentFailed") + ex.Message, danger: true);
            return;
        }

        // Handed over first, cleared after: a refusal has to leave the sentence where it was typed. The decision
        // card living above the box rather than over it is what makes this reachable — Enter with a draft in the
        // box is now an ordinary thing to do while a plan waits, and the workspace answers "resolve it first".
        if (!SendTextAsync(text, context, pictures)) return;
        _contextAttachments.Clear();
        _pendingPictures.Clear();
        RenderContextAttachments();
        InputBox.Text = "";
    }

    /// <summary>Hands the message to the workspace and starts a run for it. The user's own turn is painted from
    /// the transcript like any other row, so there is no second copy of it here to keep in step. Reports whether
    /// the message got in, because the caller owns the draft and must not empty the box on a refusal.</summary>
    private bool SendTextAsync(string text, string? attachedContext = null, IReadOnlyList<byte[]>? pictures = null)
    {
        if (text.Length == 0 && pictures is not { Count: > 0 }) return false;
        if (_chat.ActiveConversation is null) _chat.StartConversation();
        if (_chat.ActiveConversation is not { } conversation) return false;

        if (!_chat.TryEnqueueSend(conversation.Id, text, attachedContext, pictures, out var refusalKey))
        {
            AppendNotice(NoticeFor(refusalKey, conversation.Id), danger: true);
            return false;
        }

        RememberSentText(text);
        ConversationStateChanged?.Invoke();
        return true;
    }

    /// <summary>Asks for the same round again. Nothing new is appended — the workspace rebuilds the request from
    /// the transcript, so the question that went unanswered goes back out as the turn it already was. The notice
    /// that explained the failure is retired here rather than by the turn-count rule, because a continuation adds
    /// no turn of the person's and the old line would otherwise sit above an answer that is now arriving.</summary>
    private void RegenerateAsync()
    {
        if (_chat.ActiveConversation is not { } conversation) return;
        if (IsViewedStreaming)
        {
            // The button is only built while nothing is answering, so reaching here means a run started
            // between the click and this line. A refusal the user can read beats a button that appears dead.
            AppendNotice(HubStrings.Get("ChatSessionBusy"), danger: true);
            return;
        }

        if (!_chat.Regenerate(conversation.Id)) return;

        ForceRebuildMessages();
        if (!_chat.TryEnqueueContinuation(conversation.Id, out var refusalKey,
                ToolApprovalResults.NotAnswered))
        {
            AppendNotice(HubStrings.Get(refusalKey ?? "ChatFailed"), danger: true);
            return;
        }

        ForgetNotice(conversation.Id);
    }

    /// <summary>Drops the notice a retryable ending filed on one conversation, and repaints if that conversation is
    /// the one on screen. Only a <b>retryable</b> line goes: an informational notice is the tail of the transcript
    /// until the person speaks again, or a newer run begins where the news came from a run, and a rebuild — or a ↻ pressed for another reason — must
    /// not erase it as collateral, which is a rule this suite already holds. The line offering this button is the
    /// exception because it is the answer to the button: the failure it describes is the thing now being retried.
    /// A run notice that is not retryable needs no call here — <see cref="RenderNotice"/> ages it out the moment the
    /// conversation has a different run. Only the viewed session is redrawn — a background session's news never
    /// moves the reader's position.</summary>
    private void ForgetNotice(string conversationId)
    {
        if (!_notices.TryGetValue(conversationId, out var notice) || !notice.Retryable) return;
        _notices.Remove(conversationId);
        if (conversationId != _chat.ViewedConversationId) return;
        RenderNotice();
    }

    /// <summary>Forks the conversation at this message into a fresh session and switches to it. Refused while
    /// a reply is streaming, because the history is still growing and the cut point would not be the one that
    /// was clicked. The message flow and the sidebar both repaint through the workspace's Changed event.</summary>
    private void BranchFromHere(int index)
    {
        if (_chat.ActiveConversation is not { } conversation || IsViewedStreaming) return;
        _chat.BranchFrom(conversation.Id, index);
    }

    /// <summary>
    /// Puts a user message back into an editable box where it stands, and swaps the row's hover actions for
    /// cancel and confirm so both exits are visible instead of something to remember. Enter commits,
    /// Shift+Enter adds a line, Escape or a click anywhere outside abandons the edit.
    ///
    /// Committing drops everything after this turn. That used to be gated by a confirmation dialog that only
    /// appeared once the retyping was already done; the edit icon's tooltip carries the consequence now, at
    /// the moment the edit is chosen instead.
    /// </summary>
    private void BeginInlineEdit(int index, StackPanel body, StackPanel bar)
    {
        if (IsViewedStreaming || body.Children.FirstOrDefault() is not TextBlock original) return;

        var untouched = original.Text ?? "";
        var normalActions = bar.Children.ToList();
        var closed = false;
        var editor = new TextBox
        {
            Text = untouched,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = original.FontSize,
        };
        editor.Classes.Add("message-edit");

        void Close()
        {
            if (closed) return;
            closed = true;
            body.Children.Remove(editor);
            body.Children.Insert(0, original);
            bar.Children.Clear();
            foreach (var action in normalActions) bar.Children.Add(action);
        }

        void Commit()
        {
            if (closed) return;
            var edited = editor.Text.Trim();
            var conversation = _chat.ActiveConversation;
            if (edited.Length == 0 || edited == untouched
                || IsViewedStreaming || conversation is null || !_chat.EditAndResend(conversation.Id, index, edited))
            {
                Close();
                return;
            }

            // The rebuild throws this whole subtree away, so there is nothing left to restore.
            closed = true;
            ForceRebuildMessages();
            if (!_chat.TryEnqueueContinuation(conversation.Id, out var refusalKey))
                AppendNotice(HubStrings.Get(refusalKey ?? "ChatFailed"), danger: true);
        }

        body.Children.Remove(original);
        body.Children.Insert(0, editor);
        bar.Children.Clear();
        bar.Children.Add(CloseActionButton(Close));
        bar.Children.Add(IconActionButton("ConfirmEdit", "Hub.Icon.Tick", Commit));
        editor.Focus();
        editor.CaretIndex = editor.Text.Length;

        // Registered on the tunnel for the same reason the composer is: a TextBox with AcceptsReturn marks
        // Enter handled in order to insert a newline, so a plain subscriber never sees the key.
        editor.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                Commit();
            }
        }, RoutingStrategies.Tunnel);

        // Clicking away abandons the edit — but not when focus only moved to this editor's own two buttons,
        // which have to be allowed to receive the click, and not when the window itself lost focus, which
        // would discard a draft nobody meant to drop.
        editor.LostFocus += (_, e) =>
        {
            if (e.NewFocusedElement is not Visual next || ReferenceEquals(next, editor)) return;
            if (next.GetLogicalAncestors().Any(ancestor
                    => ReferenceEquals(ancestor, bar) || ReferenceEquals(ancestor, body))) return;
            Close();
        };
    }

    /// <summary>
    /// The bubble for the reply now arriving in this session, created when the session was not on screen a
    /// moment ago. Everything on it is read off the run — the text that arrived while it was hidden, the calls it
    /// has been through, the time it has been taking — so switching away and back shows the same reply the user
    /// left, not one restarted from zero.
    /// </summary>
    private LiveBubble AttachLive(ConversationRun run)
    {
        if (_live is { } existing && existing.Run == run)
        {
            RefreshLive(existing);
            return existing;
        }

        CloseLive();
        // The thinking first, so it reads above the answer the way it arrived. It starts collapsed because most
        // replies never think at all, and an empty row would be chrome describing nothing. No fold memory is
        // passed: this row belongs to one answer in flight, and the stored turn's row takes over from it.
        var thought = BuildThoughtRow(ThoughtLabel(null, inGroup: false), onUserToggle: null);
        thought.Shell.IsVisible = false;
        // The answer's own slot is a panel rather than a run of text: every finished Markdown block is built once
        // and then left alone, and only the one still being typed is replaced, at the bottom of the stack.
        var prose = new StackPanel { Spacing = 8 };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(thought.Shell);
        body.Children.Add(prose);
        var activity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        activity.Classes.Add("chat-activity");
        // The glyph replaces the three blinking dots: it draws the phase the run is actually in rather than a
        // generic wait, and it stops its own clock the moment it is hidden or the phase needs no motion.
        var glyph = new ActivityGlyph();
        glyph.Bind(ActivityGlyph.TrackBrushProperty, new DynamicResourceExtension("Hub.Border"));
        glyph.Bind(ActivityGlyph.DotBrushProperty, new DynamicResourceExtension("Hub.Accent"));
        activity.Children.Add(glyph);
        var status = new TextBlock { Text = HubStrings.Get("ChatPreparing") };
        status.Classes.Add("chat-activity-label");
        activity.Children.Add(status);
        activity.Children.Add(new TextBlock
        {
            Text = "·",
            Classes = { "chat-activity-elapsed" },
        });
        // Seeded from the run rather than started at "0s": a bubble rebuilt a minute into a reply has to say a
        // minute, and it should say it on the frame it appears, not after the next tick.
        var elapsed = new TextBlock
        {
            Text = FormatDuration(run.Elapsed),
            Classes = { "chat-activity-elapsed" },
        };
        activity.Children.Add(elapsed);
        body.Children.Add(activity);

        var bubble = new LiveBubble
        {
            Run = run,
            Row = BuildMessageRow(false, body, null, ChatRoles.Assistant, "", isLast: true),
            Prose = prose,
            Thought = thought,
            Status = status,
            Elapsed = elapsed,
            Glyph = glyph,
            ToolRunning = run.IsToolRunning,
        };
        // The thinking body follows its newest line only while the reader has left it at the bottom — the same
        // rule the transcript applies to the whole column, and for the same reason. A box that always follows is a
        // box nobody can read the top of, and a position somebody took is not ours to take back.
        bubble.Thought.Scroll.ScrollChanged += (_, _) => bubble.ThoughtStick =
            bubble.Thought.Scroll.Offset.Y + bubble.Thought.Scroll.Viewport.Height
            >= bubble.Thought.Scroll.Extent.Height - StickEpsilon;
        // A wait already in progress is the reason this reply is late, and the event that said so fired while the
        // session was out of sight: read the fact off the run rather than wait for it to happen again.
        if (run.RetryWaiting) status.Text = RetryActivityText(run.RetryWaitNotice());
        bubble.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        bubble.Timer.Tick += (_, _) =>
        {
            if (_live is not { } current) return;
            current.Frame++;
            current.Elapsed.Text = FormatDuration(current.Run.Elapsed);
            RefreshGlyphPhase(current);
            // The command card's clock ticks with the bubble's rather than on one of its own: it is a number about
            // the same run, and a second timer would only be a second thing to stop.
            if (_runningCommand is { } printing) RefreshRunningCommand(printing, current.Run);
        };
        _live = bubble;
        bubble.Timer.Start();
        RefreshLive(bubble);
        UpdateSendState();
        ScrollToEnd();
        return bubble;
    }

    /// <summary>Throws the bubble away, not the reply: the run keeps streaming and a later attach reads the text,
    /// the tool count and the elapsed time back off it. The timer goes with the row, because nothing is being
    /// animated any more — and it is only the animation, never the clock the answer is measured by.</summary>
    private void CloseLive()
    {
        if (_live is not { } live) return;
        live.Timer?.Stop();
        live.Timer = null;
        // The bubble is about to be the only record of what streaming cost, and it is thrown away here.
        _lastLiveBuilds = live.Builds;
        MessageFlow.Children.Remove(live.Row);
        _live = null;
        _liveRepaintPending = false;
        UpdateSendState();
    }

    private void RefreshLive(LiveBubble bubble)
    {
        // Read off the run the bubble is attached to, not looked up by session id: the bubble and its run are the
        // same pairing from attach to detach, so a refresh can never land on a reply that started afterwards.
        var text = bubble.Run.LiveText;
        var thought = bubble.Run.LiveReasoning;

        // Painting the arriving text re-measures the whole message column, and the sticky scroll that follows it
        // flips the column width, re-wrapping every completed bubble — both re-arrange the focused composer and
        // interrupt an open Linux composition, so the caret jumps. Defer just those two while the user composes;
        // the thinking glyph keeps running (cheap, and the suite guards against freezing it), and run.LiveText
        // accrues server-side, so nothing is lost — focus loss flushes the held-back paint.
        // A held thumb needs the same protection an open composition gets: a repaint re-measures the message
        // column (see PaintStreamingMarkdown), and a column that changes height under the hand moves the Track's
        // mapping mid-drag, which is a thumb that shrinks back from one hand and drifts off the other. Held back
        // the same way, flushed the same way, when the hand lets go.
        if (DeferringLiveRepaint || _thumbDragging)
        {
            if (bubble.PaintedText != text || (bubble.Thought.Text.Text ?? "") != thought) _liveRepaintPending = true;
            RefreshGlyphPhase(bubble);
            return;
        }

        _liveRepaintPending = false;
        PaintStreamingMarkdown(bubble, text, QuoteIntoComposer);
        bubble.PaintedText = text;
        if (text.Length == 0) bubble.ShowedText = false;
        else if (!bubble.ShowedText)
        {
            bubble.ShowedText = true;
            bubble.Status.Text = HubStrings.Get("ChatGenerating");
        }

        // The row appears the first time a run thinks and does not leave again while the answer is on screen: it
        // folds when the prose starts rather than vanishing, because nobody who watched forty seconds of reasoning
        // asked for it to be taken away the moment the result turned up.
        //
        // A tool loop thinks before every round, though, and showing it each time would flash the card above the
        // group a hundred and twenty-seven times in a run that made that many calls. So one run gets one live
        // view — unless a hand has opened the row, which is a standing request to keep seeing it. The later rounds
        // are not a dead screen either: the glyph and the elapsed line keep moving, and their words go straight to
        // the row inside the group.
        var showRow = _chat.ShowThinking && thought.Length > 0
                      && (!bubble.Run.ThoughtFiledBefore || bubble.Thought.TouchedByUser);
        bubble.Thought.Shell.IsVisible = showRow;
        if (showRow)
        {
            bubble.Thought.Text.Text = thought;
            // The same sentence the stored row will carry, and off the same clock — which is the point of
            // recording the duration at all: the number the reader watched must still be there when the bubble is
            // thrown away and the real turn takes its place.
            bubble.Thought.Label.Text = ThoughtLabel(bubble.Run.ReasoningElapsed, inGroup: false);
            // Open while it is the only thing arriving, folded once the answer speaks — and never folded against
            // a reader who moved it. This is the same boundary the status line and the glyph turn on, so the
            // three cannot disagree about which phase the run is in.
            if (!bubble.Thought.TouchedByUser) bubble.Thought.SetExpanded(!bubble.ShowedText);
            if (bubble.Thought.Expanded && bubble.ThoughtStick) FollowNewestLine(bubble.Thought.Scroll);
        }

        RefreshGlyphPhase(bubble);
        ScrollToEndIfSticky();
    }

    /// <summary>How often the block still being typed is rebuilt. Every chunk would be a rebuild per frame —
    /// a fresh document, a fresh parse, a fresh code editor for each fence — and the transcript is not
    /// virtualized, so each one re-measures the whole column. At this interval the answer keeps up with a fast
    /// model while a long reply costs a handful of small builds a second.</summary>
    private const long OpenBlockMilliseconds = 120;

    /// <summary>
    /// Lays the arriving answer down as Markdown, building each finished block exactly once.
    ///
    /// <para>The whole reply is never re-rendered. <see cref="MarkdownMessageRenderer.SettlePoint"/> says how
    /// much of it is made of blocks that can no longer change; those are appended and then never touched again,
    /// and only the open block underneath them is replaced — and only when its text actually moved and the
    /// throttle has expired. A frame that arrives inside the same block therefore does no building at all.</para>
    ///
    /// <para>The open block is rendered from a repaired copy, so a fence or an emphasis the model has not closed
    /// yet still reads as formatting rather than as markup. The run's own buffer is never touched: what gets
    /// filed in the transcript is what was actually said.</para>
    ///
    /// <para>Static because a bubble is painted from the run's own progress, not from the panel's state — so the
    /// one thing a painted block still needs from the panel, where a 引用 from its right-click menu goes, arrives
    /// as <paramref name="quote"/> rather than being reached for.</para>
    /// </summary>
    private static void PaintStreamingMarkdown(LiveBubble bubble, string text, Action<string>? quote = null)
    {
        // A run clears its buffer between tool-loop rounds, so a bubble that survives the switch can be handed a
        // shorter string than the one it last drew from — and a longer one that merely starts the same is the
        // same answer only by luck. Anything that is no longer a prefix of what is on screen belongs to a reply
        // already filed in the transcript, and this slot starts over.
        if (bubble.Settled > text.Length || !text.StartsWith(bubble.SettledSource, StringComparison.Ordinal))
        {
            bubble.Prose.Children.Clear();
            bubble.Settled = 0;
            bubble.SettledSource = "";
            bubble.OpenBlockSource = "";
        }

        var settled = MarkdownMessageRenderer.SettlePoint(text);
        var force = false;
        if (settled > bubble.Settled)
        {
            // The open block's slot is about to be filled by the finished version of itself. Whatever the repair
            // had guessed at is discarded and the real text, closed or not, is laid down once.
            DropOpenBlock(bubble);
            var finished = text[bubble.Settled..settled];
            bubble.Prose.Children.Add(MarkdownMessageRenderer.Render(finished, quote));
            bubble.Builds++;
            bubble.Settled = settled;
            bubble.SettledSource += finished;
            force = true;
        }

        var open = text[bubble.Settled..];
        if (open == bubble.OpenBlockSource) return;
        // The throttle bounds how often a block that is <i>still growing</i> gets rebuilt. An empty remainder is a
        // removal, and a removal builds nothing, so it never waits: holding it back for one window left the last
        // segment's sentence painted in the bubble beside the transcript row that had just taken it over, which
        // read as the model saying the same thing twice — and since a parked tool emits no further delta, nothing
        // came after to correct it until the next round spoke.
        if (open.Length > 0 && !force && bubble.OpenBlockSource.Length > 0
            && Environment.TickCount64 - bubble.OpenBlockPaintedAt < OpenBlockMilliseconds) return;

        DropOpenBlock(bubble);
        if (open.Length > 0)
        {
            bubble.Prose.Children.Add(
                MarkdownMessageRenderer.Render(MarkdownMessageRenderer.CloseOpenConstructs(open), quote));
            bubble.Builds++;
        }
        bubble.OpenBlockSource = open;
        bubble.OpenBlockPaintedAt = Environment.TickCount64;
    }

    /// <summary>Takes the still-being-typed block out of the stack. It is always the last child when there is
    /// one, because finished blocks only ever go underneath nothing.</summary>
    private static void DropOpenBlock(LiveBubble bubble)
    {
        if (bubble.OpenBlockSource.Length == 0 || bubble.Prose.Children.Count == 0) return;
        bubble.Prose.Children.RemoveAt(bubble.Prose.Children.Count - 1);
        bubble.OpenBlockSource = "";
    }

    /// <summary>Takes a thinking body to its newest line, after this frame has measured it. Posted rather than
    /// done inline because the text was assigned a moment ago and the extent is not known until layout runs — the
    /// same reason the transcript's own follow is posted at the same priority. The two then land in one pass
    /// instead of racing over it, and neither one re-measures anything the frame has not measured already.</summary>
    private static void FollowNewestLine(ScrollViewer scroll)
        => Dispatcher.UIThread.Post(
            () => scroll.Offset = new Point(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)),
            DispatcherPriority.Background);

    /// <summary>Paints the reply the deferred ticks were holding back, once it is safe to re-arrange the column:
    /// the composer just lost focus (so no composition is open) or the live row is being torn down.</summary>
    private void FlushLiveRepaintIfPending()
    {
        if (!_liveRepaintPending) return;
        _liveRepaintPending = false;
        if (_live is { } live) RefreshLive(live);
    }

    /// <summary>
    /// Puts the glyph in the phase the run is actually in, and hides it entirely once prose is arriving or the
    /// run is parked: the prose and the decision host are already saying those two, and a waiting symbol
    /// beside them would be chrome repeating a fact.
    /// </summary>
    private static void RefreshGlyphPhase(LiveBubble bubble)
    {
        var phase = bubble.ShowedText
            ? ActivityPhase.ShowingText
            : bubble.ToolRunning
                ? ActivityPhase.RunningTool
                : ActivityPhase.WaitingFirstToken;

        // One call sets the phase and the visibility and re-arms the clock together, so a refresh that only
        // confirms "still waiting" cannot leave the glyph visible but frozen (see ActivityGlyph.ApplyPhase).
        bubble.Glyph.ApplyPhase(phase);
    }

    /// <summary>Says how a run ended, on the transcript of the session that ended — not on whichever one happens
    /// to be on screen. The reason is recorded where it happened rather than inferred here from exception types,
    /// which is how "the user pressed stop" and "the stream stalled" used to blur together.</summary>
    private void AppendRunNotice(string conversationId, RunOutcome outcome)
    {
        // Which endings are worth asking for again is one rule, read the same way here and when a stored line is
        // painted back (<see cref="NoticeIsRetryable"/>).
        if (outcome.NoticeKey is { } key)
            RememberNotice(conversationId, HubStrings.Get(key) + (outcome.Detail ?? ""),
                outcome.NoticeDanger, outcome.UserTurns, NoticeIsRetryable(key),
                // The run that just ended has already left the registry, so this reads null: the line belongs to a
                // round that is over, and the next one to appear retires it however that round began.
                fromRun: true, run: _chat.RunFor(conversationId));

        // A session that finished out of sight is filed and left alone: repainting or scrolling it would move
        // the reading position of the conversation the person is actually looking at.
        if (conversationId != _chat.ViewedConversationId) return;
        RenderNotice();
        ScrollToEnd();
    }

    private ConversationRun? ViewedRun
        => _chat.ActiveConversation is { } viewed ? _chat.RunFor(viewed.Id) : null;

    private bool IsViewedStreaming => ViewedRun is { IsStreaming: true };

    // ───────────────────────── Scrolling ─────────────────────────

    private void ScrollToEnd()
    {
        _stickToBottom = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                // Tested here as well as at the call sites: the post runs after a hand may already be on the
                // thumb, and a queued jump is exactly the scroll that used to pull the thumb out from under the
                // mouse mid-drag.
                if (_thumbDragging) return;
                MessageScroller.ScrollToEnd();
                UpdateScrollAffordance();
            },
            DispatcherPriority.Background);
    }

    private void ScrollToEndIfSticky()
    {
        if (!_stickToBottom || _thumbDragging) return;
        Dispatcher.UIThread.Post(
            () =>
            {
                // Only the hand may veto a queued landing. The stick flag itself is recomputed from the view's
                // position on every ScrollChanged, so testing it here would cancel the very "land on the newest
                // line" this posts: the switch that queued it has already moved the view, and the flag reads
                // false until the landing lands.
                if (_thumbDragging) return;
                MessageScroller.ScrollToEnd();
            },
            DispatcherPriority.Background);
    }

    private void UpdateScrollAffordance()
    {
        var extent = MessageScroller.Extent.Height;
        var viewport = MessageScroller.Viewport.Height;
        var atBottom = MessageScroller.Offset.Y + viewport >= extent - StickEpsilon;
        _stickToBottom = atBottom;
        var scrollable = extent > viewport + StickEpsilon;
        ScrollToBottomButton.IsVisible = scrollable && !atBottom;
        // The band's height is reserved by whether the transcript <i>can</i> scroll, never by where the reading
        // position happens to have stopped. The plate inside it does appear and disappear with the reading position,
        // and measured without the reservation the row grows from 22 to 28 as it rises — retaking from the
        // transcript the height a person is reading through, every time the bottom edge is crossed. Reserving on
        // capacity pays for that once, when content first overflows, and never again while the session is read.
        ComposerBand.MinHeight = scrollable && !double.IsNaN(ScrollToBottomButton.Height)
            ? ScrollToBottomButton.Height
            : 0;
    }

    private void UpdateMessageColumnWidth()
    {
        // Hub's ScrollBar is opaque, always visible once the transcript overflows, and painted over the content
        // it belongs to rather than given a column of its own (Theme/HubControls.axaml; InspectorPanel and
        // MarkdownMessageRenderer clear it the same way on their own surfaces). A column measured against the raw
        // viewport therefore keeps its last bar-width — the user pill's tail, an activity row's trailing chip —
        // behind the track. Measure the strip the reader can see instead; the inset is the same on both sides, so
        // the column stays concentric with the composer (ChatPanel.axaml carries the matching gutter).
        var viewportWidth = MessageScroller.Viewport.Width;
        if (viewportWidth <= 0) return;

        var width = Math.Min(MessageColumn.MaxWidth,
            Math.Max(0, viewportWidth - (2 * HubMetrics.ScrollBarThickness)));
        if (double.IsNaN(MessageColumn.Width) || Math.Abs(MessageColumn.Width - width) > 0.1)
            MessageColumn.Width = width;
    }

    // ───────────────────────── Self-check hooks ─────────────────────────

    /// <summary>Message rows are Borders carrying the message-row class (both row kinds, since the container
    /// was unified); the class is what the hover-reveal rules and every row-counting check key on. Both machine
    /// containers are entered — a fold, and the flat column a narrated call is painted into — because a row the
    /// person can read without clicking anything is a row of the transcript whatever element happens to hold it.</summary>
    private static bool IsMachinePanel(Control child)
        => child.Classes.Contains("activity-group") || child.Classes.Contains("activity-plain");

    private IEnumerable<Control> MessageRows
        => MessageFlow.Children.SelectMany(child => IsMachinePanel(child)
            ? child.GetLogicalDescendants().OfType<Control>().Where(row => row.Classes.Contains("message-row"))
            : Enumerable.Repeat(child, 1).Where(row => row.Classes.Contains("message-row")));

    internal string FlowText
    {
        get
        {
            // Every text block (plain bodies plus the Markdown viewer's CTextBlock descendants) in order…
            var blocks = MessageRows
                .SelectMany(row => row.GetLogicalDescendants().OfType<TextBlock>())
                .Select(block => block.Text ?? "")
                .Where(text => text.Length > 0);

            // …plus the raw markdown carried as each viewer's Tag, so fenced code text is present too.
            var rawMarkdown = MessageRows
                .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
                .Select(viewer => viewer.Tag as string ?? "")
                .Where(text => text.Length > 0);

            return string.Join("\n", blocks.Concat(rawMarkdown));
        }
    }

    /// <summary>What one painted row says: its text blocks in order, plus the raw markdown carried as a viewer's
    /// Tag so a fenced document can name the row it renders.</summary>
    private static string RowText(Control row)
        => string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>()
            .Select(block => block.Text ?? "").Where(text => text.Length > 0)
            .Concat(row.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
                .Select(viewer => viewer.Tag as string ?? ""))
            .Where(text => text.Length > 0));

    /// <summary>One entry per painted row, holding the texts that row shows (a Markdown layer contributes the
    /// document it rendered, so a row can be named by what it says). The incremental renderer's whole claim is
    /// that each turn is painted once, in order — and that is only checkable from outside as a list of rows.</summary>
    internal IReadOnlyList<string> PaintedRowsForCheck => MessageRows.Select(RowText).ToArray();

    /// <summary>The texts of every message row that is on screen <b>without the person opening anything</b>: prose
    /// in the flow, and the rows of a flat piece. Rows inside a fold are absent by design, which is what makes this
    /// the read that separates "the model said it" from "the model said it, then hid it behind a chevron" — the
    /// second of those used to be indistinguishable from returning nothing at all.</summary>
    internal IReadOnlyList<string> UnfoldedRowTextsForCheck => MessageFlow.Children
        .Where(child => !child.Classes.Contains("activity-group"))
        .SelectMany(child => child.Classes.Contains("activity-plain")
            ? child.GetLogicalDescendants().OfType<Control>().Where(row => row.Classes.Contains("message-row"))
            : Enumerable.Repeat(child, 1).Where(row => row.Classes.Contains("message-row")))
        .Select(RowText)
        .ToArray();

    // ── the folded activity runs and the live thinking glyph ──

    /// <summary>How many activity runs the transcript folded into a collapsible group. One run of tool calls and
    /// thinking is one head, so this counts heads, not turns.</summary>
    internal int ActivityGroupCountForCheck
        => MessageFlow.Children.Count(child => child.Classes.Contains("activity-group"));

    /// <summary>The Nth group's head sentence: the tool count when there were calls, otherwise the measured
    /// thinking duration — "执行工具 0 次" is a count of nothing, so a thought-only run says how long it thought.</summary>
    internal string ActivityGroupTitleForCheck(int index)
        => ActivityGroupAt(index)?.GetLogicalDescendants().OfType<ToggleButton>()
            .FirstOrDefault(button => button.Classes.Contains("activity-group-head"))
            ?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "";

    internal bool ActivityGroupExpandedForCheck(int index)
        => ActivityGroupBodyAt(index) is { IsVisible: true };

    /// <summary>How many of the flow's aggregate counts are pressable. Zero would mean the number is back to
    /// being decoration — the state the pane existed to escape and no build error would ever report.</summary>
    internal int RunDiffChipCountForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Button>()
            .Count(button => button.Classes.Contains("file-chip-action"));

    /// <summary>Whether every chip hangs under a group head. This is the fact that makes "pressing the count does
    /// not fold the run" a statement about the tree rather than about one fixture: the chip is the head's own
    /// descendant, so the button taking the press is what keeps the reader's fold.</summary>
    internal bool RunDiffChipsInsideGroupHeadsForCheck
    {
        get
        {
            var chips = MessageFlow.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("file-chip-action")).ToList();
            return chips.Count > 0 && chips.All(chip => chip.GetLogicalAncestors()
                .OfType<ToggleButton>().Any(head => head.Classes.Contains("activity-group-head")));
        }
    }

    /// <summary>Whether the band above the composer is showing its tally at all. A plate that is present in the
    /// tree but never made visible would satisfy a scan of the transcript and tell nobody anything on screen.</summary>
    internal bool ChangesPillVisibleForCheck => ChangesPillHost.IsVisible;

    /// <summary>The whole line on the plate: the file count and the two numbers, joined as they read.</summary>
    internal string ChangesPillTextForCheck
        => ChangesPillHost.Child is Button { Content: Border chip }
            ? string.Join(" ", chip.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text))
            : "";

    /// <summary>Presses the plate through its own click event. Like the run's chip, the fact being asserted is that
    /// a press reaches the pane rather than that a pane could be opened by hand.</summary>
    internal bool ClickChangesPillForCheck()
    {
        if (ChangesPillHost.Child is not Button pill) return false;
        pill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>Whether the transcript holds more than one screen at all. A fixture that wants the way back down to
    /// rise has to be able to say it overflowed the page first: every condition that reads "at the bottom and no
    /// plate" is also true of a transcript that never needed a plate, and a check built on that passes in silence.</summary>
    internal bool TranscriptScrollableForCheck
        => MessageScroller.Extent.Height > MessageScroller.Viewport.Height + StickEpsilon;

    /// <summary>How far the transcript can be read past one screen — the whole travel of its reading position.
    /// Exposed next to <see cref="TranscriptScrollableForCheck"/> because "it can scroll" is not enough for a
    /// fixture that means to leave the position mid-way: a scroll asked for in pixels is clamped to this number, so
    /// anything asking for more than the range lands at the bottom instead, and the plate that is supposed to rise
    /// never does.</summary>
    internal double TranscriptScrollRangeForCheck
        => Math.Max(0, MessageScroller.Extent.Height - MessageScroller.Viewport.Height);

    /// <summary>The transcript's own scroller, for the thumb-drag checks. The drag is asserted on the real
    /// Track/Thumb the ScrollBar template builds — a stand-in would measure its own arithmetic instead of the
    /// scrollbar a hand actually touches.</summary>
    internal ScrollViewer TranscriptScrollerForCheck => MessageScroller;

    /// <summary>Whether the framework still considers the scrollbar's thumb held. The drag checks read it into
    /// their per-step record because "the thumb did not move" and "the thumb moved and something pulled it back"
    /// are different bugs, and this is the line that tells them apart.</summary>
    internal bool TranscriptThumbDraggingForCheck => _thumbDragging;

    /// <summary>How tall the band's row actually is. Zero is the claim that an empty thing does not take a row;
    /// the plate's own height is the claim that a reserved row is what stopped the composer from jumping.</summary>
    internal double ChangesPillRowHeightForCheck => ComposerBand.Bounds.Height;

    /// <summary>The plate's bottom edge in the input frame's own coordinates, or null when it is not showing. The
    /// same measurement <see cref="DecisionHostPlacementForCheck"/> makes for a question: a number at or above the
    /// frame's top edge would mean the band had moved into the box it is supposed to sit above.</summary>
    internal double? ChangesPillBottomForCheck
        => !ChangesPillHost.IsVisible ? null
            : ChangesPillHost.TranslatePoint(new Point(0, ChangesPillHost.Bounds.Height), ComposerFrame)?.Y;

    /// <summary>Where the top edge of the input frame sits in the panel's own coordinates. Read before and after a
    /// scroll, this is the only way to assert that scrolling leaves the composer where it was: a bounds check on
    /// the band would pass while the frame still moved, because both are laid out by the same grid.</summary>
    internal double ComposerFrameTopForCheck
        => ComposerFrame.TranslatePoint(new Point(0, 0), this) is { } top ? top.Y : double.NaN;

    /// <summary>The band's two plates measured as arranged rather than as intended: the tally's bottom edge in the
    /// input frame's coordinates, the vertical centre of each plate, the horizontal centre of the <i>pair</i>
    /// against the centre of the column the box is centered in, and the gap between them. Null unless both are up,
    /// because a claim about their arrangement is not a claim about one of them alone.</summary>
    internal (double PillBottom, double PillCenterY, double PlateCenterY, double BandCenterX, double FrameCenterX,
        double Gap)? BandPlatesForCheck
    {
        get
        {
            if (!ChangesPillHost.IsVisible || !ScrollToBottomButton.IsVisible) return null;
            // Each edge translated into the frame's own coordinates, the way the decision host is measured: Bounds
            // are relative to whatever panel laid the control out, and the two plates do not share the frame the
            // claim is about. The gap is the one measurement that may stay in the row's own coordinates, because
            // both plates are laid out by the same StackPanel.
            if (ChangesPillHost.TranslatePoint(new Point(0, ChangesPillHost.Bounds.Height), ComposerFrame)
                    is not { } pillBottom) return null;
            if (ChangesPillHost.TranslatePoint(new Point(ChangesPillHost.Bounds.Width / 2, ChangesPillHost.Bounds.Height / 2),
                    ComposerFrame) is not { } pillCentre) return null;
            if (ScrollToBottomButton.TranslatePoint(
                    new Point(ScrollToBottomButton.Bounds.Width / 2, ScrollToBottomButton.Bounds.Height / 2),
                    ComposerFrame) is not { } plateCentre) return null;
            if (ComposerBand.TranslatePoint(new Point(ComposerBand.Bounds.Width / 2, 0), ComposerFrame)
                    is not { } bandCentre) return null;
            return (pillBottom.Y, pillCentre.Y, plateCentre.Y, bandCentre.X, ComposerFrame.Bounds.Width / 2,
                ScrollToBottomButton.Bounds.Left - ChangesPillHost.Bounds.Right);
        }
    }

    /// <summary>The plate as painted, for the frame that proves it is not a layout that produced nothing.</summary>
    internal Control? ChangesPillForCheck => ChangesPillHost.IsVisible ? ChangesPillHost : null;

    /// <summary>The plate's own frame, reached through the class the pill is identified by rather than by a name —
    /// the two colour tokens at stake differ by one hex digit, and a check that found the wrong Border would say
    /// nothing about which one is drawn.</summary>
    internal Border? ChangesPillBorderForCheck
        => ChangesPillHost.GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("changes-pill"));

    /// <summary>Presses one chip through its own click event and says whether there was a chip to press. A raised
    /// Click is precisely what a ToggleButton ignores, so the fold state asserted next to this call can only be
    /// explained by the wiring — not by a check that never touched the head.</summary>
    internal bool ClickRunDiffChipForCheck(int index)
    {
        if (MessageFlow.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("file-chip-action"))
                .ElementAtOrDefault(index) is not { } chip)
            return false;
        chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>The text a chip carries, joined: the two counts the reader pressed. Read as text so a cell can
    /// prove the pane opened from <i>this</i> run's number rather than from some other group's.</summary>
    internal string RunDiffChipTextForCheck(int index)
        => MessageFlow.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("file-chip-action"))
            .ElementAtOrDefault(index) is { } chip
            ? string.Join(" ", chip.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? ""))
            : "";

    /// <summary>Which group control the Nth fold is painted from, as an instance token. A run that grows its own
    /// group leaves the token alone; a run that tears the flow down and rebuilds it hands back a fresh number even
    /// when the restored head looks identical — and the person who had it open would see it close.</summary>
    internal int ActivityGroupInstanceTokenForCheck(int index)
        => ActivityGroupAt(index) is { } group
            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group)
            : 0;

    /// <summary>The description text of every folded action row, in order. A tool exchange now renders as one
    /// <c>activity-row</c> (not a message-row), so this — not <c>PaintedRowsForCheck</c> — is what a check reads to
    /// prove each call produced exactly one row and a late-inserted result rebuilt rather than duplicated it.</summary>
    internal string[] ActivityRowsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("activity-row"))
            .Select(grid => grid.GetLogicalDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("activity-row-text"))?.Text ?? "")
            .ToArray();

    /// <summary>The address chips drawn under a reply that the endpoint searched for, in document order. Empty
    /// unless a search was reported — which is what makes the negative case assertable: an answer nobody looked
    /// anything up for must not grow a source line.</summary>
    internal string[] SearchChipsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("source-chip"))
            .Select(border => border.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "")
            .ToArray();

    /// <summary>The full addresses behind those chips, in the same order — the chip shows a host and the tooltip
    /// carries the URL, and a check has to be able to tell "shows the host" from "sent the wrong address".</summary>
    internal string[] SearchChipTargetsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("source-chip"))
            .Select(border => ToolTip.GetTip(border) as string ?? "")
            .ToArray();

    /// <summary>The queries the reply reports, from the tooltip on its source line — the part of the record that
    /// is deliberately one hover away rather than a paragraph over the answer.</summary>
    internal string[] SearchTooltipsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<StackPanel>()
            .Where(panel => panel.GetLogicalChildren().OfType<TextBlock>()
                .Any(text => text.Classes.Contains("muted")
                             && text.Text == HubStrings.Get("MessageSources")))
            .Select(panel => ToolTip.GetTip(panel) as string ?? "")
            .ToArray();

    /// <summary>The addresses the source line could not afford, read off its 「+N」 fold's tooltip. A check that
    /// wants to prove folding loses nothing has to read the folded list rather than count chips and hope.</summary>
    internal string SearchOverflowTipForCheck
        => MessageFlow.GetLogicalDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("source-fold"))
            .Select(text => ToolTip.GetTip(text) as string ?? "")
            .FirstOrDefault() ?? "";

    /// <summary>How many buttons live inside action rows — must stay zero: a tool exchange is not a readable
    /// message, and acting on one half of a call/result pair orphans the other, so the folded row carries none.</summary>
    internal int ActivityRowActionCountForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("activity-row"))
            .Sum(grid => grid.GetLogicalDescendants().OfType<Button>().Count());

    // ── the per-row detail each folded action reveals on click ──
    // A shell holds one toggle and one detail panel, created together and in the same order, so the Nth toggle pairs
    // with the Nth detail by index. The detail's IsVisible is what the toggle's IsCheckedChanged drives, so the
    // expanded state is read off the panel rather than guessed from the button.

    internal int ActivityRowToggleCountForCheck
        => MessageFlow.GetLogicalDescendants().OfType<ToggleButton>()
            .Count(button => button.Classes.Contains("activity-row-toggle"));

    internal bool ActivityRowExpandedForCheck(int index)
        => ActivityRowDetailAt(index) is { IsVisible: true };

    /// <summary>Opens/closes a row's detail the way a person does — by setting the toggle's IsChecked, which is what
    /// the IsCheckedChanged wiring reads. A raised Click never flips a ToggleButton, so a check must not use one.</summary>
    internal void SetActivityRowExpandedForCheck(int index, bool expanded)
    {
        var toggle = MessageFlow.GetLogicalDescendants().OfType<ToggleButton>()
            .Where(button => button.Classes.Contains("activity-row-toggle"))
            .ElementAtOrDefault(index);
        if (toggle is not null) toggle.IsChecked = expanded;
    }

    /// <summary>The whole text a row's detail panel shows (labels, the formatted arguments, the payload), joined so a
    /// check can prove both the full command and the returned line are present once the row is opened.</summary>
    internal string ActivityRowDetailTextForCheck(int index)
        => ActivityRowDetailAt(index) is { } detail
            ? string.Join(" ~ ", detail.GetLogicalDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "").Where(text => text.Length > 0))
            : "";

    private Border? ActivityRowDetailAt(int index)
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("activity-detail"))
            .ElementAtOrDefault(index);

    /// <summary>The hover text a collapsed action row carries. It has to come back empty: the row used to hang the
    /// whole tool payload there, which for a session transcript is tens of thousands of characters no tooltip can
    /// show. The detail panel below is that payload's only surface.</summary>
    internal string ActivityRowTooltipForCheck(int index)
        => MessageFlow.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("activity-row"))
            .ElementAtOrDefault(index) is { } row
            ? ToolTip.GetTip(row) as string ?? ""
            : "";

    /// <summary>Whether the Nth row's detail actually scrolls — the opened panel is bounded, so proof that a long
    /// payload is reachable is that its content extends past what the box shows. A scroller handed infinite height
    /// (no bound of its own) reports a viewport as tall as its content, and this says false for it.</summary>
    internal bool ActivityRowDetailScrollsForCheck(int index)
        => ActivityRowDetailAt(index) is { } detail
            && detail.GetLogicalDescendants().OfType<ScrollViewer>()
                .Any(scroll => scroll.Extent.Height > scroll.Viewport.Height + 1);

    // ── the left-slot expander arrow: where it sits, what glyph it is, and the status it swaps with ──

    private IEnumerable<Avalonia.Controls.Shapes.Path> ActivityRowChevronsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .Where(path => path.Classes.Contains("activity-row-chevron"));

    /// <summary>One arrow per action row — the affordance lives on the left, not stranded on the right edge.</summary>
    internal int ActivityRowChevronCountForCheck => ActivityRowChevronsForCheck.Count();

    /// <summary>The grid column the Nth row's arrow sits in: 0 (the status slot) is the whole point of the move.</summary>
    internal int ActivityRowChevronColumnForCheck(int index)
        => ActivityRowChevronsForCheck.ElementAtOrDefault(index) is { } chevron
            ? Grid.GetColumn(chevron)
            : -1;

    /// <summary>Whether the arrow is the sidebar's own glyph, so the chat's expander reads as the same affordance as
    /// the workspace groups in the left rail rather than a look-alike.</summary>
    internal bool ActivityRowChevronUsesSidebarGlyphForCheck
        => ActivityRowChevronsForCheck.Any(path => ReferenceEquals(path.Data, ThemeGeometry("Hub.Icon.ChevronRight")));

    /// <summary>The first row's status glyph size — asserted small so the ✓/✕ stay quiet beside the 10px
    /// expander arrow that shares their slot.</summary>
    internal double ActivityRowStatusSizeForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Classes.Contains("activity-status"))?.Width ?? -1;

    /// <summary>The status marks on rows that have no verdict yet. The <c>running</c> class is what says so: a
    /// finished row carries the same slot with a tick or a cross in it.</summary>
    private IEnumerable<Avalonia.Controls.Shapes.Path> RunningRowStatusPathsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("activity-row") && grid.Classes.Contains("running"))
            .SelectMany(grid => grid.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>())
            .Where(path => path.Classes.Contains("activity-status"));

    /// <summary>Whether a row with no verdict yet wears the gap-topped in-progress ring — and, the half that
    /// actually pins the regression, whether the regenerate arrow is gone from it. Hub.Icon.Refresh is the
    /// regenerate button's face in this same panel, so a row nobody can press must not wear it; identity rather
    /// than shape is what this asserts, because a look-alike arc would pass a shape test.</summary>
    internal bool ActivityRowRunningGlyphForCheck
    {
        get
        {
            var marks = RunningRowStatusPathsForCheck.ToList();
            return marks.Count > 0
                && marks.All(mark => ReferenceEquals(mark.Data, ThemeGeometry("Hub.Icon.InProgress")))
                && marks.All(mark => !ReferenceEquals(mark.Data, ThemeGeometry("Hub.Icon.Refresh")));
        }
    }

    // ── where a row's trailing piece sits: beside its sentence, or pinned to the column's right edge ──

    private Grid? ActivityRowGridAt(int index)
        => MessageFlow.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("activity-row"))
            .ElementAtOrDefault(index);

    /// <summary>The grid column the Nth row's command preview was laid into: the one after the verb, not the one a
    /// star-sized label could push to the far side.</summary>
    internal int ActivityRowChipColumnForCheck(int index)
        => ActivityRowGridAt(index)?.GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("inline-chip")) is { } chip
            ? Grid.GetColumn(chip)
            : -1;

    // ── the command card, as a check can see it ──
    // The card is not a turn: it is on the page only while a call is working, so the check that owns this must
    // read it through the panel rather than through the transcript.

    internal bool RunningCommandRowShownForCheck
        => _runningCommand is { } card && MessageFlow.Children.Contains(card.Shell);

    /// <summary>Everything the row's collapsed line says, joined: the verb, the command chip, the state and the
    /// clock, in the order they read.</summary>
    internal string RunningCommandRowTextForCheck => _runningCommand is { } card
        ? string.Join(" · ", card.Shell.GetLogicalDescendants().OfType<TextBlock>()
            .Select(text => text.Text ?? "").Where(text => text.Length > 0))
        : "";

    internal string RunningCommandOutputForCheck => _runningCommand?.Output.Text ?? "";

    internal bool RunningCommandExpandedForCheck => _runningCommand?.Detail.IsVisible == true;

    internal void SetRunningCommandExpandedForCheck(bool expanded)
    {
        if (_runningCommand is { } card) card.Toggle.IsChecked = expanded;
    }

    /// <summary>The exit code a finished command reports, in the order the rows were drawn. Empty unless a command
    /// ended by itself and the number reached the turn.</summary>
    internal string[] ActivityRowExitChipsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("exit-chip"))
            .Select(border => border.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "")
            .ToArray();

    /// <summary>The small trailing facts a row carries beside its sentence — a running command's state and clock,
    /// a finished one's duration — in document order.</summary>
    internal string[] ActivityRowMetaForCheck
        => MessageFlow.GetLogicalDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("activity-row-meta"))
            .Select(text => text.Text ?? "")
            .ToArray();

    /// <summary>The gap between a piece of trailing text and the sentence it belongs to, and the empty row still
    /// left to its right. Read as a pair, because neither half separates the layouts on its own: a pinned chip has
    /// no slack and a wide gap, a squeezed one has no slack and a narrow gap, and only the inline one has both
    /// numbers on the side that says "this is part of the line".</summary>
    internal (double Gap, double RightSlack)? ActivityRowTrailingGapForCheck(int index)
    {
        if (ActivityRowGridAt(index) is not { } row) return null;
        var label = row.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("activity-row-text"));
        var chip = row.GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("inline-chip"));
        return TrailingGap(row, label, chip);
    }

    /// <summary>The same pair for a group head: its sentence and the aggregate count a person presses.</summary>
    internal (double Gap, double RightSlack)? ActivityGroupHeadTrailingGapForCheck(int index)
    {
        if (ActivityGroupAt(index) is not { } group) return null;
        var head = group.GetLogicalDescendants().OfType<ToggleButton>()
            .FirstOrDefault(button => button.Classes.Contains("activity-group-head"));
        var grid = head?.GetLogicalDescendants().OfType<Grid>().FirstOrDefault();
        return TrailingGap(grid, grid?.Children.OfType<TextBlock>().FirstOrDefault(),
            grid?.Children.OfType<Button>().FirstOrDefault());
    }

    private static (double Gap, double RightSlack)? TrailingGap(Control? container, TextBlock? label, Control? chip)
    {
        if (container is null || label is null || chip is null) return null;
        var chipLeft = chip.TranslatePoint(new Point(0, 0), container)?.X ?? double.NaN;
        // The ink, not the box: a TextBlock left in a star column is stretched to it, so its Bounds would end where
        // the chip begins and report a tidy gap for a layout that pinned the chip across the column.
        var labelRight = label.TranslatePoint(new Point(label.DesiredSize.Width, 0), container)?.X ?? double.NaN;
        if (double.IsNaN(chipLeft) || double.IsNaN(labelRight)) return null;
        return (chipLeft - labelRight, container.Bounds.Width - chipLeft - chip.Bounds.Width);
    }

    /// <summary>Whether the group head borrows the sidebar's Expander header theme. That theme is what keeps a
    /// checked head from painting the Fluent accent plate — the purple pill — and gives it the shared left arrow.</summary>
    internal bool ActivityGroupHeadUsesExpanderForCheck
    {
        get
        {
            var head = MessageFlow.GetLogicalDescendants().OfType<ToggleButton>()
                .FirstOrDefault(button => button.Classes.Contains("activity-group-head"));
            if (head?.Theme is null) return false;
            var app = Application.Current;
            return app is not null
                   && app.TryGetResource("HubExpanderHeader", app.ActualThemeVariant, out var theme)
                   && ReferenceEquals(head.Theme, theme);
        }
    }

    /// <summary>Folds/unfolds a group the way a person does — by setting the head's IsChecked, which is what the
    /// IsCheckedChanged wiring reads. A raised Click never flips a ToggleButton, so a check must not use one.</summary>
    internal void SetActivityGroupExpandedForCheck(int index, bool expanded)
    {
        var head = ActivityGroupAt(index)?.GetLogicalDescendants().OfType<ToggleButton>()
            .FirstOrDefault(button => button.Classes.Contains("activity-group-head"));
        if (head is not null) head.IsChecked = expanded;
    }

    private Control? ActivityGroupAt(int index)
        => MessageFlow.Children.Where(child => child.Classes.Contains("activity-group"))
            .ElementAtOrDefault(index);

    private StackPanel? ActivityGroupBodyAt(int index)
        => ActivityGroupAt(index)?.GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Name == "ActivityGroupBody");

    /// <summary>The live bubble's glyph: which phase it is morphing through and whether its clock is running.
    /// A glyph stuck in one shape while a tool runs is the bug these read.</summary>
    internal ActivityPhase LiveGlyphPhaseForCheck => _live?.Glyph.Phase ?? ActivityPhase.ShowingText;
    internal bool LiveGlyphTickingForCheck => _live?.Glyph.IsTickingForCheck ?? false;

    /// <summary>How many times the live bubble's 350ms tick has run. A check waits on this to prove the tick that
    /// drives both the elapsed label and the glyph's phase has actually fired, rather than reading the chrome the
    /// instant it was attached. The label cannot serve as that proof: it is seeded from the run, so a freshly
    /// attached bubble already reads whatever the answer has taken — which is exactly what a check switching
    /// sessions and back wants to assert, not what it wants to wait on.</summary>
    internal int LiveFrameForCheck => _live?.Frame ?? 0;
    internal bool LiveGlyphVisibleForCheck => _live?.Glyph.IsVisible ?? false;

    internal bool HasVisibleMarkdownCodeBlock(string code)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => viewer.Markdown?.Contains(code, StringComparison.Ordinal) == true
                           && viewer.Bounds.Width > 0
                           && viewer.Bounds.Height > 0);

    /// <summary>The eager window itself, so a check can assert against the number the renderer uses rather than
    /// against a copy of it that would drift.</summary>
    internal static int EagerMarkdownLimitForCheck => EagerMarkdownLimit;

    /// <summary>How many rendered Markdown documents the transcript is holding. The claim this measures is not
    /// speed but shape: the expensive viewers have to be proportional to what a person can see, not to how long
    /// the session has been going.</summary>
    internal int MarkdownDocumentCountForCheck
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Count();

    /// <summary>Rows drawn as plain text although their turn holds Markdown — the ones no promotion pass has
    /// reached yet.</summary>
    internal int PlainTurnRowCountForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Count(border => border.Classes.Contains(PlainTurnClass));

    /// <summary>The first control in the flow, as an object. Identity rather than content, because what a check
    /// wants to say about coming back to a session is that the rows it left came back — not that new ones were
    /// drawn to look the same.</summary>
    internal object? FirstTranscriptRowForCheck => MessageFlow.Children.FirstOrDefault();

    /// <summary>How many turns a fresh paint lays down, so a check can assert against the number the renderer uses
    /// rather than against a copy of it that would drift.</summary>
    internal static int FirstPaintTurnsForCheck => FirstPaintTurns;

    /// <summary>Pins the window for a benchmark run, which otherwise inherits whatever width earlier samples or
    /// checks left behind and makes its own summaries incomparable.</summary>
    internal void SetPaintWindowForCheck(int turns)
    {
        _paintWindow = Math.Max(1, turns);
        _paintFromScratch = true;
        RenderMessages();
    }

    /// <summary>How many turns the flow is holding: the whole transcript when nobody is windowed, the newest
    /// stretch of it otherwise.</summary>
    internal int PaintedTurnCountForCheck => _renderedCount - _paintedFrom;

    /// <summary>Whether the flow opens with the line that stands for the turns above it.</summary>
    internal bool HasLoadEarlierRowForCheck => LoadEarlierRowForCheck() is not null;

    /// <summary>How tall that line is on screen. A count says the control is in the flow; this says it was laid
    /// out, which is the difference between a row and a style that resolved to nothing.</summary>
    internal double LoadEarlierRowHeightForCheck => LoadEarlierRowForCheck()?.Bounds.Height ?? 0;

    /// <summary>Presses that line the way a hand does, and answers whether there was one to press.</summary>
    internal bool ClickLoadEarlierForCheck()
    {
        if (LoadEarlierRowForCheck() is not Border { Child: Button button }) return false;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>The line itself, found by its class. It is deliberately not a message row, so nothing that counts
    /// rows sees it — which is also why it has to be looked up by name rather than by position.</summary>
    /// <summary>The conversation the shadow comparison reads: the projection is built from data, never from
    /// controls.</summary>
    internal Conversation? ActiveConversationForCheck => _chat?.ActiveConversation;

    internal bool ShowThinkingForCheck => _chat?.ShowThinking ?? false;

    /// <summary>What the flow actually holds, as one signature per top-level row, for the shadow comparison against
    /// <see cref="TranscriptProjection"/>. Fine enough that a behaviour change cannot hide inside it: the row kind,
    /// a plan card by name, whether a prose row holds Markdown, and inside every piece each child carrying the tool
    /// name its exchange was actually paired with.
    ///
    /// <para>The two <b>ephemeral tails</b> are left out, by object identity rather than by class: the live bubble
    /// and the running-command card are drawn from a live run, not from stored turns, so a projection of the
    /// conversation has nothing to say about them. Identity is the only safe filter — the command card
    /// deliberately wears the same classes as the finished row it becomes, so a class-based filter would silently
    /// drop a real row one day.</para></summary>
    internal long[] ProjectionKeysForCheck => _chat.ActiveConversation is { } conversation && _projection is not null
        ? Enumerable.Range(0, conversation.Messages.Count).Select(_projection.KeyAt).ToArray() : [];

    internal IReadOnlyList<string> PaintedStructureForCheck => MessageFlow.Children
        .Where(child => !ReferenceEquals(child, _live?.Row) && !ReferenceEquals(child, _runningCommand?.Shell))
        .Select(PaintedToken)
        .ToArray();

    private static string PaintedToken(Control child)
    {
        if (child.Classes.Contains("load-earlier-row")) return "earlier";
        if (IsMachinePanel(child))
        {
            var rows = PaintedPieceRows(child).Select(PaintedChildToken);
            return (child.Classes.Contains("activity-group") ? "P:folded{" : "P:flat{")
                   + string.Join(",", rows) + "}";
        }

        if (child.Classes.Contains("message-row"))
        {
            var plan = child.GetLogicalDescendants().OfType<Button>()
                .Any(button => button.Classes.Contains("plan-card"));
            var kind = plan ? "plan" : child.Classes.Contains("user-row") ? "user" : "assistant";
            return "M:" + kind + (HoldsMarkdown(child) ? ":md" : "");
        }

        if (child.Classes.Contains("notice")) return "N";
        return "other:" + string.Join("|", child.Classes);
    }

    /// <summary>The rows of one piece, in order, off the container they were laid into: a flat piece is its own
    /// panel, a folded one keeps them one level down inside its body.</summary>
    private static IEnumerable<Control> PaintedPieceRows(Control piece)
    {
        if (piece is StackPanel plain) return plain.Children;
        var body = piece.GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Name == "ActivityGroupBody");
        if (body is null) return [];
        return body.Children.OfType<StackPanel>().FirstOrDefault()?.Children ?? body.Children;
    }

    private static string PaintedChildToken(Control row)
    {
        // A thought row is an exchange shell holding an `activity-row` marked `thought`, so the marker is one level
        // down: checking the row itself reported every thinking row as an exchange with an empty tool name.
        if (row.Classes.Contains("thought")
            || row.GetLogicalDescendants().OfType<Control>().Any(inner => inner.Classes.Contains("thought")))
            return "Th";
        if (row.Classes.Contains("message-row")) return "C" + (HoldsMarkdown(row) ? ":md" : "");
        return "T:" + (row.Tag as string ?? "");
    }

    private static bool HoldsMarkdown(Control row)
        => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Any();

    /// <summary>The line itself, found by its class. It is deliberately not a message row, so nothing that counts
    /// rows sees it — which is also why it has to be looked up by name rather than by position.</summary>
    private Control? LoadEarlierRowForCheck()
        => MessageFlow.Children.FirstOrDefault(child => child.Classes.Contains("load-earlier-row"));

    /// <summary>The left indent a rendered list actually gets, or -1 when no document on screen held one.
    /// GithubLike stamps 40 onto every list grid and a nested list pays it again, so this is the only reading
    /// that tells the winning layer's answer from a style setter that silently did nothing.</summary>
    internal double MarkdownListIndentForCheck()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .SelectMany(viewer => viewer.GetVisualDescendants().OfType<Grid>())
            .FirstOrDefault(grid => grid.Classes.Contains("List"))?.Margin.Left ?? -1;

    /// <summary>The raw asterisks still drawn across every rendered answer on screen, as the reader sees them.
    /// Empty is the pass condition; anything in it names the case the engine failed to parse.</summary>
    internal IReadOnlyList<string> MarkdownLeakedEmphasisForCheck()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .SelectMany(MarkdownMessageRenderer.LeakedEmphasisRuns)
            .ToArray();

    /// <summary>Whether any Markdown viewer on screen was handed this text. Read as the negative it is: a leaked
    /// tool call has to reach the reader as plain text, and the one proof of that is that no viewer ever took
    /// it — because a viewer is exactly where its pipes turn into a table.</summary>
    internal bool HasMarkdownHolding(string text)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => viewer.Markdown?.Contains(text, StringComparison.Ordinal) == true);

    /// <summary>Whether a plain <c>turn-text</c> block still carries this text. Read as the negative it is: a body
    /// the Markdown pass took shows its source through a viewer's Tag, never as raw source text, so a
    /// <c>turn-text</c> block holding it is the proof that body never went through the pass.</summary>
    internal bool PlainTurnHoldsForCheck(string text)
        => MessageFlow.GetLogicalDescendants().OfType<TextBlock>()
            .Any(block => block.Classes.Contains("turn-text")
                          && (block.Text?.Contains(text, StringComparison.Ordinal) ?? false));

    internal bool HasRenderedMarkdownLink(string url)
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => MarkdownMessageRenderer.HasLinkHandler(viewer, url));

    internal bool HasThemedMarkdownLink()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedLink);

    internal bool HasSyntaxHighlightedCode(string language)
        => MessageFlow.GetLogicalDescendants().OfType<TextEditor>()
            .Any(editor => string.Equals(editor.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase)
                           && editor.SyntaxHighlighting is not null);

    /// <summary>What a fenced block was actually handed. The definition's name travels with the answer because
    /// yes-or-no cannot tell a palette that was lifted for the dark well from a language that has no definition
    /// at all — and the second one is fine, while the first is the bug this reads for.</summary>
    internal (bool Found, bool Resolved, bool Adapted, string Definition) DarkSyntaxForCheck(string language)
    {
        foreach (var editor in MessageFlow.GetLogicalDescendants().OfType<TextEditor>())
        {
            if (!string.Equals(editor.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase)) continue;
            return editor.SyntaxHighlighting is { } definition
                ? (true, true, DarkSyntax.IsAdapted(definition), definition.Name)
                : (true, false, false, "（无定义）");
        }

        return (false, false, false, "（没有这个代码块）");
    }

    internal bool HasMarkdownCopyToolbar()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasCopyToolbar);

    internal bool HasScrollableMarkdownTable()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasScrollableTable);

    internal bool HasThemedMarkdownTable()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedTable);

    internal bool HasThemedMarkdownDocument()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedDocument);

    /// <summary>Whether a rendered answer paints its selection band from the Hub token. Read per block, because
    /// that is where the band is drawn (inside <c>CTextBlock.Render</c>), and the library's fallback there is a
    /// hardcoded <c>Brushes.Cyan</c> — the colour this reading exists to rule out.</summary>
    internal bool HasThemedMarkdownSelection()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(MarkdownMessageRenderer.HasThemedSelection);

    /// <summary>Exercises click, internal drag and cross-boundary drag on the first inline code in the actual
    /// transcript, rather than substituting a renderer-only fixture for the message the reader sees.</summary>
    internal MarkdownMessageRenderer.InlineCodeProbe ProbeMarkdownChipSelection()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Select(MarkdownMessageRenderer.ProbeChipSelectionForCheck)
            .FirstOrDefault(probe => probe.Found);

    /// <summary>Whether the reply on screen carries the menu a selection earns, with both rows in the order a
    /// reader meets them.</summary>
    internal bool MarkdownSelectionMenuForCheck()
    {
        var expected = new[] { HubStrings.Get("CopyMessage"), HubStrings.Get("QuoteSelection") };
        return MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Any(viewer => SelectionMenu.HeadersForCheck(viewer).SequenceEqual(expected));
    }

    /// <summary>The same question for the person's own message, which carries no document to hang a menu on.</summary>
    internal bool UserTurnSelectionMenuForCheck()
    {
        var expected = new[] { HubStrings.Get("CopyMessage"), HubStrings.Get("QuoteSelection") };
        return MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("user-pill"))
            .SelectMany(pill => pill.GetLogicalDescendants().OfType<SelectableTextBlock>())
            .Any(text => text.Classes.Contains("turn-text")
                         && SelectionMenu.HeadersForCheck(text).SequenceEqual(expected));
    }

    /// <summary>The rows the reply on screen would offer, as text — the diagnosis half of the check above, so a
    /// red cell says which surface lost its menu rather than only that one of them did.</summary>
    internal string MarkdownSelectionMenuRowsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>()
            .Select(SelectionMenu.DescribeForCheck)
            .FirstOrDefault(rows => rows.Length > 0) ?? "";

    /// <summary>The same for the person's own message.</summary>
    internal string UserTurnSelectionMenuRowsForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("user-pill"))
            .SelectMany(pill => pill.GetLogicalDescendants().OfType<SelectableTextBlock>())
            .Where(text => text.Classes.Contains("turn-text"))
            .Select(SelectionMenu.DescribeForCheck)
            .FirstOrDefault(rows => rows.Length > 0) ?? "";

    /// <summary>Whether the composer's menu offers the three clipboard verbs — and whether it is the box's own
    /// <c>ContextFlyout</c>. The second half is not decoration: Avalonia clears a TextBox's selection when it
    /// loses focus unless its <c>ContextFlyout</c> or <c>ContextMenu</c> is the open one, and the menu's popup is
    /// what takes that focus — a menu opened by hand from an event handler made 复制/剪切 act on nothing.</summary>
    internal bool ComposerMenuForCheck()
        => InputBox.ContextFlyout is MenuFlyout
           && SelectionMenu.HeadersForCheck(InputBox)
               .SequenceEqual([HubStrings.Get("CutText"), HubStrings.Get("CopyMessage"), HubStrings.Get("PasteText")]);

    /// <summary>Selects across the reply's inline code chip the way a drag does, presses the menu's 引用 row the
    /// way a click does, and hands back what the composer holds afterwards — the whole path from a selection to a
    /// quoted draft, rather than the quoting rule on its own.</summary>
    internal string QuoteSelectionForCheck()
    {
        foreach (var viewer in MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
        {
            if (MarkdownMessageRenderer.SelectAcrossChip(viewer).Length == 0) continue;
            if (SelectionMenu.ItemForCheck(viewer, HubStrings.Get("QuoteSelection")) is not { } quote) return "";
            quote.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return InputBox.Text ?? "";
        }

        return "";
    }

    /// <summary>Whether the person's own message is drawn with text that can be selected at all, and paints the
    /// band from the same token as everything else. A bare <see cref="TextBlock"/> answers false to both, which
    /// is what the row used to be.</summary>
    internal bool UserTurnTextIsSelectableForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("user-pill"))
            .SelectMany(pill => pill.GetLogicalDescendants().OfType<SelectableTextBlock>())
            .Any(text => text.Classes.Contains("turn-text")
                         && MarkdownMessageRenderer.MatchesToken(text.SelectionBrush, "Hub.Selection"));

    /// <summary>A reading off the person's own message taken while its right-click menu is up: whether the menu
    /// opened, the text that was selected, what the block still holds, and what the two rows handed on.</summary>
    internal readonly record struct UserPillSelectionProbe(bool MenuOpen, string Picked, string SelectedText,
        string CopiedText, string Quoted);

    /// <summary>Selects the whole of the person's own message, asks for its menu the way a right-click does, takes
    /// the keyboard focus away from the block the way the menu's popup does, and presses both rows.
    ///
    /// <para>The range is set through the block's own properties rather than dragged out with pointer events: what
    /// this pins is what the menu does to a selection, not how a drag builds one. The focus move is explicit for a
    /// different reason — the popup takes the focus on a real desktop and may not under the check window, and the
    /// reading has to come back the same everywhere. It is exactly the event the bug was about: the block dropping
    /// its selection because something else took the focus.</para>
    ///
    /// <para>Everything is put back afterwards — the menu closed, the composer emptied and its focus restored, the
    /// band cleared — because a selection left on screen would be read by the pixel checks that follow.</para>
    /// </summary>
    internal UserPillSelectionProbe ProbeUserPillSelectionForCheck()
    {
        var block = MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("user-pill"))
            .SelectMany(pill => pill.GetLogicalDescendants().OfType<SelectableTextBlock>())
            .FirstOrDefault(text => text.Classes.Contains("turn-text") && (text.Text ?? "").Length > 0);
        if (block is null) return default;

        var composerHadFocus = InputBox.IsKeyboardFocusWithin;
        var picked = block.Text ?? "";
        block.Focus();
        block.SelectionStart = 0;
        block.SelectionEnd = picked.Length;

        // The request a right-click raises, not the menu opened by hand: what the reader presses is the request,
        // and the handler that answers it is the half that lost the selection.
        block.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        var menuOpen = SelectionMenu.IsOpenForCheck(block);

        // The popup's focus grab, made deterministic — see the summary.
        InputBox.Focus();
        Dispatcher.UIThread.RunJobs();
        var selected = block.SelectedText ?? "";

        // 复制 goes to a recorded string instead of the clipboard, the way the sidebar's copy is checked.
        var copied = "";
        SelectionMenu.TestClipboardWriter = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        try
        {
            SelectionMenu.ItemForCheck(block, HubStrings.Get("CopyMessage"))
                ?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            SelectionMenu.TestClipboardWriter = null;
        }

        SetInputForCheck("");
        SelectionMenu.ItemForCheck(block, HubStrings.Get("QuoteSelection"))
            ?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var quoted = InputBox.Text ?? "";

        SelectionMenu.HideForCheck(block);
        block.ClearSelection();
        SetInputForCheck("");
        if (composerHadFocus) FocusComposerForCheck();
        else MoveFocusOffComposerForCheck();
        Dispatcher.UIThread.RunJobs();
        return new UserPillSelectionProbe(menuOpen, picked, selected, copied, quoted);
    }

    internal void ApplyMarkdownSyntaxHighlightingForCheck()
    {
        foreach (var viewer in MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
        {
            MarkdownMessageRenderer.ApplySyntaxHighlighting(viewer);
        }
    }

    /// <summary>The selected model formatted as text — the model no longer has a dedicated label, but the
    /// composer chip and this value still expose it.</summary>
    internal string ActiveModelText => _chat.SelectedChatModel is { } choice
        ? string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HubStrings.Get("ActiveModelFormat"),
            choice.Provider.Name,
            choice.ModelName)
        : HubStrings.Get("NoAvailableChatModels");

    internal int ModelChoiceCount => _chat.AvailableChatModels.Count;
    internal string SelectedModelText => SelectedModelLabel.Text ?? "";

    internal int BubbleCount => MessageRows.Count();

    internal int MessageActionCount => MessageFlow.GetLogicalDescendants().OfType<Button>()
        .Count(button => button.Classes.Contains("message-action"));

    internal bool ScrollToBottomVisible => ScrollToBottomButton.IsVisible;

    /// <summary>The floating button's shape: a square plate, not the pill it used to be. Read off the
    /// properties rather than <c>Bounds</c> because the button is collapsed until the flow scrolls.</summary>
    internal bool ScrollToBottomIsSquarePlateForCheck
        => ScrollToBottomButton is { Width: 28, Height: 28, CornerRadius: { TopLeft: 8 } };

    /// <summary>Whether the button carries the down-arrow glyph, matched against the resource itself so a
    /// different arrow cannot pass.</summary>
    internal bool ScrollToBottomShowsArrowForCheck
    {
        get
        {
            if (ScrollToBottomButton.GetLogicalDescendants()
                    .OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault()?.Data is not { } data) return false;
            return Application.Current is { } app
                   && app.TryGetResource("Hub.Icon.ArrowDown", app.ActualThemeVariant, out var arrow)
                   && ReferenceEquals(data, arrow);
        }
    }

    /// <summary>The first rendered message row, so a check can prove a reload reuses it instead of rebuilding
    /// the whole flow (the incremental path's whole point).</summary>
    internal object? FirstBubbleForCheck => MessageRows.FirstOrDefault();

    // ── Approval card ──
    // Both kinds of decision are asked by the host, and the host is the one place a check looks: it is the one place
    // a decision is answered. It sits in its own row above the composer frame, so a question never covers the draft
    // that was being typed when it arrived. The transcript keeps a signpost line (class approval-pending-line)
    // which is deliberately NOT counted here — these accessors mean "actionable right now", and
    // FlowApprovalCardCountForCheck below is the negative half that proves the move happened.
    private IEnumerable<Border> ApprovalCards
        => DecisionHost.IsVisible && _decisionCallId is not null ? [DecisionHost] : [];

    private IEnumerable<Border> PlanApprovalCards
        => DecisionHost.IsVisible && _decisionPlanIndex >= 0 ? [DecisionHost] : [];

    /// <summary>The old walk, kept as the negative half of "the card moved": a transcript that still paints an
    /// actionable approval card is a regression this number catches.</summary>
    internal int FlowApprovalCardCountForCheck
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Border>())
            .Count(card => card.Classes.Contains("approval-card"));

    /// <summary>How many calls are asking. A request the user has to answer is never hover-gated — a hidden
    /// actionable request looks exactly like a reply that stopped working — so this reads the flow as painted.</summary>
    internal int PendingApprovalCardsForCheck => ApprovalCards.Count();

    internal Border? ApprovalCardForCheck => ApprovalCards.FirstOrDefault();

    internal bool ApprovalCardOnScreenForCheck
        => ApprovalCards.FirstOrDefault() is { IsVisible: true } card && card.Bounds is { Width: > 0, Height: > 0 };

    internal string ApprovalCardTextForCheck
        => ApprovalCards.FirstOrDefault() is { } card
            ? string.Join(" | ", card.GetLogicalDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "").Where(text => text.Length > 0))
            : "";

    internal string[] ApprovalCardActionsForCheck
        => ApprovalCards.FirstOrDefault() is { } card
            ? card.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("approval-action"))
                .Select(button => button.Tag as string ?? "").ToArray()
            : [];

    /// <summary>How many calls the card on screen lists as member rows. A card about a single call draws none,
    /// because its title is already that tool's name — so this reads zero for the one-call card and N for a batch,
    /// which is the only way to notice a batch that collapsed to its first call while still approving all of them.</summary>
    internal int ApprovalCardRowsForCheck
        => ApprovalCards.FirstOrDefault() is { } card
            ? card.GetLogicalDescendants().OfType<TextBlock>()
                .Count(block => block.Classes.Contains("approval-member"))
            : 0;

    /// <summary>Whether any of the card's buttons is drawn as a destructive act. Refusing a tool call is an
    /// ordinary answer to an ordinary question, and painting it as a detonation is how a permission UI ends up
    /// trained on saying yes.</summary>
    internal bool ApprovalCardActionsAreCalmForCheck
        => ApprovalCards.FirstOrDefault() is { } card
           && !card.GetLogicalDescendants().OfType<Button>().Any(button => button.Classes.Contains("destructive"));

    internal void ClickApprovalActionForCheck(string actionKey)
    {
        var button = ApprovalCards.FirstOrDefault()?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == actionKey);
        if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Presses the detail block's own toggle the way a pointer does: the control flips its state and then
    /// raises the click that reads it. Raising the click alone would leave the box unchecked and measure the
    /// collapsed card, which is not the state the ceiling is about.</summary>
    internal void ExpandApprovalDetailForCheck()
    {
        var toggle = ApprovalCards.FirstOrDefault()?
            .GetLogicalDescendants().OfType<ToggleButton>()
            .FirstOrDefault(candidate => candidate.Tag as string == "ChatApprovalExpand");
        if (toggle is null) return;
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
    }

    /// <summary>How far the lowest answer button hangs below the card's own bottom edge, in device-independent
    /// pixels; nothing below zero means every button is inside the clip. The host clips, so an expanded detail block
    /// that outgrows the ceiling hides 拒绝 and 允许 behind it — a question with no way to answer it.</summary>
    internal double ApprovalActionsOverflowForCheck
    {
        get
        {
            if (ApprovalCardForCheck is not { } card) return double.NaN;
            var worst = double.NegativeInfinity;
            foreach (var tag in new[] { "ApprovalDeny", "ApprovalAllow" })
            {
                var button = card.GetLogicalDescendants().OfType<Button>()
                    .FirstOrDefault(candidate => candidate.Tag as string == tag);
                if (button is null || button.TranslatePoint(new Point(0, button.Bounds.Height), card) is not { } bottom)
                    return double.NaN;
                worst = Math.Max(worst, bottom.Y - card.Bounds.Height);
            }
            return worst;
        }
    }

    /// <summary>The box the card used to cover. A decision is a question asked <i>above</i> the draft, so the draft
    /// keeps working while it waits: enabled, hit-testable, and in the tab order like any other moment.</summary>
    internal bool ComposerInputEnabledForCheck => InputBox.IsEnabled;

    internal bool DecisionHostFocusedForCheck => DecisionHost.IsKeyboardFocusWithin;

    /// <summary>The card as arranged, in the composer frame's own coordinates: its bottom edge (negative means above
    /// the frame's top, which is the whole promise) and its left edge against the frame's. A card that still painted
    /// over the input box would report a positive bottom; one that drifted out of the column reports an offset.</summary>
    internal (double BottomRelativeToFrame, double LeftOffsetFromFrame)? DecisionHostPlacementForCheck
    {
        get
        {
            if (!DecisionHost.IsVisible || DecisionHost.Bounds.Height <= 0) return null;
            if (DecisionHost.TranslatePoint(new Point(0, DecisionHost.Bounds.Height), ComposerFrame)
                is not { } bottom) return null;
            if (DecisionHost.TranslatePoint(new Point(0, 0), ComposerFrame) is not { } left) return null;
            return (bottom.Y, left.X);
        }
    }

    internal int PendingPlanApprovalCardsForCheck => PlanApprovalCards.Count();

    internal bool PlanApprovalCardOnScreenForCheck
        => PlanApprovalCards.FirstOrDefault() is { IsVisible: true } card
           && card.Bounds is { Width: > 0, Height: > 0 };

    internal string PlanApprovalMessageTextForCheck
        => string.Join(" ", MessageFlow.Children
            .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
            .Select(viewer => viewer.Tag as string ?? ""));

    internal bool PlanApprovalMarkdownOnScreenForCheck
        => MessageFlow.Children
            .SelectMany(row => row.GetLogicalDescendants().OfType<MarkdownScrollViewer>())
            .FirstOrDefault(viewer => (viewer.Tag as string)?.Contains("Reviewed plan", StringComparison.Ordinal) == true)
            is { IsVisible: true, Bounds: { Width: > 0, Height: > 0 } };

    // ── the plan's card in the transcript ──

    private IEnumerable<Button> PlanCards
        => MessageRows.SelectMany(row => row.GetLogicalDescendants().OfType<Button>())
            .Where(card => card.Classes.Contains("plan-card"));

    internal int PlanCardCountForCheck => PlanCards.Count();

    /// <summary>What each card actually reads, in transcript order: the label and the title, joined. The glyph is
    /// a Path and carries no text, so this string is the whole of what a person sees on the card.</summary>
    internal string[] PlanCardTextsForCheck
        => PlanCards.Select(card => string.Join(" · ", card.GetLogicalDescendants().OfType<TextBlock>()
            .Select(block => block.Text ?? "").Where(text => text.Length > 0))).ToArray();

    internal string[] PlanCardStateKeysForCheck
        => PlanCards.Select(card => card.Tag as string ?? "").ToArray();

    /// <summary>The negative half, and the point of the card: a plan row that still paints its body — as
    /// rendered Markdown, or as the plain block the Markdown pass replaces — is the transcript back to one
    /// document per message. Whether the body survives at all is not this assertion's business and is already
    /// pinned where the stored turn text and the approved instruction are compared.</summary>
    internal bool PlanBodiesSuppressedForCheck
        => MessageRows
            .Where(row => row.GetLogicalDescendants().OfType<Button>()
                .Any(card => card.Classes.Contains("plan-card")))
            .All(row => !row.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Any()
                         && !row.GetLogicalDescendants().OfType<TextBlock>()
                             .Any(text => text.Classes.Contains("turn-text")));

    /// <summary>Whether a card is drawn rather than merely in the tree: a zero-bounds card passes a count and is
    /// still invisible.</summary>
    internal bool PlanCardOnScreenForCheck(int index)
        => PlanCards.ElementAtOrDefault(index) is { IsVisible: true } card && card.Bounds is { Width: > 0, Height: > 0 };

    /// <summary>Presses the nth card (transcript order) through its own Click, so a check drives the wiring from
    /// the card into the shell's inspector instead of calling the shell and proving nothing.</summary>
    internal void ClickPlanCardForCheck(int index)
        => PlanCards.ElementAtOrDefault(index)
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The plan review's two lettered choices, in the order offered: A approves, B asks for a revision.
    /// There is deliberately no third "exit plan mode" row — the owner removed it, and the workspace has no such
    /// decision to record.</summary>
    internal string[] PlanReviewChoicesForCheck
        => PlanApprovalCards.FirstOrDefault() is { } card
            ? card.GetLogicalDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("plan-review-option"))
                .Select(button => button.Tag as string ?? "").ToArray()
            : [];

    internal bool PlanReviewHasContinueForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Name == "PlanContinueButton") == true;

    internal bool PlanReviewHasCancelForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Tag as string == "ChatPlanCancel") == true;

    private Button? PlanOptionRowForCheck(string tag)
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == tag
                                       && candidate.Classes.Contains("plan-review-option"));

    private TextBox? PlanFeedbackBoxForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "PlanFeedbackBox");

    /// <summary>The letter cap inside one option row, as painted. An armed cap once took the very fill the plate
    /// under it wore and its letter disappeared into the card; a build passes that, so the fills are what gets
    /// read back rather than the class names that produce them.</summary>
    internal Border? PlanCapForCheck(string tag)
        => PlanOptionRowForCheck(tag)?.GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(cap => cap.Classes.Contains("plan-review-key"));

    /// <summary>Which option is armed, or empty when none is. Nothing read the <c>selected</c> class before this,
    /// so a card that kept lighting the same cap whatever happened would pass every check that only clicks and
    /// counts.</summary>
    internal string PlanSelectedChoiceForCheck
        => PlanReviewChoicesForCheck.FirstOrDefault(tag =>
            PlanOptionRowForCheck(tag)?.Classes.Contains("selected") == true) ?? "";

    /// <summary>The card's painted height. The flat list's whole promise is that choosing a line moves nothing,
    /// and a hand-checked margin is exactly the kind of 8px thing that comes back, so the height is measured.</summary>
    internal double PlanReviewCardHeightForCheck => PlanApprovalCards.FirstOrDefault()?.Bounds.Height ?? 0;

    // ── seams for a question the model asked ──

    /// <summary>The host, but only while the decision it is holding is a question. Same one-slot rule as the plan
    /// cards: the host shows one owed decision at a time, so "which one" is answered by the stored call.</summary>
    private IEnumerable<Border> QuestionCards
        => DecisionHost.IsVisible && _decisionCallId is { } callId && IsPendingQuestion(callId)
            ? [DecisionHost] : [];

    private Border? QuestionCard => QuestionCards.FirstOrDefault();

    /// <summary>The option letters the card offers, in order, with <c>"free"</c> last — the free-text line is a
    /// row of the list, and a check that could not see it could not prove it is inside the clip either.</summary>
    internal string[] QuestionChoicesForCheck
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("question-option"))
            .Select(button => button.Tag as string ?? "").ToArray() ?? [];

    internal string QuestionSelectedChoiceForCheck
        => QuestionChoicesForCheck.FirstOrDefault(tag =>
            QuestionCard?.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(row => row.Tag as string == tag)?.Classes.Contains("selected") == true) ?? "";

    internal bool QuestionContinueEnabledForCheck
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "QuestionContinueButton")?.IsEnabled == true;

    internal bool QuestionHasSkipForCheck
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Tag as string == "ChatQuestionSkip") == true;

    /// <summary>Anything on the card that could be read as the grant button. The question card has no such control,
    /// and the negative is what matters: a 「总是允许」 here would let one click silence every later question.</summary>
    internal bool QuestionOffersAlwaysAllowForCheck
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Tag as string is { Length: > 0 } tag
                           && tag.Contains("Always", StringComparison.Ordinal)) == true;

    internal string QuestionTitleForCheck
        => QuestionCard?.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("question-title"))?.Text ?? "";

    internal string QuestionLabelForCheck(string letter)
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(row => row.Tag as string == letter)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("question-label"))?.Text ?? "";

    /// <summary>The whole question, when the card had to leave part of it out of the title and handed the rest to a
    /// tip. Empty when nothing was cut — a card that tipped the line it already draws would be saying the same
    /// sentence twice, and the empty answer is what lets a check prove the tip is a fallback and not a habit.</summary>
    internal string QuestionTitleTipForCheck
        => QuestionCard?.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("question-title")) is { } title
            ? TipTextOf(title)
            : "";

    /// <summary>The whole label of one option row, on the same terms as the title's.</summary>
    internal string QuestionLabelTipForCheck(string letter)
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(row => row.Tag as string == letter)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("question-label")) is { } label
            ? TipTextOf(label)
            : "";

    /// <summary>A tip read back as text, whichever shape it was installed in: the plain string most controls carry,
    /// or the wrapping holder <c>TipContent</c> builds for a sentence.</summary>
    private static string TipTextOf(Control control)
        => ToolTip.GetTip(control) switch
        {
            TextBlock holder => holder.Text ?? "",
            null => "",
            var other => other.ToString() ?? "",
        };

    private TextBox? QuestionAnswerBox => QuestionCard?.GetLogicalDescendants().OfType<TextBox>()
        .FirstOrDefault(box => box.Name == "QuestionAnswerBox");

    internal string QuestionFreeTextForCheck => QuestionAnswerBox?.Text ?? "";

    internal void SetQuestionFreeTextForCheck(string text)
    {
        if (QuestionAnswerBox is { } box) box.Text = text;
    }

    internal void ClickQuestionChoiceForCheck(string letter)
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(row => row.Tag as string == letter)
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal void ClickQuestionContinueForCheck()
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "QuestionContinueButton")
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal void ClickQuestionSkipForCheck()
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Tag as string == "ChatQuestionSkip")
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Presses a key on the question card through the routed event, from the composer's box or from the
    /// card's own free-text line. <paramref name="inFreeLine"/> is what makes the negative half real: with the
    /// caret in that line, A and B are letters, and only the handler's third gate knows the difference.</summary>
    internal bool PressQuestionCardKeyForCheck(Key key, bool inFreeLine = false)
    {
        var source = inFreeLine ? (Control?)QuestionAnswerBox : DecisionHost;
        if (source is null) return false;
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = source,
        };
        source.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>The card's painted height, measured the same way the plan card's is: the four-option clip case is a
    /// pixel fact, and a class name cannot report that 「继续」 fell outside the host.</summary>
    internal double QuestionCardHeightForCheck => QuestionCard?.Bounds.Height ?? 0;

    /// <summary>Whether the footer is still inside the host's own bounds. The host clips, so a card that outgrows
    /// it keeps a visible-but-unreachable answer button unless this is measured at the far end of the range.</summary>
    internal bool QuestionContinueInsideHostForCheck
        => QuestionCard?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "QuestionContinueButton") is { } button
           && button.Bounds.Bottom <= QuestionCard.Bounds.Bottom + 0.5;

    /// <summary>The transcript's waiting lines, as written. A question and an action both park a run and both
    /// leave one muted line behind; if those lines say the same thing, the person scrolling back cannot tell
    /// whether their answer was a decision or a permission.</summary>
    internal string[] TranscriptWaitingLinesForCheck
        => MessageFlow.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("approval-pending-line"))
            .SelectMany(border => border.GetLogicalDescendants().OfType<TextBlock>())
            .Select(text => text.Text ?? "").ToArray();

    /// <summary>The card as a visual, for the frames a check photographs.</summary>
    internal Border? PlanReviewCardForCheck => PlanApprovalCards.FirstOrDefault();

    internal Button? PlanContinueButtonForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "PlanContinueButton");

    internal string PlanFeedbackTextForCheck => PlanFeedbackBoxForCheck?.Text ?? "";

    /// <summary>The revision line's painted height, and the height its own text asked for. A box arranged shorter
    /// than it measured is clipping what was typed into it — the one failure mode an inline line can have that a
    /// screenshot of an empty one never shows.</summary>
    internal double PlanFeedbackHeightForCheck => PlanFeedbackBoxForCheck?.Bounds.Height ?? 0;

    internal double PlanFeedbackDesiredHeightForCheck => PlanFeedbackBoxForCheck?.DesiredSize.Height ?? 0;

    internal bool PlanFeedbackClippedForCheck
        => PlanFeedbackBoxForCheck is { } box && box.Bounds.Height + 0.5 < box.DesiredSize.Height;

    /// <summary>
    /// Presses a key while the plan card is up, through the routed event the way
    /// <see cref="PressComposerKeyForCheck"/> does, so a check exercises the wiring rather than the private method
    /// the wiring happens to call. <paramref name="inRevisionLine"/> sends it from the caret's own control, which
    /// is how "A and B are letters while somebody is typing" and "Escape leaves the line" reach the handler.
    /// Returns whether the card took the key, so the negative half is asserted instead of assumed.
    /// </summary>
    internal bool PressPlanCardKeyForCheck(Key key, bool inRevisionLine = false,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var source = inRevisionLine ? (Control?)PlanFeedbackBoxForCheck : DecisionHost;
        if (source is null) return false;
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = source,
        };
        source.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>Moves the caret into the revision line the way a tab does, so "nothing moves" is measured for
    /// focus as well as for clicks.</summary>
    internal void FocusPlanFeedbackForCheck() => PlanFeedbackBoxForCheck?.Focus();

    /// <summary>The revision line is option B itself now, so it is on screen from the first frame rather than
    /// opened by choosing something.</summary>
    internal bool PlanFeedbackVisibleForCheck
        => PlanFeedbackBoxForCheck is { IsVisible: true, Bounds: { Width: > 0 } };

    internal bool PlanContinueEnabledForCheck
        => PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "PlanContinueButton")?.IsEnabled == true;

    internal void ClickPlanChoiceForCheck(string tag)
    {
        var button = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == tag
                                       && candidate.Classes.Contains("plan-review-option"));
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Types into the revision box through its own Text setter, so the "keep going is disabled until
    /// there is something to send" wiring is exercised rather than bypassed.</summary>
    internal void SetPlanFeedbackForCheck(string text)
    {
        var box = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(candidate => candidate.Name == "PlanFeedbackBox");
        if (box is not null) box.Text = text;
    }

    internal void ClickPlanContinueForCheck()
    {
        var button = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Name == "PlanContinueButton");
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal void ClickPlanCancelForCheck()
    {
        var button = PlanApprovalCards.FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag as string == "ChatPlanCancel");
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>The one-line records of calls already decided, in the order they appear.</summary>
    internal string[] ApprovalRecordsForCheck
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Border>())
            .Where(record => record.Classes.Contains("approval-record"))
            .Select(record => record.GetLogicalDescendants().OfType<TextBlock>()
                .FirstOrDefault()?.Text ?? "")
            .ToArray();

    // ── the undo on a write's record line ──
    private IEnumerable<Button> UndoButtons
        => MessageFlow.Children.SelectMany(row => row.GetLogicalDescendants().OfType<Button>())
            .Where(button => button.Classes.Contains("undo-action"));

    /// <summary>How many writes still have a copy behind them. One per spent revert is the whole design: the
    /// button is the record that the pre-image exists.</summary>
    internal int UndoButtonsForCheck => UndoButtons.Count();

    internal string[] UndoButtonTipsForCheck
        => UndoButtons.Select(button => ToolTip.GetTip(button)?.ToString() ?? "").ToArray();

    /// <summary>Presses the <paramref name="index"/>th undo the way a person presses it. A missing button raises
    /// nothing rather than throwing, so the assertion that follows reports a failed expectation instead of a
    /// crashed suite.</summary>
    internal void ClickUndoForCheck(int index)
    {
        var button = UndoButtons.ElementAtOrDefault(index);
        if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Appends a notice row through the real path, so the row's layout can be read back off the
    /// controls instead of inferred from code.</summary>
    internal void AppendNoticeForCheck(string text, bool danger = false) => AppendNotice(text, danger);

    /// <summary>Takes the panel's own memory of how runs ended away, leaving nothing but what the session file
    /// says. The question it exists for is whether a notice survives on the record alone — memory first, disk
    /// second — and a check cannot ask that while the dictionary is still holding the answer.</summary>
    internal void ClearNoticesForCheck()
    {
        _notices.Clear();
        NoticeHost.Children.Clear();
    }

    /// <summary>The last notice row's column layout — (icon column, text column, declared column count) —
    /// or null when no notice is on screen. Notice rows live below the message flow, so this
    /// never disturbs the bubble assertions.</summary>
    internal (int IconColumn, int TextColumn, int Columns)? LastNoticeLayoutForCheck
    {
        get
        {
            if (CurrentNoticeRow is not { } row || row.Children.Count < 2)
            {
                return null;
            }

            return (Grid.GetColumn(row.Children[0]), Grid.GetColumn(row.Children[1]), row.ColumnDefinitions.Count);
        }
    }

    // ── Composer (send button state, chip, focus highlight) ──
    internal void SetComposerFocusForCheck(bool focused) => SetComposerFocus(focused);
    internal bool ComposerFocusedForCheck => ComposerFrame.Classes.Contains("focused");
    internal IBrush? ComposerBorderBrushForCheck => ComposerFrame.BorderBrush;
    internal IBrush? ComposerBackgroundForCheck => ComposerFrame.Background;
    internal bool SendButtonEnabledForCheck => SendButton.IsEnabled;
    internal bool PermissionChipEnabledForCheck => PermissionChip.IsEnabled;
    internal bool ChatActivityVisibleForCheck => _live is { Timer: not null };
    internal string ChatActivityTextForCheck => _live?.Status.Text ?? "";
    /// <summary>The elapsed lines the transcript is drawing, in document order. Empty is the pass condition for a
    /// turn that recorded no total: an older session has to show nothing, not a confident 本次共 0s.</summary>
    internal IReadOnlyList<string> RunElapsedLinesForCheck()
        => MessageFlow.GetLogicalDescendants().OfType<TextBlock>()
            .Where(block => block.Classes.Contains("run-elapsed"))
            .Select(block => block.Text ?? "")
            .ToArray();

    internal string ChatActivityElapsedForCheck => _live?.Elapsed.Text ?? "";
    internal double MessageFlowWidthForCheck => MessageFlow.Bounds.Width;
    internal double MessageRowWidthForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?.Bounds.Width ?? -1;
    internal double UserPillRightForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("user-pill"))?.Bounds.Right ?? -1;

    /// <summary>The transcript's own vertical bar, measured in the scroller's space: the strip Hub's ScrollBar
    /// paints <i>over</i> the content it belongs to (opaque, always visible, 10px — Theme/HubControls.axaml, and
    /// InspectorPanel / MarkdownMessageRenderer / ChatSidebar each clear it in their own way). Zero when no bar
    /// is on screen, because a transcript that fits under its viewport loses nothing to one.</summary>
    internal double TranscriptScrollBarWidthForCheck
        => MessageScroller.GetVisualDescendants().OfType<ScrollBar>()
            .Where(bar => bar.Orientation == Orientation.Vertical && bar.IsVisible)
            .Select(bar => bar.Bounds.Width)
            .FirstOrDefault();

    internal double MessageScrollerViewportWidthForCheck => MessageScroller.Viewport.Width;

    /// <summary>Where the reading column's own right edge lands in the scroller's space — the ink the bar
    /// covers. A row's <c>Bounds</c> are read in its parent's space, so a claim about the strip the bar paints
    /// has to translate rather than compare two coordinate systems.</summary>
    internal double MessageFlowRightInScrollerForCheck
        => MessageFlow.TranslatePoint(new Point(MessageFlow.Bounds.Width, 0), MessageScroller)?.X ?? -1;

    /// <summary>The user pill's right edge in the scroller's space. The pill is the row that hugs the column's
    /// right edge (asserted beside the message-column checks), so it is the ink a bar drawn over that edge would
    /// eat — and being a filled Border, its box's right edge is where the plate really stops.</summary>
    internal double UserPillRightInScrollerForCheck(int visibleIndex)
    {
        var pill = MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("user-pill"));
        return pill is null
            ? -1
            : pill.TranslatePoint(new Point(pill.Bounds.Width, 0), MessageScroller)?.X ?? -1;
    }

    /// <summary>Where the reading column's right edge and the composer frame's right edge land in the panel's own
    /// space. The two are one column — the composer is centred to the transcript's — so the same gutter has to sit
    /// under both; a check that measured only the transcript would stay green while the composer drifted one
    /// bar-width wider than the text above it.</summary>
    internal double MessageColumnRightForCheck
        => MessageColumn.TranslatePoint(new Point(MessageColumn.Bounds.Width, 0), this)?.X ?? -1;

    internal double ComposerFrameRightForCheck
        => ComposerFrame.TranslatePoint(new Point(ComposerFrame.Bounds.Width, 0), this)?.X ?? -1;

    internal double LiveRowWidthForCheck => _live?.Row.Bounds.Width ?? -1;
    internal double LiveActivityLeftForCheck
        => _live?.Row.GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(stack => stack.Classes.Contains("chat-activity"))?.Bounds.X ?? -1;
    internal string? LastNoticeTextForCheck
        => CurrentNoticeRow?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;

    /// <summary>Whether the notice strip is on screen at all — the rebuild-survival question, which the text alone
    /// cannot answer because the stored string and the painted row are two different facts.</summary>
    internal bool NoticeVisibleForCheck => CurrentNoticeRow is not null;

    /// <summary>Presses the ↻ on the notice strip exactly as a click would, so the check proves the line carries an
    /// action and that the action is the panel's own continuation — not a test calling the workspace behind the
    /// button's back.</summary>
    internal bool ClickNoticeActionForCheck()
    {
        if (CurrentNoticeRow?.Children.OfType<Button>().FirstOrDefault() is not { } button) return false;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>Whether the painted notice offers a retry at all. Separate from the click because "the button is
    /// there" is the claim a timeout line has to answer: a retryable ending and a plain sentence look identical
    /// until something asks which one was drawn.</summary>
    internal bool NoticeRetryVisibleForCheck => CurrentNoticeRow?.Children.OfType<Button>().Any() == true;

    /// <summary>Identity of the painted notice row. A repaint makes a new one, so two different tokens across a
    /// rebuild say the strip was cleared and filled again — which is the only way to tell "it survived" apart
    /// from "nothing ever erased it".</summary>
    internal int NoticeInstanceTokenForCheck
        => CurrentNoticeRow is { } row
            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(row)
            : 0;

    /// <summary>Whether the transcript is looking at its newest line. The plate below is derived from the same
    /// three numbers, so this is the fact and the plate is the symptom.</summary>
    internal bool ViewAtBottomForCheck
        => MessageScroller.Offset.Y + MessageScroller.Viewport.Height
           >= MessageScroller.Extent.Height - StickEpsilon;

    /// <summary>Moves the transcript's reading position the way a wheel or a drag on the bar would. A check that
    /// asks where a session reopens has to be able to leave it somewhere first.</summary>
    internal void ScrollTranscriptForCheck(double offset)
    {
        MessageScroller.Offset = new Point(0, offset);
        UpdateScrollAffordance();
    }

    /// <summary>Where the transcript is being read. The bottom flag says whether the newest line is in view; only
    /// the offset says whether a repaint left the reader where they had put themselves.</summary>
    internal double FlowOffsetForCheck => MessageScroller.Offset.Y;

    internal string InputTextForCheck => InputBox.Text ?? "";

    /// <summary>How many times the send button's state has been recomputed. A check asserts it grows when the
    /// input text changes, which is what proves the change notification is actually wired up.</summary>
    internal int SendStateUpdates { get; private set; }
    internal string SendButtonTooltipForCheck => ToolTip.GetTip(SendButton)?.ToString() ?? "";
    internal bool ModelPickerIsChipForCheck => ModelPicker.Classes.Contains("chip");
    internal bool ModelPickerUsesContentWidthForCheck => double.IsNaN(ModelPicker.Width);
    internal string SelectedModeForCheck => _chat.ActiveMode;
    internal string SelectedReasoningForCheck => _chat.ActiveReasoningEffort;
    internal string ReasoningChipTextForCheck => SelectedReasoningLabel.Text ?? "";
    internal bool ReasoningPickerEnabledForCheck => SelectedReasoningLabel.IsVisible;
    internal double ContextArcForCheck => ContextRing.Usage;

    /// <summary>The whole reading behind the ring, absolute numbers and all. A ratio alone cannot tell a session
    /// that is full from one that no longer fits, which is the distinction the panel exists to make.</summary>
    internal ContextUsage ContextReadingForCheck => CurrentContextUsage();

    internal int ContextUsedForCheck => CurrentContextUsage().Used;
    internal string[] ContextCategoryRowsForCheck => ContextCategoryList.Children.OfType<Grid>()
        .Select(row => string.Join("=", row.Children.OfType<TextBlock>().Select(cell => cell.Text)))
        .ToArray();
    internal string ContextPopoverSourceForCheck => ContextPopoverSource.Text ?? "";
    internal string ContextPopoverNoteForCheck => ContextPopoverNote.Text ?? "";

    /// <summary>The measured-spend block as drawn. Read as text rather than as rows because its whole claim is the
    /// wording: 本轮 and 本会话累计 are two different totals and a check that only counted numbers could not tell
    /// them apart.</summary>
    internal string ContextBurnTextForCheck => ContextBurnNote.Text ?? "";

    internal bool ContextBurnVisibleForCheck => ContextBurnNote.IsVisible;

    /// <summary>The cache hit headline as drawn, plus the two containers a check needs to prove the figure comes
    /// before the breakdown rather than inside it: a claim about position cannot be read off the text.</summary>
    internal bool CacheHitVisibleForCheck => CacheHitBlock.IsVisible;
    internal string CacheHitValueForCheck => CacheHitValue.Text ?? "";
    internal string CacheHitDetailForCheck => CacheHitDetail.Text ?? "";
    internal Control CacheHitBlockForCheck => CacheHitBlock;
    internal Control ContextCategoryListForCheck => ContextCategoryList;

    /// <summary>The emphasis this figure is allowed: bold ink of its own, never a size above the title's percentage
    /// — two equal headlines in one card was the reason the first version was wrong.</summary>
    internal double CacheHitValueFontSizeForCheck => CacheHitValue.FontSize;
    internal bool CacheHitValueBoldForCheck => CacheHitValue.FontWeight == FontWeight.Bold;
    internal double ContextPopoverPercentFontSizeForCheck => ContextPopoverPercent.FontSize;

    /// <summary>One spend line built from a synthetic reading, so a check can pin the wording and the rounding
    /// without a provider, a stream or a session: what these assertions are about is the sentence.</summary>
    internal static string DescribeBurnForCheck(ContextBurn burned, bool countRequests = false)
        => DescribeBurn(burned, countRequests);

    internal string ContextTooltipForCheck => ToolTip.GetTip(ContextButton)?.ToString() ?? "";
    internal string ContextPopoverTitleForCheck => ContextPopoverTitle.Text ?? "";
    internal string ContextPopoverPercentForCheck => ContextPopoverPercent.Text ?? "";
    internal bool ContextPopoverOpenForCheck => ContextPopup.IsOpen;
    internal bool ContextCompressButtonEnabledForCheck => CompressContextButton.IsEnabled;
    internal string ContextCompressButtonTextForCheck => CompressContextButton.Content?.ToString() ?? "";
    internal Visual ContextPopoverContentForCheck => (Visual)ContextPopup.Child!;
    internal Task? ContextCompressionTaskForCheck => _contextCompressionTask;
    internal void OpenContextPopoverForCheck() => ShowContextMenu();
    internal void CloseContextPopoverForCheck() => ContextPopup.IsOpen = false;
    internal void ClickContextCompressForCheck()
        => CompressContextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal int ContextAttachmentCountForCheck => _contextAttachments.Count;

    /// <summary>Pictures the composer is holding, in the order they would be sent.</summary>
    internal int PendingPictureCountForCheck => _pendingPictures.Count;

    /// <summary>Drives the same entrance the file picker hands back, so a check can prove the chip, the send and
    /// the stored file without a dialog nobody can automate (the picker's own decision is asserted in
    /// <c>Pickers.Resolve</c>).</summary>
    internal void AddImageForCheck(string path) => AddPictureFromPath(path);

    /// <summary>Feeds the composer a picture that never was a file — which is what a clipboard capture becomes
    /// once it is encoded. Every entrance ends in the same admission, so this is the one that proves the shared
    /// path and the one that a pasted screenshot goes through.</summary>
    internal void AddImageBytesForCheck(byte[] bytes, string name) => AddPendingPicture(bytes, name);

    /// <summary>The composer's drop target: what "you can drop a screenshot here" is decided by.</summary>
    internal bool ComposerAcceptsDropForCheck => DragDrop.GetAllowDrop(ComposerFrame);

    /// <summary>Pictures painted into the message flow, counted by the frame each one sits in.</summary>
    internal int RenderedPictureCountForCheck
        => MessageFlow.GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("turn-picture"));

    /// <summary>Chips in the composer that carry a thumbnail rather than only a name. Read off the shape of the
    /// chip rather than its visual descendants: a chip built a moment ago has not been through a layout pass
    /// yet, and "does this attachment show the picture" cannot depend on when the check happens to look. The chip
    /// is identified by the picture entry it takes out being a picture, because a file attachment draws an SVG
    /// cover of its own — an assertion about attached pictures must not start counting files.</summary>
    internal int PictureChipsForCheck
        => ContextAttachmentPanel.Children.OfType<Border>()
            .Count(border => border.Child is StackPanel panel
                             && panel.Children.OfType<Button>().Any(button => button.Tag is PendingPicture)
                             && panel.Children.Any(IsThumbnailFace));

    private static bool IsThumbnailFace(Control face)
        => face is Image or Button { Content: Image };

    /// <summary>The context chips' own labels, as painted. Read off the row rather than off the private list: a
    /// check that reads the list would pass while the chip row drew nothing.</summary>
    internal string[] ContextAttachmentLabelsForCheck
        => ContextAttachmentPanel.Children.OfType<Border>()
            .Select(border => border.Child as StackPanel)
            .Where(panel => panel is not null)
            .SelectMany(panel => panel!.Children)
            .OfType<TextBlock>()
            .Where(text => text.Classes.Contains("context-attachment-label"))
            .Select(text => text.Text ?? "")
            .ToArray();

    /// <summary>Whether one context chip drew a face rather than only a name. The shape a vector cover has to be
    /// checked by, since a chip that renders nothing still lays out and still measures.</summary>
    internal bool ContextChipHasFaceForCheck(int index)
    {
        var chips = ContextAttachmentPanel.Children.OfType<Border>()
            .Select(border => border.Child as StackPanel)
            .Where(panel => panel is not null)
            .ToList();
        return index >= 0 && index < chips.Count && chips[index]!.Children.Any(IsThumbnailFace);
    }

    /// <summary>Drives the file entrance the picker and the drop both end in, without a dialog nobody can
    /// automate — the picker's own decision is asserted in <c>Pickers.Resolve</c>.</summary>
    internal bool AddContextFileForCheck(string path) => AddFileAttachment(path);

    /// <summary>Presses one chip's own × rather than removing the entry here: what a check has to prove is that
    /// the button takes the picture out of the message, not that the list can be edited.</summary>
    internal void RemovePictureChipForCheck(int index)
    {
        var buttons = ContextAttachmentPanel.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Tag is PendingPicture)
            .ToList();
        if (index < buttons.Count) buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// Hands the paste path a clipboard payload of a known shape. The real Ctrl+V reads the system clipboard,
    /// which a self-check cannot fill without overwriting what the person had copied — and the part worth
    /// asserting is what the composer decides once it has the payload, not whether Windows answered.
    /// </summary>
    internal Task<bool> PasteTransferForCheck(IAsyncDataTransfer transfer) => TakeClipboardAsync(transfer);

    /// <summary>Raises the composer's own drop event, so the drop target and the file extraction run exactly as
    /// they do when a screenshot is dragged in from Explorer.</summary>
    internal void DropForCheck(IDataTransfer transfer)
        => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, ComposerFrame,
            default, KeyModifiers.None));

    /// <summary>Sends exactly what the composer holds, which is how a picture with no text under it is sent.</summary>
    internal Task SendComposerForCheck() => SendAsync();

    // ── The drag ring and the picture preview ──
    /// <summary>Whether the composer is answering a drag right now. The ring is the only thing that says so, and
    /// it is decided by the same handler a real drag runs.</summary>
    internal bool ComposerDragOverForCheck => ComposerFrame.Classes.Contains("drag-over");

    /// <summary>Raises the composer's own DragOver handler with a payload of the asked-for shape.</summary>
    internal void RaiseComposerDragOverForCheck(IDataTransfer transfer)
        => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, transfer, ComposerFrame,
            default, KeyModifiers.None));

    internal void RaiseComposerDragLeaveForCheck()
        => ComposerFrame.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, new DataTransfer(),
            ComposerFrame, default, KeyModifiers.None));

    /// <summary>The hint the composer gives before anyone types: pasting and dropping are interactions a person
    /// has to be told about once, and the placeholder is the only place that names them.</summary>
    internal string ComposerPlaceholderForCheck => InputBox.PlaceholderText ?? "";

    /// <summary>Clicks the Nth picture in the transcript through the button that opens it. The picture now
    /// opens in the window-level viewer, so the assertion of what opened lives on <c>MainWindow</c>; this only
    /// proves the click routes.</summary>
    internal void ClickRenderedPictureForCheck(int index)
    {
        var faces = MessageFlow.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("picture-face"))
            .ToList();
        if (index < faces.Count) faces[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Clicks the Nth chip's thumbnail in the composer — the draft's picture, which never was a file.
    /// Walked off the chip's shape for the same reason the count is: a chip added a moment ago may not have been
    /// laid out yet, and a click that depends on that is a click that depends on nothing.</summary>
    internal void ClickPendingPictureForCheck(int index)
    {
        var faces = ContextAttachmentPanel.Children.OfType<Border>()
            .Select(border => border.Child as StackPanel)
            .Where(panel => panel is not null)
            .SelectMany(panel => panel!.Children)
            .OfType<Button>()
            .Where(button => button.Classes.Contains("picture-face"))
            .ToList();
        if (index < faces.Count) faces[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Sends Escape down the composer's own route, so the preview-first ordering is the real one.</summary>
    internal void PressEscapeForCheck() => PressComposerKeyForCheck(Key.Escape);

    /// <summary>Presses a key on the composer through the routed event, so the bindings themselves are what a
    /// check exercises — not the private methods they happen to call.</summary>
    internal void PressComposerKeyForCheck(Key key, KeyModifiers modifiers = KeyModifiers.None)
        => InputBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = InputBox,
        });

    /// <summary>How far into the recall walk the composer stands, and the newest sentences it can walk back to.
    /// The tail is what a check reads: the queue is capped, so a count would say one thing on a fresh window and
    /// another after an hour of the suite having sent things.</summary>
    internal int RecallStepForCheck => _recallIndex;
    internal string[] RecentSentTextsForCheck(int count)
        => _sentTexts.TakeLast(Math.Max(count, 0)).ToArray();

    /// <summary>
    /// Moves the keyboard off the text box and onto the <c>+</c> button. Without this the focus-after-send cell
    /// would prove nothing: the box already has focus, so a handler that never gave it back would still pass.
    /// </summary>
    internal void MoveFocusOffComposerForCheck() => AddContextButton.Focus();

    /// <summary>The session's attachment directory, as the store sees it. A check that claims a deleted session
    /// took its pictures with it has to look at the disk, not at a list this view keeps.</summary>
    internal string SessionImageDirectoryForCheck(string conversationId)
        => _chat.SessionImageDirectory(conversationId);
    internal bool HasComposerAddMenuForCheck => AddContextButton is not null;

    /// <summary>What the <c>+</c> menu offers right now, read from the menu that is actually built on click. A
    /// menu item that exists only in a screenshot is not a feature.</summary>
    internal string[] ComposerMenuHeadersForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>()
            .Select(item => item.Header?.ToString() ?? "").ToArray();
    internal bool ModeIndicatorVisibleForCheck => ModeIndicatorButton.IsVisible;
    internal string ModeIndicatorLabelForCheck => ModeIndicatorLabel.Text ?? "";
    internal bool ComposerPlusCenteredForCheck
        => AddContextButton.HorizontalContentAlignment == HorizontalAlignment.Center
           && AddContextButton.VerticalContentAlignment == VerticalAlignment.Center;
    internal bool ContextRingPrecedesModelForCheck
        => ContextButton.Parent is Panel panel
           && panel.Children.IndexOf(ContextButton) < panel.Children.IndexOf(ModelPicker);
    internal bool PermissionChipFollowsPlusForCheck
        => PermissionChip.Parent is Panel panel
           && panel.Children.IndexOf(PermissionChip) == panel.Children.IndexOf(AddContextButton) + 1;
    internal bool ModeIndicatorFollowsPermissionForCheck
        => ModeIndicatorButton.Parent is Panel panel
           && panel.Children.IndexOf(ModeIndicatorButton) == panel.Children.IndexOf(PermissionChip) + 1;
    internal bool ModeIndicatorKeepsLabelVisibleForCheck
        => ModeIndicatorLabel.IsVisible && ModeIndicatorLabel.Parent is Grid grid
           && grid.ColumnDefinitions.Count == 2
           && Grid.GetColumn(ModeIndicatorLabel) == 1;
    // Compared against the session, not against anything this view remembers: comparing the chip with a field the
    // chip itself wrote is how a stale marker passed its own check.
    internal bool ModeIndicatorIconMatchesSelectedModeForCheck
        => ReferenceEquals(ModeIndicatorIcon.Data, ThemeGeometry(ComposerModeIconKey(_chat.ActiveMode)));
    internal bool ModeIndicatorCloseIsRedForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
           && close.Foreground is ISolidColorBrush closeBrush
           && closeBrush.Color.R > closeBrush.Color.G
           && closeBrush.Color.R > closeBrush.Color.B;
    internal string ModeIndicatorCloseColorForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close"))?.Foreground?.ToString() ?? "unset";
    internal bool ModeIndicatorCloseIsLeftAndCenteredForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
           && close.VerticalAlignment == VerticalAlignment.Center
           && close.Parent is Grid grid
           && Grid.GetColumn(close) == 0;
    internal (string Glyph, double FontSize, string FontFamily) ModeIndicatorCloseGlyphForCheck
        => ModeIndicatorButton.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("mode-indicator-close")) is { } close
            ? (close.Text ?? "", close.FontSize, close.FontFamily?.ToString() ?? "")
            : ("", 0, "");
    internal bool ComposerMenuModesHaveIconsForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => item.Header is StackPanel header
                         && header.Children.OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault() is { } icon
                         && ReferenceEquals(icon.Data, ThemeGeometry(ComposerModeIconKey((string)item.Tag!)))
                         && header.Children.OfType<TextBlock>().Any());
    internal string[] ComposerMenuModesForCheck => BuildComposerMenu().Items.OfType<MenuItem>()
        .Where(IsComposerModeItem)
        .Select(item => (string)item.Tag!)
        .ToArray();
    internal string[] CheckedComposerMenuModesForCheck => BuildComposerMenu().Items.OfType<MenuItem>()
        .Where(item => IsComposerModeItem(item) && item.IsChecked)
        .Select(item => (string)item.Tag!)
        .ToArray();
    internal bool ComposerMenuModeItemsCloseOnClickForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => !item.StaysOpenOnClick);
    internal bool ComposerMenuModesAreCheckboxesForCheck
        => BuildComposerMenu().Items.OfType<MenuItem>().Where(IsComposerModeItem)
            .All(item => item.ToggleType == MenuItemToggleType.CheckBox);
    /// <summary>The marker the composer is wearing, read the same way the chip reads it: 计划 while the session on
    /// screen is in plan mode, nothing otherwise — the default and 提问 wear no chip by design.</summary>
    internal string? ComposerModeMarkerForCheck
        => _chat.ActiveMode == ChatModes.Plan ? ChatModes.Plan : null;

    // ── Permission chip and its menu ──
    // Each probe builds a fresh menu, which is the same object a user's click would have arrived on: the list
    // is composed from the session as it stands, not from state this view keeps.
    private static string MenuItemTitle(MenuItem item) => item.Header switch
    {
        Grid header => header.Children.OfType<StackPanel>().FirstOrDefault()?
            .Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
        string text => text,
        _ => "",
    };

    private static string MenuItemHint(MenuItem item)
        => item.Header is Grid header
            ? header.Children.OfType<StackPanel>().FirstOrDefault()?
                .Children.OfType<TextBlock>().ElementAtOrDefault(1)?.Text ?? ""
            : "";

    internal string PermissionChipLabelForCheck => PermissionChipLabel.Text ?? "";
    internal bool PermissionChipMarksDangerForCheck => PermissionChip.Classes.Contains("danger");
    internal bool PermissionChipIconIsDrawnForCheck => PermissionChipIcon.Data is not null;

    /// <summary>Both composer chips have to carry the same caret: on a chip that is otherwise plain text it is
    /// the only thing saying "this opens a menu", and a stroked triangle at this size collapses into a blob.
    /// </summary>
    internal bool ComposerChipsShareLineCaretForCheck => HasLineCaret(PermissionChip) && HasLineCaret(ModelPicker);

    private static bool HasLineCaret(Control anchor)
        => anchor.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Classes.Contains("chip-caret")) is { } caret
           && ReferenceEquals(caret.Data, ThemeGeometry("Hub.Icon.Caret"))
           && caret.Fill is ISolidColorBrush { Color.A: 0 }
           && caret.Stroke is not null;

    /// <summary>Opens the permission menu through the same path the chip uses, and hands back an item of it:
    /// the flyout presents in its own top-level, so a screenshot has to be taken from inside.</summary>
    internal Control? OpenPermissionMenuForCheck()
    {
        ShowPermissionMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    internal Control? OpenComposerMenuForCheck()
    {
        ShowAddContextMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    internal string[] PermissionMenuTitlesForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Select(MenuItemTitle).ToArray();

    // ── Verification hooks for the workspace chip (used by --verify-shell) ──
    // The folder dialog is a modal and cannot be driven from a self-check, so these read the built state and
    // drive the workspace through ChatWorkspace instead — the same split AuthDialog's hooks use.

    internal string WorkspaceChipLabelForCheck => WorkspaceChipLabel.Text ?? "";
    internal bool WorkspaceChipVisibleForCheck => WorkspaceChip.IsVisible;
    internal string WorkspaceChipHintForCheck => ToolTip.GetTip(WorkspaceChip)?.ToString() ?? "";

    /// <summary>Where the picker sits, in the composer frame's own coordinates, and whether it is still inside
    /// that frame. The move is the change, so the placement is what gets pinned: (0, just under the frame) is
    /// "outside the chat box, bottom-left", and anything inside the frame is the old affordance back again.
    /// </summary>
    internal (double X, double Y, double FrameHeight, bool InsideFrame) WorkspaceChipPlateForCheck
    {
        get
        {
            var at = WorkspaceChip.TranslatePoint(new Point(0, 0), ComposerFrame) ?? default;
            return (at.X, at.Y, ComposerFrame.Bounds.Height, WorkspaceChip.GetLogicalAncestors()
                .Any(ancestor => ReferenceEquals(ancestor, ComposerFrame)));
        }
    }

    internal string[] WorkspaceMenuTitlesForCheck
        => BuildWorkspaceMenu().Items.OfType<MenuItem>().Select(MenuItemTitle).ToArray();

    internal Control? OpenWorkspaceMenuForCheck()
    {
        ShowWorkspaceMenu();
        return _openMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    /// <summary>Clicks the "clear" row, which exists only while a directory is bound.</summary>
    internal bool ClickWorkspaceMenuClearForCheck()
    {
        var clear = BuildWorkspaceMenu().Items.OfType<MenuItem>()
            .FirstOrDefault(item => MenuItemTitle(item) == HubStrings.Get("ChatWorkspaceMenuClear"));
        if (clear is null) return false;
        clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    internal string[] PermissionMenuHintsForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Select(MenuItemHint).ToArray();

    internal int PermissionMenuCheckedCountForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Count(item => item.IsChecked);

    /// <summary>Which tier the menu marks as current, read off the tick's own visibility rather than a flag:
    /// the mark is what a person sees, so that is the thing that has to be right. -1 when nothing is marked.</summary>
    internal int PermissionMenuMarkedIndexForCheck
    {
        get
        {
            var items = BuildPermissionMenu().Items.OfType<MenuItem>().ToList();
            for (var i = 0; i < items.Count; i++)
            {
                // The row's two Paths are the tier's icon and then the tick; the text column is a panel.
                if (items[i].Header is Grid grid
                    && grid.Children.OfType<Avalonia.Controls.Shapes.Path>().LastOrDefault()
                        is { IsVisible: true } tick
                    && ReferenceEquals(tick.Data, ThemeGeometry("Hub.Icon.Tick"))) return i;
            }

            return -1;
        }
    }

    /// <summary>Whether the quiet "follow the default" row is offered — it exists only while this session has
    /// an override that could be dropped.</summary>
    internal bool PermissionMenuOffersFollowDefaultForCheck
        => BuildPermissionMenu().Items.OfType<MenuItem>().Any(item => item.Header is string);

    /// <summary>Clicks one entry of the permission menu, in the order the menu shows them.</summary>
    internal bool SelectPermissionMenuEntryForCheck(int index)
    {
        if (BuildPermissionMenu().Items.OfType<MenuItem>().ElementAtOrDefault(index) is not { } item) return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    /// <summary>True when the round button currently shows the stop square. Compared by geometry identity
    /// rather than by string, because the icons are resolved from the same cached resource.</summary>
    internal bool SendIconIsStopForCheck
        => SendButton.Content is Avalonia.Controls.Shapes.Path path
           && ReferenceEquals(path.Data, ThemeGeometry("Hub.Icon.Stop"));

    /// <summary>The round button on its own, so a check can render just the circle and its glyph.</summary>
    internal Control SendButtonForCheck => SendButton;

    /// <summary>The colour the glyph is drawn in, read from its live brush. The arrow is stroked and the stop
    /// square is filled, so both ends have to be read — the fill used to come back transparent. A pixel mask
    /// must not hardcode it: <c>Hub.Accent</c> is a different blue per theme, and a literal would fail on a
    /// theme switch.</summary>
    internal Color SendGlyphColorForCheck
        => SendButton.Content is Avalonia.Controls.Shapes.Path path
            ? (path.Stroke as ISolidColorBrush)?.Color
              ?? (path.Fill as ISolidColorBrush)?.Color
              ?? Colors.Transparent
            : Colors.Transparent;

    /// <summary>The plate the glyph sits on: the other end of the line <see cref="SendGlyphColorForCheck"/>
    /// reads, so a pixel check can measure the ink as a fraction between two live colours rather than against
    /// a hardcoded blue.</summary>
    internal Color SendPlateColorForCheck
        => SendButton.Background is ISolidColorBrush brush ? brush.Color : Colors.Transparent;

    /// <summary>
    /// Where the glyph's slot sits inside the button, in DIP, plus the width the hidden "…" affordance
    /// claims. Measured from layout bounds because it is the ink's placement *inside* the slot that goes
    /// wrong, not the slot's placement in the button — so the two together say whether a centring failure
    /// comes from layout or from Stretch. A negative affordance width means the template part was not
    /// found at all, which is a different failure from "it takes no space".
    /// </summary>
    internal (double Dx, double Dy, double SlotWidth, double AffordanceWidth) SendGlyphLayoutForCheck
    {
        get
        {
            var glyph = SendButton.Content as Visual;
            double x = 0, y = 0;
            for (var v = glyph; v is not null && v != SendButton; v = v.GetVisualParent())
            {
                x += v.Bounds.X;
                y += v.Bounds.Y;
            }
            var affordance = SendButton.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "PART_MoreAffordance");
            return (x + (glyph?.Bounds.Width ?? 0) / 2 - SendButton.Bounds.Width / 2,
                    y + (glyph?.Bounds.Height ?? 0) / 2 - SendButton.Bounds.Height / 2,
                    glyph?.Bounds.Width ?? 0,
                    affordance?.Bounds.Width ?? -1);
        }
    }

    internal void SetInputForCheck(string text) => InputBox.Text = text;

    internal bool SelectModeForCheck(string mode) => _chat.SelectMode(mode);

    internal bool ClickComposerModeMenuForCheck(string mode)
    {
        var menu = BuildComposerMenu();
        var item = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), mode, StringComparison.Ordinal));
        if (item is null) return false;

        menu.ShowAt(AddContextButton);
        Dispatcher.UIThread.RunJobs();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return !menu.IsOpen;
    }

    internal void ResetModeForCheck()
        => ModeIndicatorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool IsComposerModeItem(MenuItem item)
        => item.Tag is string mode
           && (mode == ChatModes.Ask || mode == ChatModes.Plan || mode == ChatModes.Agent);

    internal bool SelectReasoningForCheck(string effort)
    {
        if (!ReasoningChoices.Any(choice => choice.Value == effort)) return false;
        return _chat.SelectReasoningEffort(effort);
    }

    internal bool SelectModelForCheck(string providerId, string modelName)
    {
        var choice = _chat.AvailableChatModels
            .FirstOrDefault(option => option.Provider.Id == providerId && option.ModelName == modelName);
        return choice is not null && _chat.SelectChatModel(providerId, modelName);
    }

    /// <summary>Types and sends, then waits for that session's run to be over. Sending no longer blocks a
    /// caller until the reply lands — that is the whole point of a run — so a check that wants the finished
    /// answer has to say so, one pump at a time.</summary>
    internal async Task SendForCheckAsync(string text)
    {
        InputBox.Text = text;
        await SendAsync();
        await WaitForRunToFinishForCheck();
    }

    /// <summary>Pumps the dispatcher until the viewed session's run is over. A check that wants the finished
    /// answer has to ask for it now: a reply arrives independently of whoever pressed send.</summary>
    internal async Task WaitForRunToFinishForCheck()
    {
        for (var wait = 0; wait < 200 && IsStreamingForCheck; wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    /// <summary>Starts a send without awaiting it, so a check can inspect the mid-stream state (the button
    /// becomes a stop icon before the first token arrives).</summary>
    internal Task BeginSendForCheckAsync(string text)
    {
        InputBox.Text = text;
        return SendAsync();
    }

    private sealed record ContextAttachment(string Kind, string Name, string Path, string? Details = null);

    /// <summary>
    /// One picture the composer is holding. Bytes rather than a path because two of the three entrances have no
    /// file to point at — a pasted capture arrives as a bitmap, and what all three have in common is the bytes
    /// Hub would store.
    /// </summary>
    private sealed record PendingPicture(byte[] Bytes, string Name);

    internal bool BubbleHasAction(int visibleIndex, string textKey)
    {
        var row = MessageRows.ElementAtOrDefault(visibleIndex);
        return row is not null && row.GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("message-action")
                           && (string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal)
                               || string.Equals(button.Content?.ToString(), HubStrings.Get(textKey), StringComparison.Ordinal)));
    }

    internal bool BubbleHasIconAction(int visibleIndex, string textKey)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("message-action-icon")
                           && string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal)) == true;

    /// <summary>How many icon actions one bubble offers. Counted rather than sampled, because a button that
    /// quietly comes back is exactly the regression this row has already had once.</summary>
    internal int BubbleIconActionCount(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .Count(button => button.Classes.Contains("message-action-icon")) ?? -1;

    /// <summary>Rendered width of one icon action, so a plate that quietly grows back is caught.</summary>
    /// <summary>Raises one icon action by its string key, exactly as a click would.</summary>
    internal bool ClickBubbleAction(int visibleIndex, string textKey)
    {
        if (MessageRows.ElementAtOrDefault(visibleIndex)?
                .GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("message-action-icon")
                                          && string.Equals(button.Tag?.ToString(), textKey, StringComparison.Ordinal))
            is not { } button) return false;

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    /// <summary>The in-place editor's cancel cross, measured the same way as the composer's: the two checks
    /// together are what keep one ✕ from being redrawn heavy while the other was softened.</summary>
    internal (string Glyph, double FontSize, string FontFamily) BubbleCancelGlyphForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("message-action-icon")
                                      && string.Equals(button.Tag?.ToString(), "Cancel", StringComparison.Ordinal))
            is { Content: TextBlock glyph }
            ? (glyph.Text ?? "", glyph.FontSize, glyph.FontFamily?.ToString() ?? "")
            : ("", 0, "");

    /// <summary>The in-place editor a bubble is hosting right now, or null while it shows plain text.</summary>
    internal TextBox? BubbleEditorForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Classes.Contains("message-edit"));

    internal bool IsStreamingForCheck => IsViewedStreaming;

    /// <summary>Everything that has arrived for this answer. Read off the run rather than off a control: the
    /// bubble now lays its text down as Markdown blocks, so no single control holds the whole of it any more.</summary>
    internal string LivePreviewTextForCheck => _live?.Run.LiveText ?? "";
    internal bool LiveRepaintDeferredForCheck => _liveRepaintPending;

    /// <summary>How many blocks the streaming bubble has laid down. Text arriving with this still at zero means
    /// the answer is being shown as source, which is the one failure the incremental path exists to prevent.</summary>
    internal int LiveBlockCountForCheck => _live?.Prose.Children.Count ?? 0;

    /// <summary>How many Markdown documents streaming this answer built, read after it lands. The bound a naive
    /// implementation cannot meet: rebuilding the whole reply per chunk scales with the stream's frame rate,
    /// while building each block once scales with the number of blocks.</summary>
    internal int LiveRenderBuildsForCheck => _live?.Builds ?? _lastLiveBuilds;

    /// <summary>Whether a heading the model wrote is being <i>drawn</i> as one while the reply is still coming.
    /// Raw markup on screen produces no heading element at all, so this distinguishes rendered from tidy-looking
    /// text in a way no amount of reading the string back can.</summary>
    internal bool LiveHasRenderedHeadingForCheck
        => _live is { } live && live.Prose.GetVisualDescendants()
            .OfType<ColorTextBlock.Avalonia.CTextBlock>()
            .Any(block => block.Classes.Contains("Heading1"));

    /// <summary>Whether the live thinking row is on screen at all, and whether it is open. The two together are
    /// the difference between the old behaviour — a card that hid itself when the answer started — and a row that
    /// folds and stays reachable.</summary>
    internal bool LiveThoughtVisibleForCheck => _live?.Thought.Shell.IsVisible ?? false;
    internal bool LiveThoughtExpandedForCheck => _live?.Thought.Expanded ?? false;
    internal string LiveThoughtTextForCheck => _live?.Thought.Text.Text ?? "";
    internal string LiveThoughtLabelForCheck => _live?.Thought.Label.Text ?? "";

    /// <summary>The head text of every thinking row the transcript is drawing. Compared against what the live row
    /// said, this is the whole handoff in one assertion.</summary>
    internal IReadOnlyList<string> ThoughtRowLabelsForCheck()
        => MessageFlow.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("thought"))
            .Select(grid => grid.Children.OfType<TextBlock>()
                .FirstOrDefault(block => block.Classes.Contains("activity-row-text"))?.Text ?? "")
            .ToArray();

    /// <summary>How deep the row-level folds go beneath a thinking row on screen. One is the design. Two means the
    /// old shape came back — a collapsed row tucked inside a collapsed group, so reading the thinking cost two
    /// clicks and the group head had already said 已思考 once before either of them.</summary>
    internal int GroupThoughtRowNestingForCheck()
    {
        var deepest = 0;
        foreach (var shell in MessageFlow.GetLogicalDescendants().OfType<Border>()
                     .Where(shell => shell.Classes.Contains("activity-row-shell")
                                     && shell.GetLogicalDescendants().OfType<Grid>()
                                         .Any(grid => grid.Classes.Contains("thought"))))
        {
            deepest = Math.Max(deepest, shell.GetLogicalDescendants().OfType<ToggleButton>()
                .Count(toggle => toggle.Classes.Contains("activity-row-toggle")));
        }

        return deepest;
    }

    /// <summary>The text the bubble has actually <i>painted</i>, as opposed to what has arrived on the run. A check
    /// that waits on the run can win a race against the frame that was supposed to react to it — which is exactly
    /// how the fold assertion below kept passing and failing at random.</summary>
    internal string LivePaintedTextForCheck => _live?.PaintedText ?? "";

    /// <summary>The live thinking body's own reading position. "It is open" and "it is looking at the newest
    /// line" are different claims, and only this can tell them apart.</summary>
    internal (double Offset, double Extent, double Viewport) LiveThoughtScrollForCheck
        => _live is not { } live
            ? (0, 0, 0)
            : (live.Thought.Scroll.Offset.Y, live.Thought.Scroll.Extent.Height, live.Thought.Scroll.Viewport.Height);

    /// <summary>Moves the live thinking body's reading position, so a check can be a hand rather than a reader.
    /// Setting it is enough — the scroll event that follows is the same one a wheel produces, which is what the
    /// stick rule is actually keyed on.</summary>
    internal void ScrollLiveThoughtForCheck(double offset)
    {
        if (_live is not { } live) return;
        live.Thought.Scroll.Offset = new Point(0, offset);
    }

    /// <summary>Presses the live thinking row the way a hand does. A check that set <c>IsChecked</c> through a
    /// property would still prove the fold, but this is the half that broke before — the wiring between the
    /// checkbox and the row — so it is exercised through the checkbox.</summary>
    internal void ToggleLiveThoughtForCheck()
    {
        if (_live is not { } live) return;
        live.Thought.Head.IsChecked = !live.Thought.Expanded;
    }

    /// <summary>Every thinking block the transcript is painting, as its own text. Empty is the pass condition
    /// for "off means off"; a count is what says a fold hid the words without removing the turn.</summary>
    internal IReadOnlyList<string> ReasoningTextsForCheck()
        => MessageFlow.GetLogicalDescendants().OfType<TextBlock>()
            .Where(block => block.Classes.Contains("turn-reasoning"))
            .Select(block => block.Text ?? "")
            .ToArray();

    /// <summary>Presses the round button the way a click would, so stopping goes through the view instead of
    /// cancelling a run behind the panel's back.</summary>
    internal void ClickSendButtonForCheck() => SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal bool ForkNoticeVisibleForCheck => ForkNotice.IsVisible;
    internal string? ForkNoticeTextForCheck => ForkNotice.IsVisible ? ForkNotice.Content?.ToString() : null;
    internal void ClickForkNoticeForCheck() => ForkNotice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Moves focus out of an open inline editor, the way clicking anywhere else in the window would.</summary>
    internal void FocusComposerForCheck() => InputBox.Focus();

    // ── the composer's clipboard menu (used by --verify-shell) ──
    // Read end to end, because the failure this covers lived in a step the object graph cannot see: the menu's
    // popup takes the keyboard focus, and the box's own lost-focus path is what threw the selection away.

    /// <summary>Selects a range in the composer, focused, the way a drag across it would.</summary>
    internal void SelectComposerForCheck(int start, int length)
    {
        InputBox.Focus();
        InputBox.SelectionStart = start;
        InputBox.SelectionEnd = start + length;
    }

    /// <summary>The composer's selection, read by the check that a menu did not eat it.</summary>
    internal (int Start, int End) ComposerSelectionForCheck => (InputBox.SelectionStart, InputBox.SelectionEnd);

    /// <summary>Asks for the composer's right-click menu the way a right-click does — the request the framework
    /// raises is raised here too, rather than the menu being opened by hand — so the flyout opens and takes the
    /// focus the box reacts to. Named apart from <see cref="OpenComposerMenuForCheck"/>, which opens the ＋ menu.
    /// Reports whether the menu is up.</summary>
    internal bool OpenComposerContextMenuForCheck()
    {
        InputBox.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        return InputBox.ContextFlyout is { IsOpen: true };
    }

    internal void HideComposerContextMenuForCheck() => InputBox.ContextFlyout?.Hide();

    /// <summary>Presses one row of the composer's menu the way a click does.</summary>
    internal bool PressComposerMenuRowForCheck(string header)
    {
        if (SelectionMenu.ItemForCheck(InputBox, header) is not { } item) return false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    /// <summary>What the clipboard holds as text — the read-back half of the menu check. Empty when the clipboard
    /// is unavailable or carries no text, read the way <c>TryPastePictureAsync</c> reads it, because the platform
    /// having no clipboard must not look like a failed copy.</summary>
    internal async Task<string> ClipboardTextForCheckAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return "";
        try
        {
            if (await clipboard.TryGetDataAsync() is not { } transfer) return "";
            foreach (var item in transfer.Items)
            {
                if (item.Formats.Contains(DataFormat.Text)
                    && await item.TryGetRawAsync(DataFormat.Text) is string text)
                {
                    return text;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }

        return "";
    }

    // ── Empty state ──
    // The chips are asserted through the object graph and through a real ClickEvent, never by reading the text
    // table: what a person can act on is the button and the composer it fills, not the key behind it.
    internal bool EmptyStateVisibleForCheck => EmptyState.IsVisible;

    /// <summary>Effective, not local: the row keeps its own <c>IsVisible</c> when the empty state it lives in is
    /// hidden, and what a person needs to know is that the chips are neither drawn nor reachable.</summary>
    internal bool SuggestionRowVisibleForCheck => SuggestionPanel.IsEffectivelyVisible;
    internal int SuggestionChipCountForCheck => SuggestionPanel.Children.OfType<Button>().Count();

    internal string SuggestionChipTextForCheck(int index)
        => SuggestionPanel.Children.OfType<Button>().ElementAtOrDefault(index)?.Content as string ?? "";

    /// <summary>Clicks a chip the way a pointer would, so the fill-without-sending behaviour is what runs.</summary>
    internal void ClickSuggestionChipForCheck(int index)
    {
        if (SuggestionPanel.Children.OfType<Button>().ElementAtOrDefault(index) is { } chip)
            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal bool ComposerHasKeyboardFocusForCheck => InputBox.IsKeyboardFocusWithin;

    /// <summary>The keys the row is built from, so a check can compare the rendered chips against the text table
    /// instead of against a copy of the expected sentences — which would pass even if the table lost a key.</summary>
    internal string[] SuggestionKeysForCheck => SuggestionKeys;

    /// <summary>Sends one key to the in-place editor, so the Enter/Escape bindings themselves are what a check
    /// exercises rather than the method they call.</summary>
    internal bool SendKeyToBubbleEditor(int visibleIndex, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (BubbleEditorForCheck(visibleIndex) is not { } editor) return false;
        editor.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = editor,
        });
        return true;
    }

    internal double BubbleIconActionWidth(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("message-action-icon"))?.Bounds.Width ?? -1;

    internal double BubbleActionBarOpacity(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Classes.Contains("message-actions"))?.Opacity ?? -1;

    internal string? MessageTimestampTextForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("message-timestamp"))?.Text;

    internal string? MessageTimestampTooltipForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("message-timestamp")) is { } label
            ? ToolTip.GetTip(label)?.ToString()
            : null;

    /// <summary>The size a peer label is drawn at. <c>peer-origin</c> is the handle a check finds the line by; the
    /// quiet 12 it renders at is <c>muted</c>'s, so the two classes stay one decision rather than two.</summary>
    internal double PeerOriginFontSizeForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("peer-origin"))?.FontSize ?? 0;

    /// <summary>The size a turn's plain words are drawn at, and the size a Markdown body is drawn at. Only the
    /// last forty turns go through the Markdown layer, so the two have to agree or a message changes font as it
    /// scrolls away.</summary>
    /// <summary>The turn the plain path paints — the first body block, whether or not it became Markdown. The
    /// size, line pitch and ink checks below all read it, so the lookup lives once.</summary>
    private TextBlock? PlainTurnForCheck()
        => MessageFlow.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("turn-text"));

    /// <summary>The paragraph a rendered answer paints its prose with: the body block, not a heading.</summary>
    private ColorTextBlock.Avalonia.CTextBlock? MarkdownBodyForCheck()
        => MessageFlow.GetLogicalDescendants().OfType<MarkdownScrollViewer>().FirstOrDefault() is { } viewer
            ? viewer.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>()
                .FirstOrDefault(text => !text.Classes.Any(name =>
                    name.StartsWith("Heading", StringComparison.Ordinal)))
            : null;

    internal double PlainTurnFontSizeForCheck => PlainTurnForCheck()?.FontSize ?? 0;

    internal double MarkdownBodyFontSizeForCheck => MarkdownBodyForCheck()?.FontSize ?? 0;

    /// <summary>The line pitch each path arranges its lines at. A class can set the size and still leave the
    /// leading to the font, which is exactly how Chinese prose ends up touching across two rows.</summary>
    internal double PlainTurnLineHeightForCheck => PlainTurnForCheck()?.LineHeight ?? 0;

    internal double MarkdownBodyLineHeightForCheck => MarkdownBodyForCheck()?.LineHeight ?? 0;

    /// <summary>The ink those two blocks paint with, read off the same elements and for the same reason as the
    /// size pair above: a message that scrolled out of the Markdown window must not change brightness either.</summary>
    internal IBrush? PlainTurnForegroundForCheck => PlainTurnForCheck()?.Foreground;

    internal IBrush? MarkdownBodyForegroundForCheck => MarkdownBodyForCheck()?.Foreground;

    /// <summary>The font a rendered code block actually draws with — empty when the reply had no code to look at.
    /// The fenced block becomes a text editor rather than a text block, so this is the element that answers the
    /// question a person can see the answer to.</summary>
    internal string MarkdownCodeFontFamilyForCheck
        => MessageFlow.GetLogicalDescendants().OfType<TextEditor>().FirstOrDefault()
            ?.FontFamily?.ToString() ?? "";

    /// <summary>Who a bubble says wrote it — the peer label when the message came from another session, null when
    /// the user typed it. Asserted as text because an absent label is exactly the misreading it exists to stop.</summary>
    internal string? PeerOriginTextForCheck(int visibleIndex)
        => MessageRows.ElementAtOrDefault(visibleIndex)?
            .GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Classes.Contains("peer-origin"))?.Text;

    /// <summary>How many bubbles on screen are labelled as another session's. Counted rather than sampled: a
    /// label on a turn nobody sent is the same bug as one that never appears.</summary>
    internal int PeerOriginCountForCheck => MessageFlow.GetLogicalDescendants()
        .OfType<TextBlock>().Count(block => block.Classes.Contains("peer-origin"));
}
