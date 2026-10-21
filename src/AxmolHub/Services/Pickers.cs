using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>The result of a file / folder picker.</summary>
public enum PickOutcome
{
    /// <summary>The user cancelled, or the picker returned nothing.</summary>
    Cancelled,

    /// <summary>A local path was picked.</summary>
    Picked,

    /// <summary>
    /// An item was picked, but it has no local filesystem path (cloud drive, virtual location,
    /// content provider). Hub's semantics are "local directory", so such a location can't be used as
    /// a path and must be rejected explicitly rather than treated as a cancel.
    /// </summary>
    NotLocal,
}

public readonly record struct PickResult(PickOutcome Outcome, string? Path)
{
    public static PickResult Cancelled { get; } = new(PickOutcome.Cancelled, null);

    public static PickResult NotLocal { get; } = new(PickOutcome.NotLocal, null);

    public static PickResult Picked(string path) => new(PickOutcome.Picked, path);
}

/// <summary>
/// WPF's <c>OpenFileDialog</c> / <c>OpenFolderDialog</c> are unified into
/// <see cref="IStorageProvider"/> in Avalonia, and **only async signatures exist**. The return
/// semantics changed too: WPF gives <c>string?</c> directly, Avalonia gives
/// <c>IStorageFolder</c>/<c>IStorageFile</c>; the local path must be recovered via
/// <c>TryGetLocalPath()</c>, which is null when it can't be recovered (cloud location).
/// </summary>
public static class Pickers
{
    /// <summary>
    /// The decision logic itself. Extracted into a pure function that doesn't touch Avalonia, for
    /// the sole reason that it **can be asserted**: actually popping a picker can't be automated,
    /// and distinguishing "cancelled / picked / picked a non-local location" is exactly the kind of
    /// thing that's easy to get wrong and hard to spot in a few manual clicks.
    /// </summary>
    public static PickResult Resolve(bool itemPresent, string? localPath)
    {
        if (!itemPresent)
        {
            return PickResult.Cancelled;
        }

        return string.IsNullOrEmpty(localPath) ? PickResult.NotLocal : PickResult.Picked(localPath);
    }

    /// <summary>Translates the picker's returned item into a result.</summary>
    public static PickResult Translate(IStorageItem? item)
        => Resolve(item is not null, item?.TryGetLocalPath());

    public static async Task<PickResult> PickFolderAsync(TopLevel owner, string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return Translate(folders.Count > 0 ? folders[0] : null);
    }

    public static async Task<PickResult> PickFileAsync(
        TopLevel owner,
        string title,
        IReadOnlyList<FilePickerFileType>? types = null)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types,
        });

        return Translate(files.Count > 0 ? files[0] : null);
    }

    /// <summary>
    /// An editor: an application bundle on macOS, an executable elsewhere.
    ///
    /// The two hosts disagree about what one *is*, and on macOS the disagreement is not cosmetic: a bundle is a
    /// **directory**, and Avalonia's file picker filters directories out of its result even though the panel lets
    /// you click one — it comes back empty (AvaloniaUI/Avalonia#18303, still true of the pinned version), which
    /// reads to the user as "choose an application and nothing happens". The folder picker is the one that returns
    /// the bundle, so it is what asks on macOS; it starts in <c>/Applications</c> so the list is applications
    /// rather than files. Elsewhere the file picker is right and <c>*.exe</c> is the type worth filtering on.
    ///
    /// The returned path is folded up to the enclosing bundle; whether it can be launched is
    /// <see cref="CodeEditors"/>' question, not this one's.
    /// </summary>
    public static async Task<PickResult> PickEditorAsync(TopLevel owner, string title)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return await PickFileAsync(owner, title,
                OperatingSystem.IsWindows() ? [new FilePickerFileType("Executable") { Patterns = ["*.exe"] }] : null);
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync("/Applications"),
        });
        var picked = Translate(folders.Count > 0 ? folders[0] : null);
        return picked.Outcome == PickOutcome.Picked
            ? PickResult.Picked(CodeEditors.ApplicationBundle(picked.Path!) ?? picked.Path!)
            : picked;
    }

    public static async Task<PickResult> SaveFileAsync(
        TopLevel owner,
        string title,
        string suggestedName,
        IReadOnlyList<FilePickerFileType>? types = null)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = types,
        });

        return Translate(file);
    }
}
