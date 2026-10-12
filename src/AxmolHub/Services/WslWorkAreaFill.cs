using System;
using System.IO;

namespace AxmolHub;

/// <summary>
/// WSLg needs the shell's "maximize" done by the app instead of by the window manager.
///
/// <para>Measured on WSL2 Ubuntu-24.04 (WSLg, Avalonia's X11 backend over XWayland, host display at 150%):</para>
/// <list type="bullet">
///   <item>a window the window manager has maximized sits at root (0,0) sized 2560x1368 while the X screen is
///   2560x1440, and it advertises <c>_NET_FRAME_EXTENTS = 6,6,27,6</c> — yet the window's own frame is 32 px on
///   every side. The compositor draws our surface from one origin and takes pointer input for the client from
///   another, ~27–35 px apart, so everything the shell paints in its top 32 px is unreachable: the caption plates
///   never light on hover, never answer a click, and the bar cannot be dragged. Recorded from a real pointer:
///   clicks aimed at plates drawn at y=0..31 arrive as y=43, while the same physical spot in a self-filled window
///   arrives as y=8 and y=17 and hits both plates.</item>
///   <item>a window in <see cref="Avalonia.Controls.WindowState.Normal"/> that the app has sized to the same rect
///   has no such split — paint and input agree, and the bar works.</item>
/// </list>
///
/// <para>So on this host the app asks the window manager to maximize <em>once</em>, records the rect it actually
/// used (its work area is smaller than <c>Screen.WorkingArea</c> reports, by the 72 px the Windows taskbar costs at
/// 150% scaling), then leaves the maximized state and applies that rect itself. The rect is cached, so a later
/// toggle never shows the window manager's maximized state again and the window does not move a pixel when the
/// flag is dropped.</para>
///
/// <para>Windows is untouched (DWM, and <see cref="TitleBarSnapHook"/> built on it), and so are real Linux desktops
/// and macOS: their window managers maximize without this split, and they un-maximize on a caption drag either
/// natively or through the restore the shell does before handing over the move.</para>
/// </summary>
internal static class WslWorkAreaFill
{
    /// <summary>Whether this host is Windows Subsystem for Linux. .NET 10 exposes no <c>OperatingSystem.IsWsl</c>,
    /// so this reads the two conventions WSL itself publishes: the distribution name in the environment, and the
    /// kernel version string, which carries "microsoft" on both generation 1 and 2. Cached because the answer can
    /// only be true or false for the life of the process.</summary>
    internal static bool IsWslHost => _isWsl ??= DetectWsl();

    /// <summary>Whether the shell should fill the work area itself rather than ask the window manager to maximize.
    /// Pure so the self-check can drive every platform combination without a window.</summary>
    internal static bool Applies(bool isWindows, bool isWslHost) => !isWindows && isWslHost;

    internal static bool AppliesHere => !OperatingSystem.IsWindows() && IsWslHost;

    private static bool? _isWsl;

    private static bool DetectWsl()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP")))
        {
            return true;
        }

        try
        {
            return File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No readable kernel version line: not a WSL verdict we can support, so treat it as an ordinary
            // Linux session and let the window manager maximize. A wrong guess here costs the caption bar its
            // clicks, which is the bug this class exists to avoid.
            return false;
        }
    }
}
