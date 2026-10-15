namespace AxmolHub.Core;

public sealed class HubLog
{
    private readonly object gate = new();
    public string Folder { get; }
    public string FilePath { get; }
    public event Action<string>? Written;
    public HubLog(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, $"hub-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    }
    public void Write(string message)
    {
        // Folded at the sink as well as at the source: this is a Hub file, and a log whose newline depends on
        // which host wrote it cannot be read side by side with one written on another.
        var line = $"{DateTimeOffset.Now:O} {TextEol.ToLf(message)}";
        lock (gate) File.AppendAllText(FilePath, line + "\n");
        Written?.Invoke(line);
    }
}
