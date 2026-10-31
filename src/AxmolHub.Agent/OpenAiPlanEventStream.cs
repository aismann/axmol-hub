using System.Text;
using System.Text.Json;

namespace AxmolHub.Agent;

/// <summary>Requires a terminal Responses event before a plan stream can finish successfully.</summary>
internal sealed class OpenAiPlanEventStream(Stream inner, HttpContent owner, Action? onUsageLimit = null) : Stream
{
    private readonly MemoryStream _line = new();
    private bool _completed;
    private bool _ended;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (count != 0) Inspect(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (!buffer.IsEmpty) Inspect(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            if (!_ended)
            {
                _ended = true;
                if (_line.Length > 0) InspectLine();
                if (!_completed) throw new IOException("The ChatGPT plan response stream ended before response.completed. Retry the request.");
            }
            return;
        }
        foreach (var value in bytes)
        {
            if (value == (byte)'\n') InspectLine();
            else
            {
                if (_line.Length >= 1024 * 1024) throw new IOException("The ChatGPT plan response event is too large.");
                _line.WriteByte(value);
            }
        }
    }

    private void InspectLine()
    {
        var line = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
        _line.SetLength(0);
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var data = line[5..].TrimStart();
        if (data == "[DONE]")
        {
            if (!_completed) throw new IOException("The ChatGPT plan response stream ended before response.completed. Retry the request.");
            return;
        }
        JsonDocument document;
        try { document = JsonDocument.Parse(data); }
        catch (JsonException) { return; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            switch (type.GetString())
            {
                case "response.completed": _completed = true; break;
                case "response.incomplete": throw new IOException("The ChatGPT plan response was incomplete. Retry the request.");
                case "response.failed":
                    var code = root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                        && response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString() : null;
                    if (code == "subscription_sharing_usage_limit_exceeded")
                    {
                        onUsageLimit?.Invoke();
                        throw new IOException("ChatGPT plan usage limit reached. Manage usage in ChatGPT settings, then resume plan requests in provider settings.");
                    }
                    if (code == "subscription_sharing_usage_unavailable")
                        throw new IOException("ChatGPT plan usage is temporarily unavailable. Retry later.");
                    throw new IOException("The ChatGPT plan response failed" + (IsSafeCode(code) ? " (" + code + ")" : "") + ".");
                case "error": throw new IOException("The ChatGPT plan response stream reported an error.");
            }
        }
    }

    private static bool IsSafeCode(string? code) => code is { Length: > 0 and <= 100 }
        && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _line.Dispose(); owner.Dispose(); }
        base.Dispose(disposing);
    }
}
