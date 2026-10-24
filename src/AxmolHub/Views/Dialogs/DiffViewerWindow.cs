using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Non-modal host for an aligned file comparison.</summary>
internal sealed class DiffViewerWindow : Window
{
    private DiffViewerWindow(string title, Control viewer)
    {
        Title = title;
        Width = 1100;
        Height = 720;
        MinWidth = 680;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new HubDialogChrome(this, "CloseWindow", Close).Wrap(viewer);
    }

    internal static void Show(Window? owner, string path, string leftLabel, string rightLabel, string before, string after,
        Action<string>? quote = null,
        Func<int, DiffHunkAction, Task<DiffHunkActionResult>>? hunkAction = null,
        DiffHunkAction? availableHunkAction = null)
    {
        var viewer = new DiffViewer(path, leftLabel, rightLabel, before, after,
            quote, hunkAction, availableHunkAction);
        if (owner is MainWindow shell)
        {
            shell.ShowCodeWorkspace(path, viewer);
            return;
        }
        var window = new DiffViewerWindow(path, viewer);
        if (owner is null) window.Show(); else window.Show(owner);
    }

    internal static void Show(Window? owner, string title, Control viewer)
    {
        if (owner is MainWindow shell)
        {
            shell.ShowCodeWorkspace(title, viewer);
            return;
        }
        var window = new DiffViewerWindow(title, viewer);
        if (owner is null) window.Show(); else window.Show(owner);
    }
}
