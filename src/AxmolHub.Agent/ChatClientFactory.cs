using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace AxmolHub.Agent;

/// <summary>
/// Turns a <see cref="Core.ModelProvider"/> into a ready <c>IChatClient</c>.
///
/// <para>Every provider this app talks to speaks OpenAI's protocol, so there is one client stack and one place
/// that decides the request shape — which is what keeps "add a provider" a manifest entry rather than a new
/// branch. The one thing that is genuinely two things is the <b>wire</b>: a model answers on
/// <c>chat/completions</c> or on <c>responses</c>, and the answer comes from a wire declared on the provider
/// first and the provider's own catalog second — see <see cref="ProtocolFor"/>. That choice is made here, from
/// the model's capabilities, rather than being threaded in from the callers — the model is what determines it,
/// the callers already know which model they asked for, and a rule passed through five signatures is a rule
/// that will be passed wrong in one of them.</para>
///
/// <para>The third name in <c>ModelProtocols</c> — Anthropic's <c>messages</c> — has no client in this file yet,
/// and <see cref="Create"/> refuses it by name instead of defaulting it to chat. A wire that is recognized but
/// not spoken is still worth naming: it is what turns a catalog row into an explanation rather than into a
/// request that fails somewhere else.</para>
///
/// <para>Validation lives here and throws, rather than silently producing a client that fails on first call:
/// the error message names the provider and the missing field, which is what the settings UI surfaces.</para>
/// </summary>
public static class ChatClientFactory
{
    /// <summary>
    /// How many retries the request pipeline may make after its first attempt, on both wires.
    ///
    /// <para>Two — three attempts in total — which is the number the client model's own default already applied.
    /// The number is not the change; <b>owning</b> it is. Inherited, it was invisible in this file, undocumented,
    /// unasserted, and one SDK bump away from becoming a different number that nobody chose, on a request path
    /// where a retry is what makes a rate-limited provider feel like a broken connection.</para>
    ///
    /// <para>The budget this has to be read against: the tool loop carries no per-turn round cap — the model
    /// ends it by answering — and every round-trip is a request that can now be attempted three times, so a
    /// long investigation multiplies the two together. Each wait in between is the provider's own
    /// <c>Retry-After</c>, and <see cref="IdleAwareRetryPolicy"/> hands it to the run so the inactivity deadline
    /// stands down for it rather than mistaking an obeyed limit for a dead stream. That is why the number
    /// stays small and why the checks count the requests rather than trusting the policy.</para>
    /// </summary>
    public const int MaxRetries = 2;

    public static IChatClient Create(Core.ModelProvider provider, string? modelOverride = null)
        => Create(provider, modelOverride, null);

    /// <summary>
    /// Which wire this model answers on.
    ///
    /// <para><b>Two rungs, in this order.</b> A wire declared on the provider wins over what the catalog said.
    /// The catalog answers "which endpoints does this model appear on", which is the right source for one provider
    /// choosing between two OpenAI bodies; it is the wrong source for switching a whole provider onto a different
    /// authentication scheme, because a catalog cannot know whether the credential in the file was minted by a
    /// bearer flow or pasted as <c>sk-ant-…</c>. That question belongs to whoever configured the endpoint, and
    /// their answer is <see cref="Core.ModelProvider.Protocol"/>.</para>
    ///
    /// <para>Public because the settings page has to be able to say what will happen when a model is picked, and
    /// a second copy of the rule over there is the drift this repository keeps having to undo. A model nobody
    /// described is chat: every provider except Copilot publishes nothing about protocols, and silence must not
    /// change what already works.</para>
    /// </summary>
    public static string ProtocolFor(Core.ModelProvider provider, string? model)
        => provider.IsOpenAiOAuth ? Core.ModelProtocols.Responses
            : Core.ModelProtocols.IsKnown(provider.Protocol)
            ? provider.Protocol!
            : Core.ContextBudget.CapabilitiesOf(provider, model)?.Protocol ?? Core.ModelProtocols.Chat;

    /// <summary>
    /// <paramref name="reasoning"/> is what makes a thinking model's chain of thought go back out on the wire.
    /// It is optional because it is per-request rather than per-provider: the caller that owns the conversation
    /// owns the table, and a client built without one simply never replays anything — which is the right answer
    /// for a model that does not think, and for the scripted clients the self-checks drive.
    ///
    /// <para><paramref name="interactionId"/> is the conversation's own id, for the providers that group the
    /// rounds of one tool run under it. Optional for the same reason the headers are: most providers want
    /// nothing with it, and sending a header a gateway has never heard of is a worse default than omitting it.</para>
    ///
    /// <para><paramref name="transport"/> is the seam that lets a self-check read <b>the serialized request
    /// body</b> rather than Hub's intention to send one. It exists for the hosted-search assertions and for
    /// nothing else today: a declaration that never reaches the wire is invisible from the outside, and the only
    /// proof that matters is the JSON that left — which field it landed in, and whether a replayed item silently
    /// vanished. Passing a handler keeps that proof free of any real endpoint (the check points it at a loopback
    /// URL and answers from memory), so nothing is billed and no gateway is contacted. An injected transport is
    /// <b>not</b> disposed here, the same rule the app's three <see cref="System.Net.Http.HttpClient"/> seams
    /// follow: whoever supplied it owns it.</para>
    ///
    /// <para><paramref name="onRetryWait"/> is how the owner of a run learns that the pipeline is sitting out a
    /// retry delay: called with the attempt now pending and the length of the wait, then with <c>0</c> once the
    /// wait is over. The policy that does the sleeping lives below the streaming enumerator, where waiting for a
    /// provider's <c>Retry-After</c> is indistinguishable from a provider that has stopped answering — and the
    /// caller's inactivity deadline measures exactly that silence, so without this notice a rate limit reads as a
    /// dead stream. Optional because a compaction request and the scripted clients have no run to tell.</para>
    /// </summary>
    public static IChatClient Create(
        Core.ModelProvider provider,
        string? modelOverride,
        ReasoningTable? reasoning,
        string? interactionId = null,
        System.Net.Http.HttpMessageHandler? transport = null,
        Action<int, TimeSpan>? onRetryWait = null)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no base URL.");
        var model = string.IsNullOrWhiteSpace(modelOverride) ? provider.Model : modelOverride;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no model.");
        if (provider.RequiresCredential && string.IsNullOrEmpty(provider.ApiKey))
            throw new InvalidOperationException($"Provider '{provider.Name}' is not authenticated yet.");

        // Refused here, at the one place that decides the wire, rather than sent and watched to fail. A model that
        // answers only on Anthropic's /v1/messages is a real row in a real catalog today, and before this line it
        // resolved silently to chat — which posted an OpenAI body at the endpoint and came back as a provider's
        // 404 page, indistinguishable from a broken key or a dead gateway. Saying which wire the model wants is
        // the whole fix, and it is why the protocol has a name even though only two of the three have a client.
        if (ProtocolFor(provider, model) == Core.ModelProtocols.Messages)
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' model '{model}' answers on Anthropic's /v1/messages wire, which this build does not speak: "
                + "Hub sends OpenAI chat/completions or responses. Point the provider at its OpenAI-compatible base URL instead.");

        // A local endpoint (Ollama / custom) needs no key; pass a placeholder credential so the header is harmless.
        var credential = new ApiKeyCredential(provider.ApiKey ?? "not-needed");
        // Built only for a provider that asked for it: an ordinary OpenAI-compatible endpoint must not start
        // carrying headers from a different family.
        var headers = provider.ExtraHeaders.Count > 0 || provider.RequestSemantics is { Length: > 0 }
            ? new ProviderRequestPolicy(provider.ExtraHeaders, provider.RequestSemantics, interactionId)
            : null;

        if (ProtocolFor(provider, model) == Core.ModelProtocols.Responses)
        {
            // The Responses wire, for the models whose own catalog says they answer nowhere else. The reasoning
            // replay is deliberately absent: it edits <c>messages[].reasoning_content</c>, and this body carries
            // <c>input</c> instead, so attaching it would be a policy that silently does nothing while looking
            // like coverage. What that costs a thinking model across a tool loop has not been measured (no
            // billable request was run) — the gap is recorded in local/copilot-oauth-probe.md, not papered over.
            //
            // The SDK labels this whole namespace experimental. Suppressed here rather than in the project file
            // so the exception is one block wide and has to be re-read by whoever moves it: an experimental API
            // that changes is a build error at this line, which is the loud failure we want, and a project-wide
            // NoWarn would also be swallowing the next one.
#pragma warning disable OPENAI001 // OpenAI.Responses is marked experimental in the 2.14 SDK.
            var options = new ResponsesClientOptions { Endpoint = new Uri(provider.IsOpenAiOAuth ? OpenAiCodexTransport.BaseUrl : provider.BaseUrl) };
            // The retry decision is stated on both wires from one constant, so the two cannot drift into two
            // different numbers of attempts — and both wrap it in the same policy, so a wait is reported on
            // whichever wire the model answers on.
            options.RetryPolicy = new IdleAwareRetryPolicy(MaxRetries, onRetryWait);
            if (provider.IsOpenAiOAuth)
            {
                options.Transport = new HttpClientPipelineTransport(new System.Net.Http.HttpClient(
                    new OpenAiCodexTransport(provider, transport ?? new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false })));
                options.AddPolicy(new OpenAiCodexRequestPolicy(), PipelinePosition.BeforeTransport);
            }
            else if (transport is not null)
                options.Transport = new HttpClientPipelineTransport(new System.Net.Http.HttpClient(transport));
            if (headers is not null)
                options.AddPolicy(headers, PipelinePosition.BeforeTransport);
            // Last, so the recorded body is the one that leaves: the replay policy above has already rewritten
            // the messages, and a recording taken before that would document an intention rather than a request.
            options.AddPolicy(new WireLogPolicy(), PipelinePosition.BeforeTransport);
            var responses = new ResponsesClient(credential, options).AsIChatClient(model);
            return provider.IsOpenAiOAuth ? new OpenAiCodexChatClient(responses) : responses;
#pragma warning restore OPENAI001
        }

        var chatOptions = new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl) };
        chatOptions.RetryPolicy = new IdleAwareRetryPolicy(MaxRetries, onRetryWait);
        if (transport is not null)
            chatOptions.Transport = new HttpClientPipelineTransport(new System.Net.Http.HttpClient(transport));
        if (reasoning is not null)
            // BeforeTransport: the last thing the pipeline does, so the connector has already built the body and
            // the transport has not yet sent it — the one moment the serialized messages exist and can still be
            // edited. (There is no per-apply position in this version of the client model.) The second argument is
            // what this endpoint demands of the history: a model whose own declaration puts the request in
            // thinking mode refuses the whole thing when an assistant message arrives without its
            // reasoning_content, and a conversation that changed models midway is exactly that shape.
            chatOptions.AddPolicy(
                new ReasoningReplayPolicy(reasoning, Core.ModelCatalog.IsThinkingMode(provider, model)),
                PipelinePosition.BeforeTransport);
        // Added after the reasoning rewrite, though nothing depends on that: the header policy reads roles and
        // image parts, which the rewrite does not touch.
        if (headers is not null)
            chatOptions.AddPolicy(headers, PipelinePosition.BeforeTransport);
        // Added last on purpose: <see cref="ReasoningReplayPolicy"/> rewrites the messages at this same position,
        // and the whole point of the recording is to show the body after that rewrite rather than before it.
        chatOptions.AddPolicy(new WireLogPolicy(), PipelinePosition.BeforeTransport);
        return new OpenAIClient(credential, chatOptions).GetChatClient(model).AsIChatClient();
    }
}
