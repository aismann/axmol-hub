using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// Per-model reasoning accessors over the capabilities a provider carries (manifest + live /models metadata).
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Returns explicit reasoning metadata for a provider/model pair. Unknown models fail closed because
    /// OpenAI-compatible model-list responses generally expose IDs only.
    /// </summary>
    public static AiModelReasoning? ReasoningFor(ModelProvider? provider, string? modelName)
    {
        if (provider is null || string.IsNullOrWhiteSpace(modelName)) return null;
        return provider.ReasoningModels.TryGetValue(modelName.Trim(), out var reasoning) ? reasoning : null;
    }

    /// <summary>
    /// Whether this (provider, model) pair asks the endpoint to think — read off what the model declared under
    /// <c>requestOptions</c>, the fields its own manifest says are "required to enable reasoning for this model"
    /// (DeepSeek's two models declare <c>{"thinking":{"type":"enabled"}}</c>).
    ///
    /// <para>Read here rather than inferred from a model's name, because what it decides is a rule about the
    /// <i>request</i>: an endpoint in thinking mode refuses the whole thing when an assistant message arrives
    /// without its <c>reasoning_content</c>, and Hub has to fill that field even for a turn whose thinking it
    /// never recorded — which is what a conversation that changed models midway looks like.
    /// <c>ReasoningReplayPolicy</c> honours it. The three shapes below are the ones that mean "thinking
    /// on"; every other declaration fails closed, because a field invented for an endpoint that never asked for
    /// one is a field that endpoint may reject.</para>
    /// </summary>
    public static bool IsThinkingMode(ModelProvider? provider, string? modelName)
    {
        if (ReasoningFor(provider, modelName)?.RequestOptions is not { Count: > 0 } options) return false;
        if (!options.TryGetValue(ThinkingField, out var declared)) return false;
        return declared.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => IsEnabled(declared.GetString()),
            JsonValueKind.Object => declared.TryGetProperty(ThinkingTypeField, out var type)
                                    && type.ValueKind == JsonValueKind.String
                                    && IsEnabled(type.GetString()),
            _ => false,
        };
    }

    /// <summary>The declared field and value <see cref="IsThinkingMode"/> recognises: the name a manifest writes
    /// to turn thinking on, and the word it writes there.</summary>
    private const string ThinkingField = "thinking";
    private const string ThinkingTypeField = "type";

    private static bool IsEnabled(string? declared)
        => string.Equals(declared, "enabled", StringComparison.OrdinalIgnoreCase);

    public static bool SupportsReasoningEffort(ModelProvider? provider, string? modelName)
        => EffortsFor(provider, modelName).Count > 0 || MayTryUnreportedEfforts(provider, modelName);

    public static bool SupportsReasoningEffort(ModelProvider? provider, string? modelName, string effort)
        => EffortsFor(provider, modelName).Contains(effort, StringComparer.OrdinalIgnoreCase);

    /// <summary>The tiers to offer for one model: what the manifest declared plus what a reply has proved, minus
    /// what the model itself refused.</summary>
    public static IReadOnlyList<string> EffortsFor(ModelProvider? provider, string? modelName)
    {
        var reasoning = ReasoningFor(provider, modelName);
        if (reasoning is null) return [];
        var offered = new List<string>(reasoning.Efforts);
        foreach (var effort in reasoning.ObservedEfforts)
            if (!offered.Contains(effort, StringComparer.OrdinalIgnoreCase)) offered.Add(effort);
        return offered
            .Where(effort => !reasoning.RejectedEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Whether a model that has never reported a tier should still be <b>askable</b>.
    ///
    /// <para>Yes. The alternative is a deadlock this app is sitting in right now: the composer's tier menu is
    /// hidden unless the model's tiers are known, the tiers are known only from a manifest that names one model
    /// in the whole catalog, and the gateway serving 205 models reports nothing — so the menu never appears, a
    /// tier is never sent, and no evidence can ever arrive to open it. A capability that can only be
    /// demonstrated after it has been granted is a capability that stays hidden.</para>
    ///
    /// <para>The cost is bounded and self-correcting: a refusal is recorded against that (model, tier) pair and
    /// the tier stops being offered, and once every tier has been refused the model is treated as one that
    /// cannot think — which is the answer no manifest ever gave Hub either.</para>
    /// </summary>
    public static bool MayTryUnreportedEfforts(ModelProvider? provider, string? modelName)
    {
        var reasoning = ReasoningFor(provider, modelName);
        if (reasoning is null) return true;
        return EffortsFor(provider, modelName).Count == 0
               && reasoning.RejectedEfforts.Count < ReasoningEffortLadder.Length;
    }

    /// <summary>Whether this tier may go on the wire: never a refused one; otherwise yes, even when nobody ever
    /// promised it — a person chose it, and silence is not a refusal.</summary>
    public static bool MaySendEffort(ModelProvider? provider, string? modelName, string? effort)
    {
        if (string.IsNullOrEmpty(effort) || effort == ChatReasoningEfforts.Default) return false;
        return ReasoningFor(provider, modelName)?.RejectedEfforts
                   .Contains(effort, StringComparer.OrdinalIgnoreCase) != true;
    }

    /// <summary>The tier a refusal named, or null when the message is not about reasoning. Both halves have to
    /// be there: "invalid api key" must never read as a model declining a tier, or one bad credential would
    /// quietly delete a model's whole tier menu.</summary>
    public static string? RejectedEffortOf(string? message, IReadOnlyList<string?> candidates)
    {
        if (string.IsNullOrEmpty(message)) return null;
        if (!message.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("thinking", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("effort", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var effort in candidates)
            if (effort is { Length: > 0 } level && level != ChatReasoningEfforts.Default
                && message.Contains(level, StringComparison.OrdinalIgnoreCase)) return level;
        return null;
    }

    /// <summary>The ladder, strongest first — used to tell "this model refused every tier we know" from
    /// "this model has never been asked".</summary>
    public static readonly string[] ReasoningEffortLadder =
    [
        ChatReasoningEfforts.Ultra, ChatReasoningEfforts.Max, ChatReasoningEfforts.XHigh,
        ChatReasoningEfforts.High, ChatReasoningEfforts.Medium, ChatReasoningEfforts.Low,
    ];
}
