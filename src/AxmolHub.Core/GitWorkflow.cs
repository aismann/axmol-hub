using System.Text.Json;

namespace AxmolHub.Core;

[Flags]
public enum GitActions
{
    None = 0,
    Commit = 1,
    Push = 2,
    CommitAndPush = 4,
}

/// <summary>Pure presentation evidence, never a command runner or an approval decision.</summary>
public static class GitWorkflow
{
    public static string? ParseRequest(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "commit" => "commit",
        "push" => "push",
        "commit+push" => "commit-push",
        _ => null,
    };

    public sealed record Evidence(bool Committed, bool Pushed, bool ChangedAfterCommit);

    /// <summary>A user-requested, read-only repository refresh is authoritative over old reply metadata.</summary>
    public static GitActions ActionsForRepository(GitRepositoryState state)
    {
        if (!state.IsReadable || string.IsNullOrWhiteSpace(state.Branch)) return GitActions.None;
        var actions = state.Entries.Count > 0 ? GitActions.Commit | GitActions.CommitAndPush : GitActions.None;
        if (state.Ahead > 0) actions |= GitActions.Push;
        return actions;
    }

    public static GitActions ActionsFor(IReadOnlyList<ChatTurn> history, string workspaceRoot, string? summary)
    {
        if (history.LastOrDefault(turn => turn.Role == ChatRoles.User)?.IsGitOperationFallback == true)
            return GitActions.None;
        var evidence = EvidenceFor(history, workspaceRoot);
        var actions = evidence.Committed && !evidence.Pushed ? GitActions.Push : GitActions.None;
        if (summary is not null && (!evidence.Committed || evidence.ChangedAfterCommit))
            actions |= GitActions.Commit | GitActions.CommitAndPush;
        return actions;
    }

    public static Evidence EvidenceFor(IReadOnlyList<ChatTurn> history, string workspaceRoot)
    {
        var committed = false;
        var changedAfterCommit = false;
        var pushed = false;
        var calls = history.Where(turn => turn.Role == ChatRoles.Assistant && turn.ToolCallId is not null)
            .GroupBy(turn => turn.ToolCallId!).ToDictionary(group => group.Key, group => group.First());
        foreach (var result in history)
        {
            if (result.Role != ChatRoles.Tool || result.ToolCallId is null || result.ToolFailed
                || !calls.TryGetValue(result.ToolCallId, out var call)) continue;
            if (call.ToolName == "file_write")
            {
                if (committed && call.ChangeId is not null) changedAfterCommit = true;
                continue;
            }
            if (call.ToolName != "run_command" || result.ToolFacts?.ExitCode != 0) continue;
            var verb = CompletedGitVerb(call.ToolArguments, workspaceRoot);
            if (verb == "commit")
            {
                committed = true;
                changedAfterCommit = false;
                pushed = false;
            }
            else if (verb == "push") pushed = true;
            else if (committed && GitCommandGrades.CommandOf(call.ToolArguments) != GitGrade.GitRead)
            {
                // A shell/script we cannot prove read-only may have written after the commit. Do not assert
                // it committed or pushed anything; require a fresh explicit title before offering a commit.
                changedAfterCommit = true;
            }
        }
        return new Evidence(committed, pushed, changedAfterCommit);
    }

    private static string? CompletedGitVerb(string? arguments, string workspaceRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments ?? "{}");
            var json = document.RootElement;
            var command = json.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
            if (command is null || command.Length > ShellCommandLine.MaxCommandCharacters) return null;
            if (json.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } directory)
            {
                var target = Path.GetFullPath(directory, Path.GetFullPath(workspaceRoot));
                if (!string.Equals(WorkspacePaths.CanonicalRoot(target), WorkspacePaths.CanonicalRoot(workspaceRoot),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
            }
            var segments = ShellCommandLine.SegmentsOf(command);
            if (segments.Count != 1) return null;
            var tokens = segments[0];
            if (tokens.Count < 2 || ShellCommandLine.ExecutableName(tokens[0].Text) != "git") return null;
            var index = 1;
            while (index < tokens.Count)
            {
                var token = tokens[index].Text;
                if (token is "--no-pager" or "--no-optional-locks") { index++; continue; }
                if (token == "-c" && index + 1 < tokens.Count && tokens[index + 1].Text == "core.quotepath=false")
                { index += 2; continue; }
                break;
            }
            if (index >= tokens.Count || tokens[index].Text is not ("commit" or "push")) return null;
            if (tokens.Skip(index + 1).Any(token => token.Text is "--dry-run" or "-n" or "--help" or "-h")) return null;
            // An explicit push of another ref cannot prove this branch was pushed. Unknown targets stay unknown.
            if (tokens[index].Text == "push" && tokens.Count != index + 1) return null;
            return tokens[index].Text;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
