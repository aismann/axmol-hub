using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace AxmolHub;
internal static partial class TitleBarSnapHook
{
    /// <summary>Whether the most recent <see cref = "Install"/> really subclassed a window. Exposed so the shell
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
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            return IntPtr.Zero;
        return SendMessage(hwnd, message, new IntPtr(HtMaxButton), PackPoint(screen));
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
