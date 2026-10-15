using System.Text.Json;
using Token = AxmolHub.Core.ShellCommandLine.Token;

namespace AxmolHub.Core;

/// <summary>
/// What a shell command line would do to a git repository, decided without running it.
///
/// Hub gives the model one command tool rather than a git tool, which is what every established client does
/// (Copilot's <c>#execute</c>, Claude Code's <c>Bash</c>, opencode's <c>bash</c>, Cline's
/// <c>execute_command</c>): the shell stays one path, and the repository stays reachable. What that choice
/// commits Hub to is grading the <i>subcommand</i>, because "a command run in the session's sandbox" stops being
/// the whole story the moment the command is <c>git reset --hard</c> — the sandbox is the working tree, and the
/// state git moves lives in <c>.git</c>, which the write guard already treats as protected
/// (<see cref="WorkspacePaths.IsExcludedDirectory"/>). Windsurf's terminal documentation is the cautionary tale
/// for the coarser rule: allow-listing the bare word <c>git</c> let <c>git add -A</c> run unasked, so the
/// granularity has to be the verb, not the program.
///
/// The ladder only ever goes <b>up</b>. A command this classifier does not understand keeps the tier it has today
/// (<see cref="ToolRisk.WorkspaceCommand"/>), and that direction is deliberate: every read in the product
/// (<c>git status</c>, <c>git diff</c>, <c>git log</c>) must keep running under 自动审批, because a model that
/// cannot look at the repository without a click guesses from a file it read three turns ago instead. An
/// <i>unrecognised git verb</i> is the exception and goes up: an extension or alias outside the read set
/// (<c>git lfs install</c>, <c>git flow feature start</c>) can move the tree, and nothing about a verb nobody
/// recognised says it will not.
///
/// <para>What a grade then <i>costs</i> is a second question, decided one step out from here:
/// <see cref="LocalWriteVerbs"/> is the whole list of writes that stay inside the repository and are undone
/// there, and every other write — including a verb nobody recognised — is the reach the tier table calls a system
/// command. So this classifier never says "safe". It says what the line does, and the table beside it says who
/// has to be asked.</para>
///
/// <para>The line is split by <see cref="ShellCommandLine"/>, the same reader
/// <see cref="BuildCommandGrades"/> uses: two classifiers with two tokenizers would answer two different
/// questions about one command string.</para>
/// </summary>
public enum GitGrade
{
    /// <summary>No git invocation in it, or nothing in it that can change a repository. Keeps today's tier.</summary>
    NotGit,

    /// <summary>A git command that only reads: <c>status</c>, <c>diff</c>, <c>log</c>, <c>show</c>. Keeps
    /// today's tier, so the model can look at the repository under 自动审批 exactly as it looks at files.</summary>
    GitRead,

    /// <summary>A git command that moves the index, the working tree, a ref, or history — state no file tool can
    /// hand back. What that <i>costs</i> is not decided here: <see cref="LocalWriteVerbs"/> is the half of this
    /// grade that stays inside the repository and is undone there, and the tier table says what the rest ask
    /// for.</summary>
    GitWrite,

    /// <summary>Git was called with a verb this build has never heard of. Graded as a write, because the honest
    /// reading of an unrecognised subcommand is that it might touch the tree.</summary>
    GitUnclassifiable,
}

/// <summary>
/// The classifier, as one pure function over a command line. It lives in Core for the standing reason given at
/// <see cref="WorkspaceTools"/>: <c>tests/AxmolHub.Checks</c> may reference Core but not the app, so a rule only
/// the app could see is a rule nobody can assert. It does no I/O and never throws — a command line that confuses
/// the tokenizer answers <see cref="GitGrade.NotGit"/>, which is the tier the call already had.
/// </summary>
public static class GitCommandGrades
{
    /// <summary>
    /// Verbs whose writes stay inside the repository and can be reached back, so no mode has to ask about them:
    /// <c>add</c> undoes with <c>git reset</c>, and <c>commit</c> undoes with <c>git reset --soft</c> or the reflog.
    /// Neither throws away uncommitted work, and neither sends anything off this machine — that pair of questions
    /// is the admission bar, which is why <c>push</c> is not on the list (it leaves the machine), <c>reset --hard</c>
    /// and <c>clean</c> are not (they destroy work nothing can hand back), and <c>stash</c> is not (hiding work is
    /// not recording it). Extending the list is a one-word edit and a one-cell assertion change, which is the point
    /// of keeping it small and named.
    ///
    /// <para><b>One table, and <c>add</c> and <c>commit</c> have to stay in it together.</b> The exemption is read
    /// as one set that has to cover every write in the line, never as a union of sets (see
    /// <see cref="AllWritesAllowed"/>): split them across two tables and <c>git add -A &amp;&amp; git commit -m x</c>
    /// — the ordinary way anyone commits — would straddle both and keep asking, which is the one line this table
    /// exists for.</para>
    ///
    /// <para>It is the <i>policy</i> half of the exemption; the grant half is the user's own list
    /// (<see cref="CommandTrust"/>). The two are consulted one at a time and never as a union, and a grant names a
    /// verb rather than a table.</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> LocalWriteVerbs = ["add", "commit"];
    /// <summary>Verbs that only read. A verb missing from both sets lands on
    /// <see cref="GitGrade.GitUnclassifiable"/>, which asks, so an omission costs a card rather than opening a
    /// hole — which is why the write set is allowed to be short.</summary>
    private static readonly HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "annotate", "blame", "cat-file", "describe", "diff", "diff-files", "diff-index", "diff-tree",
        "for-each-ref", "fsck", "grep", "help", "log", "ls-files", "ls-remote", "ls-tree", "merge-base",
        "name-rev", "reflog", "rev-list", "rev-parse", "shortlog", "show", "show-ref", "status",
        "stripspace", "var", "verify-commit", "verify-pack", "verify-tag", "version", "whitespace",
    };

    /// <summary>Verbs that move the index, the tree, a ref, or history. <c>stash</c>, <c>worktree</c>,
    /// <c>branch</c>, <c>tag</c>, <c>config</c> and <c>remote</c> are deliberately absent: each is a read or a
    /// write depending on its arguments, so listing them here would decide them before their arguments were read.</summary>
    private static readonly HashSet<string> WriteVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "am", "apply", "bisect", "bundle", "checkout", "cherry", "cherry-pick", "clean", "clone",
        "commit", "fast-import", "fetch", "filter-branch", "gc", "init", "merge", "mv", "notes", "prune",
        "pull", "push", "rebase", "replace", "reset", "restore", "revert", "rm", "sparse-checkout", "submodule",
        "switch", "update-index", "update-ref", "update-server-info",
    };

    /// <summary>Git's own options that take a separate argument, so the verb is one token further on than it
    /// looks. Everything else beginning with <c>-</c> is boolean or carries its value with <c>=</c>.</summary>
    private static readonly HashSet<string> GlobalFlagsWithValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "-C", "-c", "--exec-path", "--git-dir", "--namespace", "--super-prefix", "--work-tree",
    };

    /// <summary>
    /// The grade of the <c>command</c> argument of a tool call. Absent, blank, or unparseable arguments answer
    /// <see cref="GitGrade.NotGit"/> rather than the top tier, and that is load-bearing twice: the tool body
    /// refuses an empty command before it can run anything (<see cref="WorkspaceTools.RunCommand"/>), and a card
    /// about "the command Hub could not read" would be a card with nothing in it to approve.
    /// </summary>
    public static GitGrade CommandOf(string? argumentsJson) => Grade(CommandTextOf(argumentsJson));

    /// <summary>
    /// The write verbs a command line spells out for itself, lowercased and in order — what a card may offer to stop
    /// asking about, and what becomes a <see cref="CommandTrust"/> key.
    ///
    /// <para>Empty means "nothing in this line can be granted", and it covers three lines on purpose: one with no
    /// write at all, one whose write this build cannot name (<see cref="GitGrade.GitUnclassifiable"/>), and one whose
    /// write only exists behind a wrapper (<c>pwsh -c "git push"</c>, <c>sudo git push</c>). A grant names the call as
    /// the person read it on the card, so a verb the line never spells is a verb no grant can cover — reporting it
    /// would only grow a button the gate would ignore.</para>
    /// </summary>
    public static IReadOnlyCollection<string> WriteVerbsOf(string? argumentsJson)
    {
        var verbs = new List<string>();
        foreach (var call in CallsOf(argumentsJson))
        {
            if (call.Grade == GitGrade.GitUnclassifiable) return [];
            if (call.Grade == GitGrade.GitWrite && call.Verb is null) return [];
            if (call.IsDirectWrite && !verbs.Contains(call.Verb!)) verbs.Add(call.Verb!);
        }
        return verbs;
    }

    /// <summary>
    /// Whether every git write in the line is a direct <c>git &lt;verb&gt;</c> call whose verb is in
    /// <paramref name="allowedVerbs"/>. A write reached only through a shell payload or a pass-through keeps its
    /// tier: the grant names a spelled call, and a segment that does not spell it is not that call. A line this
    /// build cannot read at all is never allowed, and a line with no git write in it is allowed vacuously — which is
    /// what leaves every non-git command at the tier it already had.
    /// </summary>
    public static bool AllWritesAllowed(string? argumentsJson, IReadOnlyCollection<string> allowedVerbs)
    {
        foreach (var call in CallsOf(argumentsJson))
        {
            if (call.Grade == GitGrade.GitUnclassifiable) return false;
            if (call.Grade != GitGrade.GitWrite) continue;
            if (call.Verb is null || !allowedVerbs.Contains(call.Verb)) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the line's git content is nothing but writes that stay in the repository — every write a direct
    /// <c>git &lt;verb&gt;</c> call naming a verb in <see cref="LocalWriteVerbs"/>, and at least one of them.
    ///
    /// <para>The "at least one" half is what keeps a command with no git write in it — a build, a test run, a bare
    /// <c>git status</c> — out of this answer: those keep the sandbox tier they already had, and a caller that read
    /// a true here as "nothing in this line needs asking" would be reading it the wrong way round. A line this
    /// build could not read answers false by construction rather than by a second rule
    /// (<see cref="WriteVerbsOf"/> reports no verb for an unrecognised subcommand or for a write reached through a
    /// wrapper, so neither can be covered), which is the direction every uncertain case has to fail in.</para>
    /// </summary>
    public static bool LocalWritesOnly(string? argumentsJson)
        => WriteVerbsOf(argumentsJson).Count > 0 && AllWritesAllowed(argumentsJson, LocalWriteVerbs);

    private static string? CommandTextOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("command", out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<GitCall> CallsOf(string? argumentsJson)
    {
        var calls = new List<GitCall>();
        Collect(CommandTextOf(argumentsJson), 0, direct: true, calls);
        return calls;
    }

    /// <summary>Grade a whole command line: the worst segment wins, since <c>git add -A && git commit -m x</c>
    /// is a write whatever its first half looks like.</summary>
    public static GitGrade Grade(string? command) => Grade(command, 0);

    /// <summary>One git call a line performs, with the verb it spelled when it spelled one directly. <c>Verb</c> is
    /// empty for a read and for a write the classifier had to reach through a wrapper — there is no
    /// <c>git &lt;verb&gt;</c> in the line to name either.</summary>
    private readonly record struct GitCall(GitGrade Grade, string? Verb)
    {
        public bool IsDirectWrite => Grade == GitGrade.GitWrite && Verb is not null;
    }

    private static GitGrade Grade(string? command, int depth)
    {
        // One walk, two readers: the tier is the worst call this finds, and the exemption asks the same calls
        // whether they are all covered. A second traversal here would be a second matcher to keep in step, which is
        // exactly how 「认不出的子命令按写处理」 stopped being true last time.
        var worst = GitGrade.NotGit;
        var calls = new List<GitCall>();
        Collect(command, depth, direct: true, calls);
        foreach (var call in calls)
        {
            if (Rank(call.Grade) > Rank(worst)) worst = call.Grade;
        }
        return worst;
    }

    private static void Collect(string? command, int depth, bool direct, List<GitCall> calls)
    {
        foreach (var segment in ShellCommandLine.SegmentsOf(command)) CollectSegment(segment, depth, direct, calls);
    }

    private static void CollectSegment(List<Token> segment, int depth, bool direct, List<GitCall> calls)
    {
        var index = ShellCommandLine.HeadIndex(segment);
        if (index >= segment.Count) return;

        var name = ShellCommandLine.ExecutableName(segment[index].Text);
        if (name.Length == 0) return;
        if (string.Equals(name, "git", StringComparison.Ordinal))
        {
            // The verb is reported only where the line spells it at this segment's head. Anything reached by
            // piercing keeps the grade and loses the verb: `pwsh -c "git push"` is a write, and no part of the line
            // is the call `git push` a person can grant.
            var call = DirectCall(segment, index + 1);
            calls.Add(direct ? call : call with { Verb = null });
            return;
        }
        if (depth < 2)
        {
            if (ShellCommandLine.IsShell(name))
            {
                var payload = ShellCommandLine.CommandPayload(segment, index + 1);
                if (payload is not null) Collect(payload, depth + 1, direct: false, calls);
                return;
            }
            if (ShellCommandLine.IsPassThrough(name)) CollectTail(segment, index + 1, depth, calls);
        }
    }

    /// <summary>
    /// Collect what a pass-through program was given, by finding the first token that is itself a program this
    /// classifier knows: <c>timeout 60 git clone x</c> and <c>env -i LC_ALL=C git push</c> both hide their verb
    /// behind the wrapper's own arguments, and reading position 0 as the head would call <c>60</c> a program.
    /// Anything the scan does not recognise keeps today's tier, which is the direction that never stops a build.
    ///
    /// <para>Whatever it finds is reported with no verb: the write is graded, so its tier is right, but no part of the
    /// line spells <c>git &lt;verb&gt;</c> for a grant to name — that is what keeps <c>sudo git push</c> asking even
    /// after <c>git push</c> was granted.</para>
    /// </summary>
    private static void CollectTail(List<Token> segment, int from, int depth, List<GitCall> calls)
    {
        for (var i = from; i < segment.Count; i++)
        {
            var token = segment[i];
            if (ShellCommandLine.IsEnvironmentAssignment(token)) continue;
            if (token.Text.StartsWith('-') && !token.Quoted) continue;
            var name = ShellCommandLine.ExecutableName(token.Text);
            if (name.Length == 0) continue;
            if (string.Equals(name, "git", StringComparison.Ordinal))
            {
                calls.Add(new GitCall(GradeGitCall(segment, i + 1), null));
                return;
            }
            if (depth >= 2) continue;
            if (ShellCommandLine.IsShell(name))
            {
                var payload = ShellCommandLine.CommandPayload(segment, i + 1);
                if (payload is not null) Collect(payload, depth + 1, direct: false, calls);
                return;
            }
            if (ShellCommandLine.IsPassThrough(name))
            {
                CollectTail(segment, i + 1, depth + 1, calls);
                return;
            }
        }
    }

    private static int Rank(GitGrade grade) => grade switch
    {
        GitGrade.GitWrite => 3,
        GitGrade.GitUnclassifiable => 2,
        GitGrade.GitRead => 1,
        _ => 0,
    };

    /// <summary>Read the verb after <c>git</c>, skipping its own options — and report which verb was spelled, since
    /// that verb is the key a grant is written against.</summary>
    private static GitCall DirectCall(List<Token> segment, int from)
    {
        // A bare `git` prints usage and changes nothing.
        if (from >= segment.Count) return new GitCall(GitGrade.NotGit, null);
        for (var i = from; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.Length == 0) continue;
            if (token.Text[0] != '-')
            {
                var verb = token.Text.ToLowerInvariant();
                var grade = GradeGitVerb(token.Text, segment, i + 1);
                // A read spells nothing a grant could name — only a write verb is a key.
                return new GitCall(grade, grade == GitGrade.GitWrite ? verb : null);
            }
            if (token.Text is "--version" or "--help" or "--help-all" or "-h" or "-V") return new GitCall(GitGrade.NotGit, null);
            if (GlobalFlagsWithValues.Contains(token.Text))
            {
                i++; // its value is not the verb either.
                continue;
            }
        }
        // Options and no verb: something was asked of git and this build cannot say what.
        return new GitCall(GitGrade.GitUnclassifiable, null);
    }

    private static GitGrade GradeGitCall(List<Token> segment, int from) => DirectCall(segment, from).Grade;

    private static GitGrade GradeGitVerb(string verb, List<Token> segment, int rest)
    {
        if (ReadVerbs.Contains(verb)) return GitGrade.GitRead;
        if (WriteVerbs.Contains(verb)) return GitGrade.GitWrite;
        return verb.ToLowerInvariant() switch
        {
            // These are reads or writes depending on their arguments, so each is decided by them rather than
            // guessed from the verb alone.
            "branch" => GradeNamedOperation(segment, rest, ListingFlags:
                ["-a", "-r", "-v", "-l", "--list", "--all", "--remotes", "--show-current", "--contains",
                 "--no-merged", "--merged", "--format"]),
            "tag" => GradeNamedOperation(segment, rest, ListingFlags: ["-l", "--list", "-n", "--contains"]),
            "config" => GradeConfig(segment, rest),
            "remote" => GradeRemote(segment, rest),
            "stash" => GradeSubcommandListing(segment, rest, defaultWrite: true),
            // `git stash` with nothing after it pushes; `git worktree` prints its subcommand list.
            "worktree" => GradeSubcommandListing(segment, rest, defaultWrite: false),
            _ => GitGrade.GitUnclassifiable,
        };
    }

    /// <summary>A bare listing versus a named operation: no non-option argument, or one of the listing flags, is
    /// a read; anything else names a thing to create, delete, or move.</summary>
    private static GitGrade GradeNamedOperation(List<Token> segment, int rest, string[] ListingFlags)
    {
        for (var i = rest; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted)
            {
                if (Array.IndexOf(ListingFlags, token.Text) >= 0) return GitGrade.GitRead;
                continue;
            }
            return GitGrade.GitWrite;
        }
        return GitGrade.GitRead;
    }

    /// <summary><c>git config x</c> reads, <c>git config x y</c> writes, and the destructive spellings write
    /// whatever their argument count.</summary>
    private static GitGrade GradeConfig(List<Token> segment, int rest)
    {
        var arguments = 0;
        for (var i = rest; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted)
            {
                if (token.Text is "--unset" or "--unset-all" or "--add" or "--replace-all" or "--remove-section"
                               or "--rename-section" or "--edit" or "-e")
                    return GitGrade.GitWrite;
                if (token.Text is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l")
                    return GitGrade.GitRead;
                continue;
            }
            arguments++;
        }
        return arguments >= 2 ? GitGrade.GitWrite : GitGrade.GitRead;
    }

    /// <summary><c>git remote</c> and <c>git remote -v</c> list; <c>show</c>/<c>get-url</c> read; the rest name
    /// a remote to add, rename, or point somewhere else.</summary>
    private static GitGrade GradeRemote(List<Token> segment, int rest)
    {
        for (var i = rest; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted)
            {
                if (token.Text is "-v" or "-vn" or "--verbose") return GitGrade.GitRead;
                continue;
            }
            return token.Text.ToLowerInvariant() is "show" or "get-url"
                ? GitGrade.GitRead
                : GitGrade.GitWrite;
        }
        return GitGrade.GitRead;
    }

    /// <summary>For the verbs whose read is a sub-listing (<c>stash list</c>, <c>worktree list</c>).</summary>
    private static GitGrade GradeSubcommandListing(List<Token> segment, int rest, bool defaultWrite)
    {
        for (var i = rest; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted) continue;
            return token.Text.ToLowerInvariant() is "list" or "show" or "diff"
                ? GitGrade.GitRead
                : GitGrade.GitWrite;
        }
        return defaultWrite ? GitGrade.GitWrite : GitGrade.GitRead;
    }
}
