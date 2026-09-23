using LibGit2Sharp;

namespace TotalGit.Core.Git;

/// <summary>
/// A read-only view of one worktree of a repository, kept open so history can be paged and
/// details loaded on demand. All members are thread-safe (LibGit2Sharp itself is not).
/// Write operations go through <see cref="GitActions"/> instead.
/// </summary>
public sealed class RepositorySession : IDisposable
{
    public const int PageSize = 2000;

    private readonly Repository _repo;
    private readonly Lock _lock = new();
    private IEnumerator<Commit>? _history;
    private bool _hasMore = true;
    private string? _patchSha;
    private Patch? _patch;

    static RepositorySession()
    {
        // libgit2 refuses repos declaring config extensions it doesn't know. These only change how
        // worktree metadata is stored, which a read-only history viewer never touches.
        GlobalSettings.SetExtensions([.. GlobalSettings.GetExtensions(), "relativeworktrees"]);
    }

    private RepositorySession(Repository repo)
    {
        _repo = repo;
        GitDirectory = Path.TrimEndingDirectorySeparator(repo.Info.Path);
        WorkingDirectory = Path.TrimEndingDirectorySeparator(repo.Info.WorkingDirectory ?? repo.Info.Path);

        var commonDirFile = Path.Combine(GitDirectory, "commondir");
        IsLinkedWorktree = File.Exists(commonDirFile);
        CommonGitDirectory = IsLinkedWorktree
            ? Path.GetFullPath(Path.Combine(GitDirectory, File.ReadAllText(commonDirFile).Trim()))
            : GitDirectory;
        CommonGitDirectory = Path.TrimEndingDirectorySeparator(CommonGitDirectory);

        MainWorkingDirectory = Path.GetFileName(CommonGitDirectory) == ".git"
            ? Path.GetDirectoryName(CommonGitDirectory)!
            : CommonGitDirectory;
    }

    public string WorkingDirectory { get; }
    public string MainWorkingDirectory { get; }
    public string GitDirectory { get; }
    public string CommonGitDirectory { get; }
    public bool IsLinkedWorktree { get; }

    public bool HasMoreHistory
    {
        get { lock (_lock) return _hasMore; }
    }

    /// <summary>Opens the repository (or worktree) containing <paramref name="path"/>.</summary>
    public static RepositorySession Open(string path)
    {
        var gitDir = Repository.Discover(path)
            ?? throw new RepositoryOpenException($"'{path}' is not inside a git repository.");
        try
        {
            return new RepositorySession(new Repository(gitDir));
        }
        catch (LibGit2SharpException ex)
        {
            throw new RepositoryOpenException($"Unable to open repository: {ex.Message}", ex);
        }
    }

    public RepositoryState LoadState()
    {
        lock (_lock)
        {
            var info = _repo.Info;
            var current = info.IsHeadDetached || info.IsHeadUnborn ? null : _repo.Head.FriendlyName;
            return new RepositoryState(
                WorkingDirectory,
                MainWorkingDirectory,
                Path.GetFileName(MainWorkingDirectory),
                IsLinkedWorktree,
                current,
                _repo.Head.Tip?.Sha,
                _repo.Network.Remotes["origin"]?.Url,
                ReadRefs(),
                ReadStashes());
        }
    }

    /// <summary>Restarts history paging, e.g. after refs changed.</summary>
    public void ResetHistory()
    {
        lock (_lock)
        {
            _history?.Dispose();
            _history = null;
            _hasMore = true;
        }
    }

    /// <summary>Returns the next <paramref name="count"/> commits in topological order.</summary>
    public IReadOnlyList<CommitInfo> ReadHistory(int count = PageSize)
    {
        lock (_lock)
        {
            var result = new List<CommitInfo>();
            if (!_hasMore) return result;

            _history ??= StartHistory();
            while (result.Count < count)
            {
                if (!_history.MoveNext())
                {
                    _hasMore = false;
                    break;
                }
                result.Add(ToInfo(_history.Current));
            }
            return result;
        }
    }

    public WorkingTreeStatus GetStatus()
    {
        lock (_lock)
        {
            if (_repo.Info.IsBare) return WorkingTreeStatus.Clean;

            var status = _repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
                IncludeIgnored = false,
                DetectRenamesInIndex = true,
            });

            var unstaged = new List<FileChange>();
            var staged = new List<FileChange>();
            foreach (var e in status)
            {
                var s = e.State;
                if (s.HasFlag(FileStatus.Conflicted))
                {
                    unstaged.Add(new FileChange(e.FilePath, null, ChangeKind.Conflicted));
                    continue;
                }

                ChangeKind? indexKind =
                    s.HasFlag(FileStatus.NewInIndex) ? ChangeKind.Added :
                    s.HasFlag(FileStatus.RenamedInIndex) ? ChangeKind.Renamed :
                    s.HasFlag(FileStatus.DeletedFromIndex) ? ChangeKind.Deleted :
                    s.HasFlag(FileStatus.TypeChangeInIndex) ? ChangeKind.TypeChanged :
                    s.HasFlag(FileStatus.ModifiedInIndex) ? ChangeKind.Modified : null;
                if (indexKind is { } ik)
                    staged.Add(new FileChange(e.FilePath, e.HeadToIndexRenameDetails?.OldFilePath, ik));

                ChangeKind? workKind =
                    s.HasFlag(FileStatus.NewInWorkdir) ? ChangeKind.Untracked :
                    s.HasFlag(FileStatus.RenamedInWorkdir) ? ChangeKind.Renamed :
                    s.HasFlag(FileStatus.DeletedFromWorkdir) ? ChangeKind.Deleted :
                    s.HasFlag(FileStatus.TypeChangeInWorkdir) ? ChangeKind.TypeChanged :
                    s.HasFlag(FileStatus.ModifiedInWorkdir) ? ChangeKind.Modified : null;
                if (workKind is { } wk)
                    unstaged.Add(new FileChange(e.FilePath, e.IndexToWorkDirRenameDetails?.OldFilePath, wk));
            }

            return new WorkingTreeStatus(
                unstaged.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                staged.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public CommitDetails GetCommitDetails(string sha)
    {
        lock (_lock)
        {
            var c = _repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit {sha} not found.", nameof(sha));
            var patch = CommitPatch(c);
            var files = patch
                .Select(p => new FileChange(
                    p.Path,
                    p.OldPath != p.Path ? p.OldPath : null,
                    Map(p.Status),
                    p.LinesAdded,
                    p.LinesDeleted,
                    p.IsBinaryComparison))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new CommitDetails(ToInfo(c), c.Committer.Name, c.Committer.Email, c.Committer.When, c.Message, files);
        }
    }

    public FileDiff GetCommitFileDiff(string sha, string path)
    {
        lock (_lock)
        {
            var c = _repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit {sha} not found.", nameof(sha));
            var entry = CommitPatch(c)[path];
            return entry is null ? new FileDiff(path, false, [], false) : ToFileDiff(path, entry.IsBinaryComparison, entry.Patch);
        }
    }

    /// <summary>Diff of a working-tree file: index vs HEAD when <paramref name="staged"/>, else working tree vs index.</summary>
    public FileDiff GetWorkingFileDiff(string path, bool staged)
    {
        lock (_lock)
        {
            var patch = staged
                ? _repo.Diff.Compare<Patch>(_repo.Head.Tip?.Tree, DiffTargets.Index, [path])
                : _repo.Diff.Compare<Patch>([path], includeUntracked: true);
            var entry = patch[path];
            if (entry is not null && !string.IsNullOrEmpty(entry.Patch) && entry.Patch.Contains("@@"))
                return ToFileDiff(path, entry.IsBinaryComparison, entry.Patch);

            // Untracked files: libgit2 may omit the content, so show the file as all-added.
            var full = Path.Combine(WorkingDirectory, path);
            if (!staged && File.Exists(full)) return UntrackedFileDiff(path, full);
            return new FileDiff(path, entry?.IsBinaryComparison ?? false, [], false);
        }
    }

    private static FileDiff UntrackedFileDiff(string path, string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (info.Length > 2_000_000) return new FileDiff(path, false, [], true);
        var bytes = File.ReadAllBytes(fullPath);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) return new FileDiff(path, true, [], false);

        var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        var content = text.Split('\n');
        if (content.Length > 0 && content[^1].Length == 0) content = content[..^1];
        var lines = new List<DiffLine> { new(DiffLineKind.Hunk, null, null, $"@@ -0,0 +1,{content.Length} @@") };
        var truncated = false;
        for (var i = 0; i < content.Length; i++)
        {
            if (lines.Count >= UnifiedDiff.MaxLines) { truncated = true; break; }
            lines.Add(new DiffLine(DiffLineKind.Added, null, i + 1, content[i]));
        }
        return new FileDiff(path, false, lines, truncated);
    }

    private Patch CommitPatch(Commit c)
    {
        if (_patchSha == c.Sha && _patch is not null) return _patch;
        _patch = _repo.Diff.Compare<Patch>(c.Parents.FirstOrDefault()?.Tree, c.Tree,
            new CompareOptions { Similarity = SimilarityOptions.Renames });
        _patchSha = c.Sha;
        return _patch;
    }

    private static FileDiff ToFileDiff(string path, bool binary, string patch)
    {
        if (binary) return new FileDiff(path, true, [], false);
        var (lines, truncated) = UnifiedDiff.Parse(patch);
        return new FileDiff(path, false, lines, truncated);
    }

    private static ChangeKind Map(LibGit2Sharp.ChangeKind kind) => kind switch
    {
        LibGit2Sharp.ChangeKind.Added or LibGit2Sharp.ChangeKind.Copied => ChangeKind.Added,
        LibGit2Sharp.ChangeKind.Deleted => ChangeKind.Deleted,
        LibGit2Sharp.ChangeKind.Renamed => ChangeKind.Renamed,
        LibGit2Sharp.ChangeKind.TypeChanged => ChangeKind.TypeChanged,
        LibGit2Sharp.ChangeKind.Conflicted => ChangeKind.Conflicted,
        LibGit2Sharp.ChangeKind.Untracked => ChangeKind.Untracked,
        _ => ChangeKind.Modified,
    };

    private IEnumerator<Commit> StartHistory()
    {
        var tips = _repo.Refs
            .Where(r => !r.CanonicalName.StartsWith("refs/stash", StringComparison.Ordinal))
            .Select(r => r.ResolveToDirectReference()?.Target)
            .OfType<Commit>()
            .Cast<object>()
            .ToList();
        if (_repo.Head.Tip is { } head) tips.Add(head);
        if (tips.Count == 0) return Enumerable.Empty<Commit>().GetEnumerator();

        return _repo.Commits.QueryBy(new CommitFilter
        {
            IncludeReachableFrom = tips,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
        }).GetEnumerator();
    }

    private static CommitInfo ToInfo(Commit c) => new(
        c.Sha,
        c.Parents.Select(p => p.Sha).ToArray(),
        c.Author.Name,
        c.Author.Email,
        c.Author.When,
        c.MessageShort);

    private List<StashInfo> ReadStashes() => _repo.Stashes
        .Select((s, i) =>
        {
            var (message, branch) = StashInfo.ParseMessage(s.Message);
            return new StashInfo(i, message, s.WorkTree.Sha, s.WorkTree.Committer.When, branch);
        })
        .ToList();

    private List<RefInfo> ReadRefs()
    {
        var result = new List<RefInfo>();

        foreach (var branch in _repo.Branches)
        {
            if (branch.Tip is null) continue;
            // Skip symbolic refs like origin/HEAD.
            if (branch.IsRemote && branch.CanonicalName.EndsWith("/HEAD", StringComparison.Ordinal)) continue;

            if (branch.IsRemote)
            {
                result.Add(new RefInfo(branch.FriendlyName, RefKind.RemoteBranch, branch.Tip.Sha, false, RemoteName: branch.RemoteName));
                continue;
            }

            var tracking = branch.IsTracking ? branch.TrackingDetails : null;
            var upstreamGone = branch.UpstreamBranchCanonicalName is not null && branch.TrackedBranch?.Tip is null;
            result.Add(new RefInfo(
                branch.FriendlyName,
                RefKind.LocalBranch,
                branch.Tip.Sha,
                branch.IsCurrentRepositoryHead,
                Upstream: branch.IsTracking ? branch.TrackedBranch?.FriendlyName
                    : upstreamGone ? $"{branch.RemoteName}/{branch.UpstreamBranchCanonicalName!.Replace("refs/heads/", "")}" : null,
                Ahead: tracking?.AheadBy ?? 0,
                Behind: tracking?.BehindBy ?? 0,
                UpstreamGone: upstreamGone));
        }

        foreach (var tag in _repo.Tags)
        {
            var target = tag.PeeledTarget as Commit ?? tag.Target as Commit;
            if (target is null) continue;
            result.Add(new RefInfo(tag.FriendlyName, RefKind.Tag, target.Sha, false));
        }

        if (_repo.Info.IsHeadDetached && _repo.Head.Tip is { } head)
            result.Add(new RefInfo("HEAD", RefKind.DetachedHead, head.Sha, true));

        return result;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _history?.Dispose();
            _repo.Dispose();
        }
    }
}
