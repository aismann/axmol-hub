using System.Linq;
using Avalonia.Controls;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AxmolHub;
/// <summary>
/// The half of <see cref = "SelectionMenu"/> that exists only for <c>--verify-shell</c>: reading a surface's rows,
/// finding one, opening and closing its menu. In its own file so the product half — which builds the menu — can be
/// read without the readers in the middle of it.
/// </summary>
internal static partial class SelectionMenu
{
    /// <summary>The rows a surface's menu offers, by their localized headers — empty when it has no menu. Read
    /// from the checks, which is the only reason the table above is kept; the composer's menu is found through
    /// <see cref = "Control.ContextFlyout"/> instead, because that is where it lives, and reading it from there is
    /// part of what a check asserts about it.</summary>
    internal static IReadOnlyList<string> HeadersForCheck(Control target) => MenuOf(target)is { } menu ? menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray() : [];
    /// <summary>The row itself, for a check that presses it the way a hand does.</summary>
    internal static MenuItem? ItemForCheck(Control target, string header) => MenuOf(target)?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header?.ToString() == header);
    /// <summary>Whether the surface's menu is up, for a check that has to see the popup open rather than only
    /// read the rows it carries.</summary>
    internal static bool IsOpenForCheck(Control target) => MenuOf(target)is { IsOpen: true };
    /// <summary>Closes a menu a check opened, so the surface is left the way it was found.</summary>
    internal static void HideForCheck(Control target) => MenuOf(target)?.Hide();
    /// <summary>The rows as one line, for the message of a check that failed: "复制|引用", or empty when the
    /// surface carries no menu. A boolean that cannot say which half of it was false costs a second run to
    /// diagnose.</summary>
    internal static string DescribeForCheck(Control target) => string.Join("|", HeadersForCheck(target));
    /// <summary>The menu a surface was given, wherever it was given: the table this file keeps first, because a
    /// menu this file attached is the one a check is asking about, then <see cref = "Control.ContextFlyout"/> — which
    /// is where the composer's lives, and where a control may also carry a menu of somebody else's (the rendered
    /// Markdown ships controls with context properties of their own, and asking the control first made this answer
    /// about those instead).</summary>
    private static MenuFlyout? MenuOf(Control target) => Attached.TryGetValue(target, out var attached) ? attached : target.ContextFlyout as MenuFlyout;
}
