using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// Whether a shell command line builds something — or destroys the derived state a build depends on — decided
/// without running it.
///
/// <para>This is the classifier behind <see cref="ToolRisk.BuildCommand"/>, and it exists because of a failure the
/// user reported from their own machine: an assistant that decided to rebuild on its own, twice, while a compile
/// was already running. Two of them writing one output directory does not produce a slow build; it produces a tree
/// half new, and MSBuild's node reuse and <c>VSTest</c> hosts outlive the process tree a cancel kills
/// (<see cref="ProcessRunner"/>'s kill walks the tree that is still attached, and a detached build server is not in
/// it). Nothing later can undo that with a card, which is why this tier is the one that keeps asking.</para>
///
/// <para>Two halves, because both end in a tree no later approval can repair: <b>running a build</b>
/// (<c>cmake --build</c>, <c>msbuild</c>, <c>dotnet build</c>, <c>ninja</c>, the engine's <c>setup.ps1</c>) and
/// <b>breaking the cache</b> it writes into (<c>dotnet clean</c>, <c>cmake -U</c>, <c>git clean -fdx</c>, deleting
/// <c>build/</c>, <c>out/</c>, <c>.cxx/</c>, <c>CMakeFiles/</c> or <c>CMakeCache.txt</c>).</para>
///
/// <para>The ladder only goes <b>up</b>, exactly as <see cref="GitCommandGrades"/>'s does: a command this does not
/// recognise keeps the tier it had (<see cref="ToolRisk.WorkspaceCommand"/>), so an omission costs a card nobody
/// needed rather than blocking a build nobody asked for. The shapes it cannot see are named rather than pretended
/// about: a wrapper script that is not itself a builder (<c>cmd /c build.bat</c>) and a variable payload
/// (<c>pwsh -c $cmd</c>), both of which stay inside the sandbox and still take the change gate
/// (<see cref="WorkspaceTools.RunCommand"/>), so the worst an omission can do is skip the question, never let two
/// builds run at once.</para>
/// </summary>
public static class BuildCommandGrades
{
    /// <summary>Programs whose ordinary job in this product is to compile something. <c>dotnet</c> is absent: it is
    /// a host for many verbs, and only some of them build, so it is graded by its subcommand below.</summary>
    private static readonly HashSet<string> BuilderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmake", "msbuild", "ninja", "make", "mingw32-make", "xcodebuild", "ndk-build",
        "gradle", "gradlew", "gradlew.bat", "gradlew.sh", "setup.ps1", "setup.sh",
    };

    /// <summary><c>dotnet</c> verbs that run a compile or rewrite what a compile reads. <c>build</c>,
    /// <c>msbuild</c> and <c>publish</c> are the obvious ones; <c>test</c> and <c>run</c> compile first, which is
    /// what makes them the same hazard and not a convenience; <c>clean</c> and <c>restore</c> are here for the other
    /// half — they take the derived state out from under a build that is already running.</summary>
    private static readonly HashSet<string> DotnetBuildVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "msbuild", "publish", "test", "run", "clean", "restore",
    };

    /// <summary>Programs that delete. A recursive switch is not required: <c>rm CMakeCache.txt</c> breaks the cache
    /// just as reliably as <c>rm -rf build</c>, and it is the target that makes this a build question.</summary>
    private static readonly HashSet<string> DeleterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "del", "erase", "rd", "rmdir", "remove-item", "unlink",
    };

    /// <summary>Whether a tool call's <c>command</c> argument builds or breaks a build cache. Absent, blank or
    /// unparseable arguments answer <c>false</c>, which keeps the tier the call already had — the same rule
    /// <see cref="GitCommandGrades.CommandOf"/> follows, and for the same reason: an empty argument is not evidence
    /// of anything, and the tool body refuses it before it can run.</summary>
    public static bool CommandOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("command", out var value)
                   && value.ValueKind == JsonValueKind.String
                && IsBuild(value.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether a whole command line builds. Any segment that builds makes the line build, because
    /// <c>git status &amp;&amp; cmake --build build</c> is a rebuild whatever its first half reads like.</summary>
    public static bool IsBuild(string? command) => IsBuild(command, 0);

    private static bool IsBuild(string? command, int depth)
    {
        foreach (var segment in ShellCommandLine.SegmentsOf(command))
            if (GradeSegment(segment, depth)) return true;
        return false;
    }

    private static bool GradeSegment(List<ShellCommandLine.Token> segment, int depth)
    {
        var index = ShellCommandLine.HeadIndex(segment);
        if (index >= segment.Count) return false;

        var name = ShellCommandLine.ExecutableName(segment[index].Text);
        if (name.Length == 0) return false;
        if (IsBuilder(name, segment, index + 1)) return true;
        if (IsCacheDeletion(name, segment, index + 1)) return true;
        if (depth < 2)
        {
            // The route around the rule, read the way the git classifier reads it: a shell started for one command
            // ran that command, and `timeout 300 cmake --build build` is a compile behind a wrapper's own argument.
            if (ShellCommandLine.IsShell(name))
                return (ShellCommandLine.CommandPayload(segment, index + 1) is { } payload && IsBuild(payload, depth + 1))
                       // …and a shell pointed at a script file, which is how the engine's own setup is started
                       // (`pwsh -File setup.ps1 -android`): the payload flag is absent, the builder is an argument.
                       || GradeTail(segment, index + 1, depth + 1);
            if (ShellCommandLine.IsPassThrough(name)) return GradeTail(segment, index + 1, depth);
        }
        return false;
    }

    /// <summary>Scan past a pass-through wrapper's own options for the first program this knows.</summary>
    private static bool GradeTail(List<ShellCommandLine.Token> segment, int from, int depth)
    {
        for (var i = from; i < segment.Count; i++)
        {
            var token = segment[i];
            if (ShellCommandLine.IsEnvironmentAssignment(token)) continue;
            if (token.Text.StartsWith('-') && !token.Quoted) continue;
            var name = ShellCommandLine.ExecutableName(token.Text);
            if (name.Length == 0) continue;
            if (IsBuilder(name, segment, i + 1)) return true;
            if (IsCacheDeletion(name, segment, i + 1)) return true;
            if (depth >= 2) continue;
            if (ShellCommandLine.IsShell(name))
                return (ShellCommandLine.CommandPayload(segment, i + 1) is { } payload && IsBuild(payload, depth + 1))
                       || GradeTail(segment, i + 1, depth + 1);
            if (ShellCommandLine.IsPassThrough(name)) return GradeTail(segment, i + 1, depth + 1);
        }
        return false;
    }

    /// <summary>Whether this program, with these arguments, is a build being run.</summary>
    private static bool IsBuilder(string name, List<ShellCommandLine.Token> segment, int from)
    {
        // `dotnet` is decided by its verb, and `git` by its subcommand: `git clean` is the cache break the user
        // named, while every other git verb belongs to GitCommandGrades and its own ladder.
        if (string.Equals(name, "dotnet", StringComparison.Ordinal))
            return DotnetBuildVerbs.Contains(FirstVerb(segment, from));
        if (string.Equals(name, "git", StringComparison.Ordinal))
            return string.Equals(FirstVerb(segment, from), "clean", StringComparison.OrdinalIgnoreCase);
        if (!BuilderNames.Contains(name)) return false;
        // `cmake` is the one builder whose bare form is decoration: `cmake --version` and `cmake --help` read a
        // version, and a plain `cmake` prints usage. Configuring is a build — it is what writes CMakeCache.txt and
        // the whole CMakeFiles tree — so -B and --fresh count with --build.
        return !string.Equals(name, "cmake", StringComparison.Ordinal) || ConfiguresOrBuilds(segment, from);
    }

    /// <summary>Whether a deletion names a build's derived state rather than any old file.</summary>
    private static bool IsCacheDeletion(string name, List<ShellCommandLine.Token> segment, int from)
    {
        if (!DeleterNames.Contains(name)) return false;
        for (var i = from; i < segment.Count; i++)
            if (IsCachePath(segment[i].Text)) return true;
        return false;
    }

    /// <summary>The first argument that is an operation rather than an option: git's and dotnet's verb, which sits
    /// behind any number of flags (<c>git -c core.x=1 clean -fdx</c>, <c>dotnet --nologo build</c>).</summary>
    private static string FirstVerb(List<ShellCommandLine.Token> segment, int from)
    {
        for (var i = from; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text.StartsWith('-') && !token.Quoted)
            {
                // `git -C build clean -fdx`: -C names a repository, so its directory is the next token and the verb
                // is one further on still. -c's value is an assignment, which the scan below already skips.
                if (token.Text is "-C" or "--work-tree" or "--git-dir") i++;
                continue;
            }
            // `git -c core.autocrlf=false clean -fdx`: the option's value is not the verb either.
            if (ShellCommandLine.IsEnvironmentAssignment(token)) continue;
            return token.Text;
        }
        return "";
    }

    /// <summary><c>cmake</c> spellings that generate or compile: <c>--build</c>, a binary directory to configure
    /// (<c>-B</c>, <c>--fresh</c>), or a cache pattern to delete (<c>-U</c>). Everything else this build knows of
    /// (<c>--version</c>, <c>--help</c>, a bare call) only says something about CMake.</summary>
    private static bool ConfiguresOrBuilds(List<ShellCommandLine.Token> segment, int from)
    {
        for (var i = from; i < segment.Count; i++)
        {
            var flag = segment[i].Text;
            if (flag.StartsWith("-U", StringComparison.Ordinal)) return true;
            if (flag is "--build" or "--fresh" or "-B" or "--preset") return true;
        }
        return false;
    }

    /// <summary>Whether a path argument names the tree a build made: <c>build</c>, <c>build-windows</c>,
    /// <c>out</c>, <c>.cxx</c>, <c>CMakeFiles</c>, <c>CMakeCache.txt</c>. Only the last segment counts, so
    /// <c>rm src/main.cpp</c> stays a file question, and the name is matched rather than substring-scanned, so
    /// <c>rm rebuild-notes.md</c> does not become a build. A <c>-D</c>/<c>/s</c> style switch on the deleter is
    /// irrelevant: what makes this the cache is which directory it points at.</summary>
    private static bool IsCachePath(string text)
    {
        if (text.Length == 0 || text[0] == '-') return false;
        var name = Path.GetFileName(text.TrimEnd('/', '\\').Replace('\\', '/'));
        if (name.Length == 0) return false;
        name = name.ToLowerInvariant();
        return name is "out" or ".cxx" or "cmakefiles" or "cmakecache.txt"
               || name == "build" || (name.StartsWith("build", StringComparison.Ordinal)
                   && name.Length > 5 && name[5] is '-' or '_' or '.');
    }
}
