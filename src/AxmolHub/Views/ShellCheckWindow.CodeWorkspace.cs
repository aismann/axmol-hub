using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;

namespace AxmolHub;

/// <summary>Phase-zero experiment: projection documents never participate in source highlighting or copying.</summary>
internal sealed class ShellCodeWorkspaceChecks(Action<bool, string> check)
{
    internal async Task VerifyAsync()
    {
        var watch = Stopwatch.StartNew();
        var source = "/* comment\nstill comment */\nint value = 42;\n" +
            string.Join('\n', Enumerable.Range(4, 180).Select(n => $"int row{n} = {n};"));
        var identical = AxmolHub.Core.FileDiff.Rows("same\n", "same\n");
        check(identical.Exact && identical.Rows.Count == 1
            && identical.Rows[0] is { Kind: AxmolHub.Core.DiffRowKind.Equal, OriginalLine: 1, UpdatedLine: 1 },
            "代码工作区：相同文件仍映射为一对真实行");
        var deletion = AxmolHub.Core.FileDiff.Rows("one\ntwo\nthree\n", "one\nthree\n");
        check(deletion.Rows.Any(row => row.Kind == AxmolHub.Core.DiffRowKind.Delete
            && row.OriginalLine == 2 && row.UpdatedLine is null)
            && deletion.Rows.Any(row => row.Kind == AxmolHub.Core.DiffRowKind.Equal
            && row.OriginalLine == 3 && row.UpdatedLine == 2),
            "代码工作区：删除行与后续行保留独立行号");
        var sourceCount = new TextDocument(source).LineCount;
        var leftMap = Enumerable.Range(1, sourceCount).ToList();
        leftMap.InsertRange(1, new int[3]);
        var rightMap = Enumerable.Range(1, sourceCount).ToList();
        rightMap.InsertRange(4, new int[3]);
        using var left = new DiffProjectionProbe(source, leftMap.ToArray());
        using var right = new DiffProjectionProbe(source, rightMap.ToArray());
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        grid.Children.Add(left.Editor);
        Grid.SetColumn(right.Editor, 1);
        grid.Children.Add(right.Editor);
        var sync = false;
        void Synchronize(DiffProjectionProbe from, DiffProjectionProbe to)
        {
            if (sync) return;
            sync = true;
            try { to.ScrollVertically(from.Editor.TextArea.TextView.ScrollOffset.Y); }
            finally { sync = false; }
        }
        left.Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Synchronize(left, right);
        right.Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Synchronize(right, left);
        var window = new Window { Width = 900, Height = 420, Content = grid, Title = "Code workspace phase-zero probe" };
        window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AxmolHub/"))
        { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(80);
            window.UpdateLayout();
            left.Editor.TextArea.TextView.EnsureVisualLines();
            right.Editor.TextArea.TextView.EnsureVisualLines();
            var l = left.Editor.TextArea.TextView;
            var r = right.Editor.TextArea.TextView;
            var initialAligned = Math.Abs(l.DefaultLineHeight - r.DefaultLineHeight) < .01
                && left.SourceLineAt(2) == 0 && left.SourceLineAt(5) == 2
                && right.SourceLineAt(5) == 0 && left.Margin.Label(2) == "" && left.Margin.Label(5) == "2";
            check(initialAligned, "代码工作区：占位不伪造源行号，双栏使用同一展示行高度");
            var original = left.Highlighter.HighlightLine(2);
            var projected = left.ColorsForDisplayLine(5);
            check(original.Sections.Count > 0 && projected.SequenceEqual(original.Sections.Select(s => (s.Offset, s.Length)))
                && left.ColorsForDisplayLine(2).Count == 0,
                "代码工作区：高亮来自未插行原文，多行状态不受对齐占位污染");
            left.Editor.Select(0, left.Editor.Document.TextLength);
            check(left.SelectionText() == source && left.Margin.Label(leftMap.Count) == sourceCount.ToString(CultureInfo.InvariantCulture),
                "代码工作区：跨占位复制保留真实文本和无换行尾行");
            if (TopLevel.GetTopLevel(left.Editor)?.Clipboard is { } clipboard)
            {
                await left.CopyAsync();
                var transfer = await clipboard.TryGetDataAsync();
                var copied = transfer is null ? null : await transfer.TryGetTextAsync();
                check(copied == source, "代码工作区：实际剪贴板不含展示占位行");
            }
            left.Editor.Select(0, 0);
            await Task.Delay(80);
            var scroller = left.Editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            var beforeScroll = $"{scroller.Extent}/{scroller.Viewport}/{scroller.Offset}; view:{l.ScrollOffset}";
            left.ScrollVertically(1500);
            var immediateScroll = $"{scroller.Offset}; view:{l.ScrollOffset}";
            await Task.Delay(100);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            check(Math.Abs(left.Editor.VerticalOffset - right.Editor.VerticalOffset) < .5 && left.Editor.VerticalOffset > 100,
                "代码工作区：纵向滚动同步且不递归，横向偏移独立");
            var syncedLeft = left.Editor.VerticalOffset;
            var syncedRight = right.Editor.VerticalOffset;
            left.Editor.ScrollToEnd();
            await Task.Delay(100);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            l.EnsureVisualLines();
            var last = l.VisualLines.LastOrDefault(v => v.LastDocumentLine.LineNumber == leftMap.Count);
            var point = last is null ? default : new Point(2, last.VisualTop - l.ScrollOffset.Y + l.DefaultLineHeight / 2);
            var position = last is null ? null : left.Editor.GetPositionFromPoint(l.TranslatePoint(point, left.Editor) ?? default);
            check(position is { } p && left.SourceLineAt(p.Line) == sourceCount,
                "代码工作区：滚动到底部仍可命中真实尾行");
            await File.WriteAllTextAsync("tmp/code-workspace-probe.txt",
                $"Phase-zero projection probe\nSource lines: {sourceCount}\nDisplay lines: {leftMap.Count}\nElapsed: {watch.ElapsedMilliseconds} ms\n" +
                $"Virtual lines at tail: {l.VisualLines.Count}\nLine height: {l.DefaultLineHeight}\n" +
                $"Bounds: {window.Bounds} / {grid.Bounds} / {left.Editor.Bounds} / {l.Bounds}\n" +
                $"Before: {beforeScroll}; immediate: {immediateScroll}\n" +
                $"Scroll: {syncedLeft} / {syncedRight}; tail: {l.ScrollOffset}; point: {point}; position: {position}\n" +
                "Public APIs only: TextDocument, DocumentHighlighter, DocumentColorizingTransformer, AbstractMargin.\n");

            var quoted = "";
            var viewer = new DiffViewer("probe.cpp", "Before", "After", "alpha\nremoved\nomega\n", "alpha\nadded\nomega\n", text => quoted = text);
            window.Content = viewer;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(80);
            var inlineToggle = viewer.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Tag, "DiffInlineToggle"));
            var inlineSurface = viewer.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault(editor => Equals(editor.Tag, "DiffInlineSurface"));
            var rightSurface = viewer.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault(editor => Equals(editor.Tag, "DiffRightSurface"));
            if (inlineToggle is not null) inlineToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            check(inlineSurface is { IsVisible: true } && rightSurface is { IsVisible: false },
                "代码工作区：精确对齐可切换内联视图");
            if (inlineSurface is not null)
            {
                var line = inlineSurface.Document.GetLineByNumber(3);
                inlineSurface.Select(line.Offset, line.Length);
                var quote = viewer.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Tag, "DiffQuoteSelection"));
                quote?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            check(quoted == "probe.cpp:2\nadded", "代码工作区：引用选择使用真实行号和源文本");
        }
        finally { window.Close(); }
    }
}

/// <summary>Throwaway feasibility fixture; not a product viewer or a second diff algorithm.</summary>
internal sealed class DiffProjectionProbe : IDisposable
{
    private readonly int[] _sourceLines;
    internal TextDocument Source { get; }
    internal DocumentHighlighter Highlighter { get; }
    internal TextEditor Editor { get; }
    internal SourceNumberProbe Margin { get; }

    internal DiffProjectionProbe(string source, int[] sourceLines)
    {
        _sourceLines = sourceLines;
        Source = new TextDocument(source);
        Highlighter = new DocumentHighlighter(Source, HighlightingManager.Instance.GetDefinition("C#"));
        Editor = new TextEditor
        {
            Text = string.Join('\n', sourceLines.Select(n => n == 0 ? "" : Source.GetText(Source.GetLineByNumber(n)))),
            IsReadOnly = true, WordWrap = false, FontFamily = FontFamily.Parse("Menlo,Consolas,monospace"),
            FontSize = 13, Background = Brushes.Transparent, Foreground = Brushes.Gray,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
        };
        Editor.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AxmolHub/"))
        { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.EnableRectangularSelection = false;
        Margin = new SourceNumberProbe(this);
        Editor.TextArea.LeftMargins.Add(Margin);
        Editor.TextArea.TextView.LineTransformers.Add(new ProjectionColorProbe(this));
        Editor.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.C && (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
            { e.Handled = true; await CopyAsync(); }
        }, RoutingStrategies.Tunnel);
    }

    internal int SourceLineAt(int displayLine) => displayLine >= 1 && displayLine <= _sourceLines.Length ? _sourceLines[displayLine - 1] : 0;
    internal List<(int Offset, int Length)> ColorsForDisplayLine(int displayLine)
        => SourceLineAt(displayLine) is > 0 and var n
            ? Highlighter.HighlightLine(n).Sections.Select(s => (s.Offset, s.Length)).ToList() : [];

    internal string SelectionText()
    {
        var result = new StringBuilder();
        foreach (var selection in Editor.TextArea.Selection.Segments)
        {
            foreach (var line in Editor.Document.Lines)
            {
                if (line.Offset >= selection.EndOffset) break;
                if (line.EndOffset + line.DelimiterLength <= selection.StartOffset || SourceLineAt(line.LineNumber) == 0) continue;
                var original = Source.GetLineByNumber(SourceLineAt(line.LineNumber));
                var start = Math.Max(0, selection.StartOffset - line.Offset);
                var end = Math.Min(line.Length, selection.EndOffset - line.Offset);
                if (end > start) result.Append(Source.GetText(original.Offset + start, end - start));
                if (selection.EndOffset > line.EndOffset && original.DelimiterLength != 0)
                    result.Append(Source.GetText(original.EndOffset, original.DelimiterLength));
            }
        }
        return result.ToString();
    }

    internal void ScrollVertically(double y)
    {
        if (Editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            scroll.Offset = new Vector(scroll.Offset.X, y);
    }

    internal async Task CopyAsync()
    {
        if (TopLevel.GetTopLevel(Editor)?.Clipboard is { } clipboard)
        {
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, SelectionText());
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
    }
    public void Dispose() => Highlighter.Dispose();

    private sealed class ProjectionColorProbe(DiffProjectionProbe owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var n = owner.SourceLineAt(line.LineNumber);
            if (n == 0) return;
            var original = owner.Source.GetLineByNumber(n);
            foreach (var section in owner.Highlighter.HighlightLine(n).Sections)
            {
                var start = line.Offset + section.Offset - original.Offset;
                ChangeLinePart(start, start + section.Length, element =>
                {
                    if (section.Color.Foreground is { } foreground)
                        element.TextRunProperties.SetForegroundBrush(foreground.GetBrush(CurrentContext));
                });
            }
        }
    }
}

internal sealed class SourceNumberProbe(DiffProjectionProbe owner) : AbstractMargin
{
    internal string Label(int line) => owner.SourceLineAt(line) is > 0 and var n ? n.ToString(CultureInfo.InvariantCulture) : "";
    protected override Size MeasureOverride(Size availableSize) => new(48, 0);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (TextView is not { VisualLinesValid: true } view) return;
        foreach (var line in view.VisualLines)
        {
            var text = new FormattedText(Label(line.FirstDocumentLine.LineNumber), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(owner.Editor.FontFamily), owner.Editor.FontSize, Brushes.Gray);
            context.DrawText(text, new Point(40 - text.Width, line.VisualTop - view.ScrollOffset.Y));
        }
    }
}
