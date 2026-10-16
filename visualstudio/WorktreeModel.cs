using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalGit.VisualStudio.Git;

namespace TotalGit.VisualStudio;

/// <summary>A worktree with its status, as the Worktrees window shows it.</summary>
internal sealed class WorktreeEntry
{
    public WorktreeEntry(Worktree worktree, bool exists, bool current)
    {
        Worktree = worktree;
        Exists = exists;
        Current = current;
    }

    public Worktree Worktree { get; }
    public string Folder => Worktree.Path;
    public string Name => Porcelain.Name(Worktree.Path);
    public WorktreeStatus? Status { get; set; }
    public bool Exists { get; }
    /// <summary>This Visual Studio has its solution (or folder) open.</summary>
    public bool Current { get; }

    /// <summary>The branch, or the commit it's detached at.</summary>
    public string BranchText => Worktree.Branch ?? (Worktree.Head is { Length: >= 7 } head ? "detached at " + head.Substring(0, 7) : "");
}

/// <summary>The worktrees of the repository the open solution is in, kept fresh.</summary>
internal sealed class WorktreeModel : IDisposable
{
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private FileSystemWatcher? _watcher;
    private string? _watchedDir;
    private Timer? _debounce;
    private int _again;

    /// <summary>The folder of the open solution (or folder); null with nothing open.</summary>
    public string? OpenFolder { get; private set; }

    /// <summary>The repository's name (its main worktree's folder), or null outside a repository.</summary>
    public string? RepoName { get; private set; }
    public string? MainRoot { get; private set; }
    public IReadOnlyList<WorktreeEntry> Entries { get; private set; } = Array.Empty<WorktreeEntry>();
    public WorktreeEntry? CurrentEntry => Entries.FirstOrDefault(e => e.Current);

    /// <summary>Why there's nothing to show (no solution, not in git), or null.</summary>
    public string? Message { get; private set; } = "Open a solution or folder in a git repository.";

    /// <summary>Raised (on a background thread) after each refresh.</summary>
    public event EventHandler? Changed;

    public void SetOpenFolder(string? folder)
    {
        OpenFolder = string.IsNullOrEmpty(folder) ? null : folder;
        RefreshSoon(0);
    }

    /// <summary>Refreshes after a short pause, so a burst of events makes one refresh.</summary>
    public void RefreshSoon(int delayMs = 400)
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => _ = RefreshAsync(), null, delayMs, Timeout.Infinite);
    }

    public async Task RefreshAsync()
    {
        if (!await _refreshing.WaitAsync(0).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _again, 1);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _again, 0);
                await ReadAsync().ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);
            } while (Interlocked.Exchange(ref _again, 0) == 1);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Whether <paramref name="file"/> is a conflicted file of the open worktree.</summary>
    public bool IsConflicted(string file)
    {
        var current = CurrentEntry;
        if (current?.Status is not { Conflicts.Count: > 0 } status) return false;
        return status.Conflicts.Any(c => Porcelain.SamePath(Path.Combine(current.Folder, c.Replace('/', '\\')), file));
    }

    private async Task ReadAsync()
    {
        var folder = OpenFolder;
        if (folder is null || !Directory.Exists(folder))
        {
            Clear("Open a solution or folder in a git repository.");
            return;
        }

        string top, common;
        try
        {
            var lines = (await GitRunner.RunAsync(folder, "rev-parse --path-format=absolute --show-toplevel --git-common-dir").ConfigureAwait(false))
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                Clear("The open solution isn't in a git repository.");
                return;
            }
            top = Path.GetFullPath(lines[0]);
            common = Path.GetFullPath(lines[1]);
        }
        catch (GitException)
        {
            Clear("The open solution isn't in a git repository.");
            return;
        }

        List<Worktree> list;
        try
        {
            list = Porcelain.ParseWorktreeList(await GitRunner.RunAsync(top, "worktree list --porcelain").ConfigureAwait(false));
        }
        catch (GitException ex)
        {
            Clear(ex.Message);
            return;
        }

        var entries = list.Where(w => !w.Bare)
            .Select(w => new WorktreeEntry(w, !w.Prunable && Directory.Exists(w.Path), Porcelain.SamePath(w.Path, top)))
            .ToList();
        // Four at a time: a repository can have many worktrees.
        using (var gate = new SemaphoreSlim(4))
        {
            await Task.WhenAll(entries.Where(e => e.Exists).Select(async e =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // --no-optional-locks: reading status mustn't rewrite the index (and set off the watcher).
                    e.Status = Porcelain.ParseStatus(await GitRunner.RunAsync(e.Folder, "--no-optional-locks status --porcelain=v2 --branch", 10000).ConfigureAwait(false));
                }
                catch (GitException)
                {
                    e.Status = null;
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }

        MainRoot = list.FirstOrDefault()?.Path ?? top;
        RepoName = Porcelain.Name(MainRoot);
        Entries = entries;
        Message = null;
        Watch(common);
    }

    private void Clear(string message)
    {
        Entries = Array.Empty<WorktreeEntry>();
        RepoName = null;
        MainRoot = null;
        Message = message;
        Watch(null);
    }

    /// <summary>Watches the repository's git folder for branch switches and worktrees coming and going.</summary>
    private void Watch(string? commonDir)
    {
        if (string.Equals(commonDir, _watchedDir, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose();
        _watcher = null;
        _watchedDir = commonDir;
        if (commonDir is null || !Directory.Exists(commonDir)) return;
        try
        {
            _watcher = new FileSystemWatcher(commonDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler onChange = (_, e) =>
            {
                if (Matters(commonDir, e.FullPath)) RefreshSoon();
            };
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (_, e) => onChange(_, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null;
        }
    }

    /// <summary>HEAD, branches, merges and the worktree list matter; objects, logs and indexes don't.</summary>
    private static bool Matters(string commonDir, string path)
    {
        var rel = path.Length > commonDir.Length ? path.Substring(commonDir.Length).TrimStart('\\', '/').Replace('/', '\\') : "";
        if (rel.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        // MERGE_HEAD: a merge (perhaps stopped at conflicts) started or finished outside Visual Studio.
        if (rel is "HEAD" or "packed-refs" or "MERGE_HEAD") return true;
        if (rel.StartsWith(@"refs\heads\", StringComparison.OrdinalIgnoreCase)) return true;
        if (!rel.StartsWith(@"worktrees\", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = rel.Split('\\');
        return parts.Length == 2 || (parts.Length == 3 && parts[2] is "HEAD" or "MERGE_HEAD");
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        _watcher?.Dispose();
        _refreshing.Dispose();
    }
}
