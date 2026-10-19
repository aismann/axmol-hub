using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        report.AppendLine("Transcript UI benchmark v2; process-warm; registry-cached; Loaded proxy, not presentation");
#if DEBUG
        report.AppendLine("configuration=Debug (use Release for performance comparisons)");
#else
        report.AppendLine("configuration=Release");
#endif
        report.AppendLine($"os={RuntimeInformation.OSDescription}; runtime={RuntimeInformation.FrameworkDescription}; arch={RuntimeInformation.ProcessArchitecture}; cpus={Environment.ProcessorCount}");
        report.AppendLine("percentile=nearest-rank; samples=5; storage and cold startup are NOT measured; scrolling uses synchronous layout plus dispatcher jobs");
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var alternate = chat.StartConversation();
        try
        {
            foreach (var count in new[] { 1_000, 10_000, 100_000 })
            foreach (var shape in new[] { "plain", "tools", "code-table", "huge-result" })
            {
                if (Environment.GetEnvironmentVariable("AXMOL_BENCHMARK_COUNT") is { } countFilter && count.ToString() != countFilter) continue;
                if (Environment.GetEnvironmentVariable("AXMOL_BENCHMARK_SHAPE") is { } selected && selected != shape) continue;
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
                        var realizedBefore = panel.VirtualRealizationsForCheck;
                        var allocated = GC.GetTotalAllocatedBytes(false);
                        chat.OpenConversation(session.Id);
                        var operation = chat.CurrentSessionOpen!;
                        shell.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        samples.Add(operation);
                        var openAllocation = GC.GetTotalAllocatedBytes(false) - allocated;
                        report.AppendLine(FormattableString.Invariant($"shape={shape} turns={count} sample={sample} operation={operation.Id} completed={operation.Completed} reused={operation.Reused} painted={operation.PaintedTurns} messageRows={operation.Rows} activityRows={operation.ActivityRows} flowChildren={operation.FlowChildren} markdown={operation.MarkdownDocuments} render={operation.RenderMs:0.00}ms openToLoaded={operation.ObservedMs:0.00}ms allocatedProcessBytes={openAllocation} realized={panel.RealizedItemCountForCheck} editors={panel.RealizedEditorCountForCheck} parked={panel.ParkedVirtualRowsForCheck} built={panel.VirtualRealizationsForCheck - realizedBefore}"));
                    }
                    if (shape == "tools")
                    {
                        panel.SetActivityGroupExpandedForCheck(0, true);
                        shell.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                    }
                    var firstRealized = new Dictionary<int, int>();
                    var scrollsReached = true;
                    var retainedBounded = true;
                    for (var pass = 0; pass < 3; pass++)
                    foreach (var index in new[] { 0, panel.VirtualItemCountForCheck / 2, panel.VirtualItemCountForCheck - 1 })
                    {
                        var allocation = GC.GetTotalAllocatedBytes(false);
                        var scrolling = Stopwatch.StartNew();
                        panel.ScrollVirtualItemForCheck(index);
                        shell.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        scrolling.Stop();
                        scrollsReached &= panel.VirtualIndexRealizedForCheck(index);
                        retainedBounded &= panel.RealizedItemCountForCheck is > 0 and < 150;
                        if (pass == 0) firstRealized[index] = panel.RealizedItemCountForCheck;
                        else if (pass == 2) retainedBounded &= panel.RealizedItemCountForCheck <= firstRealized[index] + 2;
                        report.AppendLine(FormattableString.Invariant($"scroll shape={shape} turns={count} pass={pass} index={index} elapsed={scrolling.Elapsed.TotalMilliseconds:0.00}ms realized={panel.RealizedItemCountForCheck} markdown={panel.MarkdownDocumentCountForCheck} editors={panel.RealizedEditorCountForCheck} allocatedProcessBytes={GC.GetTotalAllocatedBytes(false) - allocation} reached={panel.VirtualIndexRealizedForCheck(index)}"));
                    }
                    Check(scrollsReached && retainedBounded,
                        $"基准往返可达且回收稳定「{shape}/{count}」");
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
