using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>One file from the project, opened by its path and read-only. The project is the workspace root the
/// conversation runs in, and the path is the one the transcript and the inspector already name — so a row saying
/// <c>Source/AppDelegate.cpp</c> opens what a reader expects and nothing else.
///
/// <para>Deliberately not modal: a file is something to keep beside the conversation, not a question to answer and
/// close. It reads through <see cref="CodeSurface"/> so it is highlighted by the same table the transcript's code
/// blocks use.</para></summary>
public sealed class CodeFileWindow : Window
{
    private static readonly HashSet<CodeFileWindow> OpenWindows = [];
    private readonly CodeEditorView? _editor;
    private bool _discardClose;

    private CodeFileWindow(string relativePath, Control editor)
    {
        _editor = editor as CodeEditorView;
        OpenWindows.Add(this);
        Closed += (_, _) => OpenWindows.Remove(this);
        Title = relativePath;
        Width = 980;
        Height = 680;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new HubDialogChrome(this, "CloseWindow", Close).Wrap(editor);
    }

    /// <summary>Opens <paramref name="relativePath"/> read from <paramref name="workspaceRoot"/>. A path that
    /// climbs out of the project, or a file that cannot be read, opens nothing: this is a reader, and guessing
    /// what a reader meant with <c>../../.ssh/id_rsa</c> is how a viewer becomes a way around the sandbox.</summary>
    internal static void ShowEditor(Window? owner, string conversationId, string relativePath,
        Func<string, WorkspaceToolScope> scopeFor, Action? saved = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(relativePath)) return;
        var initialScope = scopeFor(conversationId);
        var read = WorkspacePaths.ResolveRead(initialScope.Sandbox, relativePath, initialScope.Guards);
        if (!read.IsAllowed) return;
        var baseline = TextFile.Read(read.Full, TextFile.MaxEditorBytes);
        if (baseline.State != TextFileState.Text) return;
        var write = WorkspacePaths.ResolveWrite(initialScope.Sandbox, relativePath, initialScope.Guards);
        var writable = write.IsAllowed && WorkspacePaths.IsWritableText(Path.GetFileName(read.Full));
        CodeFileWindow? window = null;
        var shell = owner as MainWindow;
        var editor = new CodeEditorView(read.Full, baseline, (snapshot, text) =>
        {
            using var lease = TextFile.AcquireWrite(read.Full);
            var result = lease.Save(() =>
            {
                var currentScope = scopeFor(conversationId);
                var currentPath = WorkspacePaths.ResolveWrite(currentScope.Sandbox, relativePath, currentScope.Guards);
                return currentPath.IsAllowed && string.Equals(currentPath.Full, read.Full, StringComparison.Ordinal)
                    ? currentPath
                    : WorkspacePath.Refuse(currentPath.IsAllowed ? WorkspacePathVerdict.ReparsePoint : currentPath.Verdict,
                        relativePath);
            }, snapshot, text);
            if (result.Verdict == TextReplaceVerdict.Written) saved?.Invoke();
            return result;
        }, writable, () =>
        {
            if (shell is not null) shell.CloseCodeWorkspace(discard: true);
            else if (window is not null)
            {
                window._discardClose = true;
                window.Close();
            }
        }, owner);
        if (shell is not null)
        {
            shell.ShowCodeWorkspace(relativePath, editor, () => editor.IsDirty);
            return;
        }
        window = new CodeFileWindow(relativePath, editor);
        window.Closing += (_, e) =>
        {
            if (editor.IsDirty && !window._discardClose) e.Cancel = true;
        };
        if (owner is null) window.Show(); else window.Show(owner);
    }

    internal static async Task<bool> ConfirmCloseDirtyAsync(Window owner)
    {
        var dirty = OpenWindows.Where(window => window._editor?.IsDirty == true).ToArray();
        var shell = owner as MainWindow;
        var shellDirty = shell?.CodeWorkspaceIsDirty == true;
        var count = dirty.Length + (shellDirty ? 1 : 0) + ChangesEditSession.DirtyCount;
        if (count == 0) return true;
        var answer = await HubDialog.ShowAsync(owner, "Unsaved code files",
            $"{count} code file buffer(s) have unsaved changes. Discard them and quit?",
            HubDialogButtons.YesNo, danger: true);
        if (answer != HubDialogResult.Yes) return false;
        ChangesEditSession.DiscardAll();
        foreach (var window in dirty)
        {
            window._discardClose = true;
            window.Close();
        }
        if (shellDirty) shell!.CloseCodeWorkspace(discard: true);
        return true;
    }

    public static void Show(Window? owner, string? workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath)) return;
        var root = Path.GetFullPath(workspaceRoot);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return;
        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var surface = CodeSurface.ForFile(full, text);
        if (owner is MainWindow shell)
        {
            shell.ShowCodeWorkspace(relativePath, surface);
            return;
        }
        var window = new CodeFileWindow(relativePath, surface);
        if (owner is null) window.Show(); else window.Show(owner);
    }
}
