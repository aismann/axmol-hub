using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;
internal sealed class ShellCheckSuite
{
    private readonly Action<bool, string> Check;
    private readonly CheckReport _report;
    internal ShellCheckSuite(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
        _report = report;
        var shell = new ShellLayoutChecks(report, check, fixture);
        var providers = new ShellProvidersChecks(report, check, fixture);
        var assistant = new ShellAssistantChecks(report, check, fixture);
        var sessions = new ShellSessionsChecks(report, check, fixture);
        var transcript = new ShellTranscriptChecks(report, check, fixture);
        var decisions = new ShellDecisionsChecks(report, check, fixture);
        var approvals = new ShellApprovalsChecks(report, check, fixture);
        var context = new ShellContextChecks(report, check, fixture);
        var platform = new ShellPlatformChecks(report, check, fixture);
        var requestStability = new ShellRequestStabilityChecks(report, check, fixture);
        var scrollDrag = new ShellScrollDragChecks(report, check, fixture);
        var projectionIdentity = new ShellProjectionIdentityChecks(report, check, fixture);
        var transcriptBenchmark = new ShellTranscriptBenchmarkChecks(report, check, fixture);
        var about = new ShellAboutChecks(report, check, fixture);
        var compacting = new ShellCompactingChecks(report, check, fixture);
        var openMeasurements = new ShellOpenMeasurementsChecks(report, check, fixture);
        var transcriptCost = new ShellTranscriptCostChecks(report, check, fixture);
        var contextBoundary = new ShellContextBoundaryChecks(report, check, fixture);
        var modelSwitch = new ShellModelSwitchChecks(report, check, fixture);
        var virtualScroll = new ShellVirtualScrollChecks(report, check, fixture);
        var projectionShadow = new ShellProjectionShadowChecks(report, check, fixture);
        var inlineCode = new ShellInlineCodeChecks(report, check, fixture);
        var codeBlocks = new ShellCodeBlocksChecks(report, check, fixture);
        assistant.InlineCode = inlineCode;
        assistant.CodeBlocks = codeBlocks;
        assistant.Shell = shell;
        assistant.Sessions = sessions;
        assistant.Approvals = approvals;
        assistant.Decisions = decisions;
        assistant.Context = context;
        assistant.Compacting = compacting;
        assistant.Providers = providers;
        assistant.Transcript = transcript;
        assistant.ScrollDrag = scrollDrag;
        assistant.TranscriptCost = transcriptCost;
        assistant.ContextBoundary = contextBoundary;
        assistant.ModelSwitch = modelSwitch;
        assistant.About = about;
        transcriptCost.VirtualScroll = virtualScroll;
        transcriptCost.ProjectionIdentity = projectionIdentity;
        transcriptCost.ProjectionShadow = projectionShadow;
        transcriptCost.OpenMeasurements = openMeasurements;
        transcriptCost.RequestStability = requestStability;
        transcriptCost.TranscriptBenchmark = transcriptBenchmark;
        Platform = platform;
        Providers = providers;
        Shell = shell;
        TranscriptBenchmark = transcriptBenchmark;
        TranscriptCost = transcriptCost;
        Context = context;
        ContextBoundary = contextBoundary;
        RequestStability = requestStability;
        Assistant = assistant;
        Sessions = sessions;
    }

    internal ShellPlatformChecks Platform { private get; set; } = null!;
    internal ShellProvidersChecks Providers { private get; set; } = null!;
    internal ShellLayoutChecks Shell { private get; set; } = null!;
    internal ShellTranscriptBenchmarkChecks TranscriptBenchmark { private get; set; } = null!;
    internal ShellTranscriptCostChecks TranscriptCost { private get; set; } = null!;
    internal ShellContextChecks Context { private get; set; } = null!;
    internal ShellContextBoundaryChecks ContextBoundary { private get; set; } = null!;
    internal ShellRequestStabilityChecks RequestStability { private get; set; } = null!;
    internal ShellAssistantChecks Assistant { private get; set; } = null!;
    internal ShellSessionsChecks Sessions { private get; set; } = null!;

    internal async Task RunAsync(string scratchRoot)
    {
        var gitWorkflow = new ShellGitWorkflowChecks(_report, Check);
        if (Environment.GetEnvironmentVariable("AXMOL_GIT_WORKFLOW_CHECK_ONLY") == "1")
        {
            await gitWorkflow.VerifyAsync();
            return;
        }
        // The starting point must be Chinese: the assertions below hard-code "this is a Chinese UI".
        // App forces Chinese in verification mode; if that forcing is removed, this blows up
        // immediately rather than only on some machine whose language is English.
        // Note we assert ChineseLanguage, not DefaultLanguage: the latter is the **cold-start**
        // language, English since 2026-10-03, no longer the same thing as "self-check starting
        // language".
        Check(HubStrings.Language == HubTexts.ChineseLanguage, "自检起步语言被强制为中文（否则断言会随用户设置漂移，实测 " + HubStrings.Language + "）");
        Platform.CheckStringDefinition();
        await Providers.VerifyDeepSeekWebApiTransportAsync();
        Platform.CheckWindowsTaskbarBadge();
        Platform.CheckWindowsToastInterop();
        Platform.CheckMacNotificationInterop();
        await Platform.CheckCjkFontNoticeAsync();
        Platform.CheckEveryXamlResourceKeyResolves();
        var shell = Shell.CheckShell(scratchRoot);
        var diffSelection = new ShellDiffSelectionChecks(Check);
        if (Environment.GetEnvironmentVariable("AXMOL_DIFF_SELECTION_CHECK_ONLY") == "1")
        {
            await diffSelection.VerifyAsync(shell);
            shell.Close();
            return;
        }
        await Shell.CheckProjectTrashFlowAsync(shell.Workspace, scratchRoot);
        if (Environment.GetEnvironmentVariable("AXMOL_VIRTUAL_CHECK_ONLY") == "1")
        {
            var panel = shell.OpenAssistant();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (Environment.GetEnvironmentVariable("AXMOL_VIRTUAL_BENCHMARK_ONLY") == "1")
                TranscriptBenchmark.RunTranscriptBenchmark(shell, panel);
            else
                TranscriptCost.CheckTranscriptCost(shell, panel);
            shell.Close();
            return;
        }

        if (Environment.GetEnvironmentVariable("AXMOL_COMPACTION_CHECK_ONLY") == "1")
        {
            var panel = shell.OpenAssistant();
            var provider = shell.Chat.AddProvider("Compaction fixture", "https://offline.invalid/v1", "check-model", null)!;
            await Context.CheckAutoCompactionAsync(shell, panel, provider.Id);
            await ContextBoundary.CheckContextBoundaryAsync(shell, panel);
            RequestStability.CheckRequestStability(shell, panel);
            shell.Close();
            return;
        }

        await Shell.CheckInstallsPageAsync(scratchRoot, shell);
        await Shell.CheckSettingsPageAsync(scratchRoot, shell);
        Shell.CheckSettingsOverlay(shell);
        await Shell.CheckDrawnDialogsAsync(shell);
        Shell.CheckHostShellCard(shell);
        await Assistant.CheckAssistantAsync(scratchRoot, shell);
        await gitWorkflow.VerifyAsync();
        await diffSelection.VerifyAsync(shell);
        Sessions.CheckWorkspaceGroups(scratchRoot, shell);
        await Shell.CheckDataRootSwitch(scratchRoot, shell);
    }
}
