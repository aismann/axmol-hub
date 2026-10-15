using System.Text;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>
/// Turns an attached folder into the text that rides on the user's turn.
///
/// <para>Two limits, for two different problems. The byte limits below bound what is read <i>off disk</i> — they
/// keep a folder of build artifacts from stalling the UI. <see cref="CeilingTokens"/> bounds what is put
/// <i>on the wire</i>, and it is that one that decides whether the turn fits the model's window: 160 KiB of
/// Chinese is tens of thousands of tokens, and against a window that small the whole attachment used to be
/// dropped by the trimmer along with the question it belonged to.</para>
/// </summary>
internal static class ChatContextReader
{
    private const int MaxFiles = 12;
    private const int MaxFileBytes = 32 * 1024;
    private const int MaxTotalBytes = 160 * 1024;
    private const int MaxScannedEntries = 2000;

    /// <summary>How much of one model's window a single upload may take: a quarter of it, floored so a small
    /// local model still gets something useful, and capped so a folder cannot spend a large window's slack
    /// before the first question has been answered.</summary>
    public static int CeilingTokens(int windowTokens) => Math.Clamp(windowTokens / 4, 1024, 32_768);

    /// <summary>How large one file picked or dropped by hand may be before the composer refuses it. Larger than
    /// the 32 KiB the folder scan allows per file, on purpose: that number is a guess about files found while
    /// walking a folder nobody chose, while this is one file a person pointed at — and what a single file costs
    /// the prompt is the token ceiling's business rather than the byte count's.</summary>
    public const int MaxAttachedFileBytes = 1024 * 1024;

    /// <summary>Why one offered file cannot ride along as text, or that it can. Asked before anything is read,
    /// and the rule is the extension — the same one <c>read_file</c> and the folder scan answer to, so a file Hub
    /// will read is a file it will attach, and no megabyte moves for a draft that may never be sent.</summary>
    public enum FileAttachmentVerdict
    {
        Accepted,
        Unreadable,
        TooLarge,
    }

    public static FileAttachmentVerdict AdmitFile(string path)
    {
        if (!File.Exists(path) || !WorkspacePaths.IsTextFile(Path.GetFileName(path)))
            return FileAttachmentVerdict.Unreadable;
        return new FileInfo(path).Length > MaxAttachedFileBytes
            ? FileAttachmentVerdict.TooLarge
            : FileAttachmentVerdict.Accepted;
    }

    /// <summary>One attachment as the text that rides on the user's turn, whether it named a folder or a single
    /// file. One entry point because the caller has only what the chip remembers, and reading a file as a folder
    /// is the failure this dispatch exists to make impossible.</summary>
    public static string Read(string path, string displayName, int windowTokens)
        => Directory.Exists(path)
            ? ReadFolder(path, displayName, windowTokens)
            : ReadFile(path, displayName, windowTokens);

    /// <summary>
    /// One file a person picked, as the text of the turn it belongs to. The file is read whole and the token
    /// ceiling is what shortens it: the folder scan skips an oversized file silently, which is right when the
    /// assistant is guessing and wrong when somebody pointed at this one, so the shortening is said out loud in
    /// the block instead — the same head-and-tail shape the folder blocks use.
    /// </summary>
    public static string ReadFile(string path, string displayName, int windowTokens)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException($"The attached file is no longer there: {path}", path);
        var text = WorkspaceTools.ReadAll(path)
                   ?? throw new InvalidDataException($"The attached file is not UTF-8 text: {path}");

        var ceilingTokens = CeilingTokens(windowTokens);
        var output = new StringBuilder();
        // \n rather than the host's newline: this block is pasted into the request, and the file it quotes was
        // read into it verbatim.
        output.Append($"Attached file: {displayName}").Append('\n');
        output.Append($"{file.Length} bytes; the whole attachment is limited to about {ceilingTokens} tokens of "
                      + "the model's window.").Append('\n');
        output.Append('\n');
        output.Append(ToolResultCap.ApplyTokenBudget(text, ceilingTokens)).Append('\n');
        return output.ToString();
    }

    public static string ReadFolder(string path, string displayName, int windowTokens)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The selected folder no longer exists: {root}");

        var files = new List<string>();
        var skippedLarge = 0;
        var skippedBinary = 0;
        var scannedEntries = 0;
        var scanLimitReached = false;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && files.Count < MaxFiles && !scanLimitReached)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;

            foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (++scannedEntries > MaxScannedEntries)
                {
                    scanLimitReached = true;
                    break;
                }

                if (!WorkspacePaths.IsExcludedDirectory(Path.GetFileName(child))) pending.Push(child);
            }

            if (scanLimitReached) break;
            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (++scannedEntries > MaxScannedEntries)
                {
                    scanLimitReached = true;
                    break;
                }

                if (!WorkspacePaths.IsTextFile(Path.GetFileName(file))) continue;

                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                var length = new FileInfo(file).Length;
                if (length > MaxFileBytes || length + files.Sum(existing => new FileInfo(existing).Length) > MaxTotalBytes)
                {
                    skippedLarge++;
                    continue;
                }

                try
                {
                    _ = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
                    files.Add(file);
                }
                catch (DecoderFallbackException)
                {
                    skippedBinary++;
                }

                if (files.Count >= MaxFiles) break;
            }
        }

        if (files.Count == 0)
            throw new InvalidDataException("No supported text files were found in the selected folder.");

        var output = new StringBuilder();
        var ceilingTokens = CeilingTokens(windowTokens);
        // Each file gets its own slice of the ceiling rather than the whole of it: cut in the aggregate, one
        // long file in the middle would push the eleven around it out of the request, and the person who
        // attached the folder would get back an answer about a file they were not asking about.
        var perFileTokens = Math.Max(ToolResultCap.MinimumTokens, ceilingTokens / MaxFiles);
        // Every line here ends with \n rather than the host's newline: this block is pasted into the request, and
        // the files it quotes were read into it verbatim.
        output.Append($"Attached local folder: {displayName}").Append('\n');
        output.Append($"Included up to {MaxFiles} text files; each is limited to {MaxFileBytes / 1024} KiB and the folder to {MaxTotalBytes / 1024} KiB, "
                      + $"and the whole attachment to about {ceilingTokens} tokens of the model's window.").Append('\n');
        foreach (var file in files)
        {
            var content = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
            output.Append('\n');
            output.Append($"--- {Path.GetRelativePath(root, file)} ---").Append('\n');
            output.Append(ToolResultCap.ApplyTokenBudget(content, perFileTokens)).Append('\n');
        }

        if (skippedLarge > 0) output.Append($"\nSkipped {skippedLarge} oversized files.");
        if (skippedBinary > 0) output.Append($"\nSkipped {skippedBinary} non-UTF-8 files.");
        if (scanLimitReached) output.Append($"\nStopped scanning after {MaxScannedEntries} entries.");
        return ToolResultCap.ApplyTokenBudget(output.ToString(), ceilingTokens);
    }
}
