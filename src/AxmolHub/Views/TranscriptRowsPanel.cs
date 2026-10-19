using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace AxmolHub;

/// <summary>
/// A deterministic single-column virtualizer for the transcript.
///
/// <para>It exists because <see cref="VirtualizingStackPanel"/>'s layout is driven by an average measured row
/// height: with a transcript whose rows range from one line to a plan or a pasted log, that average is revised
/// on every realization and the estimated extent oscillates (observed 23k ↔ 68k px on a real session). A jump
/// to the end then lands where the estimate believed, not where the newest row is, and the offset can be
/// clamped back into the middle.</para>
///
/// <para>This panel keeps one cumulative offset per item, so the index↔pixel mapping is a single array that
/// every measure, arrange and jump agrees on. Unmeasured rows use a fixed estimate; a measured height is cached
/// by the item object (the same object the panel already reuses across reconciles) and the suffix is shifted in
/// place. Realization covers the viewport plus one viewport of overscan, and nothing else.</para>
/// </summary>
public sealed class TranscriptRowsPanel : VirtualizingPanel
{
    private const double EstimatedRowHeight = 64;
    private const double HeightEpsilon = 0.1;

    private readonly Dictionary<object, double> _heights = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _realizedIndices = [];
    private readonly Dictionary<int, Control> _realized = new();
    private readonly Dictionary<Control, int> _containerIndices = new(ReferenceEqualityComparer.Instance);
    private ScrollViewer? _scroll;
    private double[] _offsets = [0];
    private double _width = double.NaN;
    private bool _dirty = true;
    private int _scrollTarget = -1;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
        ClearRealized();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // An extent-only change is our own measurement growing the panel; arranging the realized range is
        // enough, and re-measuring on every such change is what turns "measure the last page" into a layout
        // loop. Only a moved viewport needs a new realization range.
        if (Math.Abs(e.OffsetDelta.Y) > 0.01 || Math.Abs(e.ViewportDelta.Y) > 0.01) InvalidateMeasure();
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // Do not tear the realized range down for every collection change: a tool call landing at the end
        // must leave the group head the person opened as the same control. The measure validates each kept
        // container against the item now at its index and drops the ones that no longer match.
        _dirty = true;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        if (items.Count == 0)
        {
            ClearRealized();
            _offsets = [0];
            _dirty = true;
            return default;
        }

        var width = availableSize.Width;
        if (double.IsInfinity(width) || double.IsNaN(width)) width = 780;
        ValidateRealized(items);
        EnsureOffsets(items, width);
        // Remember whether the reader was at the end before this pass. Measuring the last page grows the
        // extent; if the offset is only clamped up by that growth, the next pass realizes the rows between the
        // old and new ends and the walk repeats page by page. Jumping straight to the new end realizes the
        // last page again, so the cascade settles in a couple of passes instead of one per page.
        var viewport = _scroll?.Viewport.Height ?? availableSize.Height;
        if (double.IsNaN(viewport) || viewport <= 0) viewport = Math.Max(1, availableSize.Height);
        var offset = _scroll?.Offset.Y ?? 0;
        // No extra viewport of overscan: the range already includes one row on each side, and for the
        // code/table/reasoning shapes every extra realized row is a Markdown document and a code editor. The
        // benchmark's code-table open tripled when this was a full viewport.
        var overscan = 0d;
        // Include one row on each side of the computed range: a row taller than the viewport starts before
        // offset - overscan and still covers the top of the viewport, so starting at the first row whose top
        // is inside the overscan leaves that row unrealized and a blank band above the content.
        var first = Math.Max(0, FirstIndexAtOrAfter(Math.Max(0, offset - overscan)) - 1);
        var last = Math.Min(items.Count - 1, FirstIndexAtOrAfter(offset + viewport + overscan) + 1);
        if (last < first) last = first;

        // Recycle everything outside the new range before measuring the range, so a scroll through a long
        // history leaves only one viewport of controls alive.
        foreach (var index in _realizedIndices.ToArray())
            if (index < first || index > last) RemoveContainer(index);

        var maxWidth = 0d;
        for (var index = first; index <= last; index++)
        {
            var item = items[index]!;
            var container = GetOrCreate(items, index);
            container.Measure(new Size(width, double.PositiveInfinity));
            maxWidth = Math.Max(maxWidth, container.DesiredSize.Width);
            var height = container.DesiredSize.Height;
            if (_heights.TryGetValue(item, out var known) && Math.Abs(known - height) <= HeightEpsilon) continue;
            _heights[item] = height;
            var delta = height - (_offsets[index + 1] - _offsets[index]);
            if (Math.Abs(delta) > HeightEpsilon)
            {
                for (var at = index + 1; at < _offsets.Length; at++) _offsets[at] += delta;
                // A row above the reading position growing or shrinking must not move the words under the
                // pointer; move the offset with it. The rows below only extend the extent.
                // Do not move the offset from inside MeasureOverride. A height change above the viewport would
                // shift the offset, realize a different range, measure it, shift again — a cascade the layout
                // manager correctly calls an infinite loop. The reading position is owned by ChatPanel's anchor
                // restore and follow, which run after the layout, not during it.
            }
        }

        if (_scrollTarget is >= 0 && _scrollTarget < items.Count && _scroll is { } targetScroll)
        {
            // The first visit to an unmeasured region jumps using estimates; measuring the rows there moves
            // their true offsets. Correct the offset to the target once from the measured offsets, so the
            // requested row is actually in front of the reader instead of being left where the estimate
            // placed it.
            var target = Math.Clamp(_offsets[_scrollTarget], 0,
                Math.Max(0, _offsets[^1] - targetScroll.Viewport.Height));
            _scrollTarget = -1;
            if (Math.Abs(targetScroll.Offset.Y - target) > 1)
                targetScroll.Offset = targetScroll.Offset.WithY(target);
        }
        return new Size(double.IsInfinity(width) ? maxWidth : width, _offsets[^1]);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = Items;
        foreach (var index in _realizedIndices)
        {
            if (index < 0 || index >= items.Count) continue;
            var container = _realized[index];
            var top = _offsets[index];
            var height = Math.Max(0, _offsets[index + 1] - top);
            container.Arrange(new Rect(0, top, finalSize.Width, height));
        }
        return finalSize;
    }

    protected override Control? ContainerFromIndex(int index)
        => _realized.TryGetValue(index, out var container) ? container : null;

    protected override int IndexFromContainer(Control container)
        => _containerIndices.TryGetValue(container, out var index) ? index : -1;

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values;

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
        => null;

    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;
        if (index < 0 || index >= items.Count) return null;
        _scrollTarget = index;
        if (_scroll is { } scroller)
        {
            if (_dirty) EnsureOffsets(items, double.IsNaN(_width) ? 780 : _width);
            var target = _offsets[index];
            var maximum = Math.Max(0, _offsets[^1] - scroller.Viewport.Height);
            scroller.Offset = scroller.Offset.WithY(Math.Clamp(target, 0, maximum));
        }
        InvalidateMeasure();
        return _realized.TryGetValue(index, out var container) ? container : null;
    }

    private void EnsureOffsets(IReadOnlyList<object?> items, double width)
    {
        if (!_dirty && _offsets.Length == items.Count + 1 && Math.Abs(width - _width) <= HeightEpsilon) return;
        if (!double.IsNaN(_width) && Math.Abs(width - _width) > HeightEpsilon) _heights.Clear();
        _width = width;
        var offsets = new double[items.Count + 1];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]!;
            offsets[index + 1] = offsets[index]
                + (_heights.TryGetValue(item, out var height) ? height : EstimatedRowHeight);
        }
        _offsets = offsets;
        _dirty = false;
    }

    private int FirstIndexAtOrAfter(double y)
    {
        var low = 0;
        var high = _offsets.Length - 1;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (_offsets[mid] < y) low = mid + 1;
            else high = mid;
        }
        return Math.Min(low, Math.Max(0, _offsets.Length - 2));
    }

    private Control GetOrCreate(IReadOnlyList<object?> items, int index)
    {
        if (_realized.TryGetValue(index, out var existing)) return existing;
        var item = items[index]!;
        var generator = ItemContainerGenerator!;
        Control container;
        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            container = generator.CreateContainer(item, index, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }
        else
        {
            container = (Control)item;
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }
        _realized[index] = container;
        _containerIndices[container] = index;
        _realizedIndices.Add(index);
        return container;
    }

    private void ValidateRealized(IReadOnlyList<object?> items)
    {
        foreach (var index in _realizedIndices.ToArray())
        {
            if (index < items.Count && ReferenceEquals(_realized[index].DataContext, items[index])) continue;
            RemoveContainer(index);
        }
    }

    private void RemoveContainer(int index)
    {
        if (!_realized.Remove(index, out var container)) return;
        _containerIndices.Remove(container);
        _realizedIndices.Remove(index);
        ItemContainerGenerator?.ClearItemContainer(container);
        RemoveInternalChild(container);
    }

    private void ClearRealized()
    {
        foreach (var index in _realizedIndices.ToArray()) RemoveContainer(index);
        _realized.Clear();
        _containerIndices.Clear();
        _realizedIndices.Clear();
    }
}
