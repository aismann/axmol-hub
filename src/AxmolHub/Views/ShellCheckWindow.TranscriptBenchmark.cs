using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

public partial class ShellCheckWindow
{
    /// <summary>Opt-in UI baseline, deliberately outside timed pass/fail assertions. Sessions are built in one
    /// batch in memory: this measures the current UI path, not per-turn saves or cold disk loads. The shell suite
    /// has already initialized the process, so every sample is explicitly process-warm.</summary>
    private void RunTranscriptBenchmark(MainWindow shell, ChatPanel panel)
    {
        if (Environment.GetEnvironmentVariable("AXMOL_TRANSCRIPT_BENCHMARK") != "1") return;
        var report = new StringBuilder();
        report.AppendLine("Transcript UI baseline v1; process-warm; registry-cached; Loaded proxy, not presentation");
#if DEBUG
        report.AppendLine("configuration=Debug (use Release for performance comparisons)");
#else
        report.AppendLine("configuration=Release");
#endif
        report.AppendLine($"os={RuntimeInformation.OSDescription}; runtime={RuntimeInformation.FrameworkDescription}; arch={RuntimeInformation.ProcessArchitecture}; cpus={Environment.ProcessorCount}");
        report.AppendLine("percentile=nearest-rank; samples=5; storage, scrolling and cold startup are NOT measured");
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var alternate = chat.StartConversation();
        // Pinned for every sample: the window is panel state, and inheriting a width an earlier check grew would
        // make the summaries incomparable between runs.
        panel.SetPaintWindowForCheck(ChatPanel.FirstPaintTurnsForCheck);
        try
        {
            foreach (var count in new[] { 1_000, 10_000, 100_000 })
            foreach (var shape in new[] { "plain", "tools", "code-table", "huge-result" })
            {
                var session = chat.StartConversation();
                try
                {
                    var largePayload = new string('x', 256 * 1024);
                    for (var index = 0; index < count; index++)
                    {
                        var callId = "benchmark-" + (index / 2).ToString(CultureInfo.InvariantCulture);
                        session.Messages.Add(shape switch
                        {
                            "tools" when index % 2 == 0 => ChatTurn.FunctionCall(callId, "run_command", "{\"command\":\"git status\"}"),
                            "tools" => ChatTurn.FunctionResult(callId, "exit: 0\nworking tree clean"),
                            "huge-result" when index == count - 2 => ChatTurn.FunctionCall("huge", "run_command", "{\"command\":\"build\"}"),
                            "huge-result" when index == count - 1 => ChatTurn.FunctionResult("huge", largePayload),
                            "code-table" => new ChatTurn(ChatRoles.Assistant,
                                "A **formatted** reply.\n\n```cpp\nint main() { return 0; }\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |", DateTimeOffset.UnixEpoch)
                                { Reasoning = "Check the result before replying." },
                            _ => new ChatTurn(ChatRoles.Assistant, "A short deterministic reply.", DateTimeOffset.UnixEpoch),
                        });
                    }
                    var samples = new List<SessionOpenMeasurement>();
                    for (var sample = 0; sample < 5; sample++)
                    {
                        chat.OpenConversation(alternate.Id);
                        Dispatcher.UIThread.RunJobs();
                        chat.OpenConversation(session.Id);
                        var operation = chat.CurrentSessionOpen!;
                        shell.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        samples.Add(operation);
                        report.AppendLine(FormattableString.Invariant($"shape={shape} turns={count} sample={sample} operation={operation.Id} completed={operation.Completed} reused={operation.Reused} painted={operation.PaintedTurns} messageRows={operation.Rows} activityRows={operation.ActivityRows} flowChildren={operation.FlowChildren} markdown={operation.MarkdownDocuments} render={operation.RenderMs:0.00}ms openToLoaded={operation.ObservedMs:0.00}ms"));
                    }
                    static double Percentile(IEnumerable<double> values, double percentile)
                    {
                        var ordered = values.OrderBy(value => value).ToArray();
                        return ordered[(int)Math.Ceiling(ordered.Length * percentile) - 1];
                    }
                    // Drawn and reused samples are kept separate: their distributions mean different things.
                    foreach (var group in samples.GroupBy(sample => sample.Reused))
                    {
                        report.AppendLine(FormattableString.Invariant(
                            $"summary shape={shape} turns={count} reused={group.Key} n={group.Count()} openToLoaded.p50={Percentile(group.Select(sample => sample.ObservedMs), .5):0.00}ms openToLoaded.p95={Percentile(group.Select(sample => sample.ObservedMs), .95):0.00}ms render.p50={Percentile(group.Select(sample => sample.RenderMs), .5):0.00}ms"));
                    }
                }
                finally { chat.DeleteConversation(session.Id); }
            }
        }
        finally
        {
            chat.DeleteConversation(alternate.Id);
            if (before is not null) chat.OpenConversation(before);
            panel.Reload();
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("data");
            File.WriteAllText("data/transcript-benchmark.txt", TextEol.ToLf(report.ToString()));
        }
    }
}
