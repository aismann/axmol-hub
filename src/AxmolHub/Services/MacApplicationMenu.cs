using System;
using Avalonia;
using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// The macOS application menu's Hub-owned "show main window" entry — the way back to a window the close button
/// hid.
///
/// <para>Why this entry exists at all: on macOS the close button hides the main window instead of ending the run
/// (see <see cref="MainWindow.ShouldHideOnCloseForCheck"/>), and the way back the platform would normally supply —
/// a click on the Dock icon, delivered as <c>ActivationKind.Reopen</c> — never arrives: Avalonia 12.1.3's
/// <c>ClassicDesktopStyleApplicationLifetime</c> does not implement <c>IActivatableLifetime</c>, which the shell
/// self-check asserts by reading the interface off the running lifetime. So Hub carries the entry itself rather
/// than leaving a hidden window with no route back to it.</para>
///
/// <para>The label is the tray menu's own string — both mean the same thing — read through
/// <see cref="HubStrings"/> and refreshed when the menu opens, the way <c>WindowsTrayIntegration</c> does it: a
/// <see cref="NativeMenuItem"/> is not in the visual or logical tree, so a <c>{DynamicResource}</c> there has no
/// lookup chain and would render blank without an error.</para>
/// </summary>
internal static class MacApplicationMenu
{
    /// <summary>Puts the entry first on the application menu, ahead of Hub's About row and of the standard
    /// Services / Hide / Show All / Quit block the framework appends after it. The restore arrives as a delegate so
    /// the shell self-check can install the same entry without a <see cref="MainWindow"/> to hand.</summary>
    internal static void Install(Application application, Action activate)
    {
        if (NativeMenu.GetMenu(application) is not { } menu) return;
        var item = CreateShowWindowItem(activate);
        menu.Items.Insert(0, item);
        menu.Opening += (_, _) => item.Header = HubStrings.Get("TrayShowWindow");
    }

    /// <summary>The entry on its own, taking the restore as a delegate: <c>--verify-shell</c> has no
    /// <see cref="MainWindow"/> to hand (its own window is the lifetime's main window) and still has to read the
    /// label and the wiring, which is what the shell self-check does with this.</summary>
    internal static NativeMenuItem CreateShowWindowItem(Action activate)
    {
        var item = new NativeMenuItem(HubStrings.Get("TrayShowWindow"));
        item.Click += (_, _) => activate();
        return item;
    }
}
