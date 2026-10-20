using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Authenticated HTTP transport for DeepSeek's standalone Search and Fetch APIs.</summary>
internal static class OutboundDeepSeekWebApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static DeepSeekWebApiBridge? Bridge(
        ModelProvider provider,
        HttpClient? injected,
        Func<bool> allowed,
        Action<DeepSeekWebApiCall>? record)
    {
        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(provider.ApiKey)) return null;

        return new DeepSeekWebApiBridge(allowed,
            (request, cancellation) => SearchAsync(baseUri, provider.ApiKey!, injected, record, request, cancellation),
            (uri, cancellation) => FetchAsync(baseUri, provider.ApiKey!, injected, record, uri, cancellation));
    }

    private static async Task<DeepSeekWebSearchResponse> SearchAsync(
        Uri baseUri, string apiKey, HttpClient? injected, Action<DeepSeekWebApiCall>? record,
        DeepSeekWebSearchRequest request, CancellationToken cancellationToken)
    {
        var billable = false;
        try
        {
            var result = await PostAsync<DeepSeekWebSearchRequest, DeepSeekWebSearchResponse>(
                baseUri, "search", apiKey, injected, request, cancellationToken).ConfigureAwait(false);
            if (result.Results is null) throw new JsonException("The DeepSeek Search API response omitted results.");
            billable = result.Results.Count > 0;
            return result;
        }
        finally { record?.Invoke(new DeepSeekWebApiCall("search", billable, DateTimeOffset.UtcNow)); }
    }

    private static async Task<DeepSeekWebFetchResponse> FetchAsync(
        Uri baseUri, string apiKey, HttpClient? injected, Action<DeepSeekWebApiCall>? record,
        Uri target, CancellationToken cancellationToken)
    {
        var billable = false;
        try
        {
            var result = await PostAsync<FetchBody, DeepSeekWebFetchResponse>(baseUri, "fetch", apiKey, injected,
                new FetchBody(target.ToString()), cancellationToken).ConfigureAwait(false);
            if (result.Content is null) throw new JsonException("The DeepSeek Fetch API response omitted content.");
            billable = result.Content.Length > 0;
            return result;
        }
        finally { record?.Invoke(new DeepSeekWebApiCall("fetch", billable, DateTimeOffset.UtcNow)); }
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(
        Uri baseUri, string operation, string apiKey, HttpClient? injected, TRequest body,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        using var owned = injected is null ? new HttpClient { Timeout = Timeout + TimeSpan.FromSeconds(5) } : null;
        var client = injected ?? owned!;
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUri, operation));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Accept.ParseAdd("application/json");
        message.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek {operation} API returned HTTP {(int)response.StatusCode}.");
        await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var limit = 4 * 1024 * 1024;
        var total = 0;
        while (total <= limit)
        {
            var read = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, limit + 1 - total)),
                deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            await buffer.WriteAsync(chunk.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
            total += read;
        }
        if (total > limit) throw new InvalidOperationException("DeepSeek web response exceeded Hub's 4 MiB limit.");
        return JsonSerializer.Deserialize<TResponse>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), Json)
               ?? throw new JsonException($"The DeepSeek {operation} API returned an empty response.");
    }

    private static Uri Endpoint(Uri baseUri, string operation)
    {
        var root = baseUri.ToString().TrimEnd('/');
        var path = baseUri.AbsolutePath.TrimEnd('/');
        var suffix = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? "/tools/" + operation
            : "/v1/tools/" + operation;
        return new Uri(root + suffix, UriKind.Absolute);
    }

    private sealed record FetchBody(string Url);
}
