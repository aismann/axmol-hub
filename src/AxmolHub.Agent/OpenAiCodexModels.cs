using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>The subscription catalog is separate from the API-key catalog.</summary>
public static class OpenAiCodexModels
{
    // A Codex protocol compatibility version, independent of the Hub release number.
    public const string CatalogClientVersion = "0.99.0";

    public static async Task<ModelFetchResult> FetchAsync(HttpClient http, ModelProvider provider, CancellationToken token)
    {
        try
        {
            var get = provider.OpenAiTokenProvider ?? throw new InvalidOperationException("OpenAI credential service is unavailable.");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var tokens = await get(attempt != 0, token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCodexTransport.BaseUrl + "/models?client_version=" + CatalogClientVersion);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                if (tokens.AccountId is { Length: > 0 } account) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
                request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (!response.IsSuccessStatusCode) return ModelFetchResult.Unreachable($"OpenAI catalog returned HTTP {(int)response.StatusCode}.");
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return Parse(await ModelList.ReadBoundedAsync(stream, token).ConfigureAwait(false));
            }
            return ModelFetchResult.Unreachable("OpenAI authorization was rejected.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException or IOException)
        { return ModelFetchResult.Unreachable(ex is OpenAiReauthenticationException ? ex.Message : "The OpenAI model catalog could not be read."); }
    }
    public static ModelFetchResult Parse(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return ModelFetchResult.Unreachable("OpenAI returned an invalid model catalog.");
        var names = new List<string>();
        var capabilities = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
        var reasoning = new Dictionary<string, AiModelReasoning>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models.EnumerateArray().Take(ModelList.MaxModels))
        {
            if (!model.TryGetProperty("slug", out var slug) || slug.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(slug.GetString())) continue;
            if (model.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "hide") continue;
            var name = slug.GetString()!;
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            names.Add(name);
            int? context = model.TryGetProperty("context_window", out var window) && window.TryGetInt32(out var size) && size > 0 ? size : null;
            var efforts = new List<string>();
            if (model.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                foreach (var level in levels.EnumerateArray())
                    if (level.TryGetProperty("effort", out var effort) && effort.GetString() is { } value && ChatReasoningEfforts.IsTier(value) && !efforts.Contains(value)) efforts.Add(value);
            capabilities[name] = new ModelCapabilities { ContextTokens = context, EndpointTypes = ["responses"], EffortLevels = efforts, Source = CapabilitySource.EndpointReported, ObservedAt = DateTimeOffset.UtcNow };
            if (efforts.Count > 0)
                reasoning[name] = new AiModelReasoning { Efforts = efforts, DefaultEffort = model.TryGetProperty("default_reasoning_level", out var defaultLevel) && ChatReasoningEfforts.IsTier(defaultLevel.GetString()) ? defaultLevel.GetString() : null };
        }
        return new ModelFetchResult(names, null) { Capabilities = capabilities, ReasoningModels = reasoning };
    }
}
