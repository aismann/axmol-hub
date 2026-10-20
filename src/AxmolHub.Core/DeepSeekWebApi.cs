using System.Text.Json.Serialization;

namespace AxmolHub.Core;

/// <summary>The web tools backend selected for one model provider.</summary>
public static class WebToolBackends
{
    public const string Default = "default";
    public const string DeepSeekApi = "deepSeekApi";
}

public sealed record DeepSeekWebSearchRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("ori_question")] string? OriginalQuestion = null,
    [property: JsonPropertyName("localization")] string? Localization = null,
    [property: JsonPropertyName("need_content")] bool NeedContent = false);

public sealed record DeepSeekWebSearchResult(
    [property: JsonPropertyName("rerank_score")] double Score,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("site_name")] string? SiteName,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("authority")] int Authority);

public sealed record DeepSeekWebSearchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<DeepSeekWebSearchResult> Results);

public sealed record DeepSeekWebFetchResponse(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content);

/// <summary>One completed request to the DeepSeek Search or Fetch API.</summary>
public sealed record DeepSeekWebApiCall(string Operation, bool Billable, DateTimeOffset At);

/// <summary>The app host's authenticated DeepSeek Search/Fetch transport.</summary>
public sealed record DeepSeekWebApiBridge(
    Func<bool> Allowed,
    Func<DeepSeekWebSearchRequest, CancellationToken, Task<DeepSeekWebSearchResponse>> Search,
    Func<Uri, CancellationToken, Task<DeepSeekWebFetchResponse>> Fetch);
