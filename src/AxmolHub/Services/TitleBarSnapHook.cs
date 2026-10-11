using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace AxmolHub;

/// <summary>
/// Windows 11 Snap Layouts for the app's own maximize button.
///
/// <para>Windows decides whether to raise the layouts flyout from the return value of
/// <c>WM_NCHITTEST</c>: the cursor being over the maximize button must answer <c>HTMAXBUTTON</c>. A native
/// caption button gets that for free; a button the app draws does not, which is why GitHub Copilot Desktop
/// shows the flyout and this app did not.</para>
///
/// <para>Why this is hand-written rather than configured: as of Avalonia 12.1.3 the Win32 backend has no
/// <c>HTMAXBUTTON</c> handling at all — <c>WindowImpl</c> answers only the eight resize edges — and
/// <c>WindowImpl.WndProcHookCallback</c> is internal, with no public property on <c>Window</c> to reach it.
/// So the window procedure is subclassed here, exactly as the Avalonia issue that asked for this feature
/// (#11409) said it would have to be. The hook is the app's only Win32 message hook; every message except the
/// hit test is passed straight through to the previous procedure, so nothing else about the window changes.</para>
///
/// <para><b>Hover only.</b> <c>HTMAXBUTTON</c> is answered for a move over the button and deliberately *not*
/// for a press: a press would make the system treat the click as a caption-button click, which would
/// double-apply our own click handler (it toggles the window state) and change what dragging the bar does.
/// The flyout appears on hover, and clicking still runs the app's handler.</para>
/// </summary>
internal static class TitleBarSnapHook
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;
    private const int GwlWndProc = -4;
    private const int SmCxSizeFrame = 32;
    private const int SmCxPaddedBorder = 92;
    private const int SmCySizeFrame = 33;
    private const int SmCyPaddedBorder = 93;

    /// <summary>The thickness of the window's own resize frame, in physical pixels — the strip the system keeps
    /// between the client area and the window's outer edge. A caption button centred on that edge has to reach it
    /// (that is what "flush like the system's own close button" means), so the app's title bar is laid out by
    /// extending its right end by this much. Measured rather than hard-coded, because the frame is DPI- and
    /// theme-dependent: <c>SM_CXSIZEFRAME + SM_CXPADDEDBORDER</c> is exactly how WinUI computes it.</summary>
    internal static int ResizeFrameWidth =>
        OperatingSystem.IsWindows()
            ? GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder)
            : 0;

    /// <summary>The same for the bottom edge. Not used by the title bar, but measured together so the pair can
    /// never drift apart.</summary>
    internal static int ResizeFrameHeight =>
        OperatingSystem.IsWindows()
            ? GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCyPaddedBorder)
            : 0;

    /// <summary>Every subclassed window's bridge, so the delegate the system holds stays alive for the window's
    /// whole life. A delegate collected while Win32 still points at it is a crash on the next message.</summary>
    private static readonly System.Collections.Generic.Dictionary<IntPtr, WindowBridge> Bridges = [];

    /// <summary>Installs the hook, or does nothing when there is nothing to install (a non-Windows host, or the
    /// platform handle is not a Win32 one — a headless/self-check run). Never throws: a missing flyout is an
    /// appearance problem and must not take the window down with it.</summary>
    internal static void Install(Window window, Button maximizeButton)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
            if (Bridges.ContainsKey(hwnd)) return;

            var bridge = new WindowBridge(window, maximizeButton);
            var previous = SetWindowLongPtr(hwnd, GwlWndProc, bridge.Proc);
            if (previous == IntPtr.Zero)
            {
                // Nothing was subclassed, so the delegate must not be kept alive either.
                GC.KeepAlive(bridge);
                return;
            }

            bridge.Previous = previous;
            Bridges[hwnd] = bridge;
            InstalledForCheck = true;
        }
        catch (Exception ex)
        {
            InstalledForCheck = false;
            System.Diagnostics.Trace.TraceWarning($"Could not install the Snap Layouts hit test: {ex}");
        }
    }

    private sealed class WindowBridge
    {
        private readonly Window _window;
        private readonly Button _maximize;
        private readonly WndProc _proc;
        private bool _hovered;

        internal IntPtr Previous;

        internal WindowBridge(Window window, Button maximizeButton)
        {
            _window = window;
            _maximize = maximizeButton;
            _proc = Dispatch;
        }

        internal IntPtr Proc => Marshal.GetFunctionPointerForDelegate(_proc);

        private IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (Previous == IntPtr.Zero)
            {
                // The chain is broken, which must never happen; saying so beats looping or crashing.
                System.Diagnostics.Trace.TraceWarning("Snap Layouts hook reached with no previous window procedure.");
                return IntPtr.Zero;
            }

            try
            {
                switch (msg)
                {
                    case WmNcHitTest when _maximize.Bounds.Width > 0 && IsOverMaximizeButton(lParam):
                        return (IntPtr)HtMaxButton;

                    // Answering HTMAXBUTTON above is what raises the flyout, and it is also why the plate's own
                    // hover never appears: the system treats that point as a *non-client* caption button and stops
                    // sending client mouse moves, so Avalonia's :pointerover never fires for this one control.
                    // The non-client messages it sends instead are the same information, so the hover state is
                    // driven from them. Every other control keeps its normal client hover untouched.
                    case WmNcMouseMove when IsOverMaximizeButton(lParam):
                        SetMaximizeHover(true);
                        break;

                    case WmNcMouseLeave:
                        SetMaximizeHover(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                // A message hook that throws takes the whole window with it, and this one runs on every mouse
                // move. A failed hit test must cost at most the flyout.
                System.Diagnostics.Trace.TraceWarning($"Snap Layouts hit test failed: {ex.Message}");
            }

            return CallWindowProc(Previous, hwnd, msg, wParam, lParam);
        }

        /// <summary>Shows or hides the maximize plate's hover background. The class carries the same brush the
        /// other two plates get from <c>:pointerover</c>, so all three read as one set.</summary>
        private void SetMaximizeHover(bool hovered)
        {
            if (hovered == _hovered) return;
            _hovered = hovered;
            _maximize.Classes.Set("nchover", hovered);
        }

        /// <summary>Whether the hit test's cursor position falls inside the maximize plate. The position is
        /// converted through <see cref="TopLevel.PointToScreen"/> and both rects come from the same place, so
        /// DPI cancels out and no scale factor has to be assumed.</summary>
        private bool IsOverMaximizeButton(IntPtr lParam)
        {
            var position = new PixelPoint((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));
            var local = _window.PointToClient(position);
            var origin = _maximize.TranslatePoint(new Point(0, 0), _window);
            if (origin is not { } topLeft) return false;

            var rect = new Rect(topLeft, _maximize.Bounds.Size);
            return rect.Contains(new Point(local.X, local.Y));
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Whether the most recent <see cref="Install"/> really subclassed a window. Exposed so the shell
    /// self-check can prove it: this hook silently declines whenever the platform handle is not a Win32 one or
    /// <c>SetWindowLongPtr</c> refuses, and "the flyout never appears" would otherwise be indistinguishable from
    /// a typo in the message constant.</summary>
    internal static bool InstalledForCheck { get; private set; }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>Passes a message to the window procedure that was in place before this hook. The pointer is
    /// handed over as-is: this is the entry point that exists precisely for a stored <c>WNDPROC</c> pointer, and
    /// converting it to a delegate instead is what crashed the process the first time this hook was tried.</summary>
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", ExactSpelling = true)]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);
}