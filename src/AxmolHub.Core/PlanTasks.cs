using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>Progress shown for one executable item in an approved plan.</summary>
public static class PlanTaskStates
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";

    public static bool IsState(string? value)
        => value is Pending or InProgress or Completed;
}

/// <summary>A plan step with a stable, one-based number and a display-only progress state.</summary>
public sealed record PlanTask(int Number, string Title, string State = PlanTaskStates.Pending);

/// <summary>Extracts the explicitly marked task section from a plan without guessing from its prose.</summary>
public static partial class PlanTaskParser
{
    public const int MaxTasks = 20;
    public const int MaxTitleCharacters = 180;

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s{0,3}(?:[-*+]\s+(?:\[[ xX~]\]\s*)?|\d+[.)]\s+)(?<title>.+?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ItemRegex();

    public static IReadOnlyList<PlanTask> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];

        var inTasks = false;
        var titles = new List<string>();
        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n').Split('\n'))
        {
            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var title = heading.Groups["title"].Value.Trim().TrimEnd('#').Trim();
                if (inTasks) break;
                inTasks = IsTaskHeading(title);
                continue;
            }

            if (!inTasks) continue;
            var item = ItemRegex().Match(line);
            if (!item.Success) continue;
            var taskTitle = item.Groups["title"].Value.Trim();
            if (taskTitle.Length == 0) continue;
            if (taskTitle.Length > MaxTitleCharacters)
                taskTitle = taskTitle[..(MaxTitleCharacters - 1)].TrimEnd() + "…";
            titles.Add(taskTitle);
            if (titles.Count == MaxTasks) break;
        }

        return titles.Select((title, index) => new PlanTask(index + 1, title)).ToArray();
    }

    private static bool IsTaskHeading(string title)
    {
        var normalized = title.Trim().TrimEnd(':', '：').ToLowerInvariant();
        return normalized is "tasks" or "task list" or "implementation tasks" or "subtasks"
               or "任务" or "任务清单" or "子任务" or "实施任务" or "执行步骤";
    }
}
