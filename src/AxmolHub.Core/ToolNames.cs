namespace AxmolHub.Core;

/// <summary>
/// The spelling of every tool name the assistant can call, once.
///
/// <para>A name used to be retyped wherever it was needed: the declaration, the risk it carries, the preview a
/// person reads, the activity line in the transcript, the spin guard, the grant check. A rename was a search that
/// could miss a table, and a typo was a tool that silently fell through to the generic path instead of failing to
/// compile. The constants live in Core because the declarations are in the app while the previews are here.</para>
/// </summary>
public static class ToolNames
{
    public const string ReadFile = "read_file";

    public const string FileWrite = "file_write";

    public const string SearchText = "search_text";

    public const string FindFiles = "find_files";

    public const string ListDirectory = "list_directory";

    public const string WorkspaceMap = "workspace_map";

    public const string RunCommand = "run_command";

    public const string WebFetch = "web_fetch";

    public const string WebSearch = "web_search";

    public const string GetProjects = "get_projects";

    public const string GetEngines = "get_engines";

    public const string GetToolchainStatus = "get_toolchain_status";

    public const string ListSessions = "list_sessions";

    public const string ReadSession = "read_session";

    public const string SendToSession = "send_to_session";

    public const string SpawnSession = "spawn_session";

    public const string MemoryRead = "memory_read";

    public const string MemoryWrite = "memory_write";

    public const string RequestDirectory = "request_directory";

    public const string RequestFile = "request_file";

    public const string CaptureScreen = "capture_screen";

    public const string ComputerUse = "computer_use";

    public const string AskUser = "ask_user";

    public const string UpdatePlanTask = "update_plan_task";
}