using AxmolHub.Core;

internal static class GitWorkflowEvidenceChecks
{
    internal static void Run()
    {
        var all = GitActions.Commit | GitActions.Push | GitActions.CommitAndPush;
        var clean = new GitRepositoryState(GitReadOutcome.Ok, Path.GetFullPath("."), "main", 0, 0,
            [], new Dictionary<string, string>(), false, null);
        var fixtures = new (string Name, GitRepositoryState State, GitActions Expected)[]
        {
            ("clean", clean, GitActions.None),
            ("pending push", clean with { Ahead = 1 }, GitActions.Push),
            ("unstaged only", clean with { Entries = [new("file.txt", " ", "M", false)] }, all),
            ("staged only", clean with { Entries = [new("file.txt", "M", " ", false)] }, all),
            ("untracked only", clean with { Entries = [new("file.txt", "?", "?", true)] }, all),
            ("dirty with ahead", clean with { Ahead = 1, Entries = [new("file.txt", " ", "M", false)] }, all),
            ("branch safety is checked at execution", clean with { Branch = null, Entries = [new("file.txt", " ", "M", false)] }, all),
            ("unreadable", GitRepositoryState.Stopped(GitReadOutcome.Failed), GitActions.None),
        };
        foreach (var fixture in fixtures)
        {
            var actual = GitWorkflow.ActionsForRepository(fixture.State);
            if (actual != fixture.Expected)
                throw new Exception(fixture.Name + ": expected " + fixture.Expected + ", actual " + actual);
        }
        Console.WriteLine("PASS: Git action visibility depends only on repository changes and pending commits, with no reply-summary or tool-history input.");
        foreach (var (text, expected) in new (string, string?)[]
                 { ("@hub commit", "commit"), (" @HUB PUSH ", "push"), ("@hub commit+push", "commit-push"),
                     ("commit", null), ("push", null), ("commit+push", null), ("提交", null), ("推送", null), ("提交并推送", null),
                     ("do not commit", null), ("explain git commit", null), ("commit && push", null), ("git commit -m x", null),
                     ("@hub commit && push", null), ("explain @hub commit", null), ("@hub commit -m x", null) })
            if (GitWorkflow.ParseRequest(text) != expected) throw new Exception("Ambiguous Git request recognized: " + text);
        Console.WriteLine("PASS: local Git text routing accepts only exact @hub actions; ordinary English and Chinese requests stay on the Agent path.");
    }
}
