using System.Net;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Exchanges dynamic registration authorization codes and rotating refresh tokens.</summary>
internal sealed class OpenAiDynamicTokenExchange(HttpClient http, OpenAiIdentityValidator validator)
{
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public static OpenAiDynamicTokenExchange Create() => new(DefaultHttp, OpenAiIdentityValidator.Create());

    public async Task<OpenAiTokens> ExchangeCodeAsync(string code, string verifier, string redirectUri,
        string clientId, string hostId, string nonce, OpenAiTokens? previous, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(verifier)
            || string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(nonce)
            || string.IsNullOrWhiteSpace(clientId) || clientId == OpenAiDynamicRegistration.RegistrationClientId)
            throw new InvalidOperationException("The OpenAI authorization transaction is incomplete.");
        if (previous is not null && (!previous.IsDynamicRegistration || previous.ClientId != clientId || previous.HostId != hostId))
            throw new InvalidOperationException("The OpenAI registration does not match the selected account.");
        using var result = await PostAsync(new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri, ["resource"] = OpenAiDynamicRegistration.Resource,
        }, false, cancellationToken).ConfigureAwait(false);
        var root = result.RootElement;
        var access = Required(root, "access_token");
        var refresh = Required(root, "refresh_token");
        var id = Required(root, "id_token");
        var scope = Required(root, "scope");
        RequireBearer(root);
        var expiry = Expiration(root);
        var identity = await validator.ValidateAsync(id, clientId, nonce, previous?.Subject, cancellationToken).ConfigureAwait(false);
        return new OpenAiTokens(access, refresh, id, expiry, null)
        {
            Version = 1, ClientId = clientId, HostId = hostId, Issuer = "https://auth.openai.com",
            Subject = identity.Subject, Email = identity.Email, Scope = scope,
        };
    }

    public async Task<OpenAiTokens> RefreshAsync(OpenAiTokens previous, CancellationToken cancellationToken)
    {
        if (!previous.IsDynamicRegistration) throw new OpenAiReauthenticationException();
        using var result = await PostAsync(new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = previous.ClientId!,
            ["refresh_token"] = previous.RefreshToken, ["resource"] = OpenAiDynamicRegistration.Resource,
        }, true, cancellationToken).ConfigureAwait(false);
        var root = result.RootElement;
        RequireBearer(root);
        return previous with
        {
            AccessToken = Required(root, "access_token"), RefreshToken = Required(root, "refresh_token"),
            ExpiresAt = Expiration(root), Scope = Text(root, "scope") ?? previous.Scope,
        };
    }

    private async Task<JsonDocument> PostAsync(Dictionary<string, string> fields, bool refresh, CancellationToken token)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(OpenAiDynamicRegistration.TokenEndpoint, content, token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = await ModelList.ReadBoundedAsync(stream, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (refresh)
            {
                try
                {
                    using var error = JsonDocument.Parse(bytes);
                    var code = Text(error.RootElement, "error");
                    if (code is "invalid_grant" or "invalid_refresh_token" or "token_expired"
                        or "refresh_token_expired" or "refresh_token_reused" or "refresh_token_invalidated")
                        throw new OpenAiReauthenticationException();
                    if (code == "invalid_client")
                        throw new InvalidOperationException("OpenAI rejected the registered client configuration (invalid_client).");
                }
                catch (JsonException) { }
            }
            throw new InvalidOperationException($"OpenAI token exchange failed (HTTP {(int)response.StatusCode}).");
        }
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new InvalidOperationException("OpenAI returned an invalid token response."); }
    }

    private static string? Text(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement root, string name)
        => Text(root, name) is { Length: > 0 } value ? value : throw new InvalidOperationException("OpenAI returned an incomplete token response.");
    private static void RequireBearer(JsonElement root)
    {
        if (!string.Equals(Required(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenAI returned an unsupported token type.");
    }
    private static DateTimeOffset Expiration(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("expires_in", out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var seconds) || seconds <= 0)
            throw new InvalidOperationException("OpenAI returned an invalid token expiration.");
        return DateTimeOffset.UtcNow.AddSeconds(seconds);
    }
}
