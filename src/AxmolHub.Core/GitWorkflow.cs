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
        "@hub commit" => "commit",
        "@hub push" => "push",
        "@hub commit+push" => "commit-push",
        _ => null,
    };

    /// <summary>Only a read-only Git snapshot decides visibility; reply wording and tool history do not.</summary>
    public static GitActions ActionsForRepository(GitRepositoryState state)
    {
        if (!state.IsReadable) return GitActions.None;
        var actions = state.Entries.Count > 0 ? GitActions.Commit | GitActions.Push | GitActions.CommitAndPush : GitActions.None;
        if (state.Ahead > 0) actions |= GitActions.Push;
        return actions;
    }
}
