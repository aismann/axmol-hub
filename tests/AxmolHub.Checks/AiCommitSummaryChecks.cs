using AxmolHub.Core;

internal static class AiCommitSummaryChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        var valid = "Done.\n\nCommit summary: feat: add offline commit shortcut";
        if (CommitSummary.Parse(valid) != "feat: add offline commit shortcut")
            throw new Exception("The explicit commit-summary marker did not yield its valid title.");
        if (CommitSummary.Parse("Commit summary: chore: update dependencies") is not null)
            throw new Exception("A title with an unconfigured prefix was accepted.");
        if (CommitSummary.Parse("Commit summary: feat: first\nCommit summary: fix: second") is not null)
            throw new Exception("Ambiguous multiple summary markers were accepted.");
        if (CommitSummary.Parse("Ordinary prose mentions feat: something") is not null)
            throw new Exception("Ordinary prose was guessed as a commit title.");
        var configured = CommitSummary.NormalizePrefixes(["feature", "repair:", " "]);
        if (CommitSummary.Parse("Commit summary: feature: save staged files", configured)
            != "feature: save staged files"
            || CommitSummary.IsValid("feature: " + new string('x', 121), configured))
            throw new Exception("Custom prefixes or the title length bound were not enforced.");
        var oldPreferences = System.Text.Json.JsonSerializer.Deserialize<HubPreferences>("{}");
        if (oldPreferences is null || CommitSummary.NormalizePrefixes(oldPreferences.CommitSummaryPrefixes).Count == 0)
            throw new Exception("Legacy preferences without commit-prefix settings did not keep usable defaults.");

        var git = CommandShells.Resolve("git");
        if (git is not null)
        {
            var repo = Path.Combine(Path.GetTempPath(), "axmol-hub-commit-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repo);
            var runner = new ProcessRunner(_ => { });
            try
            {
                await RunGit("init", "-b", "main");
                await RunGit("config", "user.name", "Axmol Hub Check");
                await RunGit("config", "user.email", "hub-check@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "before staged\n");
                await File.WriteAllTextAsync(Path.Combine(repo, "unstaged.txt"), "before worktree\n");
                await RunGit("add", "staged.txt", "unstaged.txt");
                await RunGit("commit", "-m", "chore: create fixture baseline");
                await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged change\n");
                await RunGit("add", "staged.txt");
                await File.WriteAllTextAsync(Path.Combine(repo, "unstaged.txt"), "unstaged change\n");
                var repository = await GitRepository.ReadAsync(repo, runner);
                if (!repository.IsReadable || repository.Root is not { } repositoryRoot)
                    throw new Exception("Git fixture repository could not be read.");
                var outcome = await GitRepository.CommitStagedAsync(repo, repositoryRoot,
                    "feat: commit only staged content", ["feat:", "fix:", "docs:", "misc:"], runner);
                if (!outcome.Success) throw new Exception("Staged commit failed: " + outcome.FailureKey + " " + outcome.Detail);
                var committed = await runner.RunAsync(git, ["show", "--format=", "--name-only", "HEAD"], repo);
                var status = await GitRepository.ReadAsync(repo, runner);
                if (!committed.Output.Contains("staged.txt", StringComparison.Ordinal)
                    || committed.Output.Contains("unstaged.txt", StringComparison.Ordinal)
                    || !status.Entries.Any(entry => entry.RelativePath == "unstaged.txt"
                        && entry.IndexState == " " && entry.WorktreeState == "M"))
                    throw new Exception("Hub's staged-only commit included unstaged work or failed to leave it in the worktree.");
                var noneStaged = await GitRepository.CommitStagedAsync(repo, repositoryRoot,
                    "fix: no staged changes", ["feat:", "fix:"], runner);
                if (noneStaged.Success || noneStaged.FailureKey != "nothing-staged")
                    throw new Exception("Commit accepted an empty index.");

                var remote = Path.Combine(Path.GetTempPath(), "axmol-hub-commit-remote-" + Guid.NewGuid().ToString("N") + ".git");
                try
                {
                    var remoteInit = await runner.RunAsync(git, ["init", "--bare", remote], repo);
                    if (remoteInit.ExitCode != 0) throw new Exception("Git remote fixture failed: " + remoteInit.Error);
                    await RunGit("remote", "add", "origin", remote);
                    await RunGit("push", "-u", "origin", "main");
                    await File.WriteAllTextAsync(Path.Combine(repo, "push.txt"), "push this\n");
                    await RunGit("add", "push.txt");
                    var combined = await GitRepository.CommitAndPushStagedAsync(repo, repositoryRoot,
                        "feat: commit and push staged content", ["feat:", "fix:"], runner);
                    if (!combined.Success) throw new Exception("Commit-and-push failed: " + combined.FailureKey + " " + combined.Detail);
                    var remoteHead = await runner.RunAsync(git, ["rev-parse", "--verify", "refs/heads/main"], remote);
                    var localHead = await runner.RunAsync(git, ["rev-parse", "HEAD"], repo);
                    if (remoteHead.ExitCode != 0 || remoteHead.Output.Trim() != localHead.Output.Trim())
                        throw new Exception("Commit-and-push did not update the configured upstream.");
                    var pushOnly = await GitRepository.PushAsync(repo, repositoryRoot, runner);
                    if (!pushOnly.Success) throw new Exception("Push-only operation failed: " + pushOnly.FailureKey);
                }
                finally
                {
                    try { Directory.Delete(remote, recursive: true); } catch (IOException) { }
                }
            }
            finally
            {
                try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
            }

            async Task RunGit(params string[] arguments)
            {
                var result = await runner.RunAsync(git, arguments, repo);
                if (result.ExitCode != 0) throw new Exception("Git fixture failed: " + result.Error + result.Output);
            }
        }
        else
        {
            Console.WriteLine("SKIP: git executable unavailable; repository commit fixture not run.");
        }
        Console.WriteLine("PASS: commit summaries require one explicit marker, an allowed prefix and a bounded title; legacy settings retain defaults; local commits include staged files only.");
        return;
    }
}
