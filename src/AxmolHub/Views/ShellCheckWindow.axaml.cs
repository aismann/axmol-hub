using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// P5 shell and localization runtime self-check (<c>verify shell</c>).
///
/// Every failure mode it guards against is **silent** — none can be caught at build time:
/// <list type="bullet">
///   <item><c>{DynamicResource Foo}</c> in XAML missing its key → the label shows blank, no error;</item>
///   <item>copy not loaded into the resource dictionary before the window is constructed → same kind of blank;</item>
///   <item>navigation button events not wired → clicking does nothing and the program still exits 0;</item>
///   <item>pages rebuilt repeatedly → just "selection occasionally lost", almost impossible to spot with a few manual clicks;</item>
///   <item>changing language only touched the settings file without updating **existing controls**' text → looks like "settings saved but the UI didn't change".</item>
/// </list>
/// So here we assert the runtime object graph and real rendered frames, not "the code looks right".
/// </summary>
public partial class ShellCheckWindow : Window
{
    private readonly CheckReport _report = CheckReport.Collected();

    public ShellCheckWindow() => InitializeComponent();

    public void Run(IClassicDesktopStyleApplicationLifetime lifetime, string reportPath)
    {
        // The self-check must **never** use the real data root: it writes fixture state, while the
        // default data root is the user's own %LocalAppData%\AxmolHub\data. Overwriting the user's
        // engine list in one verification run is unacceptable, so a temp directory is used here,
        // matching how the foundation self-check uses one.
        // The settings file also lands in the temp directory — the settings page switching language
        // writes to disk, and using the default path would change the user's language.
        // The landing point is fixed under the repo's tmp/ (see ScratchDirectory): all artifacts of
        // one self-check stay in a single wholly-deletable place.
        var scratchRoot = ScratchDirectory.Resolve("shell-check", Guid.NewGuid().ToString("N"));

        // The run's own clock, reported on the RESULT line: the suite's cost is a budget (SuiteCeiling), and a
        // number read off the report is the only way to see it approaching that budget without timing a run by
        // hand. Started before the window is built so the figure covers everything the ceiling covers.
        var runClock = System.Diagnostics.Stopwatch.StartNew();

        var finished = false;

        void Finish()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            _report.Line(string.Format(CultureInfo.InvariantCulture,
                "RESULT: {0}/{1} passed in {2:0.0} s (ceiling {3:0} s)",
                _report.Passed, _report.Passed + _report.Failures, runClock.Elapsed.TotalSeconds, SuiteCeiling.TotalSeconds));

            try
            {
                _report.WriteReport(reportPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            foreach (var line in _report.Lines)
            {
                Console.WriteLine(line);
            }

            lifetime.Shutdown(_report.Succeeded ? 0 : 1);
        }

        var checksStarted = false;
        Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            checksStarted = true;
            try
            {
                UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                using var fixture = new ShellCheckFixture(this);
                var suite = new ShellCheckSuite(_report, Check, fixture);
                await suite.RunAsync(scratchRoot);
            }
            catch (Exception ex)
            {
                Check(false, "验证过程抛出异常: " + ex);
            }

            Finish();
        }, DispatcherPriority.Background);

        // Fallback: don't let the process hang forever. Two different failures look the same from here — a window
        // that never shows, and an assertion waiting on something that never arrives — and they used to be
        // reported as one, which turned a slow-but-complete run into a lie about the window.
        //
        // The ceiling has to clear the suite's own length, not just a stuck assertion's: measured 2026-10-08 the
        // full run finishes in 26–27 s warm and 37 s as a cold first run of a freshly built binary, and at 30 s it
        // reported "an assertion is stuck" for a suite that was merely finishing. A genuinely stuck check waits
        // forever, so headroom costs only the time before a real hang is named — and the constant below is what
        // the message quotes, so the two cannot drift apart again.
        DispatcherTimer.RunOnce(() =>
        {
            if (finished) return;
            Check(false, checksStarted
                ? $"自检在 {(int)SuiteCeiling.TotalSeconds} 秒内没有跑完：有断言卡在等待上，请检查最近加入的断言组"
                : $"窗口在 {(int)SuiteCeiling.TotalSeconds} 秒内没有触发 Opened，断言未执行");
            Finish();
        }, SuiteCeiling);
    }

    /// <summary>How long the whole check run may take before it is called stuck, rather than merely slow. Three
    /// times the slowest completed run on record; raise it with the suite, because a ceiling the suite can brush
    /// produces a failure that names the wrong cause.</summary>
    private static readonly TimeSpan SuiteCeiling = TimeSpan.FromSeconds(180);

    private void Check(bool ok, string message)
    {
        if (Environment.GetEnvironmentVariable("AXMOL_SHELL_TRACE") == "1")
        {
            Console.Error.WriteLine((ok ? "PASS " : "FAIL ") + message);
        }

        _report.Check(ok, message);
    }
}
