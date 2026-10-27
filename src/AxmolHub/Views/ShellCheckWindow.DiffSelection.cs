using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Guards the diff's actual pointer-selection path. The old inspector checks read labels only, so a
/// derived text control with no selection theme passed while showing neither a text cursor nor a highlight.</summary>
internal sealed class ShellDiffSelectionChecks(Action<bool, string> check)
{
    internal async Task VerifyAsync(MainWindow shell)
    {
        var wasOpen = shell.InspectorVisibleForCheck;
        var savedTab = shell.InspectorTabForCheck;
        var savedWidth = shell.InspectorWidthForCheck;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        try
        {
            shell.Show();
            shell.OpenAssistant();
            shell.OpenInspector("changes");
            shell.SetInspectorWidthForCheck(360);
            if (shell.InspectorPaneForCheck is not { } pane)
            {
                check(false, "夹具：diff 拖选检查没有审阅面板");
                return;
            }

            var sample = "--- a/file\n+++ b/file\n@@ -4,2 +4,2 @@\n--- actual removed\n+++ actual added\n@@ -20 +20 @@\n-old\n+new\n";
            var counts = InspectorPanel.PatchCounts(sample);
            using var syntax = new ChangesDiffSyntax("file.cpp", sample, null);
            var compact = InspectorPanel.BuildUnifiedDiffBlock(sample, syntax, compact: true);
            var split = InspectorPanel.BuildSplitDiffBlock(sample, syntax);
            var compactText = string.Join("|", compact.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(InspectorPanel.TextForCheck));
            var splitText = string.Join("|", split.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(InspectorPanel.TextForCheck));
            check(counts == (2, 2) && compactText.Contains("-- actual removed") && compactText.Contains("++ actual added")
                  && !compactText.Contains("@@") && !compactText.Contains("a/file")
                  && splitText.Contains("-- actual removed") && splitText.Contains("++ actual added")
                  && !splitText.Contains("@@") && !splitText.Contains("a/file"),
                "精简 diff 正确隐藏元数据并保留多段变更及类似头部的代码行");

            var line = string.Concat(Enumerable.Repeat("0123456789 abcdefghij ", 20));
            var diff = "@@ -1 +1 @@\n-old\n+" + line;
            pane.Reload(null,
                [new ChangedFile("selection.cpp", ChangeVerdict.Edited, "selection", 0,
                    diff, null, null, "Edited selection.cpp", true)
                {
                    History =
                    [
                        new ChatFileRevision("selection-1", "session", "/workspace/selection.cpp", "/workspace",
                            DateTimeOffset.UnixEpoch, "before-1", "after-1", ChatChangeStore.Complete),
                        new ChatFileRevision("selection-2", "session", "/workspace/selection.cpp", "/workspace",
                            DateTimeOffset.UnixEpoch.AddSeconds(1), "before-2", "after-2", ChatChangeStore.Complete),
                    ],
                }],
                (_, _) => (diff, UndoCopyState.Read), null, "changes");
            pane.ClickChangeRowForCheck(0);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
            shell.UpdateLayout();
            var text = pane.GetVisualDescendants().OfType<SelectableTextBlock>()
                .FirstOrDefault(block => InspectorPanel.TextForCheck(block) == line);
            var viewer = text?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            if (text is null || viewer is null || viewer.Extent.Width <= viewer.Viewport.Width)
            {
                check(false, "夹具：diff 长行没有形成可横向滚动的文本视口");
                return;
            }

            // The revision picker is secondary to the path: verify rendered content and popup rows, not only
            // ComboBox.FontSize, because the selection presenter can otherwise keep its larger inherited font.
            var selector = pane.GetVisualDescendants().OfType<ComboBox>()
                .Single(control => Equals(control.Tag, "InspectorHistorySelector"));
            var pathFont = pane.GetVisualDescendants().OfType<TextBlock>()
                .First(block => block.Classes.Contains("inspector-path")).FontSize;
            TextBlock SelectedLabel() => selector.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Classes.Contains("inspector-history-text"));
            TextBlock ItemLabel(ComboBoxItem item) => item.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Classes.Contains("inspector-history-text"));
            // Read the closed surface before the popup has ever attached. Inspecting only item.Content missed
            // the stale, black VisualBrush snapshot in the selected surface.
            var firstLabel = SelectedLabel();
            var firstColor = firstLabel.Foreground;
            var firstThemed = MarkdownMessageRenderer.MatchesToken(firstColor, "Hub.TextPrimary");
            selector.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            shell.UpdateLayout();
            await Task.Delay(30);
            var items = selector.Items.Cast<ComboBoxItem>().ToArray();
            var fonts = items.Select(item => ItemLabel(item).FontSize).ToArray();
            check(selector.FontSize <= pathFont && fonts.All(size => size <= pathFont)
                  && selector.Bounds.Height <= 28 && items.All(item => item.Bounds.Height is > 0 and <= 28),
                "修订时间及弹出菜单字号不超过文件路径，使用紧凑行高（路径 " + pathFont
                + "，时间 " + selector.FontSize + "，菜单 " + string.Join("/", fonts)
                + "，高度 " + selector.Bounds.Height + "/" + string.Join("/", items.Select(item => item.Bounds.Height)) + "）");
            // Half a DIP allows layout rounding to put an odd-height label on an even-height frame.
            double CenterError(Control label, Control frame) => label.TranslatePoint(
                new Point(0, label.Bounds.Height / 2), frame) is { } center
                    ? Math.Abs(center.Y - frame.Bounds.Height / 2) : double.PositiveInfinity;
            var closedCenterError = CenterError(SelectedLabel(), selector);
            var menuCenterErrors = items.Select(item => CenterError(ItemLabel(item), item)).ToArray();
            check(closedCenterError <= 0.5 && menuCenterErrors.All(error => error <= 0.5),
                "修订时间在收起框和菜单行内垂直居中（中心偏差 " + closedCenterError.ToString("0.00")
                + "/" + string.Join("/", menuCenterErrors.Select(error => error.ToString("0.00"))) + " DIP）");
            SmokeCapture.Capture(pane, System.IO.Path.Combine(ScratchDirectory.Resolve("diff-selection"), "compact-history.png"));
            selector.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();

            // Explicit content bypassed the presenter's foreground inheritance and painted black in dark mode.
            // Check both the selected label and popup labels after a live theme change, without rebuilding them.
            if (Application.Current is { } app)
            {
                var previousTheme = app.RequestedThemeVariant;
                var themedLabels = firstThemed;
                var themedSelection = true;
                var colors = new List<string> { "initial-closed:" + firstColor };
                var selectionColors = new List<string>();
                try
                {
                    foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                    {
                        app.RequestedThemeVariant = variant;
                        Dispatcher.UIThread.RunJobs();
                        shell.UpdateLayout();
                        var closedLabel = SelectedLabel();
                        themedLabels &= MarkdownMessageRenderer.MatchesToken(closedLabel.Foreground, "Hub.TextPrimary");
                        selectionColors.Add(variant + ":" + text.SelectionBrush);
                        themedSelection &= MarkdownMessageRenderer.MatchesToken(text.SelectionBrush, "Hub.CodeSelection");
                        var artifacts = ScratchDirectory.Resolve("diff-selection");
                        SmokeCapture.Capture(selector, System.IO.Path.Combine(artifacts, "history-closed-" + variant + ".png"));
                        text.SelectionStart = 2;
                        text.SelectionEnd = 7;
                        SmokeCapture.Capture(viewer, System.IO.Path.Combine(artifacts, "diff-selection-" + variant + ".png"));
                        text.ClearSelection();
                        selector.IsDropDownOpen = true;
                        Dispatcher.UIThread.RunJobs();
                        shell.UpdateLayout();
                        await Task.Delay(30);
                        var labels = items.Select(ItemLabel).ToArray();
                        themedLabels &= labels.All(label => MarkdownMessageRenderer.MatchesToken(label.Foreground, "Hub.TextPrimary"));
                        colors.Add(variant + ":closed=" + closedLabel.Foreground + ", menu="
                            + string.Join("/", labels.Select(label => label.Foreground)));
                        foreach (var (item, index) in items.Select((item, index) => (item, index)))
                            SmokeCapture.Capture(item, System.IO.Path.Combine(artifacts, "history-menu-" + variant + "-" + index + ".png"));
                        selector.IsDropDownOpen = false;
                    }
                }
                finally
                {
                    selector.IsDropDownOpen = false;
                    app.RequestedThemeVariant = previousTheme;
                    Dispatcher.UIThread.RunJobs();
                    shell.UpdateLayout();
                }
                check(themedLabels, "修订时间首次收起、切换主题与菜单文字均使用主题文字色（"
                    + string.Join("；", colors) + "）");
                check(themedSelection, "diff 选区使用与聊天相同的 Hub.Selection，并随深浅主题切换（"
                    + string.Join("；", selectionColors) + "）");
            }

            Point At(int index)
            {
                var caret = text.TextLayout.HitTestTextPosition(index);
                return text.TranslatePoint(new Point(caret.X + 0.1, caret.Y + caret.Height / 2), shell) ?? default;
            }
            Point Edge(bool right) => viewer.TranslatePoint(
                new Point(right ? viewer.Viewport.Width - 2 : 2,
                    (text.TranslatePoint(new Point(0, text.Bounds.Height / 2), viewer) ?? default).Y), shell) ?? default;
            InputElement? Hit(Point point) => shell.InputHitTest(point) as InputElement;
            void Press(Point point)
            {
                var target = Hit(point);
                // Routed synthetic events bypass the input manager's native pointer-focus step.
                target?.Focus();
                target?.RaiseEvent(new PointerPressedEventArgs(target, pointer, shell, point, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                    KeyModifiers.None, 1));
            }
            void Move(Point point)
            {
                var target = pointer.Captured as InputElement ?? Hit(point);
                target?.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, target, pointer, shell, point, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            }
            void Release(Point point)
            {
                var target = pointer.Captured as InputElement ?? Hit(point);
                target?.RaiseEvent(new PointerReleasedEventArgs(target, pointer, shell, point, 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                    KeyModifiers.None, MouseButton.Left));
            }

            var hitText = ReferenceEquals(Hit(At(2)), text);
            var directory = ScratchDirectory.Resolve("diff-selection");
            var before = System.IO.Path.Combine(directory, "before.png");
            var after = System.IO.Path.Combine(directory, "selected.png");
            SmokeCapture.Capture(text, before);
            Press(At(2));
            Move(At(7));
            Release(At(7));
            SmokeCapture.Capture(text, after);
            var original = SmokeCapture.ReadBgra(before, out _, out _, out _);
            var selected = SmokeCapture.ReadBgra(after, out _, out _, out _);
            check(hitText && text.SelectedText == "23456"
                  && text.Cursor?.ToString() == "Ibeam"
                  && text.SelectionBrush is ISolidColorBrush { Color.A: > 0 }
                  && !original.AsSpan().SequenceEqual(selected),
                "diff 正文可命中并拖出可见选区，光标为文本选择形态（命中 " + hitText
                + "，选中「" + text.SelectedText + "」，光标 " + text.Cursor
                + "，选区色 " + text.SelectionBrush + "，命中元素 " + Hit(At(2))?.GetType().Name + "）");

            // Hold the pointer still at each edge: scrolling only on PointerMoved would stop immediately.
            var observations = new List<string>();
            var followsSelection = true;
            foreach (var right in new[] { true, false })
            {
                viewer.Offset = new Vector(right ? 0 : 300, viewer.Offset.Y);
                shell.UpdateLayout();
                var start = viewer.TranslatePoint(new Point(viewer.Viewport.Width / 2,
                    (text.TranslatePoint(new Point(0, text.Bounds.Height / 2), viewer) ?? default).Y), shell) ?? default;
                Press(start);
                Move(Edge(right));
                var offset = viewer.Offset.X;
                var endpoint = text.SelectionEnd;
                await Task.Delay(100);
                shell.UpdateLayout();
                var delta = viewer.Offset.X - offset;
                var selectionDelta = text.SelectionEnd - endpoint;
                followsSelection &= right ? delta > 0 && selectionDelta > 0 : delta < 0 && selectionDelta < 0;
                observations.Add((right ? "右" : "左") + $": offset {offset:0.0}→{viewer.Offset.X:0.0}, end {endpoint}→{text.SelectionEnd}");
                Release(Edge(right));
            }
            check(followsSelection, "diff 拖选停在左右边缘时持续滚动并延伸选区（" + string.Join("；", observations) + "）");

            var stoppedOffset = viewer.Offset.X;
            var stoppedSelection = text.SelectedText;
            await Task.Delay(60);
            check(stoppedSelection.Length > 0 && viewer.Offset.X == stoppedOffset
                  && text.SelectedText == stoppedSelection && pointer.Captured is null,
                "diff 松开拖选后不再滚动，选区保留供复制（offset " + stoppedOffset
                + "→" + viewer.Offset.X + "，选区长度 " + stoppedSelection.Length + "→" + text.SelectedText.Length + "）");
        }
        catch (Exception ex)
        {
            check(false, "diff 拖选检查异常：" + ex);
        }
        finally
        {
            pointer.Capture(null);
            shell.SetInspectorWidthForCheck(savedWidth);
            shell.OpenInspector(savedTab);
            if (!wasOpen) shell.CloseInspector();
        }
    }
}
