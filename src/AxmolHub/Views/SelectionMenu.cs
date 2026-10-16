using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AxmolHub;

/// <summary>
/// The right-click menus Hub's text surfaces carry: on a selection inside a message they offer 复制 and 引用, and
/// on the composer the three clipboard verbs a text box is expected to have.
///
/// <para>Why this file exists at all: nothing in the shell had a context menu. Avalonia's <see cref="TextBox"/>
/// ships none, and the rendered Markdown is a third-party control with no menu of its own, so a right-click
/// anywhere over a message or over the composer did nothing — the only ways at the text were the per-message copy
/// button and Ctrl+C, and Ctrl+C copies whatever the library can extract (see
/// <see cref="MarkdownMessageRenderer.SelectionText"/> for what its extractor leaves out).</para>
///
/// <para>Opening is driven by <see cref="InputElement.ContextRequestedEvent"/> rather than by the
/// <c>ContextFlyout</c> property, because a menu on a message is earned by a selection: with nothing selected the
/// event is left unhandled and no menu appears. That is also why the menu is kept in the table below — a control
/// asked for a menu it owns through <c>ContextFlyout</c> can answer, and one that opens on request cannot, so the
/// answer a check needs lives here next to the attachment.</para>
/// </summary>
internal static class SelectionMenu
{
    /// <summary>Target → the menu attached to it. Weak on the control on purpose: a transcript rebuilds its
    /// viewers as it scrolls, and a strong table would hold every document ever painted.</summary>
    private static readonly ConditionalWeakTable<Control, MenuFlyout> Attached = new();

    /// <summary>A menu for a surface whose selection the caller can read: 复制 always, 引用 when it has somewhere
    /// to put a quote.</summary>
    internal static void Attach(Control target, Func<string> readSelection, Action<string>? quote)
    {
        var menu = new MenuFlyout();

        var copy = new MenuItem { Header = HubStrings.Get("CopyMessage") };
        copy.Click += async (_, _) => await CopyAsync(target, readSelection());
        menu.Items.Add(copy);

        if (quote is not null)
        {
            var quoted = new MenuItem { Header = HubStrings.Get("QuoteSelection") };
            quoted.Click += (_, _) =>
            {
                var text = readSelection();
                if (text.Length > 0) quote(text);
            };
            menu.Items.Add(quoted);
        }

        Attached.AddOrUpdate(target, menu);
        target.AddHandler(InputElement.ContextRequestedEvent, (_, e) =>
        {
            // Nothing selected, nothing asked: a menu whose only rows could not act on anything is worse than no
            // menu, and leaving the event alone keeps the library's own handling (if it ever grows one) intact.
            if (readSelection().Length == 0) return;
            e.Handled = true;
            menu.ShowAt(target, showAtPointer: true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The composer's menu. Unlike a message, a text box always has something a right-click can offer,
    /// so this one opens unconditionally and disables the rows the box cannot act on.</summary>
    internal static void AttachTextBox(TextBox box)
    {
        var menu = new MenuFlyout();

        var cut = new MenuItem { Header = HubStrings.Get("CutText") };
        cut.Click += (_, _) => box.Cut();
        menu.Items.Add(cut);

        var copy = new MenuItem { Header = HubStrings.Get("CopyMessage") };
        copy.Click += (_, _) => box.Copy();
        menu.Items.Add(copy);

        var paste = new MenuItem { Header = HubStrings.Get("PasteText") };
        paste.Click += (_, _) => box.Paste();
        menu.Items.Add(paste);

        Attached.AddOrUpdate(box, menu);
        box.AddHandler(InputElement.ContextRequestedEvent, (_, e) =>
        {
            // Enabled per box state rather than hidden: rows that come and go under the pointer are worse than
            // rows that are greyed, and an empty clipboard still leaves 粘贴 as the answer to "why is this dead".
            cut.IsEnabled = box.CanCut;
            copy.IsEnabled = box.CanCopy;
            paste.IsEnabled = box.CanPaste;
            e.Handled = true;
            menu.ShowAt(box, showAtPointer: true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The rows a surface's menu offers, by their localized headers — empty when it has no menu. Read
    /// from the checks, which is the only reason the table above is kept.</summary>
    internal static IReadOnlyList<string> HeadersForCheck(Control target)
        => Attached.TryGetValue(target, out var menu)
            ? menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray()
            : [];

    /// <summary>The row itself, for a check that presses it the way a hand does.</summary>
    internal static MenuItem? ItemForCheck(Control target, string header)
        => Attached.TryGetValue(target, out var menu)
            ? menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header?.ToString() == header)
            : null;

    private static async Task CopyAsync(Control source, string text)
    {
        if (text.Length == 0) return;
        if (TopLevel.GetTopLevel(source)?.Clipboard is not { } clipboard) return;

        // The same shape the code block's copy button uses: a DataTransfer with one text item, so the clipboard
        // carries plain text and nothing else.
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);
    }
}
