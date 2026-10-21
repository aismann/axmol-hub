using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// The list of directories the user has designated as workspaces, persisted at <c>data-root/ai/workspaces.json</c>.
///
/// <para>This exists because a workspace is a place somebody named, and a place does not stop being named when the
/// last chat that worked in it is deleted or put away. Before this file the sidebar's groups were a pure reading of
/// the sessions that name a directory (<see cref="SessionGroupKey"/>), so a workspace with no sessions left had no
/// header to show — the group vanished, and with it the ＋ and the 编辑路径 entry point that repair it. The registry
/// is what lets an empty workspace stay on screen and say 「暂无会话」 instead of nothing.</para>
///
/// <para><b>Why a file in the data root rather than a <see cref="HubPreferences"/> list.</b> The registry is state
/// about this data root, not a preference: it has to die with the root and be rebuilt with it, while
/// <c>hub-settings.json</c> is shared across roots and keeps exactly one writer. It also has to be readable by a
/// shell that never loaded the settings file — every automation entry point constructs a bare
/// <c>new HubPreferences()</c> (see <c>App.Start</c>'s verification branch), so a preference-backed list would read
/// empty in the very suite that proves it works. A second writer would also mean the fold-state save could drop
/// registry entries it had never seen, which is the failure
/// <c>ChatWorkspace.PersistPreferences</c> is documented to prevent.</para>
///
/// <para><b>Why not under <c>ai/sessions/</c>.</b> <see cref="ConversationStore"/> rebuilds its index by enumerating
/// every <c>*.json</c> in that directory and binding each one to a conversation; a registry file parked there would
/// be read as a session.</para>
/// </summary>
public sealed class WorkspaceStore(string root)
{
    /// <summary>The file is read-modify-written as a whole, so overlapping saves would drop one of them.</summary>
    private readonly object _gate = new();

    /// <summary>Loaded once and kept: this instance owns the file for as long as the data root is open, and the
    /// sidebar reads the list on every repaint.</summary>
    private List<string>? _cache;

    private string RegistryPath => Path.Combine(root, "ai", "workspaces.json");

    /// <summary>Every designated workspace, in the order they were added (oldest first). The stored spelling is the
    /// one the user gave; <see cref="SessionGroupKey.Workspace"/> turns it into the group key. A property rather than
    /// a call because the sidebar reads it on every repaint and the list is held in memory between writes.</summary>
    public IReadOnlyList<string> Roots
    {
        get
        {
            lock (_gate) return _cache ??= Read();
        }
    }

    /// <summary>Whether this directory is on the list, judged by canonical key so two spellings are one answer.</summary>
    public bool Contains(string? root)
    {
        var key = SessionGroupKey.Workspace(root);
        return key is not null && Roots.Any(known => SessionGroupKey.Workspace(known) == key);
    }

    /// <summary>Adds a workspace. Returns whether the list changed, so a caller can tell a designation from a
    /// re-click on a directory that was already one. Blank and unresolvable entries are not added.</summary>
    public bool Add(string? path)
    {
        var recorded = Record(path);
        if (recorded is null) return false;
        lock (_gate)
        {
            var roots = _cache ??= Read();
            var key = SessionGroupKey.Workspace(recorded);
            if (key is null || roots.Any(known => SessionGroupKey.Workspace(known) == key)) return false;
            _cache = [.. roots, recorded];
            Write(_cache);
            return true;
        }
    }

    /// <summary>Takes a workspace off the list. Nothing else is touched: the sessions that worked there keep their
    /// records, and pointing any of them at the directory again — or pressing ＋ in it — makes the group come back.</summary>
    public bool Remove(string? path)
    {
        var key = SessionGroupKey.Workspace(path);
        if (key is null) return false;
        lock (_gate)
        {
            var roots = _cache ??= Read();
            var kept = roots.Where(known => SessionGroupKey.Workspace(known) != key).ToList();
            if (kept.Count == roots.Count) return false;
            _cache = kept;
            Write(_cache);
            return true;
        }
    }

    /// <summary>
    /// Moves one entry to a different directory, in one write, keeping the slot it already held: re-pathing a
    /// workspace is the same group reading a new place, not a new group, and the order the user built is the order
    /// the list shows. When the old directory was never registered (a group that only its sessions were holding up)
    /// the destination is added at the tail, which is where a designation the user just made belongs. Returns
    /// whether the list changed, because a group whose path moved has to repaint even with no sessions in it.
    /// </summary>
    public bool Replace(string? from, string? to)
    {
        var key = SessionGroupKey.Workspace(from);
        var recorded = Record(to);
        if (key is null || recorded is null) return false;
        lock (_gate)
        {
            var roots = _cache ??= Read();
            var slot = roots.FindIndex(known => SessionGroupKey.Workspace(known) == key);
            if (slot < 0)
            {
                if (roots.Any(known => SessionGroupKey.Workspace(known) == SessionGroupKey.Workspace(recorded))) return false;
                _cache = [.. roots, recorded];
                Write(_cache);
                return true;
            }

            if (string.Equals(roots[slot], recorded, StringComparison.Ordinal)) return false;
            roots[slot] = recorded;
            _cache = Normalize(roots);
            Write(_cache);
            return true;
        }
    }

    /// <summary>
    /// Adopts the directories a Hub that predates this file was already working in, and only when there is no file
    /// to read. The absence of the file is the whole trigger: a backfill that ran on every start would resurrect a
    /// workspace somebody removed, because an empty draft that no longer shows in the list still names that
    /// directory. Writes the file even when the seed is empty, which is what marks the migration done.
    /// </summary>
    public bool SeedFrom(IEnumerable<string?> knownRoots)
    {
        lock (_gate)
        {
            if (_cache is not null) return false;
            if (File.Exists(RegistryPath))
            {
                _cache = Read();
                return false;
            }

            _cache = Normalize([.. knownRoots]);
            Write(_cache);
            return true;
        }
    }

    /// <summary>Empties the list. The self-check's cleanup path uses it: a suite that registered six fixture
    /// directories has to be able to hand the next section the empty list it asserts on.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if ((_cache ??= Read()).Count == 0) return;
            _cache = [];
            Write(_cache);
        }
    }

    /// <summary>
    /// The path as the user gave it, made absolute but never folded: the label on the group and the tooltip under it
    /// read the spelling somebody typed, and the canonical form is derived at compare time instead. An unresolvable
    /// path is dropped here rather than kept, because unlike a session's own directory — which still has to show the
    /// place it lost — a registry entry exists only to name a group, and a group nobody can name is noise.
    /// </summary>
    private static string? Record(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>A damaged registry reads as an empty one: the sessions still describe their own directories, so
    /// the list rebuilds itself from what is on screen instead of refusing to start.</summary>
    private List<string> Read() => Normalize(StateStore.ReadJson<WorkspaceRegistry>(RegistryPath)?.Roots ?? []);

    private void Write(List<string> roots) => StateStore.WriteJson(RegistryPath, new WorkspaceRegistry { Roots = roots });

    /// <summary>
    /// The rules a stored list has to satisfy before anything reads it: no blanks, no duplicates of one directory
    /// under two spellings (the first spelling wins, because that is the one already on screen), and the order kept.
    /// Entries this build cannot make sense of are kept rather than dropped — a reader that deleted what it did not
    /// recognise would erase a workspace a newer Hub named.
    /// </summary>
    internal static List<string> Normalize(IReadOnlyList<string?> roots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var key = SessionGroupKey.Workspace(root);
            if (key is null || !seen.Add(key)) continue;
            kept.Add(root);
        }

        return kept;
    }

    /// <summary>An object rather than a bare array, the same reason the session index is one: the file then has a
    /// place to carry a shape marker if the registry ever grows a second kind of entry.</summary>
    public sealed class WorkspaceRegistry
    {
        public List<string> Roots { get; set; } = [];
    }
}
