namespace AxmolHub.Core;

/// <summary>
/// Names retained solely for saved-conversation compatibility and old grant migration.
/// These are not tool declarations and must not be registered for new calls.
/// In particular, the old file_write means an exact replacement, not the new fs_write overwrite.
/// </summary>
public static class LegacyFileToolNames
{
    public const string ReadFile = "read_file";
    public const string FileWrite = "file_write";
    public const string SearchText = "search_text";
    public const string FindFiles = "find_files";
    public const string ListDirectory = "list_directory";
    public const string RequestDirectory = "request_directory";
    public const string RequestFile = "request_file";
    public const string GetProjects = "get_projects";
    public const string GetEngines = "get_engines";
    public const string GetToolchainStatus = "get_toolchain_status";

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
        { ReadFile, FileWrite, SearchText, FindFiles, ListDirectory, RequestDirectory, RequestFile,
            GetProjects, GetEngines, GetToolchainStatus };

    public static bool Contains(string? name) => name is not null && Names.Contains(name);
}
