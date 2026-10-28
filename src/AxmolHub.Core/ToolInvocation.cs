namespace AxmolHub.Core;

/// <summary>Associates a disk journal with the tool call that caused it.</summary>
public static class ToolInvocation
{
    private static readonly AsyncLocal<string?> CurrentCall = new();
    public static string? CallId => CurrentCall.Value;

    public static IDisposable Enter(string callId)
    {
        var previous = CurrentCall.Value;
        CurrentCall.Value = callId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => CurrentCall.Value = previous;
    }
}
