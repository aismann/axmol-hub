using System.Text.Json;
using AxmolHub.Core;

internal static class GitWorkflowEvidenceChecks
{
    internal static void Run()
    {
        var root = Path.GetFullPath(".");
        const string summary = "fix: update button states";
        var both = GitActions.Commit | GitActions.CommitAndPush;
        var commit = Call("commit", "git commit -m 'fix: update button states'");
        var push = Call("push", "git push");
        var write = ChatTurn.FunctionCall("write", "file_write", "{}") with { ChangeId = "changed-file" };
        var fixtures = new (string Name, ChatTurn[] History, string? Summary, GitActions Expected)[]
        {
            ("ordinary reply", [ChatTurn.Assistant("Already committed.")], null, GitActions.None),
            ("summary only", [ChatTurn.Assistant("Done")], summary, both),
            ("successful commit", [commit, Done("commit")], summary, GitActions.Push),
            ("successful commit without title", [commit, Done("commit")], null, GitActions.Push),
            ("commit and push", [commit, Done("commit"), push, Done("push")], summary, GitActions.None),
            ("failed push", [commit, Done("commit"), push, Done("push", 1)], summary, GitActions.Push),
            ("later real edit", [commit, Done("commit"), write, ChatTurn.FunctionResult("write", "Edited", false)], summary, both | GitActions.Push),
            ("later edit without title", [commit, Done("commit"), write, ChatTurn.FunctionResult("write", "Edited", false)], null, GitActions.Push),
            ("unproven command", [Call("unknown", "git commit -m x && echo done"), Done("unknown")], null, GitActions.None),
            ("dry run", [Call("dry", "git commit --dry-run"), Done("dry")], null, GitActions.None),
            ("different directory", [Call("other", "git -C elsewhere commit -m x"), Done("other")], null, GitActions.None),
            ("not an exit result", [commit, ChatTurn.FunctionResult("commit", "exit 0", false)], null, GitActions.None),
            ("failed commit", [commit, Done("commit", 1)], summary, both),
            ("push another ref", [commit, Done("commit"), Call("other-push", "git push origin other"), Done("other-push")], null, GitActions.Push),
            ("fallback terminal", [ChatTurn.User("short handoff") with { IsGitOperationFallback = true }, commit, Done("commit")], summary, GitActions.None),
        };
        foreach (var fixture in fixtures)
        {
            var actual = GitWorkflow.ActionsFor(fixture.History, root, fixture.Summary);
            if (actual != fixture.Expected)
                throw new Exception(fixture.Name + ": expected " + fixture.Expected + ", actual " + actual);
        }
        Console.WriteLine("PASS: Git action visibility follows successful tool outcomes, not reply claims; committed and pushed work is not offered again.");
        foreach (var (text, expected) in new (string, string?)[]
                 { ("commit", "commit"), (" PUSH ", "push"), ("commit+push", "commit-push"), ("do not commit", null),
                     ("explain git commit", null), ("commit && push", null), ("git commit -m x", null) })
            if (GitWorkflow.ParseRequest(text) != expected) throw new Exception("Ambiguous Git request recognized: " + text);
        Console.WriteLine("PASS: local Git text routing accepts only standalone explicit actions.");

        ChatTurn Call(string id, string command) => ChatTurn.FunctionCall(id, "run_command",
            JsonSerializer.Serialize(new { command, cwd = root }));
        static ChatTurn Done(string id, int exit = 0) => ChatTurn.FunctionResult(id, "result", exit != 0)
            with { ToolFacts = new ToolRunFacts(exit, TimeSpan.Zero) };
    }
}
