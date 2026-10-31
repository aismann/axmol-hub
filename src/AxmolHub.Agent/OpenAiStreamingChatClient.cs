namespace AxmolHub.Agent;

/// <summary>
/// ChatGPT plan Responses requests are stream-only, so a caller that asks for one complete response reads the
/// same stream and folds it. One wire path for both callers also keeps the "must see response.completed"
/// check in front of the non-streaming one, instead of letting it skip the rule.
/// </summary>
public sealed class OpenAiStreamingChatClient(Microsoft.Extensions.AI.IChatClient inner)
    : Microsoft.Extensions.AI.DelegatingChatClient(inner)
{
    public override Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Microsoft.Extensions.AI.ChatResponseExtensions.ToChatResponseAsync(
            InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken);
}
