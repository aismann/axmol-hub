namespace AxmolHub.Core;

/// <summary>What one file operation in a patch does to its file.</summary>
public enum PatchFileOperation
{
    /// <summary>The file is created; every line of the section is an addition.</summary>
    Add,

    /// <summary>Hunks are applied to the file that is already there.</summary>
    Update,

    /// <summary>The file is removed, and the patch carries no lines for it.</summary>
    Delete,
}

/// <summary>One file a patch names, with the lines it moves in it.</summary>
/// <remarks>The lines keep the prefix the patch wrote them with — a space for context, <c>-</c> for a removal,
/// <c>+</c> for an addition, <c>@</c> for a hunk header — because that is the shape a unified-diff renderer already
/// reads. Nothing here reconstructs line numbers: the envelope's hunks anchor on context, not on positions, so any
/// number printed beside these lines would be invented.</remarks>
public sealed class PatchSection
{
    internal PatchSection(PatchFileOperation operation, string path, string? movedTo, IReadOnlyList<string> lines)
    {
        Operation = operation;
        Path = path;
        MovedTo = movedTo;
        Lines = lines;
        foreach (var line in lines)
        {
            if (line.StartsWith('+')) Added++;
            else if (line.StartsWith('-')) Removed++;
        }
    }

    public PatchFileOperation Operation { get; }

    public string Path { get; }

    /// <summary>Where an update moves the file, when it says so; null otherwise.</summary>
    public string? MovedTo { get; }

    /// <summary>The section's own body as unified-diff lines, prefixes kept.</summary>
    public IReadOnlyList<string> Lines { get; }

    public int Added { get; }

    public int Removed { get; }
}

/// <summary>One patch, split into the files it touches.</summary>
/// <remarks>This is a reader, not an applier. The model runs <c>apply_patch</c> (or a script) as a command and the
/// command is what changes the disk; Hub parses the same text only to say what happened — which files, and how many
/// lines each way — so an edit made through a shell is not the one kind of edit the transcript cannot name.</remarks>
public sealed class PatchSummary
{
    internal PatchSummary(IReadOnlyList<PatchSection> sections)
    {
        Sections = sections;
        foreach (var section in sections)
        {
            Added += section.Added;
            Removed += section.Removed;
        }
    }

    public IReadOnlyList<PatchSection> Sections { get; }

    public int Added { get; }

    public int Removed { get; }
}

/// <summary>Reads the patch envelope a coding model writes when it edits files through a command.</summary>
/// <remarks>The shape is OpenAI Codex's <c>apply_patch</c> grammar, and it is worth parsing rather than guessing at:
/// <code>
/// *** Begin Patch
/// *** Update File: src/Foo.cs
/// @@ some context
///  unchanged line
/// -removed line
/// +added line
/// *** Add File: docs/New.md
/// +first line
/// *** Delete File: old.txt
/// *** End Patch
/// </code>
/// <para>Markers are matched by prefix and unknown ones are skipped, so a later version of the format degrades to
/// "fewer details" rather than to a misread. The command text around the envelope is ignored on purpose: the same
/// patch arrives as a heredoc, inside a quoted argument, or wrapped in <c>cd sub &amp;&amp; …</c>, and the envelope
/// is the one part all three spell the same way. A patch this cannot read — a Python string literal with escaped
/// newlines, say — reports nothing, which is exactly what the transcript showed before this existed.</para></remarks>
public static class PatchText
{
    public const string BeginMarker = "*** Begin Patch";

    private const string EndMarker = "*** End Patch";
    private const string AddFileMarker = "*** Add File:";
    private const string UpdateFileMarker = "*** Update File:";
    private const string DeleteFileMarker = "*** Delete File:";
    private const string MoveMarker = "*** Move to:";

    /// <summary>Whether this text looks like it carries a patch at all. The cheap question, asked before the parse:
    /// every command in a session goes through here and almost none of them is an edit.</summary>
    public static bool LooksLike(string? text)
        => text is not null && text.Contains(BeginMarker, StringComparison.Ordinal);

    /// <summary>The patch one tool call carries, or null when the call is not a patch at all.</summary>
    public static PatchSummary? ForCall(ChatTurn? call)
        => call is not null
           && string.Equals(call.ToolName, WorkspaceTools.RunCommandTool, StringComparison.Ordinal)
            ? Parse(CommandOf(call.ToolArguments))
            : null;

    /// <summary>The patch inside a command line, or null when there is none to read.</summary>
    public static PatchSummary? Parse(string? command)
    {
        if (!LooksLike(command)) return null;

        var begin = command!.IndexOf(BeginMarker, StringComparison.Ordinal) + BeginMarker.Length;
        // Only the lines after the envelope's own marker are read: what precedes it is the command's own words and
        // what follows the end marker is a heredoc terminator or a closing quote.
        var lines = TextEol.ToLf(command[begin..]).Split('\n');

        var sections = new List<PatchSection>();
        var body = new List<string>();
        var operation = PatchFileOperation.Update;
        var path = "";
        string? movedTo = null;
        var open = false;

        void Close()
        {
            if (!open) return;
            sections.Add(new PatchSection(operation, path, movedTo, body.ToArray()));
            body.Clear();
            movedTo = null;
            open = false;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith(EndMarker, StringComparison.Ordinal)) break;
            if (line.StartsWith("*** ", StringComparison.Ordinal))
            {
                if (Field(line, UpdateFileMarker) is { } updated)
                {
                    Close();
                    operation = PatchFileOperation.Update;
                    path = updated;
                    open = true;
                }
                else if (Field(line, AddFileMarker) is { } added)
                {
                    Close();
                    operation = PatchFileOperation.Add;
                    path = added;
                    open = true;
                }
                else if (Field(line, DeleteFileMarker) is { } deleted)
                {
                    Close();
                    operation = PatchFileOperation.Delete;
                    path = deleted;
                    open = true;
                }
                else if (open && Field(line, MoveMarker) is { } target)
                {
                    movedTo = target;
                }
                // Anything else marked this way — `*** End of File`, `*** Environment ID:` — says nothing a reader
                // needs, and a marker this build has never seen is skipped rather than taken for content.
                continue;
            }

            if (!open) continue;
            // A context line the writer left empty still reads as context; a prefix this format does not use is
            // dropped, because a transcript that invents a line is worse than one that leaves it out.
            if (line.Length == 0) body.Add(" ");
            else if (line[0] is ' ' or '-' or '+' or '@') body.Add(line);
        }

        Close();
        return sections.Count > 0 ? new PatchSummary(sections) : null;
    }

    /// <summary>The value a marker line carries, or null when the line is a different marker. A marker with no value
    /// is not a marker — a section without a path would be drawn with nothing to name it.</summary>
    private static string? Field(string line, string marker)
    {
        if (!line.StartsWith(marker, StringComparison.Ordinal)) return null;
        var value = line[marker.Length..].Trim();
        return value.Length > 0 ? value : null;
    }

    /// <summary>The command a <c>run_command</c> call was made with. <c>cmd</c> is read beside <c>command</c> for the
    /// same reason the panel reads both: a session written before the field settled still opens.</summary>
    private static string? CommandOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var key in new[] { "command", "cmd" })
            {
                if (document.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        return null;
    }
}
