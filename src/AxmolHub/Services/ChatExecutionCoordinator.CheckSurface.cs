using static AxmolHub.ChatProviderCoordinator;
using static AxmolHub.ChatSessionCoordinator;
using AxmolHub.Core;
using Microsoft.Extensions.AI;
using static AxmolHub.ChatWorkspace;
using System.Collections.Concurrent;
using System.Text;
using Avalonia.Threading;
using AxmolHub.Agent;

namespace AxmolHub;
internal sealed partial class ChatExecutionCoordinator
{
    internal string? PlansDirectoryForCheck => PlanStore.DirectoryFor(_dataRoot);

    // A typed snapshot keeps checks independent of the coordinator's private request representation.
    internal (IReadOnlyList<ChatTurn> History, string SystemPrompt, IReadOnlyList<AITool> Tools)? PreparedRequestForCheck(string conversationId)
        => PrepareRequest(conversationId) is { } request ? (request.History, request.SystemPrompt, request.Tools) : null;
    internal int PreparedImagePartsForCheck(string conversationId)
    {
        if (PrepareRequest(conversationId)is not { } request)
            return -1;
        var parts = 0;
        foreach (var turn in request.History)
            foreach (var image in turn.Images)
                if (request.Images?.Invoke(image)is not null)
                    parts++;
        return parts;
    }

    internal string CompressionPromptForCheck() => CompressionSystemPrompt("上一条摘要");
    internal ContextUsage TranscriptUsageForCheck(string conversationId) => EstimateTranscript(conversationId);
    internal (int Used, int Room) CompactionReadingForCheck(string conversationId) => CompactionReading(conversationId);
    internal bool SeedTurnForCheck(string conversationId, string text) => Sessions.Registry.TryUpdate(conversationId, opened => opened.Append(ChatTurn.User(text)));
    internal bool SeedGoalForCheck(string conversationId, string text)
    {
        var turn = ChatTurn.User(text, isGoal: true);
        var written = Sessions.Registry.TryUpdate(conversationId, opened =>
        {
            opened.Goal = text;
            opened.GoalState = ChatGoalStates.Active;
            opened.GoalTurnAt = turn.At;
            opened.GoalContinuationCount = 0;
            opened.Append(turn);
        });
        if (written)
            Changed?.Invoke();
        return written;
    }

    internal bool SeedAssistantReplyForCheck(string conversationId, string text) => Sessions.Registry.TryUpdate(conversationId, opened => opened.Append(new ChatTurn(ChatRoles.Assistant, text, DateTimeOffset.Now)));
    internal bool SeedUnansweredCallForCheck(string conversationId, string callId, string toolName, string arguments) => Sessions.Registry.TryUpdate(conversationId, opened => opened.Append(ChatTurn.FunctionCall(callId, toolName, arguments)));
    internal bool SeedSearchReplyForCheck(string conversationId, string text, ServerSearchLog searches) => Sessions.Registry.TryUpdate(conversationId, opened => opened.Append(new ChatTurn(ChatRoles.Assistant, text, DateTimeOffset.Now) { WebSearch = searches.ToStored() }));
    internal bool SeedPlanReplyForCheck(string conversationId, string body, string? reasoning = null)
    {
        var appended = Sessions.Registry.TryUpdate(conversationId, opened =>
        {
            opened.Mode = ChatModes.Plan;
            opened.Append(new ChatTurn(ChatRoles.Assistant, body, DateTimeOffset.Now) { Reasoning = reasoning, });
        });
        return appended && MarkCompletedPlanPending(conversationId);
    }
}
