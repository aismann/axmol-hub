using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AxmolHub;

/// <summary>
/// AppKit's "the application became active" notification, observed so that clicking Hub's Dock icon brings back a
/// main window the close button hid (see <see cref="MainWindow.ShouldHideOnCloseForCheck"/>).
///
/// <para>Why AppKit directly: Avalonia 12.1.3's <c>ClassicDesktopStyleApplicationLifetime</c> does not implement
/// <c>IActivatableLifetime</c>, so the <c>ActivationKind.Reopen</c> that the framework would raise for exactly this
/// gesture is never raised — the shell self-check reads that interface off the running lifetime, which is the same
/// fact this file works around. Becoming active is the transition a Dock click of a background app produces, and it
/// is also produced by Cmd-Tab, which is the other way back to a window that is not on screen.</para>
///
/// <para>Nothing is swizzled: the observer is a class of Hub's own, added to the shared notification centre, so
/// Avalonia's own application delegate keeps whatever it does with the same notification. The callback restores a
/// hidden window and does nothing else — in particular it does not go through
/// <see cref="MainWindow.ActivateFromRequest"/>, whose notification bookkeeping (open the conversation an alert was
/// about) belongs to the window's own <c>Activated</c> handler, which gates it on the window having genuinely left
/// the foreground.</para>
/// </summary>
internal static class MacDockActivation
{
    /// <summary>The AppKit notification name. Written out rather than read from anywhere: the string <em>is</em> the
    /// contract, and a typo in it fails as "nothing ever happens", which no compiler sees.</summary>
    private const string BecameActiveNotification = "NSApplicationDidBecomeActiveNotification";

    private const string ObserverClassName = "AxmolHubActivationObserver";

    /// <summary><c>v@:@</c>: void, self, _cmd, one object — the shape AppKit reads off <c>class_addMethod</c>.</summary>
    private static readonly byte[] ActivatedSignature = Encoding.ASCII.GetBytes("v@:@\0");

    private static readonly ActivatedHandler Activated = OnActivated;
    private static IntPtr _observer;
    private static bool _installed;
    private static Action? _restore;

    /// <summary>Puts the observer on the default notification centre and reports whether it went on. False off
    /// macOS, so a caller can state the platform as a fact rather than branching twice.</summary>
    internal static bool Install(Action restore)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        _restore = restore;
        if (_installed) return true;

        // The class is created once per process; a second Install (the self-check installs and uninstalls its own)
        // only adds another observer object on the same class, because class_addMethod on an existing class would
        // fail for a method that is already registered.
        var type = objc_getClass(ObserverClassName);
        var selector = Selector("hubApplicationActivated:");
        if (type == IntPtr.Zero)
        {
            type = objc_allocateClassPair(objc_getClass("NSObject"), ObserverClassName, 0);
            if (type == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the macOS activation observer.");
            if (class_addMethod(type, selector, Marshal.GetFunctionPointerForDelegate(Activated),
                    ActivatedSignature) == 0)
                throw new InvalidOperationException("Could not register the macOS activation callback.");
            objc_registerClassPair(type);
        }

        _observer = Send0(Send0(type, Selector("alloc")), Selector("init"));
        var center = Send0(objc_getClass("NSNotificationCenter"), Selector("defaultCenter"));
        SendAddObserver(center, Selector("addObserver:selector:name:object:"), _observer, selector,
            ToNSString(BecameActiveNotification), IntPtr.Zero);
        _installed = true;
        return true;
    }

    /// <summary>Takes the observer back off the centre. Only the shutdown path needs it — the process is ending —
    /// but a notification centre outliving its observer is a crash waiting for a slow machine, not a detail worth
    /// leaving to "we exit fast enough".</summary>
    internal static void Uninstall()
    {
        if (_installed && _observer != IntPtr.Zero)
        {
            var center = Send0(objc_getClass("NSNotificationCenter"), Selector("defaultCenter"));
            SendObject(center, Selector("removeObserver:"), _observer);
            Send0(_observer, Selector("release"));
        }

        _observer = IntPtr.Zero;
        _installed = false;
        _restore = null;
    }

    /// <summary>Posts the notification AppKit posts. A Dock click cannot be produced from a check, so the shell
    /// self-check raises this one instead: what it proves is the half that can be wrong — that the observer really
    /// is on the centre under the real name and that the registered callback really runs.</summary>
    internal static void PostActivationForCheck()
    {
        var center = Send0(objc_getClass("NSNotificationCenter"), Selector("defaultCenter"));
        SendPost(center, Selector("postNotificationName:object:"), ToNSString(BecameActiveNotification), IntPtr.Zero);
    }

    /// <summary>The callback. AppKit posts on the main run loop, which is Avalonia's UI thread, so the restore is
    /// called directly: a dispatcher hop would only put the window up after whatever the click was meant to show.
    /// </summary>
    private static void OnActivated(IntPtr self, IntPtr selector, IntPtr notification)
    {
        _ = self;
        _ = selector;
        _ = notification;
        _restore?.Invoke();
    }

    private static IntPtr ToNSString(string value)
    {
        var bytes = Marshal.StringToCoTaskMemUTF8(value);
        try { return SendString(objc_getClass("NSString"), Selector("stringWithUTF8String:"), bytes); }
        finally { Marshal.FreeCoTaskMem(bytes); }
    }

    private static IntPtr Selector(string name) => sel_registerName(name);

    private static IntPtr Send0(IntPtr receiver, IntPtr selector) => objc_msgSend(receiver, selector);

    private static void SendObject(IntPtr receiver, IntPtr selector, IntPtr value)
        => objc_msgSend(receiver, selector, value);

    private static IntPtr SendString(IntPtr receiver, IntPtr selector, IntPtr value)
        => objc_msgSendString(receiver, selector, value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActivatedHandler(IntPtr self, IntPtr selector, IntPtr notification);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(IntPtr type);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addMethod")]
    private static extern byte class_addMethod(IntPtr type, IntPtr selector, IntPtr implementation, byte[] types);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr argument);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSendString(IntPtr receiver, IntPtr selector, IntPtr argument);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendAddObserver(
        IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr method, IntPtr name, IntPtr value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendPost(IntPtr receiver, IntPtr selector, IntPtr name, IntPtr value);
}
