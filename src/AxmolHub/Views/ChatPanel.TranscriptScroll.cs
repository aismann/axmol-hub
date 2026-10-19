using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AxmolHub;

public partial class ChatPanel
{
    private ScrollBar? _transcriptBar;
    private double _logicalMaximum;
    private double _logicalViewport;
    private double _logicalValue;
    private bool _mappingTranscriptBar;
    private int _mappedTranscriptCount = -1;
    private string? _mappedTranscriptSession;
    private bool _bottomMappingKnown;
    private double _heldTravel;
    private double _heldMaximum;
    private double _heldLogicalValue;
    private VirtualTranscriptItem[]? _heldItems;
    private bool _discardTranscriptDrag;
    private Thumb? _heldThumb;

    private bool TranscriptActuallyScrollable()
    {
        if (_transcriptItems.Count == 0) return false;
        if (_realizedTranscript.Count < _transcriptItems.Count) return true;
        var first = MessageFlow.ContainerFromIndex(0);
        var last = MessageFlow.ContainerFromIndex(_transcriptItems.Count - 1);
        if (first?.TranslatePoint(default, MessageScroller) is not { } top
            || last?.TranslatePoint(new Point(0, last.Bounds.Height), MessageScroller) is not { } bottom) return true;
        return bottom.Y - top.Y + MessageColumn.Margin.Top + MessageColumn.Margin.Bottom
            > MessageScroller.Viewport.Height + StickEpsilon;
    }

    private bool TranscriptTailVisible()
        => _transcriptItems.Count == 0 || MessageFlow.ContainerFromIndex(_transcriptItems.Count - 1) is { } tail
            && tail.TranslatePoint(new Point(0, tail.Bounds.Height), MessageScroller) is { } point
            && point.Y >= 0 && point.Y <= MessageScroller.Viewport.Height + StickEpsilon;

    private void UpdateTranscriptScrollMapping()
    {
        if (_mappingTranscriptBar || _thumbDragging) return;
        var bar = TranscriptScrollBar;
        _transcriptBar = bar;
        bar.IsVisible = TranscriptActuallyScrollable();
        if (_transcriptItems.Count == 0) return;
        if (_mappedTranscriptSession != _renderedConversationId || _mappedTranscriptCount != _transcriptItems.Count)
        {
            _mappedTranscriptSession = _renderedConversationId;
            _mappedTranscriptCount = _transcriptItems.Count;
            _bottomMappingKnown = false;
        }
        var first = _realizedTranscript.Values.OrderBy(row => row.Item.Ordinal)
            .Select(row => (row.Item, Container: MessageFlow.ContainerFromIndex(row.Item.Ordinal)))
            .Where(row => row.Container is { Bounds.Height: > 0 })
            .Select(row => (row.Item, Container: row.Container!, Top: row.Container!.TranslatePoint(default, MessageScroller)?.Y))
            .FirstOrDefault(row => row.Top is { } y && y + row.Container.Bounds.Height > 0
                && y < MessageScroller.Viewport.Height);
        if (first.Container is null || first.Top is not { } top) return;
        var position = first.Item.Ordinal + Math.Clamp(-top / first.Container.Bounds.Height, 0, 1);
        var bottom = TranscriptTailVisible() && MessageScroller.Offset.Y + MessageScroller.Viewport.Height
            >= MessageScroller.Extent.Height - StickEpsilon;
        if (bottom) { _logicalMaximum = position; _bottomMappingKnown = true; }
        else if (!_bottomMappingKnown) _logicalMaximum = Math.Max(position, _transcriptItems.Count - 1d);
        _logicalViewport = Math.Max(0.001, _transcriptItems.Count - _logicalMaximum);
        SetTranscriptBar(position);
    }

    private void SetTranscriptBar(double value)
    {
        if (_transcriptBar is not { } bar || _mappingTranscriptBar) return;
        _mappingTranscriptBar = true;
        try
        {
            bar.Maximum = _logicalMaximum;
            bar.ViewportSize = _logicalViewport;
            bar.Value = value;
            _logicalValue = value;
        }
        finally { _mappingTranscriptBar = false; }
    }

    private void BeginTranscriptThumbDrag(VectorEventArgs change)
    {
        if (change.Source is not Thumb thumb || thumb.FindAncestorOfType<ScrollBar>() is not { } bar
            || bar != TranscriptScrollBar) return;
        UpdateTranscriptScrollMapping();
        ClearTranscriptThumbHandlers();
        _heldThumb = thumb;
        // Thumb drag events bubble only. Handle the thumb itself before Track consumes the delta.
        thumb.AddHandler(Thumb.DragDeltaEvent, OnTranscriptThumbDelta);
        thumb.AddHandler(PointerReleasedEvent, OnTranscriptThumbReleased);
        _transcriptGeneration++;
        _thumbDragging = true;
        _discardTranscriptDrag = false;
        _stickToBottom = false;
        _heldItems = _transcriptItems.ToArray();
        _heldMaximum = _logicalMaximum;
        _heldLogicalValue = _logicalValue;
        _heldTravel = Math.Max(1, (thumb.FindAncestorOfType<Track>()?.Bounds.Height ?? 1) - thumb.Bounds.Height);
    }

    private void OnTranscriptThumbDelta(object? sender, VectorEventArgs change) => MoveTranscriptThumb(change);

    private void MoveTranscriptThumb(VectorEventArgs change)
    {
        if (_discardTranscriptDrag) { change.Handled = true; return; }
        if (_heldItems is null) return;
        change.Handled = true;
        _heldLogicalValue = Math.Clamp(_heldLogicalValue + change.Vector.Y / _heldTravel * _heldMaximum, 0, _heldMaximum);
        SetTranscriptBar(_heldLogicalValue);
        ScrollTranscriptToLogical(_heldLogicalValue, _heldItems);
    }

    private void ScrollTranscriptToLogical(double position, VirtualTranscriptItem[]? order = null)
    {
        if (_transcriptItems.Count == 0) return;
        var source = order ?? _transcriptItems.ToArray();
        var at = Math.Clamp((int)position, 0, source.Length - 1);
        var key = source[at].Key;
        var item = _transcriptItems.FirstOrDefault(candidate => candidate.Key == key);
        if (item is null) return;
        MessageFlow.ScrollIntoView(item.Ordinal);
        if (MessageFlow.ContainerFromIndex(item.Ordinal) is { } container
            && container.TranslatePoint(default, MessageScroller) is { } point)
            MessageScroller.Offset = MessageScroller.Offset.WithY(Math.Max(0,
                MessageScroller.Offset.Y + point.Y + container.Bounds.Height * (position - at)));
    }

    private void EndTranscriptThumbDrag(bool restorePosition = true, bool flush = true)
    {
        var held = _heldItems is not null;
        if (restorePosition && !_discardTranscriptDrag) ClearTranscriptThumbHandlers();
        _heldItems = null;
        if (held && restorePosition) _stickToBottom = _heldLogicalValue >= _heldMaximum - 0.000001;
        _thumbDragging = false;
        if (held && restorePosition && _stickToBottom) QueueVirtualFollow();
        if (flush) FlushLiveRepaintIfPending();
        UpdateTranscriptScrollMapping();
    }

    private void ClearTranscriptThumbHandlers()
    {
        if (_heldThumb is null) return;
        _heldThumb.RemoveHandler(Thumb.DragDeltaEvent, OnTranscriptThumbDelta);
        _heldThumb.RemoveHandler(PointerReleasedEvent, OnTranscriptThumbReleased);
        _heldThumb = null;
    }

    private void OnTranscriptThumbReleased(object? sender, PointerReleasedEventArgs change)
    {
        if (!_discardTranscriptDrag) return;
        _discardTranscriptDrag = false;
        ClearTranscriptThumbHandlers();
    }

    private void PressTranscriptTrack(PointerPressedEventArgs change)
    {
        if (!change.GetCurrentPoint(TranscriptScrollBar).Properties.IsLeftButtonPressed
            || _transcriptBar is not { } bar || change.Source is not Visual source
            || (source != bar && !source.GetVisualAncestors().Contains(bar))
            || source is Thumb || source.GetVisualAncestors().OfType<Thumb>().Any()) return;
        var track = bar.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        if (track?.Thumb is not { } thumb) return;
        var top = thumb.TranslatePoint(default, track)?.Y ?? 0;
        var delta = change.GetPosition(track).Y < top ? -MessageScroller.Viewport.Height : MessageScroller.Viewport.Height;
        _stickToBottom = false;
        _transcriptGeneration++;
        MessageScroller.Offset = MessageScroller.Offset.WithY(Math.Max(0, MessageScroller.Offset.Y + delta));
        change.Handled = true;
    }
}
