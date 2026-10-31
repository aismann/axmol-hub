namespace AxmolHub.Agent;

/// <summary>The plan registration can no longer be refreshed: the user has to sign in again.</summary>
public sealed class OpenAiReauthenticationException()
    : InvalidOperationException("OpenAI authorization expired or was revoked. Sign in again.");
