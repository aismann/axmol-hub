using System.Diagnostics;
using Avalonia.Controls;

namespace AxmolHub;

/// <summary>UI-thread-only native activity indicator. No work is attached to streamed tokens.</summary>
internal sealed class TaskbarInProgress(Action<bool> setNative, Action? releaseNative = null) : IDisposable
{
    private bool _active;
    private bool _disposed;

    internal static TaskbarInProgress ForWindow(Window window)
    {
        if (OperatingSystem.IsMacOS())
        {
            var dock = new MacDockProgress();
            return new(dock.SetInProgress, dock.Dispose);
        }
        if (OperatingSystem.IsWindows())
            return new(active =>
            {
                var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (!SystemAttentionService.WindowsTaskbarBadge.TrySetProgress(handle, active, out _, out var failure))
                    Trace.WriteLine("Taskbar progress: " + failure);
            });
        return new(_ => { });
    }

    internal void SetInProgress(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        // Native decoration must never break the Agent's start/finish path, even if the shell is unavailable.
        try { setNative(active); }
        catch (Exception ex) { Trace.WriteLine("Taskbar progress: " + ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetInProgress(false);
        _disposed = true;
        try { releaseNative?.Invoke(); }
        catch (Exception ex) { Trace.WriteLine("Taskbar progress cleanup: " + ex); }
    }
}
