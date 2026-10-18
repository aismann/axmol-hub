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
/// <para>Two routes on purpose, because the two surfaces want opposite things from a right-click. A menu on a
/// message is <i>earned by a selection</i>: it is opened from a context-requested handler and appears only when
/// there is something to act on. The composer's is assigned to <see cref="Control.ContextFlyout"/>, which the
/// framework opens itself — and that route is not cosmetic: it is what keeps the box's selection alive while the
/// menu is up (see <see cref="AttachTextBox"/>). Because the table below cannot answer for a menu the framework
/// owns, the lookups read <see cref="Control.ContextFlyout"/> first and fall back to it.</para>
///
/// <para>A menu opened by hand costs the selection unless something pays for it, and that is why
/// <see cref="Attach"/> looks the way it does. The popup takes the keyboard focus, and a
/// <see cref="SelectableTextBlock"/> — which is what the person's own message is drawn with — answers a lost
/// focus by clearing its selection, keeping it only while its own <c>ContextFlyout</c>/<c>ContextMenu</c> is the
/// open one. So the menu is handed to the control for as long as it is up, and its rows act on what was selected
/// when it opened: the first keeps the band on screen, the second makes the copy land even if a host clears the
/// selection anyway. A rendered document needs neither half — the library's selection lives in its blocks rather
/// than in the focus.</para>
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

        // What the surface held when the menu opened. A row pressed after the popup took the focus has to act on
        // what the person saw when they asked for the menu, even if something cleared the selection in between —
        // which is what a selectable block does (see the class header). Live first, this as the fallback, so a
        // surface that keeps its selection reads as it always did.
        var opened = "";
        string Selection() => readSelection() is { Length: > 0 } live ? live : opened;

        var copy = new MenuItem { Header = HubStrings.Get("CopyMessage") };
        copy.Click += async (_, _) => await CopyAsync(target, Selection());
        menu.Items.Add(copy);

        if (quote is not null)
        {
            var quoted = new MenuItem { Header = HubStrings.Get("QuoteSelection") };
            quoted.Click += (_, _) =>
            {
                var text = Selection();
                if (text.Length > 0) quote(text);
            };
            menu.Items.Add(quoted);
        }

        // The band survives the menu, and the framework is what pays for it. A popup takes the keyboard focus, and
        // a selectable block answers a lost focus by clearing its selection — unless its own
        // ContextFlyout/ContextMenu is the open one (SelectableTextBlock.OnLostFocus carries the very guard a
        // TextBox does). Opening the menu by hand leaves that property null, which is why the band went the
        // instant the menu appeared; a rendered document never had the problem, because its selection lives in the
        // library's blocks and not in the focus. So the flyout is handed to the control for as long as it is up,
        // and taken back afterwards — long enough to hold the band, short enough that the next right-click is
        // still earned by a selection rather than opened by the framework for everyone.
        var held = target as SelectableTextBlock;
        var previousFlyout = held?.ContextFlyout;
        menu.Closed += (_, _) =>
        {
            if (held is not null && ReferenceEquals(held.ContextFlyout, menu)) held.ContextFlyout = previousFlyout;
        };

        Attached.AddOrUpdate(target, menu);
        target.AddHandler(InputElement.ContextRequestedEvent, (_, e) =>
        {
            // Nothing selected, nothing asked: a menu whose only rows could not act on anything is worse than no
            // menu, and leaving the event alone keeps the library's own handling (if it ever grows one) intact.
            var text = readSelection();
            if (text.Length == 0) return;
            e.Handled = true;
            opened = text;
            if (held is not null)
            {
                previousFlyout = held.ContextFlyout;
                held.ContextFlyout = menu;
            }
            menu.ShowAt(target, showAtPointer: true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The composer's menu: the three clipboard verbs, each enabled by what the box can actually do
    /// with it right now.
    ///
    /// <para>Assigned to <see cref="Control.ContextFlyout"/> rather than opened from a context-requested handler,
    /// and that is not a style choice — it is the one thing that keeps the selection alive. A popup takes keyboard
    /// focus, and Avalonia's <c>TextBox.OnLostFocus</c> clears the selection unless its own
    /// <c>ContextFlyout</c>/<c>ContextMenu</c> is the open one (measured: the guard is
    /// <c>ContextFlyout is { IsOpen: true } || ContextMenu is { IsOpen: true }</c>, and
    /// <c>ClearSelectionOnLostFocus</c> defaults to true). A menu opened by hand from an event handler leaves both
    /// properties null, so the box wiped the selection the moment the menu appeared and 复制/剪切 acted on
    /// nothing. The framework's own path also positions the flyout from the request position, so the menu still
    /// appears at the pointer. And unlike a message's menu this one always opens: a text box with nothing selected
    /// still has 粘贴 to offer, and the two verbs that cannot act are greyed instead of absent.</para>
    /// </summary>
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

        // Enabled as the menu opens, per box state: rows that come and go under the pointer are worse than rows
        // that are greyed, and an empty selection leaves 复制 as the answer to "why is this dead".
        menu.Opened += (_, _) =>
        {
            cut.IsEnabled = box.CanCut;
            copy.IsEnabled = box.CanCopy;
            paste.IsEnabled = box.CanPaste;
        };
        box.ContextFlyout = menu;
        Attached.AddOrUpdate(box, menu);
    }

    /// <summary>The rows a surface's menu offers, by their localized headers — empty when it has no menu. Read
    /// from the checks, which is the only reason the table above is kept; the composer's menu is found through
    /// <see cref="Control.ContextFlyout"/> instead, because that is where it lives, and reading it from there is
    /// part of what a check asserts about it.</summary>
    internal static IReadOnlyList<string> HeadersForCheck(Control target)
        => MenuOf(target) is { } menu
            ? menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray()
            : [];

    /// <summary>The row itself, for a check that presses it the way a hand does.</summary>
    internal static MenuItem? ItemForCheck(Control target, string header)
        => MenuOf(target)?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Header?.ToString() == header);

    /// <summary>Whether the surface's menu is up, for a check that has to see the popup open rather than only
    /// read the rows it carries.</summary>
    internal static bool IsOpenForCheck(Control target) => MenuOf(target) is { IsOpen: true };

    /// <summary>Closes a menu a check opened, so the surface is left the way it was found.</summary>
    internal static void HideForCheck(Control target) => MenuOf(target)?.Hide();

    /// <summary>The rows as one line, for the message of a check that failed: "复制|引用", or empty when the
    /// surface carries no menu. A boolean that cannot say which half of it was false costs a second run to
    /// diagnose.</summary>
    internal static string DescribeForCheck(Control target) => string.Join("|", HeadersForCheck(target));

    /// <summary>The menu a surface was given, wherever it was given: the table this file keeps first, because a
    /// menu this file attached is the one a check is asking about, then <see cref="Control.ContextFlyout"/> — which
    /// is where the composer's lives, and where a control may also carry a menu of somebody else's (the rendered
    /// Markdown ships controls with context properties of their own, and asking the control first made this answer
    /// about those instead).</summary>
    private static MenuFlyout? MenuOf(Control target)
        => Attached.TryGetValue(target, out var attached)
            ? attached
            : target.ContextFlyout as MenuFlyout;

    /// <summary>Where a copy lands when a check wants it, instead of the real clipboard — the same hook
    /// <c>ChatSidebar.TestClipboardWriter</c> gives the conversation-id copy, and for the same reason: the suite
    /// drives the row's own click handler, and reading the text back must not throw away what the person last
    /// copied. Left null in the shell, where a copy goes to the clipboard.</summary>
    internal static Func<string, Task>? TestClipboardWriter { get; set; }

    private static async Task CopyAsync(Control source, string text)
    {
        if (text.Length == 0) return;
        if (TestClipboardWriter is { } writer)
        {
            await writer(text);
            return;
        }
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
