using System.Net;
using System.Text;
using System.Text.Json;
using AxmolHub.Agent;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

internal static class OpenAiChecks
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        Console.WriteLine("PASS: " + name);
    }
    private static async Task Fails(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or SecretStoreException)
        { Check(true, name); return; }
        throw new Exception("FAILED: " + name);
    }
    private static Dictionary<string, string> Query(string url)
        => url[(url.IndexOf('?') + 1)..].Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string TokenBody(string access = "access-new", string refresh = "refresh-new")
        => JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, expires_in = 3600 });

    public static async Task RunAsync(string root, bool native, bool loopback = false)
    {
        var scratch = Path.Combine(root, "openai-" + Guid.NewGuid().ToString("N"));
        var api = new KeychainFake();
        var store = new MacKeychainSecretStore(scratch, api);
        Check(store.Read("a") is null, "missing Keychain item is absent");
        store.Write("a", "first"); store.Write("a", "updated");
        Check(store.Read("a") == "updated" && api.Adds == 1 && api.Updates == 2, "Keychain creates then updates a generic password");
        Check(store.Service != new MacKeychainSecretStore(scratch + "-other", api).Service,
            "different data roots use different Keychain services");
        Check(store.Service == new MacKeychainSecretStore(Path.Combine(scratch, "."), api).Service,
            "normalized data roots share their Keychain service");
        store.Delete("a"); store.Delete("a");
        Check(store.Read("a") is null && store.Descriptor.Persists, "Keychain delete is idempotent and backend persists");
        var metadata = new ProviderCredential { Id = "a", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = "secret" };
        new CredentialStore(scratch, store).Save([metadata]);
        api.Status = -25308;
        Check(new CredentialStore(scratch, store).Load().Single() is { Id: "a", Secret: null }, "locked Keychain preserves credential metadata on startup");
        await Fails(() => { store.Write("a", "replacement"); return Task.CompletedTask; }, "locked Keychain writes fail explicitly");
        await Fails(() => { store.Delete("a"); return Task.CompletedTask; }, "denied Keychain deletion fails explicitly");
        api.Status = 0;
        store.Delete("a");
        if (native && OperatingSystem.IsMacOS())
        {
            var real = new MacKeychainSecretStore(scratch);
            try
            {
                real.Write("native-check", "unicode-秘密\nline2");
                Check(real.Read("native-check") == "unicode-秘密\nline2", "native Keychain round-trips Unicode and LF");
                real.Write("native-check", "rotated");
                Check(real.Read("native-check") == "rotated", "native Keychain updates an existing secret");
            }
            finally { real.Delete("native-check"); }
            Check(real.Read("native-check") is null, "native Keychain test item is removed");
        }

        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Check(OAuthPkce.CodeChallenge(verifier) == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE matches RFC 7636 vector");
        var parameters = Query(OpenAiOAuthFlow.AuthorizationUrl("challenge", "state"));
        Check(parameters["client_id"] == OpenAiOAuthFlow.ClientId && parameters["redirect_uri"] == OpenAiOAuthFlow.RedirectUri
            && parameters["code_challenge_method"] == "S256" && parameters["codex_cli_simplified_flow"] == "true"
            && parameters["originator"] == "codex_cli_rs" && parameters["scope"].Contains("offline_access"), "authorization URL carries Codex PKCE parameters");
        var requests = 0;
        string? challenge = null;
        var authWire = new Handler(async (request, token) =>
        {
            requests++;
            var fields = Query("?" + await request.Content!.ReadAsStringAsync(token));
            Check(request.RequestUri!.AbsoluteUri == OpenAiOAuthFlow.TokenUrl && fields["redirect_uri"] == OpenAiOAuthFlow.RedirectUri
                && OAuthPkce.CodeChallenge(fields["code_verifier"]) == challenge && fields["code"] == "good-code", "code exchange uses the original verifier and redirect URI");
            return Json(TokenBody());
        });
        using var authHttp = new HttpClient(authWire);
        OpenAiLoginAttempt? pending = null;
        var signedIn = await new OpenAiOAuthFlow(authHttp).SignInAsync(attempt =>
        {
            pending = attempt;
            var query = Query(attempt.AuthorizationUrl);
            challenge = query["code_challenge"];
            Check(!attempt.Submit(OpenAiOAuthFlow.RedirectUri + "?code=bad&state=wrong"), "wrong state does not complete sign-in");
            Check(!attempt.Submit("https://evil.example/auth/callback?code=bad&state=" + query["state"]), "foreign callback origins are rejected");
            Check(attempt.Submit(OpenAiOAuthFlow.RedirectUri + "?code=good-code&state=" + query["state"]), "manual callback completes the pending attempt");
            Check(!attempt.Submit(OpenAiOAuthFlow.RedirectUri + "?code=duplicate&state=" + query["state"]), "duplicate callback is not exchanged again");
        }, _ => { });
        Check(requests == 1 && signedIn.AccessToken == "access-new", "manual sign-in exchanges exactly once");
        await Fails(async () => await new OpenAiOAuthFlow(authHttp).SignInAsync(attempt =>
        {
            var state = Query(attempt.AuthorizationUrl)["state"];
            attempt.Submit(OpenAiOAuthFlow.RedirectUri + "?error=access_denied&state=" + state);
        }, _ => { }), "authorization denial does not exchange a code");
        using (var cancel = new CancellationTokenSource())
            await Fails(async () => await new OpenAiOAuthFlow(authHttp).SignInAsync(_ => cancel.Cancel(), _ => { }, cancel.Token), "cancelled login exits without exchange");
        await Fails(async () => await new OpenAiOAuthFlow(authHttp).SignInAsync(_ => { }, _ => { }, timeout: TimeSpan.FromMilliseconds(30)), "login timeout releases the listener");

        if (loopback)
        {
            using var occupied = new HttpListener();
            occupied.Prefixes.Add("http://localhost:1455/");
            occupied.Start();
            var fallback = await new OpenAiOAuthFlow(authHttp).SignInAsync(attempt =>
            {
                var query = Query(attempt.AuthorizationUrl);
                challenge = query["code_challenge"];
                attempt.Submit(OpenAiOAuthFlow.RedirectUri + "?code=good-code&state=" + query["state"]);
            }, _ => { });
            Check(fallback.AccessToken == "access-new" && occupied.IsListening, "occupied callback port falls back to manual entry without stopping its owner");
            occupied.Stop();
            Task? callback = null;
            using var browser = new HttpClient();
            var automatic = await new OpenAiOAuthFlow(authHttp).SignInAsync(attempt =>
            {
                var query = Query(attempt.AuthorizationUrl);
                challenge = query["code_challenge"];
                callback = Task.Run(async () =>
                {
                    using var unrelated = await browser.GetAsync("http://localhost:1455/favicon.ico");
                    Check(unrelated.StatusCode == HttpStatusCode.BadRequest, "unrelated loopback requests do not finish the attempt");
                    using var accepted = await browser.GetAsync(OpenAiOAuthFlow.RedirectUri + "?code=good-code&state=" + query["state"]);
                    Check(accepted.IsSuccessStatusCode, "automatic loopback callback receives a success response");
                });
            }, _ => { });
            await callback!;
            Check(automatic.AccessToken == "access-new", "automatic loopback callback exchanges its code");
            occupied.Start();
            Check(occupied.IsListening, "completed OAuth flow releases port 1455");
            occupied.Stop();
        }

        var tokens = new OpenAiTokens("access-old", "refresh-old", "id-secret", DateTimeOffset.UtcNow.AddSeconds(-1), "account-1");
        var credential = new ProviderCredential { Id = "openai-check", ProviderId = "openai", AuthKind = "openaiOAuth", Secret = tokens.Encode() };
        var provider = AiProviderManifest.CreateBuiltIn("openai")!;
        provider.Credential = credential;
        provider.Models.Add(new ProviderModel { Name = "fixture-model", Enabled = true });
        provider.Model = "fixture-model";
        var refreshes = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var refreshHttp = new HttpClient(new Handler(async (request, token) =>
        {
            Interlocked.Increment(ref refreshes);
            var fields = Query("?" + await request.Content!.ReadAsStringAsync(token));
            Check(fields["grant_type"] == "refresh_token" && fields["refresh_token"] == "refresh-old", "refresh uses the saved refresh token");
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(TokenBody());
        }));
        var session = new OpenAiTokenSession(provider, credential, store, refreshHttp);
        provider.OpenAiTokenProvider = session.GetAsync;
        var first = session.GetAsync(false, default);
        await entered.Task;
        var concurrent = Enumerable.Range(0, 8).Select(_ => session.GetAsync(false, default)).ToArray();
        release.SetResult();
        await first; await Task.WhenAll(concurrent);
        Check(refreshes == 1 && OpenAiTokens.Decode(store.Read(credential.Id)!).RefreshToken == "refresh-new", "concurrent refreshes are merged and rotated tokens persist");
        var saved = new CredentialStore(scratch, store);
        saved.Save([credential]);
        var json = File.ReadAllText(Path.Combine(scratch, "ai", "credentials.json"));
        Check(!json.Contains("access-new") && !json.Contains("refresh-new") && !json.Contains("id-secret"), "credential metadata contains no token material");
        Check(saved.Load().Single().AuthKind == "openaiOAuth", "OAuth credential type survives restart");
        Check(provider.ApiKey == "access-new" && ChatClientFactory.ProtocolFor(provider, provider.Model) == ModelProtocols.Responses, "OAuth bundle resolves to Responses and its access token");
        provider.Credential = new ProviderCredential { Secret = "plain-key" };
        Check(provider.ApiKey == "plain-key" && !provider.IsOpenAiOAuth, "legacy and API-key credentials remain plain keys");
        provider.Credential = credential;

        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var racingHttp = new HttpClient(new Handler(async (_, token) =>
        { refreshStarted.SetResult(); await finishRefresh.Task.WaitAsync(token); return Json(TokenBody("stale-access", "stale-refresh")); }));
        var racing = new OpenAiTokenSession(provider, credential, store, racingHttp);
        var inFlight = racing.GetAsync(true, default);
        await refreshStarted.Task;
        lock (credential) { credential.Secret = (tokens with { AccessToken = "replacement" }).Encode(); }
        store.Write(credential.Id, credential.Secret);
        finishRefresh.SetResult();
        await Fails(async () => await inFlight, "a replaced credential rejects an old refresh result");
        Check(OpenAiTokens.Decode(store.Read(credential.Id)!).AccessToken == "replacement", "old refresh cannot overwrite the new secret");
        var storageSnapshot = credential.Secret;
        using (var successHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(TokenBody("unstored", "unstored-refresh"))))))
        {
            api.Status = -25308;
            await Fails(async () => await new OpenAiTokenSession(provider, credential, store, successHttp).GetAsync(true, default),
                "refresh persistence failure is reported instead of publishing an unstored token");
            Check(credential.Secret == storageSnapshot, "failed persistence leaves the previous in-memory credential intact");
            api.Status = 0;
        }
        var deletionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var deletingHttp = new HttpClient(new Handler(async (_, token) =>
        { deletionStarted.SetResult(); await deletionRelease.Task.WaitAsync(token); return Json(TokenBody()); })))
        {
            var deletedRefresh = new OpenAiTokenSession(provider, credential, store, deletingHttp).GetAsync(true, default);
            await deletionStarted.Task;
            lock (credential) { provider.Credential = null; store.Delete(credential.Id); }
            deletionRelease.SetResult();
            await Fails(async () => await deletedRefresh, "deleting a credential rejects its outstanding refresh");
            Check(store.Read(credential.Id) is null, "a completed old refresh cannot recreate a deleted Keychain item");
            provider.Credential = credential;
            store.Write(credential.Id, credential.Secret!);
        }
        var refusedCount = 0;
        using var refusedHttp = new HttpClient(new Handler((_, _) => { refusedCount++; return Task.FromResult(Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest)); }));
        var refused = new OpenAiTokenSession(provider, credential, store, refusedHttp);
        await Fails(async () => await refused.GetAsync(true, default), "invalid refresh requires sign-in again");
        await Fails(async () => await refused.GetAsync(true, default), "revoked credentials do not repeatedly refresh");
        Check(refusedCount == 1, "revocation is remembered until credentials change");
        using var offlineHttp = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline")));
        var before = credential.Secret;
        try { await new OpenAiTokenSession(provider, credential, store, offlineHttp).GetAsync(true, default); }
        catch (HttpRequestException) { }
        Check(credential.Secret == before, "network failures preserve saved credentials");

        var catalog = OpenAiCodexModels.Parse(Encoding.UTF8.GetBytes("""
            {"models":[{"slug":"fixture-model","context_window":128000,"supported_reasoning_levels":[{"effort":"low"},{"effort":"high"}],"default_reasoning_level":"low"},{"slug":"hidden","visibility":"hide"}]}
            """));
        Check(catalog.Models.SequenceEqual(["fixture-model"]) && catalog.Capabilities["fixture-model"].ContextTokens == 128000
            && catalog.ReasoningModels["fixture-model"].Efforts.SequenceEqual(["low", "high"]), "Codex model metadata maps into Hub capabilities");
        var catalogFixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "openai-codex-models.json"));
        var realCatalog = OpenAiCodexModels.Parse(catalogFixture);
        Check(realCatalog.Reachable && realCatalog.Models.Contains("gpt-6.1-sol") && realCatalog.Capabilities["gpt-6.1-sol"].ContextTokens > 0,
            "the real Codex catalog exposes usable models and their context windows");
        using (var catalogHttp = new HttpClient(new Handler((request, _) =>
        {
            Check(request.RequestUri!.AbsolutePath == "/backend-api/codex/models"
                && Query(request.RequestUri.AbsoluteUri)["client_version"] == "0.162.0",
                "catalog requests advertise the supported Codex version instead of the obsolete version that hides all chat models");
            Check(request.Headers.Authorization?.Parameter == "catalog-access", "catalog requests use the OAuth access token");
            return Task.FromResult(Json(Encoding.UTF8.GetString(catalogFixture)));
        })))
        {
            provider.OpenAiTokenProvider = (_, _) => Task.FromResult(tokens with { AccessToken = "catalog-access" });
            var fetchedCatalog = await OpenAiCodexModels.FetchAsync(catalogHttp, provider, default);
            Check(fetchedCatalog.Reachable && fetchedCatalog.Models.SequenceEqual(realCatalog.Models),
                "the model fetch path preserves the usable rows from a real Codex catalog");
        }
        var adapted = OpenAiCodexRequestPolicy.Adapt("""
            {"store":true,"max_output_tokens":123,"temperature":1,"input":[{"role":"system","content":"instructions"},{"type":"function_call","id":"item1","call_id":"call1","name":"read_file","arguments":"{}"},{"type":"function_call_output","call_id":"call1","output":"file contents"}]}
            """);
        using (var body = JsonDocument.Parse(adapted))
            Check(!body.RootElement.GetProperty("store").GetBoolean() && body.RootElement.GetProperty("stream").GetBoolean()
                && body.RootElement.GetProperty("instructions").GetString() == "instructions"
                && !body.RootElement.TryGetProperty("max_output_tokens", out _)
                && body.RootElement.GetProperty("input").GetArrayLength() == 2 && adapted.Contains("call1") && adapted.Contains("file contents"),
                "Codex request adapts instructions while preserving complete tool calls and results");
        var forces = new List<bool>();
        provider.OpenAiTokenProvider = (force, _) => { forces.Add(force); return Task.FromResult(tokens with { AccessToken = force ? "after401" : "before401" }); };
        var sends = 0;
        using (var client = new HttpClient(new OpenAiCodexTransport(provider, new Handler(async (request, token) =>
        {
            sends++;
            Check(request.Headers.Authorization?.Parameter == (sends == 1 ? "before401" : "after401")
                && request.Headers.GetValues("ChatGPT-Account-Id").Single() == "account-1", "each request receives the current bearer token and account ID");
            Check(await request.Content!.ReadAsStringAsync(token) == "body", "401 retry preserves the request body");
            return Json("{}", HttpStatusCode.Unauthorized);
        }))))
        {
            using var response = await client.PostAsync(OpenAiCodexTransport.BaseUrl + "/responses", new StringContent("body"));
            Check(sends == 2 && forces.SequenceEqual([false, true]) && response.StatusCode == HttpStatusCode.Unauthorized, "401 forces at most one refresh and one retry");
            await Fails(async () => await client.GetAsync("https://example.com/responses"), "subscription bearer tokens cannot reach foreign endpoints");
        }
        var sdkBodies = new List<string>();
        var sdkRounds = 0;
        provider.OpenAiTokenProvider = (_, _) => Task.FromResult(tokens with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        using (var sdk = ChatClientFactory.Create(provider, null, null, transport: new Handler(async (request, token) =>
        {
            Check(request.RequestUri!.AbsoluteUri == OpenAiCodexTransport.BaseUrl + "/responses", "SDK requests reach the Codex Responses endpoint");
            sdkBodies.Add(await request.Content!.ReadAsStringAsync(token));
            sdkRounds++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(StreamEvents(sdkRounds == 1), Encoding.UTF8, "text/event-stream"),
            };
        })))
        {
            var history = new List<ChatMessage> { new(ChatRole.System, "Test instructions"), new(ChatRole.User, "Read the file") };
            var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "file contents", "read_file")] };
            var result = await sdk.GetResponseAsync(history, options);
            var calls = result.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToList();
            Check(calls.Count == 1 && calls[0].CallId == "call1" && calls[0].Name == "read_file", "SDK streaming response reconstructs a tool call for full-response callers");
            history.AddRange(result.Messages);
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call1", "file contents")]));
            var text = new StringBuilder();
            await foreach (var update in sdk.GetStreamingResponseAsync(history, options)) text.Append(update.Text);
            Check(text.ToString() == "done" && sdkRounds == 2, "SDK streams the answer after a complete tool round trip");
            using var firstBody = JsonDocument.Parse(sdkBodies[0]);
            using var secondBody = JsonDocument.Parse(sdkBodies[1]);
            Check(firstBody.RootElement.GetProperty("instructions").GetString() == "Test instructions"
                && !firstBody.RootElement.GetProperty("store").GetBoolean()
                && secondBody.RootElement.GetProperty("input").EnumerateArray().Any(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output")
                && sdkBodies[1].Contains("file contents") && sdkBodies[1].Contains("call1"), "SDK serializes stateless instructions and tool results on the second request");
        }
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
    }

    private static string StreamEvents(bool tool)
    {
        var item = tool
            ? JsonSerializer.SerializeToElement(new { id = "fc1", type = "function_call", call_id = "call1", name = "read_file", arguments = "{}", status = "completed" })
            : JsonSerializer.SerializeToElement(new { id = "msg1", type = "message", role = "assistant", status = "completed",
                content = new[] { new { type = "output_text", text = "done", annotations = Array.Empty<object>() } } });
        object Response(string status, object[] output) => new { id = "resp1", @object = "response", created_at = 1, model = "fixture-model", status, output };
        var events = new List<object>
        {
            new { type = "response.created", response = Response("in_progress", []) },
            new { type = "response.output_item.added", output_index = 0, item },
        };
        if (tool)
        {
            events.Add(new { type = "response.function_call_arguments.delta", item_id = "fc1", output_index = 0, delta = "{}" });
            events.Add(new { type = "response.function_call_arguments.done", item_id = "fc1", output_index = 0, arguments = "{}" });
        }
        else
        {
            events.Add(new { type = "response.content_part.added", item_id = "msg1", output_index = 0, content_index = 0,
                part = new { type = "output_text", text = "", annotations = Array.Empty<object>() } });
            events.Add(new { type = "response.output_text.delta", item_id = "msg1", output_index = 0, content_index = 0, delta = "done" });
            events.Add(new { type = "response.output_text.done", item_id = "msg1", output_index = 0, content_index = 0, text = "done" });
        }
        events.Add(new { type = "response.output_item.done", output_index = 0, item });
        events.Add(new { type = "response.completed", response = Response("completed", [item]) });
        return string.Concat(events.Select(value =>
        {
            var json = JsonSerializer.Serialize(value);
            var type = JsonDocument.Parse(json).RootElement.GetProperty("type").GetString();
            return "event: " + type + "\ndata: " + json + "\n\n";
        }));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class KeychainFake : IMacKeychainApi
    {
        private readonly Dictionary<(string, string), string> _items = [];
        public int Status, Adds, Updates;
        public (int Status, string? Value) Read(string service, string account)
            => Status != 0 ? (Status, null) : _items.TryGetValue((service, account), out var value) ? (0, value) : (MacKeychainSecretStore.NotFound, null);
        public int Add(string service, string account, string value)
        { Adds++; if (Status != 0) return Status; _items[(service, account)] = value; return 0; }
        public int Update(string service, string account, string value)
        { Updates++; if (Status != 0) return Status; if (!_items.ContainsKey((service, account))) return MacKeychainSecretStore.NotFound; _items[(service, account)] = value; return 0; }
        public int Delete(string service, string account)
        { if (Status != 0) return Status; return _items.Remove((service, account)) ? 0 : MacKeychainSecretStore.NotFound; }
    }
}
