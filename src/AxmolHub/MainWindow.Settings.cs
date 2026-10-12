using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AxmolHub;

/// <summary>
/// The settings overlay: the layer the settings surface lives on.
///
/// It used to be a page in <c>PageHost</c>. That stopped working the way a page works: eight stacked cards in
/// one scroll, where the model-provider card alone runs past a screen and pushed every group below it off the
/// bottom, so "change the download channel" meant scrolling past infrastructure nobody is looking at. As a
/// layer it gets a category rail and one group on screen at a time.
///
/// What it is **not** is a modal that owns the window. The host sits on <c>ClientRoot</c>'s second row with a
/// bottom margin the same height as the status strip, which leaves two things outside the scrim on purpose:
/// the title bar, so the window is still draggable while settings is open, and the strip's download slot,
/// which is the only surface that can act on a pending app update. A full-window layer would have frozen both
/// — and the queue ruling that made the slot the update's only action surface is exactly what that would have
/// broken.
/// </summary>
public partial class MainWindow
{
    /// <summary>The one settings page this window hosts. Kept out of <see cref="_pages"/> on purpose: that
    /// dictionary is the page cache keyed by navigation destination, and this is no destination — it has no
    /// nav item, no title-bar name, and nothing in <c>NavigateTo</c> that could find it.</summary>
    private SettingsPage? _settings;

    /// <summary>Which category the card is on. Held by the shell rather than the page because the two have
    /// different lifetimes: the page is dropped on a data-root switch (its workspace went with the old one)
    /// while the shell's answer to "where was I" survives. Not persisted — which group you were reading is a
    /// momentary decision, and a restored 模型提供商 on a machine that just changed roots would be an answer
    /// about a directory nobody is looking at.</summary>
    private string _settingsCategory = SettingsPage.DefaultCategory;

    /// <summary>The card's size envelope. It follows the window — host minus the inset, clamped — because
    /// neither alignment expresses that on its own: a centered child measures against infinity and collapses to
    /// its minimum (measured: 720x420 in a 1280x880 window), and a stretched one ignores <c>MaxWidth</c> by
    /// pinning to the cell's left edge. The floor is what a minimum-size window gets; the ceiling is what keeps
    /// a hint line from becoming a 2,500-pixel measure on a 5K display.</summary>
    private const double CardInsetX = 28;
    private const double CardInsetY = 14;
    private const double CardMinWidth = 720;
    private const double CardMinHeight = 420;
    private const double CardMaxWidth = 1180;
    private const double CardMaxHeight = 820;

    private void SyncSettingsCardSize()
    {
        var host = SettingsOverlayHost.Bounds;
        if (host.Width <= 0 || host.Height <= 0)
        {
            // Before the first layout pass there is nothing to follow, and a card sized off an empty rect would
            // be a card that never grows back.
            return;
        }

        SettingsCard.Width = Math.Clamp(host.Width - CardInsetX * 2, CardMinWidth, CardMaxWidth);
        SettingsCard.Height = Math.Clamp(host.Height - CardInsetY * 2, CardMinHeight, CardMaxHeight);
    }

    /// <summary>Wires the overlay's own input. Called after <c>InitializePictureViewer</c> because the three
    /// Escape handlers on this window are ordered by the guards in each of them, not by registration luck:
    /// <see cref="OnViewerKeyDown"/> and <see cref="OnInspectorExpandKeyDown"/> both yield while this layer is
    /// up, so the surface on top is the one Escape closes.</summary>
    private void InitializeSettings()
    {
        // Clicking the scrim closes. Nothing here waits to be saved — every picker commits as it changes —
        // so there is no half-finished state a click outside could destroy.
        SettingsScrim.PointerPressed += (_, e) =>
        {
            CloseSettings();
            e.Handled = true;
        };
        SettingsCloseButton.Click += (_, _) => CloseSettings();
        // The card follows the window, so a resize while it is up has to be followed rather than sampled once.
        SettingsOverlayHost.SizeChanged += (_, _) => SyncSettingsCardSize();
        AddHandler(KeyDownEvent, OnSettingsKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Builds the page on first use. The shell's arguments are the same eight the page has always
    /// taken: it asks the shell to open folders, switch the data root and apply a theme rather than reaching
    /// around it, and <see cref="UseTheme"/> stays the only writer of the theme.</summary>
    private SettingsPage EnsureSettings()
        => _settings ??= new SettingsPage(
            _workspace, _preferencesStore, _preferences, _chat,
            OpenFolder, ApplyLanguage, SwitchDataRoot, UseTheme);

    /// <summary>Opens the card. <paramref name="category"/> lands it somewhere specific — what a caller that
    /// already knows which group answers its question should say so instead of dropping the reader on the
    /// first row; <c>null</c> reopens on the row they left.</summary>
    internal void OpenSettings(string? category = null)
    {
        var page = EnsureSettings();
        if (SettingsPage.IsCategory(category))
        {
            _settingsCategory = category!;
        }

        page.SelectCategory(_settingsCategory);
        // The content goes on when the layer opens and comes off when it closes (see CloseSettings): the page
        // hooks UpdateService while it is attached and unhooks when it is not, so a copy left on a hidden
        // host is an invisible card that keeps repainting itself.
        SettingsHost.Content = page;
        SettingsOverlayHost.IsVisible = true;
        // Sized before the first arrange so the card never shows up at last time's rect for a frame; the
        // SizeChanged hook covers every resize that happens while it is already up.
        SyncSettingsCardSize();
        // PageHost used to be the thing the busy gate disabled, and this surface is not inside it any more —
        // the gate has to be stated here or an operation in flight becomes a settings page you can act on.
        SettingsHost.IsEnabled = !_workspace.IsBusy;
    }

    internal void CloseSettings()
    {
        if (!SettingsOverlayHost.IsVisible) return;
        SettingsOverlayHost.IsVisible = false;
        SettingsHost.Content = null;
    }

    /// <summary>The gear's whole action: lift the card, or drop the one it lifted. Toggling is what keeps the
    /// button honest for the keyboard and for anything that drives it directly — a button that only ever opens
    /// cannot undo itself. It is **not** how a person gets rid of the card: the scrim covers the rail while the
    /// card is up, and the three ways out that are reachable are Escape, the ×, and a press anywhere outside
    /// the card.</summary>
    private void ToggleSettings()
    {
        if (SettingsOverlayHost.IsVisible) CloseSettings();
        else OpenSettings();
    }

    private void OnSettingsKeyDown(object? sender, KeyEventArgs e)
    {
        if (!SettingsOverlayHost.IsVisible) return;
        if (e.Key != Key.Escape) return;

        // A ComboBox dropdown is its own top-level, so its Escape never reaches this window's route: the page
        // reports the open dropdown and this handler stays out of the way. Closing the card with a dropdown
        // open would take the card away from under a choice the reader was in the middle of making.
        if (_settings?.HasOpenDropDown == true) return;

        CloseSettings();
        e.Handled = true;
    }

    // ───────────────────────── Seams for the shell self-check ─────────────────────────

    internal bool SettingsOverlayVisibleForCheck => SettingsOverlayHost.IsVisible;

    /// <summary>The page behind the card, or null before anything opened it.</summary>
    internal SettingsPage? SettingsPageForCheck => _settings;

    internal Rect SettingsOverlayBoundsForCheck => InClient(SettingsOverlayHost);
    internal Rect SettingsCardBoundsForCheck => InClient(SettingsCard);
    internal Rect SettingsScrimBoundsForCheck => InClient(SettingsScrim);
    internal Rect StatusStripBoundsForCheck => InClient(StatusStrip);

    /// <summary>The card as a visual, so a render check can photograph **it** rather than the window: a frame
    /// of the window reads as non-blank off the shell behind the scrim, which would let a card that painted
    /// nothing pass.</summary>
    internal Control SettingsCardForCheck => SettingsCard;


    /// <summary>Where the layer sits in the stacking order. Nothing sets ZIndex, so document order is the
    /// whole contract, and a layer in the wrong slot still covers the shell just as well — which is exactly
    /// why a bounds check cannot catch it and this can (same argument as
    /// <see cref="InspectorOverlayOrderForCheck"/>).</summary>
    internal bool SettingsOverlayOrderForCheck
    {
        get
        {
            var shell = ClientRoot.Children.IndexOf(ShellRoot);
            var inspector = ClientRoot.Children.IndexOf(InspectorExpandHost);
            var viewer = ClientRoot.Children.IndexOf(PictureViewerHost);
            var settings = ClientRoot.Children.IndexOf(SettingsOverlayHost);
            return shell >= 0 && shell < inspector && inspector < viewer && viewer < settings;
        }
    }

    /// <summary>Whether the scrim actually blocks: a Border with no brush hit-tests nothing, so a scrim that
    /// lost its background would leave the shell behind it clickable while every geometry check still passed.
    /// The check that reads this is paired with the one that measures the scrim's rect — together they say
    /// "the shell is covered", which neither half can say alone.</summary>
    internal bool SettingsScrimBlocksForCheck => SettingsScrim.Background is not null;

    /// <summary>The category the card is on, for the cross-page and deep-link assertions.</summary>
    internal string SettingsCategoryForCheck => _settingsCategory;

    /// <summary>The scrim itself, so a check can raise a real press on it the way it does on the title bar's
    /// drag layer — exercising the wiring rather than calling <see cref="CloseSettings"/>.</summary>
    internal Border SettingsScrimForCheck => SettingsScrim;

    /// <summary>Closes the card through the header's own button rather than the method behind it.</summary>
    internal void ClickSettingsCloseForCheck() => SettingsCloseButton.RaiseEvent(
        new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Sends a key down the same tunnel route <see cref="OnSettingsKeyDown"/> listens on.</summary>
    internal void PressSettingsKeyForCheck(Key key)
        => OnSettingsKeyDown(this, new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = this,
        });

    /// <summary>Opens the card on a named category and hands back the page, so a check does not have to reach
    /// for the field separately and can fail on a null page rather than on a null reference inside one.</summary>
    internal SettingsPage? OpenSettingsForCheck(string? category)
    {
        OpenSettings(category);
        return _settings;
    }

    /// <summary>Runs the shell's answer to "a session needs a decision" — the same handler the
    /// <c>AttentionRequired</c> event calls — so a check can prove the card steps aside for a question without
    /// parking a real approval behind a model call.</summary>
    internal void RaiseAttentionForCheck(ChatAttentionKind kind)
        => OnChatAttentionRequired(_chat.ViewedConversationId ?? "", kind);

    /// <summary>The category the page is showing, read off the page rather than the shell's copy.</summary>
    internal string SettingsSelectedCategoryForCheck => _settings?.SelectedCategory ?? "";
}
