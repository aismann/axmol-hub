using System.Runtime.InteropServices;

namespace AxmolHub;

/// <summary>
/// The one thing Avalonia's storage provider cannot do on macOS: show a **file** panel and hand back an
/// application bundle.
///
/// <para><c>OpenFilePickerAsync</c> lets the user click an <c>.app</c> and then returns nothing — the macOS backend
/// drops directories from the result by design (AvaloniaUI/Avalonia#18303). The folder panel is the only panel it
/// will return a bundle from, and it renders the bundle as a folder with a <c>Contents</c> child, which reads as
/// "browse a directory" rather than "choose an application"; narrowing it is not possible either, since
/// <c>FolderPickerOpenOptions</c> carries no type filter.</para>
///
/// <para>So this drives <c>NSOpenPanel</c> directly through the Objective-C runtime: same panel macOS shows in
/// its own dialogs, told to treat file packages as files and to accept only application bundles. It is
/// deliberately self-contained — no Avalonia internals, no IL patching — so an Avalonia upgrade cannot break it.
/// Anything that goes wrong returns <see cref="Outcome.Unavailable"/>, and the caller falls back to the folder
/// panel that already worked.</para>
/// </summary>
internal static class MacApplicationPicker
{
    internal enum Outcome
    {
        /// <summary>No panel was shown (not macOS, or the Objective-C calls failed); the caller should fall back.</summary>
        Unavailable,

        /// <summary>The panel was shown and dismissed without a choice.</summary>
        Cancelled,

        /// <summary>An application was chosen.</summary>
        Picked,
    }

    private const string LibObjc = "/usr/lib/libobjc.A.dylib";

    [DllImport(LibObjc)] private static extern nint objc_getClass(string name);

    [DllImport(LibObjc)] private static extern nint sel_registerName(string name);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    // BOOL is one byte on every Apple platform (signed char on x86_64, bool on arm64), so a byte carries it.
    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern nint SendBool(nint receiver, nint selector, byte argument);

    /// <summary>Shows the panel and waits for it. Must run on the UI thread — the same thread AppKit's run loop is on.</summary>
    internal static (Outcome Outcome, string? Path) Pick(string title)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return (Outcome.Unavailable, null);
        }

        try
        {
            // Everything allocated below (strings, the extension array, the URL) is autoreleased into this pool,
            // so one call leaks nothing. Objects AppKit owns are not touched.
            var pool = Send(objc_getClass("NSAutoreleasePool"), Sel("new"));
            try
            {
                var panel = Send(objc_getClass("NSOpenPanel"), Sel("openPanel"));
                if (panel == 0)
                {
                    return (Outcome.Unavailable, null);
                }

                SetString(panel, "setTitle:", title);
                SendBool(panel, Sel("setCanChooseFiles:"), 1);
                SendBool(panel, Sel("setCanChooseDirectories:"), 0);
                SendBool(panel, Sel("setAllowsMultipleSelection:"), 0);
                // The whole point: with packages treated as directories the panel shows the bundle's contents and
                // selecting it yields the folder; treated as files, the bundle is the selectable item.
                SendBool(panel, Sel("setTreatsFilePackagesAsDirectories:"), 0);
                RestrictToApplications(panel);
                SetObject(panel, "setDirectoryURL:",
                    Send(objc_getClass("NSURL"), Sel("fileURLWithPath:"), AutoreleasedString("/Applications")));

                // 1 is NSModalResponseOK; anything else is cancel.
                if (Send(panel, Sel("runModal")) != 1)
                {
                    return (Outcome.Cancelled, null);
                }

                var url = Send(panel, Sel("URL"));
                var path = url == 0 ? 0 : Send(url, Sel("path"));
                var utf8 = path == 0 ? 0 : Send(path, Sel("UTF8String"));
                var chosen = utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
                return chosen is { Length: > 0 } ? (Outcome.Picked, chosen) : (Outcome.Cancelled, null);
            }
            finally
            {
                Send(pool, Sel("drain"));
            }
        }
        catch (Exception)
        {
            // A missing libobjc, a changed AppKit selector, a platform that is not really macOS: all of it means
            // "this route is not available", never "the settings page breaks".
            return (Outcome.Unavailable, null);
        }
    }

    /// <summary>
    /// Narrows the panel to application bundles: <c>allowedContentTypes</c> with the bundle UTI where
    /// <c>UTType</c> exists (macOS 11+), otherwise the deprecated extension list, which filters the same set.
    /// </summary>
    private static void RestrictToApplications(nint panel)
    {
        var uniformTypes = objc_getClass("UTType");
        if (uniformTypes != 0)
        {
            var appBundle = Send(uniformTypes, Sel("typeWithIdentifier:"), AutoreleasedString("com.apple.application-bundle"));
            if (appBundle != 0)
            {
                SetObject(panel, "setAllowedContentTypes:",
                    Send(objc_getClass("NSArray"), Sel("arrayWithObject:"), appBundle));
                return;
            }
        }

        SetObject(panel, "setAllowedFileTypes:", Send(objc_getClass("NSArray"), Sel("arrayWithObject:"), AutoreleasedString("app")));
    }

    private static nint Sel(string name) => sel_registerName(name);

    private static void SetObject(nint target, string selector, nint value) => Send(target, Sel(selector), value);

    private static void SetString(nint target, string selector, string value)
        => SetObject(target, selector, AutoreleasedString(value));

    /// <summary>An <c>NSString</c> that the enclosing pool releases.</summary>
    private static nint AutoreleasedString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            var text = Send(Send(objc_getClass("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), utf8);
            return Send(text, Sel("autorelease"));
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }
}
