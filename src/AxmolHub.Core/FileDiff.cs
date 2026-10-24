using System.Text;

namespace AxmolHub.Core;

/// <summary>
/// Bounds on a preview that is persisted into the session file and painted into a small card, so an uncapped
/// diff is both a storage bug and an unreadable approval prompt.
/// </summary>
public sealed record DiffLimits(
    int ContextLines = 3,
    int MaxLinesPerSide = 4000,
    int MaxEditScript = 2000,
    int MaxOutputLines = 60,
    int MaxOutputCharacters = 8000,
    int MaxTotalCharacters = 4 * 1024 * 1024)
{
    /// <summary>The same caps with the preview's own output budget removed: every line of the file becomes
    /// context and nothing is cut off the end. What stays is the budget that bounds the *comparison*
    /// (<see cref="MaxLinesPerSide"/>, <see cref="MaxEditScript"/>), so a file too large to align still says so
    /// instead of hanging or printing a wall of red. This is what a row's unfold button asks for: the reader said
    /// "show me the file", and answering with the same sixty-line preview would be a button that does nothing.</summary>
    public DiffLimits Expanded() => this with
    {
        // Not int.MaxValue: the hunk builder adds the context to a line index (`i + contextLines`), and a value
        // that large turns that sum negative for every edit after the first. A megabyte of lines is already more
        // than the comparison budget will ever emit, so this is "all of the file" without the overflow.
        ContextLines = 1 << 20,
        MaxOutputLines = int.MaxValue,
        MaxOutputCharacters = int.MaxValue,
    };
}

/// <summary>Which side of the edit a row's line belongs to. A deletion occupies no line on the right and an
/// insertion none on the left, which is what the alignment's gap is made of.</summary>
public enum DiffRowKind { Equal, Delete, Insert }

/// <summary>One aligned row of a full-file comparison: the line on each side, and null for the side the line is
/// not on. The numbers are real line numbers in that side's own document, which is what a gutter shows and what a
/// hunk header counts from — a row counter would drift the moment a deletion shares a row.</summary>
public readonly record struct DiffRow(DiffRowKind Kind, int? OriginalLine, int? UpdatedLine, string Text);

/// <summary>The full aligned comparison, plus whether the edit script inside it is exact. Past a budget the rows
/// are the honest degraded answer — every line on each side, marked changed — rather than an alignment that was
/// not computed; <see cref="Exact"/> is what tells the two apart, so a view can say "too large to align" instead of
/// presenting a wall of red as a finding.</summary>
public sealed record DiffScript(IReadOnlyList<DiffRow> Rows, bool Exact);

/// <summary>
/// Unified diff for the write tool's approval preview. Hand-rolled because Core takes no dependencies: Myers
/// O(ND) over lines, with a common prefix/suffix trim first so a real edit inside a large file costs D = the
/// size of the edit, not the size of the file. When either side is too large to diff honestly, the output says
/// so instead of pretending to show the change.
/// </summary>
public static class FileDiff
{
    public static string Unified(string originalText, string updatedText, string relativePath, DiffLimits? limits = null)
    {
        var original = SplitLines(originalText ?? "");
        var updated = SplitLines(updatedText ?? "");
        if (original.SequenceEqual(updated, StringComparer.Ordinal)) return "";

        var capped = limits ?? new DiffLimits();
        var edits = Diff(original, updated, capped, out _);

        // Forward slashes in the header whatever the host: a unified diff is a text format with its own
        // convention, and `--- a/src\main.cpp` would not apply with patch(1) or read as a path anywhere else.
        var shown = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var output = new StringBuilder();
        output.Append(original.Length == 0 ? "--- /dev/null" : $"--- a/{shown}").Append('\n');
        output.Append(updated.Length == 0 ? "+++ /dev/null" : $"+++ b/{shown}").Append('\n');

        var hunks = Hunks(edits, capped.ContextLines);
        var totalChanges = hunks.Sum(hunk => hunk.Lines.Count(line => line[0] is '-' or '+'));
        var emittedLines = 2;
        var emittedChanges = 0;
        foreach (var hunk in hunks)
        {
            if (emittedLines >= capped.MaxOutputLines || output.Length >= capped.MaxOutputCharacters) break;
            output.Append($"@@ -{hunk.OriginalStart},{hunk.OriginalCount} +{hunk.UpdatedStart},{hunk.UpdatedCount} @@").Append('\n');
            emittedLines++;
            foreach (var line in hunk.Lines)
            {
                if (emittedLines >= capped.MaxOutputLines || output.Length >= capped.MaxOutputCharacters) break;
                output.Append(line).Append('\n');
                emittedLines++;
                if (line[0] is '-' or '+') emittedChanges++;
            }
        }

        // The count is of changed lines, not of context: what a reader needs to know is how much of the edit
        // they are approving without seeing.
        var omitted = totalChanges - emittedChanges;
        if (omitted > 0) output.Append($"… (+{omitted} more changed lines)");
        return output.ToString().TrimEnd('\n');
    }

    /// <summary>The whole file aligned line by line, for a side-by-side view rather than a card preview. The same
    /// budget decides it as decides the counts, so a view and a tally cannot disagree; what is new here is that the
    /// caller is told whether the alignment is exact instead of having to infer it from the output.</summary>
    public static DiffScript Rows(string? originalText, string? updatedText, DiffLimits? limits = null)
    {
        var original = SplitLines(originalText ?? "");
        var updated = SplitLines(updatedText ?? "");
        var capped = limits ?? new DiffLimits();
        var exact = true;
        List<Edit> edits;
        if (original.SequenceEqual(updated, StringComparer.Ordinal))
            edits = original.Select(line => new Edit(Kind.Equal, line)).ToList();
        else if ((long)(originalText?.Length ?? 0) + (updatedText?.Length ?? 0) > capped.MaxTotalCharacters)
        {
            // Reading the bytes was already capped; this cap is about the cost of comparing what was read.
            edits = ReplaceAll(original, updated);
            exact = false;
        }
        else edits = Diff(original, updated, capped, out exact);

        var rows = new List<DiffRow>(edits.Count);
        var originalLine = 1;
        var updatedLine = 1;
        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case Kind.Equal:
                    rows.Add(new DiffRow(DiffRowKind.Equal, originalLine++, updatedLine++, edit.Line));
                    break;
                case Kind.Delete:
                    rows.Add(new DiffRow(DiffRowKind.Delete, originalLine++, null, edit.Line));
                    break;
                default:
                    rows.Add(new DiffRow(DiffRowKind.Insert, null, updatedLine++, edit.Line));
                    break;
            }
        }
        return new DiffScript(rows, exact);
    }

    /// <summary>How many lines an edit adds and removes, counted off the edit script rather than off the rendered
    /// diff. <see cref="Unified"/> is capped by <see cref="DiffLimits.MaxOutputLines"/> because an approval card
    /// has to fit on screen, and a tally that inherited that cap would report a 200-line rewrite as 60 — which is
    /// what the counts derived from a frozen preview do today.
    ///
    /// <para>The bounds on <i>which</i> script is computed are shared with the renderer on purpose: past
    /// <see cref="DiffLimits.MaxLinesPerSide"/> or beyond <see cref="DiffLimits.MaxEditScript"/> both sides answer
    /// "everything changed", so a count and a rendering of the same pair cannot disagree about a file too large to
    /// diff honestly. Only the output caps differ, and those are a fact about the card, not about the edit.</para></summary>
    public static (int Added, int Removed) CountChangedLines(string? beforeText, string? afterText)
    {
        var original = SplitLines(beforeText ?? "");
        var updated = SplitLines(afterText ?? "");
        if (original.SequenceEqual(updated, StringComparer.Ordinal)) return (0, 0);
        var edits = Diff(original, updated, new DiffLimits(), out _);
        var added = 0;
        var removed = 0;
        foreach (var edit in edits)
        {
            if (edit.Kind == Kind.Insert) added++;
            else if (edit.Kind == Kind.Delete) removed++;
        }
        return (added, removed);
    }

    /// <summary>Split into lines for comparison: newline style is normalized away so a CRLF file diffed against
    /// an LF edit does not show every line as changed, and no stray \r reaches the card.</summary>
    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = TextEol.ToLf(text).Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private enum Kind { Equal, Delete, Insert }

    private readonly record struct Edit(Kind Kind, string Line);

    private sealed record Hunk(int OriginalStart, int OriginalCount, int UpdatedStart, int UpdatedCount, List<string> Lines);

    private static List<Edit> ReplaceAll(string[] original, string[] updated)
        => [.. original.Select(line => new Edit(Kind.Delete, line)), .. updated.Select(line => new Edit(Kind.Insert, line))];

    private static List<Edit> Diff(string[] original, string[] updated, DiffLimits limits, out bool exact)
    {
        exact = true;
        // The common prefix and suffix are not part of the edit script; trimming them is what keeps D small.
        var start = 0;
        while (start < original.Length && start < updated.Length
               && string.Equals(original[start], updated[start], StringComparison.Ordinal)) start++;
        var endOriginal = original.Length;
        var endUpdated = updated.Length;
        while (endOriginal > start && endUpdated > start
               && string.Equals(original[endOriginal - 1], updated[endUpdated - 1], StringComparison.Ordinal))
        {
            endOriginal--;
            endUpdated--;
        }

        var middleOriginal = original[start..endOriginal];
        var middleUpdated = updated[start..endUpdated];
        // The size bound applies to the trimmed middle: a large file with a small edit is exactly the common
        // case, and refusing to diff it would show "whole file rewritten" for a one-line change.
        List<Edit> middle;
        if (middleOriginal.Length > limits.MaxLinesPerSide || middleUpdated.Length > limits.MaxLinesPerSide)
        {
            middle = ReplaceAll(middleOriginal, middleUpdated);
            exact = false;
        }
        else if (Myers(middleOriginal, middleUpdated, limits.MaxEditScript) is { } scripted) middle = scripted;
        else
        {
            middle = ReplaceAll(middleOriginal, middleUpdated);
            exact = false;
        }
        return
        [
            .. original[..start].Select(line => new Edit(Kind.Equal, line)),
            .. middle,
            .. original[endOriginal..].Select(line => new Edit(Kind.Equal, line)),
        ];
    }

    /// <summary>Greedy Myers with the V array snapshotted per D so the edit script can be backtracked. Returns
    /// null when the edit script would exceed <paramref name="maxEditScript"/>, which the caller treats as
    /// "too different to be worth an exact script".</summary>
    private static List<Edit>? Myers(string[] a, string[] b, int maxEditScript)
    {
        var n = a.Length;
        var m = b.Length;
        if (n + m > maxEditScript) return null;
        if (n + m == 0) return [];

        var offset = n + m;
        var v = new int[2 * offset + 1];
        var trace = new List<int[]>(offset + 1);
        for (var d = 0; d <= offset; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                else x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal)) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, a, b, offset);
            }
        }
        return null;
    }

    private static List<Edit> Backtrack(List<int[]> trace, string[] a, string[] b, int offset)
    {
        var edits = new List<Edit>();
        var x = a.Length;
        var y = b.Length;
        for (var d = trace.Count - 1; d >= 0 && (x > 0 || y > 0); d--)
        {
            var v = trace[d];
            var k = x - y;
            int previousK;
            if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) previousK = k + 1;
            else previousK = k - 1;
            var previousX = v[offset + previousK];
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY)
            {
                edits.Add(new Edit(Kind.Equal, a[x - 1]));
                x--;
                y--;
            }
            if (d > 0) edits.Add(x == previousX ? new Edit(Kind.Insert, b[previousY]) : new Edit(Kind.Delete, a[previousX]));
            x = previousX;
            y = previousY;
        }
        edits.Reverse();
        return edits;
    }

    private static List<Hunk> Hunks(List<Edit> edits, int contextLines)
    {
        var originalBefore = new int[edits.Count + 1];
        var updatedBefore = new int[edits.Count + 1];
        for (var i = 0; i < edits.Count; i++)
        {
            originalBefore[i + 1] = originalBefore[i] + (edits[i].Kind is Kind.Equal or Kind.Delete ? 1 : 0);
            updatedBefore[i + 1] = updatedBefore[i] + (edits[i].Kind is Kind.Equal or Kind.Insert ? 1 : 0);
        }

        var ranges = new List<(int Start, int End)>();
        for (var i = 0; i < edits.Count; i++)
        {
            if (edits[i].Kind == Kind.Equal) continue;
            var start = Math.Max(0, i - contextLines);
            var end = Math.Min(edits.Count - 1, i + contextLines);
            if (ranges.Count > 0 && start <= ranges[^1].End + 1) ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            else ranges.Add((start, end));
        }

        var hunks = new List<Hunk>();
        foreach (var (start, end) in ranges)
        {
            var lines = new List<string>();
            for (var i = start; i <= end; i++)
                lines.Add(edits[i].Kind switch { Kind.Delete => "-" + edits[i].Line, Kind.Insert => "+" + edits[i].Line, _ => " " + edits[i].Line });
            var originalCount = originalBefore[end + 1] - originalBefore[start];
            var updatedCount = updatedBefore[end + 1] - updatedBefore[start];
            // An empty side starts at 0, the way a created file reads as "@@ -0,0 +1,2 @@".
            hunks.Add(new Hunk(
                originalCount == 0 ? originalBefore[start] : originalBefore[start] + 1,
                originalCount,
                updatedCount == 0 ? updatedBefore[start] : updatedBefore[start] + 1,
                updatedCount,
                lines));
        }
        return hunks;
    }
}
