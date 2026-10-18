using System.Net;
using System.Text;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>A single pending attempt; callbacks never escape to logs or persisted state.</summary>
public sealed class OpenAiLoginAttempt
{
    private readonly string _state;
    private readonly TaskCompletionSource<string> _code = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string AuthorizationUrl { get; }
    internal Task<string> Code => _code.Task;
    internal OpenAiLoginAttempt(string url, string state) { AuthorizationUrl = url; _state = state; }
    internal CallbackOutcome Read(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.GetLeftPart(UriPartial.Path) != OpenAiOAuthFlow.RedirectUri || !string.IsNullOrEmpty(uri.Fragment))
            return new CallbackOutcome(CallbackState.StateMismatch, null, null);
        return OrcaRouterOAuthFlow.ReadCallback(uri.Query, _state);
    }
    public bool Submit(string url)
    {
        var result = Read(url);
        if (result.State == CallbackState.Declined)
            return _code.TrySetException(new InvalidOperationException("OpenAI authorization was declined."));
        return result is { State: CallbackState.Accepted, Code: { } code } && _code.TrySetResult(code);
    }
}

/// <summary>Codex-compatible authorization code flow with loopback and manual callback entry.</summary>
public sealed class OpenAiOAuthFlow(HttpClient http)
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string RedirectUri = "http://localhost:1455/auth/callback";
    public const string TokenUrl = "https://auth.openai.com/oauth/token";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    public static string AuthorizationUrl(string challenge, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
            ["scope"] = "openid profile email offline_access", ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256", ["state"] = state,
            ["codex_cli_simplified_flow"] = "true", ["originator"] = "codex_cli_rs",
            ["id_token_add_organizations"] = "true",
        };
        return "https://auth.openai.com/oauth/authorize?" + string.Join("&", parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }
    public async Task<OpenAiTokens> SignInAsync(Action<OpenAiLoginAttempt> present,
        Action<string> openBrowser, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        var verifier = OAuthPkce.NewCodeVerifier();
        var state = OAuthPkce.NewState();
        var attempt = new OpenAiLoginAttempt(AuthorizationUrl(OAuthPkce.CodeChallenge(verifier), state), state);
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:1455/");
        try { listener.Start(); }
        catch (HttpListenerException) { /* Manual entry remains available when the fixed port is occupied. */ }
        var receive = listener.IsListening ? ReceiveAsync(listener, attempt, deadline.Token) : Task.CompletedTask;
        try
        {
            present(attempt);
            openBrowser(attempt.AuthorizationUrl);
            var code = await attempt.Code.WaitAsync(deadline.Token).ConfigureAwait(false);
            // The code is now captured; release the fixed port before the network exchange.
            deadline.Cancel();
            listener.Close();
            using var exchangeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exchangeDeadline.CancelAfter(TimeSpan.FromSeconds(45));
            return await ExchangeAsync(new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = ClientId,
                ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = RedirectUri,
            }, null, exchangeDeadline.Token).ConfigureAwait(false);
        }
        finally
        {
            deadline.Cancel();
            listener.Close();
            await receive.ConfigureAwait(false);
        }
    }
    private static async Task ReceiveAsync(HttpListener listener, OpenAiLoginAttempt attempt, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false);
                var url = context.Request.Url?.AbsoluteUri ?? "";
                var accepted = context.Request.HttpMethod == "GET"
                    && attempt.Read(url).State is CallbackState.Accepted or CallbackState.Declined;
                var bytes = Encoding.UTF8.GetBytes(accepted ? "Sign-in received. You can close this tab." : "This callback does not match the pending sign-in.");
                context.Response.StatusCode = accepted ? 200 : 400;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                try { await context.Response.OutputStream.WriteAsync(bytes, token).ConfigureAwait(false); }
                finally { context.Response.Close(); }
                if (accepted) { attempt.Submit(url); return; }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException) { }
    }
    public Task<OpenAiTokens> RefreshAsync(OpenAiTokens previous, CancellationToken token)
        => ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = ClientId,
            ["refresh_token"] = previous.RefreshToken }, previous, token);

    private async Task<OpenAiTokens> ExchangeAsync(Dictionary<string, string> fields, OpenAiTokens? previous, CancellationToken token)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(TokenUrl, content, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (previous is not null)
            {
                var rejected = response.StatusCode == HttpStatusCode.Unauthorized;
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    try
                    {
                        using var errorBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                        var error = Text(errorBody.RootElement, "error");
                        rejected = error is "invalid_grant" or "invalid_token" or "refresh_token_expired" or "refresh_token_reused" or "refresh_token_invalidated";
                    }
                    catch (JsonException) { }
                }
                if (rejected) throw new OpenAiReauthenticationException();
            }
            throw new InvalidOperationException($"OpenAI token exchange failed (HTTP {(int)response.StatusCode}).");
        }
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var root = body.RootElement;
        var access = Text(root, "access_token");
        var refresh = Text(root, "refresh_token") ?? previous?.RefreshToken;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
            throw new InvalidOperationException("OpenAI returned an incomplete token response.");
        var id = Text(root, "id_token") ?? previous?.IdToken;
        var seconds = root.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var count) && count > 0 ? count : 3600;
        return new(access, refresh, id, DateTimeOffset.UtcNow.AddSeconds(seconds), AccountOf(id) ?? AccountOf(access) ?? previous?.AccountId);
    }
    private static string? Text(JsonElement root, string key)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? AccountOf(string? jwt)
    {
        if (jwt is null) return null;
        try
        {
            var segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(segment.PadRight((segment.Length + 3) / 4 * 4, '=')));
            var root = payload.RootElement;
            return root.TryGetProperty("https://api.openai.com/auth", out var auth) ? Text(auth, "chatgpt_account_id") : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException) { return null; }
    }
}

public sealed class OpenAiReauthenticationException() : InvalidOperationException("OpenAI authorization expired or was revoked. Sign in again.");
