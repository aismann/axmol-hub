using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace AxmolHub;

/// <summary>
/// The app's own maximize plate, as far as Windows is concerned: the Snap Layouts flyout, the pressed plate, and
/// the click itself.
///
/// <para>Windows decides whether to raise the layouts flyout from the return value of <c>WM_NCHITTEST</c>: the
/// cursor being over the maximize button must answer <c>HTMAXBUTTON</c>. A native caption button gets that for
/// free; a button the app draws does not, which is why GitHub Copilot Desktop shows the flyout and this app did
/// not.</para>
///
/// <para>Answering it has a price that the first cut of this hook paid without noticing: the system sends the hit
/// test when the cursor moves <em>and when a mouse button goes down</em>, so a press over the plate is classified
/// as a non-client press and arrives as <c>WM_NCLBUTTONDOWN</c> — never as <c>WM_LBUTTONDOWN</c>, which is the
/// only thing Avalonia turns into a click. The plate's <c>Click</c> handler therefore never ran. Letting the
/// system's own caption handling do the work instead is not available either: at
/// <see cref="WindowDecorations.BorderOnly"/> this window is created without <c>WS_SYSMENU</c> (Avalonia adds that
/// style only for the <c>Full</c> decorations) and Avalonia's window procedure does not handle incoming
/// non-client button messages at all, so <c>DefWindowProc</c> has no caption to act on. So the hook answers the
/// hit test <em>and</em> runs the plate's press/click protocol itself — the same protocol a real caption button
/// has, which is why <c>HTMAXBUTTON</c> is still answered while the button is down.</para>
///
/// <para>Why this is hand-written rather than configured: as of Avalonia 12.1.3 the Win32 backend has no
/// <c>HTMAXBUTTON</c> handling at all — <c>WindowImpl</c> answers only the eight resize edges — and
/// <c>WindowImpl.WndProcHookCallback</c> is internal, with no public property on <c>Window</c> to reach it.
/// So the window procedure is subclassed here, exactly as the Avalonia issue that asked for this feature
/// (#11409) said it would have to be. The hook is the app's only Win32 message hook; every message it does not
/// own is passed straight through to the previous procedure, so nothing else about the window changes.</para>
/// </summary>
internal static class TitleBarSnapHook
{
    private const int WmCancelMode = 0x001F;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
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

    /// <summary>
    /// What the hook does with one non-client message. Named as effects rather than as message numbers because the
    /// message numbers are not a protocol the app owns: the system repeats the hit test, the move and the button
    /// messages in an order this hook must not assume.
    /// </summary>
    internal enum NcAction
    {
        /// <summary>Not ours: hand it to the procedure that was there before this hook.</summary>
        Forward,

        /// <summary>Answer <c>HTMAXBUTTON</c>, which is what raises the flyout.</summary>
        HitTest,

        /// <summary>The plate went down: show it pressed and keep the message.</summary>
        Press,

        /// <summary>The cursor is over the plate and nothing is held down.</summary>
        HoverOn,

        /// <summary>The cursor has left the plate, or the window, with nothing held down.</summary>
        HoverOff,

        /// <summary>A press this hook owns ended somewhere that is not the plate: drop it without acting.</summary>
        Release,

        /// <summary>The system cancelled whatever was in progress under the cursor. The press state is dropped for
        /// the same reason as <see cref="Release"/> — its release is a message that will never come — but the
        /// message itself still belongs to whoever else is listening, so it is forwarded.</summary>
        CancelPress,

        /// <summary>A press this hook owns ended on the plate: run the action.</summary>
        Toggle,
    }

    /// <summary>
    /// The plate's whole non-client protocol as one pure decision, so the self-check can drive every arm without a
    /// mouse — and so the arms that are easy to get wrong (a release that lost the plate, a press with no release)
    /// are stated once instead of being spread over a message switch.
    /// </summary>
    internal static NcAction Classify(uint message, bool overPlate, bool pressed)
    {
        switch (message)
        {
            case WmNcHitTest:
                return overPlate ? NcAction.HitTest : NcAction.Forward;

            case WmNcLButtonDown:
                return overPlate ? NcAction.Press : NcAction.Forward;

            case WmNcLButtonUp:
                // A release this hook never saw the press for belongs to something else — a resize edge, the
                // flyout's own zone, a caption the system decided to drag.
                if (!pressed) return NcAction.Forward;
                return overPlate ? NcAction.Toggle : NcAction.Release;

            case WmNcMouseMove:
                if (pressed) return overPlate ? NcAction.Forward : NcAction.Release;
                return overPlate ? NcAction.HoverOn : NcAction.HoverOff;

            case WmNcMouseLeave:
                return pressed ? NcAction.Release : NcAction.HoverOff;

            // The system's "whatever modal thing you were doing is over" — a dialog opening under the cursor, the
            // window losing the foreground. Without this the plate can be left dark forever, because the release
            // it is waiting for is a message that will never come.
            case WmCancelMode:
                return pressed ? NcAction.CancelPress : NcAction.Forward;

            default:
                return NcAction.Forward;
        }
    }

    /// <summary>
    /// Installs the hook, or does nothing when there is nothing to install (a non-Windows host, or the platform
    /// handle is not a Win32 one — a headless/self-check run). Never throws: a missing flyout is an appearance
    /// problem and must not take the window down with it.
    ///
    /// <paramref name="toggleMaximize"/> is the window's own maximize/restore flip, not a state this hook sets:
    /// which of the two states the window is in is the shell's business, and it is the same method the plate's
    /// client click calls, so the glyph and the tooltip follow either way through one path.
    /// </summary>
    internal static void Install(Window window, Button maximizeButton, Action toggleMaximize)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
            if (Bridges.ContainsKey(hwnd)) return;

            var bridge = new WindowBridge(window, maximizeButton, toggleMaximize);
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
        private readonly Action _toggleMaximize;
        private readonly WndProc _proc;
        private bool _hovered;

        /// <summary>Whether a press this hook answered with <see cref="NcAction.Press"/> is still open. Cleared by
        /// every arm that ends one — and, in case the release never arrives, by the next press.</summary>
        private bool _pressed;

        internal IntPtr Previous;

        internal WindowBridge(Window window, Button maximizeButton, Action toggleMaximize)
        {
            _window = window;
            _maximize = maximizeButton;
            _toggleMaximize = toggleMaximize;
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
                // Only these four carry a screen point in lParam. Asking the others would convert a size or a
                // command into a coordinate and answer a question nobody asked.
                var overPlate = (msg == WmNcHitTest || msg == WmNcMouseMove
                                 || msg == WmNcLButtonDown || msg == WmNcLButtonUp)
                                && _maximize.Bounds.Width > 0
                                && IsOverMaximizeButton(lParam);

                switch (Classify(msg, overPlate, _pressed))
                {
                    case NcAction.HitTest:
                        return (IntPtr)HtMaxButton;

                    // The action is run here, inside the window procedure, the way a real caption button does it:
                    // posting it instead would land the toggle a frame after the click and add a state to race.
                    case NcAction.Toggle:
                        SetPressed(false);
                        _toggleMaximize();
                        return IntPtr.Zero;

                    case NcAction.Press:
                        SetPressed(true);
                        return IntPtr.Zero;

                    case NcAction.Release:
                        SetPressed(false);
                        SetMaximizeHover(false);
                        return IntPtr.Zero;

                    case NcAction.CancelPress:
                        SetPressed(false);
                        SetMaximizeHover(false);
                        break;

                    case NcAction.HoverOn:
                        SetMaximizeHover(true);
                        break;

                    case NcAction.HoverOff:
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

        /// <summary>The plate's pressed background. <c>:pressed</c> cannot serve here for the same reason
        /// <c>:pointerover</c> cannot: the point is non-client, so the pseudo-class never fires on this one plate
        /// while it works on the two beside it.</summary>
        private void SetPressed(bool pressed)
        {
            if (pressed == _pressed) return;
            _pressed = pressed;
            _maximize.Classes.Set("ncpressed", pressed);
        }

        /// <summary>Whether the hit test's cursor position falls inside the maximize plate. The position is
        /// converted through <see cref="TopLevel.PointToScreen"/> and both rects come from the same place, so
        /// DPI cancels out and no scale factor has to be assumed.</summary>
        private bool IsOverMaximizeButton(IntPtr lParam)
        {
            var position = UnpackPoint(lParam);
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

    /// <summary>
    /// Sends the window one non-client message at a screen point, exactly as the system would. The self-check's
    /// only way in: the press protocol above is invisible to every assertion that can be written against the object
    /// graph — the click that goes missing is a message the app never receives — so it is driven the way Windows
    /// drives it, through the real window procedure of the real window.
    ///
    /// Sends rather than posts, so the state a check reads next is the state the message produced; and sends on the
    /// calling thread, which is the UI thread the window belongs to, so this is a direct call and not a wait.
    /// </summary>
    internal static IntPtr SendNonClientForCheck(Window window, uint message, PixelPoint screen)
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return IntPtr.Zero;
        return SendMessage(hwnd, message, new IntPtr(HtMaxButton), PackPoint(screen));
    }

    /// <summary>A screen point packed into an <c>lParam</c> the way the non-client messages carry it: x in the low
    /// word, y in the high word, both signed 16-bit. Shared with the check on purpose, so a probe cannot disagree
    /// with <see cref="WindowBridge.IsOverMaximizeButton"/> about what a coordinate means.</summary>
    internal static IntPtr PackPoint(PixelPoint point)
        => new((point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16));

    private static PixelPoint UnpackPoint(IntPtr lParam)
        => new((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>Passes a message to the window procedure that was in place before this hook. The pointer is
    /// handed over as-is: this is the entry point that exists precisely for a stored <c>WNDPROC</c> pointer, and
    /// converting it to a delegate instead is what crashed the process the first time this hook was tried.</summary>
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", ExactSpelling = true)]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);
}
