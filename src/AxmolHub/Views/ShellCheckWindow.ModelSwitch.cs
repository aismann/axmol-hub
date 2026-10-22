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
        var first = shell.Chat.AddProvider("Model switch A local", "http://localhost:11437/v1", model, null);
        var second = shell.Chat.AddProvider("Model switch B local", "http://localhost:11438/v1", model, null);
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

        shell.Chat.DeleteConversation(session.Id);
        shell.Chat.RemoveProvider(first.Id);
        shell.Chat.RemoveProvider(second.Id);
        Relayout(shell, panel);
    }
}
