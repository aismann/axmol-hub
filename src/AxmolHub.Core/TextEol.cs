namespace AxmolHub.Core;

/// <summary>The repository-wide line-ending policy: Hub writes LF, on every host, into every file it owns or
/// edits. The model emits <c>\n</c> and <c>read_file</c> shows <c>\n</c>, so writing LF is also what makes a
/// copied anchor match on the first try instead of costing a conversion.</summary>
public static class TextEol
{
    /// <summary>Collapses CRLF and lone CR to LF. Lone CR is folded too, because a classic-Mac file or a
    /// truncated CRLF would otherwise keep a stray \r through a write that claims to be LF. Adds no trailing
    /// newline and removes none: a file that ended without one still ends without one.</summary>
    public static string ToLf(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    /// <summary>Writes a report as lines joined by LF. <c>File.WriteAllLines</c> joins them with the host's
    /// newline, which is the CRLF these files used to carry; the trailing newline it adds is kept.</summary>
    public static void WriteLines(string path, IEnumerable<string> lines)
        => File.WriteAllText(path, ToLf(string.Join('\n', lines) + "\n"));
}
