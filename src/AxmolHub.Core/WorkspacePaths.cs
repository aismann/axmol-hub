namespace AxmolHub.Core;

/// <summary>Why a model-supplied path was accepted or refused. The value is what the tool reports back, so a
/// new member needs a sentence in <see cref="WorkspacePaths.ResultFor"/> — a refusal the model cannot act on
/// turns into a retry loop.</summary>
public enum WorkspacePathVerdict
{
    Allowed,
    NoWorkspace,
    MissingWorkspace,
    EscapesWorkspace,
    /// <summary>A path this build can resolve, that no directory of this session contains. Its own verdict rather
    /// than a second meaning for <see cref="EscapesWorkspace"/> because the way out is different: the spelling was
    /// fine and the place was not, so the remedy is a grant and not a rewritten path.</summary>
    OutsideRoots,
    ProtectedRoot,
    ReparsePoint,
    ExtensionNotAllowed,
    NotAFile,
    NotADirectory,
    AlreadyExists,
}

public readonly record struct WorkspacePath(
    WorkspacePathVerdict Verdict, string Full, string Relative, string Root = "", bool Granted = false)
{
    public bool IsAllowed => Verdict == WorkspacePathVerdict.Allowed;

    /// <summary>
    /// The one spelling every surface uses for this path: the tool's own sentences, the transcript's change rows,
    /// the undo copy's name, and what <c>find_files</c> hands back to <c>read_file</c>.
    ///
    /// Under the primary root it is the workspace-relative path — the spelling every session had before a
    /// directory could be granted, which is what keeps an old transcript, an old pre-image and an old diff row
    /// meaning what they meant. Under a granted root it is the absolute path, because two granted directories can
    /// hold the same relative spelling and one of them would otherwise be addressable only by guessing. Nothing
    /// outside this file builds that spelling by hand: the rule lives here so the four consumers cannot disagree.
    /// </summary>
    public string Display => Granted ? Full : Relative;

    public static WorkspacePath Refuse(WorkspacePathVerdict verdict, string relative = "") => new(verdict, "", relative);
}

/// <summary>
/// Roots the assistant never writes into, whatever the approval mode says. <see cref="ToolApprovalModes.Full"/>
/// promises "nothing asks", not "anything goes": these stay closed.
///
/// <para>This is the list in the other direction from <see cref="WorkspaceSandbox.Granted"/>, and the two never
/// meet: a grant says where the assistant may work because a person pointed at it, a guard says what no grant and
/// no mode can open. The data directory and an engine installation are on it because Hub or the toolchain owns
/// them, and <c>.git</c> because rewriting history is never what a coding request meant.</para>
/// </summary>
public sealed record WorkspaceGuards(string? DataRoot, IReadOnlyList<string> EngineRoots);

/// <summary>
/// Every directory one request's file and command tools may touch: the session's workspace — the root the person
/// pointed the session at, and the only one a relative path resolves against — plus the directories the person has
/// granted this session, which are addressed absolutely.
///
/// <para>A grant adds a place to work; it never moves the workspace. Moving it stays the person's own move, made
/// on the composer's chip, because the directory a session is born into is what the sidebar groups it by, what the
/// repository pane reads and what a command runs in by default — none of which a reference checkout two folders
/// away should change.</para>
/// </summary>
public sealed record WorkspaceSandbox(string? Primary, IReadOnlyList<string> Granted)
{
    public static WorkspaceSandbox Empty { get; } = new(null, []);

    /// <summary>The one-root sandbox. A caller that has exactly one directory — a session nobody granted anything
    /// to, an assertion that names one folder — says so here rather than with a list of one.</summary>
    public static WorkspaceSandbox For(string? root) => new(root, []);

    /// <summary>Primary first, then the grants in the order they were approved. A path is graded against them in
    /// this order, so a directory reached by both spellings resolves as the workspace and keeps the relative
    /// spelling every older session knows it by.</summary>
    public IReadOnlyList<string> Roots { get; } = RootsOf(Primary, Granted);

    private static IReadOnlyList<string> RootsOf(string? primary, IReadOnlyList<string>? granted)
    {
        var roots = new List<string>();
        if (primary is { Length: > 0 }) roots.Add(primary);
        roots.AddRange(granted ?? []);
        return roots;
    }
}

/// <summary>What a path a card names is pointing at, decided by
/// <see cref="WorkspacePaths.ClassifyWorkspaceTarget"/>. The names answer one question: has a person already
/// agreed to this directory? <see cref="SameAsSandbox"/> and <see cref="InsideSandbox"/> have (the session is
/// already working in or under it), <see cref="RegisteredProject"/> has (the user added it to Hub), and
/// <see cref="Other"/> has not.</summary>
public enum WorkspaceTarget
{
    SameAsSandbox,
    InsideSandbox,
    RegisteredProject,
    Other,
    /// <summary>No usable directory in the argument, or one the sandbox guard would refuse in every mode.</summary>
    Unreadable,
}

/// <summary>
/// Confines the assistant's file tools to the directories one session has: its workspace, and the directories a
/// person granted that session. Deliberately separate from the approval gate: the gate decides whether a call may
/// run now, this decides whether the path is inside the sandbox at all. Containment of a relative path reuses
/// <see cref="PackageInstaller.SafePath"/>, which already rejects rooted paths, drive separators and <c>..</c>
/// traversal; it does not resolve links, so the reparse-point walk is done here — and it walks up to whichever root
/// the path resolved in, because that is the boundary the person agreed to.
///
/// <para>Two grammars, and the difference is deliberate. A relative path means "inside the workspace" and nothing
/// else, so every session file, transcript sentence and undo record written before grants existed still means what
/// it meant. An absolute path is graded against every root and is the only way to name a granted directory.</para>
/// </summary>
public static class WorkspacePaths
{
    /// <summary>Project memory lives inside the workspace, and the assistant is expected to write there.
    /// Composed rather than a literal so it carries the host's separator.</summary>
    public static string MemoryDirectory => Path.Combine(".agents", "memory");

    /// <summary>Plain-text source and project files, by extension rather than by sniffing bytes: the same set
    /// gates reading and writing, so anything not listed here is a file the assistant has to ask a person to
    /// change. The platform project formats are all text (.pbxproj, .xcconfig, .storyboard, .entitlements,
    /// .aidl, .pro), and an assistant that cannot edit them cannot help with that platform. An .svg is listed
    /// for that same reason even though it draws something: it is an XML document, no vision endpoint takes a
    /// vector file as an image, and seeing it therefore means reading its markup.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aidl", ".axaml", ".axproj", ".bat", ".c", ".cc", ".cfg", ".cmake", ".cpp", ".cs", ".css", ".cxx",
        ".def", ".editorconfig", ".entitlements", ".filters", ".frag", ".gitattributes", ".gitignore", ".glsl",
        ".go", ".gradle", ".h", ".hlsl", ".hpp", ".html", ".in", ".ini", ".ipp", ".java", ".js", ".json", ".kt",
        ".lua", ".m", ".md", ".metal", ".mm", ".patch", ".pbxproj", ".plist", ".pro", ".props", ".properties",
        ".ps1", ".py", ".rc", ".rs", ".sh", ".sln", ".csproj", ".storyboard", ".strings", ".stringsdict", ".swift",
        ".svg", ".targets", ".tmpl", ".toml", ".ts", ".tsx", ".txt", ".vcxproj", ".vert", ".xaml", ".xcconfig",
        ".xib", ".xml", ".yaml", ".yml",
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CMakeLists.txt", ".gitignore", ".gitattributes", ".clang-format", "Makefile", "Dockerfile", "LICENSE",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", "bin", "build", "dist", "node_modules", "obj", "out",
    };

    public static bool IsTextFile(string fileName)
        => TextExtensions.Contains(Path.GetExtension(fileName)) || TextFileNames.Contains(fileName);

    /// <summary>The write allowlist is the read allowlist: nothing outside it is ever written, so a model that
    /// wants to touch a binary or a build artifact has to ask the user instead.</summary>
    public static bool IsWritableText(string fileName) => IsTextFile(fileName);

    public static bool IsExcludedDirectory(string directoryName) => ExcludedDirectories.Contains(directoryName);

    /// <summary>
    /// The one spelling of a workspace directory, for anything that has to decide whether two stored paths name
    /// the same place. A surface that groups sessions by the directory they work in cannot compare the strings as
    /// typed: <c>d:\dev\ws</c>, <c>D:\DEV\WS\</c> and <c>D:/dev/ws</c> are one project, and splitting them would
    /// show the same work twice. Windows folds case because that is what the filesystem already does — the same
    /// choice <see cref="IsProtected"/> and the state-file mutex key make — while every other host keeps paths
    /// byte-for-byte, where case is part of the name.
    /// </summary>
    public static string? CanonicalRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var full = root.Trim();
        try
        {
            full = Path.GetFullPath(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // A path this build cannot resolve is still a place the user named, and the group label has to read
            // it out. Folding it to nothing would move that session into the workspace-less bucket and hide the
            // fact that its directory is the problem.
            return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        }

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Two spellings have no meaning once their separator is gone: "\" is nobody's path and "D:" names the
        // current directory on D rather than the root. A workspace sitting on a drive root is unusual, and it is
        // exactly the case where a bogus key would look like a real one.
        if (trimmed.Length == 0 || trimmed.EndsWith(':')) trimmed += Path.DirectorySeparatorChar;
        return OperatingSystem.IsWindows() ? trimmed.ToUpperInvariant() : trimmed;
    }

    /// <summary>
    /// How many directories one session may hold beyond its workspace. A hand-sized number, the same shape as
    /// <see cref="ToolTrust.Limit"/>: a grant is a person's decision, and a writer nobody is watching must not be
    /// able to grow the session file without a bound.
    /// </summary>
    public const int MaxGrantedRoots = 8;

    /// <summary>
    /// The list a session's grants are read and written through, in one place because four callers touch it — the
    /// session store on load and on save, the request that builds a sandbox, the tool that adds a directory, and
    /// the UI that revokes one — and they must not disagree about what an entry means.
    ///
    /// <para>An entry has to be a directory this build can resolve and reach, and one the guards do not already
    /// refuse: a list that kept the data directory would be a list every later reader has to re-check, and the
    /// check would eventually be forgotten somewhere. The user's own spelling is what survives — the canonical key
    /// only decides whether two entries are the same directory. <paramref name="guards"/> is optional because the
    /// session store normalizes on load and on save without knowing the engine roots; every path is graded against
    /// the guards again when it is used, which is the floor under both.</para>
    /// </summary>
    public static List<string> NormalizeRoots(IEnumerable<string>? roots, WorkspaceGuards? guards = null)
    {
        var kept = new List<string>();
        if (roots is null) return kept;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (kept.Count >= MaxGrantedRoots) break;
            if (string.IsNullOrWhiteSpace(root)) continue;

            string full;
            try
            {
                full = Path.GetFullPath(root.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                continue;
            }

            if (!Directory.Exists(full)) continue;
            if (guards is not null && IsProtected(full, guards)) continue;
            // Folded to compare and never to store: `D:\dev\ws` and `d:/DEV/ws` are one directory, and a list that
            // showed them twice would offer the same revoke button twice.
            if (CanonicalRoot(full) is not { } key || !seen.Add(key)) continue;
            kept.Add(full);
        }

        return kept;
    }

    public static WorkspacePath ResolveRead(WorkspaceSandbox sandbox, string relative, WorkspaceGuards guards)
    {
        var resolved = Resolve(sandbox, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        if (!File.Exists(resolved.Full)) return WorkspacePath.Refuse(WorkspacePathVerdict.NotAFile, resolved.Display);
        return resolved;
    }

    /// <summary>The single-root spelling: one workspace and nothing granted, which is what an assertion naming one
    /// directory means. A request that has grants must hand the <see cref="WorkspaceSandbox"/> in — through this
    /// overload every path outside the workspace would be graded against a list that does not have it.</summary>
    public static WorkspacePath ResolveRead(string? root, string relative, WorkspaceGuards guards)
        => ResolveRead(WorkspaceSandbox.For(root), relative, guards);

    public static WorkspacePath ResolveWrite(WorkspaceSandbox sandbox, string relative, WorkspaceGuards guards)
    {
        var resolved = Resolve(sandbox, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        if (Directory.Exists(resolved.Full)) return WorkspacePath.Refuse(WorkspacePathVerdict.NotAFile, resolved.Display);
        if (!IsWritableText(Path.GetFileName(resolved.Full)))
            return WorkspacePath.Refuse(WorkspacePathVerdict.ExtensionNotAllowed, resolved.Display);
        return resolved;
    }

    public static WorkspacePath ResolveWrite(string? root, string relative, WorkspaceGuards guards)
        => ResolveWrite(WorkspaceSandbox.For(root), relative, guards);

    /// <summary>A directory inside the sandbox, for the read-only listing, search and glob tools. An empty path or
    /// <c>"."</c> means the workspace itself: those tools start where the user pointed the session, and making the
    /// model name the root again would only produce a refusal it cannot act on. A granted directory has no
    /// shorthand — "." would have more than one answer — so it is named absolutely.</summary>
    public static WorkspacePath ResolveDirectory(WorkspaceSandbox sandbox, string? relative, WorkspaceGuards guards)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative is "." or "./")
        {
            if (sandbox.Roots.Count == 0 || sandbox.Primary is not { } primary)
                return WorkspacePath.Refuse(WorkspacePathVerdict.NoWorkspace, relative ?? "");
            if (!Directory.Exists(primary))
                return WorkspacePath.Refuse(WorkspacePathVerdict.MissingWorkspace, relative ?? "");

            var workspaceRoot = Path.GetFullPath(primary);
            if (IsProtected(workspaceRoot, guards))
                return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, workspaceRoot);
            return ContainsReparsePoint(workspaceRoot, workspaceRoot)
                ? WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, workspaceRoot)
                : new WorkspacePath(WorkspacePathVerdict.Allowed, workspaceRoot, "", workspaceRoot);
        }

        var resolved = Resolve(sandbox, relative, guards);
        if (!resolved.IsAllowed) return resolved;
        if (!Directory.Exists(resolved.Full))
            return WorkspacePath.Refuse(WorkspacePathVerdict.NotADirectory, resolved.Display);
        return resolved;
    }

    public static WorkspacePath ResolveDirectory(string? root, string? relative, WorkspaceGuards guards)
        => ResolveDirectory(WorkspaceSandbox.For(root), relative, guards);

    /// <summary>
    /// The directory a command would run in, checked before the shell starts. It is the same resolution every
    /// file tool goes through — so "this command stays inside a directory the person allowed" and "this command
    /// costs a card" are one expression rather than two that can drift apart — asked of a directory argument that
    /// is empty for the workspace itself.
    /// </summary>
    public static WorkspacePathVerdict VerifyCommandRoot(WorkspaceSandbox sandbox, string? root, WorkspaceGuards guards)
        => ResolveDirectory(sandbox, root, guards).Verdict;

    public static WorkspacePathVerdict VerifyCommandRoot(string? root, WorkspaceGuards guards)
        => VerifyCommandRoot(WorkspaceSandbox.For(root), root, guards);

    /// <summary>Where a directory a card names is pointing, read against the sandbox the session already has and
    /// the projects Hub already knows. Parsing the arguments is the app's job; judging the path is Core's, so what a
    /// card says about a directory can be asserted without a window. The tiers are deliberately not three names for
    /// "how far away" the directory is — they are three names for
    /// <b>who chose it</b>: the session's own subdirectory (the user's choice, narrowed), a registered project
    /// (a directory the user already pointed Hub at), or nobody's.</summary>
    public static WorkspaceTarget ClassifyWorkspaceTarget(string? path, string? currentRoot,
        IReadOnlyList<string>? projectPaths, WorkspaceGuards guards)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return WorkspaceTarget.Unreadable;
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return WorkspaceTarget.Unreadable;
        }

        // A protected root is refused by the tool itself in every mode, so the gate never gets to be lenient about
        // it; classifying it as anything but unreadable would let a card promise a sandbox the write will refuse.
        if (IsProtected(full, guards)) return WorkspaceTarget.Unreadable;

        var wanted = CanonicalRoot(full);
        var current = CanonicalRoot(currentRoot);
        if (wanted is null) return WorkspaceTarget.Unreadable;
        if (current is not null)
        {
            if (string.Equals(wanted, current, StringComparison.Ordinal)) return WorkspaceTarget.SameAsSandbox;
            // Both sides in the canonical spelling: `IsInside` compares byte-for-byte, and the fold that makes
            // `D:\DEV\WS` and `d:\dev\ws` one directory has to happen before it, not inside it.
            if (IsInside(wanted, current, StringComparison.Ordinal)) return WorkspaceTarget.InsideSandbox;
        }

        if (projectPaths is { Count: > 0 } known
            && known.Any(project => string.Equals(CanonicalRoot(project), wanted, StringComparison.Ordinal)))
            return WorkspaceTarget.RegisteredProject;
        return WorkspaceTarget.Other;
    }

    public static bool IsProtected(string fullPath, WorkspaceGuards guards)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (IsInside(fullPath, guards.DataRoot, comparison)) return true;
        foreach (var engineRoot in guards.EngineRoots)
            if (IsInside(fullPath, engineRoot, comparison)) return true;

        // .git is refused segment by segment, not just as the workspace root: rewriting history or hooks is
        // never what a coding request meant.
        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (segment.Equals(".git", StringComparison.Ordinal)) return true;
        return false;
    }

    private static WorkspacePath Resolve(WorkspaceSandbox sandbox, string relative, WorkspaceGuards guards)
    {
        if (sandbox.Roots.Count == 0) return WorkspacePath.Refuse(WorkspacePathVerdict.NoWorkspace, relative ?? "");
        if (string.IsNullOrWhiteSpace(relative)) return WorkspacePath.Refuse(WorkspacePathVerdict.EscapesWorkspace, "");

        // A rooted path is not a workspace-relative one: it is addressed against the directories this session
        // has, and if none holds it the answer names the way out — a grant — rather than pretending the spelling
        // was the problem.
        if (Path.IsPathRooted(relative)) return ResolveAmongRoots(sandbox, relative, guards);

        if (sandbox.Primary is not { } primary)
            return WorkspacePath.Refuse(WorkspacePathVerdict.NoWorkspace, relative);
        if (!Directory.Exists(primary))
            return WorkspacePath.Refuse(WorkspacePathVerdict.MissingWorkspace, relative);

        var workspaceRoot = Path.GetFullPath(primary);
        string full;
        try
        {
            full = PackageInstaller.SafePath(workspaceRoot, relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        }
        catch (InvalidDataException)
        {
            return WorkspacePath.Refuse(WorkspacePathVerdict.EscapesWorkspace, relative);
        }

        if (IsProtected(full, guards)) return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, relative);
        if (ContainsReparsePoint(full, workspaceRoot)) return WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, relative);
        return new WorkspacePath(WorkspacePathVerdict.Allowed, full, Path.GetRelativePath(workspaceRoot, full), workspaceRoot);
    }

    /// <summary>
    /// An absolute path, graded against every root this session has. The protected locations are asked first and
    /// on their own: a grant must never open one, and a refusal that said "not one of your roots" about the Hub
    /// data directory would send the model off to request a grant it can never be given.
    ///
    /// <para>The reparse walk stops at the root that matched rather than at the workspace, because the directory a
    /// person granted is the boundary now: a link between it and the file is a way out of <i>that</i> directory,
    /// which is exactly the thing the walk exists to refuse.</para>
    /// </summary>
    private static WorkspacePath ResolveAmongRoots(WorkspaceSandbox sandbox, string path, WorkspaceGuards guards)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return WorkspacePath.Refuse(WorkspacePathVerdict.EscapesWorkspace, path);
        }

        if (IsProtected(full, guards)) return WorkspacePath.Refuse(WorkspacePathVerdict.ProtectedRoot, path);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in sandbox.Roots)
        {
            bool inside;
            try
            {
                inside = IsInside(full, root, comparison);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // A root this build cannot resolve holds nothing; the next one still gets its turn.
                continue;
            }

            if (!inside) continue;
            if (ContainsReparsePoint(full, root)) return WorkspacePath.Refuse(WorkspacePathVerdict.ReparsePoint, path);
            return new WorkspacePath(WorkspacePathVerdict.Allowed, full, Path.GetRelativePath(root, full), root,
                !SameRoot(root, sandbox.Primary, comparison));
        }

        return WorkspacePath.Refuse(WorkspacePathVerdict.OutsideRoots, path);
    }

    /// <summary>Whether one root is the workspace itself. Only this decides which spelling a path is shown by: the
    /// workspace keeps the relative one every older transcript uses, a grant has to carry its directory.</summary>
    private static bool SameRoot(string root, string? primary, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(primary)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(primary).TrimEnd(Path.DirectorySeparatorChar), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Walks the existing ancestors of <paramref name="path"/> up to <paramref name="stopAt"/>, refusing
    /// any directory link. A junction inside the workspace would otherwise point a "contained" write outside it.
    /// Ancestors that do not exist are stepped over: asking a directory that is not there for its attributes
    /// answers with an error value whose bits include the reparse flag, and "create this new file in a folder the
    /// project has not grown yet" is the ordinary case for a write, not a link.</summary>
    private static bool ContainsReparsePoint(string path, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
        for (; directory != null; directory = directory.Parent)
        {
            if (!directory.Exists) continue;
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (directory.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(stop, StringComparison.Ordinal)) break;
        }
        return false;
    }

    private static bool IsInside(string fullPath, string? root, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison) || fullPath.Equals(prefix.TrimEnd(Path.DirectorySeparatorChar), comparison);
    }

    /// <summary>
    /// The sentence the model receives. Each one states the fact, names the alternative, and forbids the retry —
    /// the same shape as <see cref="ToolApprovalResults.Denied"/>, because a refusal without "do not retry" is
    /// an invitation to try nine more spellings of the same path.
    /// </summary>
    public static string ResultFor(WorkspacePathVerdict verdict, string detail) => verdict switch
    {
        WorkspacePathVerdict.NoWorkspace =>
            "This session has no working directory, so a relative path has nothing to be relative to. Absolute "
            + "paths still work inside a directory the user granted this session. Ask the user to name one from the "
            + "workspace menu beside the composer — only they can set it. Do not guess a directory and do not retry "
            + "this call.",
        WorkspacePathVerdict.MissingWorkspace =>
            $"This session's working directory '{detail}' no longer exists, or it has been renamed. Absolute paths "
            + "still work inside a directory the user granted this session. Ask the user to name the directory "
            + "again from the workspace menu beside the composer — only they can set it. Do not guess a directory "
            + "and do not retry this call.",
        WorkspacePathVerdict.EscapesWorkspace =>
            $"Refused: '{detail}' is not a workspace-relative path. Paths must be relative to the workspace and "
            + "must not use '..', a drive letter or a rooted path. Do not retry another spelling of this path; if "
            + "the user's own task named another directory, ask for it with request_directory, otherwise ask the "
            + "user to widen the workspace.",
        WorkspacePathVerdict.OutsideRoots =>
            $"Refused: '{detail}' is outside the session workspace and outside every directory the user granted "
            + "this session. If the user's own task named that directory, call request_directory with its "
            + "absolute path — one approval card opens it for this session; if it did not, work inside the "
            + "workspace. Do not retry another spelling of this path.",
        WorkspacePathVerdict.ProtectedRoot =>
            $"Refused: '{detail}' is inside a protected location (the Hub data directory, an engine installation "
            + "or a .git directory). Those are read-only for the assistant in every approval mode, and a granted "
            + "directory does not open them. Do not retry; tell the user what you needed and let them do it.",
        WorkspacePathVerdict.ReparsePoint =>
            $"Refused: '{detail}' resolves through a symbolic link or junction, which could point outside the "
            + "workspace. Do not retry through the link; ask the user for the real path.",
        WorkspacePathVerdict.ExtensionNotAllowed =>
            $"Refused: '{detail}' is not a text file the assistant may write (extension not in the allowlist). "
            + "Do not retry with a different extension; ask the user to make this change themselves.",
        WorkspacePathVerdict.NotAFile =>
            $"Refused: '{detail}' is not a readable file (it is missing or a directory). List the directory or "
            + "ask the user for the correct path; do not guess another spelling.",
        WorkspacePathVerdict.NotADirectory =>
            $"Refused: '{detail}' is not a directory inside the session's directories (it is missing, or it is a "
            + "file). Call list_directory on its parent to see what is there; do not retry another spelling of it.",
        WorkspacePathVerdict.AlreadyExists =>
            $"Refused: '{detail}' already exists, so it cannot be created with an empty old_string. Call "
            + "read_file first and edit it with the exact text to replace. Do not retry an empty old_string.",
        _ => "The path was accepted.",
    };
}
