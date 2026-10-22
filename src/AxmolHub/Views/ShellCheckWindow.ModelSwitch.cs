using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>The model-switch notice: one centered line in the conversation where the history changed hands.
///
/// <para>Kept in its own file the way <c>ChatPanel.Decisions.cs</c> is, because the file this check would
/// otherwise grow is already the largest in the repository.</para></summary>
public partial class ShellCheckWindow
{
    /// <summary>
    /// Three facts, each catching a different failure: a real switch puts exactly one row in the flow and that
    /// row is centered in the column — asserted twice over, once on the control's own alignment and once on where
    /// it actually lands; a pick that changes nothing puts no row, because a transcript that marks a switch that
    /// did not happen is worse than one that marks none; and the row comes back after the flow is torn down and
    /// rebuilt, because it is a turn the conversation stores rather than something this view happened to paint.
    ///
    /// <para>Cost is part of the design: the suite has to stay well inside <c>SuiteCeiling</c> (the one budget
    /// constant — never restate its number here), so the panel is laid out only where a measurement needs pixels,
    /// and the rebuild is the teardown the renderer itself goes through rather than a trip through two more
    /// conversations.</para></summary>
    private void CheckModelSwitchNotice(MainWindow shell, ChatPanel panel)
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

    /// <summary>Re-reads the panel and drains the dispatcher, so what a check reads is the drawn row rather than
    /// the state handed to the renderer. <paramref name="measure"/> lays the whole window out as well — only a
    /// check that reads pixels needs that, and the suite's time ceiling pays for every one of them.</summary>
    private static void Relayout(MainWindow shell, ChatPanel panel, bool measure = false)
    {
        panel.Reload();
        if (measure) shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
