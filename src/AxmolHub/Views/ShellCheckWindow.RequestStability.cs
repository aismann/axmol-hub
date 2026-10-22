using AxmolHub.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxmolHub.Agent;
using Microsoft.Extensions.AI;
using static AxmolHub.ShellCheckSupport;

namespace AxmolHub;
internal sealed class ShellRequestStabilityChecks
{
    private readonly Action<bool, string> Check;
    internal ShellRequestStabilityChecks(CheckReport report, Action<bool, string> check, ShellCheckFixture fixture)
    {
        Check = check;
    }

    internal void CheckRequestStability(MainWindow shell, ChatPanel panel)
    {
        var snapshot = Environment.GetEnvironmentVariable("AXMOL_REQUEST_SNAPSHOT");
        if (string.IsNullOrEmpty(snapshot)) return;
        var source = JsonSerializer.Deserialize<Conversation>(File.ReadAllBytes(snapshot))!;
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var session = chat.StartConversation();
        var report = new List<object>();
        var previousBodies = new Dictionary<string, string>();
        var trace = Environment.GetEnvironmentVariable("AXMOL_REQUEST_TRACE") == "1";
        var counts = trace
            ? new[] { 699 }
            : new[] { 100, 300, 600, source.Messages.Count - 4, source.Messages.Count - 2, source.Messages.Count };
        var factories = 0;
        var priorClient = chat.ClientOverride;
        string? fixtureProvider = null;
        chat.ClientOverride = (_, _) => { factories++; throw new InvalidOperationException("UI requested a model"); };
        try
        {
            // The focused shell route has no configured model. Keep this fixture independent of suite order.
            var local = chat.AddProvider("Request probe local", "http://localhost:11438/v1", "request-probe", null)
                ?? throw new InvalidOperationException("Could not create the request probe model");
            fixtureProvider = local.Id;
            if (!chat.SelectChatModel(local.Id, "request-probe"))
                throw new InvalidOperationException("Could not select the request probe model");
            foreach (var count in counts)
            {
                session.Messages = source.Messages.Take(count).ToList();
                session.LastInputTokens = 360_000;
                session.LastUsageAt = source.LastUsageAt;
                var request = chat.PreparedRequestForCheck(session.Id)
                    ?? throw new InvalidOperationException("Request probe fixture has no prepared request");
                var history = request.History;
                var system = request.SystemPrompt;
                var tools = request.Tools;
                var original = JsonSerializer.Serialize(session.Messages);
                panel.Reload();
                Relayout(shell, panel, measure: true);
                var alternate = chat.StartConversation();
                chat.OpenConversation(session.Id);
                panel.Reload();
                chat.DeleteConversation(alternate.Id);
                Check(original == JsonSerializer.Serialize(session.Messages) && factories == 0,
                    $"浏览转录不改变模型历史或发请求（{count} turns）");
                foreach (var protocol in new[] { ModelProtocols.Chat, ModelProtocols.Responses })
                {
                    var provider = AiProviderManifest.CreateBuiltIn(protocol == ModelProtocols.Chat ? "deepseek" : "openai")!;
                    provider.IsCustom = true;
                    provider.Model = protocol == ModelProtocols.Chat ? "deepseek-flash" : "gpt-6.1-sol";
                    provider.Protocol = protocol;
                    provider.MaxContextTokens = 500_000;
                    if (protocol == ModelProtocols.Responses)
                    {
                        var tokens = new OpenAiTokens("offline-access", "offline-refresh", null,
                            DateTimeOffset.MaxValue, "offline-account");
                        provider.Credential = new ProviderCredential { AuthKind = "openaiOAuth", Secret = tokens.Encode() };
                        provider.OpenAiTokenProvider = (_, _) => Task.FromResult(tokens);
                    }
                    using var wire = new RequestProbe();
                    var reasoning = new ReasoningTable();
                    using var client = ChatClientFactory.Create(provider, provider.Model, reasoning, transport: wire);
                    Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var _ in new ChatPipeline(client, reasoning).SendAsync(provider, history,
                                system, tools: tools, modelName: provider.Model)) { }
                        }
                        catch (Exception) when (wire.Body is not null) { }
                    }).GetAwaiter().GetResult();
                    if (trace)
                    {
                        var calls = source.Messages.Skip(count).Take(774 - count)
                            .Where(turn => turn.ToolName is not null).ToArray();
                        var replay = new LoopProbeClient(client, wire, calls, source.Messages);
                        var stubs = tools.Select(tool => tool is AIFunction function
                            ? (AITool)new LoopProbeFunction(function, () => replay.CurrentResult) : tool).ToArray();
                        var yieldProbe = Environment.GetEnvironmentVariable("AXMOL_REQUEST_YIELD_TRACE") == "1";
                        var yielded = false;
                        var delivered = 0;
                        Task.Run(async () =>
                        {
                            try
                            {
                                await foreach (var _ in new ChatPipeline(replay, reasoning).SendAsync(provider,
                                    history, system, tools: stubs, modelName: provider.Model,
                                    onToolCompleted: (_, _, _) => { delivered++; return Task.CompletedTask; },
                                    yieldForCompaction: yieldProbe)) { }
                            }
                            catch (ToolLoopContextBoundaryException) { yielded = true; }
                        }).GetAwaiter().GetResult();
                        if (yieldProbe) Check(yielded && delivered == 1 && replay.Rounds.Count == 1,
                            $"{protocol} 在完整工具结果之后交回压缩，未发送下一条破坏缓存的请求");
                        report.Add(new { protocol, yielded, delivered, replay.Rounds });
                        continue;
                    }
                    var body = wire.Body ?? throw new InvalidOperationException("No captured request");
                    var window = ContextBudget.For(provider, provider.Model);
                    var trimmed = ContextTrimmer.Trim(history,
                        window.ConversationRoom - ChatPipeline.ToolSchemaTokens(tools), system);
                    var first = trimmed.FirstOrDefault(turn => turn.Role != ChatRoles.System);
                    var converted = ChatPipeline.ToChatMessages(trimmed);
                    var guarded = ToolLoopContextGuard.Elide(converted,
                        window.ConversationRoom - ChatPipeline.ToolSchemaTokens(tools));
                    var changed = Enumerable.Range(0, converted.Count)
                        .Where(i => !ReferenceEquals(converted[i], guarded[i])).ToArray();
                    var commonPrefix = -1;
                    var firstChangedMessage = -1;
                    var instructionsChanged = false;
                    if (previousBodies.TryGetValue(protocol, out var priorBody))
                    {
                        var priorBytes = Encoding.UTF8.GetBytes(priorBody);
                        var currentBytes = Encoding.UTF8.GetBytes(body);
                        commonPrefix = 0;
                        while (commonPrefix < Math.Min(priorBytes.Length, currentBytes.Length)
                            && priorBytes[commonPrefix] == currentBytes[commonPrefix]) commonPrefix++;
                        using var priorJson = JsonDocument.Parse(priorBody);
                        using var currentJson = JsonDocument.Parse(body);
                        var collection = protocol == ModelProtocols.Chat ? "messages" : "input";
                        var oldItems = priorJson.RootElement.GetProperty(collection);
                        var newItems = currentJson.RootElement.GetProperty(collection);
                        for (var i = 0; i < Math.Min(oldItems.GetArrayLength(), newItems.GetArrayLength()); i++)
                            if (oldItems[i].GetRawText() != newItems[i].GetRawText()) { firstChangedMessage = i; break; }
                        instructionsChanged = protocol == ModelProtocols.Responses
                            && priorJson.RootElement.GetProperty("instructions").GetString()
                                != currentJson.RootElement.GetProperty("instructions").GetString();
                    }
                    previousBodies[protocol] = body;
                    report.Add(new { count, protocol, commonPrefix, firstChangedMessage, instructionsChanged, wire.Requests, bytes = Encoding.UTF8.GetByteCount(body),
                        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
                        sourceTurns = history.Count, keptTurns = trimmed.Count - 1,
                        guardChanged = changed.Length, guardFirstChanged = changed.FirstOrDefault(-1),
                        head = first is null ? -1 : history.ToList().IndexOf(first),
                        rawEstimate = history.Sum(ContextTrimmer.EstimateTokens) + ContextTrimmer.EstimateTokens(system)
                            + ChatPipeline.ToolSchemaTokens(tools), window.ConversationRoom,
                        measuredTriggers = ContextCompaction.ShouldCompact(360_000, window.ConversationRoom) });
                }
            }
            File.WriteAllText(snapshot + (trace ? Environment.GetEnvironmentVariable("AXMOL_REQUEST_YIELD_TRACE") == "1" ? ".yield-trace.json" : ".trace.json" : ".report.json"), TextEol.ToLf(JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true })));
        }
        finally
        {
            chat.ClientOverride = priorClient;
            chat.DeleteConversation(session.Id);
            if (fixtureProvider is not null) chat.RemoveProvider(fixtureProvider);
            if (before is not null) chat.OpenConversation(before);
            panel.Reload();
        }
    }
}
