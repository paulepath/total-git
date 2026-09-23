namespace TotalGit.App.Services;

/// <summary>
/// Watches a worktree and its git directories, raising debounced events when refs change
/// (branches, tags, HEAD, fetches) or when the working tree / index changes.
/// </summary>
public sealed class RepositoryWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);
    private static readonly string[] IgnoredWorkingFolders = [".git", ".worktrees", "bin", "obj", "node_modules", ".vs", ".idea"];

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _refsTimer;
    private readonly Timer _workingTimer;
    private readonly string _gitDir;

    public event Action? RefsChanged;
    public event Action? WorkingTreeChanged;

    public RepositoryWatcher(string workingDirectory, string gitDirectory, string commonGitDirectory)
    {
        _gitDir = Path.TrimEndingDirectorySeparator(gitDirectory);
        _refsTimer = new Timer(_ => RefsChanged?.Invoke());
        _workingTimer = new Timer(_ => WorkingTreeChanged?.Invoke());

        Watch(commonGitDirectory, OnGitDirChange);
        if (!IsUnder(_gitDir, commonGitDirectory)) Watch(_gitDir, OnGitDirChange);
        Watch(workingDirectory, e => OnWorkingChange(workingDirectory, e));
    }

    private void Watch(string path, Action<FileSystemEventArgs> handler)
    {
        if (!Directory.Exists(path)) return;
        var w = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        w.Changed += (_, e) => handler(e);
        w.Created += (_, e) => handler(e);
        w.Deleted += (_, e) => handler(e);
        w.Renamed += (_, e) => handler(e);
        w.Error += (_, _) => { Kick(_refsTimer); Kick(_workingTimer); };
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
    }

    private void OnGitDirChange(FileSystemEventArgs e)
    {
        var path = e.FullPath;
        var name = Path.GetFileName(path);
        if (name.EndsWith(".lock", StringComparison.Ordinal)) return;

        if (name == "index" && string.Equals(Path.GetDirectoryName(path), _gitDir, StringComparison.OrdinalIgnoreCase))
        {
            Kick(_workingTimer);
            return;
        }

        var sep = Path.DirectorySeparatorChar;
        if (path.Contains($"{sep}objects{sep}") || path.Contains($"{sep}logs{sep}") || name == "objects" || name == "logs") return;
        var addedOrRemovedWorktree = Path.GetFileName(Path.GetDirectoryName(path)) == "worktrees";
        if (name is "HEAD" or "packed-refs" or "FETCH_HEAD" or "ORIG_HEAD" or "MERGE_HEAD" or "locked"
            || path.Contains($"{sep}refs{sep}") || addedOrRemovedWorktree)
            Kick(_refsTimer);
    }

    private void OnWorkingChange(string root, FileSystemEventArgs e)
    {
        var relative = Path.GetRelativePath(root, e.FullPath);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (IgnoredWorkingFolders.Contains(first, StringComparer.OrdinalIgnoreCase)) return;
        if (relative.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "node_modules")) return;
        Kick(_workingTimer);
    }

    private static void Kick(Timer timer) => timer.Change(Debounce, Timeout.InfiniteTimeSpan);

    private static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _refsTimer.Dispose();
        _workingTimer.Dispose();
    }
}
