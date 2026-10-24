using Avalonia;
using Avalonia.Controls;
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
    private readonly List<SourceColorizer> _colorizers = [];
    private bool _syncing;

    internal DiffViewer(string path, string leftLabel, string rightLabel, string? before, string? after)
    {
        RowDefinitions = new RowDefinitions("Auto,*");
        ColumnDefinitions = new ColumnDefinitions("*,*");
        var script = FileDiff.Rows(before, after);
        var leftLines = new List<string>();
        var rightLines = new List<string>();
        var leftNumbers = new List<int?>();
        var rightNumbers = new List<int?>();
        var leftKinds = new List<DiffRowKind>();
        var rightKinds = new List<DiffRowKind>();
        foreach (var row in script.Rows)
        {
            leftLines.Add(row.OriginalLine is null ? "" : row.Text);
            rightLines.Add(row.UpdatedLine is null ? "" : row.Text);
            leftNumbers.Add(row.OriginalLine);
            rightNumbers.Add(row.UpdatedLine);
            leftKinds.Add(row.Kind);
            rightKinds.Add(row.Kind);
        }
        _left = CodeSurface.ForFile(path, string.Join('\n', leftLines));
        _right = CodeSurface.ForFile(path, string.Join('\n', rightLines));
        _left.ShowLineNumbers = false;
        _right.ShowLineNumbers = false;
        _left.TextArea.LeftMargins.Add(new DiffNumberMargin(_left, leftNumbers, leftKinds, rightSide: false));
        _right.TextArea.LeftMargins.Add(new DiffNumberMargin(_right, rightNumbers, rightKinds, rightSide: true));
        var definition = DarkSyntax.ForBackdrop(CodeSyntax.ForFile(path));
        if (definition is not null)
        {
            var leftColorizer = new SourceColorizer(new TextDocument(before ?? ""), leftNumbers, definition);
            var rightColorizer = new SourceColorizer(new TextDocument(after ?? ""), rightNumbers, definition);
            _colorizers.Add(leftColorizer);
            _colorizers.Add(rightColorizer);
            _left.TextArea.TextView.LineTransformers.Add(leftColorizer);
            _right.TextArea.TextView.LineTransformers.Add(rightColorizer);
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
        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        header.Children.Add(Header(leftLabel, script.Exact));
        Grid.SetColumn(Header(rightLabel, script.Exact), 1);
        header.Children.Add(Header(rightLabel, script.Exact));
        Children.Add(header);
        Grid.SetRow(_left, 1);
        Children.Add(_left);
        Grid.SetRow(_right, 1);
        Grid.SetColumn(_right, 1);
        Children.Add(_right);
        MinHeight = 240;
    }

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

        internal SourceColorizer(TextDocument source, IReadOnlyList<int?> sourceLines, IHighlightingDefinition definition)
        {
            _source = source;
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
                var start = line.Offset + section.Offset - sourceLine.Offset;
                ChangeLinePart(start, start + section.Length, element =>
                {
                    if (section.Color.Foreground is { } foreground)
                        element.TextRunProperties.SetForegroundBrush(foreground.GetBrush(CurrentContext));
                });
            }
        }

        public void Dispose() => _highlighter.Dispose();
    }

    private sealed class DiffNumberMargin(TextEditor editor, IReadOnlyList<int?> sourceLines,
        IReadOnlyList<DiffRowKind> kinds, bool rightSide) : AbstractMargin
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
                var marker = rightSide
                    ? kind == DiffRowKind.Insert ? "+" : " "
                    : kind == DiffRowKind.Delete ? "−" : " ";
                var markerBrush = kind == DiffRowKind.Insert ? Brushes.SeaGreen
                    : kind == DiffRowKind.Delete ? Brushes.IndianRed : Brushes.Gray;
                var typeface = new Typeface(editor.FontFamily);
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
