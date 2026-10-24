using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>Applies one Git-produced hunk only when the displayed source versions still match. It deliberately
/// refuses untracked, renamed, binary and commit comparisons: those need a different patch contract.</summary>
public static class GitHunkOperations
{
    private const int MaxPatchBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static async Task<DiffHunkActionResult> ApplyAsync(string repositoryRoot, GitStatusEntry entry,
        GitFileComparison displayed, int hunkIndex, DiffHunkAction action, ProcessRunner runner,
        IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellationToken = default)
    {
        if (hunkIndex < 0) return new(false, "The hunk selection is invalid.");
        if (entry.Untracked || entry.OriginalPath is not null)
            return new(false, "Untracked and renamed paths do not support hunk operations.");
        if (entry.IndexState.Contains('U') || entry.WorktreeState.Contains('U')
            || entry.IndexState.Contains('T') || entry.WorktreeState.Contains('T'))
            return new(false, "Conflicted or type-changed paths do not support hunk operations.");
        var expectedRange = action switch
        {
            DiffHunkAction.Stage or DiffHunkAction.Revert => GitFileRange.WorkingTree,
            DiffHunkAction.Unstage => GitFileRange.Index,
            _ => GitFileRange.Commit,
        };
        if (displayed.Range != expectedRange || displayed.Detail is not null
            || !displayed.Old.IsText || !displayed.New.IsText)
            return new(false, "The comparison is not a supported source for this operation.");

        var root = Path.GetFullPath(repositoryRoot);
        var path = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || entry.RelativePath.Split('/').Any(part => part is "" or "." or ".." or ".git")
            || HasReparseComponent(root, path))
            return new(false, "The selected path is outside the permitted repository boundary or traverses a link.");

        var current = await GitRepository.ReadFileAsync(root, entry.RelativePath, expectedRange, runner,
            sensitiveValues: sensitiveValues, cancellation: cancellationToken).ConfigureAwait(false);
        if (!current.IsReadable || !SameVersion(current.Old, displayed.Old) || !SameVersion(current.New, displayed.New))
            return new(false, "The file or Git index changed since this comparison opened. Refresh before acting.");

        if (CommandShells.Resolve("git") is not { } git) return new(false, "git is not installed.");
        var args = new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "--no-color", "--binary", "--unified=0" };
        if (expectedRange == GitFileRange.Index) args.Add("--cached");
        args.Add("--");
        args.Add(entry.RelativePath);
        var capture = new RawStdoutCapture(MaxPatchBytes);
        var diff = await runner.RunAsync(git, args, root, timeout: Timeout, sensitiveValues: sensitiveValues,
            cancellation: cancellationToken, rawStdout: capture).ConfigureAwait(false);
        if (diff.ExitCode != 0 || capture.Truncated)
            return new(false, capture.Truncated ? "The Git patch exceeded the safe hunk limit."
                : "Could not read a current Git patch: " + (diff.Error.Length > 0 ? diff.Error : diff.Output));
        string patch;
        try { patch = new UTF8Encoding(false, true).GetString(capture.Bytes()); }
        catch (DecoderFallbackException) { return new(false, "The Git patch is not supported UTF-8 text."); }
        var selected = SelectHunk(patch, displayed, hunkIndex);
        if (selected is null) return new(false, "The selected display hunk does not map uniquely to a Git-native hunk.");

        var patchPath = Path.Combine(Path.GetTempPath(), "axmol-hub-hunk-" + Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            await File.WriteAllTextAsync(patchPath, selected, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var cached = action is DiffHunkAction.Stage or DiffHunkAction.Unstage;
            var reverse = action is DiffHunkAction.Unstage or DiffHunkAction.Revert;
            var applyArgs = new List<string> { "apply", "--unidiff-zero" };
            if (cached) applyArgs.Add("--cached");
            if (reverse) applyArgs.Add("--reverse");
            applyArgs.Add("--check");
            applyArgs.Add(patchPath);
            var check = await runner.RunAsync(git, applyArgs, root, timeout: Timeout,
                sensitiveValues: sensitiveValues, cancellation: cancellationToken).ConfigureAwait(false);
            if (check.ExitCode != 0) return new(false, "Git refused the hunk because its exact context no longer applies.");

            applyArgs.Remove("--check");
            var applied = await runner.RunAsync(git, applyArgs, root, timeout: Timeout,
                sensitiveValues: sensitiveValues, cancellation: cancellationToken).ConfigureAwait(false);
            if (applied.ExitCode != 0) return new(false, "Git failed to apply the hunk; refresh the comparison.", true);

            var after = await GitRepository.ReadFileAsync(root, entry.RelativePath, expectedRange, runner,
                sensitiveValues: sensitiveValues, cancellation: cancellationToken).ConfigureAwait(false);
            var expectedEqual = after.IsReadable && action switch
            {
                DiffHunkAction.Stage => SameContent(after.New, current.New) && !SameContent(after.Old, current.Old),
                DiffHunkAction.Unstage or DiffHunkAction.Revert
                    => SameContent(after.Old, current.Old) && !SameContent(after.New, current.New),
                _ => false,
            };
            return expectedEqual
                ? new(true, "The selected hunk was applied.", true)
                : new(false, "The operation returned, but Git versions changed during verification. Refresh and inspect both sides.", true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(false, "Hunk operation failed: " + ex.Message, true); }
        finally
        {
            try { File.Delete(patchPath); } catch (IOException) { }
        }
    }

    private static bool HasReparseComponent(string root, string full)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(current, full);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static bool SameVersion(GitFileVersion left, GitFileVersion right)
        => left.Exists == right.Exists
           && string.Equals(left.Oid, right.Oid, StringComparison.Ordinal)
           && string.Equals(left.Snapshot.Identity, right.Snapshot.Identity, StringComparison.Ordinal);

    private static bool SameContent(GitFileVersion left, GitFileVersion right)
        => left.Exists == right.Exists
           && string.Equals(left.Snapshot.Identity, right.Snapshot.Identity, StringComparison.Ordinal);

    private sealed record NativeHunk(int Start, int End, int OldStart, int OldCount, int NewStart, int NewCount);

    private static string? SelectHunk(string patch, GitFileComparison displayed, int selectedIndex)
    {
        var hunks = ParseHunks(patch);
        var oldText = displayed.Old.Snapshot.Text;
        var newText = displayed.New.Snapshot.Text;
        if (oldText is null || newText is null) return null;
        var rows = FileDiff.Rows(oldText, newText).Rows;
        var groups = new List<(int OldStart, int OldCount, int NewStart, int NewCount)>();
        var at = 0;
        while (at < rows.Count)
        {
            while (at < rows.Count && rows[at].Kind == DiffRowKind.Equal) at++;
            if (at == rows.Count) break;
            var old = new List<int>();
            var newer = new List<int>();
            while (at < rows.Count && rows[at].Kind != DiffRowKind.Equal)
            {
                if (rows[at].OriginalLine is int oldLine) old.Add(oldLine);
                if (rows[at].UpdatedLine is int newLine) newer.Add(newLine);
                at++;
            }
            groups.Add((old.Count > 0 ? old.Min() : 0, old.Count,
                newer.Count > 0 ? newer.Min() : 0, newer.Count));
        }
        if (selectedIndex >= groups.Count) return null;
        var selected = groups[selectedIndex];
        var candidates = hunks.Where(hunk => hunk.OldCount == selected.OldCount
            && hunk.NewCount == selected.NewCount
            && (selected.OldCount == 0 || hunk.OldStart == selected.OldStart)
            && (selected.NewCount == 0 || hunk.NewStart == selected.NewStart)).ToArray();
        if (candidates.Length != 1) return null;
        var target = candidates[0];
        return patch[..hunks[0].Start] + patch[target.Start..target.End];
    }

    private static IReadOnlyList<NativeHunk> ParseHunks(string patch)
    {
        var starts = new List<int>();
        for (var at = 0; (at = patch.IndexOf("@@ ", at, StringComparison.Ordinal)) >= 0; at += 3)
            if (at == 0 || patch[at - 1] == '\n') starts.Add(at);
        var result = new List<NativeHunk>();
        var regex = new Regex("^@@ -(?<old>\\d+)(?:,(?<oldCount>\\d+))? \\+(?<new>\\d+)(?:,(?<newCount>\\d+))? @@", RegexOptions.CultureInvariant);
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : patch.Length;
            var lineEnd = patch.IndexOf('\n', start);
            if (lineEnd < 0) lineEnd = patch.Length;
            var match = regex.Match(patch[start..lineEnd]);
            if (!match.Success) continue;
            result.Add(new NativeHunk(start, end,
                int.Parse(match.Groups["old"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["oldCount"].Success ? int.Parse(match.Groups["oldCount"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1,
                int.Parse(match.Groups["new"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["newCount"].Success ? int.Parse(match.Groups["newCount"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1));
        }
        return result;
    }
}
