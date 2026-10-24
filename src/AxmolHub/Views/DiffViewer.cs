using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>A bounded, read-only, aligned two-sided view. The projection follows FileDiff.Rows, so gutters count
/// real lines on their own side and blank alignment rows never invent a source location.</summary>
internal sealed class DiffViewer : Grid
{
    private readonly TextEditor _left;
    private readonly TextEditor _right;
    private readonly TextEditor _inline;
    private readonly List<SourceColorizer> _colorizers = [];
    private readonly int?[] _rightNumbers = [];
    private readonly int?[] _inlineNumbers = [];
    private readonly Action<string>? _quote;
    private Button? _quoteButton;
    private readonly string _path;
    private readonly int[] _changedRows;
    private readonly int[] _rowToHunk;
    private readonly bool _exactAlignment;
    private readonly Func<int, DiffHunkAction, Task<DiffHunkActionResult>>? _hunkAction;
    private TextBlock? _actionStatus;
    private Button? _stageButton;
    private Button? _unstageButton;
    private Button? _revertButton;
    private Button? _acceptButton;
    private Button? _rejectButton;
    private bool _syncing;
    private bool _inlineMode;

    internal DiffViewer(string path, string leftLabel, string rightLabel, string? before, string? after,
        Action<string>? quote = null,
        Func<int, DiffHunkAction, Task<DiffHunkActionResult>>? hunkAction = null,
        DiffHunkAction? availableHunkAction = null)
    {
        _path = path;
        _quote = quote;
        _hunkAction = hunkAction;
        RowDefinitions = new RowDefinitions("Auto,Auto,*");
        ColumnDefinitions = new ColumnDefinitions("*,*");
        var script = FileDiff.Rows(before, after);
        _exactAlignment = script.Exact;
        if (!script.Exact) _hunkAction = null;
        var leftLines = new List<string>();
        var rightLines = new List<string>();
        var leftNumbers = new List<int?>();
        var rightNumbers = new List<int?>();
        var leftKinds = new List<DiffRowKind>();
        var rightKinds = new List<DiffRowKind>();
        var inlineLines = new List<string>();
        var inlineOldNumbers = new List<int?>();
        var inlineNewNumbers = new List<int?>();
        var inlineKinds = new List<DiffRowKind>();
        _changedRows = script.Rows.Select((row, index) => (row, index))
            .Where(pair => pair.row.Kind != DiffRowKind.Equal).Select(pair => pair.index).ToArray();
        _rowToHunk = Enumerable.Repeat(-1, script.Rows.Count).ToArray();
        var hunkNumber = -1;
        var priorChangedRow = -2;
        foreach (var changedRow in _changedRows)
        {
            if (changedRow != priorChangedRow + 1) hunkNumber++;
            _rowToHunk[changedRow] = hunkNumber;
            priorChangedRow = changedRow;
        }
        foreach (var row in script.Rows)
        {
            leftLines.Add(row.OriginalLine is null ? "" : row.Text);
            rightLines.Add(row.UpdatedLine is null ? "" : row.Text);
            leftNumbers.Add(row.OriginalLine);
            rightNumbers.Add(row.UpdatedLine);
            leftKinds.Add(row.Kind);
            rightKinds.Add(row.Kind);
            var marker = row.Kind switch { DiffRowKind.Insert => "+ ", DiffRowKind.Delete => "− ", _ => "  " };
            inlineLines.Add(marker + row.Text);
            inlineOldNumbers.Add(row.Kind == DiffRowKind.Delete ? row.OriginalLine : null);
            inlineNewNumbers.Add(row.Kind == DiffRowKind.Delete ? null : row.UpdatedLine);
            inlineKinds.Add(row.Kind);
        }
        _rightNumbers = rightNumbers.ToArray();
        _left = CodeSurface.ForFile(path, string.Join('\n', leftLines));
        _right = CodeSurface.ForFile(path, string.Join('\n', rightLines));
        _inline = CodeSurface.ForFile(path, string.Join('\n', inlineLines));
        _left.Tag = "DiffLeftSurface";
        _right.Tag = "DiffRightSurface";
        _inline.Tag = "DiffInlineSurface";
        _inline.IsReadOnly = true;
        _inline.ShowLineNumbers = false;
        _inlineNumbers = inlineNewNumbers.Select((number, index) => number ?? inlineOldNumbers[index]).ToArray();
        _inline.TextArea.LeftMargins.Add(new DiffNumberMargin(_inline, _inlineNumbers, inlineKinds, rightSide: true, inline: true));
        _left.ShowLineNumbers = false;
        _right.ShowLineNumbers = false;
        _left.TextArea.LeftMargins.Add(new DiffNumberMargin(_left, leftNumbers, leftKinds, rightSide: false));
        _right.TextArea.LeftMargins.Add(new DiffNumberMargin(_right, rightNumbers, rightKinds, rightSide: true));
        _inline.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        _inline.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        _inline.IsVisible = false;
        var definition = DarkSyntax.ForBackdrop(CodeSyntax.ForFile(path));
        if (definition is not null)
        {
            var leftColorizer = new SourceColorizer(new TextDocument(before ?? ""), leftNumbers, definition);
            var rightColorizer = new SourceColorizer(new TextDocument(after ?? ""), rightNumbers, definition);
            _colorizers.Add(leftColorizer);
            _colorizers.Add(rightColorizer);
            _left.TextArea.TextView.LineTransformers.Add(leftColorizer);
            _right.TextArea.TextView.LineTransformers.Add(rightColorizer);
            var inlineOld = new SourceColorizer(new TextDocument(before ?? ""), inlineOldNumbers, definition, displayPrefix: 2);
            var inlineNew = new SourceColorizer(new TextDocument(after ?? ""), inlineNewNumbers, definition, displayPrefix: 2);
            _colorizers.Add(inlineOld);
            _colorizers.Add(inlineNew);
            _inline.TextArea.TextView.LineTransformers.Add(inlineOld);
            _inline.TextArea.TextView.LineTransformers.Add(inlineNew);
        }
        DetachedFromVisualTree += (_, _) =>
        {
            foreach (var colorizer in _colorizers) colorizer.Dispose();
            _colorizers.Clear();
        };
        _left.IsReadOnly = true;
        _right.IsReadOnly = true;
        _left.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        _right.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        _left.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        _right.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible;
        _left.TextArea.TextView.BackgroundRenderers.Add(
            new DiffBackgroundRenderer(leftKinds, rightSide: false));
        _right.TextArea.TextView.BackgroundRenderers.Add(
            new DiffBackgroundRenderer(rightKinds, rightSide: true));
        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        header.Children.Add(Header(WithSideLabel("Before", leftLabel), script.Exact));
        var rightHeader = Header(WithSideLabel("After", rightLabel), script.Exact);
        Grid.SetColumn(rightHeader, 1);
        header.Children.Add(rightHeader);
        Grid.SetColumnSpan(header, 2);
        Children.Add(header);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 4) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var settingsIcon = new Avalonia.Controls.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        settingsIcon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, new DynamicResourceExtension("Hub.Icon.Settings"));
        settingsIcon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        var viewSettings = new Button { Content = settingsIcon, Tag = "DiffViewSettings", Classes = { "viewer-action" } };
        ToolTip.SetTip(viewSettings, HubStrings.Get("DiffViewSettings"));
        // The gear opens a menu, not a context menu: same flyout, same rows and same tick as the assistant's
        // switchboard, so a choice looks the same wherever Hub offers one. Built fresh on every open, which is
        // how exactly one tick is drawn without a second copy of the state to keep in sync.
        var viewMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        FlyoutBase.SetAttachedFlyout(viewSettings, viewMenu);
        viewSettings.Click += (_, _) =>
        {
            if (viewMenu.IsOpen)
            {
                viewMenu.Hide();
                return;
            }
            viewMenu.Items.Clear();
            viewMenu.Items.Add(new MenuItem
            {
                Header = HubStrings.Get("DiffViewSection"),
                Classes = { "menu-caption" },
                IsHitTestVisible = false,
                Focusable = false,
            });
            viewMenu.Items.Add(ViewModeItem("DiffSplitMode", false));
            viewMenu.Items.Add(ViewModeItem("DiffInlineMode", true));
            FlyoutBase.ShowAttachedFlyout(viewSettings);
        };

        MenuItem ViewModeItem(string key, bool inline)
        {
            var current = _inlineMode == inline;
            var item = new MenuItem
            {
                Header = MenuRow.Build(null, HubStrings.Get(key), current),
                Tag = inline ? "DiffMenuInline" : "DiffMenuSplit",
                IsChecked = current,
                IsEnabled = !inline || script.Exact,
            };
            item.Click += (_, _) =>
            {
                // Closed first: the choice rewrites the two surfaces this flyout is anchored above.
                viewMenu.Hide();
                ApplyViewMode(inline);
            };
            return item;
        }

        void ApplyViewMode(bool inline)
        {
            _inlineMode = inline;
            _inline.IsVisible = inline;
            header.IsVisible = !inline;
            _left.IsVisible = !inline;
            _right.IsVisible = !inline;
        }
        var previous = new Button { Content = HubStrings.Get("DiffPreviousChange"), Tag = "DiffPreviousChange", IsEnabled = _changedRows.Length > 0 };
        previous.Click += (_, _) => NavigateChange(-1);
        toolbar.Children.Add(previous);
        var next = new Button { Content = HubStrings.Get("DiffNextChange"), Tag = "DiffNextChange", IsEnabled = _changedRows.Length > 0 };
        next.Click += (_, _) => NavigateChange(1);
        toolbar.Children.Add(next);
        if (_hunkAction is not null && availableHunkAction is DiffHunkAction action)
        {
            if (action == DiffHunkAction.Stage)
            {
                _stageButton = ActionButton("DiffStageHunk", DiffHunkAction.Stage);
                _revertButton = ActionButton("DiffRevertHunk", DiffHunkAction.Revert);
                toolbar.Children.Add(_stageButton);
                toolbar.Children.Add(_revertButton);
            }
            else if (action == DiffHunkAction.Unstage)
            {
                _unstageButton = ActionButton("DiffUnstageHunk", DiffHunkAction.Unstage);
                toolbar.Children.Add(_unstageButton);
            }
            else if (action == DiffHunkAction.Accept)
            {
                _acceptButton = ActionButton("DiffAcceptAiHunk", DiffHunkAction.Accept);
                _rejectButton = ActionButton("DiffRejectAiHunk", DiffHunkAction.Reject);
                toolbar.Children.Add(_acceptButton);
                toolbar.Children.Add(_rejectButton);
            }
        }
        if (_quote is not null)
        {
            _quoteButton = new Button { Content = HubStrings.Get("DiffQuoteSelection"), Tag = "DiffQuoteSelection" };
            _quoteButton.Click += (_, _) => QuoteSelection();
            toolbar.Children.Add(_quoteButton);
        }
        if (_hunkAction is not null)
        {
            _actionStatus = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            toolbar.Children.Add(_actionStatus);
        }
        actions.Children.Add(toolbar);
        Grid.SetColumn(viewSettings, 1);
        actions.Children.Add(viewSettings);
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 2);
        Children.Add(actions);
        Grid.SetRow(_left, 2);
        Children.Add(_left);
        Grid.SetRow(_right, 2);
        Grid.SetColumn(_right, 1);
        Children.Add(_right);
        Grid.SetRow(_inline, 2);
        Grid.SetColumnSpan(_inline, 2);
        Children.Add(_inline);
        if (_hunkAction is not null)
        {
            var shortcutHint = new TextBlock
            {
                Text = HubStrings.Get("DiffKeyboardShortcuts"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontSize = 11,
                Opacity = .75,
            };
            toolbar.Children.Add(shortcutHint);
        }
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        MinHeight = 240;
    }

    private int _currentChange = -1;

    private Button ActionButton(string key, DiffHunkAction action)
    {
        var button = new Button { Content = HubStrings.Get(key), Tag = key, IsEnabled = false };
        button.Click += async (_, _) =>
        {
            if (_currentChange < 0 || _currentChange >= _changedRows.Length || _hunkAction is null) return;
            var row = _changedRows[_currentChange];
            var hunk = row < _rowToHunk.Length ? _rowToHunk[row] : -1;
            if (hunk < 0) return;
            button.IsEnabled = false;
            if (_actionStatus is not null) _actionStatus.Text = "Applying selected hunk…";
            try
            {
                var result = await _hunkAction(hunk, action);
                if (_actionStatus is not null) _actionStatus.Text = result.Detail;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (_actionStatus is not null) _actionStatus.Text = "Hunk operation failed: " + ex.Message;
            }
            finally { button.IsEnabled = HasMappedCurrentHunk(); }
        };
        return button;
    }

    private bool HasMappedCurrentHunk()
    {
        if (!_exactAlignment || _currentChange < 0 || _currentChange >= _changedRows.Length) return false;
        var row = _changedRows[_currentChange];
        return row < _rowToHunk.Length && _rowToHunk[row] >= 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down)
        {
            NavigateChange(e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != (KeyModifiers.Alt | KeyModifiers.Shift)) return;
        var button = e.Key switch
        {
            Key.S => _stageButton,
            Key.U => _unstageButton,
            Key.R => _revertButton,
            Key.A => _acceptButton,
            Key.X => _rejectButton,
            _ => null,
        };
        if (button?.IsEnabled != true) return;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        e.Handled = true;
    }

    private void NavigateChange(int direction)
    {
        if (_changedRows.Length == 0) return;
        var active = _inlineMode ? _inline : _right;
        var currentLine = active.TextArea.Caret.Line - 1;
        var nextIndex = direction > 0
            ? Array.FindIndex(_changedRows, row => row > currentLine)
            : Array.FindLastIndex(_changedRows, row => row < currentLine);
        if (nextIndex < 0) nextIndex = direction > 0 ? 0 : _changedRows.Length - 1;
        _currentChange = nextIndex;
        var canApply = _hunkAction is not null && HasMappedCurrentHunk();
        if (_stageButton is not null) _stageButton.IsEnabled = canApply;
        if (_unstageButton is not null) _unstageButton.IsEnabled = canApply;
        if (_revertButton is not null) _revertButton.IsEnabled = canApply;
        if (_acceptButton is not null) _acceptButton.IsEnabled = canApply;
        if (_rejectButton is not null) _rejectButton.IsEnabled = canApply;
        if (!_inlineMode) SelectDisplayLine(_left, _changedRows[nextIndex]);
        SelectDisplayLine(active, _changedRows[nextIndex]);
    }

    private static void SelectDisplayLine(TextEditor editor, int row)
    {
        var lineNumber = Math.Clamp(row + 1, 1, editor.Document.LineCount);
        var line = editor.Document.GetLineByNumber(lineNumber);
        editor.Select(line.Offset, line.Length);
        editor.ScrollToLine(lineNumber);
    }

    private void QuoteSelection()
    {
        var editor = _inlineMode ? _inline : _right;
        var map = _inlineMode ? _inlineNumbers : _rightNumbers;
        if (_quote is null || editor.SelectionLength <= 0) return;
        var startOffset = editor.SelectionStart;
        var endOffset = startOffset + editor.SelectionLength;
        var startLine = editor.Document.GetLineByOffset(startOffset).LineNumber;
        var endLine = editor.Document.GetLineByOffset(Math.Max(startOffset, endOffset - 1)).LineNumber;
        var sourceNumbers = new List<int>();
        var selectedLines = new List<string>();
        for (var displayLine = startLine; displayLine <= endLine && displayLine <= map.Length; displayLine++)
        {
            if (map[displayLine - 1] is not int sourceNumber) continue;
            var documentLine = editor.Document.GetLineByNumber(displayLine);
            var from = Math.Max(startOffset, documentLine.Offset) - documentLine.Offset;
            var to = Math.Min(endOffset, documentLine.EndOffset) - documentLine.Offset;
            from = Math.Max(from, _inlineMode ? 2 : 0);
            if (to <= from) continue;
            var sourceText = editor.Document.GetText(documentLine.Offset + from, to - from);
            selectedLines.Add(sourceText);
            sourceNumbers.Add(sourceNumber);
        }
        if (sourceNumbers.Count == 0) return;
        var selected = string.Join('\n', selectedLines);
        if (selected.Length == 0) return;
        var suffix = FormatLineRanges(sourceNumbers);
        _quote($"{_path}:{suffix}\n{selected}");
    }

    private static string FormatLineRanges(IReadOnlyList<int> lines)
    {
        var ranges = new List<string>();
        for (var index = 0; index < lines.Count;)
        {
            var start = lines[index];
            var end = start;
            while (++index < lines.Count && lines[index] == end + 1) end = lines[index];
            ranges.Add(start == end ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : start.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-"
                  + end.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return string.Join(",", ranges);
    }

    private static string WithSideLabel(string side, string source)
        => string.Equals(source, side, StringComparison.OrdinalIgnoreCase)
            ? side : side + " · " + source;

    private static Control Header(string text, bool exact)
    {
        var label = new TextBlock { Text = exact ? text : text + " · alignment limited", Margin = new Thickness(10, 6), FontSize = 12 };
        label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(exact ? "Hub.TextSecondary" : "Hub.Warning"));
        return new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = label };
    }

    private void Sync(TextEditor source, TextEditor target)
    {
        if (_syncing || source.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } from
            || target.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } to) return;
        _syncing = true;
        try { to.Offset = new Vector(to.Offset.X, from.Offset.Y); }
        finally { _syncing = false; }
    }

    private sealed class SourceColorizer : DocumentColorizingTransformer, IDisposable
    {
        private readonly TextDocument _source;
        private readonly int?[] _sourceLines;
        private readonly DocumentHighlighter _highlighter;
        private readonly int _displayPrefix;

        internal SourceColorizer(TextDocument source, IReadOnlyList<int?> sourceLines,
            IHighlightingDefinition definition, int displayPrefix = 0)
        {
            _source = source;
            _displayPrefix = displayPrefix;
            _sourceLines = sourceLines.ToArray();
            _highlighter = new DocumentHighlighter(source, definition);
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.LineNumber > _sourceLines.Length || _sourceLines[line.LineNumber - 1] is not int sourceNumber
                || sourceNumber > _source.LineCount) return;
            var sourceLine = _source.GetLineByNumber(sourceNumber);
            foreach (var section in _highlighter.HighlightLine(sourceNumber).Sections)
            {
                var start = line.Offset + _displayPrefix + section.Offset - sourceLine.Offset;
                ChangeLinePart(start, start + section.Length, element =>
                {
                    if (section.Color.Foreground is { } foreground)
                        element.TextRunProperties.SetForegroundBrush(foreground.GetBrush(CurrentContext));
                });
            }
        }

        public void Dispose() => _highlighter.Dispose();
    }

    private sealed class DiffBackgroundRenderer(IReadOnlyList<DiffRowKind> kinds, bool rightSide) : IBackgroundRenderer
    {
        private static readonly IBrush InsertBackground = new SolidColorBrush(Color.FromArgb(72, 35, 170, 95));
        private static readonly IBrush DeleteBackground = new SolidColorBrush(Color.FromArgb(72, 220, 65, 65));

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            foreach (var visual in textView.VisualLines)
            {
                var display = visual.FirstDocumentLine.LineNumber - 1;
                if (display < 0 || display >= kinds.Count) continue;
                var kind = kinds[display];
                var brush = kind switch
                {
                    DiffRowKind.Insert when rightSide => InsertBackground,
                    DiffRowKind.Delete when !rightSide => DeleteBackground,
                    _ => null,
                };
                if (brush is null) continue;
                var y = visual.VisualTop - textView.ScrollOffset.Y;
                drawingContext.FillRectangle(brush,
                    new Rect(0, y, textView.Bounds.Width, visual.Height));
            }
        }
    }

    private sealed class DiffNumberMargin(TextEditor editor, IReadOnlyList<int?> sourceLines,
        IReadOnlyList<DiffRowKind> kinds, bool rightSide, bool inline = false) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(68, 0);

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (TextView is not { VisualLinesValid: true } view) return;
            foreach (var visual in view.VisualLines)
            {
                var display = visual.FirstDocumentLine.LineNumber - 1;
                if (display < 0 || display >= sourceLines.Count) continue;
                var number = sourceLines[display]?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                var kind = kinds[display];
                var marker = inline
                    ? kind == DiffRowKind.Insert ? "+" : kind == DiffRowKind.Delete ? "−" : " "
                    : rightSide ? kind == DiffRowKind.Insert ? "+" : " "
                    : kind == DiffRowKind.Delete ? "−" : " ";
                var markerBrush = kind == DiffRowKind.Insert ? Brushes.LimeGreen
                    : kind == DiffRowKind.Delete ? Brushes.OrangeRed : Brushes.Gray;
                var typeface = new Typeface(editor.FontFamily, weight: FontWeight.Bold);
                var y = visual.VisualTop - view.ScrollOffset.Y;
                var mark = new FormattedText(marker, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, editor.FontSize, markerBrush);
                var line = new FormattedText(number, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, editor.FontSize, Brushes.Gray);
                context.DrawText(mark, new Point(2, y));
                context.DrawText(line, new Point(20 + Math.Max(0, 42 - line.Width), y));
            }
        }
    }
}
