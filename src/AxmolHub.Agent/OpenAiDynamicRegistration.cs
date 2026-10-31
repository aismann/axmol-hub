using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Parameters and callback validation for the ChatGPT plan dynamic-registration flow.</summary>
public static class OpenAiDynamicRegistration
{
    public const string RegistrationClientId = "dynamic_agent_client";
    public const string Resource = "https://api.openai.com/v1";
    public const string AuthorizationEndpoint = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    public const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    /// <summary>Builds the authorization URI for a new or previously selected registration.</summary>
    public static string AuthorizationUrl(string redirectUri, string hostId, string state, string nonce,
        string challenge, OpenAiTokens? registration = null, bool requestConsent = false)
    {
        ValidateRedirect(redirectUri);
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(state)
            || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(challenge))
            throw new ArgumentException("An OpenAI authorization transaction is incomplete.");
        if (registration is not null && (!registration.IsDynamicRegistration || registration.HostId != hostId))
            throw new InvalidOperationException("The OpenAI registration does not match this host.");

        var values = new Dictionary<string, string>
        {
            ["client_id"] = registration?.ClientId ?? RegistrationClientId,
            ["ext_agent_host_id"] = hostId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["resource"] = Resource,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
        };
        if (requestConsent) values["prompt"] = "consent";
        if (registration is null) values["agent_name_hint"] = "Axmol Hub";
        else
        {
            if (!string.IsNullOrWhiteSpace(registration.IdToken)) values["id_token_hint"] = registration.IdToken;
            if (!string.IsNullOrWhiteSpace(registration.Email)) values["login_hint"] = registration.Email;
        }
        return AuthorizationEndpoint + "?" + string.Join("&", values.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    /// <summary>Validates the callback and returns the code and issued client ID for exchange.</summary>
    public static (string Code, string ClientId) ReadCallback(string callback, string redirectUri,
        string expectedState, string? registeredClientId = null)
    {
        ValidateRedirect(redirectUri);
        if (string.IsNullOrWhiteSpace(expectedState)) throw new ArgumentException("Missing expected state.");
        if (!Uri.TryCreate(callback, UriKind.Absolute, out var uri)
            || uri.GetLeftPart(UriPartial.Path) != redirectUri || uri.Fragment.Length != 0)
            throw new InvalidOperationException("The OpenAI callback does not match this sign-in.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = Uri.UnescapeDataString((pair.Length == 2 ? pair[1] : "").Replace('+', ' '));
            if (!fields.TryAdd(key, value)) throw new InvalidOperationException("The OpenAI callback contains duplicate parameters.");
        }
        if (!fields.TryGetValue("state", out var state) || state != expectedState)
            throw new InvalidOperationException("The OpenAI callback state did not match.");
        if (fields.ContainsKey("error")) throw new OpenAiAuthorizationDeclinedException();
        if (!fields.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("The OpenAI callback did not contain an authorization code.");
        fields.TryGetValue("client_id", out var clientId);
        if (registeredClientId is not null && clientId is not null && clientId != registeredClientId)
            throw new InvalidOperationException("The OpenAI callback client ID did not match the selected account.");
        clientId ??= registeredClientId;
        if (string.IsNullOrWhiteSpace(clientId) || clientId == RegistrationClientId)
            throw new InvalidOperationException("The OpenAI registration did not return an issued client ID.");
        return (code, clientId);
    }

    private static void ValidateRedirect(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" || uri.Port <= 0
            || uri.AbsolutePath != "/auth/callback" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.UserInfo.Length != 0 || uri.GetLeftPart(UriPartial.Path) != redirectUri)
            throw new ArgumentException("OpenAI requires an exact 127.0.0.1 loopback callback URI.", nameof(redirectUri));
    }
}

/// <summary>The authorization server declined a state-validated sign-in attempt.</summary>
internal sealed class OpenAiAuthorizationDeclinedException() : InvalidOperationException("OpenAI authorization was declined.");
