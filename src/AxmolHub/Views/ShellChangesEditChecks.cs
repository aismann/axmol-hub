using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Real disk baselines and the shipped Changes controls, with no second diff implementation.</summary>
internal sealed class ShellChangesEditChecks(Action<bool, string> check)
{
    internal Task VerifyAsync(string scratchRoot)
    {
        var root = Path.Combine(scratchRoot, "changes-edit");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "probe.cpp");
        const string before = "/* start\nold */\nint value = 1;\n";
        const string after = "/* start\nnew */\nint value = 2;\n";
        File.WriteAllText(path, after);
        var scope = new WorkspaceToolScope(root, new WorkspaceGuards(null, []), null, "changes-edit", [], null, null);
        ChangesEditBuffer? Open() => ChangesEditBuffer.Open("probe.cpp", before, () => scope);
        var buffer = Open();
        if (buffer is null) { check(false, "差异编辑夹具：文本缓冲区可读取"); return Task.CompletedTask; }
        var session = new ChangesEditSession();
        session.Begin();
        session.Buffers.Add("probe.cpp", buffer);
        buffer.Text = after.Replace("2", "3", StringComparison.Ordinal);
        session.Cancel();
        check(File.ReadAllText(path) == after && !session.IsDirty && !session.Editing,
            "差异编辑：Cancel 只丢弃手动缓冲，保留进入编辑前的 AI 改动");
        buffer = Open()!;
        buffer.Text = after.Replace("2", "3", StringComparison.Ordinal);
        File.WriteAllText(path, "external\n");
        var conflict = buffer.Save();
        check(conflict.Verdict == TextReplaceVerdict.ChangedSince && File.ReadAllText(path) == "external\n" && buffer.IsDirty,
            $"差异编辑：磁盘冲突不覆盖外部内容，缓冲仍在（实际 {conflict.Verdict}）");
        File.WriteAllText(path, after);
        buffer = Open()!;
        buffer.Text = after.Replace("2", "4", StringComparison.Ordinal);
        scope = WorkspaceToolScope.Empty;
        var refused = buffer.Save();
        check(refused.Verdict != TextReplaceVerdict.Written && File.ReadAllText(path) == after && buffer.IsDirty,
            $"差异编辑：保存时重新校验权限（实际 {refused.Verdict}）");
        scope = new WorkspaceToolScope(root, new WorkspaceGuards(null, []), null, "changes-edit", [], null, null);
        session.Begin();
        session.Buffers.Add("probe.cpp", buffer);
        var saved = session.Save();
        check(saved is null && File.ReadAllText(path) == buffer.Text && !session.Editing,
            "差异编辑：Save 写入工作区，不创建 Git 提交");

        var secondPath = Path.Combine(root, "second.cpp");
        File.WriteAllText(secondPath, "int second = 1;\n");
        var firstBatch = Open()!;
        var secondBatch = ChangesEditBuffer.Open("second.cpp", "", () => scope)!;
        firstBatch.Text += "// first batch\n";
        secondBatch.Text += "// second batch\n";
        session.Begin();
        session.Buffers.Add("probe.cpp", firstBatch);
        session.Buffers.Add("second.cpp", secondBatch);
        File.WriteAllText(secondPath, "// external second\n");
        var partial = session.Save();
        check(partial is not null && !firstBatch.IsDirty && secondBatch.IsDirty && session.Editing
            && File.ReadAllText(secondPath) == "// external second\n" && ChangesEditSession.DirtyCount > 0,
            "差异编辑：批量保存部分失败时保留失败缓冲，不重复保存已成功的文件");
        session.Cancel();
        File.WriteAllText(path, after);

        var pane = new InspectorPanel();
        pane.OpenEditBuffer = (_, relative, original) => ChangesEditBuffer.Open(relative, original, () => scope);
        var change = new ChangedFile("probe.cpp", ChangeVerdict.Edited, "call", 0, null, null, null, "Edited", true);
        var repository = new GitRepositoryState(GitReadOutcome.Ok, root, "main", 0, 0,
            [new GitStatusEntry("probe.cpp", "M", "M", false)],
            new Dictionary<string, string>(StringComparer.Ordinal), false, null);
        pane.ReadComparisonRequested = (_, _, _) => Task.FromResult<ChatFileSnapshot?>(new(before, after));
        void Reload(string owner)
        {
            pane.Reload(null, [change], (_, _) =>
                (FileDiff.Unified(before, after, "probe.cpp"), UndoCopyState.Read), repository, "changes", null,
                _ => (new ChatFileSnapshot(before, after), UndoCopyState.Read), owner);
            Dispatcher.UIThread.RunJobs();
            if (!pane.RepoRowExpandedForCheck(0)) pane.ClickRepoRowForCheck(0);
            Dispatcher.UIThread.RunJobs();
        }
        Reload("one");
        var window = new Window { Width = 900, Height = 700, Content = pane };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Button? Button(string tag) => pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Tag, tag));
            void Click(string tag) => Button(tag)?.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            window.UpdateLayout();
            Click("InspectorEditDiff");
            window.UpdateLayout();
            var editors = pane.GetVisualDescendants().OfType<TextEditor>().Where(e => Equals(e.Tag, "InspectorDiffEditor")).ToArray();
            check(editors.Length > 0 && editors.All(editor => !editor.IsReadOnly)
                && Button("InspectorCancelEdit")?.IsVisible == true,
                $"差异编辑：页眉铅笔进入全局编辑并显示 Cancel/Save（实际 {editors.Length} 个源片段）");
            var last = editors.LastOrDefault();
            if (last is null) return Task.CompletedTask;
            last.Text += "// manual\n";
            Reload("one");
            window.UpdateLayout();
            check(pane.GetVisualDescendants().OfType<TextEditor>().Any(e => e.Text.Contains("// manual", StringComparison.Ordinal))
                && Button("InspectorSaveEdit")?.IsEnabled == true,
                "差异编辑：同会话刷新保留未保存的手动缓冲");
            pane.PressViewSettingsForCheck();
            pane.ClickViewMenuRowForCheck("InspectorMenuSplit");
            window.UpdateLayout();
            check(pane.GetVisualDescendants().OfType<TextEditor>().Any(e => !e.IsReadOnly && e.Text.Contains("// manual", StringComparison.Ordinal))
                && pane.GetVisualDescendants().OfType<TextEditor>().Any(e => e.IsReadOnly),
                "差异编辑：切换双栏保留缓冲，仅新版可编辑");
            pane.PressViewSettingsForCheck();
            pane.ClickViewMenuRowForCheck("InspectorMenuUnified");
            window.UpdateLayout();
            File.WriteAllText(path, "// external during edit\n");
            Click("InspectorSaveEdit");
            window.UpdateLayout();
            check(Button("InspectorCancelEdit")?.IsVisible == true && Button("InspectorSaveEdit")?.IsEnabled == true
                && pane.GetVisualDescendants().OfType<TextEditor>().Any(e => e.Text.Contains("// manual", StringComparison.Ordinal))
                && File.ReadAllText(path) == "// external during edit\n",
                "差异编辑：Save 冲突后保持编辑状态与可重试缓冲");
            File.WriteAllText(path, after);
            Reload("two");
            window.UpdateLayout();
            check(Button("InspectorEditDiff")?.IsVisible == true,
                "差异编辑：切换会话不把旧缓冲显示到新会话");
            Reload("one");
            window.UpdateLayout();
            check(pane.GetVisualDescendants().OfType<TextEditor>().Any(e => e.Text.Contains("// manual", StringComparison.Ordinal)),
                "差异编辑：返回原会话恢复未保存缓冲");
            Click("InspectorCancelEdit");
            check(!pane.GetVisualDescendants().OfType<TextEditor>().Any(e => Equals(e.Tag, "InspectorDiffEditor"))
                && File.ReadAllText(path) == after,
                "差异编辑：Cancel 退出编辑且不改磁盘");
        }
        finally { window.Close(); ChangesEditSession.DiscardAll(); }
        var diffAlongside = Open()!;
        var fullEditor = new CodeEditorView(path, TextFile.Read(path), (baseline, text) =>
        {
            using var lease = TextFile.AcquireWrite(path);
            return lease.Save(() => WorkspacePaths.ResolveWrite(scope.Sandbox, "probe.cpp", scope.Guards), baseline, text);
        }, writable: true, discardAndClose: () => { }, owner: null);
        var fullWindow = new Window { Width = 800, Height = 600, Content = fullEditor };
        try
        {
            fullWindow.Show();
            fullWindow.UpdateLayout();
            var source = fullEditor.GetVisualDescendants().OfType<TextEditor>().Single();
            source.Document.Insert(source.Document.TextLength, "// full file\n");
            var save = fullEditor.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save"));
            save.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            check(!fullEditor.IsDirty && File.ReadAllText(path).Contains("// full file", StringComparison.Ordinal),
                "完整文件编辑：Open file 保留独立的整文件输入与保存");
            diffAlongside.Text += "// diff edit\n";
            var overlap = diffAlongside.Save();
            check(overlap.Verdict == TextReplaceVerdict.ChangedSince && diffAlongside.IsDirty
                && File.ReadAllText(path).Contains("// full file", StringComparison.Ordinal),
                "两种编辑共存：整文件保存后差异缓冲检测冲突，不互相覆盖");
        }
        finally { fullWindow.Close(); }
        using var syntax = new ChangesDiffSyntax("probe.cpp", FileDiff.Unified(before, after, "probe.cpp"), new(before, after));
        check(syntax.Line(true, 2).Count > 0 && syntax.Line(false, 2).Count > 0,
            "差异高亮：两侧从完整原文计算跨行注释状态");
        return Task.CompletedTask;
    }
}
