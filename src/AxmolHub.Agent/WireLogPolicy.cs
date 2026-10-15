using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>
/// Where the diagnostic wire recording writes, or <see langword="null"/> to record nothing.
///
/// <para>Set once at startup from <c>--wire-log &lt;dir&gt;</c>. It is a static rather than another argument on
/// <see cref="ChatClientFactory.Create"/> because that factory is already reached from several call sites, and a
/// directory that only a debugging host ever sets would have to be threaded through every one of them to arrive at
/// a single <c>if</c>.</para>
/// </summary>
public static class WireLog
{
    /// <summary>The recording directory, or null when the feature is off.</summary>
    public static string? Directory { get; set; }

    private static int sequence;

    /// <summary>The next pairing number. One per HTTP request, so the request body, the response and the index
    /// line for a turn all carry the same number.</summary>
    public static int NextPair() => Interlocked.Increment(ref sequence);

    /// <summary>Writes one file under <see cref="Directory"/>. Never throws: a recording that cannot be written
    /// must not be the reason a request fails. The text is <b>not</b> folded to LF on purpose — these are the
    /// bytes the wire carried, and <see cref="WireLogPolicy"/> exists so a claim can be checked against them.</summary>
    public static void TryWrite(string name, string text)
    {
        var root = Directory;
        if (root is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, name), text);
        }
        catch (Exception)
        {
            // Swallowed on purpose. This is a diagnostic sink; the request it describes still has to go out.
        }
    }

    /// <summary>Appends one line to the index. Same no-throw rule as <see cref="TryWrite"/>.</summary>
    public static void TryAppendIndex(string line)
    {
        var root = Directory;
        if (root is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(root);
            // The index is Hub's own line, unlike the recordings above, so it is LF even when a field carried in
            // from the body — a raw `thinking` object — spans more than one line.
            File.AppendAllText(Path.Combine(root, "index.log"), TextEol.ToLf(line) + "\n");
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// Records the bytes a request actually left with, and the bytes that came back.
///
/// <para><b>Why a pipeline policy.</b> Everything above this point in the stack describes what Hub <i>intended</i>
/// to send. The connector, the raw <c>JsonPatch</c> and <see cref="ReasoningReplayPolicy"/> each rewrite the body
/// on the way out, and the only claim worth checking against a provider's behaviour is the one made about the
/// finished bytes. This is the same seam <c>WireRecorder</c> uses in the self-checks, kept in the product because
/// a bug that only appears after thirty tool rounds cannot be reproduced from a fixture.</para>
///
/// <para><b>Why the response is recorded too.</b> Without it, markup seen on screen could have been introduced
/// anywhere between the socket and the widget. The raw stream settles where it came from.</para>
///
/// <para><b>Headers are never written.</b> The bearer credential lives in them; the body carries the
/// conversation. Both are needed for diagnosis and only one of them is safe on disk.</para>
/// </summary>
public sealed class WireLogPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var pair = Begin(message);
        ProcessNext(message, pipeline, currentIndex);
        End(message, pair);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var pair = Begin(message);
        return Tee(message, pair, ProcessNextAsync(message, pipeline, currentIndex));
    }

    /// <summary>Writes the request body and returns the pairing number, or -1 when not recording.</summary>
    private static int Begin(PipelineMessage message)
    {
        if (WireLog.Directory is null) return -1;
        var pair = WireLog.NextPair();
        try
        {
            if (message.Request.Content is not { } content) return pair;
            using var buffer = new MemoryStream();
            content.WriteTo(buffer);
            var json = Encoding.UTF8.GetString(buffer.ToArray());
            WireLog.TryWrite($"{pair:D4}.request.json", json);
            Summarize(pair, message.Request.Uri?.ToString(), json);
        }
        catch (Exception)
        {
            // A body that cannot be read here is still a body the transport can send.
        }

        return pair;
    }

    /// <summary>Wraps the response stream so every byte the caller reads is also written out.
    ///
    /// <para>Only <c>ContentStream</c> is touched. <c>PipelineResponse.Content</c> throws on a response that
    /// has not been buffered, which is exactly what a streamed completion is, so asking it "is there a body"
    /// would take the recording down with the question.</para>
    ///
    /// <para>Seekability is not used as a filter: a live socket is not seekable and a test transport hands over
    /// a buffered MemoryStream, and the recording has to work on both.</para>
    /// </summary>
    private static void End(PipelineMessage message, int pair)
    {
        if (pair < 0) return;
        Stream? stream = null;
        try
        {
            stream = message.Response?.ContentStream;
        }
        catch (Exception)
        {
        }

        if (stream is null or TeeStream) return;
        try
        {
            message.Response!.ContentStream = new TeeStream(stream, $"{pair:D4}.response.log");
        }
        catch (Exception)
        {
        }
    }

    private static async ValueTask Tee(PipelineMessage message, int pair, ValueTask next)
    {
        await next.ConfigureAwait(false);
        End(message, pair);
    }

    /// <summary>
    /// One index line per request: the fields that decide the shape of the conversation, and the two counts that
    /// the replay hypothesis lives or dies on — how many assistant messages carry <c>reasoning_content</c>, and
    /// how many of those carry it as an empty string. A drift between those and the previous turn is the
    /// mis-pairing this is looking for.
    /// </summary>
    private static void Summarize(int pair, string? url, string json)
    {
        if (json.Length == 0) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var assistant = 0;
            var withReasoning = 0;
            var emptyReasoning = 0;
            var withToolCalls = 0;
            var lastAssistantHasReasoning = false;
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.TryGetProperty("role", out var role) && role.GetString() != "assistant") continue;
                    assistant++;
                    var has = false;
                    if (message.TryGetProperty("reasoning_content", out var reasoning))
                    {
                        has = true;
                        if (reasoning.ValueKind != JsonValueKind.String || reasoning.GetString() is not { Length: > 0 })
                            emptyReasoning++;
                    }

                    if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                        withToolCalls += calls.GetArrayLength();
                    withReasoning += has ? 1 : 0;
                    lastAssistantHasReasoning = has;
                }
            }

            var tools = root.TryGetProperty("tools", out var declared) && declared.ValueKind == JsonValueKind.Array
                ? declared.GetArrayLength()
                : 0;
            var maxTokens = root.TryGetProperty("max_tokens", out var cap) ? cap.ToString() : "-";
            var effort = root.TryGetProperty("reasoning_effort", out var tier) ? tier.GetString() : "-";
            var thinking = root.TryGetProperty("thinking", out var think) ? think.GetRawText() : "-";
            WireLog.TryAppendIndex(
                $"{pair:D4} {Path.GetFileName(url ?? "")} model={Prop(root, "model")} effort={effort} "
                + $"thinking={thinking} max_tokens={maxTokens} tools={tools} "
                + $"assistant={assistant} reasoning={withReasoning} empty={emptyReasoning} "
                + $"tool_calls={withToolCalls} last_assistant_reasoning={lastAssistantHasReasoning}");
        }
        catch (Exception)
        {
            WireLog.TryAppendIndex($"{pair:D4} unparseable body ({json.Length} chars)");
        }
    }

    private static string Prop(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) ? value.ToString() : "-";
}

/// <summary>
/// Forwards every byte read from an inner stream and appends them to a file, closing the file when the reader
/// finishes. Deliberately not a seekable or writable stream: the pipeline only ever reads a response.
/// </summary>
file sealed class TeeStream(Stream inner, string fileName) : Stream
{
    private readonly MemoryStream captured = new();
    private bool flushed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0) captured.Write(buffer, offset, read);
        if (read == 0) FlushToFile();
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken).ContinueWith(
            task =>
            {
                if (!task.IsFaulted && task.Result > 0) captured.Write(buffer, offset, task.Result);
                if (!task.IsFaulted && task.Result == 0) FlushToFile();
                return task.Result;
            }, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) FlushToFile();
        base.Dispose(disposing);
    }

    private void FlushToFile()
    {
        if (flushed) return;
        flushed = true;
        try
        {
            WireLog.TryWrite(fileName, Encoding.UTF8.GetString(captured.ToArray()));
        }
        catch (Exception)
        {
        }
        finally
        {
            captured.Dispose();
        }
    }
}
