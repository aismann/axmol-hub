using AxmolHub.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AxmolHub.Agent;

/// <summary>Validates a ChatGPT ID token against OpenAI's signing keys and the pending transaction.</summary>
public sealed class OpenAiIdentityValidator
{
    private const string Issuer = "https://auth.openai.com";
    private const string JwksUrl = Issuer + "/.well-known/jwks.json";
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private readonly HttpClient _http;

    internal OpenAiIdentityValidator(HttpClient http) => _http = http;

    /// <summary>Creates a validator using a no-redirect transport.</summary>
    public static OpenAiIdentityValidator Create() => new(DefaultHttp);

    /// <summary>Returns the verified subject and optional email; throws without exposing token material on failure.</summary>
    public async Task<(string Subject, string? Email)> ValidateAsync(string idToken, string clientId, string nonce,
        string? expectedSubject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(clientId)
            || clientId == OpenAiDynamicRegistration.RegistrationClientId || string.IsNullOrWhiteSpace(nonce))
            throw new InvalidOperationException("The OpenAI identity verification inputs are incomplete.");
        using var response = await _http.GetAsync(JwksUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("OpenAI identity signing keys are unavailable.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await ModelList.ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
        JsonWebKeySet keys;
        try { keys = new JsonWebKeySet(System.Text.Encoding.UTF8.GetString(bytes)); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        { throw new InvalidOperationException("OpenAI identity signing keys are invalid."); }
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidateIssuer = true,
            ValidAudience = clientId,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(5),
        }).ConfigureAwait(false);
        if (!validation.IsValid || validation.SecurityToken is not JsonWebToken jwt
            || !jwt.TryGetPayloadValue<string>("sub", out var subject) || string.IsNullOrWhiteSpace(subject)
            || !jwt.TryGetPayloadValue<string>("nonce", out var tokenNonce) || tokenNonce != nonce
            || !jwt.TryGetPayloadValue<long>("iat", out _)
            || (expectedSubject is not null && subject != expectedSubject))
            throw new InvalidOperationException("OpenAI identity could not be verified.");
        jwt.TryGetPayloadValue<string>("email", out var email);
        return (subject, email);
    }
}
