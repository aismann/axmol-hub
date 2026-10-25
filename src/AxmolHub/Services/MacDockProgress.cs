using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AxmolHub;

/// <summary>A Dock-owned AppKit view and NSTimer; independent of Avalonia window visibility/rendering.</summary>
internal sealed class MacDockProgress : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // The Dock scales whatever the content view draws, so the tile is described in fixed 128-point logic.
    private const double Tile = 128, RingInset = 3, Corner = 26, RingWidth = 5, RevolutionSeconds = 1.4;
    private static readonly NativeRect TrackRect = new(RingInset, RingInset, Tile - 2 * RingInset, Tile - 2 * RingInset);
    private static readonly NativeRect IconRect = new(8, 8, Tile - 16, Tile - 16);
    private static readonly double Perimeter = 2 * (Tile - 2 * RingInset - 2 * Corner) * 2 + 2 * Math.PI * Corner;
    // Longest and faintest first, so the bright head lands on top of its own tail.
    private static readonly (double Tail, double Alpha)[] Tails = [(.30, .16), (.16, .40), (.07, .95)];
    private static readonly DrawHandler DrawCallback = Draw;
    private static readonly TickHandler TickCallback = Tick;
    private static readonly Dictionary<IntPtr, MacDockProgress> Owners = [];
    private static IntPtr _viewClass;
    private IntPtr _tile, _previousView, _view, _icon, _timer;
    private long _started;
    internal int FramesForCheck { get; private set; }
    internal int DrawsForCheck { get; private set; }
    internal static double RingPerimeterForCheck => Perimeter;
    internal static (double X, double Y) RingPointForCheck(double distance) => RingPoint(distance);
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
            // The tile is the Dock's, so the icon is drawn by hand: a marquee around it means nothing can be left
            // to NSDockTile's own image drawing.
            DrawImage(owner._icon, Sel("drawInRect:fromRect:operation:fraction:"), IconRect, default, 2, 1);
            var context = Class("NSGraphicsContext");
            Send(context, Sel("saveGraphicsState"));
            try
            {
                // A dim full ring gives the moving segment a track to travel on, so the icon reads as "working"
                // instead of merely highlighted.
                Stroke(RoundedRect(Class("NSBezierPath"), Sel("bezierPathWithRoundedRect:xRadius:yRadius:"),
                    TrackRect, Corner, Corner), 1, .12, RingWidth);
                var phase = Stopwatch.GetElapsedTime(owner._started).TotalSeconds % RevolutionSeconds / RevolutionSeconds;
                foreach (var (tail, alpha) in Tails) StrokeComet(phase * Perimeter, tail * Perimeter, alpha);
            }
            finally { Send(context, Sel("restoreGraphicsState")); }
            owner.DrawsForCheck++;
        }
        catch (Exception ex) { Trace.WriteLine("Dock activity draw: " + ex); }
    }

    /// <summary>Strokes one segment of the ring, its head at <paramref name="head"/> and its tail behind it.</summary>
    private static void StrokeComet(double head, double length, double alpha)
    {
        const int samples = 22;
        var path = Send(Class("NSBezierPath"), Sel("bezierPath"));
        for (var i = 0; i <= samples; i++)
        {
            var (x, y) = RingPoint(head - length + length * i / samples);
            if (i == 0) MoveTo(path, Sel("moveToPoint:"), x, y);
            else LineTo(path, Sel("lineToPoint:"), x, y);
        }
        Stroke(path, 1, alpha, RingWidth);
    }

    private static void Stroke(IntPtr path, double white, double alpha, double width)
    {
        Send(Color(Class("NSColor"), Sel("colorWithCalibratedWhite:alpha:"), white, alpha), Sel("setStroke"));
        SendDouble(path, Sel("setLineWidth:"), width);
        SendInteger(path, Sel("setLineCapStyle:"), 1); // NSLineCapStyleRound
        Send(path, Sel("stroke"));
    }

    /// <summary>Walks the rounded rectangle by arc length, so equal steps are equal travel around the icon.</summary>
    private static (double X, double Y) RingPoint(double distance)
    {
        const double left = RingInset, bottom = RingInset, size = Tile - 2 * RingInset, radius = Corner;
        var straight = size - 2 * radius;
        var quarter = Math.PI * radius / 2;
        var d = distance % Perimeter;
        if (d < 0) d += Perimeter;
        if (d <= straight) return (left + radius + d, bottom);
        d -= straight;
        if (d <= quarter) return OnCorner(left + size - radius, bottom + radius, -90 + 90 * d / quarter);
        d -= quarter;
        if (d <= straight) return (left + size, bottom + radius + d);
        d -= straight;
        if (d <= quarter) return OnCorner(left + size - radius, bottom + size - radius, 90 * d / quarter);
        d -= quarter;
        if (d <= straight) return (left + size - radius - d, bottom + size);
        d -= straight;
        if (d <= quarter) return OnCorner(left + radius, bottom + size - radius, 90 + 90 * d / quarter);
        d -= quarter;
        if (d <= straight) return (left, bottom + size - radius - d);
        d -= straight;
        return OnCorner(left + radius, bottom + radius, 180 + 90 * Math.Min(d / quarter, 1));
    }

    private static (double X, double Y) OnCorner(double centerX, double centerY, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return (centerX + Corner * Math.Cos(radians), centerY + Corner * Math.Sin(radians));
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
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendDouble(IntPtr receiver, IntPtr selector, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendInteger(IntPtr receiver, IntPtr selector, nint value);
    // moveToPoint:/lineToPoint: take NSPoint by value; two doubles is the same argument shape as the struct.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void MoveTo(IntPtr receiver, IntPtr selector, double x, double y);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void LineTo(IntPtr receiver, IntPtr selector, double x, double y);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr NewString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
}
