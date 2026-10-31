namespace AxmolHub.Core;

/// <summary>Non-secret identity retained after tokens are cleared; never contains a token or login hint token.</summary>
public sealed record OpenAiRegistration(string ClientId, string HostId, string Issuer, string Subject, string? Email)
{
    public static OpenAiRegistration FromTokens(OpenAiTokens tokens)
    {
        if (!tokens.IsDynamicRegistration) throw new InvalidOperationException("Invalid OpenAI registration.");
        return new(tokens.ClientId!, tokens.HostId!, tokens.Issuer!, tokens.Subject!, tokens.Email);
    }

    /// <summary>Builds an authorization-only context. Empty tokens must never be used for inference or refresh.</summary>
    public OpenAiTokens AuthorizationContext() => new("", "", null, DateTimeOffset.MinValue, null)
    {
        Version = 1, ClientId = ClientId, HostId = HostId, Issuer = Issuer, Subject = Subject, Email = Email,
    };
}
