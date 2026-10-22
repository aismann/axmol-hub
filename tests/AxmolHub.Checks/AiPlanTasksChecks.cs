using AxmolHub.Core;

internal static class AiPlanTasksChecks
{
    internal static async Task RunAsync(string root, string[] args)
    {
        const string plan = "## Implementation tasks\r\n- [ ] First task\r\n2. Second task\r\n\r\n## Risks\r\n- Not a task";
        var tasks = PlanTaskParser.Parse(plan);
        if (tasks.Count != 2 || tasks[0] != new PlanTask(1, "First task") || tasks[1] != new PlanTask(2, "Second task"))
            throw new Exception("Plan checklist parsing did not preserve the task rows or numbering.");
        if (PlanTaskParser.Parse("# Plan\n- [ ] unscoped prose\n## Context\n- [ ] not a task").Count != 0)
            throw new Exception("Task extraction must require an explicit task section.");
        if (PlanTaskParser.Parse("## 子任务\n- [ ] 第一步\n\n## 说明\n- [ ] 不提取").Count != 1)
            throw new Exception("Chinese task section parsing did not stop at the next heading.");
        Console.WriteLine("PASS: explicit plan task sections parse checklist and numbered items, preserve LF, and stop at the next heading.");
        return;
    }
}
