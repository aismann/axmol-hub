using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

public partial class ShellCheckWindow
{
    private void CheckVariableTranscriptScroll(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        var oldWidth = shell.Width;
        var inspectorWasVisible = shell.InspectorVisibleForCheck;
        try
        {
            chat.SeedTurnForCheck(session.Id, "variable height fixture");
            for (var i = 0; i < 160; i++)
                session.Messages.Add(ChatTurn.Assistant((i % 4) switch
                {
                    0 => "short reply " + i,
                    1 => string.Join("\n\n", Enumerable.Range(0, 35).Select(n => $"Paragraph **{i}/{n}**: a variable height transcript must keep content visible while scrolling.")),
                    2 => "```cpp\n" + string.Join("\n", Enumerable.Range(0, 20).Select(n => $"int value{n} = {n};")) + "\n```",
                    _ => "| A | B |\n|---|---|\n" + string.Join("\n", Enumerable.Range(0, 8).Select(n => $"| {n} | value |")),
                }));
            panel.Reload();
            Relayout(shell, panel, measure: true);
            var scroll = panel.TranscriptScrollerForCheck;
            Check(panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1) && panel.ViewAtBottomForCheck, $"混合行高打开后定位尾行及底部（末行 {panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1)}，{panel.VirtualCoverageForCheck.Detail}）");
            var gaps = "";
            for (var step = 0; step < 60; step++)
            {
                panel.ScrollTranscriptForCheck(Math.Max(0, scroll.Offset.Y - 137));
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (panel.VirtualCoverageForCheck is { Gap: > 40 } coverage)
                    gaps += $" step={step} {coverage.Detail};";
            }
            Check(gaps.Length == 0, "混合行高逐像素滚动期间视口没有空白缺口：" + gaps);
            panel.ClickScrollToBottomForCheck();
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var track = panel.TranscriptBarForCheck.GetVisualDescendants().OfType<Track>()
                .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical && candidate.Thumb is not null);
            if (track?.Thumb is not { } thumb) { Check(false, "混合行高转录有真实滚动条"); return; }
            var origin = ThumbGrabPoint(thumb, shell);
            var top = ThumbTopInTrack(thumb, track);
            var slip = 0d;
            var dragGaps = "";
            Press(thumb, origin, shell);
            try
            {
                for (var step = 1; step <= 8; step++)
                {
                    var delta = -step * 15d;
                    Move(thumb, origin, delta, shell);
                    shell.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    slip = Math.Max(slip, Math.Abs(ThumbTopInTrack(thumb, track) - top - delta));
                    if (panel.VirtualCoverageForCheck is { Gap: > 40 } coverage)
                        dragGaps += $" step={step} {coverage.Detail};";
                }
            }
            finally { Release(thumb, origin, -120, shell); }
            var releasedTop = ThumbTopInTrack(thumb, track);
            Relayout(shell, panel, measure: true);
            Check(dragGaps.Length == 0 && panel.VirtualCoverageForCheck.Gap <= 40,
                "混合行高实际拖动及释放后内容不消失：" + dragGaps + panel.VirtualCoverageForCheck.Detail);
            Check(Math.Abs(ThumbTopInTrack(thumb, track) - releasedTop) <= 2,
                "释放及刷新后滑块位置连续，不跳回估算像素位置");
            Check(!panel.ViewAtBottomForCheck, "向上拖动释放并刷新后保持阅读位置，不返回底部");
            Check(slip <= 2, $"混合行高拖动滑块保持在指针下（最大偏差 {slip:0.0}px）");
            chat.SeedPlanReplyForCheck(session.Id, "## Scroll fixture plan\n\nInspect mixed height rows beside the transcript.");
            shell.OpenInspector("plan", session.Messages.Count - 1);
            shell.Width = 1280;
            Relayout(shell, panel, measure: true);
            var narrowGaps = "";
            for (var step = 0; step < 24; step++)
            {
                panel.ScrollTranscriptForCheck(Math.Max(0, scroll.Offset.Y - 113));
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (panel.VirtualCoverageForCheck is { Gap: > 40 } coverage) narrowGaps += coverage.Detail + ";";
            }
            Check(shell.InspectorVisibleForCheck && narrowGaps.Length == 0,
                "右侧计划检查器打开、聊天列压窄后连续滚动没有空白：" + narrowGaps);
            if (Environment.GetEnvironmentVariable("AXMOL_VIRTUAL_SCROLL_CAPTURE") is { Length: > 0 } capture)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(capture))!);
                SmokeCapture.Capture(shell.WindowSurfaceForCheck, capture);
                var contentFrame = SmokeCapture.Capture(scroll, System.IO.Path.ChangeExtension(capture, ".transcript.png"));
                Check(!contentFrame.IsBlank(), "窄列连续滚动后的实际转录截图不是空白");
            }
            Check(Math.Abs(panel.TranscriptBarForCheck.Bounds.Right - scroll.Bounds.Right) <= 1
                && Math.Abs(panel.TranscriptBarForCheck.Bounds.Top - scroll.Bounds.Top) <= 1
                && panel.TranscriptBarForCheck.Bounds.Bottom <= scroll.Bounds.Bottom + 1,
                "主滑块限于转录右边界，不覆盖输入框或检查器");
            origin = ThumbGrabPoint(thumb, shell);
            Press(thumb, origin, shell);
            var alternate = chat.StartConversation();
            try
            {
                chat.SeedTurnForCheck(alternate.Id, "switch during drag");
                alternate.Messages.AddRange(Enumerable.Range(0, 100).Select(i => ChatTurn.Assistant("alternate reply " + i)));
                Relayout(shell, panel, measure: true);
                var switchOffset = panel.TranscriptScrollerForCheck.Offset.Y;
                Check(!panel.TranscriptThumbDraggingForCheck, "切会话立即取消主滑块拖动状态");
                Check(panel.ViewAtBottomForCheck && panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1),
                    "拖动中切入的新会话先定位最新消息：" + panel.VirtualCoverageForCheck.Detail);
                Move(thumb, origin, -25, shell);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Check(!panel.TranscriptThumbDraggingForCheck && panel.ViewAtBottomForCheck
                    && panel.VirtualIndexRealizedForCheck(panel.VirtualItemCountForCheck - 1),
                    "拖动中切换会话取消旧手势，旧指针移动不滚动新会话："
                    + $"offset {switchOffset:0.0} -> {panel.TranscriptScrollerForCheck.Offset.Y:0.0}, dragging={panel.TranscriptThumbDraggingForCheck}, " + panel.VirtualCoverageForCheck.Detail);
            }
            finally
            {
                Release(thumb, origin, -25, shell);
                chat.DeleteConversation(alternate.Id);
            }
        }
        finally
        {
            shell.Width = oldWidth;
            if (!inspectorWasVisible && shell.InspectorVisibleForCheck) shell.ClickInspectorToggleForCheck();
            chat.DeleteConversation(session.Id);
            if (before is not null) chat.OpenConversation(before);
        }
    }
}
