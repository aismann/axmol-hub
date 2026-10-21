namespace AxmolHub.Core;

/// <summary>
/// The one assertion sink the verification entry points write through: records <c>PASS</c>/<c>FAIL</c>, counts
/// them, optionally writes the lines as a report, and makes the exit code the failure count — the rule every
/// <c>--check-*</c> / <c>--verify-*</c> mode promises.
///
/// <para>It replaces eight hand-written copies that had drifted apart (two <c>PASS</c> spellings, one of them
/// counting skips). Internal on purpose: this is verification plumbing, not part of Core's API. It formats no
/// summary line — each window owns its own <c>RESULT:</c> wording, which is what people read in the report.</para>
/// </summary>
internal sealed class CheckReport
{
    private readonly List<string> _lines = [];
    private readonly string _passLabel;
    private readonly string _failLabel;
    private readonly string _skipLabel;
    private readonly Action<string>? _echo;

    /// <param name="echo">Where each line goes as it is recorded, or null to only collect it (a window prints
    /// its collected lines itself, after writing the report, so the file and the terminal agree).</param>
    public CheckReport(string passLabel, string failLabel, string skipLabel, Action<string>? echo = null)
    {
        _passLabel = passLabel;
        _failLabel = failLabel;
        _skipLabel = skipLabel;
        _echo = echo;
    }

    /// <summary>The terminal shape: <c>PASS: name</c> printed as the run goes, exit code = failures.</summary>
    public static CheckReport Console() => new("PASS: ", "FAIL: ", "SKIP: ", line => System.Console.WriteLine(line));

    /// <summary>The report shape: <c>PASS  name</c> lines collected for a file the caller writes at the end.</summary>
    public static CheckReport Collected() => new("PASS  ", "FAIL  ", "SKIP  ");

    public int Passed { get; private set; }

    public int Failures { get; private set; }

    /// <summary>Assertions that were not run, because the host or the fixture could not supply them. Counted
    /// apart from failures: a skip is a statement about the report's coverage, not about the product.</summary>
    public int Skipped { get; private set; }

    public bool Succeeded => Failures == 0;

    /// <summary>The number of failed assertions — the exit code every mode in this repository promises.</summary>
    public int ExitCode => Failures;

    /// <summary>The lines recorded so far, in order, ready for <see cref="WriteReport"/>.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Records one assertion and answers it, so a caller can write <c>if (!report.Check(...)) return;</c>.</summary>
    public bool Check(bool condition, string name)
    {
        if (condition)
        {
            Passed++;
            Add(_passLabel + name);
        }
        else
        {
            Failures++;
            Add(_failLabel + name);
        }

        return condition;
    }

    /// <summary>Records an assertion that could not run. Never counted as a failure.</summary>
    public void Skip(string name)
    {
        Skipped++;
        Add(_skipLabel + name);
    }

    /// <summary>Records a line that is not an assertion — a diagnostic, a header, or the summary the caller
    /// formats itself.</summary>
    public void Line(string text) => Add(text);

    /// <summary>Writes the collected lines as a check report: LF, one per line, end of file included.</summary>
    public void WriteReport(string path) => TextEol.WriteLines(path, _lines);

    private void Add(string line)
    {
        _lines.Add(line);
        _echo?.Invoke(line);
    }
}
