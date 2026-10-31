using System.Security.Cryptography;
using System.Text;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Serializes rotating plan-token refreshes across sessions sharing a data root.</summary>
internal sealed class OpenAiDynamicTokenSession(ModelProvider provider, ProviderCredential credential,
    ISecretStore store, OpenAiDynamicTokenExchange exchange, string dataRoot)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OpenAiTokens> GetAsync(bool force, CancellationToken cancellationToken)
    {
        var observed = credential.Secret;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var crossProcess = await LockCredentialAsync(dataRoot, credential.Id, cancellationToken).ConfigureAwait(false);
            string snapshot;
            OpenAiTokens current;
            lock (credential)
            {
                EnsureSelected();
                var persisted = store.Read(credential.Id);
                if (persisted is null) throw new OpenAiReauthenticationException();
                if (credential.Secret != persisted)
                {
                    var local = OpenAiTokens.Decode(credential.Secret!);
                    var saved = OpenAiTokens.Decode(persisted);
                    if (!local.IsDynamicRegistration || !saved.IsDynamicRegistration
                        || local.ClientId != saved.ClientId || local.Subject != saved.Subject || local.HostId != saved.HostId)
                        throw new InvalidOperationException("The OpenAI credential changed. Retry the request.");
                    credential.Secret = persisted;
                }
                snapshot = credential.Secret!;
                current = OpenAiTokens.Decode(snapshot);
                if (!current.IsDynamicRegistration) throw new OpenAiReauthenticationException();
                if ((!force || snapshot != observed) && current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return current;
            }
            OpenAiTokens next;
            try { next = await exchange.RefreshAsync(current, cancellationToken).ConfigureAwait(false); }
            catch (OpenAiReauthenticationException)
            {
                lock (credential)
                {
                    EnsureSelected();
                    if (credential.Secret == snapshot && store.Read(credential.Id) == snapshot)
                    {
                        store.Delete(credential.Id);
                        credential.Secret = null;
                    }
                }
                throw;
            }
            lock (credential)
            {
                EnsureSelected();
                if (credential.Secret != snapshot || store.Read(credential.Id) != snapshot)
                    throw new InvalidOperationException("The OpenAI credential changed during refresh. Retry the request.");
                var encoded = next.Encode();
                store.Write(credential.Id, encoded);
                credential.Secret = encoded;
            }
            return next;
        }
        finally { _gate.Release(); }
    }

    private void EnsureSelected()
    {
        if (!ReferenceEquals(provider.Credential, credential) || credential.AuthKind != "openaiOAuth" || credential.Secret is null)
            throw new InvalidOperationException("The OpenAI credential changed. Retry the request.");
    }

    internal static async Task<FileStream> LockCredentialAsync(string dataRoot, string credentialId, CancellationToken token)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentialId)));
        var directory = Path.Combine(dataRoot, "ai", "openai-refresh-locks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, digest + ".lock");
        var deadline = Environment.TickCount64 + 30_000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }
}
