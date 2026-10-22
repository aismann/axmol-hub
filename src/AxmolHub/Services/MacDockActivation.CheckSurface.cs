using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AxmolHub;
/// <summary>
/// The half of <see cref = "MacDockActivation"/> that exists only for <c>--verify-shell</c>: raising the two AppKit
/// signals a Dock click would send, because a check cannot click the icon. Kept apart from the Objective-C
/// interop it drives, so that file reads as the interop and nothing else.
/// </summary>
internal static partial class MacDockActivation
{
    /// <summary>Posts the notification AppKit posts. A Dock click cannot be produced from a check, so the shell
    /// self-check raises this one instead: what it proves is the half that can be wrong — that the observer really
    /// is on the centre under the real name and that the registered callback really runs.</summary>
    internal static void PostActivationForCheck()
    {
        var center = Send0(objc_getClass("NSNotificationCenter"), Selector("defaultCenter"));
        SendPost(center, Selector("postNotificationName:object:"), ToNSString(BecameActiveNotification), IntPtr.Zero);
    }

    /// <summary>Sends the reopen event to the application delegate, which is what a Dock click does. The self-check
    /// cannot click the icon, so it sends the message the click would send: what that proves is everything that can
    /// silently be wrong — that the delegate really wears the subclass, that the override really runs, that the
    /// original implementation is still consulted, and that the answer says "handled".</summary>
    internal static byte PressReopenForCheck()
    {
        if (_reopenTarget == IntPtr.Zero)
            return 0;
        var app = Send0(objc_getClass("NSApplication"), Selector("sharedApplication"));
        return SendReopen(_reopenTarget, Selector("applicationShouldHandleReopen:hasVisibleWindows:"), app, 0);
    }
}
