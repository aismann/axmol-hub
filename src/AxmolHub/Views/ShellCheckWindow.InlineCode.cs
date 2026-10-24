using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;
using Markdown.Avalonia;
using Pointer = Avalonia.Input.Pointer;
using ColorTextBlock.Avalonia;

namespace AxmolHub;
internal sealed class ShellInlineCodeChecks
{
    private readonly Action<bool, string> Check;
    internal ShellInlineCodeChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal Task VerifyInlineCodeSurfacesAsync()
    {
        const string markdown = "Before `Configured` after.\n\n[Link](https://axmol.dev/)";
        var chatViewer = MarkdownMessageRenderer.Render(markdown);
        var inspector = new InspectorPanel();
        inspector.Reload(markdown, [], (_, _) => default, null, "plan");
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        content.Children.Add(chatViewer);
        Grid.SetColumn(inspector, 1);
        content.Children.Add(inspector);
        var window = new Window
        {
            Width = 1000, Height = 400, Content = content,
            WindowDecorations = WindowDecorations.None,
            Background = Application.Current is { } app
                && app.TryGetResource("Hub.Background", app.ActualThemeVariant, out var brush)
                    ? brush as Avalonia.Media.IBrush : null,
        };
        var previousWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows.FirstOrDefault(candidate => candidate.IsActive);
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            foreach (var (name, viewer) in new[]
                     {
                         ("会话", chatViewer),
                         ("右侧计划栏", inspector.GetVisualDescendants().OfType<MarkdownScrollViewer>().Single()),
                     })
            {
                var block = viewer.GetVisualDescendants().OfType<CTextBlock>()
                    .Single(candidate => candidate.Text.Contains("Configured", StringComparison.Ordinal));
                var code = block.GetLogicalDescendants().OfType<ReadingCode>().Single();
                var geometry = code.Geometries.Single();
                var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                Point At(double character)
                {
                    geometry = code.Geometries.Single();
                    // Stay just inside the glyph, not on an exact floating-point caret boundary.
                    var x = character > 10 ? geometry.Width + 5 : geometry.Width * (character + 0.1) / 10;
                    return (block.TranslatePoint(default, window) ?? default)
                        + new Vector(geometry.Left + x, geometry.Top + geometry.Height / 2);
                }
                void Drag(double from, double to)
                {
                    block.RaiseEvent(new PointerPressedEventArgs(block, pointer, window, At(from), 0,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                        KeyModifiers.None, 1));
                    block.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, block, pointer, window, At(to), 0,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                    block.RaiseEvent(new PointerReleasedEventArgs(block, pointer, window, At(to), 0,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                        KeyModifiers.None, MouseButton.Left));
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
                string Selected() => MarkdownMessageRenderer.SelectionText(viewer);
                // The key as a keyboard raises it, on the document itself. Which handler answers, and whether the
                // event is left unhandled for the next surface, are both part of what is being claimed.
                bool CopyKey()
                {
                    var args = new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent,
                        Key = Key.C,
                        KeyModifiers = KeyModifiers.Control,
                        Source = viewer,
                    };
                    viewer.RaiseEvent(args);
                    Dispatcher.UIThread.RunJobs();
                    return args.Handled;
                }
                Drag(2, 2);
                Check(Selected() == "", name + "：单击行内代码不选中整块");
                var directory = ScratchDirectory.Resolve("inline-code-selection");
                var before = System.IO.Path.Combine(directory, name == "会话" ? "chat-before.png" : "plan-before.png");
                var after = System.IO.Path.Combine(directory, name == "会话" ? "chat-selected.png" : "plan-selected.png");
                SmokeCapture.Capture(viewer, before);
                Drag(2, 7);
                Check(Selected() == "nfigu", name + "：内部拖选只复制真实字符范围（实际「" + Selected() + "」）");
                SmokeCapture.Capture(viewer, after);
                var original = SmokeCapture.ReadBgra(before, out _, out _, out var stride);
                var selected = SmokeCapture.ReadBgra(after, out _, out _, out _);
                var codeOrigin = block.TranslatePoint(new Point(geometry.Left, geometry.Top), viewer) ?? default;
                int Pixel(double character) => (int)(codeOrigin.Y + 2) * stride
                    + (int)(codeOrigin.X + geometry.Width * character / 10) * 4;
                bool SamePixel(int index) => original.AsSpan(index, 3).SequenceEqual(selected.AsSpan(index, 3));
                Check(SamePixel(Pixel(0.5)) && !SamePixel(Pixel(4.5)) && SamePixel(Pixel(9.5)),
                    name + "：渲染高亮只覆盖选中字符，两端未选中文字仍保留静态底色");
                var surfacePixel = Pixel(0.5);
                var surface = viewer.GetVisualDescendants().OfType<InlineCodeBackdrop>().Single()
                    .GetValue(InlineCodeBackdrop.SurfaceProperty);
                // Allow two levels for rounded-edge/glyph antialiasing in the rendered sample.
                Check(MarkdownMessageRenderer.MatchesToken(surface, "Hub.SurfaceRaised")
                      && surface is Avalonia.Media.ISolidColorBrush solid
                      && Math.Abs(original[surfacePixel + 2] - solid.Color.R) <= 2
                      && Math.Abs(original[surfacePixel + 1] - solid.Color.G) <= 2
                      && Math.Abs(original[surfacePixel] - solid.Color.B) <= 2,
                    name + "：行内代码的静态底色仍然来自主题令牌");
                Drag(7, 2);
                Check(Selected() == "nfigu", name + "：反向内部拖选也保留准确范围");
                Drag(-0.5, 4);
                Check(Selected() == " Conf", name + "：从正文拖入代码时不扩成整块（实际「" + Selected() + "」）");
                Drag(4, 10.5);
                Check(Selected() == "igured ", name + "：从代码拖出正文时保留准确范围（实际「" + Selected() + "」）");
                Drag(-0.5, 10.5);
                Check(Selected() == " Configured ", name + "：跨代码复制的文本包含完整代码");

                // A shortcut travels the route that starts at the focused element, so the document has to hold the
                // keyboard for Ctrl+C to mean anything here. The gesture that left a selection is what takes it —
                // the half the person's own message always had, since a SelectableTextBlock asks on a click.
                Check(viewer.IsFocused, name + "：选中之后文档自己拿到键盘焦点，快捷键才有落处");

                // 复制 goes to a recorded string instead of the clipboard, the way the sidebar's copy is checked.
                var copied = "";
                var previousWriter = SelectionMenu.TestClipboardWriter;
                SelectionMenu.TestClipboardWriter = text =>
                {
                    copied = text;
                    return Task.CompletedTask;
                };
                bool answered;
                try
                {
                    answered = CopyKey();
                }
                finally
                {
                    SelectionMenu.TestClipboardWriter = previousWriter;
                }
                Check(answered && copied == " Configured ",
                    name + "：Ctrl+C 交出去的就是这段选中的文字，行内代码也在其中（实际「" + copied + "」）");

                // One click, two claims: the keyboard stays where the reader already had it, and a document with
                // nothing selected does not eat the key it was given — the composer beside it still has something
                // to answer with.
                var holder = inspector.GetVisualDescendants().OfType<Button>().First();
                holder.Focus();
                Dispatcher.UIThread.RunJobs();
                Drag(2, 2);
                Check(Selected() == "" && block.Cursor?.ToString() == "Ibeam", name + "：再次单击清除选区，代码光标为文本选择形态");
                Check(holder.IsFocused, name + "：只清除选区的单击不会把键盘焦点抢走（现在 旁人=" + holder.IsFocused
                    + " 文档=" + viewer.IsFocused + "）");
                var swallowed = CopyKey();
                Check(!swallowed, name + "：没有选区时文档不吞掉 Ctrl+C（实际 handled=" + swallowed + "）");

                var link = viewer.GetVisualDescendants().OfType<CTextBlock>().Single(candidate => candidate.Text == "Link");
                void Hover(CTextBlock target, Point point) => target.RaiseEvent(new PointerEventArgs(
                    InputElement.PointerMovedEvent, target, pointer, window,
                    target.TranslatePoint(point, window) ?? default, 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
                Hover(link, new Point(3, link.Bounds.Height / 2));
                Check(link.Cursor?.ToString() == "Hand", name + "：真实链接仍显示手形光标");
                Hover(link, new Point(link.Bounds.Width - 2, link.Bounds.Height / 2));
                Check(link.Cursor?.ToString() == "Ibeam", name + "：离开链接后恢复文本选择光标");
                pointer.Capture(null);
            }
        }
        finally
        {
            window.Close();
            previousWindow?.Activate();
            Dispatcher.UIThread.RunJobs();
        }
        return Task.CompletedTask;
    }
}
