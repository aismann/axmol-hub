using System.Text.Json.Serialization;

namespace AxmolHub.Core;

/// <summary>Where a credential came from. It is metadata for the user ("how did I get this?"), not a behavioural switch.</summary>
public static class CredentialSources
{
    /// <summary>The user pasted a key.</summary>
    public const string ApiKey = "key";

    /// <summary>Browser sign-in minted it.</summary>
    public const string OAuth = "oauth";
}

/// <summary>
/// One saved credential for a provider — an <b>account</b>, in GitHub Copilot's terms.
///
/// <para>Hub currently binds one credential to each configured provider. Source records provenance;
/// AuthKind distinguishes a renewable OpenAI token bundle from the plain key OrcaRouter mints.</para>
///
/// <para>The secret itself is <b>not here</b>: it lives in <see cref="ISecretStore"/> keyed by <see cref="Id"/>,
/// exactly as <see cref="ModelProvider.ApiKey"/> used to. This object is the non-secret half that can be written
/// to JSON.</para>
/// </summary>
public sealed class ProviderCredential
{
    /// <summary>Stable id; also the key this credential's secret is stored under in the OS credential store.</summary>
    public string Id { get; set; } = "";

    /// <summary>The provider this credential belongs to (a manifest id such as <c>orcarouter</c>, or <c>custom-…</c>).</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>
    /// User-facing label, shown in the credential list ("Personal", "Work", an email). Auto-filled when the
    /// credential is created (the account id from OAuth, or a numbered fallback) so the list is never blank,
    /// and editable afterwards.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>One of <see cref="CredentialSources"/>. Provenance only — see the note on the class.</summary>
    public string Source { get; set; } = CredentialSources.ApiKey;

    /// <summary>Wire credential type; absent in older files means a plain API key.</summary>
    public string AuthKind { get; set; } = "apiKey";

    /// <summary>
    /// The account identity the provider reported, when it reported one (OrcaRouter returns <c>user_id</c>).
    /// Kept so a sign-in can tell "you are already connected as this account" from "this is a second account",
    /// which is the difference between a silent re-auth and a genuinely new entry in the list.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// The scope the provider actually granted, when the response carried one. Recorded rather than discarded
    /// because a mismatch is a security signal the user should be able to see after the fact.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>Validated account/client mapping, retained independently of renewable session tokens.</summary>
    public OpenAiRegistration? OpenAiRegistration { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    private int _planUsagePaused;
    /// <summary>Runtime admission pause for this account after a confirmed plan limit; resume only by user action.</summary>
    [JsonIgnore]
    public bool PlanUsagePaused
    {
        get => Volatile.Read(ref _planUsagePaused) != 0;
        set => Volatile.Write(ref _planUsagePaused, value ? 1 : 0);
    }

    /// <summary>
    /// Runtime-only: the secret, rehydrated from <see cref="ISecretStore"/> at load time and never serialized.
    /// Marked <c>[JsonIgnore]</c> for the same reason <see cref="ModelProvider.ApiKey"/> is — the JSON on disk
    /// must not contain a key even if a caller forgets to separate it.
    /// </summary>
    [JsonIgnore]
    public string? Secret { get; set; }

    /// <summary>Whether a secret is present in memory (i.e. this credential can actually be used right now).</summary>
    [JsonIgnore]
    public bool HasSecret
    {
        get
        {
            if (string.IsNullOrEmpty(Secret)) return false;
            if (AuthKind != "openaiOAuth") return true;
            try { _ = OpenAiTokens.Decode(Secret); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>
    /// What the credential list shows. A label is preferred, but an unlabelled credential must still render as
    /// something a person can act on rather than as a blank row.
    /// </summary>
    public override string ToString()
        => Label.Length > 0 ? Label : Id;
}
