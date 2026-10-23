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
    public static string? ParseRequest(string? text)
    {
        var action = ParseRequest(text, out _, out var validSyntax);
        return validSyntax ? action : null;
    }

    /// <summary>Parses data, never a shell command. A malformed explicit title stays local for validation.</summary>
    public static string? ParseRequest(string? text, out string? title, out bool validSyntax)
    {
        title = null;
        validSyntax = true;
        var request = text?.Trim() ?? "";
        var action = request.ToLowerInvariant() switch
        {
            "@hub commit" => "commit",
            "@hub push" => "push",
            "@hub commit+push" => "commit-push",
            _ => null,
        };
        if (action is not null) return action;
        const string command = "@hub commit";
        if (!request.StartsWith(command, StringComparison.OrdinalIgnoreCase)
            || request.Length <= command.Length || !char.IsWhiteSpace(request[command.Length])) return null;
        var argument = request[command.Length..].Trim();
        if (argument.StartsWith('\''))
        {
            validSyntax = argument.Length >= 2 && argument.EndsWith('\'')
                && !argument[1..^1].Contains('\'');
            if (validSyntax) title = argument[1..^1];
            return "commit";
        }
        try
        {
            title = System.Text.Json.JsonSerializer.Deserialize<string>(argument);
            validSyntax = title is not null;
        }
        catch (System.Text.Json.JsonException)
        {
            validSyntax = false;
        }
        return "commit";
    }

    /// <summary>Only a read-only Git snapshot decides visibility; reply wording and tool history do not.</summary>
    public static GitActions ActionsForRepository(GitRepositoryState state)
    {
        if (!state.IsReadable) return GitActions.None;
        var actions = state.Entries.Count > 0 ? GitActions.Commit | GitActions.Push | GitActions.CommitAndPush : GitActions.None;
        if (state.Ahead > 0) actions |= GitActions.Push;
        return actions;
    }
}
