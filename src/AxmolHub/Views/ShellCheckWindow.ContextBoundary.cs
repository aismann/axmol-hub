using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxmolHub.Core;
using Microsoft.Extensions.AI;

namespace AxmolHub;

public partial class ShellCheckWindow
{
    private async Task CheckContextBoundaryAsync(MainWindow shell, ChatPanel panel)
    {
        var chat = shell.Chat;
        var before = chat.ActiveConversation?.Id;
        var previous = chat.ClientOverride;
        var workspace = ScratchDirectory.Resolve("context-boundary", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var provider = chat.AddProvider("Boundary fixture", "https://offline.invalid/v1", "boundary-model", null)!;
        chat.SetModelContextTokens(provider.Id, "boundary-model", 32768);
        var session = chat.StartConversation(provider.Id);
        session.WorkspaceRoot = workspace;
        var burst = new ContextBurstClient();
        var summaries = 0;
        try
        {
            for (var i = 0; i < 8; i++) File.WriteAllText(Path.Combine(workspace, $"part-{i}.txt"), new string('x', 80_000));
            var usage = chat.TranscriptUsageForCheck(session.Id);
            var body = Math.Max(1000, ((int)(usage.Room * 0.70) - usage.Estimated) / 6 * 3);
            for (var i = 0; i < 6; i++) chat.SeedAssistantReplyForCheck(session.Id, new string('x', body));
            chat.ClientOverride = (_, id) =>
            {
                if (id == session.Id && chat.IsCompressingContext(id))
                {
                    summaries++;
                    return new ScriptedChatClient(["Context summary: continue reading the requested fixture files."]);
                }
                return burst;
            };
            chat.OpenConversation(session.Id);
            await panel.SendForCheckAsync("Read all eight fixture parts, then finish.");
            var results = session.Messages.Where(turn => turn.Role == ChatRoles.Tool).ToArray();
            Check(summaries > 0 && burst.Executed == 8 && results.Length == 8
                  && results.Select(turn => turn.ToolCallId).Distinct().Count() == 8
                  && session.Messages[^1].Text == "boundary done",
                $"工具执行内部压缩后自动续跑，八个结果各出现一次并正常结束（摘要 {summaries}，调用 {burst.Executed}，结果 {results.Length}）");
        }
        finally
        {
            chat.ClientOverride = previous;
            chat.DeleteConversation(session.Id);
            chat.RemoveProvider(provider.Id);
            if (before is not null) chat.OpenConversation(before);
            panel.Reload();
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class ContextBurstClient : IChatClient
    {
        public int Executed { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Executed < 8)
            {
                var part = Executed++;
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("boundary-" + part, "read_file",
                        new Dictionary<string, object?> { ["path"] = $"part-{part}.txt" })])
                    { ResponseId = "boundary-response-" + part, FinishReason = ChatFinishReason.ToolCalls };
            }
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "boundary done")
                { ResponseId = "boundary-response-final", FinishReason = ChatFinishReason.Stop };
            await Task.Yield();
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
