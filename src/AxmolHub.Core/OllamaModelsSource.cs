using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxmolHub.Core;

/// <summary>
/// The window a local server will serve one model with, and where that answer came from.
/// </summary>
/// <param name="Tokens">The number a request is sized against.</param>
/// <param name="Source">Provenance for the meter: <see cref="CapabilitySource.EndpointReported"/> when the server
/// said it, <see cref="CapabilitySource.Fallback"/> when it is the documented default nobody confirmed.</param>
public readonly record struct OllamaWindow(int Tokens, CapabilitySource Source);

/// <summary>
/// What a local Ollama server says about one of its models, read from <c>POST /api/show</c>.
/// </summary>
/// <param name="TrainedContext">The window the weights were trained for (<c>model_info.*.context_length</c>) — what
/// the model <i>can</i> hold, which is not what the server hands it, and which is why it is a ceiling here rather
/// than an answer. Measured on this machine: <c>qwen3-coder</c> reports 262 144 trained tokens and pins no
/// <c>num_ctx</c> at all, so believing this number would have Hub build 262 144-token requests that the server
/// cuts down to its own default before the model sees them.</param>
/// <param name="ParameterContext">The <c>num_ctx</c> the Modelfile pins, if one does. This is the only field on the
/// show answer that describes the window the server will actually give a request.</param>
/// <param name="Vision">Whether the model declares the <c>vision</c> capability. Meaningful only when
/// <see cref="DeclaresCapabilities"/> is true: an old server answers nothing here, and silence is not "no".</param>
public sealed record OllamaModelFacts(
    int? TrainedContext, int? ParameterContext, bool Vision, bool DeclaresCapabilities)
{
    public static readonly OllamaModelFacts Empty = new(null, null, false, false);

    /// <summary>
    /// The window this model gets, most authoritative answer first: what the server reported for the model it has
    /// actually loaded (<c>/api/ps</c>), then the Modelfile's own <c>num_ctx</c>, then — when the server has pinned
    /// nothing — <b>the server's own documented default of 4 096</b> rather than the trained maximum.
    ///
    /// <para>That last step is the one this type exists for, and it is the opposite of what a reader expects. A model
    /// trained for 262 144 tokens is served by Ollama with 4 096 unless somebody says otherwise
    /// (<c>OLLAMA_CONTEXT_LENGTH</c>, or <c>PARAMETER num_ctx</c> in the Modelfile), and an oversized prompt is
    /// truncated from the front instead of refused — there is no 400 to learn from, and the answer that comes back
    /// looks like a model that forgot the beginning of the conversation. Two mistakes are possible here and they are
    /// not equal: guessing the default costs compaction a person can see and undo, while guessing the trained
    /// maximum costs turns that are already gone. <c>/api/ps</c> is what makes the guess unnecessary once the model
    /// has actually been loaded.</para>
    ///
    /// <para>The trained number still bounds the answer, because a Modelfile can pin a <c>num_ctx</c> larger than the
    /// weights were built for and the smaller of the two is the one that will work.</para>
    /// </summary>
    public OllamaWindow Resolve(int? running)
    {
        if (running is > 0) return new(running.Value, CapabilitySource.EndpointReported);
        if (ParameterContext is > 0)
            return new(Ceiling(ParameterContext.Value), CapabilitySource.EndpointReported);
        return new(Ceiling(OllamaModelsSource.DefaultContextTokens), CapabilitySource.Fallback);
    }

    private int Ceiling(int tokens)
    {
        var trained = TrainedContext.GetValueOrDefault();
        return trained > 0 && trained < tokens ? trained : tokens;
    }
}

/// <summary>
/// <c>POST /api/show</c> and <c>GET /api/ps</c> on an Ollama server, alongside the OpenAI-shaped model list it
/// also serves.
///
/// <para><b>Why this family needs a source of its own.</b> The shared OpenAI-compatible parser reads the
/// OpenRouter vocabulary — <c>context_length</c>, <c>architecture.input_modalities</c> — and Ollama's
/// <c>/v1/models</c> answers with ids and nothing else. So a preset with no number of its own fell back to a
/// declared default chosen for hosted models, and the honest reading of that gap is the one in
/// <c>manifests/ai-providers.json</c>: an oversized prompt is <b>truncated in silence</b> by the server, so the
/// ceiling Hub assumed was never corrected by anything. Learning the real window is the difference between a long
/// local conversation that gets compacted and one that quietly loses its oldest turns.</para>
///
/// <para><b>Two endpoints, because they answer different questions.</b> <c>/api/ps</c> describes the model that is
/// loaded <i>now</i> and reports its <c>context_length</c> — the runtime window, environment variable and all.
/// <c>/api/show</c> describes the model on disk: the Modelfile's <c>num_ctx</c> (only when somebody pinned one),
/// the trained maximum, and its capability list. A model nobody has loaded yet is absent from the first answer, so
/// the second is what makes a fresh catalog usable at all.</para>
///
/// <para><b>Every part of this is optional and none of it may fail the fetch.</b> A server too old for an endpoint,
/// a model that answers 404 to <c>/api/show</c> while still being servable, a JSON body that is not what this file
/// expects — each one costs the fields that endpoint would have contributed and nothing else. A model list that
/// vanished because one enrichment call failed would be a worse catalog than the one this replaced.</para>
/// </summary>
public sealed class OllamaModelsSource : IModelCapabilitySource, IRuntimeModelWindow
{
    public const string IdValue = "ollama-models";

    public string Id => IdValue;

    /// <summary>How many models get their own <c>/api/show</c> call. Well under <see cref="ModelList.MaxModels"/>:
    /// this is one request per model against a server on this machine, and a catalog longer than this is a list
    /// nobody scrolls. The rest keep whatever the list itself said.</summary>
    public const int MaxShowCalls = 32;

    /// <summary>
    /// The window an Ollama server gives a model when nobody has said otherwise — its own documented default, read
    /// from the server's documentation rather than from a machine here, and the number this source falls back to
    /// when neither <c>/api/ps</c> nor the Modelfile answers.
    ///
    /// <para><b>It is smaller than every model it will be applied to, on purpose.</b> Measured against the server on
    /// the machine this was written for: <c>qwen3-coder</c> is trained for 262 144 tokens, pins no <c>num_ctx</c>,
    /// and is served with this default. Reporting the trained window would let Hub build a request forty times
    /// larger than the server accepts — and Ollama does not refuse it, it cuts the front off and answers — which is
    /// the failure of a model that seems to have forgotten what it was asked. Reporting the default costs a
    /// conversation that is compacted earlier than it strictly had to be, which is visible, announced, and one
    /// <c>PARAMETER num_ctx</c> away from being corrected.</para>
    /// </summary>
    public const int DefaultContextTokens = 4096;

    public async Task<ModelCapabilityBatch> FetchAsync(
        HttpClient client, ModelProvider provider, CancellationToken cancellationToken)
    {
        // The list itself is the same OpenAI-shaped answer as any other provider serves, and reading it through
        // the shared source rather than through /api/tags keeps one parse rule for "which models does this
        // endpoint offer" instead of two that can disagree about a name.
        var listed = await new OpenAiCompatibleModelsSource()
            .FetchAsync(client, provider, cancellationToken).ConfigureAwait(false);
        if (!listed.Reachable) return listed;

        var running = await RunningAsync(client, provider, cancellationToken).ConfigureAwait(false);
        var capabilities = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, caps) in listed.Capabilities) capabilities[name] = caps;

        var shown = 0;
        foreach (var name in listed.Models)
        {
            if (shown >= MaxShowCalls) break;
            shown++;
            var facts = await ShowAsync(client, provider, name, cancellationToken).ConfigureAwait(false);
            // Every model gets a window, including one whose /api/show call failed: the default is a better answer
            // than the preset's hosted fallback, and the run itself asks /api/ps again before it is built.
            var loaded = running.TryGetValue(NormalizeName(name), out var at) ? at : (int?)null;
            capabilities[name] = Merge(capabilities.GetValueOrDefault(name), facts.Resolve(loaded), facts);
        }

        return new ModelCapabilityBatch(listed.Models, listed.Reasoning, capabilities, null);
    }

    /// <summary>
    /// The window the server has this model loaded with, or <c>null</c> when it has not loaded it.
    ///
    /// <para>Asked per run because it is the one number that cannot be guessed: the same model on two machines
    /// runs with two windows, and the value follows <c>OLLAMA_CONTEXT_LENGTH</c>, the Modelfile and whatever the
    /// client asked for last. A catalog fetched at some earlier moment is a memory of that answer, and the run
    /// about to be built is the one that has to fit.</para>
    /// </summary>
    public async Task<int?> RuntimeWindowAsync(
        HttpClient client, ModelProvider provider, string modelName, CancellationToken cancellationToken)
    {
        var running = await RunningAsync(client, provider, cancellationToken).ConfigureAwait(false);
        // Not GetValueOrDefault: a dictionary miss answers 0 for an int, and 0 is a window nobody has — it would
        // read as "the server said nothing", which is right by accident and wrong the moment a caller stops
        // treating it as falsy.
        return running.TryGetValue(NormalizeName(modelName), out var window) ? window : null;
    }

    /// <summary>One <c>/api/show</c> answer, or <see cref="OllamaModelFacts.Empty"/> for a server that does not
    /// have it. Never throws: this rides on a fetch that must still deliver a model list.</summary>
    private static async Task<OllamaModelFacts> ShowAsync(
        HttpClient client, ModelProvider provider, string modelName, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["model"] = modelName, ["name"] = modelName };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoot(provider.BaseUrl) + "/api/show")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return OllamaModelFacts.Empty;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return ParseShow(await ModelList.ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A local server that answers a malformed body, or a connection that drops between the list and this
            // call, is a model whose window stays unknown — not a failed catalog.
            return OllamaModelFacts.Empty;
        }
    }

    /// <summary>The running models, by name. An empty answer for a server that is not up yet, has nothing loaded, or
    /// does not implement <c>/api/ps</c>: all three mean "no runtime window to report".</summary>
    private static async Task<IReadOnlyDictionary<string, int>> RunningAsync(
        HttpClient client, ModelProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoot(provider.BaseUrl) + "/api/ps");
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return EmptyRunning;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return ParseRunning(await ModelList.ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyRunning;
        }
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyRunning =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The base URL without the <c>/v1</c> the OpenAI client needs. Ollama serves its own API at the root and the
    /// OpenAI one under <c>/v1</c>, so <c>http://localhost:11434/v1</c> is two addresses in one string; joining a
    /// path onto it as it arrives would ask <c>/v1/api/show</c>, which answers 404 and would read as a server that
    /// has no such model.
    ///
    /// <para>The trim is on the exact suffix and nothing else, because a reverse proxy's base URL is the person's
    /// decision: <c>https://gpu.lan/ollama/v1</c> becomes <c>https://gpu.lan/ollama</c>, which is where the proxy
    /// will have put <c>/api</c> if it put it anywhere.</para>
    /// </summary>
    public static string ApiRoot(string? baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3]
            : trimmed;
    }

    /// <summary>Ollama names a model <c>family:tag</c> and omits <c>:latest</c> in some answers and spells it out in
    /// others, so both sides of every comparison go through here.</summary>
    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^":latest".Length]
            : trimmed;
    }

    /// <summary>One <c>/api/show</c> body. Separate from the fetch for the standing reason: what this has to get
    /// right is the shape of somebody else's JSON, and only bytes can assert on that.</summary>
    public static OllamaModelFacts ParseShow(byte[] payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return OllamaModelFacts.Empty;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return OllamaModelFacts.Empty;

            var trained = TrainedContext(document.RootElement);
            var pinned = PinnedContext(document.RootElement);
            var vision = DeclaresVision(document.RootElement, out var declares);
            return new OllamaModelFacts(trained, pinned, vision, declares);
        }
    }

    /// <summary>
    /// The trained window, from whichever <c>model_info</c> key carries it. The key is architecture-prefixed
    /// (<c>qwen3.context_length</c>, <c>llama.context_length</c>) and the prefix is not something to match against:
    /// the field is named <c>context_length</c> wherever it appears, and a new architecture must not need a release
    /// to be understood.
    /// </summary>
    private static int? TrainedContext(JsonElement root)
    {
        if (!root.TryGetProperty("model_info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        foreach (var field in info.EnumerateObject())
            if (field.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase)
                && TokenField.Of(info, field.Name) is { } tokens)
                return tokens;
        // Some builds publish the same number without an architecture prefix.
        return TokenField.Of(info, "context_length");
    }

    /// <summary>
    /// <c>num_ctx</c> out of the <c>parameters</c> block, which is the Modelfile's settings rendered as text: one
    /// "<c>name&nbsp;&nbsp;&nbsp;value</c>" per line, whitespace-aligned into columns. Parsed here rather than by a
    /// regular expression over the whole string because the block can carry a prompt or a template, and the first
    /// <c>num_ctx 4096</c> inside somebody's system prompt must not be read as this model's window.
    /// </summary>
    private static int? PinnedContext(JsonElement root)
    {
        if (!root.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.String) return null;

        foreach (var line in (parameters.GetString() ?? "").Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || !fields[0].Equals("num_ctx", StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(fields[1], out var tokens) && tokens > 0 ? tokens : null;
        }
        return null;
    }

    /// <summary>Whether the model takes a picture, from the <c>capabilities</c> array a recent server publishes.
    /// <paramref name="declared"/> separates "this server describes its models" from "this server said nothing",
    /// which is the difference between an accurate empty modality list and a guess that takes screenshots away
    /// from a model that can see them.</summary>
    private static bool DeclaresVision(JsonElement root, out bool declared)
    {
        declared = false;
        if (!root.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Array) return false;
        declared = true;
        foreach (var entry in capabilities.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String
                && string.Equals(entry.GetString(), "vision", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>One running model's window, or nothing. <c>context_length</c> is the runtime window and
    /// <c>size_vram</c> is not a token count, so only the field named for it is read.</summary>
    public static IReadOnlyDictionary<string, int> ParseRunning(byte[] payload)
    {
        var running = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return running;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array) return running;

            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object) continue;
                if (TokenField.Of(model, "context_length") is not { } window) continue;
                var name = model.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String
                    ? named.GetString()
                    : model.TryGetProperty("model", out var spelled) && spelled.ValueKind == JsonValueKind.String
                        ? spelled.GetString()
                        : null;
                if (NormalizeName(name) is { Length: > 0 } key) running[key] = window;
            }
        }

        return running;
    }

    /// <summary>The record with the endpoint's own answers folded in, keeping everything the list already said.
    /// Provenance is <see cref="CapabilitySource.EndpointReported"/> whenever a window came back: this is a server
    /// describing its own model, which is the strongest claim Hub can have short of one it learned from a
    /// refusal.</summary>
    /// <summary>The record with the endpoint's own answers folded in, keeping everything the list already said.
    /// Provenance rides along from <paramref name="window"/>, because the meter has to be able to say "the server
    /// told me 4 096" and "nobody told me, so I assumed its default" in different words — the first is a fact and
    /// the second is the reason a person opens the model catalog and types a number.</summary>
    public static ModelCapabilities Merge(ModelCapabilities? known, OllamaWindow window, OllamaModelFacts facts)
    {
        IReadOnlyList<string> modalities = known?.InputModalities ?? [];
        if (facts.DeclaresCapabilities)
            modalities = facts.Vision ? new[] { "text", "image" } : new[] { "text" };
        return new ModelCapabilities
        {
            ContextTokens = window.Tokens,
            MaxOutputTokens = known?.MaxOutputTokens,
            InputModalities = modalities,
            OutputModalities = known?.OutputModalities ?? [],
            EndpointTypes = known?.EndpointTypes ?? [],
            EffortLevels = known?.EffortLevels ?? [],
            ContextStrategies = known?.ContextStrategies ?? [],
            Source = window.Source,
            ObservedAt = DateTimeOffset.Now,
        };
    }
}
