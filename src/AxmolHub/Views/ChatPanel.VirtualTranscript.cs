using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Collections;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

public partial class ChatPanel
{
    // Items contain transcript data only. A visited row must not keep its Markdown tree alive after recycling.
    private sealed class VirtualTranscriptItem
    {
        public required string Key { get; init; }
        public required string Session { get; init; }
        public required string Kind { get; init; }
        public int Source { get; set; } = -1;
        public int Ordinal { get; set; }
        public ChatTurn? Turn { get; init; }
        public ChatTurn? Call { get; init; }
        public string? ToolName { get; init; }
        public bool Markdown { get; init; }
        public bool Last { get; init; }
        public bool Thinking { get; init; }
        public bool InGroup { get; init; }
        public string? GroupKey { get; init; }
        public List<(int Index, ChatTurn Turn, string? ToolName)>? GroupTurns { get; set; }
        public string? GroupStamp { get; set; }
        public int GroupCalls { get; set; }
        public Control? Special { get; init; }

        public bool Same(VirtualTranscriptItem other) => Key == other.Key && Session == other.Session
            && Kind == other.Kind && Equals(Turn, other.Turn) && Equals(Call, other.Call)
            && ToolName == other.ToolName && Markdown == other.Markdown && Last == other.Last
            && Thinking == other.Thinking && InGroup == other.InGroup && (Kind == "group" || GroupStamp == other.GroupStamp)
            && ReferenceEquals(Special, other.Special);
    }

    private readonly AvaloniaList<VirtualTranscriptItem> _transcriptItems = [];
    private readonly Dictionary<string, (VirtualTranscriptItem Item, StackPanel Host)> _realizedTranscript = new();
    private readonly LinkedList<(string Session, Dictionary<string, (VirtualTranscriptItem Item, StackPanel Host)> Rows)>
        _virtualParked = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Conversation, HashSet<long>> _promotedByConversation = new();
    private HashSet<long> _promotedTranscriptKeys = [];
    private readonly Dictionary<(string Session, long Key), string> _inlineDrafts = new();
    private readonly Dictionary<string, (VirtualTranscriptItem Item, StackPanel Host)> _interactionRows = new();
    private string? _selectionPinnedKey;
    private readonly List<(SelectableTextBlock Block, int Start, int End)> _pinnedTextSelection = [];
    private bool _reconcilingTranscript;
    private bool _virtualOpening;
    private int _virtualRealizations;
    private long _transcriptGeneration;
    private TranscriptSnapshot? _virtualSnapshot;

    private List<Control> RealizedTranscriptRows
    {
        get
        {
            if (!_reconcilingTranscript) MessageFlow.UpdateLayout();
            return _realizedTranscript.Values.OrderBy(row => row.Item.Ordinal).SelectMany(row => row.Host.Children).ToList();
        }
    }

    private void InitializeVirtualTranscript()
    {
        MessageFlow.ItemsSource = _transcriptItems;
        MessageFlow.ItemTemplate = new FuncDataTemplate<VirtualTranscriptItem>((item, _) => item is null ? new Border() : RealizeTranscriptItem(item), false);
        MessageFlow.ContainerClearing += (_, args) =>
        {
            if (args.Container.DataContext is VirtualTranscriptItem item) ForgetRealizedTranscriptItem(item);
        };
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _selectionPinnedKey = null;
            _pinnedTextSelection.Clear();
            foreach (var key in _interactionRows.Keys.ToArray())
                if (!_interactionRows[key].Host.IsKeyboardFocusWithin) _interactionRows.Remove(key);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        MessageScroller.SizeChanged += (_, _) => RestoreTranscriptAnchor(CaptureTranscriptAnchor());
        MessageScroller.AddHandler(PointerWheelChangedEvent, (_, _) => _transcriptGeneration++, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private Control RealizeTranscriptItem(VirtualTranscriptItem item)
    {
        _virtualRealizations++;
        StackPanel? host = null;
        var retainedInteraction = false;
        if (_interactionRows.Remove(item.Key, out var interacting) && ReferenceEquals(interacting.Item, item))
        {
            host = interacting.Host;
            retainedInteraction = true;
            if (host.Parent is ContentPresenter presenter) presenter.Content = null;
        }
        var parked = _virtualParked.FirstOrDefault(entry => entry.Session == item.Session).Rows;
        if (parked is not null && parked.Remove(item.Key, out var saved) && saved.Item.Same(item))
        {
            host = saved.Host;
            _openReusedRows = true;
        }
        host ??= new StackPanel { Spacing = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, item.InGroup ? 2 : 10), ClipToBounds = true };
        host.Width = double.IsNaN(MessageColumn.Width) ? 780 : MessageColumn.Width;
        if (host.Children.Count == 0) PaintVirtualTranscriptItem(item, host);
        _realizedTranscript[item.Key] = (item, host);
        // A pinned interaction retains its original handlers along with its control tree.
        if (retainedInteraction) return host;
        host.AttachedToVisualTree += (_, _) =>
        {
            if (item.Session != _renderedConversationId) return;
            _realizedTranscript[item.Key] = (item, host);
            if (_selectionPinnedKey == item.Key)
                Dispatcher.UIThread.Post(() =>
                {
                    if (_selectionPinnedKey != item.Key) return;
                    foreach (var (block, start, end) in _pinnedTextSelection)
                    {
                        block.SelectionStart = start;
                        block.SelectionEnd = end;
                    }
                }, DispatcherPriority.Loaded);
            ScheduleMarkdownPromotion();
        };
        host.AddHandler(PointerPressedEvent, (_, _) => _selectionPinnedKey = item.Key,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        host.AddHandler(PointerMovedEvent, (_, _) =>
        {
            if (_selectionPinnedKey == item.Key) CapturePinnedSelection(host);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        host.DetachedFromVisualTree += (_, _) => ForgetRealizedTranscriptItem(item, host);
        return host;
    }

    private void CapturePinnedSelection(StackPanel host)
    {
        var selected = host.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .Where(block => block.SelectionEnd != block.SelectionStart)
            .Select(block => (block, block.SelectionStart, block.SelectionEnd)).ToList();
        if (selected.Count == 0) return;
        _pinnedTextSelection.Clear();
        _pinnedTextSelection.AddRange(selected);
    }

    private void ForgetRealizedTranscriptItem(VirtualTranscriptItem item, StackPanel? expected = null)
    {
        if (_realizedTranscript.TryGetValue(item.Key, out var realized)
            && ReferenceEquals(item, realized.Item)
            && (expected is null || ReferenceEquals(expected, realized.Host)))
        {
            if (_selectionPinnedKey == item.Key) CapturePinnedSelection(realized.Host);
            if (realized.Host.IsKeyboardFocusWithin || _selectionPinnedKey == item.Key
                || (item.Turn is not null && item.Source >= 0 && _projection is not null
                    && _inlineDrafts.ContainsKey((item.Session, long.Parse(item.Key[(item.Key.IndexOf(':') + 1)..])))))
                _interactionRows[item.Key] = realized;
            _realizedTranscript.Remove(item.Key);
            _pendingMarkdownRows.RemoveAll(pending => ReferenceEquals(pending.Parent, realized.Host));
        }
    }

    private static bool IsLiteralTranscriptProse(string text)
    {
        // Only the unambiguous single-line subset bypasses Markdown. Links, lists, escapes and formatting
        // continue through the established renderer; ordinary prose keeps its selectable text control.
        if (text.Length == 0 || char.IsDigit(text[0]) || char.IsWhiteSpace(text[0])
            || text.Contains("  ", StringComparison.Ordinal) || text.Contains("www.", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var character in text)
            if ("\n\r\t\\`*_#[]<>|~=:+-!&".Contains(character)) return false;
        return true;
    }

    private void PaintVirtualTranscriptItem(VirtualTranscriptItem item, StackPanel host)
    {
        // Source indices are only hints; identities are resolved at the last possible point.
        if (item.Special is { } special)
        {
            if (special.Parent is Panel old) old.Children.Remove(special);
            host.Children.Add(special);
            return;
        }
        if (_chat.ActiveConversation is not { } conversation || conversation.Id != item.Session
            || _projection is null || !_projection.TryResolve(long.Parse(item.Key[(item.Key.IndexOf(':') + 1)..]), out var index))
            return;
        var turn = conversation.Messages[index];
        switch (item.Kind)
        {
            case "group":
            {
                var open = IsFoldOpen(_expandedGroups, item.Session, item.GroupKey!);
                var head = new ToggleButton { Classes = { "activity-group-head" }, IsChecked = open,
                    Content = BuildActivityGroupHead(item.GroupTurns!, item.GroupCalls) };
                head.IsCheckedChanged += (_, _) =>
                {
                    if (item.Session != _chat.ViewedConversationId) return;
                    _stickToBottom = false;
                    RememberFold(_expandedGroups, item.Session, item.GroupKey!, head.IsChecked == true);
                    RenderMessages();
                };
                host.Children.Add(new Border { Classes = { "activity-group" }, Child = head });
                break;
            }
            case "notice":
                host.Children.Add(BuildNoticeRow(turn.Text, false, null));
                break;
            case "exchange":
                var exchange = BuildActivityRowShell(item.Session, index, item.Call, turn);
                exchange.Tag = item.Call?.ToolName ?? "";
                host.Children.Add(exchange);
                break;
            case "thought":
                if (BuildThoughtRowForTurn(item.Session, index, turn, true) is { } thought) host.Children.Add(thought);
                break;
            default:
                AppendRenderedTurn(item.Session, index, turn, item.ToolName, item.Last, item.Markdown || _promotedTranscriptKeys.Contains(_projection.KeyAt(index)), host, item.InGroup);
                break;
        }
    }

    private void ParkVirtualTranscript()
    {
        if (_renderedConversationId is not { } session || _realizedTranscript.Count == 0) return;
        var cached = new Dictionary<string, (VirtualTranscriptItem Item, StackPanel Host)>();
        foreach (var (key, value) in _realizedTranscript.Take(ParkedRowLimit).ToArray())
        {
            // Keep only realized rows, detached from their old item presenter. The wrapper is never reused.
            var host = new StackPanel { Spacing = 0, Margin = value.Host.Margin, ClipToBounds = true };
            foreach (var child in value.Host.Children.ToArray())
            {
                value.Host.Children.Remove(child);
                host.Children.Add(child);
            }
            if (value.Item.Special is null) cached[key] = (value.Item, host);
        }
        var old = _virtualParked.First;
        while (old is not null)
        {
            var next = old.Next;
            if (old.Value.Session == session) _virtualParked.Remove(old);
            old = next;
        }
        _virtualParked.AddFirst((session, cached));
        while (_virtualParked.Count > ParkedTranscriptDepth) _virtualParked.RemoveLast();
    }

    private void RenderVirtualTranscript()
    {
        if (_chat is null) return;
        var conversation = _chat.ActiveConversation;
        var switched = _renderedConversationId != conversation?.Id;
        var anchor = switched ? null : CaptureTranscriptAnchor();
        var following = switched || _stickToBottom;
        if (switched)
        {
            _virtualOpening = true;
            ParkVirtualTranscript();
            _realizedTranscript.Clear();
            _pendingMarkdownRows.Clear();
            _transcriptGeneration++;
            _interactionRows.Clear();
            _selectionPinnedKey = null;
            _pinnedTextSelection.Clear();
        }
        PruneFoldMemory();
        var alive = _chat.Conversations.Select(session => session.Id).ToHashSet();
        foreach (var key in _inlineDrafts.Keys.Where(key => !alive.Contains(key.Session) && key.Session != conversation?.Id).ToArray()) _inlineDrafts.Remove(key);
        for (var node = _virtualParked.First; node is not null;)
        {
            var next = node.Next;
            if (!alive.Contains(node.Value.Session)) _virtualParked.Remove(node);
            node = next;
        }
        _promotedTranscriptKeys = conversation is null ? [] : _promotedByConversation.GetValue(conversation, _ => []);
        _projection = conversation is null ? null : _projectionStates.GetValue(conversation, _ => new());
        _virtualSnapshot = conversation is null ? null : _projection!.Build(conversation, _chat.ShowThinking, int.MaxValue);
        if (_projection is not null)
        {
            foreach (var memory in new[] { _expandedGroups, _expandedRows })
                if (memory.TryGetValue(conversation!.Id, out var expanded))
                    expanded.RemoveWhere(key => !long.TryParse(key.AsSpan(key.IndexOf(':') + 1), out var occurrence)
                        || !_projection.TryResolve(occurrence, out _));
            _promotedTranscriptKeys.RemoveWhere(key => !_projection.TryResolve(key, out _));
            foreach (var key in _inlineDrafts.Keys.Where(key => key.Session == conversation!.Id && !_projection.TryResolve(key.Key, out _)).ToArray())
                _inlineDrafts.Remove(key);
        }
        _renderedConversationId = conversation?.Id;
        if (_live is { } live && (conversation is null || _chat.RunFor(conversation.Id) is not { IsStreaming: true } run || live.Run != run)) CloseLive();
        var items = new List<VirtualTranscriptItem>();
        if (conversation is not null)
        {
            string Key(int source, string kind) => kind + ":" + _projection!.KeyAt(source);
            var names = TranscriptProjection.CallNames(conversation);
            if (ForkNotice.IsVisible) items.Add(new() { Key = "fork", Kind = "special", Session = conversation.Id, Special = ForkNotice });
            foreach (var entry in _virtualSnapshot!.Entries)
            {
                if (entry is TranscriptMessage message)
                    items.Add(new() { Key = Key(message.SourceIndex, "message"), Kind = "message", Session = conversation.Id,
                        Source = message.SourceIndex, Turn = conversation.Messages[message.SourceIndex], Markdown = message.Markdown,
                        Last = message.SourceIndex == conversation.Messages.Count - 1, Thinking = _chat.ShowThinking });
                else if (entry is TranscriptNotice notice)
                    items.Add(new() { Key = Key(notice.SourceIndex, "notice"), Kind = "notice", Session = conversation.Id,
                        Source = notice.SourceIndex, Turn = conversation.Messages[notice.SourceIndex] });
                else if (entry is TranscriptActivity activity)
                {
                    var piece = activity.Piece;
                    var first = _projection!.TryResolve(piece.TurnKeys![0], out var start) ? start : -1;
                    if (first < 0) continue;
                    var groupKey = RowKey(first, "group");
                    var slice = new List<(int Index, ChatTurn Turn, string? ToolName)>();
                    // Source positions may contain invisible system turns, so walk the occurrence keys.
                    foreach (var key in piece.TurnKeys!)
                        if (_projection.TryResolve(key, out var source))
                        {
                            var turn = conversation.Messages[source];
                            slice.Add((source, turn, turn.ToolCallId is { } id ? names.GetValueOrDefault(id) : null));
                        }
                    var calls = new Dictionary<string, ChatTurn>();
                    foreach (var slot in slice)
                        if (slot.Turn.Role == ChatRoles.Assistant && slot.Turn.ToolCallId is { } callId) calls[callId] = slot.Turn;
                    if (piece.Folded)
                    {
                        var summary = GroupSummary(slice, calls.Count);
                        items.Add(new() { Key = Key(first, "group"), Kind = "group", Session = conversation.Id,
                            Source = first, Turn = conversation.Messages[first], GroupTurns = slice, GroupKey = groupKey,
                            GroupCalls = calls.Count, GroupStamp = summary.ToString() + ":" + IsFoldOpen(_expandedGroups, conversation.Id, groupKey) });
                        if (!IsFoldOpen(_expandedGroups, conversation.Id, groupKey)) continue;
                    }
                    foreach (var row in piece.Children)
                    {
                        var turn = conversation.Messages[row.SourceIndex];
                        var kind = row.Kind == TranscriptRowKind.ToolExchange ? "exchange" : row.Kind == TranscriptRowKind.Thought ? "thought" : "message";
                        var call = turn.ToolCallId is { } id ? calls.GetValueOrDefault(id) : null;
                        items.Add(new() { Key = Key(row.SourceIndex, kind), Kind = kind, Session = conversation.Id,
                            Source = row.SourceIndex, Turn = turn, Call = call, ToolName = row.ToolName, Markdown = row.Markdown,
                            InGroup = true, GroupKey = piece.Folded ? groupKey : null, Thinking = _chat.ShowThinking,
                            Last = row.SourceIndex == conversation.Messages.Count - 1 });
                    }
                }
            }
            var currentRun = _chat.RunFor(conversation.Id);
            SyncRunningCommandRow(conversation.Id, currentRun);
            if (currentRun is { IsStreaming: true }) AttachLive(currentRun);
            RenderNotice();
            AddVirtualTails(items);
        }
        ReconcileTranscriptItems(items);
        if (switched)
        {
            // Enable the viewport buffers after landing, avoiding transient rows during the initial jump.
            MessageFlow.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { CacheLength = 0 });
            // The native infinite offset is re-coerced as the extent becomes known. Set it before the first
            // layout so the oldest page is never fully materialized just to jump away from it again.
            MessageScroller.ScrollToEnd();
        }
        EmptyState.IsVisible = conversation is null || items.Count == 0;
        _stickToBottom = following;
        if (following) ScrollToEndIfSticky();
        else RestoreTranscriptAnchor(anchor);
        ScheduleMarkdownPromotion();
    }

    private void AddVirtualTails(List<VirtualTranscriptItem> items)
    {
        var session = _renderedConversationId ?? "";
        if (_runningCommand is { } command) items.Add(new() { Key = "command", Kind = "special", Session = session, Special = command.Shell });
        if (_live is { } live) items.Add(new() { Key = "live", Kind = "special", Session = session, Special = live.Row });
        if (NoticeHost.Children.Count > 0) items.Add(new() { Key = "status", Kind = "special", Session = session, Special = NoticeHost });
    }

    private void SyncVirtualTails()
    {
        if (_reconcilingTranscript) return;
        var items = _transcriptItems.Where(item => item.Special is null || item.Key == "fork").ToList();
        AddVirtualTails(items);
        ReconcileTranscriptItems(items);
    }

    private void ReconcileTranscriptItems(List<VirtualTranscriptItem> incoming)
    {
        var old = _transcriptItems.ToDictionary(item => item.Key);
        for (var i = 0; i < incoming.Count; i++)
            if (old.TryGetValue(incoming[i].Key, out var existing) && existing.Same(incoming[i]))
            {
                if (existing.Kind == "group")
                {
                    existing.GroupTurns = incoming[i].GroupTurns;
                    existing.GroupCalls = incoming[i].GroupCalls;
                }
                if (existing.Kind == "group" && existing.GroupStamp != incoming[i].GroupStamp)
                {
                    existing.GroupTurns = incoming[i].GroupTurns;
                    existing.GroupStamp = incoming[i].GroupStamp;
                    if (_realizedTranscript.TryGetValue(existing.Key, out var view)
                        && view.Host.Children.FirstOrDefault() is Border { Child: ToggleButton head })
                        head.Content = BuildActivityGroupHead(existing.GroupTurns!, existing.GroupCalls);
                }
                existing.Source = incoming[i].Source;
                incoming[i] = existing;
            }
        for (var i = 0; i < incoming.Count; i++) incoming[i].Ordinal = i;
        var prefix = 0;
        while (prefix < incoming.Count && prefix < _transcriptItems.Count && ReferenceEquals(incoming[prefix], _transcriptItems[prefix])) prefix++;
        var suffix = 0;
        while (suffix < incoming.Count - prefix && suffix < _transcriptItems.Count - prefix
            && ReferenceEquals(incoming[^(suffix + 1)], _transcriptItems[^(suffix + 1)])) suffix++;
        var removed = _transcriptItems.Count - prefix - suffix;
        if (removed > 0) _transcriptItems.RemoveRange(prefix, removed);
        var added = incoming.Count - prefix - suffix;
        if (added > 0) _transcriptItems.InsertRange(prefix, incoming.GetRange(prefix, added));
    }

    private sealed record TranscriptAnchor(string Session, string Key, double Top, int Index, string[] Order);

    private TranscriptAnchor? CaptureTranscriptAnchor()
    {
        if (_stickToBottom || _thumbDragging || _renderedConversationId is null) return null;
        foreach (var realized in _realizedTranscript.Values.OrderBy(row => row.Item.Ordinal))
        {
            var item = realized.Item;
            var i = item.Ordinal;
            if (!_realizedTranscript.TryGetValue(item.Key, out var row)) continue;
            if (row.Host.TranslatePoint(default, MessageScroller) is not { } point
                || point.Y + row.Host.Bounds.Height <= 0 || point.Y >= MessageScroller.Viewport.Height) continue;
            return new(_renderedConversationId, item.Key, point.Y, i, _transcriptItems.Select(entry => entry.Key).ToArray());
        }
        return null;
    }

    private void RestoreTranscriptAnchor(TranscriptAnchor? anchor)
    {
        if (anchor is null) return;
        var generation = _transcriptGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _transcriptGeneration || anchor.Session != _renderedConversationId || _thumbDragging || _stickToBottom) return;
            var keys = _transcriptItems.Select(item => item.Key).ToHashSet();
            var key = keys.Contains(anchor.Key) ? anchor.Key : anchor.Order.Skip(anchor.Index + 1).FirstOrDefault(keys.Contains)
                ?? anchor.Order.Take(anchor.Index).Reverse().FirstOrDefault(keys.Contains);
            if (key is null) return;
            var index = _transcriptItems.Select((item, at) => (item, at)).First(pair => pair.item.Key == key).at;
            if (!_realizedTranscript.ContainsKey(key))
            {
                MessageFlow.ScrollIntoView(index);
                MessageFlow.UpdateLayout();
            }
            if (_realizedTranscript.TryGetValue(key, out var row) && row.Host.TranslatePoint(default, MessageScroller) is { } point)
                MessageScroller.Offset = MessageScroller.Offset.WithY(Math.Max(0, MessageScroller.Offset.Y + point.Y - anchor.Top));
        }, DispatcherPriority.Loaded);
    }

    private void UpdateVirtualOverscan()
    {
        if (MessageFlow.ItemsPanelRoot is not VirtualizingStackPanel panel || MessageScroller.Viewport.Height <= 0) return;
        if (_virtualOpening)
        {
            panel.CacheLength = 0;
            return;
        }
        var viewport = MessageScroller.Viewport.Height;
        var range = Math.Max(0, MessageScroller.Extent.Height - viewport);
        var nearestEdge = Math.Max(0, Math.Min(MessageScroller.Offset.Y, range - MessageScroller.Offset.Y));
        // The framework transfers an unavailable buffer at either end to the other side. Compensate so
        // the tail opens with one extra viewport above it, while the middle keeps one on either side.
        panel.CacheLength = 0.5 * (1 + Math.Min(1, nearestEdge / viewport));
    }

    private bool _virtualFollowQueued;
    private string? _virtualFollowSession;
    private long _virtualFollowGeneration;

    private void QueueVirtualFollow()
    {
        _virtualFollowSession = _renderedConversationId;
        _virtualFollowGeneration = _transcriptGeneration;
        if (_virtualFollowQueued) return;
        _virtualFollowQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _virtualFollowQueued = false;
            if (_thumbDragging || _virtualFollowSession != _renderedConversationId
                || _virtualFollowGeneration != _transcriptGeneration || _transcriptItems.Count == 0)
            {
                _virtualOpening = false;
                return;
            }
            MessageFlow.ScrollIntoView(_transcriptItems.Count - 1);
            if (MessageFlow.ContainerFromIndex(_transcriptItems.Count - 1) is { } tail)
                tail.BringIntoView(new Rect(0, Math.Max(0, tail.Bounds.Height - 1), 1, 17));
            UpdateScrollAffordance();
            _virtualOpening = false;
            UpdateVirtualOverscan();
        }, DispatcherPriority.Loaded);
    }

    internal void ClickScrollToBottomForCheck() => ScrollToBottomButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    internal SelectableTextBlock? SelectFirstUserTextForCheck()
    {
        var block = RealizedTranscriptRows.SelectMany(row => row.GetLogicalDescendants()).OfType<SelectableTextBlock>()
            .FirstOrDefault(text => text.Classes.Contains("turn-text") && text.Text == "editable question");
        if (block is null) return null;
        block.Focus();
        block.SelectionStart = 0;
        block.SelectionEnd = 8;
        var item = _realizedTranscript.Values.First(row => row.Host.GetLogicalDescendants().Contains(block));
        _selectionPinnedKey = item.Item.Key;
        CapturePinnedSelection(item.Host);
        return block;
    }
    internal void ReleaseVirtualSelectionForCheck()
    {
        _selectionPinnedKey = null;
        _pinnedTextSelection.Clear();
        _interactionRows.Clear();
        InputBox.Focus();
    }
    internal int QueueMarkdownPromotionForCheck(int sourceIndex)
    {
        var item = _transcriptItems.First(entry => entry.Source == sourceIndex && entry.Kind == "message");
        MessageFlow.UpdateLayout();
        ScrollVirtualItemForCheck(item.Ordinal);
        MessageFlow.UpdateLayout();
        if (!_realizedTranscript.TryGetValue(item.Key, out var row) || _chat.ActiveConversation is not { } conversation) return 0;
        row.Host.Children.Clear();
        _pendingMarkdownRows.RemoveAll(pending => ReferenceEquals(pending.Parent, row.Host));
        AppendRenderedTurn(item.Session, sourceIndex, conversation.Messages[sourceIndex], item.ToolName,
            item.Last, false, row.Host, item.InGroup);
        ScheduleMarkdownPromotion();
        return _pendingMarkdownRows.Count;
    }
    internal bool ReusedVirtualRowsForCheck => _openReusedRows;
    internal int VirtualRealizationsForCheck => _virtualRealizations;
    internal int RealizedEditorCountForCheck => RealizedTranscriptRows.Sum(row => row.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Count());
    internal string[] VirtualExchangeIdsForCheck => _transcriptItems.Where(item => item.Kind == "exchange").Select(item => item.Turn!.ToolCallId ?? "").ToArray();
    internal int VirtualItemCountForCheck => _transcriptItems.Count;
    internal int RealizedItemCountForCheck => _realizedTranscript.Count;
    internal int ParkedVirtualRowsForCheck => _virtualParked.Sum(entry => entry.Rows.Count);
    internal string[] RealizedKeysForCheck => _transcriptItems.Where(item => _realizedTranscript.ContainsKey(item.Key)).Select(item => item.Key).ToArray();
    internal void ScrollVirtualItemForCheck(int index)
    {
        _transcriptGeneration++;
        _stickToBottom = false;
        MessageFlow.ScrollIntoView(Math.Clamp(index, 0, Math.Max(0, _transcriptItems.Count - 1)));
    }
    internal bool VirtualIndexRealizedForCheck(int index) => index >= 0 && index < _transcriptItems.Count
        && _realizedTranscript.ContainsKey(_transcriptItems[index].Key);
    internal (string? Key, double Top) VirtualAnchorForCheck
    {
        get { var anchor = CaptureTranscriptAnchor(); return (anchor?.Key, anchor?.Top ?? 0); }
    }
    internal int VirtualAnchorSourceForCheck => CaptureTranscriptAnchor() is { } anchor
        ? _transcriptItems.First(item => item.Key == anchor.Key).Source : -1;
    internal void ExpandVirtualGroupsForCheck()
    {
        foreach (var item in _transcriptItems.Where(item => item.Kind == "group").ToArray())
            RememberFold(_expandedGroups, item.Session, item.GroupKey!, true);
        RenderMessages();
    }
    internal IReadOnlyDictionary<string, string> RealizedStructureForCheck => _realizedTranscript.Values
        .Where(row => row.Item.Special is null && row.Host.Children.Count > 0)
        .ToDictionary(row => row.Item.Key, row => row.Item.Kind switch
        {
            "group" => row.Host.Children[0].Classes.Contains("activity-group") ? "G" : "invalid",
            "thought" => PaintedChildToken(row.Host.Children[0]),
            "exchange" => PaintedChildToken(row.Host.Children[0]),
            "message" when row.Item.InGroup => PaintedChildToken(row.Host.Children[0]),
            _ => PaintedToken(row.Host.Children[0]),
        });

}
