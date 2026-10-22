using static AxmolHub.ChatProviderCoordinator;
using static AxmolHub.ChatExecutionCoordinator;
using AxmolHub.Core;
using static AxmolHub.ChatWorkspace;

namespace AxmolHub;
internal sealed partial class ChatSessionCoordinator
{
    internal void ForgetAllWorkspacesForCheck() => _workspaces.Clear();
    internal Conversation? StoredCopyForCheck(string conversationId) => _sessions.LoadFromDisk(conversationId);
    internal string? StoredSessionTextForCheck(string conversationId) => _sessions.Store.ReadStoredText(conversationId);
    internal int RepositoryReadsForCheck { get; private set; }

    internal void SeedRepositoryForCheck(string workspaceRoot, GitRepositoryState state)
    {
        _repository = new RepositorySnapshot(workspaceRoot, state);
        RepositoryChanged?.Invoke();
    }

    internal void ClearRepositoryForCheck()
    {
        _repository = null;
        RepositoryChanged?.Invoke();
    }
}
