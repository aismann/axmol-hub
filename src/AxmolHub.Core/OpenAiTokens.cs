using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxmolHub.Core;

/// <summary>The secret-store payload. Never serialized as provider or credential metadata.</summary>
public sealed record OpenAiTokens(string AccessToken, string RefreshToken, string? IdToken,
    DateTimeOffset ExpiresAt, string? AccountId)
{
    /// <summary>Absent on legacy Codex-compatible credentials.</summary>
    public int Version { get; init; }
    public string? ClientId { get; init; }
    public string? HostId { get; init; }
    public string? Issuer { get; init; }
    public string? Subject { get; init; }
    public string? Email { get; init; }
    public string? Scope { get; init; }

    /// <summary>Only verified dynamic registrations are eligible for ChatGPT plan usage.</summary>
    [JsonIgnore]
    public bool IsDynamicRegistration => Version == 1
        && !string.IsNullOrWhiteSpace(ClientId) && ClientId != "dynamic_agent_client"
        && !string.IsNullOrWhiteSpace(HostId) && Issuer == "https://auth.openai.com"
        && !string.IsNullOrWhiteSpace(Subject);

    [JsonIgnore]
    public bool HasPlanUsage => IsDynamicRegistration && (Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal);

    public string Encode() => JsonSerializer.Serialize(this);
    public static OpenAiTokens Decode(string secret)
    {
        try
        {
            var tokens = JsonSerializer.Deserialize<OpenAiTokens>(secret);
            if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
                throw new JsonException();
            return tokens;
        }
        catch (JsonException) { throw new InvalidOperationException("The OpenAI sign-in credential is invalid. Sign in again."); }
    }
}
