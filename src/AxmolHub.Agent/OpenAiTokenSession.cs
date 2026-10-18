using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Serializes refreshes for one credential and rejects results from a replaced credential.</summary>
public sealed class OpenAiTokenSession(ModelProvider provider, ProviderCredential credential,
    ISecretStore store, HttpClient http)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _revoked;
    public async Task<OpenAiTokens> GetAsync(bool force, CancellationToken cancellationToken)
    {
        var observed = credential.Secret;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string snapshot;
            OpenAiTokens current;
            lock (credential)
            {
                if (!ReferenceEquals(provider.Credential, credential) || credential.AuthKind != "openaiOAuth" || credential.Secret is null)
                    throw new InvalidOperationException("The OpenAI credential changed. Retry the request.");
                if (_revoked) throw new OpenAiReauthenticationException();
                snapshot = credential.Secret;
                current = OpenAiTokens.Decode(snapshot);
                if ((!force || snapshot != observed) && current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return current;
            }
            OpenAiTokens next;
            try { next = await new OpenAiOAuthFlow(http).RefreshAsync(current, cancellationToken).ConfigureAwait(false); }
            catch (OpenAiReauthenticationException) { _revoked = true; throw; }
            lock (credential)
            {
                if (!ReferenceEquals(provider.Credential, credential) || credential.Secret != snapshot || credential.AuthKind != "openaiOAuth")
                    throw new InvalidOperationException("The OpenAI credential changed during refresh. Retry the request.");
                var encoded = next.Encode();
                store.Write(credential.Id, encoded);
                credential.Secret = encoded;
            }
            return next;
        }
        finally { _gate.Release(); }
    }
}
