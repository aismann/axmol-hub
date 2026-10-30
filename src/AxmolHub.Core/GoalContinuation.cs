namespace AxmolHub.Core;

/// <summary>Bounded continuation decisions. Tool activity is a signal of work, not proof of progress.</summary>
public static class GoalContinuation
{
    public const int Limit = 3;
    public const int EmptyRunLimit = 2;
    public const string CompleteMarker = "[GOAL_COMPLETE]";
    public const string BlockedMarker = "[GOAL_BLOCKED]";

    public static GoalDecision Decide(string? answer, bool hadToolActivity, int emptyRuns, int continuations)
    {
        if (answer?.Contains(CompleteMarker, StringComparison.Ordinal) == true)
            return new(ChatGoalStates.Complete, false, 0);
        if (answer?.Contains(BlockedMarker, StringComparison.Ordinal) == true)
            return new(ChatGoalStates.Paused, false, 0, "blocked");
        var empty = hadToolActivity ? 0 : emptyRuns + 1;
        if (empty >= EmptyRunLimit) return new(ChatGoalStates.Paused, false, empty, "no_activity");
        if (continuations >= Limit) return new(ChatGoalStates.BudgetLimited, false, empty, "limit");
        return new(ChatGoalStates.Active, true, empty);
    }
}

public sealed record GoalDecision(string State, bool Continue, int EmptyRuns, string? Reason = null);
