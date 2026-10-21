using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AxmolHub.Agent;

/// <summary>
/// Stops a tool loop that repeats itself instead of working.
///
/// <para>The loop carries no round cap — the model ends it by answering — so what stands between a long task and
/// an unbounded paid loop is only cancellation, the consecutive-error counter (which counts failures, not
/// success) and the context budget. A model that has decided the answer is "read the same file again" trips
/// none of those: every call succeeds, the window compacts, the loop runs forever. This guard catches that one
/// shape by walking the transcript the loop itself accumulated: the same tool with the same arguments, run
/// <see cref="RepeatLimit"/> times inside a sliding window with nothing state-changing in between, is refused
/// as a result string the model reads instead of an exception that kills the turn.</para>
///
/// <para>Two exemptions are what keep the refusal off correct work, and both are read from the transcript
/// rather than from counters, so a restart, a steer or a replayed session sees the same history:</para>
/// <list type="bullet">
/// <item>A successful state-changing call (<c>file_write</c>, <c>run_command</c>, <c>request_directory</c>,
/// <c>memory_write</c>) ends the walk. Re-reading a file you just edited is
/// verification, not spinning.</item>
/// <item>A result that <see cref="ToolLoopContextGuard.Elide"/> rewrote tells the model in so many words to
/// call again if it still needs it — so a repeat after an elision is Hub asking for it, not a loop.</item>
/// </list>
///
/// <para>Deliberately exact-match only: similarity thresholds and cycle detection would be guesswork with no
/// local data behind them, and a false accusation refuses real work. A model that ignores the refusal keeps
/// getting the same refusal — cheap, one string per round, no tool executed.</para>
/// </summary>
public static class ToolSpinGuard
{
    /// <summary>Identical calls within the window that make the next one a spin. Three: one call, its verify,
    /// and one more is already more than a debugging loop needs.</summary>
    public const int RepeatLimit = 3;

    /// <summary>How far back repetitions count. Beyond this the calls were part of working through the problem;
    /// inside it, with no state change between, they are the problem.</summary>
    public const int WindowSize = 12;

    /// <summary>The tools whose success means the world moved. Read against their results below, not trusted
    /// by name alone. <c>request_directory</c> is here because its success is a grant recorded on the session —
    /// state a repeat would be asking for twice, and the answer to the second ask is "already granted".</summary>
    private static readonly string[] StateChangingTools =
        [Core.ToolNames.FileWrite, Core.ToolNames.RunCommand, Core.ToolNames.RequestDirectory, Core.ToolNames.MemoryWrite];

    /// <summary>The sentence a refused repeat hands back to the model. Matched on itself by the checks.</summary>
    public const string RefusalMarker = "has already run";

    /// <summary>The second refusal of one signature does not go back to the model — the turn is over. This
    /// starts the result the transcript keeps and <see cref="ChatWorkspace"/> reads to say so out loud.</summary>
    public const string TerminationMarker = "This turn is stopping:";

    /// <summary>Key order, whitespace and number spelling are not what the model meant — a spin that shuffled
    /// them would be a different signature by byte and the same call by intent. Objects are sorted, arrays keep
    /// their order (an argument list is ordered), and everything else round-trips through the JSON tree.</summary>
    public static string NormalizeArguments(string argumentsJson)
    {
        if (JsonNode.Parse(argumentsJson) is not JsonObject arguments) return argumentsJson;
        return Canonical(arguments).ToJsonString();
    }

    /// <summary>
    /// The refusal string if asking for this call now would be the <see cref="RepeatLimit"/>-th identical run
    /// inside the window, otherwise null and the call may proceed. <paramref name="history"/> is the invoking
    /// client's own accumulated list, which keeps original bytes — so elisions are reported through
    /// <paramref name="elidedCallIds"/> (see <see cref="ToolLoopContextGuard.ElidedCallIds"/>), and a result
    /// carrying the elide prefix directly is accepted as a second, list-independent spelling of the same fact.
    ///
    /// <para>The walk runs backwards and stops after <see cref="WindowSize"/> paired results: only the newest
    /// window can refuse, so pairing and normalizing the whole transcript would price every check at the size
    /// of the run — and an argument blob (a file written into a call's arguments) would be re-canonicalized on
    /// every tool call for the rest of the turn. Names are compared before arguments for the same reason: a
    /// window of twelve calls normally costs one normalization, the wanted one.</para>
    /// </summary>
    public static string? RefusalFor(string toolName, string argumentsJson, IList<ChatMessage> history,
        IReadOnlySet<string>? elidedCallIds = null)
    {
        var wantedArguments = NormalizeArguments(argumentsJson);
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var identical = 0;
        var paired = 0;
        for (var messageIndex = history.Count - 1; messageIndex >= 0 && paired < WindowSize; messageIndex--)
        {
            var contents = history[messageIndex].Contents;
            for (var contentIndex = contents.Count - 1; contentIndex >= 0; contentIndex--)
            {
                var content = contents[contentIndex];
                // Walking backwards, the result arrives before the call that made it; the dict is the seam.
                if (content is FunctionResultContent result)
                {
                    results[result.CallId] = TextOf(result);
                    continue;
                }

                if (content is not FunctionCallContent call || !results.Remove(call.CallId, out var text))
                    continue;
                paired++;
                // The world moved: anything older than this is a different problem, not a repetition of this one.
                if (StateChangingTools.Contains(call.Name, StringComparer.Ordinal) && LooksSuccessful(call.Name, text))
                    return null;
                if (text.StartsWith(ToolLoopContextGuard.ElidedResultPrefix, StringComparison.Ordinal)
                    || (elidedCallIds is { Count: > 0 } elided && elided.Contains(call.CallId))) continue;
                if (!string.Equals(call.Name, toolName, StringComparison.Ordinal)) continue;
                if (NormalizeArguments(JsonSerializer.Serialize(call.Arguments)) != wantedArguments) continue;
                if (++identical >= RepeatLimit - 1)
                    return $"This exact call ({toolName} with the same arguments) has already run {identical + 1} times "
                           + "in this turn and the results are in the conversation. Stop repeating it: answer from "
                           + "what you have already found, or do something different.";
            }
        }

        return null;
    }

    /// <summary>A refusal, a denial and a failure left the world where it was; only their absence counts as a
    /// move. The prefixes are the ones Hub's own invoker and approval gate write.</summary>
    private static bool LooksSuccessful(string toolName, string result)
    {
        if (result.Length == 0
            || result.StartsWith("Tool failed:", StringComparison.Ordinal)
            || result == Core.ToolApprovalResults.Denied
            || result == Core.ToolApprovalResults.Superseded
            // A call closed because the attempt ended before it ran changed nothing either. Left out of this list,
            // a state-changing tool the model legitimately re-asks after a continue would read as "the world moved",
            // which silently relaxes the guard on exactly the call whose repeat is the loop this protects against.
            || result == Core.ToolApprovalResults.NotAnswered
            || result == Core.ToolApprovalResults.Unavailable)
            return false;

        // Only these affirmative result shapes prove a state change. In particular, file_write's readable
        // refusals are nonempty, and treating them as progress lets repeated bad anchors reset the spin guard.
        return toolName switch
        {
            Core.ToolNames.FileWrite => result.StartsWith("Created ", StringComparison.Ordinal)
                            || result.StartsWith("Edited ", StringComparison.Ordinal),
            Core.ToolNames.RequestDirectory => result.StartsWith("Granted", StringComparison.Ordinal),
            Core.ToolNames.MemoryWrite => result.StartsWith("Saved ", StringComparison.Ordinal),
            // A command may have changed files before returning a nonzero exit code, so its exit status is not
            // evidence that no state changed. Tool failures above are the cases where execution never completed.
            Core.ToolNames.RunCommand => true,
            _ => false,
        };
    }

    private static JsonNode Canonical(JsonNode node)
    {
        switch (node)
        {
            case JsonObject table:
            {
                var sorted = new JsonObject();
                foreach (var pair in table.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    sorted[pair.Key] = pair.Value is null ? null : Canonical(pair.Value.DeepClone());
                return sorted;
            }
            case JsonArray items:
            {
                var kept = new JsonArray();
                foreach (var item in items) kept.Add(item is null ? null : Canonical(item.DeepClone()));
                return kept;
            }
            default:
                return node;
        }
    }

    private static string TextOf(FunctionResultContent result) => result.Result switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        null => "",
        _ => result.Result.ToString() ?? "",
    };
}
