namespace TotalGit.Core.Git;

/// <summary>Refs and identity of the repository as seen from one worktree.</summary>
/// <param name="WorkingDirectory">Root of the worktree this session is viewing.</param>
/// <param name="MainWorkingDirectory">Root of the main worktree (same as WorkingDirectory unless linked).</param>
/// <param name="RepositoryName">Name of the main worktree folder.</param>
/// <param name="IsLinkedWorktree">True when viewing a worktree created with <c>git worktree add</c>.</param>
public sealed record RepositoryState(
    string WorkingDirectory,
    string MainWorkingDirectory,
    string RepositoryName,
    bool IsLinkedWorktree,
    string? CurrentBranch,
    string? HeadSha,
    string? OriginUrl,
    IReadOnlyList<RefInfo> Refs)
{
    public string WorktreeName => Path.GetFileName(WorkingDirectory.TrimEnd('\\', '/'));
}

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    TypeChanged,
    Untracked,
    Conflicted,
}

public sealed record FileChange(string Path, string? OldPath, ChangeKind Kind, int Additions = 0, int Deletions = 0, bool IsBinary = false)
{
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public string? Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/');
}

public sealed record WorkingTreeStatus(IReadOnlyList<FileChange> Unstaged, IReadOnlyList<FileChange> Staged)
{
    public static WorkingTreeStatus Clean { get; } = new([], []);
    public bool IsDirty => Unstaged.Count > 0 || Staged.Count > 0;
    public int TotalCount => Unstaged.Select(f => f.Path).Union(Staged.Select(f => f.Path)).Count();
}

public sealed record CommitDetails(
    CommitInfo Commit,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommitterDate,
    string FullMessage,
    IReadOnlyList<FileChange> Files);

public sealed record FileDiff(string Path, bool IsBinary, IReadOnlyList<DiffLine> Lines, bool Truncated);
