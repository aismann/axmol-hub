using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace AxmolHub.Core;
public static partial class SystemTrash
{
    internal static Func<string, CancellationToken, Task<TrashResult>>? MoveHandlerForCheck { get; set; }
}
