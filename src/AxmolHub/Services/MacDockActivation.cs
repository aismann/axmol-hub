using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AxmolHub;

/// <summary>
/// The two AppKit signals that mean "the user is bringing Hub forward", wired so that a click on Hub's Dock icon
/// brings back a main window the close button hid (see <see cref="MainWindow.ShouldHideOnCloseForCheck"/>).
///
/// <para>Why AppKit directly: Avalonia 12.1.3's <c>ClassicDesktopStyleApplicationLifetime</c> does not implement
/// <c>IActivatableLifetime</c>, so the <c>ActivationKind.Reopen</c> the framework would raise for exactly this
/// gesture is never raised — the shell self-check reads that interface off the running lifetime, which is the same
/// fact this file works around.</para>
///
/// <para>Two signals, because one of them is not enough and which one fires depends on where the app was when the
/// icon was clicked. A Dock click is delivered to the application delegate as
/// <c>applicationShouldHandleReopen:hasVisibleWindows:</c> — that is the one that always arrives, including when Hub
/// is still the frontmost app with nothing on screen, which is exactly the state a close leaves behind. The
/// did-become-active notification only arrives on an activation <em>transition</em>, i.e. when the click comes from
/// another app (and when Cmd-Tab raises Hub), so on its own it misses the "closed, then clicked again" case.</para>
///
/// <para>The reopen half does override one method, and the honest name for it is an isa-swizzle: a dynamic subclass
/// of Avalonia's own delegate class, installed on that one delegate object, which restores the window and then
/// reports the reopen as handled. Everything else on the delegate is inherited untouched, and the original
/// implementation — if the class has one — is called first, so this is an addition to Avalonia's delegate rather
/// than a replacement for it. The notification half touches nothing: it is an observer of Hub's own on the shared
/// centre. Both, and the callback itself, only put a hidden window back: they deliberately do not run
/// <see cref="MainWindow.ActivateFromRequest"/>, whose notification bookkeeping (open the conversation an alert was
/// about) belongs to the window's own <c>Activated</c> handler, which gates it on the window having genuinely left
/// the foreground.</para>
/// </summary>
internal static partial class MacDockActivation
{
    /// <summary>The AppKit notification name. Written out rather than read from anywhere: the string <em>is</em> the
    /// contract, and a typo in it fails as "nothing ever happens", which no compiler sees.</summary>
    private const string BecameActiveNotification = "NSApplicationDidBecomeActiveNotification";

    private const string ObserverClassName = "AxmolHubActivationObserver";

    /// <summary>The subclass of Avalonia's application delegate that answers the reopen event. Distinct from the
    /// observer class above: that one is Hub's own object on the notification centre, this one is a stand-in for
    /// somebody else's class and must inherit all of it.</summary>
    private const string ReopenDelegateClassName = "AxmolHubReopenDelegate";

    /// <summary><c>v@:@</c>: void, self, _cmd, one object — the shape the runtime reads off <c>class_addMethod</c>.
    /// </summary>
    private static readonly byte[] ActivatedSignature = Encoding.ASCII.GetBytes("v@:@\0");

    /// <summary><c>B@:@c</c>: BOOL, self, _cmd, one object and one BOOL — <c>applicationShouldHandleReopen:
    /// hasVisibleWindows:</c>. ObjC encodes <c>BOOL</c> as <c>c</c> on the 64-bit runtime.</summary>
    private static readonly byte[] ReopenSignature = Encoding.ASCII.GetBytes("B@:@c\0");
    private static readonly byte[] OpenUrlsSignature = Encoding.ASCII.GetBytes("v@:@@\0");

    private static readonly ActivatedHandler Activated = OnActivated;
    private static readonly ReopenHandler Reopen = OnReopen;
    private static readonly OpenUrlsHandler OpenUrls = OnOpenUrls;
    private static IntPtr _observer;
    private static IntPtr _reopenTarget;
    private static IntPtr _reopenTargetClass;
    private static ReopenHandler? _originalReopen;
    private static OpenUrlsHandler? _originalOpenUrls;
    private static bool _installed;
    private static Action? _restore;
    private static Action<string>? _openUri;

    /// <summary>Wires both signals and reports whether the reopen one went on — that is the one a caller may have to
    /// make up for, because it is the only signal a Dock click of an already-frontmost Hub produces. False off
    /// macOS, so a caller can state the platform as a fact rather than branching twice.</summary>
    internal static bool Install(Action restore, Action<string>? openUri = null)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        _restore = restore;
        _openUri = openUri;
        if (_installed) return _reopenTarget != IntPtr.Zero;

        // The class is created once per process; a second Install (the self-check installs and uninstalls its own)
        // only puts another observer on the centre, because class_addMethod on an existing class would fail for a
        // method that is already registered.
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
        return HookReopen();
    }

    /// <summary>The reopen half: a subclass of whatever class Avalonia's application delegate is, carrying one method
    /// that restores the window, installed on that single delegate object. Returns false when there is no delegate to
    /// stand in for — the caller then has to offer the window another way back, because AppKit would have had nobody
    /// to tell.</summary>
    private static bool HookReopen()
    {
        var app = Send0(objc_getClass("NSApplication"), Selector("sharedApplication"));
        if (app == IntPtr.Zero) return false;
        var appDelegate = Send0(app, Selector("delegate"));
        if (appDelegate == IntPtr.Zero) return false;
        var original = object_getClass(appDelegate);
        if (original == IntPtr.Zero) return false;

        var selector = Selector("applicationShouldHandleReopen:hasVisibleWindows:");

        // Kept so the override adds to Avalonia's answer instead of discarding it: its class may well implement the
        // method already, and then what it does with a reopen stays its business. Looked up separately from the
        // subclass below, because a second Install finds the subclass already made and still has to know this.
        if (_originalReopen is null)
        {
            var existing = class_getInstanceMethod(original, selector);
            if (existing != IntPtr.Zero)
            {
                var implementation = method_getImplementation(existing);
                if (implementation != IntPtr.Zero)
                    _originalReopen = Marshal.GetDelegateForFunctionPointer<ReopenHandler>(implementation);
            }
        }

        var openUrlsSelector = Selector("application:openURLs:");
        if (_originalOpenUrls is null)
        {
            var existing = class_getInstanceMethod(original, openUrlsSelector);
            if (existing != IntPtr.Zero)
            {
                var implementation = method_getImplementation(existing);
                if (implementation != IntPtr.Zero)
                    _originalOpenUrls = Marshal.GetDelegateForFunctionPointer<OpenUrlsHandler>(implementation);
            }
        }

        var subclass = objc_getClass(ReopenDelegateClassName);
        if (subclass == IntPtr.Zero)
        {
            subclass = objc_allocateClassPair(original, ReopenDelegateClassName, 0);
            if (subclass == IntPtr.Zero) return false;
            if (class_addMethod(subclass, selector, Marshal.GetFunctionPointerForDelegate(Reopen),
                    ReopenSignature) == 0
                || class_addMethod(subclass, openUrlsSelector, Marshal.GetFunctionPointerForDelegate(OpenUrls),
                    OpenUrlsSignature) == 0)
                return false;
            objc_registerClassPair(subclass);
        }

        object_setClass(appDelegate, subclass);
        _reopenTarget = appDelegate;
        _reopenTargetClass = original;
        return true;
    }

    /// <summary>Takes the observer back off the centre and puts the delegate back on its own class. Only the shutdown
    /// path needs it — the process is ending — but a notification centre outliving its observer, or a delegate left
    /// wearing Hub's subclass, is a crash waiting for a slow machine rather than a detail worth leaving to "we exit
    /// fast enough".</summary>
    internal static void Uninstall()
    {
        if (_installed && _observer != IntPtr.Zero)
        {
            var center = Send0(objc_getClass("NSNotificationCenter"), Selector("defaultCenter"));
            SendObject(center, Selector("removeObserver:"), _observer);
            Send0(_observer, Selector("release"));
        }

        if (_reopenTarget != IntPtr.Zero && _reopenTargetClass != IntPtr.Zero)
            object_setClass(_reopenTarget, _reopenTargetClass);

        _observer = IntPtr.Zero;
        _reopenTarget = IntPtr.Zero;
        _reopenTargetClass = IntPtr.Zero;
        _originalReopen = null;
        _originalOpenUrls = null;
        _installed = false;
        _restore = null;
        _openUri = null;
    }

    /// <summary>The callback. AppKit posts and sends these on the main run loop, which is Avalonia's UI thread, so the
    /// restore is called directly: a dispatcher hop would only put the window up after whatever the click was meant
    /// to show. Both signals land here, and the restore is idempotent — a Dock click from another app produces both.
    /// </summary>
    private static void OnActivated(IntPtr self, IntPtr selector, IntPtr notification)
    {
        _ = self;
        _ = selector;
        _ = notification;
        _restore?.Invoke();
    }

    /// <summary>Answers the reopen event. Avalonia's own answer is asked first and thrown away — it is called for its
    /// effects, not for its value, since the value this method must return is "yes, handled" and the window is back.
    /// </summary>
    private static byte OnReopen(IntPtr self, IntPtr selector, IntPtr sender, byte hasVisibleWindows)
    {
        _ = self;
        _ = selector;
        if (_originalReopen is { } original) _ = original(self, selector, sender, hasVisibleWindows);
        _restore?.Invoke();
        return 1;
    }

    /// <summary>Receives Launch Services URL opens for both a cold launch and an already-running app. The URL is
    /// delivered by AppKit as an NSArray of NSURLs, not as a process argument.</summary>
    private static void OnOpenUrls(IntPtr self, IntPtr selector, IntPtr application, IntPtr urls)
    {
        if (_originalOpenUrls is { } original) original(self, selector, application, urls);
        var count = SendCount(urls, Selector("count"));
        for (nuint index = 0; index < count; index++)
        {
            var url = SendObjectAtIndex(urls, Selector("objectAtIndex:"), index);
            var absoluteString = Send0(url, Selector("absoluteString"));
            var utf8 = Send0(absoluteString, Selector("UTF8String"));
            var value = Marshal.PtrToStringUTF8(utf8);
            if (value is not null) _openUri?.Invoke(value);
        }
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ReopenHandler(IntPtr self, IntPtr selector, IntPtr sender, byte hasVisibleWindows);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OpenUrlsHandler(IntPtr self, IntPtr selector, IntPtr application, IntPtr urls);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "object_getClass")]
    private static extern IntPtr object_getClass(IntPtr instance);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "object_setClass")]
    private static extern IntPtr object_setClass(IntPtr instance, IntPtr type);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_getInstanceMethod")]
    private static extern IntPtr class_getInstanceMethod(IntPtr type, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "method_getImplementation")]
    private static extern IntPtr method_getImplementation(IntPtr method);
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
    private static extern nuint SendCount(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendObjectAtIndex(IntPtr receiver, IntPtr selector, nuint index);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendAddObserver(
        IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr method, IntPtr name, IntPtr value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendPost(IntPtr receiver, IntPtr selector, IntPtr name, IntPtr value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte SendReopen(IntPtr receiver, IntPtr selector, IntPtr sender, byte hasVisibleWindows);
}
