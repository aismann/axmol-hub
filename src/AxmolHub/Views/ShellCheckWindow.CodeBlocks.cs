using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Markdown.Avalonia;

namespace AxmolHub;

public sealed partial class ShellCheckWindow
{
    private Task VerifyCodeBlockGeometryAsync()
    {
        const string single = "```text\nCompleted without a final answer for a very long model response, with finish_reason=tool_calls and tool_calls=1 after recording all the request metadata\n```";
        const string multiple = "```sh\ngrep -h \"Completed without a final answer\" ~/Library/Application\\ Support/AxmolHub/data/logs/*.log | tail -20\ngrep -h \"Model usage\" ~/Library/Application\\ Support/AxmolHub/data/logs/*.log | tail -20\n```";
        var column = new StackPanel { Width = 600 };
        column.Children.Add(MarkdownMessageRenderer.Render(single));
        column.Children.Add(MarkdownMessageRenderer.Render(multiple));
        var inspector = new InspectorPanel();
        inspector.Reload(single + "\n\n" + multiple, [], _ => default, null, "plan");
        var surface = new Grid { ColumnDefinitions = new ColumnDefinitions("600,360") };
        surface.Children.Add(column);
        Grid.SetColumn(inspector, 1);
        surface.Children.Add(inspector);
        var window = new Window
        {
            Width = 960, Height = 600, Content = surface,
            WindowDecorations = WindowDecorations.None,
        };
        var previous = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows.FirstOrDefault(candidate => candidate.IsActive);
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var chatEditors = column.GetVisualDescendants().OfType<TextEditor>().ToArray();
            var planEditors = inspector.GetVisualDescendants().OfType<TextEditor>().ToArray();
            Check(chatEditors.Length == 2 && planEditors.Length == 2,
                "会话历史与计划栏均渲染单行日志及多行命令（会话=" + chatEditors.Length
                + "，计划=" + planEditors.Length + "）");
            foreach (var (name, editors) in new[] { ("会话历史", chatEditors), ("计划栏", planEditors) })
            {
                for (var i = 0; i < editors.Length; i++)
                    CheckCodeBlockGeometry(editors[i], name + (i == 0 ? "单行长日志" : "多行长命令"));
            }
            SmokeCapture.Capture(window, System.IO.Path.Combine(ScratchDirectory.Resolve("code-block-geometry"), "after.png"));
        }
        finally
        {
            window.Close();
            previous?.Activate();
            Dispatcher.UIThread.RunJobs();
        }
        return Task.CompletedTask;
    }

    private async Task VerifyStreamingCodeBlockAsync(MainWindow shell, ChatPanel panel)
    {
        const string command = "grep -h \"Completed without a final answer\" ~/Library/Application\\ Support/AxmolHub/data/logs/*.log | tail -20";
        var before = shell.Chat.ActiveConversation?.Id;
        var session = shell.Chat.StartConversation();
        shell.Chat.OpenConversation(session.Id);
        shell.OpenAssistant();
        panel.Reload();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.Chat.ClientOverride = (_, _) => new ScriptedChatClient(["```sh\n" + command + "\n```"],
            null, gate.Task, arrived);
        try
        {
            panel.SetInputForCheck("打印日志命令");
            _ = panel.SendComposerForCheck();
            await WaitForSignalAsync(arrived.Task);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            shell.UpdateLayout();
            var live = panel.GetVisualDescendants().OfType<TextEditor>()
                .FirstOrDefault(editor => editor.Text.Contains(command, StringComparison.Ordinal));
            Check(live is not null, "流式回复在尚未落盘时已经把长命令渲染为代码块");
            if (live is not null) CheckCodeBlockGeometry(live, "流式回复长命令");
            gate.TrySetResult(true);
            await panel.WaitForRunToFinishForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var stored = panel.GetVisualDescendants().OfType<TextEditor>()
                .FirstOrDefault(editor => editor.Text.Contains(command, StringComparison.Ordinal));
            Check(stored is not null, "完成后长命令回到历史消息的代码块");
            if (stored is not null) CheckCodeBlockGeometry(stored, "流式落盘后长命令");
        }
        finally
        {
            gate.TrySetResult(true);
            await WaitForRunAsync(shell.Chat, session.Id);
            shell.Chat.ClientOverride = null;
            shell.Chat.DeleteConversation(session.Id);
            if (before is { } previous) shell.Chat.OpenConversation(previous);
            shell.OpenAssistant();
            panel.Reload();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private void CheckCodeBlockGeometry(TextEditor editor, string name)
    {
        var border = editor.GetVisualAncestors().OfType<Border>()
            .First(candidate => candidate.Classes.Contains("CodeBlock"));
        var grid = (Grid)border.Child!;
        var view = editor.GetVisualDescendants().OfType<AvaloniaEdit.Rendering.TextView>().Single();
        var bar = editor.GetVisualDescendants().OfType<ScrollBar>()
            .Single(candidate => candidate.Orientation == Avalonia.Layout.Orientation.Horizontal);
        var button = grid.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Classes.Contains("markdown-code-copy"));
        var lines = editor.Text.Split('\n').Length;
        var geometry = $"border={border.Bounds} grid={grid.Bounds} editor={editor.Bounds} "
            + $"view={view.Bounds} bar={bar.Bounds} copy={button.Bounds}";
        Check(lines >= 1 && view.Bounds.Height >= lines * view.DefaultLineHeight - 0.5
              && bar.IsVisible && bar.Maximum > bar.Minimum
              && bar.Bounds.Top >= view.Bounds.Bottom - 0.5
              && editor.Bounds.Height >= view.Bounds.Height + bar.Bounds.Height - 0.5
              && grid.RowDefinitions[1].ActualHeight >= editor.Bounds.Height
              && button.Bounds.Bottom <= editor.Bounds.Top + 0.5,
            name + "：文字完整、横向滚动条不盖最后一行、复制按钮占独立行（" + geometry + "）");
    }
}
