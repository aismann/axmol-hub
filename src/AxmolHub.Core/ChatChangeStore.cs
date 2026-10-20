using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>A complete, immutable file revision. Null BeforeHash means the file did not exist; the hash of an
/// empty file is a real value and must not be confused with absence.</summary>
public sealed record ChatFileRevision(
    string Id,
    string ConversationId,
    string Path,
    string Root,
    DateTimeOffset At,
    string? BeforeHash,
    string AfterHash,
    string State);

/// <summary>Durable content and a write journal for each successful assistant file edit. Pending records are
/// written before touching source; a crash between the source rename and journal completion can be recovered by
/// comparing the source with the recorded before and after hashes.</summary>
public static class ChatChangeStore
{
    public const string Marker = " Change ID: ";
    public const string Pending = "pending";
    public const string Complete = "complete";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string? DirectoryFor(string? dataRoot) => string.IsNullOrWhiteSpace(dataRoot)
        ? null : System.IO.Path.Combine(dataRoot, "ai", "changes");

    public static string? IdFromResult(string? result)
    {
        if (result?.IndexOf(Marker, StringComparison.Ordinal) is not { } at || at < 0) return null;
        var value = result[(at + Marker.Length)..].Split([' ', '\r', '\n'], 2)[0];
        return Guid.TryParseExact(value, "N", out _) ? value : null;
    }

    public static ChatFileRevision? Read(string? dataRoot, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || DirectoryFor(dataRoot) is not { } root) return null;
        try
        {
            var record = JsonSerializer.Deserialize<ChatFileRevision>(File.ReadAllText(RecordPath(root, id)));
            if (record is null || !string.Equals(record.Id, id, StringComparison.Ordinal)) return null;
            return record.State == Pending && RecoverState(dataRoot, record) == Complete
                ? record with { State = Complete }
                : record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string? ReadContent(string? dataRoot, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || DirectoryFor(dataRoot) is not { } root)
            return null;
        try
        {
            var bytes = File.ReadAllBytes(ObjectPath(root, hash));
            if (!string.Equals(Hash(bytes), hash, StringComparison.OrdinalIgnoreCase)) return null;
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Prepare both full LF snapshots and the pending record. Throws when any durable prerequisite fails;
    /// callers must leave the source untouched on that road.</summary>
    public static ChatFileRevision Prepare(string? dataRoot, string conversationId, string path, string rootPath,
        string? before, string after)
    {
        if (DirectoryFor(dataRoot) is not { } root || string.IsNullOrWhiteSpace(conversationId))
            throw new IOException("Hub has no data directory or conversation for a durable change snapshot.");

        var beforeHash = before is null ? null : SaveContent(root, TextEol.ToLf(before));
        var afterHash = SaveContent(root, TextEol.ToLf(after));
        var record = new ChatFileRevision(Guid.NewGuid().ToString("N"), conversationId,
            System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(rootPath), DateTimeOffset.UtcNow,
            beforeHash, afterHash, Pending);
        WriteRecord(root, record);
        return record;
    }

    public static void CompleteRevision(string? dataRoot, ChatFileRevision record)
    {
        var root = DirectoryFor(dataRoot) ?? throw new IOException("Hub data directory is unavailable.");
        WriteRecord(root, record with { State = Complete });
    }

    /// <summary>A failed or conflicted source write has no successful revision. Deletion is best effort; a
    /// surviving pending record still cannot masquerade as a completed edit.</summary>
    public static void Abandon(string? dataRoot, ChatFileRevision record)
    {
        if (DirectoryFor(dataRoot) is not { } root) return;
        try { File.Delete(RecordPath(root, record.Id)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Resolve an interrupted journal without guessing. A source matching the after snapshot proves
    /// that the replacement landed. A source matching the before snapshot may also be a later user reversal, so
    /// any other state remains pending for inspection rather than being declared a failed write.</summary>
    public static string RecoverState(string? dataRoot, ChatFileRevision record)
    {
        if (record.State == Complete) return Complete;
        try
        {
            var current = File.Exists(record.Path) ? WorkspaceTools.ReadAll(record.Path) : null;
            if (current is null && File.Exists(record.Path)) return Pending;
            var hash = current is null ? null : Hash(Encoding.UTF8.GetBytes(TextEol.ToLf(current)));
            if (hash == record.AfterHash)
            {
                CompleteRevision(dataRoot, record);
                return Complete;
            }
            return Pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Pending;
        }
    }

    private static string SaveContent(string root, string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var hash = Hash(bytes);
        var path = ObjectPath(root, hash);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return hash;
            throw new IOException("A stored change snapshot did not match its content hash.");
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return hash;
    }

    private static void WriteRecord(string root, ChatFileRevision record)
    {
        var path = RecordPath(root, record.Id);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(TextEol.ToLf(JsonSerializer.Serialize(record, Json)) + "\n");
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string RecordPath(string root, string id) => System.IO.Path.Combine(root, "records", id + ".json");
    private static string ObjectPath(string root, string hash) => System.IO.Path.Combine(root, "objects", hash[..2], hash + ".txt");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
