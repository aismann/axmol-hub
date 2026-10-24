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
    string? AfterHash,
    string State,
    ChangeLineCounts? Lines = null);

public sealed record ChatFileSnapshot(string Before, string After);

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
            var record = JsonSerializer.Deserialize<ChatFileRevision>(File.ReadAllText(RecordPath(root, id)), StateStore.ReadOptions);
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

    public static IReadOnlyList<ChatFileRevision> ForConversation(string? dataRoot, string conversationId)
    {
        if (DirectoryFor(dataRoot) is not { } root || string.IsNullOrWhiteSpace(conversationId)) return [];
        var records = new List<ChatFileRevision>();
        try
        {
            var directory = System.IO.Path.Combine(root, "records");
            if (!Directory.Exists(directory)) return records;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(file);
                if (Read(dataRoot, id) is { } record && record.ConversationId == conversationId)
                    records.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return records.OrderBy(record => record.At).ToArray();
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

    public static ChatFileSnapshot? ReadSnapshot(string? dataRoot, string id, out UndoCopyState state)
    {
        state = UndoCopyState.MissingSnapshot;
        var revision = Read(dataRoot, id);
        if (revision is null) return null;
        if (revision.State != Complete)
        {
            state = UndoCopyState.PendingSnapshot;
            return null;
        }
        var before = revision.BeforeHash is null ? "" : ReadContent(dataRoot, revision.BeforeHash);
        var after = revision.AfterHash is null ? "" : ReadContent(dataRoot, revision.AfterHash);
        if ((revision.BeforeHash is not null && before is null)
            || (revision.AfterHash is not null && after is null))
        {
            state = UndoCopyState.Unreadable;
            return null;
        }
        state = UndoCopyState.Read;
        return new ChatFileSnapshot(before!, after!);
    }

    /// <summary>Prepare both full LF snapshots and the pending record. Throws when any durable prerequisite fails;
    /// callers must leave the source untouched on that road.</summary>
    public static ChatFileRevision Prepare(string? dataRoot, string conversationId, string path, string rootPath,
        string? before, string? after)
    {
        if (DirectoryFor(dataRoot) is not { } root || string.IsNullOrWhiteSpace(conversationId))
            throw new IOException("Hub has no data directory or conversation for a durable change snapshot.");

        var beforeHash = before is null ? null : SaveContent(root, TextEol.ToLf(before));
        var afterHash = after is null ? null : SaveContent(root, TextEol.ToLf(after));
        var record = new ChatFileRevision(Guid.NewGuid().ToString("N"), conversationId,
            System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(rootPath), DateTimeOffset.UtcNow,
            beforeHash, afterHash, Pending, ToLineCounts(FileDiff.CountChangedLines(before, after)));
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

    public static UndoVerdict Restore(string? dataRoot, string conversationId, string id,
        WorkspaceSandbox sandbox, WorkspaceGuards guards, out string path, out string? inverseId)
    {
        path = "";
        inverseId = null;
        if (Read(dataRoot, id) is not { } revision)
            return UndoVerdict.CopyMissing;
        if (revision.AfterHash is null) return UndoVerdict.CopyMissing;

        var resolved = WorkspacePaths.ResolveWrite(sandbox, revision.Path, guards);
        if (!resolved.IsAllowed) return UndoVerdict.RefusedPath;
        path = resolved.Display;
        using var write = TextFile.AcquireWrite(resolved.Full);
        var permitted = WorkspacePaths.ResolveWrite(sandbox, revision.Path, guards);
        if (!permitted.IsAllowed || permitted.Full != resolved.Full) return UndoVerdict.RefusedPath;
        // Keep the existing whole-revision contract; the editor's smaller opening budget is not an undo limit.
        var baseline = TextFile.Read(resolved.Full, int.MaxValue);
        if (baseline.State == TextFileState.Missing) return UndoVerdict.TargetMissing;
        if (baseline.State != TextFileState.Text) return UndoVerdict.NotText;
        var current = baseline.Text;
        if (Hash(Encoding.UTF8.GetBytes(TextEol.ToLf(current))) != revision.AfterHash)
            return UndoVerdict.ChangedSince;

        var before = revision.BeforeHash is null ? null : ReadContent(dataRoot, revision.BeforeHash);
        if (revision.BeforeHash is not null && before is null) return UndoVerdict.CopyMissing;
        ChatFileRevision inverse;
        try { inverse = Prepare(dataRoot, conversationId, resolved.Full, resolved.Root, current, before); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return UndoVerdict.WriteFailed;
        }

        var replaced = write.Replace(() => WorkspacePaths.ResolveWrite(sandbox, revision.Path, guards),
            baseline.Bytes, before);
        if (replaced.Verdict != TextReplaceVerdict.Written)
        {
            Abandon(dataRoot, inverse);
            return replaced.Verdict switch
            {
                TextReplaceVerdict.RefusedPath => UndoVerdict.RefusedPath,
                TextReplaceVerdict.ChangedSince => UndoVerdict.ChangedSince,
                _ => UndoVerdict.WriteFailed
            };
        }

        // A landed inverse must retain its pending journal even if completing the record fails. Read() can
        // recover it from the durable after-image; abandoning it here would lose a successful source change.
        try { CompleteRevision(dataRoot, inverse); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        inverseId = inverse.Id;
        return UndoVerdict.Reverted;
    }

    /// <summary>Delete a session's unreferenced records, then content no remaining record uses. Any uncertainty
    /// keeps data on disk; cleanup must never turn a readable history into a missing snapshot.</summary>
    public static void PruneSession(string? dataRoot, IReadOnlySet<string> liveConversationIds,
        IReadOnlySet<string> referencedIds)
    {
        if (DirectoryFor(dataRoot) is not { } root) return;
        try
        {
            var recordsDirectory = System.IO.Path.Combine(root, "records");
            if (!Directory.Exists(recordsDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(recordsDirectory, "*.json"))
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(file);
                var record = Read(dataRoot, id);
                if (record is not null && !liveConversationIds.Contains(record.ConversationId)
                    && !referencedIds.Contains(id)) File.Delete(file);
            }

            var liveHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(recordsDirectory, "*.json"))
            {
                var record = JsonSerializer.Deserialize<ChatFileRevision>(File.ReadAllText(file), StateStore.ReadOptions);
                if (record?.BeforeHash is { } before) liveHashes.Add(before);
                if (record?.AfterHash is { } after) liveHashes.Add(after);
            }
            var objects = System.IO.Path.Combine(root, "objects");
            if (!Directory.Exists(objects)) return;
            foreach (var file in Directory.EnumerateFiles(objects, "*.txt", SearchOption.AllDirectories))
                if (!liveHashes.Contains(System.IO.Path.GetFileNameWithoutExtension(file))) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The session remains deleted; a partial prune only leaks snapshots, it cannot lose a referenced one.
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

    private static ChangeLineCounts ToLineCounts((int Added, int Removed) lines)
        => new(lines.Added, lines.Removed);
}
