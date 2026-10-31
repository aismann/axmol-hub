using System.Net;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Attempts to revoke a ChatGPT registration's renewable session before local sign-out.</summary>
internal sealed class OpenAiSessionRevocation(HttpClient http)
{
    private const string DiscoveryUrl = "https://auth.openai.com/.well-known/openid-configuration";
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    internal static OpenAiSessionRevocation Create() => new(DefaultHttp);

    /// <summary>Returns false if revocation could not be confirmed; callers must still clear local tokens.</summary>
    public async Task<bool> RevokeAsync(OpenAiTokens tokens, CancellationToken cancellationToken)
    {
        if (!tokens.IsDynamicRegistration || string.IsNullOrWhiteSpace(tokens.RefreshToken)) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var endpoint = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                if (endpoint is null) return false;
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = tokens.RefreshToken,
                    ["token_type_hint"] = "refresh_token",
                    ["client_id"] = tokens.ClientId!,
                });
                using var response = await http.PostAsync(endpoint, form, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK) return true;
                if ((int)response.StatusCode < 500) return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException) { }
            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<Uri?> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(DiscoveryUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500) throw new HttpRequestException("OpenAI discovery is unavailable.");
            return null;
        }
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await ModelList.ReadBoundedAsync(body, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("issuer", out var issuer) || issuer.ValueKind != JsonValueKind.String
            || issuer.GetString() != "https://auth.openai.com"
            || !root.TryGetProperty("revocation_endpoint", out var value) || value.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.IdnHost != "auth.openai.com" || !endpoint.IsDefaultPort
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            return null;
        return endpoint;
    }
}
