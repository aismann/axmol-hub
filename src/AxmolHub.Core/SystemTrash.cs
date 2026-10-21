using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace AxmolHub.Core;

public enum TrashResultKind
{
    Moved,
    Cancelled,
    Unsupported,
    Failed,
}

public sealed record TrashResult(TrashResultKind Kind, string? Error = null);

/// <summary>Moves one filesystem item to the host's trash without a permanent-delete fallback.</summary>
public static class SystemTrash
{
    internal static Func<string, CancellationToken, Task<TrashResult>>? MoveHandlerForCheck { get; set; }

    public static async Task<TrashResult> MoveAsync(string path, CancellationToken cancellation = default)
    {
        path = Path.GetFullPath(path);
        if (MoveHandlerForCheck is { } handler) return await handler(path, cancellation).ConfigureAwait(false);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return new(TrashResultKind.Moved);
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacTrash.Move(path);
            }

            if (OperatingSystem.IsLinux())
            {
                var start = new ProcessStartInfo("gio") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("trash");
                start.ArgumentList.Add("--");
                start.ArgumentList.Add(path);
                using var process = Process.Start(start);
                if (process is null) return new(TrashResultKind.Unsupported, "Could not start gio.");
                try
                {
                    await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    return new(TrashResultKind.Cancelled, ex.Message);
                }
                return process.ExitCode == 0
                    ? new(TrashResultKind.Moved)
                    : new(TrashResultKind.Failed, $"gio trash exited with code {process.ExitCode}.");
            }

            return new(TrashResultKind.Unsupported, "The system trash is not supported on this platform.");
        }
        catch (OperationCanceledException ex)
        {
            return new(TrashResultKind.Cancelled, ex.Message);
        }
        catch (PlatformNotSupportedException ex)
        {
            return new(TrashResultKind.Unsupported, ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(TrashResultKind.Unsupported, ex.Message);
        }
        catch (Exception ex)
        {
            return new(TrashResultKind.Failed, ex.Message);
        }
    }

    private static class MacTrash
    {
        private const string LibObjc = "/usr/lib/libobjc.A.dylib";

        [DllImport(LibObjc)] private static extern nint objc_getClass(string name);
        [DllImport(LibObjc)] private static extern nint sel_registerName(string name);
        [DllImport(LibObjc, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
        [DllImport(LibObjc, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint argument);
        [DllImport(LibObjc, EntryPoint = "objc_msgSend")] private static extern byte Send(nint receiver, nint selector, nint first, ref nint second, ref nint third);

        public static TrashResult Move(string path)
        {
            var pool = Send(objc_getClass("NSAutoreleasePool"), Sel("new"));
            try
            {
                var text = String(path);
                var url = Send(objc_getClass("NSURL"), Sel("fileURLWithPath:"), text);
                var manager = Send(objc_getClass("NSFileManager"), Sel("defaultManager"));
                nint destination = 0;
                nint error = 0;
                var moved = Send(manager, Sel("trashItemAtURL:resultingItemURL:error:"), url, ref destination, ref error);
                if (moved != 0) return new(TrashResultKind.Moved);

                var message = error == 0 ? "NSFileManager could not move this item to the Trash." :
                    Utf8(Send(error, Sel("localizedDescription")));
                return new(TrashResultKind.Unsupported, message);
            }
            finally
            {
                Send(pool, Sel("drain"));
            }
        }

        private static nint Sel(string name) => sel_registerName(name);

        private static nint String(string value)
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                var allocated = Send(Send(objc_getClass("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), utf8);
                return Send(allocated, Sel("autorelease"));
            }
            finally { Marshal.FreeCoTaskMem(utf8); }
        }

        private static string Utf8(nint value)
        {
            var bytes = Send(value, Sel("UTF8String"));
            return bytes == 0 ? "NSFileManager could not move this item to the Trash." : Marshal.PtrToStringUTF8(bytes) ?? "";
        }
    }
}
