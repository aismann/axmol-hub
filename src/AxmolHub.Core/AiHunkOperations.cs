namespace AxmolHub.Core;

public enum AiHunkAction { Accept, Reject }

/// <summary>Applies one hunk from an immutable AI revision without disturbing sibling hunks. Ambiguous or stale
/// context is refused rather than guessed.</summary>
public static class AiHunkOperations
{
    public static bool TryApply(string before, string after, string current, int hunkIndex,
        AiHunkAction action, out string updated, out string detail)
    {
        updated = current;
        detail = "";
        if (hunkIndex < 0) { detail = "The hunk selection is invalid."; return false; }
        before = TextEol.ToLf(before);
        after = TextEol.ToLf(after);
        current = TextEol.ToLf(current);
        if (before.EndsWith('\n') != after.EndsWith('\n'))
        {
            detail = "This revision changes the end-of-file newline and cannot be split into line hunks.";
            return false;
        }
        var script = FileDiff.Rows(before, after);
        if (!script.Exact) { detail = "The revision exceeds the safe hunk alignment budget."; return false; }
        var groups = new List<List<int>>();
        for (var row = 0; row < script.Rows.Count; row++)
        {
            if (script.Rows[row].Kind == DiffRowKind.Equal) continue;
            if (groups.Count == 0 || row != groups[^1][^1] + 1) groups.Add([]);
            groups[^1].Add(row);
        }
        if (hunkIndex >= groups.Count) { detail = "The selected hunk is no longer present."; return false; }

        var lines = FileDiff.SplitLines(current).ToList();
        var selectedRows = groups[hunkIndex];
        var oldLines = selectedRows.Where(row => script.Rows[row].Kind == DiffRowKind.Delete)
            .Select(row => script.Rows[row].Text).ToArray();
        var newLines = selectedRows.Where(row => script.Rows[row].Kind == DiffRowKind.Insert)
            .Select(row => script.Rows[row].Text).ToArray();
        var source = action == AiHunkAction.Accept ? oldLines : newLines;
        var target = action == AiHunkAction.Accept ? newLines : oldLines;
        var firstRow = selectedRows[0];
        var lastRow = selectedRows[^1];
        var beforeContext = script.Rows.Take(firstRow).Reverse()
            .TakeWhile(row => row.Kind == DiffRowKind.Equal).Take(3)
            .Select(row => row.Text).Reverse().ToArray();
        var afterContext = script.Rows.Skip(lastRow + 1).TakeWhile(row => row.Kind == DiffRowKind.Equal)
            .Take(3).Select(row => row.Text).ToArray();

        var candidates = FindCandidates(lines, source, beforeContext, afterContext);
        if (candidates.Count == 0)
        {
            var alreadyApplied = FindCandidates(lines, target, beforeContext, afterContext);
            if (alreadyApplied.Count == 1)
            {
                detail = "The selected hunk is already in the requested state.";
                return true;
            }
        }
        if (candidates.Count != 1)
        {
            detail = candidates.Count == 0
                ? "The selected hunk context no longer matches the file. Refresh before acting."
                : "The selected hunk context is ambiguous. No change was made.";
            return false;
        }
        var position = candidates[0];
        lines.RemoveRange(position, source.Length);
        lines.InsertRange(position, target);
        var hasFinalNewline = current.EndsWith('\n');
        updated = string.Join('\n', lines) + (hasFinalNewline && lines.Count > 0 ? "\n" : "");
        if (updated == current) { detail = "The selected hunk is already in the requested state."; return true; }
        detail = "The selected hunk was applied to the file buffer.";
        return true;
    }

    private static List<int> FindCandidates(IReadOnlyList<string> lines, IReadOnlyList<string> source,
        IReadOnlyList<string> beforeContext, IReadOnlyList<string> afterContext)
    {
        var candidates = new List<int>();
        for (var start = 0; start <= lines.Count - source.Count; start++)
            if (SequenceAt(lines, start, source) && SequenceBefore(lines, start, beforeContext)
                && SequenceAt(lines, start + source.Count, afterContext)) candidates.Add(start);
        return candidates;
    }

    private static bool SequenceAt(IReadOnlyList<string> actual, int start, IReadOnlyList<string> expected)
    {
        if (start < 0 || start + expected.Count > actual.Count) return false;
        for (var index = 0; index < expected.Count; index++)
            if (!string.Equals(actual[start + index], expected[index], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool SequenceBefore(IReadOnlyList<string> actual, int start, IReadOnlyList<string> expected)
    {
        if (start < expected.Count) return false;
        for (var index = 0; index < expected.Count; index++)
            if (!string.Equals(actual[start - expected.Count + index], expected[index], StringComparison.Ordinal)) return false;
        return true;
    }
}

