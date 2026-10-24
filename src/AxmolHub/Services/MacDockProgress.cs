using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AxmolHub;

/// <summary>A Dock-owned AppKit view and NSTimer; independent of Avalonia window visibility/rendering.</summary>
internal sealed class MacDockProgress : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly DrawHandler DrawCallback = Draw;
    private static readonly TickHandler TickCallback = Tick;
    private static readonly Dictionary<IntPtr, MacDockProgress> Owners = [];
    private static IntPtr _viewClass;
    private IntPtr _tile, _previousView, _view, _icon, _timer;
    private long _started;
    internal int FramesForCheck { get; private set; }
    internal int DrawsForCheck { get; private set; }
    internal bool HasResourcesForCheck => _tile != IntPtr.Zero || _view != IntPtr.Zero || _timer != IntPtr.Zero;

    internal void SetInProgress(bool active)
    {
        if (!active) { Dispose(); return; }
        if (_view != IntPtr.Zero) return;
        try
        {
            EnsureViewClass();
            var app = Send(Class("NSApplication"), Sel("sharedApplication"));
            _tile = Send(app, Sel("dockTile"));
            _previousView = Send(Send(_tile, Sel("contentView")), Sel("retain"));
            _icon = Send(Send(app, Sel("applicationIconImage")), Sel("retain"));
            _view = InitFrame(Send(_viewClass, Sel("alloc")), Sel("initWithFrame:"), new(0, 0, 128, 128));
            if (_tile == IntPtr.Zero || _icon == IntPtr.Zero || _view == IntPtr.Zero)
                throw new InvalidOperationException("AppKit did not create the Dock activity view.");
            Owners.Add(_view, this);
            _started = Stopwatch.GetTimestamp();
            SendObject(_tile, Sel("setContentView:"), _view);
            Display();
            // Common modes keep ticking while menus or native tracking loops are open. The native timer
            // retains its target; invalidate it before releasing the view to break that ownership chain.
            _timer = Send(CreateTimer(Class("NSTimer"), Sel("timerWithTimeInterval:target:selector:userInfo:repeats:"),
                1.0 / 20, _view, Sel("hubProgressTick:"), IntPtr.Zero, 1), Sel("retain"));
            if (_timer == IntPtr.Zero) throw new InvalidOperationException("AppKit did not create the Dock activity timer.");
            var mode = NewString(Class("NSString"), Sel("stringWithUTF8String:"), "kCFRunLoopCommonModes");
            AddTimer(Send(Class("NSRunLoop"), Sel("mainRunLoop")), Sel("addTimer:forMode:"), _timer, mode);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero)
        {
            Send(_timer, Sel("invalidate"));
            Send(_timer, Sel("release"));
            _timer = IntPtr.Zero;
        }
        if (_tile != IntPtr.Zero)
        {
            SendObject(_tile, Sel("setContentView:"), _previousView);
            Send(_tile, Sel("display"));
            _tile = IntPtr.Zero;
        }
        if (_view != IntPtr.Zero)
        {
            Owners.Remove(_view);
            Send(_view, Sel("release"));
            _view = IntPtr.Zero;
        }
        Send(_previousView, Sel("release"));
        Send(_icon, Sel("release"));
        _previousView = _icon = IntPtr.Zero;
    }

    private static void EnsureViewClass()
    {
        if (_viewClass != IntPtr.Zero) return;
        var type = AllocateClass(Class("NSView"), "AxmolHubDockProgressView", 0);
        if (type == IntPtr.Zero) throw new InvalidOperationException("Cannot register the Dock activity view.");
        AddMethod(type, Sel("drawRect:"), Marshal.GetFunctionPointerForDelegate(DrawCallback),
            Encoding.ASCII.GetBytes("v@:{CGRect={CGPoint=dd}{CGSize=dd}}\0"));
        AddMethod(type, Sel("hubProgressTick:"), Marshal.GetFunctionPointerForDelegate(TickCallback),
            Encoding.ASCII.GetBytes("v@:@\0"));
        RegisterClass(type);
        _viewClass = type;
    }

    private void Display()
    {
        SendBool(_view, Sel("setNeedsDisplay:"), 1);
        Send(_tile, Sel("display"));
        FramesForCheck++;
    }

    private static void Tick(IntPtr self, IntPtr command, IntPtr timer)
    {
        // Managed exceptions must not cross an Objective-C callback boundary.
        try { if (Owners.TryGetValue(self, out var owner)) owner.Display(); }
        catch (Exception ex) { Trace.WriteLine("Dock activity tick: " + ex); }
    }

    private static void Draw(IntPtr self, IntPtr command, NativeRect dirty)
    {
        try
        {
            if (!Owners.TryGetValue(self, out var owner)) return;
            DrawImage(owner._icon, Sel("drawInRect:fromRect:operation:fraction:"),
                new(0, 0, 128, 128), default, 2, 1);
            var context = Class("NSGraphicsContext");
            Send(context, Sel("saveGraphicsState"));
            try
            {
                var track = RoundedRect(Class("NSBezierPath"), Sel("bezierPathWithRoundedRect:xRadius:yRadius:"),
                    new(12, 8, 104, 8), 4, 4);
                Send(Color(Class("NSColor"), Sel("colorWithCalibratedWhite:alpha:"), 0, .3), Sel("setFill"));
                Send(track, Sel("fill"));
                Send(track, Sel("addClip"));
                var phase = (Stopwatch.GetElapsedTime(owner._started).TotalSeconds / 1.2) % 1;
                var segment = RoundedRect(Class("NSBezierPath"), Sel("bezierPathWithRoundedRect:xRadius:yRadius:"),
                    new(12 - 28 + phase * 132, 8, 28, 8), 4, 4);
                Send(Color(Class("NSColor"), Sel("colorWithCalibratedWhite:alpha:"), .95, 1), Sel("setFill"));
                Send(segment, Sel("fill"));
            }
            finally { Send(context, Sel("restoreGraphicsState")); }
            owner.DrawsForCheck++;
        }
        catch (Exception ex) { Trace.WriteLine("Dock activity draw: " + ex); }
    }

    // CGFloat is double on both supported macOS ABIs. No struct-return objc_msgSend is used: x64 would
    // require objc_msgSend_stret for NSRect, unlike arm64. Fixed logical bounds are scaled by NSDockTile.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect(double x, double y, double width, double height)
    {
        private readonly double _x = x, _y = y, _width = width, _height = height;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DrawHandler(IntPtr self, IntPtr command, NativeRect rect);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TickHandler(IntPtr self, IntPtr command, IntPtr timer);
    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern IntPtr Class(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern IntPtr Sel(string name);
    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")] private static extern IntPtr AllocateClass(IntPtr parent, string name, nuint extra);
    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")] private static extern void RegisterClass(IntPtr type);
    [DllImport(ObjC, EntryPoint = "class_addMethod")] private static extern byte AddMethod(IntPtr type, IntPtr selector, IntPtr implementation, byte[] types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendObject(IntPtr receiver, IntPtr selector, IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr selector, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr InitFrame(IntPtr receiver, IntPtr selector, NativeRect frame);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr RoundedRect(IntPtr receiver, IntPtr selector, NativeRect frame, double xRadius, double yRadius);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Color(IntPtr receiver, IntPtr selector, double white, double alpha);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void DrawImage(IntPtr receiver, IntPtr selector, NativeRect target, NativeRect source, nuint operation, double fraction);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr CreateTimer(IntPtr receiver, IntPtr selector, double seconds, IntPtr target, IntPtr action, IntPtr info, byte repeats);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void AddTimer(IntPtr receiver, IntPtr selector, IntPtr timer, IntPtr mode);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr NewString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
}
