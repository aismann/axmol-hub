using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Gets fresh credentials per HTTP round, including tool rounds and compaction requests.</summary>
public sealed class OpenAiCodexTransport(ModelProvider provider, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const string BaseUrl = "https://chatgpt.com/backend-api/codex";
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { Scheme: "https", Host: "chatgpt.com" } uri
            || !uri.AbsolutePath.StartsWith("/backend-api/codex/", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to send an OpenAI subscription token to another endpoint.");
        var get = provider.OpenAiTokenProvider ?? throw new InvalidOperationException("OpenAI credential service is unavailable.");
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var tokens = await get(attempt != 0, cancellationToken).ConfigureAwait(false);
            using var outbound = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) outbound.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (body is not null)
            {
                outbound.Content = new ByteArrayContent(body);
                foreach (var header in request.Content!.Headers) outbound.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            if (tokens.AccountId is { Length: > 0 } account) outbound.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
            outbound.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
            var response = await base.SendAsync(outbound, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt != 0) return response;
            response.Dispose();
        }
    }
}

/// <summary>Adapts the SDK's serialized Responses body before the diagnostic recorder observes it.</summary>
public sealed class OpenAiCodexRequestPolicy : PipelinePolicy
{
    public static string Adapt(string body)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        root["store"] = false;
        root["stream"] = true;
        if (root["include"] is not JsonArray includes) root["include"] = includes = new JsonArray();
        if (!includes.Any(item => item?.GetValue<string>() == "reasoning.encrypted_content"))
            includes.Add("reasoning.encrypted_content");
        root["instructions"] ??= "You are a helpful programming assistant.";
        foreach (var key in new[] { "max_output_tokens", "max_completion_tokens", "temperature", "top_p", "previous_response_id" }) root.Remove(key);
        if (root["input"] is JsonArray input)
        {
            var instructions = new List<string>();
            foreach (var item in input.ToArray())
            {
                if (item is not JsonObject message) continue;
                var role = message["role"]?.GetValue<string>();
                if (role is "system" or "developer")
                {
                    if (message["content"] is JsonValue text) instructions.Add(text.GetValue<string>());
                    else if (message["content"] is JsonArray parts)
                        foreach (var part in parts) if (part?["text"] is JsonValue value) instructions.Add(value.GetValue<string>());
                    input.Remove(item);
                }
                // Stateless requests replay complete items instead of server-side references.
                if (message["type"]?.GetValue<string>() == "item_reference") input.Remove(item);
                else message.Remove("id");
            }
            if (instructions.Count > 0) root["instructions"] = string.Join("\n\n", instructions);
        }
        return root.ToJsonString();
    }
    private static void Apply(PipelineMessage message)
    {
        if (message.Request.Content is { } content)
        {
            using var buffer = new MemoryStream();
            content.WriteTo(buffer);
            var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(Adapt(json)));
        }
    }
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    { Apply(message); ProcessNext(message, pipeline, currentIndex); }
    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    { Apply(message); return ProcessNextAsync(message, pipeline, currentIndex); }
}

/// <summary>Codex is streaming-only; callers requesting a full response use the same streaming path.</summary>
public sealed class OpenAiCodexChatClient(Microsoft.Extensions.AI.IChatClient inner)
    : Microsoft.Extensions.AI.DelegatingChatClient(inner)
{
    public override Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Microsoft.Extensions.AI.ChatResponseExtensions.ToChatResponseAsync(
            InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken);
}
