using System.Text;

namespace AxmolHub.Core;

/// <summary>
/// The shell-command line reader every subcommand classifier shares: split one command line into segments, hand
/// back each segment's tokens, name the program a segment runs, and pull the payload out of a shell that was
/// started for one command.
///
/// <para>It is a separate type rather than a private half of <see cref="GitCommandGrades"/> because there is now
/// more than one thing to grade from the same string, and <b>the splitting cannot be duplicated</b>. Two
/// tokenizers means two answers for one line: <c>cmake --build build &amp;&amp; git status</c> is one command to a
/// person and would be two different commands to two classifiers — the git side seeing its read in the second
/// segment while the build side, splitting differently, sees nothing at all. Whatever is decided about a command
/// is decided over one segmentation of it, which is the only way "the worst segment wins" means the same thing in
/// every rule.</para>
///
/// <para>No I/O, never throws, and nothing here decides a permission: this answers <i>what is the program, what
/// are its arguments</i> and stops there.</para>
/// </summary>
public static class ShellCommandLine
{
    /// <summary>Segments past this are not looked at. A line with thirty separators is not one a person can
    /// approve anyway, and the cap keeps a pathological argument bounded.</summary>
    public const int MaxSegments = 32;

    /// <summary>How much of a command line is read. Every segment's head token arrives long before this, so the
    /// cut costs nothing in accuracy; the limit is on parsing an absurd argument.</summary>
    public const int MaxCommandCharacters = 8000;

    /// <summary>One parsed word of the command line. <see cref="Quoted"/> is kept because <c>"git commit"</c>
    /// is one argument to <c>echo</c> rather than a git call, while <c>"git" commit</c> really is one.</summary>
    public readonly record struct Token(string Text, bool Quoted);

    /// <summary>Shells whose command payload is itself a command line, and so worth parsing again.</summary>
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "cmd", "dash", "ksh", "pwsh", "powershell", "sh", "zsh",
    };

    /// <summary>Programs that only arrange somebody else's execution, so the verb to grade is the next token's.
    /// <c>env</c> and <c>nohup</c> are the ones a build script really uses, and <c>sudo</c> is the one that would
    /// otherwise hide a write behind the most privileged prefix available; without them <c>env git commit</c>
    /// would read as "a program called env ran".</summary>
    private static readonly HashSet<string> PassThroughNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "env", "nohup", "sudo", "time", "timeout",
    };

    /// <summary>Whether this program is a shell started to run one command line, whose payload therefore has to be
    /// graded as its own command rather than as "a shell ran".</summary>
    public static bool IsShell(string executableName) => ShellNames.Contains(executableName);

    /// <summary>Whether this program only arranges somebody else's execution.</summary>
    public static bool IsPassThrough(string executableName) => PassThroughNames.Contains(executableName);

    /// <summary>Split on the shell's sequence separators — <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>;</c>,
    /// <c>&amp;</c>, newline — with quoting respected, so a separator inside a commit message invents no segment.
    /// Redirect characters split too, which leaves heads like <c>2&gt;</c> and <c>1</c> that nobody recognises: a
    /// classifier grades those as "nothing I know", and the real verb is still read in its own segment.</summary>
    public static List<List<Token>> Segments(string command)
    {
        var segments = new List<List<Token>>();
        var current = new List<Token>();
        var buffer = new StringBuilder();
        var quotedInBuffer = false;

        void FlushToken()
        {
            if (buffer.Length == 0) return;
            current.Add(new Token(buffer.ToString(), quotedInBuffer));
            buffer.Clear();
            quotedInBuffer = false;
        }

        void FlushSegment()
        {
            FlushToken();
            if (current.Count == 0) return;
            segments.Add(current);
            current = new List<Token>();
        }

        for (var i = 0; i < command.Length; i++)
        {
            if (segments.Count >= MaxSegments) break;
            var c = command[i];
            if (c is '"' or '\'')
            {
                quotedInBuffer = true;
                var quote = c;
                i++;
                while (i < command.Length && command[i] != quote)
                {
                    // Only a double-quoted string honours the backslash escape; inside single quotes every
                    // character is literal, which is git's own rule and the one a shell follows.
                    if (quote == '"' && command[i] == '\\' && i + 1 < command.Length && command[i + 1] == quote) i++;
                    buffer.Append(command[i]);
                    i++;
                }

                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (c is '\n' or '\r') FlushSegment();
                else FlushToken();
                continue;
            }
            if (c is ';' or '|' or '&' or '<' or '>')
            {
                FlushToken();
                if ((c == '|' || c == '&') && i + 1 < command.Length && command[i + 1] == c) i++;
                FlushSegment();
                continue;
            }
            buffer.Append(c);
        }
        FlushSegment();
        return segments;
    }

    /// <summary>Read <paramref name="command"/> and split it, applying the same bounds every classifier uses.</summary>
    public static List<List<Token>> SegmentsOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        var text = command.Trim();
        if (text.Length > MaxCommandCharacters) text = text[..MaxCommandCharacters];
        return Segments(text);
    }

    /// <summary>The index of the token a segment actually runs: <c>LC_ALL=C cmake --build build</c> starts at the
    /// second token, because an environment assignment before the program is not the program.</summary>
    public static int HeadIndex(List<Token> segment)
    {
        var index = 0;
        while (index < segment.Count && IsEnvironmentAssignment(segment[index])) index++;
        return index;
    }

    /// <summary>Whether this token is an environment assignment (<c>LC_ALL=C</c>) rather than a program or a flag.</summary>
    public static bool IsEnvironmentAssignment(Token token)
    {
        if (token.Quoted) return false;
        var at = token.Text.IndexOf('=');
        if (at <= 0) return false;
        for (var i = 0; i < at; i++)
        {
            var c = token.Text[i];
            // A leading '-' makes this an option that happens to carry a value (--pretty=format:%h), not a
            // variable, and options are never skipped as a prefix.
            if (char.IsLetterOrDigit(c) || c is '_' or ':' or '$' or '.') continue;
            return false;
        }
        return true;
    }

    /// <summary>The program a segment runs, as a bare lower-cased name: <c>git</c>, <c>git.exe</c>,
    /// <c>/usr/bin/git</c> and <c>C:\Program Files\Git\bin\git.exe</c> name one program, and PowerShell's call
    /// operator is decoration on it.</summary>
    public static string ExecutableName(string text)
    {
        var trimmed = text.Trim().TrimStart('&', '.');
        if (trimmed.Length == 0) return "";
        var name = Path.GetFileName(trimmed.Replace('\\', '/'));
        if (name.Length >= 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// The command payload of a shell started for one command: <c>pwsh -c "cmake --build build"</c> is that build
    /// as far as any classifier here is concerned, because treating it as "a shell ran and nothing happened" is
    /// exactly the route a model takes around a rule it dislikes. Everything after the flag is taken, so the
    /// unquoted <c>cmd /c git push</c> form finds its verb too.
    ///
    /// Two blind spots are accepted and named, because closing them would cost a card for every script run: a
    /// payload held in a variable (<c>pwsh -c $cmd</c> after <c>$cmd="git push"</c>) and a script file
    /// (<c>bash deploy.sh</c>). Both sit on the read side of a ladder that only goes up, and the same shapes are
    /// invisible to every client's subcommand rules.
    /// </summary>
    public static string? CommandPayload(List<Token> segment, int rest)
    {
        for (var i = rest; i < segment.Count; i++)
        {
            var flag = segment[i].Text.ToLowerInvariant();
            if (flag is not ("-c" or "-lc" or "-command" or "/c" or "/lc")) continue;
            if (i + 1 >= segment.Count) return null;
            var first = segment[i + 1].Text;
            // A variable rather than a command line: nothing here can see inside it.
            if (first.Length == 0 || first[0] is '$' or '%') return null;
            var payload = new StringBuilder();
            for (var j = i + 1; j < segment.Count; j++)
            {
                if (payload.Length > 0) payload.Append(' ');
                payload.Append(segment[j].Text);
            }
            return payload.ToString();
        }
        return null;
    }
}
