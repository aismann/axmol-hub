namespace AxmolHub.Core;

/// <summary>What the Committed list came to, in the cases the pane has to say out loud. The four repository
/// refusals are the same ones <see cref="GitReadOutcome"/> names, because the reader is the same reader.</summary>
public enum GitCommitListOutcome
{
    Ok,
    NoWorkspace,
    NotARepository,
    GitMissing,
    UnsafeRepository,

    /// <summary>The branch has no upstream, so "unpushed" has no meaning here. Listing every commit and calling
    /// it unpushed would be a guess about where the person pushes.</summary>
    NoUpstream,

    /// <summary>An upstream is configured and nothing is ahead of it.</summary>
    NoCommits,

    Failed,
}

/// <summary>One commit in the Committed list. The time is the committer's, which is the one <c>git log</c> shows
/// and the one that answers "when did this land on the branch" — an author date rewritten by a rebase would
/// order the list by something nobody sees in git's own output.</summary>
public sealed record GitCommitInfo(string Id, string ShortId, string Subject, DateTimeOffset At, int ParentCount)
{
    public bool IsMerge => ParentCount > 1;
}

/// <summary>The unpushed commits of the current branch, newest first, with the reason when there are none.</summary>
public sealed record GitCommitList(GitCommitListOutcome Outcome, string? Root, string? Branch, string? Upstream,
    IReadOnlyList<GitCommitInfo> Commits, string? Detail)
{
    public bool IsReadable => Outcome == GitCommitListOutcome.Ok;

    public static GitCommitList Stopped(GitCommitListOutcome outcome, string? detail = null)
        => new(outcome, null, null, null, [], detail);
}

/// <summary>One path a commit or a branch range touched: the name, git's own status letter, and the origin name
/// for a rename or copy.</summary>
public sealed record GitChangedPath(string RelativePath, string Status, string? OriginalPath = null);

/// <summary>A list of paths from one comparison — a commit against its parent, or a merge base against HEAD —
/// with the merge base kept so the pane can say what it compared.</summary>
public sealed record GitChangedPaths(GitCommitListOutcome Outcome, string? Detail,
    IReadOnlyList<GitChangedPath> Paths, string? MergeBase = null)
{
    public bool IsReadable => Outcome == GitCommitListOutcome.Ok;

    public static GitChangedPaths Stopped(GitCommitListOutcome outcome, string? detail = null)
        => new(outcome, detail, []);
}

/// <summary>One local branch, as the Branch scope's picker needs it: what to compare against and whether this is
/// the branch that is checked out.</summary>
public sealed record GitBranchInfo(string Name, string? Upstream, bool IsCurrent);

/// <summary>
/// Which comparison the Changes pane is showing. The order is the menu's order, and it is the only place the
/// scopes are enumerated — a second list in the view would be a second answer to "what can I look at here".
/// </summary>
public enum GitReviewScope
{
    /// <summary>This session's own edits, from the transcript and the revisions it points at. Not a git scope:
    /// it answers "what did the assistant just do", which git cannot attribute.</summary>
    LastTurn,
    Uncommitted,
    Unstaged,
    Staged,
    Committed,
    Branch,
}

/// <summary>How each scope reads the repository. A scope is a filter over one status reading plus one range for
/// the per-file comparison, and both live here so a row and its diff can never be built from different ranges.</summary>
public static class GitReviewScopes
{
    /// <summary>Which two versions a row of this scope compares.</summary>
    public static GitFileRange RangeFor(GitReviewScope scope) => scope switch
    {
        GitReviewScope.Staged => GitFileRange.Index,
        GitReviewScope.Unstaged => GitFileRange.WorkingTree,
        GitReviewScope.Committed => GitFileRange.Commit,
        GitReviewScope.Branch => GitFileRange.Range,
        _ => GitFileRange.WorkingTreeAgainstHead,
    };

    /// <summary>
    /// Whether a status line belongs in the scope. Unstaged means the worktree differs from the index, staged means
    /// the index differs from HEAD, and uncommitted is both plus everything untracked — which is what "not in a
    /// commit yet" means, and the only one of the three git itself spells <c>git diff HEAD</c>.
    ///
    /// <para>A conflict (<c>UU</c>, <c>AA</c>, <c>DU</c>) is listed in both staged and unstaged on purpose: both
    /// sides really are different, and <see cref="ReadFileAsync"/> answers the resolution with a sentence rather
    /// than a diff.</para>
    /// </summary>
    public static bool Applies(GitReviewScope scope, GitStatusEntry entry) => scope switch
    {
        GitReviewScope.Unstaged => entry.Untracked || entry.WorktreeState != " ",
        GitReviewScope.Staged => !entry.Untracked && entry.IndexState != " ",
        _ => true,
    };
}

public static partial class GitRepository
{
    /// <summary>How many unpushed commits the list carries. Past it the pane says the list was cut; a branch a
    /// thousand commits ahead is a branch whose individual commits nobody is reviewing from a dropdown.</summary>
    public const int MaxCommits = 100;

    /// <summary>The status fields each list line is cut on, chosen because neither can occur in a commit subject
    /// or a branch name that git itself will produce.</summary>
    private const char FieldSeparator = '\u001f';

    /// <summary>
    /// The commits on this branch that the upstream does not have, newest first — the Committed menu's list.
    ///
    /// <para><b>Why the upstream and not HEAD.</b> A commit is "committed" to a person in the sense of "done and
    /// recorded locally"; the interesting ones are the ones that have not left the machine yet, because after a
    /// push they belong to the remote history and reviewing them one at a time is what the remote is for. With no
    /// upstream configured there is no such set, and the answer is <see cref="GitCommitListOutcome.NoUpstream"/>
    /// rather than every commit the branch has.</para>
    /// </summary>
    public static async Task<GitCommitList> ReadUnpushedCommitsAsync(string? workspaceRoot, ProcessRunner runner,
        IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return GitCommitList.Stopped(GitCommitListOutcome.NoWorkspace);
        if (CommandShells.Resolve("git") is not { } git) return GitCommitList.Stopped(GitCommitListOutcome.GitMissing);
        var workspace = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(workspace)) return GitCommitList.Stopped(GitCommitListOutcome.NoWorkspace, workspace);

        try
        {
            var head = await runner.RunAsync(git, ["rev-parse", "--verify", "--quiet", "HEAD"], workspace,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            if (head.ExitCode != 0)
                return GitCommitList.Stopped(GitCommitListOutcome.NoCommits, FirstText(head.Error, head.Output));
            if (await ReadToplevelAsync(git, runner, workspace, sensitiveValues, cancellation).ConfigureAwait(false) is not { } root)
                return GitCommitList.Stopped(await ClassifyAsync(git, runner, workspace, head, sensitiveValues, cancellation)
                    .ConfigureAwait(false));

            var upstream = await runner.RunAsync(git,
                ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            var upstreamName = upstream.Output.Trim();
            if (upstream.ExitCode != 0 || upstreamName.Length == 0)
                return new GitCommitList(GitCommitListOutcome.NoUpstream, root, await ReadBranchNameAsync(git, runner, root, sensitiveValues, cancellation).ConfigureAwait(false),
                    null, [], FirstText(upstream.Error, upstream.Output));

            var log = await runner.RunAsync(git,
                ["--no-pager", "log", "-n", MaxCommits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--format=%H" + FieldSeparator + "%h" + FieldSeparator + "%ct" + FieldSeparator + "%P" +
                    FieldSeparator + "%s", "-z", $"{upstreamName}..HEAD", "--"],
                root, timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation)
                .ConfigureAwait(false);
            if (log.ExitCode != 0)
                return new GitCommitList(GitCommitListOutcome.Failed, root, null, upstreamName, [],
                    FirstText(log.Error, log.Output));

            var commits = new List<GitCommitInfo>();
            foreach (var record in log.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = record.Trim('\n', '\r').Split(FieldSeparator);
                if (fields.Length < 5 || fields[0].Length is not (40 or 64)) continue;
                if (!long.TryParse(fields[2], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var seconds)) continue;
                var parents = fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                commits.Add(new GitCommitInfo(fields[0], fields[1], fields[4],
                    DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime(), parents));
            }
            // git log prints newest first; the list keeps that order because a picker's first row is the one a
            // person expects to be the latest, and because sorting by a parsed time would lose equal timestamps.
            if (commits.Count == 0)
                return new GitCommitList(GitCommitListOutcome.NoCommits, root,
                    await ReadBranchNameAsync(git, runner, root, sensitiveValues, cancellation).ConfigureAwait(false),
                    upstreamName, [], null);
            return new GitCommitList(GitCommitListOutcome.Ok, root,
                await ReadBranchNameAsync(git, runner, root, sensitiveValues, cancellation).ConfigureAwait(false),
                upstreamName, commits, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IdleTimeoutException stalled)
        {
            return GitCommitList.Stopped(GitCommitListOutcome.Failed, Stalled(stalled));
        }
        catch (Exception ex)
        {
            return GitCommitList.Stopped(GitCommitListOutcome.Failed, ex.Message);
        }
    }

    /// <summary>The local branches, current one marked, for the Branch scope's base picker.</summary>
    public static async Task<IReadOnlyList<GitBranchInfo>> ReadBranchesAsync(string repositoryRoot, ProcessRunner runner,
        IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default)
    {
        if (CommandShells.Resolve("git") is not { } git) return [];
        try
        {
            var result = await runner.RunAsync(git,
                ["for-each-ref", "--format=%(refname:short)" + FieldSeparator + "%(upstream:short)" + FieldSeparator + "%(HEAD)", "refs/heads"],
                Path.GetFullPath(repositoryRoot), timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues,
                cancellation: cancellation).ConfigureAwait(false);
            if (result.ExitCode != 0) return [];
            var branches = new List<GitBranchInfo>();
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Trim('\r').Split(FieldSeparator);
                if (fields.Length < 3 || fields[0].Length == 0) continue;
                branches.Add(new GitBranchInfo(fields[0],
                    fields[1].Length == 0 ? null : fields[1],
                    string.Equals(fields[2].Trim(), "*", StringComparison.Ordinal)));
            }
            return branches;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// The paths a commit touched, compared with the same parent <see cref="ReadCommitFileAsync"/> compares with:
    /// the first parent for a merge, the empty tree for a root commit. A row opened from this list therefore shows
    /// exactly the change the list said the commit made.
    /// </summary>
    public static async Task<GitChangedPaths> ReadCommitPathsAsync(string repositoryRoot, string commit,
        ProcessRunner runner, IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default)
    {
        if (CommandShells.Resolve("git") is not { } git)
            return GitChangedPaths.Stopped(GitCommitListOutcome.GitMissing);
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            var resolved = await ResolveCommitAsync(git, runner, root, commit, sensitiveValues, cancellation).ConfigureAwait(false);
            if (resolved.Error is { } error) return GitChangedPaths.Stopped(GitCommitListOutcome.Failed, error);
            var (oid, parent, _) = resolved;
            var arguments = new List<string> { "-c", "core.quotepath=false", "diff-tree", "-r", "-z", "--no-commit-id", "--name-status" };
            if (parent is null) arguments.Add("--root");
            else arguments.Add(parent);
            arguments.Add(oid);
            arguments.Add("--");
            var result = await runner.RunAsync(git, arguments, root, timeout: ReadIdleTimeout,
                sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return GitChangedPaths.Stopped(GitCommitListOutcome.Failed, FirstText(result.Error, result.Output));
            return new GitChangedPaths(GitCommitListOutcome.Ok, null, ParseNameStatus(result.Output));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GitChangedPaths.Stopped(GitCommitListOutcome.Failed,
                ex is IdleTimeoutException stalled ? Stalled(stalled) : ex.Message);
        }
    }

    /// <summary>
    /// The paths this branch added or changed since it left <paramref name="baseRef"/>: the merge base to HEAD, the
    /// three-dot comparison, so a base that moved on without this branch does not read as deletions. The merge base
    /// comes back with the list because the pane names it — "since main (abc1234) forked" is a fact the reader can
    /// check, and an unnamed range is not.
    /// </summary>
    public static async Task<GitChangedPaths> ReadBranchRangePathsAsync(string repositoryRoot, string baseRef,
        ProcessRunner runner, IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default)
    {
        if (CommandShells.Resolve("git") is not { } git)
            return GitChangedPaths.Stopped(GitCommitListOutcome.GitMissing);
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            var baseCommit = await ResolveCommitAsync(git, runner, root, baseRef, sensitiveValues, cancellation).ConfigureAwait(false);
            if (baseCommit.Error is { } error) return GitChangedPaths.Stopped(GitCommitListOutcome.Failed, error);
            var mergeBase = await runner.RunAsync(git, ["merge-base", baseCommit.Oid, "HEAD"], root,
                timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            var fork = mergeBase.Output.Trim();
            if (mergeBase.ExitCode != 0 || fork.Length is not (40 or 64))
                return GitChangedPaths.Stopped(GitCommitListOutcome.Failed,
                    FirstText(mergeBase.Error, mergeBase.Output) ?? "The branches have no common history.");
            var result = await runner.RunAsync(git,
                ["-c", "core.quotepath=false", "diff", "-z", "--name-status", "--no-renames", fork, "HEAD", "--"],
                root, timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return GitChangedPaths.Stopped(GitCommitListOutcome.Failed, FirstText(result.Error, result.Output));
            return new GitChangedPaths(GitCommitListOutcome.Ok, null, ParseNameStatus(result.Output), fork);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GitChangedPaths.Stopped(GitCommitListOutcome.Failed,
                ex is IdleTimeoutException stalled ? Stalled(stalled) : ex.Message);
        }
    }

    /// <summary>
    /// Both sides of one path between two named versions — the Branch scope's row. The versions are whatever the
    /// caller read out of git (a merge base and HEAD), never refs re-resolved here: two calls could otherwise see
    /// two different HEADs and draw a diff of a comparison nobody asked for.
    /// </summary>
    public static async Task<GitFileComparison> ReadRangeFileAsync(string repositoryRoot, string path,
        string oldVersion, string newVersion, ProcessRunner runner, string? note = null,
        IReadOnlyList<string>? sensitiveValues = null, CancellationToken cancellation = default,
        int maxBytes = TextFile.MaxDiffBytes)
    {
        GitFileComparison Failed(string detail) => new(path, null, GitFileRange.Range,
            GitFileVersion.Absent(), GitFileVersion.Absent(), detail);
        if (CommandShells.Resolve("git") is not { } git) return Failed("git is not installed.");
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            if (FullPathInside(root, path) is null) return Failed("The path must be repository-relative.");
            var older = await ReadTreeVersionAsync(git, runner, root, oldVersion, path, maxBytes, sensitiveValues, cancellation).ConfigureAwait(false);
            var newer = await ReadTreeVersionAsync(git, runner, root, newVersion, path, maxBytes, sensitiveValues, cancellation).ConfigureAwait(false);
            if (older.Snapshot.State == TextFileState.Failed || newer.Snapshot.State == TextFileState.Failed)
                return Failed(older.Snapshot.Detail ?? newer.Snapshot.Detail ?? "The version could not be read.");
            return new GitFileComparison(path, null, GitFileRange.Range, older, newer, ComparisonNote: note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ex is IdleTimeoutException stalled ? Stalled(stalled) : ex.Message);
        }
    }

    /// <summary>The current branch name, or null on a detached HEAD where there is no branch to name.</summary>
    private static async Task<string?> ReadBranchNameAsync(string git, ProcessRunner runner, string root,
        IReadOnlyList<string>? sensitiveValues, CancellationToken cancellation)
    {
        var result = await runner.RunAsync(git, ["symbolic-ref", "--quiet", "--short", "HEAD"], root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        var name = result.Output.Trim();
        return result.ExitCode == 0 && name.Length > 0 ? name : null;
    }

    /// <summary>A commit's id and its first parent (null for a root commit), or the sentence saying why not.</summary>
    private static async Task<(string Oid, string? Parent, string? Error)> ResolveCommitAsync(string git,
        ProcessRunner runner, string root, string commit, IReadOnlyList<string>? sensitiveValues,
        CancellationToken cancellation)
    {
        var resolved = await runner.RunAsync(git,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", commit + "^{commit}"], root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        var oid = resolved.Output.Trim();
        if (resolved.ExitCode != 0 || oid.Length is not (40 or 64))
            return ("", null, "The selected commit is unavailable.");
        var parents = await runner.RunAsync(git, ["rev-list", "--parents", "-n", "1", oid], root,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        if (parents.ExitCode != 0)
            return ("", null, FirstText(parents.Error, parents.Output) ?? "Could not read commit parents.");
        var fields = parents.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0 || fields[0] != oid) return ("", null, "Git returned an invalid commit record.");
        return (oid, fields.Length > 1 ? fields[1] : null, null);
    }

    /// <summary><c>--name-status -z</c> records: a status letter, then the path, and for a rename or copy the
    /// origin after the destination.</summary>
    private static IReadOnlyList<GitChangedPath> ParseNameStatus(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<GitChangedPath>(fields.Length);
        for (var index = 0; index < fields.Length; index++)
        {
            var status = fields[index].Trim('\n', '\r');
            if (status.Length == 0 || index + 1 >= fields.Length) break;
            if (status[0] is 'R' or 'C')
            {
                if (index + 2 >= fields.Length) break;
                paths.Add(new GitChangedPath(fields[index + 1], status[..1], fields[index + 2]));
                index += 2;
                continue;
            }
            paths.Add(new GitChangedPath(fields[index + 1], status[..1]));
            index += 1;
        }
        return paths;
    }

    /// <summary>Turn a failed <c>git rev-parse HEAD</c> into the repository refusal it is, so a folder that is not a
    /// repository says so instead of reading as a repository with no commits.</summary>
    private static async Task<GitCommitListOutcome> ClassifyAsync(string git, ProcessRunner runner, string workspace,
        ProcessResult head, IReadOnlyList<string>? sensitiveValues, CancellationToken cancellation)
    {
        var why = FirstText(head.Error, head.Output) ?? "";
        if (why.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            return GitCommitListOutcome.NotARepository;
        if (why.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
            return GitCommitListOutcome.UnsafeRepository;
        var probe = await runner.RunAsync(git, ["rev-parse", "--is-inside-work-tree"], workspace,
            timeout: ReadIdleTimeout, sensitiveValues: sensitiveValues, cancellation: cancellation).ConfigureAwait(false);
        var text = FirstText(probe.Error, probe.Output) ?? "";
        if (probe.ExitCode != 0 && text.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            return GitCommitListOutcome.NotARepository;
        return GitCommitListOutcome.Failed;
    }
}
