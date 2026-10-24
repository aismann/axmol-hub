using Avalonia.Controls;
using Avalonia.Threading;
using AxmolHub.Agent;
using AxmolHub.Core;
using Microsoft.Extensions.AI;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;

internal sealed class ShellTaskbarProgressChecks(Action<bool, string> check)
{
    internal async Task VerifyAsync(string scratchRoot)
    {
        var changes = new List<bool>();
        var releases = 0;
        var indicator = new TaskbarInProgress(changes.Add, () => releases++);
        indicator.SetInProgress(false);
        indicator.SetInProgress(true);
        indicator.SetInProgress(true);
        indicator.SetInProgress(false);
        indicator.SetInProgress(false);
        indicator.SetInProgress(true);
        indicator.Dispose();
        indicator.Dispose();
        indicator.SetInProgress(true);
        var attempts = 0;
        var unavailable = new TaskbarInProgress(_ => { attempts++; throw new InvalidOperationException("offline shell"); });
        unavailable.SetInProgress(true);
        unavailable.SetInProgress(true);
        unavailable.Dispose();
        check(changes.SequenceEqual(new[] { true, false, true, false }) && releases == 1 && attempts == 2,
            "任务栏活动状态只在边沿更新，退出只清理一次，原生失败不打断执行");

        using var chat = new ChatWorkspace(Path.Combine(scratchRoot, "taskbar-progress"));
        // Agent mode refuses to build its tool list without a Hub snapshot; an empty one is enough here because
        // this check is about the window's activity indicator, not about what the assistant can read.
        chat.HubSnapshotProvider = () => new ChatWorkspace.HubReadOnlySnapshot([], [], []);
        var provider = chat.AddProvider("Activity fixture", "http://localhost:11438/v1", "activity-model", null, out _)!
            ?? throw new InvalidOperationException("Could not create activity fixture");
        chat.SelectChatModel(provider.Id, "activity-model");
        var work = Path.Combine(scratchRoot, "activity-work");
        Directory.CreateDirectory(work);
        var clients = new Dictionary<string, IChatClient>();
        chat.ClientOverride = (_, id) => clients[id];
        var activity = new List<bool>();
        using var runsIndicator = new TaskbarInProgress(activity.Add);
        var onUi = true;
        chat.RunsChanged += _ =>
        {
            onUi &= Dispatcher.UIThread.CheckAccess();
            runsIndicator.SetInProgress(chat.HasExecutingRuns);
        };
        var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = chat.StartConversation();
        clients[first.Id] = new ScriptedChatClient(["First complete."], gate: firstGate.Task);
        var second = chat.StartConversation();
        clients[second.Id] = new ScriptedChatClient(["Second complete."], gate: secondGate.Task);
        var startedFirst = chat.TryEnqueueSend(first.Id, "first task", null, out _);
        var startedSecond = chat.TryEnqueueSend(second.Id, "second task", null, out _);
        var concurrent = chat.RunningCount == 2 && chat.HasExecutingRuns && activity.SequenceEqual(new[] { true });
        firstGate.TrySetResult(true);
        var firstDone = await WaitForConditionAsync(() => chat.RunFor(first.Id) is null);
        var stillActive = chat.HasExecutingRuns && activity.SequenceEqual(new[] { true });
        chat.RequestStop(second.Id);
        var stopped = await WaitForConditionAsync(() => chat.RunningCount == 0);

        var failed = chat.StartConversation();
        clients[failed.Id] = new ScriptedChatClient([], exception: new InvalidOperationException("fixture failure"));
        var startedFailure = chat.TryEnqueueSend(failed.Id, "fail", null, out _);
        var failedDone = await WaitForConditionAsync(() => chat.RunFor(failed.Id) is null);

        var approval = chat.StartConversation();
        var resumedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalClient = new ApprovalChatClient
        {
            ToolName = "file_write",
            Arguments = new() { ["path"] = "activity.txt", ["old_string"] = "", ["new_string"] = "activity\n" },
            ParkBeforeFollowUp = resumedGate.Task,
        };
        clients[approval.Id] = approvalClient;
        chat.SelectMode(ChatModes.Agent);
        chat.SetWorkspaceRoot(approval.Id, work);
        chat.SetApprovalMode(approval.Id, ToolApprovalModes.Ask);
        var startedApproval = chat.TryEnqueueSend(approval.Id, "write the fixture", null, out _);
        var parked = await WaitForConditionAsync(() => chat.RunFor(approval.Id)?.Phase == RunPhase.AwaitingApproval);
        // ParkForApproval precedes the pump's RunsChanged hop; wait for both facts, not scheduler timing.
        var parkedNotified = await WaitForConditionAsync(() => activity.Count >= 6 && !activity[^1]);
        var idleApproval = chat.RunningCount == 1 && !chat.HasExecutingRuns;
        var call = approval.Messages.FirstOrDefault(turn => turn.ApprovalState == ChatApprovalStates.Pending)?.ToolCallId;
        var approved = chat.TryResolveApproval(approval.Id, call ?? "", approved: true, alwaysAllow: false, out _);
        var resumed = await WaitForConditionAsync(() => chat.HasExecutingRuns && activity.Count >= 7 && activity[^1]);
        resumedGate.TrySetResult(true);
        var approvalDone = await WaitForConditionAsync(() => chat.RunningCount == 0);
        var written = File.Exists(Path.Combine(work, "activity.txt"));
        var stages = new (string Name, bool Ok)[]
        {
            ("并行启动", startedFirst && startedSecond), ("并行仅置位一次", concurrent), ("首个完成不熄灭", firstDone && stillActive),
            ("取消归零", stopped), ("异常归零", startedFailure && failedDone), ("审批暂停", startedApproval && parked && parkedNotified && idleApproval),
            ("审批恢复", approved && resumed && approvalDone && written), ("UI 线程", onUi), ("最终归零", !chat.HasExecutingRuns),
        };
        var failedStages = stages.Where(stage => !stage.Ok).Select(stage => stage.Name).ToArray();
        check(failedStages.Length == 0
              && activity.SequenceEqual(new[] { true, false, true, false, true, false, true, false }),
            "真实执行生命周期：并行任务不提前熄灭，完成/取消/异常归零，审批暂停与恢复在 UI 线程更新（实际 "
            + string.Join(",", activity)
            + (failedStages.Length == 0 ? "" : "；未通过：" + string.Join(",", failedStages)) + "）");

        await VerifyNativeAsync();
    }

    private async Task VerifyNativeAsync()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        var window = new Window { Width = 240, Height = 120, Title = "Activity fixture" };
        window.Show();
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                using var dock = new MacDockProgress();
                dock.SetInProgress(true);
                dock.SetInProgress(true);
                var first = dock.FramesForCheck;
                window.WindowState = WindowState.Minimized;
                var minimized = await WaitForConditionAsync(() => dock.FramesForCheck > first + 2);
                var beforeHide = dock.FramesForCheck;
                window.Hide();
                var hidden = await WaitForConditionAsync(() => dock.FramesForCheck > beforeHide + 2);
                var drawn = dock.DrawsForCheck > 1;
                dock.SetInProgress(false);
                var stopped = dock.FramesForCheck;
                dock.Dispose();
                await Task.Delay(160);
                Dispatcher.UIThread.RunJobs();
                var released = !dock.HasResourcesForCheck && dock.FramesForCheck == stopped;
                dock.SetInProgress(true);
                var restarted = await WaitForConditionAsync(() => dock.FramesForCheck > stopped + 2);
                dock.Dispose();
                check(minimized && hidden && drawn && released && restarted && !dock.HasResourcesForCheck,
                    "macOS 原生 Dock 实际绘制并在最小化/隐藏后继续计时，停止释放资源且可重新启动（绘制 " + dock.DrawsForCheck + " 帧）");
            }
            else
            {
                var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                var active = SystemAttentionService.WindowsTaskbarBadge.TrySetProgress(handle, true, out var startHr, out _);
                window.WindowState = WindowState.Minimized;
                var cleared = SystemAttentionService.WindowsTaskbarBadge.TrySetProgress(handle, false, out var stopHr, out _);
                check(active && cleared,
                    $"Windows 原生任务栏进入不定进度并在最小化后清除（0x{startHr:x8}/0x{stopHr:x8}）");
            }
        }
        finally { window.Close(); }
    }
}
