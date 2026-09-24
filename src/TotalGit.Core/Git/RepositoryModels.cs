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
    IReadOnlyList<RefInfo> Refs,
    IReadOnlyList<StashInfo> Stashes,
    RepoOperation Operation = RepoOperation.None,
    string? OperationProgress = null)
{
    public string WorktreeName => Path.GetFileName(WorkingDirectory.TrimEnd('\\', '/'));
}

/// <summary>A multi-step operation the repository is in the middle of (usually stopped on conflicts).</summary>
public enum RepoOperation
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
}

/// <summary>How a merge or rebase ended: done, or stopped for the user (conflicts, or an edit step).</summary>
public enum OperationOutcome
{
    Completed,
    Stopped,
}

/// <param name="Index">Position in the stash list (0 = stash@{0}, the newest).</param>
/// <param name="Branch">Branch the stash was made on, when git recorded it.</param>
public sealed record StashInfo(int Index, string Message, string Sha, DateTimeOffset When, string? Branch)
{
    public string RefName => $"stash@{{{Index}}}";

    /// <summary>Splits git's reflog message ("On main: msg" / "WIP on main: abc123 subject").</summary>
    public static (string Message, string? Branch) ParseMessage(string raw)
    {
        foreach (var prefix in (string[])["WIP on ", "On "])
        {
            if (!raw.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var colon = raw.IndexOf(": ", prefix.Length, StringComparison.Ordinal);
            if (colon < 0) break;
            var branch = raw[prefix.Length..colon];
            var message = raw[(colon + 2)..];
            // "WIP on main: abc1234 last commit subject" -> keep the subject only.
            if (prefix == "WIP on ")
            {
                var space = message.IndexOf(' ');
                message = "WIP: " + (space > 0 ? message[(space + 1)..] : message);
            }
            return (message, branch);
        }
        return (raw, null);
    }
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

public enum IgnoreTarget
{
    /// <summary>The worktree's root .gitignore (shared with everyone once committed).</summary>
    GitIgnore,

    /// <summary>The repository's info/exclude (this clone only).</summary>
    InfoExclude,
}

public sealed record IgnorePreview(IReadOnlyList<string> Untracked, int TrackedCount);

/// <summary>What to do with uncommitted changes when checking out another branch or commit.</summary>
public enum LocalChanges
{
    /// <summary>Bring them along (git refuses if a changed file differs on the other branch).</summary>
    Keep,

    /// <summary>Bring them along, merging them into files that differ (may leave conflicts).</summary>
    Merge,

    /// <summary>Stash them first, so the other branch starts clean.</summary>
    Stash,

    /// <summary>Throw away changes to tracked files (untracked files are kept).</summary>
    Discard,
}

/// <summary>The kinds of <c>git reset</c>, named after its flags.</summary>
public enum ResetMode
{
    /// <summary>Move the branch only; the working tree and index keep their contents (changes show as staged).</summary>
    Soft,

    /// <summary>Move the branch and reset the index; the working tree keeps its contents (changes show as unstaged).</summary>
    Mixed,

    /// <summary>Move the branch and update the working tree; refuses if that would touch local changes.</summary>
    Keep,

    /// <summary>Move the branch and update the working tree, keeping local changes; refuses on conflicts.</summary>
    Merge,

    /// <summary>Move the branch and make the working tree and index match it, discarding all local changes.</summary>
    Hard,
}
