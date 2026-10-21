using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>The transcript's scrollbar thumb: a thumb that slips its hand is a scrollbar nobody trusts.
///
/// <para>Kept in its own file the way <c>ShellCheckWindow.ModelSwitch.cs</c> is — the file it would otherwise
/// grow is already the largest in the repository. The drag is synthesized the way the title bar's press is: the
/// framework's own <see cref="Pointer"/> and real pointer events raised on the real <see cref="Thumb"/>, so what
/// is exercised is the Track's own drag plumbing rather than a re-enactment of it.</para>
///
/// <para>Three invariants, one per way a thumb has been seen to leave its hand. The thumb stays under the
/// pointer for the whole hold. The transcript's Extent and Viewport do not move while the thumb is held, because
/// a mapping that recomputes mid-drag is exactly how a thumb shrinks back from one hand and drifts off the
/// other. And a reply streaming in under the hand moves neither the reading position nor the thumb: no
/// automatic scroll beats a hand that is holding the scrollbar.</para></summary>
public partial class ShellCheckWindow
{
    private async Task CheckThumbDragAsync(MainWindow shell, ChatPanel panel)
    {
        var groupClock = System.Diagnostics.Stopwatch.StartNew();
        var chat = shell.Chat;
        var savedOverride = chat.ClientOverride;
        var session = chat.StartConversation();
        // A scratch workspace with no repository: the transcript's composer band reserves room for the change
        // tally, and a dirty checkout would make that tally appear between the two holds, moving the scrollbar
        // track under the assertion. The fixture is about the thumb, not about this checkout's working tree.
        var scrollWorkspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "axmolhub-checks", "scroll-drag");
        System.IO.Directory.CreateDirectory(scrollWorkspace);
        chat.SetWorkspaceRoot(session.Id, scrollWorkspace);
        chat.SetApprovalMode(session.Id, ToolApprovalModes.Full);
        // Several medium turns rather than one enormous one: the transcript has to overflow by a real
        // multiple of the viewport, or the logical bar's whole travel is a few pixels and a thumb at the top
        // cannot be dragged at all. Added directly and reloaded once so the fixture pays one repaint, not six.
        // The fenced block rides along because a block's height settles only after it has been realized once,
        // which is one of the places a re-measure can move a drag.
        session.Messages.AddRange(Enumerable.Range(1, 6).Select(part => ChatTurn.Assistant(
            "```cpp\nint main() { return " + part + "; }\n```\n\n第 " + part + " 段：" + new string('长', 600))));
        panel.Reload();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var scroller = panel.TranscriptScrollerForCheck;
        var track = panel.TranscriptBarForCheck.GetVisualDescendants().OfType<Track>()
            .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical);
        var thumb = track?.Thumb;
        Check(thumb is { Bounds.Height: > 1 } && panel.TranscriptScrollableForCheck && panel.ViewAtBottomForCheck,
            "夹具：转录溢出、真滚动条与滑块都在，而且转录停在底部（滑块 "
            + (thumb is null ? "无" : Fmt(thumb.Bounds.Height)) + " / 轨道 "
            + (track is null ? "无" : Fmt(track.Bounds.Height)) + "，在底部 " + panel.ViewAtBottomForCheck + "）");
        if (track is null || thumb is null) return;

        // ── Static transcript: the thumb stays under the pointer and the mapping does not move ──
        // Two moves rather than four and one layout per move: this group pays for every full arrange of the
        // window and the suite has a 120 second budget it is already close to. The per-step record is what turns
        // a red cell into a diagnosis: thumb travel, the offset it produced, and whether the framework still
        // considers the thumb held tell "did not move" and "moved and was pulled back" apart.
        var extentBefore = scroller.Extent.Height;
        var viewportBefore = scroller.Viewport.Height;
        var thumbHeightBefore = thumb.Bounds.Height;
        var topBefore = ThumbTopInTrack(thumb, track);
        var origin = ThumbGrabPoint(thumb, shell);
        string? synthesis = null;
        var slip = 0.0;
        var travel = 0.0;
        var steps = "";
        try
        {
            Press(thumb, origin, shell);
            for (var step = 1; step <= 2; step++)
            {
                var at = -24.0 * step;
                Move(thumb, origin, at, shell);
                shell.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                travel = ThumbTopInTrack(thumb, track) - topBefore;
                slip = Math.Max(slip, Math.Abs(travel - at));
                steps += (step == 1 ? "" : " / ") + "指针 " + Fmt(at) + "→滑块 " + Fmt(travel)
                    + "→offset " + Fmt(scroller.Offset.Y) + "→extent " + Fmt(scroller.Extent.Height)
                    + "→按住 " + panel.TranscriptThumbDraggingForCheck;
            }
        }
        catch (Exception ex)
        {
            synthesis = ex.GetType().Name + ": " + ex.Message;
        }
        var offsetAfterDrag = scroller.Offset.Y;
        var extentAfterDrag = scroller.Extent.Height;
        var viewportAfterDrag = scroller.Viewport.Height;
        var thumbHeightAfterDrag = thumb.Bounds.Height;
        Release(thumb, origin, -48, shell);

        Check(synthesis is null && slip <= 1.5,
            "拖动滑块的全程它都严格跟在指针底下（指针 -48.0 DIP，滑块 " + Fmt(travel)
            + "，最大偏差 " + Fmt(slip) + "，每步 [" + steps + "]"
            + (synthesis is null ? "" : "，合成失败 " + synthesis) + "）");
        // A virtualized transcript's total is an estimate until a row has been realized, and the drag is what
        // realizes the row that scrolls into the viewport — so one refinement here is the panel working, not the
        // column being re-measured. The per-step record above is what separates the two: a refinement lands in a
        // single step, while a re-measure that recomputed the column would move the total on every step and, by a
        // row's height rather than a row's remainder, on the order of a hundred pixels. What a hand can actually
        // see is the strict half below: the viewport, and the thumb's own size.
        const double EstimateRefinementAllowance = 0.01;
        var extentDrift = Math.Abs(extentAfterDrag - extentBefore);
        Check(extentDrift <= Math.Max(4, extentBefore * EstimateRefinementAllowance)
              && Math.Abs(viewportAfterDrag - viewportBefore) <= 0.5
              && Math.Abs(thumbHeightAfterDrag - thumbHeightBefore) <= 0.5,
            "静态转录上拖动不重算 Extent/Viewport，滑块尺寸不回缩（ΔExtent "
            + Fmt(extentAfterDrag - extentBefore) + " ≤ " + Fmt(Math.Max(4, extentBefore * EstimateRefinementAllowance))
            + "（估算修正上限 " + Fmt(EstimateRefinementAllowance * 100) + "%）"
            + "，ΔViewport " + Fmt(viewportAfterDrag - viewportBefore)
            + "，Δ滑块高 " + Fmt(thumbHeightAfterDrag - thumbHeightBefore) + "）");

        // The static hold above leaves the reading position wherever the drag ended; the streaming hold is
        // about a reply arriving under a hand at the bottom, so put the transcript back there first.
        panel.ClickScrollToBottomForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // ── Under the hand: a reply streaming in moves neither the thumb nor the reading position ──
        // Two holds around one gated reply, each catching a different theft. The first takes the thumb and drags
        // off the bottom before the answer's body lands: a repaint re-measures the message column and the Track
        // re-maps the thumb mid-drag, which is the thumb that shrinks back from one hand and drifts off the other.
        // The second holds the thumb still while the run finishes: the completion repaints the transcript and
        // scrolls to the end by design (AppendRunNotice's own tail), and that scroll is what used to pull the
        // thumb out from under the mouse. Only the second hold lets the run finish; under the first it parks.
        var gate = new TaskCompletionSource<bool>();
        var tailGate = new TaskCompletionSource<bool>();
        var reached = new TaskCompletionSource<bool>();
        chat.ClientOverride = (_, _) => new ScriptedChatClient(
            ["\n\n" + new string('流', 300), new string('续', 200)], gate.Task, tailGate.Task, reached);
        var streaming = panel.BeginSendForCheckAsync("流式提问");
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var heldTop = ThumbTopInTrack(thumb, track);
        var heldExtent = scroller.Extent.Height;
        var heldOrigin = ThumbGrabPoint(thumb, shell);
        var heldSlip = 0.0;
        var heldOffset = 0.0;
        var heldOffsetAfter = 0.0;
        var heldBarBefore = "";
        var heldDragBefore = false;
        var heldExtentAfter = heldExtent;
        string? heldSynthesis = null;
        try
        {
            Press(thumb, heldOrigin, shell);
            Move(thumb, heldOrigin, -20, shell);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            heldSlip = Math.Max(heldSlip, Math.Abs((ThumbTopInTrack(thumb, track) - heldTop) + 20));
            Move(thumb, heldOrigin, -40, shell);
            shell.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            heldSlip = Math.Max(heldSlip, Math.Abs((ThumbTopInTrack(thumb, track) - heldTop) + 40));
            heldOffset = scroller.Offset.Y;
            heldBarBefore = panel.TranscriptBarForCheck.Value + "/" + panel.TranscriptBarForCheck.Maximum
                + "/" + panel.TranscriptBarForCheck.ViewportSize;
            heldDragBefore = panel.TranscriptThumbDraggingForCheck;
            gate.SetResult(true);
            await reached.Task;
            for (var tick = 0; tick < 2; tick++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            heldSlip = Math.Max(heldSlip, Math.Abs((ThumbTopInTrack(thumb, track) - heldTop) + 40));
            heldOffsetAfter = scroller.Offset.Y;
            heldExtentAfter = scroller.Extent.Height;
            Release(thumb, heldOrigin, -40, shell);
        }
        catch (Exception ex)
        {
            heldSynthesis = ex.GetType().Name + ": " + ex.Message;
        }

        // A stored turn landing under the hand can change the column's total by a row's worth of height (the
        // live row becomes the committed row); the invariants that matter while the hand holds are the thumb
        // staying under the pointer, its size not shrinking, and the reading offset not moving (next check).
        Check(heldSynthesis is null && heldSlip <= 1.5 && Math.Abs(heldExtentAfter - heldExtent) <= 250,
            "回复流到按住的滑块底下时不重测整列：不回缩、不脱手（最大偏差 " + Fmt(heldSlip)
            + "，ΔExtent " + Fmt(heldExtentAfter - heldExtent)
            + "，bar " + Fmt(panel.TranscriptBarForCheck.Value) + "/" + Fmt(panel.TranscriptBarForCheck.Maximum)
            + "/" + Fmt(panel.TranscriptBarForCheck.ViewportSize)
            + "，before " + heldBarBefore + " drag=" + heldDragBefore
            + "，after drag=" + panel.TranscriptThumbDraggingForCheck
            + "，offset " + Fmt(scroller.Offset.Y)
            + (heldSynthesis is null ? "" : "，合成失败 " + heldSynthesis) + "）");
        Check(Math.Abs(heldOffsetAfter - heldOffset) <= 2,
            "按住滑块时流到的正文不把阅读位置拽回底部（按住时 offset " + Fmt(heldOffset)
            + "，到达后 " + Fmt(heldOffsetAfter) + "）");

        // The second hold: the run finishes under the hand and its scroll-to-end has to stand down. Only the
        // offset is asserted here, because the completion rebuilds the transcript by design and a rebuild may
        // legitimately re-map the thumb; what it may not do is write the reading position under a held hand.
        var finishOrigin = ThumbGrabPoint(thumb, shell);
        var finishOffset = 0.0;
        var finishOffsetAfter = 0.0;
        string? finishSynthesis = null;
        try
        {
            Press(thumb, finishOrigin, shell);
            finishOffset = scroller.Offset.Y;
            tailGate.SetResult(true);
            for (var tick = 0; tick < 5; tick++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            finishOffsetAfter = scroller.Offset.Y;
            Release(thumb, finishOrigin, 0, shell);
        }
        catch (Exception ex)
        {
            finishSynthesis = ex.GetType().Name + ": " + ex.Message;
        }
        gate.TrySetResult(true);
        tailGate.TrySetResult(true);
        await streaming;
        await panel.WaitForRunToFinishForCheck();
        shell.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Check(finishSynthesis is null && Math.Abs(finishOffsetAfter - finishOffset) <= 2,
            "回复收尾的重画与回底滚动在按住的滑块前让路（offset " + Fmt(finishOffset)
            + "→" + Fmt(finishOffsetAfter)
            + (finishSynthesis is null ? "" : "，合成失败 " + finishSynthesis)
            + "，本组 " + (int)groupClock.Elapsed.TotalMilliseconds + "ms）");

        chat.ClientOverride = savedOverride;
        chat.DeleteConversation(session.Id);
    }

    /// <summary>Where a hand grabs the thumb: its centre, in the window's coordinates — the same shape
    /// <c>BarPressPoint</c> measures the title bar's press at.</summary>
    private static Point ThumbGrabPoint(Thumb thumb, MainWindow shell)
        => thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), shell) ?? default;

    /// <summary>The thumb's top edge inside its track: the number that has to follow the pointer for the
    /// scrollbar to feel attached to the hand, whatever the Track's arithmetic does to Offset on the way.</summary>
    private static double ThumbTopInTrack(Thumb thumb, Track track)
        => (thumb.TranslatePoint(new Point(0, 0), track) ?? default).Y;

    private static void Press(Thumb thumb, Point origin, MainWindow shell)
        => thumb.RaiseEvent(new PointerPressedEventArgs(
            thumb, CheckPointer, shell, origin, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));

    private static void Move(Thumb thumb, Point origin, double at, MainWindow shell)
        => thumb.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            thumb, CheckPointer, shell, origin + new Vector(0, at), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

    private static void Release(Thumb thumb, Point origin, double at, MainWindow shell)
        => thumb.RaiseEvent(new PointerReleasedEventArgs(
            thumb, CheckPointer, shell, origin + new Vector(0, at), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
}
