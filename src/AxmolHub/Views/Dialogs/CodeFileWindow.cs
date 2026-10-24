using Avalonia.Controls;

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
    private CodeFileWindow(string relativePath, Control editor)
    {
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

        var window = new CodeFileWindow(relativePath, CodeSurface.ForFile(full, text));
        if (owner is null) window.Show(); else window.Show(owner);
    }
}
