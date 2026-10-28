using System.Globalization;
using Avalonia.Controls;
using Avalonia.Styling;
using AxmolHub.Core;
using Avalonia.Layout;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellModelSwitchChecks
{
    private readonly Action<bool, string> Check;
    internal ShellModelSwitchChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal void CheckModelSwitchNotice(MainWindow shell, ChatPanel panel)
    {
        const string model = "model-switch-x";
        var first = shell.Chat.AddProvider("Model switch A local", "http://localhost:11437/v1", model, null, out _);
        var second = shell.Chat.AddProvider("Model switch B local", "http://localhost:11438/v1", model, null, out _);
        Check(first is not null && second is not null, "模型切换提示的夹具建起了两个免密 provider");
        if (first is null || second is null) return;

        var session = shell.Chat.StartConversation(first.Id);
        shell.Chat.SeedTurnForCheck(session.Id, "切换前的问题");
        Relayout(shell, panel);
        var rowsBefore = panel.NoticeTurnRowsForCheck.Count;

        var picked = panel.SelectModelForCheck(second.Id, model);
        Relayout(shell, panel, measure: true);
        var rows = panel.NoticeTurnRowsForCheck;
        var row = rows.Count > 0 ? rows[^1] : null;
        var expected = string.Format(CultureInfo.CurrentCulture,
            HubStrings.Get("ModelChangedFormat"), second.Name + " · " + model);
        var rowText = row?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
        var stored = shell.Chat.StoredCopyForCheck(session.Id)?.Messages
            .LastOrDefault(turn => turn.Role == ChatRoles.Notice);
        var geometry = panel.NoticeTurnRowGeometryForCheck;
        Check(picked && rowsBefore == 0 && rows.Count == 1
              && row is { HorizontalAlignment: HorizontalAlignment.Center }
              && geometry is { } laid
              && Math.Abs((laid.Left + laid.Width / 2) - laid.FlowWidth / 2) <= 1
              && rowText == expected && stored?.Text == expected,
            "切换模型在会话里留下一条水平居中的提示行，行上与落盘的字都是「已切换模型：…」"
            + "（实际 " + rows.Count + " 行，对齐 " + row?.HorizontalAlignment + "，行中 "
            + (geometry is { } g ? (g.Left + g.Width / 2).ToString("0.0", CultureInfo.InvariantCulture) : "无")
            + " / 流中 "
            + (geometry is { } g2 ? (g2.FlowWidth / 2).ToString("0.0", CultureInfo.InvariantCulture) : "无")
            + "，行上「" + rowText + "」，落盘「" + stored?.Text + "」）");

        var sameAgain = panel.SelectModelForCheck(second.Id, model);
        Relayout(shell, panel);
        Check(sameAgain && panel.NoticeTurnRowsForCheck.Count == 1,
            "选回同一个模型不再多一条提示行：没有发生的切换不在转录里留下痕迹（实际 "
            + panel.NoticeTurnRowsForCheck.Count + " 行）");

        panel.RebuildMessagesForCheck();
        Relayout(shell, panel);
        var backRows = panel.NoticeTurnRowsForCheck;
        Check(backRows.Count == 1 && backRows[^1].HorizontalAlignment == HorizontalAlignment.Center
              && backRows[^1].Children.OfType<TextBlock>().FirstOrDefault()?.Text == expected,
            "整段重画之后提示行跟着会话回到原处，仍然居中（实际 " + backRows.Count + " 行）");

        CheckReasoningOnModelSwitch(shell, panel, first, second, session.Id, model);

        shell.Chat.DeleteConversation(session.Id);
        shell.Chat.RemoveProvider(first.Id);
        shell.Chat.RemoveProvider(second.Id);
        Relayout(shell, panel);
    }

    private void CheckReasoningOnModelSwitch(MainWindow shell, ChatPanel panel,
        ModelProvider first, ModelProvider second, string sessionId, string sourceModel)
    {
        const string targetModel = "gpt-shell-luna";
        first.ReasoningModels[sourceModel] = new AiModelReasoning
        {
            Efforts = [ChatReasoningEfforts.High, ChatReasoningEfforts.Max],
        };
        shell.Chat.AddModel(second.Id, targetModel);
        second.ReasoningModels[targetModel] = new AiModelReasoning
        {
            Efforts = [ChatReasoningEfforts.Low, ChatReasoningEfforts.Medium,
                ChatReasoningEfforts.High, ChatReasoningEfforts.XHigh],
            DefaultEffort = ChatReasoningEfforts.Medium,
        };

        // Existing notice checks never select a tier. Exercise the incompatible Max choice that otherwise
        // leaves the chip, slider and durable request setting describing different levels of reasoning.
        var observations = new List<string>();
        var consistent = true;
        foreach (var (picked, expected) in new[]
                 {
                     (ChatReasoningEfforts.Max, ChatReasoningEfforts.Medium),
                     (ChatReasoningEfforts.High, ChatReasoningEfforts.High),
                     (ChatReasoningEfforts.Default, ChatReasoningEfforts.Default),
                 })
        {
            var backToSource = panel.SelectModelForCheck(first.Id, sourceModel);
            var tierAccepted = shell.Chat.SelectReasoningEffort(picked);
            var switchToTarget = panel.SelectModelForCheck(second.Id, targetModel);
            var switched = backToSource && tierAccepted && switchToTarget;
            Relayout(shell, panel);
            var stored = shell.Chat.StoredCopyForCheck(sessionId)?.ReasoningEffort;
            var slider = ToolTip.GetTip(panel.FindControl<Slider>("ReasoningLevelSlider")!)?.ToString();
            var expectedSlider = expected == ChatReasoningEfforts.Default ? ChatReasoningEfforts.Medium : expected;
            var expectedChip = expected == ChatReasoningEfforts.Default ? "" : panel.ReasoningChipLabelForCheck(expected);
            var selectedRight = panel.SelectedReasoningForCheck == expected;
            var storedRight = stored == expected;
            var sliderRight = slider == expectedSlider;
            var chipRight = panel.ReasoningChipTextForCheck == expectedChip;
            consistent &= switched && selectedRight && storedRight && sliderRight && chipRight;
            observations.Add($"{picked} → 选 {panel.SelectedReasoningForCheck}/{expected} "
                + $"({selectedRight})、落盘 {stored}/{expected} ({storedRight})、"
                + $"滑块 {slider}/{expectedSlider} ({sliderRight})、芯片「{panel.ReasoningChipTextForCheck}」/「{expectedChip}」({chipRight})、"
                + $"回源 {backToSource}、选档 {tierAccepted}、切到目标 {switchToTarget}");
        }
        Check(consistent,
            "跨模型切换保留兼容档位、不兼容时落到目标默认档位，芯片、滑块与落盘一致（实际 "
            + string.Join("; ", observations) + "）");
    }
}
