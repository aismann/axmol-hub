using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>The secret-store payload. Never serialized as provider or credential metadata.</summary>
public sealed record OpenAiTokens(string AccessToken, string RefreshToken, string? IdToken,
    DateTimeOffset ExpiresAt, string? AccountId)
{
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
