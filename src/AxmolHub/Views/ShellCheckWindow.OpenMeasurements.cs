using Avalonia.Threading;
using AxmolHub.Core;

namespace AxmolHub;
internal sealed class ShellOpenMeasurementsChecks
{
    private readonly Action<bool, string> Check;
    internal ShellOpenMeasurementsChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal void CheckSessionOpenMeasurements(MainWindow shell, ChatPanel panel, string first, string second)
    {
        var chat = shell.Chat;
        Dispatcher.UIThread.RunJobs();
        chat.OpenConversation(first);
        var a = chat.CurrentSessionOpen!;
        panel.Reload();
        chat.OpenConversation(second);
        var b = chat.CurrentSessionOpen!;
        Dispatcher.UIThread.RunJobs();
        Check(a.Id != b.Id && a.ConversationId == first && b.ConversationId == second
              && a.Completed && b.Completed && a.Reloads >= 2,
            "快速切换的打开计时各有独立操作编号，Loaded 之前的重复 Reload 不丢失");
        Check(a.ObservedMs >= a.LoadMs && b.ObservedMs >= b.LoadMs
              && a.ReloadMs >= a.RenderMs && b.ReloadMs >= b.RenderMs,
            "打开计时包含加载与通知，渲染耗时是 Reload 的子集而不是可相加的首帧");
        var line = a.Complete(false);
        Check(line.Contains("presentation=unavailable") && line.Contains("openToLoaded=")
              && !line.Contains("first layout") && line.Contains("allocatedProcessBytes="),
            "日志明确 Loaded 是代理指标，不冒充呈现，分配量标为进程范围");
    }
}
